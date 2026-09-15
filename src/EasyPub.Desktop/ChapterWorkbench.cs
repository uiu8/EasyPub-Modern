using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow
{
    private ChapterEditorSnapshot? _initialSnapshot;
    private ChapterRuleState _recognitionState = null!;
    private string _headingNumberCorrections = HeadingTypoRules.Default;
    private string? _initialRulesFingerprint;
    private bool _allowClose;
    private bool _sourceChanged;
    private bool _checkingSource;
    private Window? _rulesDialog;
    private DispatcherTimer? _searchTimer;
    internal Func<string, bool>? ConfirmAction { get; set; }

    private void InitializeWorkbench()
    {
        RecognitionRulesPanel.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) => UpdateRuleGuidance()));
        RecognitionRulesPanel.AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => UpdateRuleGuidance()));
        RecognitionRulesPanel.AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => UpdateRuleGuidance()));
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplySearchFilter(); };
        Loaded += (_, _) =>
        {
            _initialRulesFingerprint ??= SettingsFingerprint();
            EditSourceButton.ToolTip = "使用 " + Path.GetFileNameWithoutExtension(TextEditorPath) + " 编辑；先备份，源文件变化后须重新识别";
            UpdateSaveState();
            if (_allReviewIssues.FirstOrDefault(i => i.Code == "numeric_chapters_suspected") is { } numericSuggestion)
                NavigateToSuggestion(numericSuggestion);
        };
        Activated += async (_, _) => await CheckSourceVersionAsync();
        Closing += Workbench_Closing;
        Closed += (_, _) => _searchTimer?.Stop();
        CurrentActionsPanel.AddHandler(Button.ClickEvent, new RoutedEventHandler((_, _) =>
        {
            ChapterActionsPopup.IsOpen = false;
            UpdateReviewVisibility();
            UpdateActionButtons();
        }));
    }

    internal bool ConfirmWorkbench(string message, string title) => ConfirmAction?.Invoke(message)
        ?? InkDialog.Show(_rulesDialog ?? this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    internal void DiscardChangesAndClose() { _allowClose = true; Close(); }

    private void Workbench_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose && HasUnsavedChanges() && !ConfirmWorkbench("有尚未提交的章节、规则或方案修改。是否放弃本次修改？\n外部编辑器已经写入 TXT 的内容不受影响。", "放弃工作台修改？")) e.Cancel = true;
    }

    private bool HasUnsavedChanges() => _initialSnapshot is not null && (!SnapshotsEqual(_initialSnapshot, CaptureSnapshot())
        || _initialRulesFingerprint is not null && _initialRulesFingerprint != SettingsFingerprint());

    private string SettingsFingerprint() => JsonSerializer.Serialize(CaptureRules()) + "|" + TextEditorPath;

    private void UpdateSaveState()
    {
        if (_document is null || SaveStateText is null || _initialSnapshot is null) return;
        SaveStateText.Text = _sourceChanged ? "原始 TXT 已变化 · 点击原文区的“刷新原文”，无需重新打开工作台"
            : (HasUnsavedChanges() ? "工作台修改未提交 · " : "原始 TXT 不变 · ") + "应用并返回后自动保存；未命名项目保留恢复快照";
        UpdateRuleGuidance();
        SaveChapterTreeButton.IsEnabled = !_sourceChanged;
        RefreshSourceButton.Content = _sourceChanged ? "原文已变化 · 刷新" : "刷新原文";
        RefreshSourceButton.FontWeight = _sourceChanged ? FontWeights.Bold : FontWeights.Normal;
        ChapterTree.IsEnabled = !_sourceChanged;
        if (_sourceChanged) UndoButton.IsEnabled = RedoButton.IsEnabled = false;
    }

    private void UpdateRuleGuidance()
    {
        if (_recognitionState is null || EffectiveRulesText is null) return;
        var rules = CaptureRules();
        var inherited = rules.Inherit && rules.Numeric == GlobalNumericDefaults.RecognizeNumericHeadings
            && int.TryParse(rules.Minimum, out var minimum) && minimum == GlobalNumericDefaults.NumericHeadingMinimumBodyLines
            && rules.NumericPattern == GlobalNumericDefaults.NumericHeadingPattern;
        NumericScopeText.Text = inherited ? "继承全局规则；在此直接修改会成为本书覆盖。" : "本书独立规则；不影响其他书。";
        EffectiveRulesText.Text = $"下次识别使用：普通章节{(string.IsNullOrWhiteSpace(rules.Pattern) ? "内置规则" : "自定义规则")}；"
            + (rules.Numeric ? $"数字章节开启，至少 {rules.Minimum} 行正文（{(inherited ? "继承全局" : "本书独立")}）；" : "数字章节关闭；")
            + (rules.Hierarchy ? "卷 / 章 / 节分层开启。" : "卷 / 章 / 节分层关闭，标题仍可作为普通章节识别。")
            + (RecognitionRulesChanged() ? "\n设置已改变，当前树尚未重新识别。" : "\n输出使用当前章节树，手动编辑优先；设置不会自动覆盖它。");
        WorkflowHintText.Text = _sourceChanged ? "下一步：原文已变化，请先刷新原文。"
            : RecognitionRulesChanged() ? "规则已调整：当前树保持不变。需要套用新规则时，在“识别设置”中重新识别。"
            : "先查看待核对问题 → 预览目录修复；来源或匹配不准确时手动核对 → 应用并返回。";
    }

    private ChapterRuleState CaptureRules() => new(ChapterPatternText.Text, NumericHeadingsCheck.IsChecked == true,
        NumericMinimumLinesText.Text, _numericPattern, HierarchyEnabledCheck.IsChecked == true,
        Level1PatternText.Text, Level2PatternText.Text, Level3PatternText.Text,
        IncludeHtmlTocPageCheck.IsChecked == true, IncludeChapterTopNavigationCheck.IsChecked == true,
        InheritNumericDefaults, GlobalNumericDefaults, NumericPresets.ToArray(), _headingNumberCorrections);

    private void ApplyRules(ChapterRuleState rules, bool includeShared = false)
    {
        ChapterPatternText.Text = rules.Pattern;
        NumericHeadingsCheck.IsChecked = rules.Numeric;
        NumericMinimumLinesText.Text = rules.Minimum;
        _numericPattern = rules.NumericPattern;
        _headingNumberCorrections = rules.Corrections;
        HierarchyEnabledCheck.IsChecked = rules.Hierarchy;
        Level1PatternText.Text = rules.Level1; Level2PatternText.Text = rules.Level2; Level3PatternText.Text = rules.Level3;
        IncludeHtmlTocPageCheck.IsChecked = rules.HtmlToc; IncludeChapterTopNavigationCheck.IsChecked = rules.TopNavigation;
        InheritNumericDefaults = rules.Inherit;
        if (includeShared) { GlobalNumericDefaults = rules.Global; NumericPresets = rules.Presets; }
    }

    private static string RecognitionSignature(ChapterRuleState rules) => JsonSerializer.Serialize(new
    { rules.Pattern, rules.Numeric, rules.Minimum, rules.NumericPattern, rules.Hierarchy, rules.Level1, rules.Level2, rules.Level3, rules.Corrections });

    private bool RecognitionRulesChanged() => RecognitionSignature(CaptureRules()) != RecognitionSignature(_recognitionState);

    private void ValidateRules()
    {
        _ = ReadHierarchyOptions();
        NumericHeadingRule.Compile(_numericPattern);
        HeadingTypoRules.Parse(_headingNumberCorrections);
        foreach (var expression in new[] { ChapterPatternText.Text, Level1PatternText.Text, Level2PatternText.Text, Level3PatternText.Text })
            if (!string.IsNullOrWhiteSpace(expression)) _ = new Regex(expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
    }

    private void RecognitionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_rulesDialog is not null) return;
        var before = CaptureRules();
        NumericScopeText.Text = InheritNumericDefaults ? "本书继承全局数字规则；直接修改仅本书生效。" : "本书独立数字规则，不影响其他书。";
        UpdateRuleGuidance();
        var dialog = ThemedWindow("识别设置", 740, 690);
        dialog.MinWidth = 560; dialog.MinHeight = 400;
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        RulesStorage.Child = null;
        var scroll = new ScrollViewer { Content = RecognitionRulesPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        grid.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(16) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "保留设置", IsDefault = true };
        apply.Click += (_, _) =>
        {
            try { ValidateRules(); dialog.DialogResult = true; }
            catch (Exception error) { InkDialog.Show(dialog, error.Message, "识别设置有误"); }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); Grid.SetRow(buttons, 1); grid.Children.Add(buttons);
        dialog.Content = grid;
        _rulesDialog = dialog;
        try { if (dialog.ShowDialog() != true) ApplyRules(before, includeShared: true); }
        finally { scroll.Content = null; RulesStorage.Child = RecognitionRulesPanel; _rulesDialog = null; }
        UpdateSaveState();
        if (RecognitionRulesChanged()) ShowReviewFeedback("规则已调整，当前树尚未重新识别；可继续保留手调树，或在识别设置中重新识别。");
    }

    internal void ApplyRebuiltDocument(ChapterTreeDocument replacement)
    {
        var changedSource = _document.SourceSha256 != replacement.SourceSha256;
        _sourceChanged = false;
        if (changedSource) { _undo.Clear(); _redo.Clear(); _confirmedGroups.Clear(); }
        Mutate(() =>
        {
            _document = replacement;
            _recognitionState = CaptureRules();
            Roots.Clear();
            foreach (var root in BuildTree(replacement.Entries)) Roots.Add(root);
            _selectedNode = null;
            SetOperationSelection([]);
        });
        if (changedSource) { _undo.Clear(); _redo.Clear(); }
        _detectUnrecognized = true;
        RefreshSelectedLines(); UpdateSummary(); UpdateActionButtons(); UpdateUndoRedoButtons();
        SetReviewResult(changedSource ? "已按新 TXT 重新识别，旧行号历史已清除。" : "已重新识别；同一原文版本可撤销恢复旧章节树。");
        if (_rulesDialog is not null) _rulesDialog.DialogResult = true;
    }

    internal async Task RecognizeSuggestedNumericChaptersAsync(bool normalizeTitles = true)
    {
        if (_sourceChanged || ChapterReviewAnalyzer.FindNumericChapters(_document, Flatten().Select(n => n.ToEntry()).ToArray()) is not { } suggestion) return;
        var previousRules = CaptureRules();
        try
        {
            IsEnabled = false;
            var options = ReadHierarchyOptions() with
            {
                RecognizeNumericHeadings = true,
                NumericHeadingPattern = suggestion.Pattern,
                NumericHeadingMinimumBodyLines = suggestion.MinimumBodyLines
            };
            var replacement = await ChapterTreeDocument.LoadAsync(_document.SourcePath,
                NormalizePattern(ChapterPatternText.Text), options, _encodingMode);
            if (replacement.SourceSha256 != _document.SourceSha256)
                throw new InvalidOperationException("原始 TXT 已变化，请重新打开或重新识别后再操作。");
            var normalizedCount = 0;
            if (normalizeTitles)
            {
                var normalizedEntries = replacement.Entries.Select(entry =>
                {
                    if (ChapterTitleNormalizer.TryNormalizeNumericTitle(entry.Title, out var title) && title != entry.Title)
                    {
                        normalizedCount++;
                        return entry with { Title = title };
                    }
                    return entry;
                }).ToArray();
                replacement = replacement.WithEntries(normalizedEntries);
            }
            NumericHeadingsCheck.IsChecked = true;
            NumericMinimumLinesText.Text = suggestion.MinimumBodyLines.ToString();
            _numericPattern = suggestion.Pattern;
            InheritNumericDefaults = false;
            ApplyRebuiltDocument(replacement);
            var first = replacement.Entries.FirstOrDefault(e => !e.IsFrontMatter)?.TitleLineNumber;
            ReviewOnlyCheck.IsChecked = false; AllChaptersRadio.IsChecked = true;
            UpdateReviewVisibility();
            if (first is int line) NavigateToSourceLine(line);
            SetReviewResult(normalizedCount > 0
                ? $"已识别 {replacement.Entries.Count(e => !e.IsFrontMatter)} 个章节，并规范化 {normalizedCount} 个成品标题；仅应用于本书，全局设置及原始 TXT 不变，可撤销。"
                : $"已识别 {replacement.Entries.Count(e => !e.IsFrontMatter)} 个章节；未发现可规范化标题，仅应用于本书，全局设置及原始 TXT 不变，可撤销。");
        }
        catch (Exception ex)
        {
            ApplyRules(previousRules, includeShared: true);
            ShowReviewFeedback("未完成识别：" + ex.Message);
        }
        finally { IsEnabled = true; UpdateActionButtons(); UpdateSaveState(); }
    }

    internal async Task CheckSourceVersionAsync()
    {
        if (_checkingSource || _document is null || !IsLoaded) return;
        _checkingSource = true;
        try
        {
            var document = _document;
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(document.SourcePath)));
            if (!ReferenceEquals(document, _document)) return;
            _sourceChanged = hash != document.SourceSha256;
            if (_sourceChanged) ShowReviewFeedback("原始 TXT 已保存修改。点击右侧“原文已变化 · 刷新”即可在当前工作台更新章节和检查结果。");
        }
        catch (Exception error) { _sourceChanged = true; ShowReviewFeedback("无法校验原始 TXT：" + error.Message); }
        finally { _checkingSource = false; UpdateSaveState(); UpdateActionButtons(); }
    }

    private Window ThemedWindow(string title, double width, double height)
    {
        var window = new Window { Owner = _rulesDialog ?? this, Title = title, Width = width, Height = height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        window.SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        return window;
    }

    private bool PreviewChanges(string title, IReadOnlyList<string> changes, string explanation)
    {
        if (ConfirmAction is not null) return ConfirmAction(explanation + "\n" + string.Join("\n", changes));
        var dialog = ThemedWindow(title, 660, 500);
        var panel = new DockPanel { Margin = new Thickness(16) };
        var heading = new TextBlock { Text = $"{explanation}\n\n将修改 {changes.Count} 项", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var apply = new Button { Content = "确认处理", IsDefault = true };
        apply.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(apply); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        panel.Children.Add(new ListBox { ItemsSource = changes }); dialog.Content = panel;
        return dialog.ShowDialog() == true;
    }

    private void WorkbenchMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        MenuItem Category(string title) { var item = new MenuItem { Header = title }; menu.Items.Add(item); return item; }
        void Add(MenuItem category, string title, RoutedEventHandler action, bool enabled = true)
        { var item = new MenuItem { Header = title, IsEnabled = enabled }; item.Click += action; category.Items.Add(item); }
        var all = Category("全书整理");
        Add(all, "重建编号分组与结构（预览）…", RecoverStructure_Click, !_sourceChanged);
        Add(all, "清理全书重复标题…", CleanDuplicateTitles_Click, !_sourceChanged);
        Add(all, "规范化全书数字标题…", NormalizeAll_Click, !_sourceChanged);
        Add(all, "批量修复跳章区间漏识别标题…", (_, _) => RepairMissingHeadings(null), !_sourceChanged);
        var toc = Category("目录（全书）");
        Add(toc, "全部加入目录", SelectAll_Click, !_sourceChanged);
        Add(toc, "全部不加入目录（保留正文）", (_, args) =>
        { if (ConfirmWorkbench("将全书所有章节设为不进入目录，正文仍然保留。是否继续？", "全书目录")) ClearAll_Click(this, args); }, !_sourceChanged);
        var view = Category("视图");
        Add(view, "展开全部章节", (_, _) => { foreach (var node in Flatten()) node.IsExpanded = true; });
        Add(view, "折叠全部章节", (_, _) =>
        {
            foreach (var node in Flatten()) node.IsExpanded = false;
            ClearHiddenSelection();
        });
        Add(view, "查看 / 恢复本次已确认的提醒…", (_, _) => ShowConfirmedGroups(), _confirmedGroups.Count > 0);
        Add(view, "恢复本次已确认的提醒", (_, _) => RestoreConfirmedGroups(), _confirmedGroups.Count > 0);
        Add(view, "清除搜索与筛选", (_, args) => { ChapterSearchText.Text = ""; IssueCategoryCombo.SelectedIndex = 0; AllChapters_Click(this, args); ApplySearchFilter(); });
        menu.Items.Add(new Separator());
        var copy = new MenuItem { Header = "复制原始文件路径" }; copy.Click += (_, _) => Clipboard.SetText(_document.SourcePath); menu.Items.Add(copy);
        var editor = new MenuItem { Header = "文本编辑器设置…" }; editor.Click += (_, _) => ChooseTextEditor(); menu.Items.Add(editor);
        menu.PlacementTarget = WorkbenchMoreButton; WorkbenchMoreButton.ContextMenu = menu; menu.IsOpen = true;
    }

    internal void RestoreConfirmedGroups()
    {
        _confirmedGroups.Clear(); ClearReviewResult();
        RefreshSuggestions(Flatten().Select(n => n.ToEntry()).ToArray());
        ShowReviewFeedback("已恢复本次确认过的提醒。");
    }

    private void ShowConfirmedGroups()
    {
        var dialog = ThemedWindow("本次确认正常的提醒", 650, 430);
        var panel = new DockPanel { Margin = new Thickness(16) };
        var list = new ListBox { ItemsSource = _confirmedGroups.ToArray(), DisplayMemberPath = "Value.Issue.Message" };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var restore = new Button { Content = "恢复选中提醒" };
        restore.Click += (_, _) =>
        {
            if (list.SelectedItem is not KeyValuePair<string, ChapterReviewGroup> item) return;
            _confirmedGroups.Remove(item.Key); list.ItemsSource = _confirmedGroups.ToArray();
            ClearReviewResult(); RefreshSuggestions(Flatten().Select(n => n.ToEntry()).ToArray());
        };
        buttons.Children.Add(restore); buttons.Children.Add(new Button { Content = "关闭", IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons); panel.Children.Add(list); dialog.Content = panel; dialog.ShowDialog();
    }

    private void ChooseTextEditor()
    {
        var dialog = new OpenFileDialog { Title = "选择文本编辑器（如 Notepad3.exe）", Filter = "应用程序 (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        TextEditorPath = dialog.FileName;
        EditSourceButton.ToolTip = "使用 " + Path.GetFileNameWithoutExtension(TextEditorPath) + "；应用并返回后保存编辑器配置";
        ShowReviewFeedback("编辑器已选择：" + Path.GetFileName(TextEditorPath)); UpdateSaveState();
    }

    private void AllChapters_Click(object sender, RoutedEventArgs e)
    { ReviewOnlyCheck.IsChecked = false; AllChaptersRadio.IsChecked = true; ClearReviewResult(); UpdateReviewVisibility(userChange: true); UpdateActionButtons(); }
    private void ChapterSearch_Changed(object sender, TextChangedEventArgs e) { _searchTimer?.Stop(); _searchTimer?.Start(); }
    internal void ApplySearchFilter()
    {
        _searchQuery = ChapterSearchText.Text.Trim(); ClearReviewResult();
        _refreshingSuggestions = true;
        try { ChapterSuggestionsCombo.ItemsSource = FilteredReviewIssues(); ChapterSuggestionsCombo.SelectedIndex = -1; _nextSuggestionIndex = 0; }
        finally { _refreshingSuggestions = false; }
        UpdateReviewVisibility(userChange: true); UpdateSuggestionButtons(); UpdateActionButtons();
    }
    private void ChapterSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        _searchTimer?.Stop(); ApplySearchFilter();
        var match = Regex.Match(_searchQuery, @"^行\s*[:：]\s*(\d+)\s*$", RegexOptions.None, TimeSpan.FromMilliseconds(200));
        if (match.Success && int.TryParse(match.Groups[1].Value, out var line)) NavigateToSourceLine(line);
        else if (Flatten().FirstOrDefault(MatchesSearch) is { } first) SelectChapterForOperation(first, ModifierKeys.None);
        e.Handled = true;
    }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { ChapterSearchText.Text = ""; _searchTimer?.Stop(); ApplySearchFilter(); }
    private void ClearSelection_Click(object sender, RoutedEventArgs e) { SetOperationSelection([]); _selectedNode = null; _selectionAnchor = null; ClearReviewResult(); RefreshSelectedLines(); UpdateActionButtons(); }
    private void ClearHiddenSelection()
    {
        var selected = OperationSelection();
        var kept = selected.Where(n => n.IsReviewVisible && AncestorsExpanded(n)).ToArray();
        if (kept.Length == selected.Length) return;
        SetOperationSelection(kept); if (_selectedNode is not null && !kept.Contains(_selectedNode)) _selectedNode = kept.FirstOrDefault();
        ShowReviewFeedback($"已取消 {selected.Length - kept.Length} 个隐藏项的选择。"); RefreshSelectedLines(); UpdateActionButtons();
    }
    private void ChapterNode_Collapsed(object sender, RoutedEventArgs e) { if (IsLoaded && !_trackingPaused && !_refreshingTreeView) ClearHiddenSelection(); }
    private void ChapterTree_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ChapterTreeNode node) return;
        if (!node.IsOperationSelected) SelectChapterForOperation(node, ModifierKeys.None);
        ReviewMore_Click(item, new RoutedEventArgs()); e.Handled = true;
    }
    private void ChapterTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton) return;
        var visible = Flatten().Where(n => n.IsReviewVisible && AncestorsExpanded(n)).ToArray();
        if (visible.Length == 0) return;
        var index = _selectedNode is null ? -1 : Array.IndexOf(visible, _selectedNode);
        index = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, visible.Length - 1);
        SelectChapterForOperation(visible[index], Keyboard.Modifiers); e.Handled = true;
    }
    private void EditCurrentTitle_Click(object sender, RoutedEventArgs e) { if (!_sourceChanged && OperationSelection().Length == 1 && _selectedNode is not null) EditTitle(_selectedNode); }
    private void HeadingCorrections_Click(object sender, RoutedEventArgs e)
    {
        var current = ReadHierarchyOptions();
        var dialog = new NumericHeadingRulesWindow(current, GlobalNumericDefaults, NumericPresets,
            _headingNumberCorrections == GlobalNumericDefaults.HeadingNumberCorrections, correctionsOnly: true) { Owner = _rulesDialog ?? this };
        if (dialog.ShowDialog() != true) return;
        NumericPresets = dialog.Presets;
        _headingNumberCorrections = dialog.Result.HeadingNumberCorrections;
        if (dialog.Scope == 1) GlobalNumericDefaults = GlobalNumericDefaults with { HeadingNumberCorrections = _headingNumberCorrections };
        UpdateSaveState();
    }

    internal void RestoreContainerToBody(bool keepHeading = false)
    {
        if (_sourceChanged || _selectedNode is not { IsFrontMatter: false } node || OperationSelection().Length != 1) return;
        var siblings = Siblings(node);
        var index = siblings.IndexOf(node);
        // Continue the preceding container where one exists; lifting everything to root
        // would split a real volume in two and change numbering scope after a false boundary.
        var destination = node.Children.Count > 0 && index > 0 && !siblings[index - 1].IsFrontMatter
            && siblings[index - 1].Children.Count > 0 ? siblings[index - 1] : node.Parent;
        var children = node.Children.ToArray();
        var placement = destination is null ? "顶层目录" : $"「{destination.Title}」下";
        if (!ConfirmWorkbench((keepHeading ? "保留本标题和正文，改为普通章节。" : "将本标题和正文还原到前一节正文。")
            + $"\n原有 {children.Length} 个直属子章节按当前阅读顺序接续到{placement}，各自的子章节保持从属关系。\n后面的其他卷不变，操作可撤销。",
            keepHeading ? "取消父级身份" : "移除误识别章节身份")) return;
        var previous = Flatten().TakeWhile(n => n != node).LastOrDefault();
        Mutate(() =>
        {
            if (!keepHeading && previous is null)
            {
                previous = new ChapterTreeNode(new ChapterTreeEntry(Guid.NewGuid().ToString("N"), "序", 1, true, null, WithOriginalTitle(node).ToArray()) { IsFrontMatter = true });
                Roots.Insert(0, previous);
            }
            else if (!keepHeading) { previous!.ContentRanges = previous.ContentRanges.Concat(WithOriginalTitle(node)).ToArray(); previous.NotifyLineCount(); }
            // Recompute after a possible front-matter insertion.
            var insertion = siblings.IndexOf(node);
            siblings.Remove(node);
            var target = destination?.Children ?? Roots;
            if (!ReferenceEquals(destination, node.Parent)) insertion = target.Count;
            foreach (var child in children) node.Children.Remove(child);
            if (keepHeading)
            {
                node.Parent = destination;
                node.Level = destination is null ? 1 : destination.Level + 1;
                target.Insert(insertion++, node);
            }
            foreach (var child in children)
            {
                child.Parent = destination;
                SetLevelRecursive(child, destination is null ? 1 : destination.Level + 1);
                target.Insert(insertion++, child);
            }
            if (destination is not null) destination.IsExpanded = true;
            _selectedNode = keepHeading ? node : children.FirstOrDefault() ?? previous;
            SetOperationSelection(_selectedNode is null ? [] : [_selectedNode]);
        });
        ChapterSearchText.Text = ""; IssueCategoryCombo.SelectedIndex = 0;
        ApplySearchFilter();
        AllChapters_Click(this, new RoutedEventArgs());
        SelectRestoredNode();
        RefreshSelectedLines(); UpdateSummary(); UpdateActionButtons();
        SetReviewResult((keepHeading ? "已保留为普通章节。" : "已还原为前文正文。")
            + $"原子章节已按原顺序接续到{placement}；已显示全部章节便于核对，可撤销。");
    }
    private void ResetChapterPattern_Click(object sender, RoutedEventArgs e) => ChapterPatternText.Text = "";
    private void EditSource_Click(object sender, RoutedEventArgs e)
    { if (_selectedNode is not null && !_sourceChanged) EditOriginal_Click(new Button { DataContext = _selectedNode }, e); }

    private void RepairMissingHeadings(int? nextLine)
    {
        if (_sourceChanged) return;
        var entries = Flatten().Select(n => n.ToEntry()).ToArray();
        var allCandidates = MissingChapterHeadings.Find(_document, entries);
        var boundary = nextLine is null ? null : allCandidates.FirstOrDefault(c => c.Line == nextLine)?.NextLine;
        var candidates = allCandidates.Where(c => nextLine is null || c.NextLine == boundary).ToArray();
        if (candidates.Length == 0) { ShowReviewFeedback("没有可明确修复的候选；请继续核对跳章区间原文。"); return; }
        var dialog = ThemedWindow("核对漏识别标题", 800, 550);
        var panel = new DockPanel { Margin = new Thickness(16) };
        var submit = new Button { Content = "修复勾选项", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(submit, Dock.Bottom); panel.Children.Add(submit);
        var hint = new TextBlock { Text = "补建章节边界并修改成品标题；原始 TXT 不变。未找到的缺章仍会提醒，整批可撤销。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var rows = candidates.Select(c => new CheckBox { IsChecked = true, Tag = c.Line, Margin = new Thickness(0, 6, 0, 6),
            Content = new TextBlock { Text = $"第 {c.Line} 行：{c.Original}\n→ {c.Title}", TextWrapping = TextWrapping.Wrap } }).ToArray();
        var list = new StackPanel(); foreach (var row in rows) list.Children.Add(row);
        panel.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dialog.Content = panel;
        submit.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return;
        var selected = rows.Where(r => r.IsChecked == true).Select(r => (int)r.Tag).ToArray();
        if (selected.Length == 0) return;
        var repaired = MissingChapterHeadings.Repair(_document, entries, selected);
        Mutate(() => { Roots.Clear(); foreach (var root in BuildTree(repaired)) Roots.Add(root); _selectedNode = null; SetOperationSelection([]); });
        RefreshSelectedLines(); UpdateSummary(); UpdateActionButtons();
        SetReviewResult($"已补建 {repaired.Count - entries.Length} 个章节并规范标题；未找到的缺章仍会提醒，可撤销。");
    }
    private void CopySource_Click(object sender, RoutedEventArgs e)
    { if (SourceLinesList.SelectedItem is ChapterTreeSourceLine line) Clipboard.SetText(line.Text); else ShowReviewFeedback("请先选择要复制的原文行。"); }
    private void WrapSource_Click(object sender, RoutedEventArgs e) => ScrollViewer.SetHorizontalScrollBarVisibility(SourceLinesList, WrapSourceCheck.IsChecked == true ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
    private void ExpandSource_Click(object sender, RoutedEventArgs e)
    { _expandSource = true; RefreshSelectedLines(); }
}

internal sealed record ChapterRuleState(string Pattern, bool Numeric, string Minimum, string NumericPattern,
    bool Hierarchy, string Level1, string Level2, string Level3, bool HtmlToc, bool TopNavigation,
    bool Inherit, TocHierarchyOptions Global, NamedNumericHeadingPreset[] Presets, string Corrections);
