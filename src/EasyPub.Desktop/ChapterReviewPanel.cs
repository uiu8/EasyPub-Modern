using System.Windows;
using System.Windows.Controls;
using System.Text.RegularExpressions;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class ChapterEditorWindow
{
    private ConversionPreflightIssue[] _allReviewIssues = [];
    private ChapterReviewGroup[] _reviewGroups = [];
    private readonly Dictionary<(string Code, int? Line), ChapterReviewGroup> _reviewGroupIndex = [];
    private readonly Dictionary<string, ChapterReviewGroup> _confirmedGroups = [];
    private int _reviewLimit = 200;
    private int _totalReviewGroups;
    private bool _refreshingLocations;
    private string? _resultMessage;
    private int _resultUndoDepth;
    private bool _expandSource;
    private string _searchQuery = "";

    private ChapterReviewGroup? ReviewGroup(ConversionPreflightIssue? issue) => issue is null ? null
        : _reviewGroupIndex.TryGetValue((issue.Code, issue.LineNumber), out var group)
            ? group
            : _reviewGroups.FirstOrDefault(g => g.Issue.Code == issue.Code && g.Issue.LineNumber == issue.LineNumber);

    private string ReviewKey(ChapterReviewGroup group) => _document.SourceSha256 + "|" + group.Issue.Code + "|"
        + string.Join(",", group.Lines) + "|" + group.Issue.Message + "|"
        + string.Join(";", Flatten().Where(n => group.NodeIds.Contains(n.Id)).Select(n =>
            n.Id + ":" + n.Title + ":" + n.Level + ":" + string.Join(",", n.ContentRanges.Select(r => r.StartLine + "-" + r.EndLine))));

    private string ReviewCategory(ConversionPreflightIssue issue) => ReviewGroup(issue)?.Category ?? "其他提醒";

    private ChapterTreeNode[] CurrentNumericGroup(ConversionPreflightIssue? issue) =>
        issue is { Code: "numeric_body_group" } && ReviewGroup(issue) is { } group
            ? Flatten().Where(n => group.NodeIds.Contains(n.Id)).ToArray() : [];

    private bool MatchesSearch(ChapterTreeNode node)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery)) return true;
        var lineMatch = Regex.Match(_searchQuery, @"^行\s*[:：]\s*(\d+)\s*$", RegexOptions.None, TimeSpan.FromMilliseconds(200));
        if (lineMatch.Success && int.TryParse(lineMatch.Groups[1].Value, out var line))
            return node.TitleLineNumber == line || node.ContentRanges.Any(r => line >= r.StartLine && line <= r.EndLine);
        return node.Title.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase)
            || (_document.SourceLine(node.TitleLineNumber ?? 0)?.Text.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private ConversionPreflightIssue[] FilteredReviewIssues()
    {
        var category = (IssueCategoryCombo.SelectedItem as ComboBoxItem)?.Content as string;
        var categoryMatches = _allReviewIssues.Where(i => category is null or "全部问题" || ReviewCategory(i) == category);
        if (string.IsNullOrWhiteSpace(_searchQuery)) return categoryMatches.ToArray();
        var nodes = Flatten().Where(MatchesSearch).Select(n => n.Id).ToHashSet();
        return categoryMatches.Where(i => ReviewGroup(i)?.NodeIds.Any(nodes.Contains) == true).ToArray();
    }

    private void IssueCategory_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_document is null || ChapterSuggestionsCombo is null) return;
        ClearReviewResult();
        RefreshSuggestions(Flatten().Select(n => n.ToEntry()).ToArray());
        UpdateReviewVisibility(userChange: true);
        UpdateActionButtons();
    }

    private void ReviewFilter_Click(object sender, RoutedEventArgs e)
    {
        AllChaptersRadio.IsChecked = ReviewOnlyCheck.IsChecked != true;
        ClearReviewResult();
        UpdateReviewVisibility(userChange: true);
        UpdateActionButtons();
    }

    private void UpdateReviewVisibility(bool userChange = false)
    {
        if (_document is null || Roots is null) return;
        var nodes = Flatten().ToArray();
        var nodesById = nodes.ToDictionary(node => node.Id);
        var relevant = FilteredReviewIssues();
        var shown = new HashSet<ChapterTreeNode>();
        var reviewOnly = ReviewOnlyCheck.IsChecked == true;
        if (!reviewOnly)
            foreach (var node in string.IsNullOrWhiteSpace(_searchQuery) ? nodes : nodes.Where(MatchesSearch)) shown.Add(node);
        else foreach (var issue in relevant)
        {
            var group = ReviewGroup(issue);
            if (group is null) continue;
            // Resolve group members by id instead of scanning the entire tree for every issue.
            foreach (var id in group.NodeIds)
            {
                if (!nodesById.TryGetValue(id, out var member)) continue;
                shown.Add(member);
                // Include real neighbors of the entire group, not just its first member.
                var siblings = Siblings(member);
                var index = siblings.IndexOf(member);
                if (index > 0) shown.Add(siblings[index - 1]);
                if (index + 1 < siblings.Count) shown.Add(siblings[index + 1]);
            }
        }
        if (!userChange && _resultMessage is not null && _selectedNode is not null) shown.Add(_selectedNode);
        foreach (var node in shown.ToArray())
            for (var parent = node.Parent; parent is not null; parent = parent.Parent) shown.Add(parent);
        foreach (var node in nodes) node.IsReviewVisible = shown.Contains(node);
        if (userChange)
        {
            var selection = OperationSelection();
            var remaining = selection.Where(n => n.IsReviewVisible && AncestorsExpanded(n)).ToArray();
            if (remaining.Length != selection.Length)
            {
                SetOperationSelection(remaining);
                if (_selectedNode is not null && !remaining.Contains(_selectedNode)) _selectedNode = remaining.FirstOrDefault();
                ShowReviewFeedback($"已取消 {selection.Length - remaining.Length} 个隐藏项的选择，不会对隐藏项执行操作。");
                RefreshSelectedLines();
            }
        }
        var confirmedKeys = _confirmedGroups.Count == 0 ? null : _confirmedGroups.Keys.ToHashSet();
        var marked = _reviewGroups.Where(g => confirmedKeys is null || !confirmedKeys.Contains(ReviewKey(g)))
            .SelectMany(g => g.NodeIds.Select(id => (id, g.Category))).GroupBy(x => x.id).ToDictionary(g => g.Key, g => string.Join(" · ", g.Select(x => x.Category).Distinct()));
        foreach (var node in nodes) node.ReviewDescription = marked.GetValueOrDefault(node.Id, "");
        var matches = string.IsNullOrWhiteSpace(_searchQuery) ? nodes.Length : nodes.Count(MatchesSearch);
        FilterSummaryText.Text = reviewOnly
            ? $"当前 {relevant.Length} 组 / 已加载 {_allReviewIssues.Length} 组 · 提醒不一定是错误"
            : string.IsNullOrWhiteSpace(_searchQuery) ? "搜索标题，或输入 行:51397 · 勾选框仅控制目录" : $"匹配 {matches} 章（含原始标题）；祖先和上下文不计入匹配数";
    }

    private ConversionPreflightIssue? CurrentReviewIssue()
    {
        if (_resultMessage is not null) return null;
        bool Belongs(ConversionPreflightIssue issue) => _selectedNode is not null &&
            (ReviewGroup(issue)?.NodeIds.Contains(_selectedNode.Id) == true
             || issue.LineNumber is int line && _selectedNode.ContentRanges.Any(r => line >= r.StartLine && line <= r.EndLine));
        if (ChapterSuggestionsCombo.SelectedItem is ConversionPreflightIssue current && Belongs(current)) return current;
        return _allReviewIssues.FirstOrDefault(Belongs);
    }

    private void RefreshReviewGroupPreview()
    {
        // One source view: group positions navigate it; do not duplicate the whole group in another preview.
        var group = ReviewGroup(CurrentReviewIssue());
        _refreshingLocations = true;
        try
        {
            GroupLocationsCombo.ItemsSource = group?.Lines.Select(line =>
                new ReviewLocation(line, $"原文第 {line} 行 · {_document.SourceLine(line)?.Text.Trim()}")).ToArray() ?? [];
            GroupLocationsCombo.SelectedItem = GroupLocationsCombo.Items.Cast<ReviewLocation>().FirstOrDefault(p => p.Line == _selectedNode?.TitleLineNumber);
        }
        finally { _refreshingLocations = false; }
        GroupLocationsCombo.IsEnabled = group is not null && group.Lines.Count > 0;
    }

    private void UpdateReviewCard()
    {
        if (ReviewHeading is null || _document is null) return;
        var selected = OperationSelection();
        var count = selected.Length;
        var issue = CurrentReviewIssue();
        var category = issue is null ? null : ReviewCategory(issue);
        var group = ReviewGroup(issue);
        ConfirmNormalButton.IsEnabled = issue is not null && count <= 1;
        ConfirmNormalButton.Visibility = issue is not null && count <= 1 ? Visibility.Visible : Visibility.Collapsed;
        ReviewActionButton.Visibility = count == 0 || _resultMessage is not null ? Visibility.Collapsed : Visibility.Visible;
        RecognizeNumericKeepButton.Visibility = Visibility.Collapsed;
        RecognizeNumericKeepButton.IsEnabled = false;
        ReviewMoreButton.IsEnabled = count > 0 && !_sourceChanged;
        EditSourceButton.IsEnabled = _selectedNode is not null && !_sourceChanged;
        EditTitleButton.IsEnabled = count == 1 && !_sourceChanged;
        UndoResultButton.Visibility = _resultMessage is not null && _undo.Count == _resultUndoDepth && _undo.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FinalTitleText.Visibility = Visibility.Collapsed;
        if (_selectedNode is { TitleLineNumber: int originalLine } node && _document.SourceLine(originalLine) is { } original && node.Title != original.Text.Trim())
        {
            FinalTitleText.Text = "成品标题：" + node.Title + "（原始标题见下方）";
            FinalTitleText.Visibility = Visibility.Visible;
        }
        OnlyCurrentIssueCheck.IsEnabled = group?.NodeIds.Count > 1;
        if (_resultMessage is not null)
        {
            ReviewHeading.Text = "本次处理结果";
            ReviewExplanation.Text = _resultMessage;
            ReviewDetailsText.Text = "原始 TXT 未被修改。核对下方原文后，可撤销或前往下一组。";
            ActionScopeText.Text = _selectedNode is null ? "" : "当前结果：" + _selectedNode.Title;
            return;
        }
        if (issue?.Code == "numeric_chapters_suspected")
        {
            ReviewHeading.Text = "发现疑似数字章节";
            ReviewExplanation.Text = issue.Message;
            ReviewDetailsText.Text = issue.Message + "\n展开涉及位置可逐处查看候选原文。";
            ReviewActionButton.Content = "识别并规范标题";
            ReviewActionButton.Visibility = Visibility.Visible;
            ReviewActionButton.IsEnabled = !_sourceChanged;
            RecognizeNumericKeepButton.Visibility = Visibility.Visible;
            RecognizeNumericKeepButton.IsEnabled = !_sourceChanged;
            ActionScopeText.Text = $"本书 · {group?.Lines.Count ?? 0} 处候选 · 可撤销 · 原始 TXT 不变";
            OnlyCurrentIssueCheck.IsEnabled = false;
            return;
        }
        ReviewHeading.Text = count > 1 ? $"已选 {count} 个章节" : category ?? (_selectedNode is null ? "选择章节，核对原文" : "编辑当前章节");
        var advice = category switch
        {
            "疑似正文误识别" => "若是正文列举，可还原正文；原始数字条目及正文会完整保留。",
            "重复标题" => "先预览再清理。默认仅清理相邻同名、正文 0 行的重复标题。",
            "编号异常" => "核对前后章号；跳号可能是漏识别，分卷也可能重新编号。",
            "卷与层级" => "提醒不自动改层级；核对原文后选择设为子章节或移出父章节。",
            "疑似漏识别" => "确认属于标题后，选择原文行建立章节；不要把普通正文再次拆成章节。",
            _ => "编辑成品标题不改原始 TXT。Ctrl 多选，Shift 连选；目录勾选与操作选择独立。"
        };
        ReviewExplanation.Text = count > 1 ? "按真实兄弟顺序分段归入上一章；原始标题行与正文全部保留。" : advice;
        ReviewDetailsText.Text = (issue?.Message ?? advice) + (group is null ? "" : $"\n涉及 {group.Lines.Count} 处 · {Math.Max(1, group.RelatedIssues.Count)} 条关联提醒");
        ReviewExplanation.ToolTip = ReviewDetailsText.Text;
        var numericGroup = CurrentNumericGroup(issue);
        var targets = count == 1 && OnlyCurrentIssueCheck.IsChecked != true && numericGroup.Length > 1 ? numericGroup : selected;
        var restore = count > 1 || category == "疑似正文误识别";
        ReviewActionButton.Content = restore ? targets.Length > 1 ? $"还原{(count > 1 ? "已选" : "本组")} {targets.Length} 处为正文" : "还原为上一章正文"
            : category == "重复标题" ? "检查并清理本组…"
            : category == "疑似漏识别" ? "从选中行建立章节"
            : SuggestedTitle() is { } title ? "修改为：" + title
            : category == "卷与层级" ? "核对层级处理…" : "编辑成品标题…";
        var plan = PlanBatchAction(targets, merge: true);
        ReviewActionButton.IsEnabled = !_sourceChanged && count > 0 && (restore ? plan.Any(p => p.Reason is null)
            : category != "疑似漏识别" || CanSplitSelectedLine());
        ActionScopeText.Text = restore ? DescribeBatchPlan(plan) : count == 1 ? $"当前章节：{_selectedNode?.Title}" : "请选择左侧章节。";
        if (_sourceChanged) ActionScopeText.Text = "原始 TXT 已变化，请先重新识别；当前行号不能继续编辑。";
    }

    private async void ReviewAction_Click(object sender, RoutedEventArgs e)
    {
        if (!ReviewActionButton.IsEnabled || _sourceChanged) return;
        var issue = CurrentReviewIssue();
        if (issue?.Code == "numeric_chapters_suspected")
        {
            await RecognizeSuggestedNumericChaptersAsync(normalizeTitles: true);
            return;
        }
        var category = issue is null ? null : ReviewCategory(issue);
        var before = _undo.Count;
        var group = OperationSelection().Length == 1 && OnlyCurrentIssueCheck.IsChecked != true ? CurrentNumericGroup(issue) : [];
        if (group.Length > 1) SetOperationSelection(group);
        if (OperationSelection().Length > 1 || category == "疑似正文误识别") Merge_Click(sender, e);
        else if (category == "重复标题") CleanDuplicateTitles(ReviewGroup(issue)?.NodeIds.ToHashSet());
        else if (category == "卷与层级") { ReviewMore_Click(sender, e); return; }
        else if (category == "疑似漏识别") Split_Click(sender, e);
        else if (SuggestedTitle() is not null) CorrectNumber_Click(sender, e);
        else if (_selectedNode is not null) EditTitle(_selectedNode);
        if (_undo.Count > before && _resultMessage is null) SetReviewResult("修改已完成，原始 TXT 不变。核对下方原文后再前往下一组。");
        UpdateReviewVisibility();
        UpdateActionButtons();
    }

    private async void RecognizeNumericKeep_Click(object sender, RoutedEventArgs e)
    {
        if (_sourceChanged || !RecognizeNumericKeepButton.IsEnabled) return;
        await RecognizeSuggestedNumericChaptersAsync(normalizeTitles: false);
    }

    private void ShowReviewFeedback(string message)
    {
        ReviewFeedback.Text = message;
        ReviewFeedback.Visibility = Visibility.Visible;
    }

    private void SetReviewResult(string message)
    {
        _resultMessage = message;
        _resultUndoDepth = _undo.Count;
        ShowReviewFeedback(message);
        UpdateReviewCard();
    }

    private void ClearReviewResult()
    {
        _resultMessage = null;
        if (ReviewFeedback is not null) ReviewFeedback.Visibility = Visibility.Collapsed;
        if (UndoResultButton is not null) UndoResultButton.Visibility = Visibility.Collapsed;
    }

    private void ConfirmNormal_Click(object sender, RoutedEventArgs e)
    {
        if (OperationSelection().Length > 1 || ReviewGroup(CurrentReviewIssue()) is not { } group) return;
        _confirmedGroups[ReviewKey(group)] = group;
        RefreshSuggestions(Flatten().Select(n => n.ToEntry()).ToArray());
        SetReviewResult("已确认本组正常，仅本次窗口有效；更多 → 视图可查看和恢复。");
        _resultUndoDepth = -1;
        UndoResultButton.Visibility = Visibility.Collapsed;
        UpdateReviewVisibility();
    }

    private void ReviewMore_Click(object sender, RoutedEventArgs e)
    {
        UpdateActionButtons();
        ChapterActionsPopup.PlacementTarget = sender as UIElement ?? ReviewMoreButton;
        ChapterActionsPopup.IsOpen = true;
    }

    private void GroupLocation_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingLocations || GroupLocationsCombo.SelectedItem is not ReviewLocation position) return;
        var issue = ChapterSuggestionsCombo.SelectedItem;
        _refreshingSuggestions = true;
        try { NavigateToSourceLine(position.Line); ChapterSuggestionsCombo.SelectedItem = issue; }
        finally { _refreshingSuggestions = false; }
        UpdateReviewCard();
    }

    private void SelectReviewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ReviewGroup(CurrentReviewIssue()) is not { } group) return;
        var nodes = Flatten().Where(n => group.NodeIds.Contains(n.Id)).ToArray();
        SetOperationSelection(nodes);
        _selectedNode = nodes.FirstOrDefault();
        UpdateActionButtons();
    }

    private void OnlyCurrentIssue_Click(object sender, RoutedEventArgs e) => UpdateReviewCard();
    private void LoadMoreIssues_Click(object sender, RoutedEventArgs e)
    {
        _reviewLimit += 200;
        RefreshSuggestions(Flatten().Select(n => n.ToEntry()).ToArray());
    }
}

internal sealed record ReviewLocation(int Line, string Label);
