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

    public ChapterEditorWindow(ChapterTreeDocument document)
        : this(document, new TocHierarchyOptions(), null, TextEncodingMode.Auto)
    {
    }

    public ChapterEditorWindow(
        ChapterTreeDocument document,
        TocHierarchyOptions hierarchy,
        string? chapterPattern,
        TextEncodingMode encodingMode)
    {
        InitializeComponent();
        _document = document;
        _encodingMode = encodingMode;
        Roots = BuildTree(document.Entries);
        DataContext = this;
        SourceText.Text = document.SourcePath;
        SourceText.ToolTip = document.SourcePath;
        ChapterPatternText.Text = chapterPattern ?? string.Empty;
        HierarchyEnabledCheck.IsChecked = hierarchy.Enabled;
        Level1PatternText.Text = hierarchy.Level1Pattern;
        Level2PatternText.Text = hierarchy.Level2Pattern;
        Level3PatternText.Text = hierarchy.Level3Pattern;
        SubscribeToNodes(Roots);
        _currentSnapshot = CaptureSnapshot();
        UpdateSummary();
        UpdateUndoRedoButtons();
    }

    public ObservableCollection<ChapterTreeNode> Roots { get; }
    public ObservableCollection<ChapterTreeSourceLine> SelectedLines { get; } = [];
    public ChapterTreePlan? ResultPlan { get; private set; }
    public TocHierarchyOptions? ResultHierarchyOptions { get; private set; }
    public string? ResultChapterPattern { get; private set; }

    private void ChapterTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedNode = e.NewValue as ChapterTreeNode;
        RefreshSelectedLines();
        UpdateActionButtons();
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);
    private void MoveSelected(int direction)
    {
        if (_selectedNode is null) return;
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
        var selected = _selectedNode;
        if (selected?.Parent is not { } parent) return;
        var selectedIndex = parent.Children.IndexOf(selected);
        if (selectedIndex < 0) return;
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
        if (_selectedNode is null) return;
        var siblings = Siblings(_selectedNode);
        var index = siblings.IndexOf(_selectedNode);
        if (index <= 0)
        {
            ShowInfo("当前章节没有可合并的上一章。", "无法合并");
            return;
        }
        var previous = siblings[index - 1];
        if (previous.Children.Count > 0 || _selectedNode.Children.Count > 0)
        {
            ShowInfo("含有子章节的节点不能直接合并。请先调整子章节层级，避免正文顺序发生歧义。", "无法合并");
            return;
        }
        Mutate(() =>
        {
            previous.ContentRanges = previous.ContentRanges.Concat(_selectedNode.ContentRanges).ToArray();
            siblings.Remove(_selectedNode);
            previous.NotifyLineCount();
        });
        _selectedNode = previous;
        RefreshSelectedLines();
        UpdateSummary();
        UpdateActionButtons();
    }

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedNode is null || SourceLinesList.SelectedItem is not ChapterTreeSourceLine selectedLine) return;
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
                Guid.NewGuid().ToString("N"), "新章节", _selectedNode.Level, true, null, after))
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
        Mutate(() =>
        {
            foreach (var node in Flatten())
                if (ChapterTitleNormalizer.TryNormalizeNumericTitle(node.Title, out var normalized)) node.Title = normalized;
        });
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
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ResultPlan = _document.CreatePlan(Flatten().Select(node => node.ToEntry()));
            ResultHierarchyOptions = ReadHierarchyOptions();
            ResultChapterPattern = NormalizePattern(ChapterPatternText.Text);
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法保存章节树", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ChapterTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(ChapterTree);
        _draggedNode = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext as ChapterTreeNode;
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
        var confirm = MessageBox.Show(
            this,
            "重新识别会用当前规则重建章节树。你可以使用“撤销”恢复当前结构。是否继续？",
            "重新识别章节",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var replacement = await ChapterTreeDocument.LoadAsync(
                _document.SourcePath,
                NormalizePattern(ChapterPatternText.Text),
                ReadHierarchyOptions(),
                _encodingMode);
            Mutate(() =>
            {
                _document = replacement;
                Roots.Clear();
                foreach (var root in BuildTree(replacement.Entries)) Roots.Add(root);
                _selectedNode = null;
            });
            RefreshSelectedLines();
            UpdateSummary();
            UpdateActionButtons();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法重新识别章节", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        Level1Pattern = NormalizePattern(Level1PatternText.Text) ?? TocHierarchyOptions.DefaultLevel1Pattern,
        Level2Pattern = NormalizePattern(Level2PatternText.Text) ?? TocHierarchyOptions.DefaultLevel2Pattern,
        Level3Pattern = NormalizePattern(Level3PatternText.Text) ?? TocHierarchyOptions.DefaultLevel3Pattern,
    };

    private static string? NormalizePattern(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
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
        if (_undo.Count == 0) return;
        _redo.Push(CaptureSnapshot());
        RestoreSnapshot(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(CaptureSnapshot());
        RestoreSnapshot(_redo.Pop());
    }

    private void Mutate(Action change)
    {
        var before = CaptureSnapshot();
        _trackingPaused = true;
        try { change(); }
        finally { _trackingPaused = false; }
        SubscribeToNodes(Roots);
        var after = CaptureSnapshot();
        if (!SnapshotsEqual(before, after))
        {
            _undo.Push(before);
            _redo.Clear();
        }
        _currentSnapshot = after;
        UpdateUndoRedoButtons();
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
        _selectedNode?.Id);

    private void RestoreSnapshot(ChapterEditorSnapshot snapshot)
    {
        _trackingPaused = true;
        try
        {
            Roots.Clear();
            foreach (var root in BuildTree(snapshot.Entries)) Roots.Add(root);
            SubscribeToNodes(Roots);
            _selectedNode = Flatten().FirstOrDefault(node => node.Id == snapshot.SelectedId);
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
        Dispatcher.BeginInvoke(SelectRestoredNode, DispatcherPriority.Loaded);
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

    private static bool SnapshotsEqual(ChapterEditorSnapshot left, ChapterEditorSnapshot right)
    {
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
    }

    private void SourceLinesList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SplitButton.IsEnabled = _selectedNode is not null && SourceLinesList.SelectedItem is not null;

    private void RefreshSelectedLines()
    {
        SelectedLines.Clear();
        SplitButton.IsEnabled = false;
        if (_selectedNode is null)
        {
            SelectionHint.Text = "请选择章节";
            return;
        }
        foreach (var line in _document.GetSourceLines(_selectedNode.ToEntry())) SelectedLines.Add(line);
        SelectionHint.Text = $"{SelectedLines.Count} 行";
    }
    private void UpdateSummary()
    {
        var nodes = Flatten().ToArray();
        var normalNodes = nodes.Where(node => !node.IsFrontMatter).ToArray();
        var frontMatterCount = nodes.Length - normalNodes.Length;
        var depth = normalNodes.Length == 0 ? 0 : normalNodes.Max(node => node.Level);
        var frontMatterLabel = frontMatterCount == 0 ? string.Empty : $" · 前置 {frontMatterCount} 项";
        SummaryText.Text = $"{normalNodes.Length} 章{frontMatterLabel} · {nodes.Count(node => node.IncludeInToc)} 项进入目录 · 最深 {depth} 级";
    }

    private void UpdateActionButtons()
    {
        if (_selectedNode is null)
        {
            MoveUpButton.IsEnabled = false;
            MoveDownButton.IsEnabled = false;
            PromoteButton.IsEnabled = false;
            DemoteButton.IsEnabled = false;
            MergeButton.IsEnabled = false;
            SplitButton.IsEnabled = false;
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
        MergeButton.IsEnabled = index > 0
            && siblings[index - 1].Children.Count == 0
            && _selectedNode.Children.Count == 0;
        SplitButton.IsEnabled = SourceLinesList.SelectedItem is not null;
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
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
}

internal sealed record ChapterEditorSnapshot(
    IReadOnlyList<ChapterTreeEntry> Entries,
    string? SelectedId);

public sealed class ChapterTreeNode : INotifyPropertyChanged
{
    private string _title;
    private int _level;
    private bool _includeInToc;
    private IReadOnlyList<ChapterSourceRange> _contentRanges;
    public ChapterTreeNode(ChapterTreeEntry entry)
    {
        Id = entry.Id; _title = entry.Title; _level = entry.Level; _includeInToc = entry.IncludeInToc;
        TitleLineNumber = entry.TitleLineNumber; _contentRanges = entry.ContentRanges ?? [];
        IsFrontMatter = entry.IsFrontMatter;
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
