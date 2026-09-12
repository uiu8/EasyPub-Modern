using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow : Window
{
    private ChapterTreeDocument _document;
    private readonly TextEncodingMode _encodingMode;
    private readonly Stack<ChapterEditorSnapshot> _undo = [];
    private readonly Stack<ChapterEditorSnapshot> _redo = [];
    private readonly HashSet<ChapterTreeNode> _trackedNodes = new(ReferenceEqualityComparer.Instance);
    private ChapterEditorSnapshot _currentSnapshot = null!;
    private bool _trackingPaused;
    private Point _dragStart;
    private ChapterTreeNode? _draggedNode;
    private ChapterTreeNode? _selectedNode;
    private bool _refreshingSuggestions;
    private bool _detectUnrecognized;
    private int _nextSuggestionIndex;
    private string _numericPattern = NumericHeadingRule.DefaultPattern;
    public TocHierarchyOptions GlobalNumericDefaults { get; set; } = new();
    public IReadOnlyList<NamedNumericHeadingPreset> NumericPresets { get; set; } = [];
    public bool InheritNumericDefaults { get; set; } = true;

    public ChapterEditorWindow(ChapterTreeDocument document)
        : this(document, document.RecognitionOptions, null, TextEncodingMode.Auto)
    {
    }

    public ChapterEditorWindow(
        ChapterTreeDocument document,
        TocHierarchyOptions hierarchy,
        string? chapterPattern,
        TextEncodingMode encodingMode,
        bool detectUnrecognized = true)
    {
        InitializeComponent();
        _document = document;
        _encodingMode = encodingMode;
        _detectUnrecognized = detectUnrecognized;
        Roots = BuildTree(document.Entries);
        DataContext = this;
        SourceText.Text = System.IO.Path.GetFileName(document.SourcePath);
        SourceText.ToolTip = document.SourcePath;
        ChapterPatternText.Text = chapterPattern ?? string.Empty;
        IncludeHtmlTocPageCheck.IsChecked = hierarchy.IncludeHtmlTocPage;
        IncludeChapterTopNavigationCheck.IsChecked = hierarchy.IncludeChapterTopNavigation;
        HierarchyEnabledCheck.IsChecked = hierarchy.Enabled;
        NumericHeadingsCheck.IsChecked = hierarchy.RecognizeNumericHeadings;
        NumericMinimumLinesText.Text = hierarchy.NumericHeadingMinimumBodyLines.ToString();
        _numericPattern = hierarchy.NumericHeadingPattern;
        Level1PatternText.Text = hierarchy.Level1Pattern;
        Level2PatternText.Text = hierarchy.Level2Pattern;
        Level3PatternText.Text = hierarchy.Level3Pattern;
        _recognitionState = CaptureRules();
        SubscribeToNodes(Roots);
        _currentSnapshot = CaptureSnapshot();
        _initialSnapshot = _currentSnapshot;
        UpdateSummary();
        UpdateUndoRedoButtons();
        InitializeWorkbench();
    }

    public ObservableCollection<ChapterTreeNode> Roots { get; }
    public ObservableCollection<ChapterTreeSourceLine> SelectedLines { get; } = [];
    public ChapterTreePlan? ResultPlan { get; private set; }
    public TocHierarchyOptions? ResultHierarchyOptions { get; private set; }
    public string? ResultChapterPattern { get; private set; }

    public void NavigateToSuggestion(ConversionPreflightIssue issue)
    {
        ReviewOnlyCheck.IsChecked = true;
        AllChaptersRadio.IsChecked = false;
        IssueCategoryCombo.SelectedIndex = 0;
        UpdateReviewVisibility();
        var match = ChapterSuggestionsCombo.Items.Cast<ConversionPreflightIssue>()
            .FirstOrDefault(candidate => candidate.Code == issue.Code && candidate.LineNumber == issue.LineNumber);
        if (match is not null) ChapterSuggestionsCombo.SelectedItem = match;
        else if (issue.LineNumber is int line) NavigateToSourceLine(line);
    }

    public void NavigateToSourceLine(int lineNumber)
    {
        ClearReviewResult();
        _expandSource = false;
        if (!_refreshingSuggestions)
        {
            var match = ChapterSuggestionsCombo.Items.Cast<ConversionPreflightIssue>().FirstOrDefault(issue => issue.LineNumber == lineNumber);
            _refreshingSuggestions = true;
            try { ChapterSuggestionsCombo.SelectedItem = match; }
            finally { _refreshingSuggestions = false; }
            UpdateSuggestionButtons();
        }
        _selectedNode = Flatten().FirstOrDefault(node => node.ToEntry().TitleLineNumber == lineNumber)
            ?? Flatten().FirstOrDefault(node => node.ToEntry().ContentRanges.Any(range => lineNumber >= range.StartLine && lineNumber <= range.EndLine));
        if (_selectedNode is null) { ShowReviewFeedback($"原文第 {lineNumber} 行不属于当前章节树。"); return; }
        _selectedNode.IsReviewVisible = true;
        for (var ancestor = _selectedNode.Parent; ancestor is not null; ancestor = ancestor.Parent) ancestor.IsReviewVisible = true;
        SelectRestoredNode();
        if (!_applyingMultiSelection) { SetOperationSelection([_selectedNode]); _selectionAnchor = _selectedNode; }
        RefreshSelectedLines();
        UpdateActionButtons();
        var line = SelectedLines.FirstOrDefault(item => item.LineNumber == lineNumber);
          if (line is null)
        {
              _expandSource = true;
              RefreshSelectedLines();
              line = SelectedLines.FirstOrDefault(item => item.LineNumber == lineNumber);
        }
        if (line is null) return;
        SourceLinesList.SelectedItem = line;
        SourceLinesList.ScrollIntoView(line);
        if (_selectedNode.ToEntry().TitleLineNumber == lineNumber) SplitButton.IsEnabled = false;
    }

    private void ChapterTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedNode = e.NewValue as ChapterTreeNode;
        if (!_applyingMultiSelection) SetOperationSelection(_selectedNode is null ? [] : [_selectedNode]);
        RefreshSelectedLines();
        UpdateActionButtons();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void MoveSelected(int direction)
    {
        if (_selectedNode is null || OperationSelection().Length != 1 || _sourceChanged) return;
        var siblings = Siblings(_selectedNode);
        var index = siblings.IndexOf(_selectedNode);
        var target = index + direction;
        if (target < 0 || target >= siblings.Count) return;
        Mutate(() => siblings.Move(index, target));
        UpdateSummary();
        UpdateActionButtons();
    }

    private void Promote_Click(object sender, RoutedEventArgs e)
    {
        if (OperationSelection().Length != 1 || _sourceChanged) return;
        var selected = _selectedNode;
        if (selected?.Parent is not { } parent) return;
        var selectedIndex = parent.Children.IndexOf(selected);
        if (selectedIndex < 0) return;
        var followingCount = parent.Children.Count - selectedIndex - 1;
        if (followingCount > 0 && !ConfirmWorkbench($"「{selected.Title}」将移出父章节。为保持正文顺序，后续 {followingCount} 个同级章节将归入它的子章节。\n可撤销，是否继续？", "提升章节层级")) return;
        Mutate(() =>
        {
            var followingSiblings = parent.Children.Skip(selectedIndex + 1).ToArray();
            foreach (var sibling in followingSiblings) parent.Children.Remove(sibling);
            parent.Children.Remove(selected);
            var newSiblings = parent.Parent?.Children ?? Roots;
            newSiblings.Insert(newSiblings.IndexOf(parent) + 1, selected);
            selected.Parent = parent.Parent;
            SetLevelRecursive(selected, parent.Level);
            foreach (var sibling in followingSiblings)
            {
                sibling.Parent = selected;
                selected.Children.Add(sibling);
            }
        });
        _selectedNode = selected;
        RefreshSelectedLines();
        UpdateSummary();
        UpdateActionButtons();
    }

    private void Demote_Click(object sender, RoutedEventArgs e)
    {
        if (TryBatchChapterAction(merge: false)) return;
        var selected = _selectedNode;
        if (selected is null) return;
        if (selected.IsFrontMatter)
        {
            ShowInfo("前置章节不参与卷、章、节层级，不能降为其他章节的子项。", "前置章节");
            return;
        }
        var siblings = Siblings(selected);
        var index = siblings.IndexOf(selected);
        if (index <= 0) return;
        var newParent = siblings[index - 1];
        if (newParent.IsFrontMatter)
        {
            ShowInfo("前置章节不能包含普通章节。", "无法降级");
            return;
        }
        if (MaxRelativeDepth(selected) + newParent.Level > 4)
        {
            ShowInfo("降级后会超过四级目录。", "无法降级");
            return;
        }
        Mutate(() =>
        {
            siblings.Remove(selected);
            newParent.Children.Add(selected);
            selected.Parent = newParent;
            SetLevelRecursive(selected, newParent.Level + 1);
        });
        _selectedNode = selected;
        RefreshSelectedLines();
        UpdateSummary();
        UpdateActionButtons();
    }

    private void Merge_Click(object sender, RoutedEventArgs e)
    {
        if (TryBatchChapterAction(merge: true)) return;
        if (_selectedNode is null) return;
        var siblings = Siblings(_selectedNode);
        var index = siblings.IndexOf(_selectedNode);
        if (index <= 0)
        {
            ShowInfo("当前章节没有可合并的上一章。", "无法合并");
            return;
        }
        var previous = siblings[index - 1];
        if (_selectedNode.IsFrontMatter || previous.IsFrontMatter) { ShowReviewFeedback("前置项不参与正文归并。"); return; }
        if (previous.Children.Count > 0 || _selectedNode.Children.Count > 0)
        {
            ShowInfo("含有子章节的节点不能直接合并。请先调整子章节层级，避免正文顺序发生歧义。", "无法合并");
            return;
        }
        Mutate(() =>
        {
            previous.ContentRanges = previous.ContentRanges.Concat(WithOriginalTitle(_selectedNode)).ToArray();
            siblings.Remove(_selectedNode);
            previous.NotifyLineCount();
        });
        _selectedNode = previous;
        SetOperationSelection([previous]);
        RefreshSelectedLines();
        UpdateSummary();
        UpdateActionButtons();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSplitSelectedLine() || _selectedNode is null || SourceLinesList.SelectedItem is not ChapterTreeSourceLine selectedLine
            || _selectedNode.ToEntry().TitleLineNumber == selectedLine.LineNumber) return;
        var before = new List<ChapterSourceRange>();
        var after = new List<ChapterSourceRange>();
        foreach (var range in _selectedNode.ContentRanges)
        {
            if (selectedLine.LineNumber <= range.StartLine) after.Add(range);
            else if (selectedLine.LineNumber > range.EndLine) before.Add(range);
            else
            {
                if (range.StartLine <= selectedLine.LineNumber - 1)
                    before.Add(new ChapterSourceRange(range.StartLine, selectedLine.LineNumber - 1));
                after.Add(new ChapterSourceRange(selectedLine.LineNumber, range.EndLine));
            }
        }
        if (before.Count == 0 || after.Count == 0)
        {
            ShowInfo("请在本章正文中间选择拆分位置。", "无法拆分");
            return;
        }
        Mutate(() =>
        {
            _selectedNode.ContentRanges = before;
            _selectedNode.NotifyLineCount();
            var newNode = new ChapterTreeNode(new ChapterTreeEntry(
                Guid.NewGuid().ToString("N"), "新章节", _selectedNode.Level, true, null, after) { RecognitionSource = "manual" })
            {
                Parent = _selectedNode.Parent,
                IsFrontMatter = _selectedNode.IsFrontMatter,
                HeadingLevel = _selectedNode.HeadingLevel,
            };
            var siblings = Siblings(_selectedNode);
            siblings.Insert(siblings.IndexOf(_selectedNode) + 1, newNode);
        });
        RefreshSelectedLines();
        UpdateSummary();
        UpdateActionButtons();
    }

    private void NormalizeAll_Click(object sender, RoutedEventArgs e)
    {
        var changes = Flatten().Select(n => (Node: n, Title: ChapterTitleNormalizer.TryNormalizeNumericTitle(n.Title, out var normalized) ? normalized : n.Title))
            .Where(x => x.Node.Title != x.Title).ToArray();
        if (changes.Length == 0) { ShowReviewFeedback("没有需要规范化的数字标题。"); return; }
        if (!PreviewChanges("规范化全书数字标题", changes.Select(x => x.Node.Title + " → " + x.Title).ToArray(),
            "只修改成品标题与目录，不重新编号、不修改原始 TXT。")) return;
        Mutate(() => { foreach (var change in changes) change.Node.Title = change.Title; });
        RefreshSelectedLines();
        SetReviewResult($"已规范化 {changes.Length} 个成品标题，原始 TXT 不变。");
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        Mutate(() => { foreach (var node in Flatten()) node.IncludeInToc = true; });
        UpdateSummary();
        UpdateActionButtons();
    }
    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        Mutate(() => { foreach (var node in Flatten()) node.IncludeInToc = false; });
        UpdateSummary();
        UpdateActionButtons();
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ValidateRules();
            if (RecognitionRulesChanged() && !ConfirmWorkbench("识别规则已调整，当前树尚未重新识别。\n选择“是”保留当前手调树并保存规则（规则用于后续识别）。\n选择“否”返回，可在识别设置中先重新识别。", "保留当前章节树？")) return;
            var bytes = await System.IO.File.ReadAllBytesAsync(_document.SourcePath);
            if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != _document.SourceSha256)
                throw new InvalidOperationException("原始 TXT 已修改，当前章节位置可能失效。请先按当前规则重新识别，再保存章节树。");
            ResultPlan = _document.CreatePlan(Flatten().Select(node => node.ToEntry()));
            ResultHierarchyOptions = ReadHierarchyOptions();
            ResultPlan = ResultPlan with
            {
                NumericHeadingRecognition = UsesGlobalNumeric(ResultHierarchyOptions) ? null : ResultHierarchyOptions.RecognizeNumericHeadings,
                NumericHeadingMinimumBodyLines = UsesGlobalNumeric(ResultHierarchyOptions) ? null : ResultHierarchyOptions.NumericHeadingMinimumBodyLines,
                NumericHeadingPattern = UsesGlobalNumeric(ResultHierarchyOptions) ? null : ResultHierarchyOptions.NumericHeadingPattern,
            };
            ResultChapterPattern = NormalizePattern(ChapterPatternText.Text);
            _allowClose = true;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法保存章节树", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ChapterTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject) is not null) { _draggedNode = null; return; }
        _dragStart = e.GetPosition(ChapterTree);
        _draggedNode = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ChapterTreeNode;
        if (_draggedNode is not null)
        {
            SelectChapterForOperation(_draggedNode, Keyboard.Modifiers);
            if (e.ClickCount == 1 || Keyboard.Modifiers != ModifierKeys.None) e.Handled = true;
            if (OperationSelection().Length > 1) _draggedNode = null;
        }
    }
    private void ChapterTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedNode is null) return;
        var current = e.GetPosition(ChapterTree);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(ChapterTree, _draggedNode, DragDropEffects.Move);
    }
    private void ChapterTree_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ChapterTreeNode))) return;
        var dragged = (ChapterTreeNode)e.Data.GetData(typeof(ChapterTreeNode))!;
        var target = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ChapterTreeNode;
        if (ReferenceEquals(dragged, target) || target is not null && IsDescendant(target, dragged)) return;
        if (dragged.IsFrontMatter && target is not null || target?.IsFrontMatter == true)
        {
            ShowInfo("前置章节必须保持为根节点，且不能包含普通章节。", "无法移动");
            return;
        }
        if (target is not null && MaxRelativeDepth(dragged) + target.Level > 4)
        {
            ShowInfo("拖放后会超过四级目录。", "无法移动");
            return;
        }
        Mutate(() =>
        {
            Siblings(dragged).Remove(dragged);
            if (target is null)
            {
                Roots.Add(dragged);
                dragged.Parent = null;
                SetLevelRecursive(dragged, dragged.IsFrontMatter ? 2 : 1);
            }
            else
            {
                target.Children.Add(dragged);
                dragged.Parent = target;
                SetLevelRecursive(dragged, target.Level + 1);
            }
        });
        UpdateSummary();
        UpdateActionButtons();
    }

    private async void RebuildFromRules_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmWorkbench("重新识别会替换当前手动章节树。同一原文版本可撤销；原文变化时开启新历史。是否继续？", "重新识别章节")) return;
        try
        {
            ValidateRules();
            RebuildRulesButton.IsEnabled = false;
            var replacement = await ChapterTreeDocument.LoadAsync(_document.SourcePath, NormalizePattern(ChapterPatternText.Text), ReadHierarchyOptions(), _encodingMode);
            ApplyRebuiltDocument(replacement);
        }
        catch (Exception exception) { InkDialog.Show(_rulesDialog ?? this, exception.Message, "无法重新识别章节", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { RebuildRulesButton.IsEnabled = true; }
    }

    private void ResetRules_Click(object sender, RoutedEventArgs e)
    {
        HierarchyEnabledCheck.IsChecked = true;
        Level1PatternText.Text = TocHierarchyOptions.DefaultLevel1Pattern;
        Level2PatternText.Text = TocHierarchyOptions.DefaultLevel2Pattern;
        Level3PatternText.Text = TocHierarchyOptions.DefaultLevel3Pattern;
    }

    private TocHierarchyOptions ReadHierarchyOptions() => new()
    {
        Enabled = HierarchyEnabledCheck.IsChecked == true,
        RecognizeNumericHeadings = NumericHeadingsCheck.IsChecked == true,
        NumericHeadingMinimumBodyLines = ReadNumericMinimumLines(),
        NumericHeadingPattern = _numericPattern,
        IncludeHtmlTocPage = IncludeHtmlTocPageCheck.IsChecked == true,
        IncludeChapterTopNavigation = IncludeChapterTopNavigationCheck.IsChecked == true,
        Level1Pattern = NormalizePattern(Level1PatternText.Text) ?? TocHierarchyOptions.DefaultLevel1Pattern,
        Level2Pattern = NormalizePattern(Level2PatternText.Text) ?? TocHierarchyOptions.DefaultLevel2Pattern,
        Level3Pattern = NormalizePattern(Level3PatternText.Text) ?? TocHierarchyOptions.DefaultLevel3Pattern,
    };

    private static string? NormalizePattern(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private int ReadNumericMinimumLines()
    {
        if (!int.TryParse(NumericMinimumLinesText.Text.Trim(), out var minimum) || minimum < 0)
            throw new InvalidOperationException("数字章节正文行数请输入大于或等于 0 的整数；0 表示不限。");
        return minimum;
    }

    private bool UsesGlobalNumeric(TocHierarchyOptions value) => InheritNumericDefaults
        && value.RecognizeNumericHeadings == GlobalNumericDefaults.RecognizeNumericHeadings
        && value.NumericHeadingMinimumBodyLines == GlobalNumericDefaults.NumericHeadingMinimumBodyLines
        && value.NumericHeadingPattern == GlobalNumericDefaults.NumericHeadingPattern;

    private void NumericRules_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var current = ReadHierarchyOptions();
            var dialog = new NumericHeadingRulesWindow(current, GlobalNumericDefaults, NumericPresets, UsesGlobalNumeric(current)) { Owner = _rulesDialog ?? this };
            if (dialog.ShowDialog() != true) return;
            NumericPresets = dialog.Presets;
            if (dialog.Scope == 1) GlobalNumericDefaults = dialog.Result;
            InheritNumericDefaults = dialog.Scope != 0;
            NumericHeadingsCheck.IsChecked = dialog.Result.RecognizeNumericHeadings;
            NumericMinimumLinesText.Text = dialog.Result.NumericHeadingMinimumBodyLines.ToString();
            _numericPattern = dialog.Result.NumericHeadingPattern;
            NumericScopeText.Text = InheritNumericDefaults ? "本书继承全局数字规则；独立覆盖不受影响。" : "本书独立数字规则；不影响其他书。";
        }
        catch (Exception error) { InkDialog.Show(this, error.Message, "数字识别规则"); }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (e.Key == Key.Escape) { if (ChapterActionsPopup.IsOpen) ChapterActionsPopup.IsOpen = false; else Close(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
        }
    }

    private void Undo()
    {
        if (_sourceChanged) return;
        ClearReviewResult();
        if (_undo.Count == 0) return;
        _redo.Push(CaptureSnapshot());
        RestoreSnapshot(_undo.Pop());
    }

    private void Redo()
    {
        if (_sourceChanged) return;
        ClearReviewResult();
        if (_redo.Count == 0) return;
        _undo.Push(CaptureSnapshot());
        RestoreSnapshot(_redo.Pop());
    }

    private void Mutate(Action change)
    {
        if (_sourceChanged) { ShowReviewFeedback("原文已变化，请先重新识别。"); return; }
        var before = CaptureSnapshot();
        _trackingPaused = true;
        try { change(); }
        finally { _trackingPaused = false; }
        SubscribeToNodes(Roots);
        var after = CaptureSnapshot();
        if (!SnapshotsEqual(before, after))
        {
            _detectUnrecognized = false;
            _undo.Push(before);
            _redo.Clear();
        }
        _currentSnapshot = after;
        UpdateUndoRedoButtons();
        UpdateSummary();
        if (!SnapshotsEqual(before, after)) SetReviewResult("修改已完成，原始 TXT 不变；请核对原文，或撤销本次处理。");
        UpdateSaveState();
    }

    private void Node_Changed(object? sender, EventArgs e)
    {
        if (_trackingPaused) return;
        var after = CaptureSnapshot();
        if (!SnapshotsEqual(_currentSnapshot, after))
        {
            _undo.Push(_currentSnapshot);
            _redo.Clear();
            _currentSnapshot = after;
            UpdateUndoRedoButtons();
            UpdateSummary();
        }
    }

    private void SubscribeToNodes(IEnumerable<ChapterTreeNode> roots)
    {
        foreach (var node in roots)
        {
            if (_trackedNodes.Add(node)) node.Changed += Node_Changed;
            SubscribeToNodes(node.Children);
        }
    }

    private ChapterEditorSnapshot CaptureSnapshot() => new(
        Flatten().Select(node => node.ToEntry() with { ContentRanges = node.ContentRanges.ToArray() }).ToArray(),
        _selectedNode?.Id, _document, _recognitionState, _detectUnrecognized,
        OperationSelection().Select(n => n.Id).ToArray(), Flatten().Where(n => n.IsExpanded).Select(n => n.Id).ToArray());

    private void RestoreSnapshot(ChapterEditorSnapshot snapshot)
    {
        _trackingPaused = true;
        try
        {
            if (!ReferenceEquals(_document, snapshot.Document)) ApplyRules(snapshot.RecognitionRules);
            _document = snapshot.Document;
            _recognitionState = snapshot.RecognitionRules;
            _detectUnrecognized = snapshot.DetectUnrecognized;
            Roots.Clear();
            foreach (var root in BuildTree(snapshot.Entries)) Roots.Add(root);
            SubscribeToNodes(Roots);
            _selectedNode = Flatten().FirstOrDefault(node => node.Id == snapshot.SelectedId);
            foreach (var node in Flatten()) node.IsExpanded = snapshot.ExpandedIds.Contains(node.Id);
            SetOperationSelection(Flatten().Where(n => snapshot.SelectedIds.Contains(n.Id)));
        }
        finally
        {
            _trackingPaused = false;
        }
        _currentSnapshot = CaptureSnapshot();
        RefreshSelectedLines();
        UpdateSummary();
        UpdateActionButtons();
        UpdateUndoRedoButtons();
        Dispatcher.BeginInvoke(() => { _applyingMultiSelection = true; try { SelectRestoredNode(); } finally { _applyingMultiSelection = false; } }, DispatcherPriority.Loaded);
    }

    private void SelectRestoredNode()
    {
        if (_selectedNode is null) return;
        var path = new Stack<ChapterTreeNode>();
        for (var node = _selectedNode; node is not null; node = node.Parent) path.Push(node);
        ItemsControl parent = ChapterTree;
        while (path.Count > 0)
        {
            parent.UpdateLayout();
            var node = path.Pop();
            if (parent.ItemContainerGenerator.ContainerFromItem(node) is null)
            {
                var presenter = FindItemsPresenter(parent);
                if (presenter is not null && VisualTreeHelper.GetChildrenCount(presenter) > 0
                    && VisualTreeHelper.GetChild(presenter, 0) is VirtualizingPanel panel)
                {
                    panel.BringIndexIntoViewPublic(parent.Items.IndexOf(node));
                    parent.UpdateLayout();
                }
            }
            if (parent.ItemContainerGenerator.ContainerFromItem(node) is not TreeViewItem container) return;
            if (path.Count == 0)
            {
                container.IsSelected = true;
                container.BringIntoView();
                return;
            }
            container.IsExpanded = true;
            parent = container;
        }
    }

    private static ItemsPresenter? FindItemsPresenter(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ItemsPresenter presenter) return presenter;
            if (child is TreeViewItem) continue;
            if (FindItemsPresenter(child) is { } nested) return nested;
        }
        return null;
    }

    private static bool SnapshotsEqual(ChapterEditorSnapshot left, ChapterEditorSnapshot right)
    {
        if (!ReferenceEquals(left.Document, right.Document)) return false;
        if (left.Entries.Count != right.Entries.Count) return false;
        for (var index = 0; index < left.Entries.Count; index++)
        {
            var a = left.Entries[index];
            var b = right.Entries[index];
            if (a.Id != b.Id || a.Title != b.Title || a.Level != b.Level || a.IncludeInToc != b.IncludeInToc
                || a.TitleLineNumber != b.TitleLineNumber || a.IsFrontMatter != b.IsFrontMatter
                || a.HeadingLevel != b.HeadingLevel || !a.ContentRanges.SequenceEqual(b.ContentRanges)) return false;
        }
        return true;
    }

    private void UpdateUndoRedoButtons()
    {
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        UpdateSaveState();
    }

    private void SourceLinesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SplitButton.IsEnabled = !_sourceChanged && OperationSelection().Length == 1 && CanSplitSelectedLine();
        SplitButton.Visibility = SplitButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateReviewCard();
    }

    private bool CanSplitSelectedLine() => _selectedNode is not null
        && SourceLinesList.SelectedItem is ChapterTreeSourceLine line
        && _selectedNode.ContentRanges.Any(range => line.LineNumber >= range.StartLine && line.LineNumber <= range.EndLine)
        && line.LineNumber != _selectedNode.ToEntry().TitleLineNumber
        && _selectedNode.ContentRanges.Any(range => range.StartLine < line.LineNumber);

    private void RefreshSelectedLines()
    {
        SelectedLines.Clear();
        SplitButton.IsEnabled = false;
        SplitButton.Visibility = Visibility.Collapsed;
        if (_selectedNode is null) { SelectionHint.Text = "请选择章节"; return; }
        var lines = WithOriginalTitle(_selectedNode).SelectMany(range => Enumerable.Range(range.StartLine, range.EndLine - range.StartLine + 1));
        var limit = _expandSource ? int.MaxValue : 160;
        foreach (var number in lines.Distinct().Take(limit))
            if (_document.SourceLine(number) is { } source) SelectedLines.Add(source);
        var count = _selectedNode.ContentRanges.Sum(r => r.EndLine - r.StartLine + 1);
        SelectionHint.Text = $"正文 {count} 行 · 原始标题一并显示";
        ExpandSourceButton.Visibility = count >= 160 && !_expandSource ? Visibility.Visible : Visibility.Collapsed;
        RefreshReviewGroupPreview();
        if (SelectedLines.Count > 0) SourceLinesList.ScrollIntoView(SelectedLines[0]);
    }

    private void UpdateSummary()
    {
        var nodes = Flatten().ToArray();
        var normalNodes = nodes.Where(node => !node.IsFrontMatter).ToArray();
        var frontMatterCount = nodes.Length - normalNodes.Length;
        var depth = normalNodes.Length == 0 ? 0 : normalNodes.Max(node => node.Level);
        var frontMatterLabel = frontMatterCount == 0 ? string.Empty : $" · 前置 {frontMatterCount} 项";
        SummaryText.Text = $"{normalNodes.Length} 章{frontMatterLabel} · {nodes.Count(node => node.IncludeInToc)} 项进入目录 · 最深 {depth} 级";
        RefreshSuggestions(nodes.Select(node => node.ToEntry()).ToArray());
    }

    private void RefreshSuggestions(IReadOnlyList<ChapterTreeEntry> entries)
    {
        var selected = ChapterSuggestionsCombo.SelectedItem as ConversionPreflightIssue;
        var oldIndex = ChapterSuggestionsCombo.SelectedIndex;
        var analysis = ChapterReviewAnalyzer.Analyze(_document, entries, _detectUnrecognized, _reviewLimit);
        _reviewGroups = analysis.Groups.ToArray();
        _reviewGroupIndex.Clear();
        foreach (var group in _reviewGroups)
            _reviewGroupIndex.TryAdd((group.Issue.Code, group.Issue.LineNumber), group);
        _totalReviewGroups = analysis.TotalGroups;
        _allReviewIssues = _reviewGroups.Where(g => !_confirmedGroups.ContainsKey(ReviewKey(g))).Select(g => g.Issue).ToArray();
        var issues = FilteredReviewIssues();
        _refreshingSuggestions = true;
        try
        {
            ChapterSuggestionsCombo.ItemsSource = issues;
            ChapterSuggestionsCombo.SelectedItem = issues.FirstOrDefault(issue => selected is not null && issue.Code == selected.Code && issue.LineNumber == selected.LineNumber);
            if (oldIndex >= 0) _nextSuggestionIndex = Math.Min(oldIndex, Math.Max(0, issues.Length - 1));
            _nextSuggestionIndex = Math.Min(_nextSuggestionIndex, Math.Max(0, issues.Length - 1));
        }
        finally { _refreshingSuggestions = false; }
        LoadMoreIssuesButton.Visibility = _reviewGroups.Length < _totalReviewGroups ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreIssuesButton.Content = $"已加载 {_reviewGroups.Length} / {_totalReviewGroups} 组 · 继续加载";
        UpdateSuggestionButtons();
        UpdateReviewVisibility();
    }

    private void UpdateSuggestionButtons()
    {
        var count = ChapterSuggestionsCombo.Items.Count;
        var index = ChapterSuggestionsCombo.SelectedIndex;
        SuggestionCountText.Text = count == 0 ? (_allReviewIssues.Length == 0 ? "暂无待核对组" : "当前筛选无匹配组") : index < 0 ? $"待核对 {count} 组 / 已加载 {_allReviewIssues.Length} 组" : $"第 {index + 1} / {count} 组";
        PreviousSuggestionButton.IsEnabled = count > 0 && (index > 0 || index < 0 && _nextSuggestionIndex > 0);
        NextSuggestionButton.IsEnabled = count > 0 && index < count - 1;
        ChapterSuggestionsCombo.IsEnabled = count > 0;
        UpdateReviewCard();
    }

    private void ChapterSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingSuggestions) return;
        ClearReviewResult();
        OnlyCurrentIssueCheck.IsChecked = false;
        if (ChapterSuggestionsCombo.SelectedItem is ConversionPreflightIssue { LineNumber: int line })
        {
            // Keep separate suggestions at the same source line individually selectable.
            _refreshingSuggestions = true;
            try { NavigateToSourceLine(line); }
            finally { _refreshingSuggestions = false; }
        }
        UpdateSuggestionButtons();
    }

    private void PreviousSuggestion_Click(object sender, RoutedEventArgs e) =>
        ChapterSuggestionsCombo.SelectedIndex = Math.Max(0, (ChapterSuggestionsCombo.SelectedIndex < 0 ? _nextSuggestionIndex : ChapterSuggestionsCombo.SelectedIndex) - 1);

    private void NextSuggestion_Click(object sender, RoutedEventArgs e) =>
        ChapterSuggestionsCombo.SelectedIndex = Math.Min(ChapterSuggestionsCombo.Items.Count - 1,
            ChapterSuggestionsCombo.SelectedIndex < 0 ? _nextSuggestionIndex : ChapterSuggestionsCombo.SelectedIndex + 1);

    private void UpdateActionButtons()
    {
        UpdateNumberSuggestion();
        if (_selectedNode is null)
        {
            MoveUpButton.IsEnabled = false;
            MoveDownButton.IsEnabled = false;
            PromoteButton.IsEnabled = false;
            DemoteButton.IsEnabled = false;
            MergeButton.IsEnabled = false;
            SplitButton.IsEnabled = false;
            UpdateReviewCard();
            return;
        }

        var siblings = Siblings(_selectedNode);
        var index = siblings.IndexOf(_selectedNode);
        MoveUpButton.IsEnabled = index > 0;
        MoveDownButton.IsEnabled = index >= 0 && index < siblings.Count - 1;
        PromoteButton.IsEnabled = !_selectedNode.IsFrontMatter && _selectedNode.Parent is not null;
        DemoteButton.IsEnabled = !_selectedNode.IsFrontMatter
            && index > 0
            && !siblings[index - 1].IsFrontMatter
            && MaxRelativeDepth(_selectedNode) + siblings[index - 1].Level <= 4;
        MergeButton.IsEnabled = !_selectedNode.IsFrontMatter && index > 0 && !siblings[index - 1].IsFrontMatter
            && siblings[index - 1].Children.Count == 0
            && _selectedNode.Children.Count == 0;
        SplitButton.IsEnabled = CanSplitSelectedLine();
        UpdateBatchActionButtons();
        if (_sourceChanged) MoveUpButton.IsEnabled = MoveDownButton.IsEnabled = PromoteButton.IsEnabled = DemoteButton.IsEnabled = MergeButton.IsEnabled = SplitButton.IsEnabled = false;
        SplitButton.Visibility = SplitButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        UpdateReviewCard();
    }
    private IEnumerable<ChapterTreeNode> Flatten()
    {
        foreach (var root in Roots)
        {
            yield return root;
            foreach (var child in Flatten(root)) yield return child;
        }
    }
    private static IEnumerable<ChapterTreeNode> Flatten(ChapterTreeNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var nested in Flatten(child)) yield return nested;
        }
    }
    private ObservableCollection<ChapterTreeNode> Siblings(ChapterTreeNode node) => node.Parent?.Children ?? Roots;
    private static ObservableCollection<ChapterTreeNode> BuildTree(IReadOnlyList<ChapterTreeEntry> entries)
    {
        var roots = new ObservableCollection<ChapterTreeNode>();
        var stack = new Stack<ChapterTreeNode>();
        foreach (var entry in entries)
        {
            var node = new ChapterTreeNode(entry);
            if (node.IsFrontMatter)
            {
                roots.Add(node);
                stack.Clear();
                continue;
            }
            while (stack.Count > 0 && stack.Peek().Level >= node.Level) stack.Pop();
            if (stack.Count == 0) roots.Add(node);
            else { node.Parent = stack.Peek(); stack.Peek().Children.Add(node); }
            stack.Push(node);
        }
        return roots;
    }
    private static void SetLevelRecursive(ChapterTreeNode node, int level)
    {
        node.Level = Math.Clamp(level, 1, 4);
        foreach (var child in node.Children) SetLevelRecursive(child, node.Level + 1);
    }
    private static int MaxRelativeDepth(ChapterTreeNode node) => node.Children.Count == 0 ? 1 : 1 + node.Children.Max(MaxRelativeDepth);
    private static bool IsDescendant(ChapterTreeNode candidate, ChapterTreeNode ancestor)
    {
        for (var current = candidate.Parent; current is not null; current = current.Parent)
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }
    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match) return match;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
    private void ShowInfo(string message, string title) =>
        InkDialog.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}

internal sealed record ChapterEditorSnapshot(
    IReadOnlyList<ChapterTreeEntry> Entries,
    string? SelectedId,
    ChapterTreeDocument Document,
    ChapterRuleState RecognitionRules,
    bool DetectUnrecognized,
    string[] SelectedIds,
    string[] ExpandedIds);

public sealed class ChapterTreeNode : INotifyPropertyChanged
{
    private string _title;
    private int _level;
    private bool _includeInToc;
    private IReadOnlyList<ChapterSourceRange> _contentRanges;
    private bool _operationSelected;
    private bool _reviewVisible = true;
    public bool IsReviewVisible { get => _reviewVisible; set { if (_reviewVisible == value) return; _reviewVisible = value; OnPropertyChanged(nameof(IsReviewVisible)); } }
    public bool IsOperationSelected { get => _operationSelected; set { if (_operationSelected == value) return; _operationSelected = value; OnPropertyChanged(nameof(IsOperationSelected)); } }
    private bool _expanded;
    public bool IsExpanded { get => _expanded; set { if (_expanded == value) return; _expanded = value; OnPropertyChanged(nameof(IsExpanded)); } }
    private string _reviewDescription = "";
    public string ReviewDescription { get => _reviewDescription; set { if (_reviewDescription == value) return; _reviewDescription = value; OnPropertyChanged(nameof(ReviewDescription)); OnPropertyChanged(nameof(ReviewMarker)); } }
    public string ReviewMarker => string.IsNullOrEmpty(ReviewDescription) ? "" : "•";
    public string? RecognitionSource { get; set; }
    public ChapterTreeNode(ChapterTreeEntry entry)
    {
        Id = entry.Id; _title = entry.Title; _level = entry.Level; _includeInToc = entry.IncludeInToc;
        TitleLineNumber = entry.TitleLineNumber; _contentRanges = entry.ContentRanges ?? [];
        IsFrontMatter = entry.IsFrontMatter;
        RecognitionSource = entry.RecognitionSource;
        HeadingLevel = entry.HeadingLevel is >= 1 and <= 4 ? entry.HeadingLevel : entry.Level;
    }
    public string Id { get; }
    public int? TitleLineNumber { get; }
    public bool IsFrontMatter { get; set; }
    public int HeadingLevel { get; set; }
    public ChapterTreeNode? Parent { get; set; }
    public ObservableCollection<ChapterTreeNode> Children { get; } = [];
    public string Title { get => _title; set => SetField(ref _title, value); }
    public int Level { get => _level; set { if (SetField(ref _level, value)) OnPropertyChanged(nameof(LevelLabel)); } }
    public bool IncludeInToc { get => _includeInToc; set => SetField(ref _includeInToc, value); }
    public IReadOnlyList<ChapterSourceRange> ContentRanges
    {
        get => _contentRanges;
        set
        {
            if (_contentRanges.SequenceEqual(value)) return;
            _contentRanges = value;
            OnPropertyChanged(nameof(LineCountLabel));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
    public string LevelLabel => IsFrontMatter ? "前置" : $"L{Level}";
    public string LineCountLabel => $"{ContentRanges.Sum(range => range.EndLine - range.StartLine + 1)} 行";
    public ChapterTreeEntry ToEntry() => new(Id, Title, Level, IncludeInToc, TitleLineNumber, ContentRanges)
    {
        IsFrontMatter = IsFrontMatter,
        HeadingLevel = HeadingLevel,
        RecognitionSource = RecognitionSource,
    };
    public void NotifyLineCount() => OnPropertyChanged(nameof(LineCountLabel));
    public event EventHandler? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
    private void OnPropertyChanged(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
