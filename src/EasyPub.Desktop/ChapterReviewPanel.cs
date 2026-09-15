using System.Windows;
using System.Windows.Controls;
using System.Text.RegularExpressions;
using System.Windows.Threading;
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
    private ReferenceCatalog? _referenceCatalog;
    private bool _referenceCatalogLoaded;
    private bool _fetchingCatalog;
    private string? _breakpointSignature;

    private ChapterReviewGroup? ReviewGroup(ConversionPreflightIssue? issue) => issue is null ? null
        : _reviewGroupIndex.TryGetValue((issue.Code, issue.LineNumber), out var group)
            ? group
            : _reviewGroups.FirstOrDefault(g => g.Issue.Code == issue.Code && g.Issue.LineNumber == issue.LineNumber);

    private string ReviewKey(ChapterReviewGroup group) => _document.SourceSha256 + "|" + group.Issue.Code + "|"
        + string.Join(",", group.Lines) + "|" + group.Issue.Message + "|"
        + string.Join(";", Flatten().Where(n => group.NodeIds.Contains(n.Id)).Select(n =>
            n.Id + ":" + n.Title + ":" + n.Level + ":" + string.Join(",", n.ContentRanges.Select(r => r.StartLine + "-" + r.EndLine))));

    private string ReviewCategory(ConversionPreflightIssue issue) => ReviewGroup(issue)?.Category ?? ReviewCategories.Other;

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

    private void UpdateReviewVisibility(bool userChange = false)    {
        if (_document is null || Roots is null) return;
        var nodes = Flatten().ToArray();
        var nodesById = nodes.ToDictionary(node => node.Id);
        // Both lookups used to scan a whole group/sibling list for each issue.
        // A preorder traversal preserves sibling order, including nested volumes.
        var siblingPositions = new Dictionary<ChapterTreeNode, int>();
        var nextPosition = new Dictionary<string, int>();
        foreach (var node in nodes)
        {
            var parentKey = node.Parent?.Id ?? string.Empty;
            var position = nextPosition.GetValueOrDefault(parentKey);
            siblingPositions[node] = position;
            nextPosition[parentKey] = position + 1;
        }
        var groupsByIssue = _reviewGroups.GroupBy(group => group.Issue).ToDictionary(group => group.Key, group => group.First());
        var relevant = FilteredReviewIssues();
        var shown = new HashSet<ChapterTreeNode>();
        var reviewOnly = ReviewOnlyCheck.IsChecked == true;
        if (!reviewOnly)
            foreach (var node in string.IsNullOrWhiteSpace(_searchQuery) ? nodes : nodes.Where(MatchesSearch)) shown.Add(node);
        else foreach (var issue in relevant)
        {
            var group = groupsByIssue.GetValueOrDefault(issue);
            if (group is null) continue;
            // Resolve group members by id instead of scanning the entire tree for every issue.
            foreach (var id in group.NodeIds)
            {
                if (!nodesById.TryGetValue(id, out var member)) continue;
                shown.Add(member);
                // Include real neighbors of the entire group, not just its first member.
                var siblings = Siblings(member);
                var index = siblingPositions[member];
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
        RefreshFilteredTree();
        var confirmedKeys = _confirmedGroups.Count == 0 ? null : _confirmedGroups.Keys.ToHashSet();
        var marked = _reviewGroups.Where(g => confirmedKeys is null || !confirmedKeys.Contains(ReviewKey(g)))
            .SelectMany(g => g.NodeIds.Select(id => (id, g.Category))).GroupBy(x => x.id).ToDictionary(g => g.Key, g => string.Join(" · ", g.Select(x => x.Category).Distinct()));
        foreach (var node in nodes) node.ReviewDescription = marked.GetValueOrDefault(node.Id, "");
        var matches = string.IsNullOrWhiteSpace(_searchQuery) ? nodes.Length : nodes.Count(MatchesSearch);
        FilterSummaryText.Text = reviewOnly
            ? $"当前 {relevant.Length} 组 / 已加载 {_allReviewIssues.Length} 组 · 提醒不一定是错误"
            : string.IsNullOrWhiteSpace(_searchQuery) ? "搜索标题，或输入 行:51397 · 勾选框仅控制目录" : $"匹配 {matches} 章（含原始标题）；祖先和上下文不计入匹配数";
        ScheduleBreakpoints();
    }

    private bool _refreshingTreeView;
    private bool _breakpointsPending;

    private void RefreshFilteredTree()
    {
        // Filter data, not container Visibility: zero-height hidden rows make WPF
        // realize the entire expanded tree while trying to fill its viewport.
        _refreshingTreeView = true;
        try
        {
            foreach (var node in Flatten()) node.RefreshVisibleChildren();
            VisibleRoots.Synchronize(Roots);
            // Removing filtered items can change native selection. The workbench's
            // selection/highlight remains on the model. Do not force BringIntoView
            // here: measuring all preceding expanded volumes defeats virtualization.
            // Explicit chapter/suggestion navigation still uses SelectRestoredNode.
        }
        finally { _refreshingTreeView = false; }
    }

    /// <summary>
    /// The saved directory this book was reconciled against, when there is one. A book that has been
    /// through 一键修复 has one stored next to its source hash, and it is what lets the tree say
    /// "this is the chapter the official release has here" instead of only "some numbers are missing".
    /// </summary>
    private ReferenceCatalog? SavedReference()
    {
        if (_referenceCatalogLoaded) return _referenceCatalog;
        _referenceCatalogLoaded = true;
        var saved = ReferenceCatalogInput.Load(_document.SourceSha256);
        if (!string.IsNullOrWhiteSpace(saved?.Text)) _referenceCatalog = ReferenceCatalogInput.ParseText(saved.Text);
        return _referenceCatalog;
    }

    /// <summary>Same source hash, so the directory that was found for the old tree still belongs to this one.</summary>
    private void InvalidateBreakpoints()
    {
        _referenceCatalog = null;
        _referenceCatalogLoaded = false;
        _breakpointSignature = null;
        RefreshCatalogState();
    }

    /// <summary>
    /// The button that fetches the official directory is only useful while there is none, and the row it
    /// sits in must say which of the two states the book is in.
    /// </summary>
    private void RefreshCatalogState()
    {
        if (FetchCatalogButton is null) return;
        var catalog = SavedReference();
        FetchCatalogButton.Content = catalog is null
            ? "获取参考目录（联网）"
            : $"已获取目录（{catalog.Titles.Count} 章）";
        FetchCatalogButton.IsEnabled = catalog is null && !_fetchingCatalog;
        FetchCatalogButton.ToolTip = catalog is null
            ? "按书源顺序搜索本书的公开目录并保存。只有拿到目录，才能标出「这里本该有哪一章」；失败时可改用手动核对并粘贴网址。"
            : $"来源：{catalog.PageTitle}。目录只用于核对结构，不会修改原始 TXT。";
    }

    /// <summary>
    /// Fetches the directory on the user's explicit request — never on its own: discovery is a network call
    /// that takes anywhere from a moment to several seconds, and the app does not send one behind the user's
    /// back. Whatever comes back is saved for this source hash, so later sessions can name the chapters the
    /// release has and this file lacks without asking again.
    /// </summary>
    private async void FetchCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_fetchingCatalog || SavedReference() is not null) return;
        _fetchingCatalog = true;
        RefreshCatalogState();
        ShowReviewFeedback("正在按书源顺序搜索公开目录…");
        try
        {
            var url = await Task.Run(() => ReferenceCatalogInput.FindLocalBookUrl(_document.SourcePath));
            var query = string.IsNullOrWhiteSpace(url) ? ChapterAutoRepair.ExtractBookName(_document.SourcePath) : url;
            var preferred = await ChapterAutoRepair.LoadPreferredSourcesAsync(_document.SourcePath);
            var catalogs = await new ReferenceCatalogClient().DiscoverAsync(query, CancellationToken.None, preferred);
            if (ReferenceCatalogInput.Pick(catalogs) is not { } catalog)
            {
                ShowReviewFeedback("未获取到可用目录。可粘贴书籍网址／编号，或导入目录文字。");
                CatalogAssist_Click(this, new RoutedEventArgs());
                return;
            }
            ReferenceCatalogInput.Save(_document.SourceSha256, new CatalogPreferences(
                query,
                catalog.Source,
                string.Join(Environment.NewLine, catalog.Nodes.Select(node => node.Title)),
                _document.RecognitionOptions.NumericHeadingMinimumBodyLines));
            _referenceCatalog = catalog;
            _referenceCatalogLoaded = true;
            _breakpointSignature = null;
            ApplyBreakpoints();
            ShowReviewFeedback($"已获取目录：{catalog.PageTitle}（{catalog.Titles.Count} 章）。"
                + "章节树上已标出目录里有、源文件里找不到的章节。");
        }
        catch (Exception error)
        {
            ShowReviewFeedback("获取目录失败：" + error.Message + "。可点「手动核对目录…」粘贴网址或导入目录文字。");
        }
        finally
        {
            _fetchingCatalog = false;
            RefreshCatalogState();
        }
    }

    /// <summary>
    /// Marks each row that does not join up with the chapter before it, so a skipped stretch of numbers —
    /// or a chapter the official directory has and the text lacks — is visible exactly where it happens
    /// instead of only inside one summary sentence. Recomputing is deferred to the dispatcher because
    /// alignment against the directory is not free and a single edit calls this several times.
    /// </summary>
    private void ScheduleBreakpoints()
    {
        if (_breakpointsPending) return;
        _breakpointsPending = true;
        Dispatcher.BeginInvoke(() =>
        {
            _breakpointsPending = false;
            ApplyBreakpoints();
        }, DispatcherPriority.Background);
    }

    private void ApplyBreakpoints()
    {
        if (_document is null || Roots is null) return;
        var nodes = Flatten().ToArray();
        var signature = BreakpointSignature(nodes);
        if (signature == _breakpointSignature) return;
        _breakpointSignature = signature;

        var breaks = ChapterBreakpoints.Between(_document, nodes.Select(node => node.ToEntry()).ToArray(), SavedReference());
        var byAnchor = breaks.Where(item => item.AnchorId is not null)
            .GroupBy(item => item.AnchorId!).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var node in nodes)
        {
            if (!byAnchor.TryGetValue(node.Id, out var items)) { node.ClearBreak(); continue; }
            node.BreakLabel = BreakLabel(items);
            node.BreakDetail = string.Join("\n", items.Select(item => item.Detail));
            node.BreakAccent = items.Any(item => item.Kind != ChapterBreakpointKind.NumberGap) ? "Error" : "Warning";
        }
    }

    /// <summary>Identity of the tree plus the directory, so an unchanged tree is never analysed twice.</summary>
    private string BreakpointSignature(IReadOnlyList<ChapterTreeNode> nodes) => string.Join("|",
        (_document.SourceSha256, SavedReference()?.Titles.Count ?? 0).ToString(),
        nodes.Count,
        string.Join(";", nodes.Select(node => node.Id + ":" + node.Title + ":" + node.Level)));

    private static string BreakLabel(IReadOnlyList<ChapterBreakpoint> items)
    {
        var gap = items.FirstOrDefault(item => item.Kind == ChapterBreakpointKind.NumberGap);
        var reference = items.FirstOrDefault(item => item.Kind == ChapterBreakpointKind.ReferenceMissing);
        var typo = items.FirstOrDefault(item => item.Kind == ChapterBreakpointKind.HeadingTypo);
        var parts = new List<string>();
        if (gap is not null)
            parts.Add(gap.MissingNumbers.Count == 1
                ? $"此处跳过：缺第 {gap.MissingNumbers[0]} 章"
                : $"此处跳过：缺第 {gap.MissingNumbers[0]}–{gap.MissingNumbers[^1]} 章");
        if (typo is not null) parts.Add("编号写法有误");
        if (reference is not null)
            parts.Add(reference.MissingTitles.Count == 1
                ? $"目录里有「{reference.MissingTitles[0]}」"
                : $"目录里有 {reference.MissingTitles.Count} 章找不到");
        return parts.Count == 0 ? "" : "↕ " + string.Join(" · ", parts);
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
            if (GroupLocationsCombo.SelectedItem is null && GroupLocationsCombo.Items.Count > 0) GroupLocationsCombo.SelectedIndex = 0;
        }
        finally { _refreshingLocations = false; }
        GroupLocationsCombo.IsEnabled = group is not null && group.Lines.Count > 0;
        GroupLocationsCombo.Visibility = GroupLocationsCombo.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ReviewEvidenceExpander.Visibility = group is not null ? Visibility.Visible : Visibility.Collapsed;
        ReviewGroupControls.Visibility = group?.NodeIds.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        OnlyCurrentIssueCheck.Visibility = CurrentReviewIssue()?.Code == "numeric_body_group" ? Visibility.Visible : Visibility.Collapsed;
        GroupLocationsCombo.ToolTip = "选择原文位置，跳转并显示对应章节；不会修改内容。";
    }

    private void UpdateReviewCard()
    {
        if (ReviewHeading is null || _document is null) return;
        RefreshReviewGroupPreview();
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
        if (issue?.Code == "chapter_unnumbered")
        {
            ReviewHeading.Text = "异常长章 · 疑似无编号标题";
            ReviewExplanation.Text = issue.Message;
            ReviewDetailsText.Text = "本地检查默认扫描至少 200 行的章节，寻找同名分段独立短行。可在目录辅助修复中自定义阈值并结合参考目录。";
            ReviewActionButton.Content = "预览补建 / 获取参考目录…";
            ReviewActionButton.Visibility = Visibility.Visible;
            ReviewActionButton.IsEnabled = !_sourceChanged;
            ActionScopeText.Text = "只补建已确认的标题，不修改原始 TXT；原有跳章检测保留。";
            return;
        }
        if (issue?.Code == "chapter_repeated_sequence")
        {
            ReviewHeading.Text = "疑似重复拼接区段";
            ReviewExplanation.Text = "连续多章在另一处重复出现。先核对两段前后衔接，不要当成正常分卷。";
            ReviewDetailsText.Text = issue.Message;
            ReviewActionButton.Content = "定位另一段开头";
            ReviewActionButton.Visibility = Visibility.Visible;
            ReviewActionButton.IsEnabled = !_sourceChanged;
            ActionScopeText.Text = "展开判断依据可逐处定位；重复正文问题中可逐对比较并选择保留。";
            return;
        }
        if (issue?.Code == "chapter_content_duplicate")
        {
            ReviewHeading.Text = "疑似重复正文";
            ReviewExplanation.Text = "同名章节正文相似度达到 90%。先对比，再选择保留哪一份；不会自动删除。";
            ReviewDetailsText.Text = issue.Message + "\n相似度采用去空白后的连续 5 字片段重合度；行数仅作参考，不作为重复依据。选择下方位置可跳转原文。";
            ReviewActionButton.Content = "对比正文，选择保留…";
            ReviewActionButton.Visibility = Visibility.Visible;
            ReviewActionButton.IsEnabled = !_sourceChanged;
            ActionScopeText.Text = "仅处理本对章节 · 删除包含该章正文 · 可撤销 · 原始 TXT 不变";
            return;
        }
        if (_resultMessage is not null)
        {
            ReviewHeading.Text = "本次处理结果";
            ReviewExplanation.Text = _resultMessage;
            ReviewDetailsText.Text = "原始 TXT 未被修改。核对下方原文后，可撤销或前往下一组。";
            ActionScopeText.Text = _selectedNode is null ? "" : "当前结果：" + _selectedNode.Title;
            return;
        }
        if (issue?.Code == "chapter_structure_suggested")
        {
            ReviewHeading.Text = "建议整理章节结构";
            ReviewExplanation.Text = "多处结构信号同时出现。可以先补建漏识别标题，再预览整体整理；不会自动应用。";
            ReviewDetailsText.Text = issue.Message;
            ReviewActionButton.Content = "预览结构整理…";
            ReviewActionButton.Visibility = Visibility.Visible;
            ReviewActionButton.IsEnabled = !_sourceChanged;
            ActionScopeText.Text = "展开涉及位置可查看依据；局部修复功能仍可使用。";
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
        if (issue?.Code == "chapter_heading_typo")
        {
            ReviewHeading.Text = "疑似标题缺字 / 错字";
            ReviewExplanation.Text = "在跳章区间找到对应缺失编号；核对后补建章节并规范成品标题。";
            ReviewDetailsText.Text = issue.Message;
            ReviewActionButton.Content = "修复本组漏识别标题…";
            ReviewActionButton.Visibility = Visibility.Visible;
            ReviewActionButton.IsEnabled = !_sourceChanged;
            ActionScopeText.Text = "可撤销 · 原始 TXT 不变 · 批量入口在“更多”";
            return;
        }
        ReviewHeading.Text = count > 1 ? $"已选 {count} 个章节"
            : issue?.Code == "numeric_body_group" ? "疑似正文被识别为章节"
            : category ?? (_selectedNode is null ? "选择章节，核对原文" : "编辑当前章节");
        var advice = category switch
        {
            ReviewCategories.Misrecognized => "若是正文列举，可还原正文；原始数字条目及正文会完整保留。",
            ReviewCategories.Duplicate => "同一段内容出现多次。先预览再清理：同名相邻标题默认只清理正文 0 行的；重复正文需逐对比较后选择保留。",
            ReviewCategories.Structure => "核对前后章号与层级。跳号可能是漏识别，分卷也可能重新编号；层级不会自动修改，核对原文后选择设为子章节或移出父章节。",
            ReviewCategories.Missing => "确认属于标题后，选择原文行建立章节；不要把普通正文再次拆成章节。",
            _ => "编辑成品标题不改原始 TXT。Ctrl 多选，Shift 连选；目录勾选与操作选择独立。"
        };
        ReviewExplanation.Text = count > 1 ? "按真实兄弟顺序分段归入上一章；原始标题行与正文全部保留。" : advice;
        ReviewDetailsText.Text = (issue?.Message ?? advice) + (group is null ? "" : $"\n涉及 {group.Lines.Count} 处 · {Math.Max(1, group.RelatedIssues.Count)} 条关联提醒");
        ReviewExplanation.ToolTip = ReviewDetailsText.Text;
        var numericGroup = CurrentNumericGroup(issue);
        var targets = count == 1 && OnlyCurrentIssueCheck.IsChecked != true && numericGroup.Length > 1 ? numericGroup : selected;
        var restore = count > 1 || issue?.Code == "numeric_body_group";
        ReviewActionButton.Content = restore ? targets.Length > 1 ? $"还原{(count > 1 ? "已选" : "本组")} {targets.Length} 处为正文" : "还原为上一章正文"
            : issue?.Code == "chapter_duplicate" ? "检查并清理本组…"
            : category == ReviewCategories.Missing ? "从选中行建立章节"
            : SuggestedTitle() is { } title ? "修改为：" + title
            : issue?.Code is "volume_number_duplicate" or "chapter_level_gap" ? "核对层级处理…" : "编辑成品标题…";
        var plan = PlanBatchAction(targets, merge: true);
        ReviewActionButton.IsEnabled = !_sourceChanged && count > 0 && (restore ? plan.Any(p => p.Reason is null)
            : category != ReviewCategories.Missing || CanSplitSelectedLine());
        ActionScopeText.Text = restore ? DescribeBatchPlan(plan) : count == 1 ? $"当前章节：{_selectedNode?.Title}" : "请选择左侧章节。";
        if (_sourceChanged) ActionScopeText.Text = "原始 TXT 已变化，请先重新识别；当前行号不能继续编辑。";
    }

    private async void ReviewAction_Click(object sender, RoutedEventArgs e)
    {
        if (!ReviewActionButton.IsEnabled || _sourceChanged) return;
        var issue = CurrentReviewIssue();
        if (issue?.Code == "chapter_unnumbered") { CatalogAssist_Click(sender, e); return; }
        if (issue?.Code == "chapter_repeated_sequence" && ReviewGroup(issue) is { } sequence)
        {
            var first = sequence.Lines.First();
            NavigateToSourceLine(_selectedNode?.TitleLineNumber == first ? issue.LineNumber!.Value : first);
            return;
        }
        if (issue?.Code == "chapter_content_duplicate") { CompareDuplicateContents(ReviewGroup(issue)); return; }
        if (issue?.Code == "chapter_structure_suggested") { RecoverStructure_Click(sender, e); return; }
        if (issue?.Code == "chapter_heading_typo") { RepairMissingHeadings(issue.LineNumber); return; }
        if (issue?.Code == "numeric_chapters_suspected")
        {
            await RecognizeSuggestedNumericChaptersAsync(normalizeTitles: true);
            return;
        }
        var category = issue is null ? null : ReviewCategory(issue);
        var before = _undo.Count;
        var group = OperationSelection().Length == 1 && OnlyCurrentIssueCheck.IsChecked != true ? CurrentNumericGroup(issue) : [];
        if (group.Length > 1) SetOperationSelection(group);
        if (OperationSelection().Length > 1 || issue?.Code == "numeric_body_group") Merge_Click(sender, e);
        else if (issue?.Code == "chapter_duplicate") CleanDuplicateTitles(ReviewGroup(issue)?.NodeIds.ToHashSet());
        else if (issue?.Code is "volume_number_duplicate" or "chapter_level_gap") { ReviewMore_Click(sender, e); return; }
        else if (category == ReviewCategories.Missing) Split_Click(sender, e);
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
        var menu = new ContextMenu { PlacementTarget = sender as UIElement ?? ReviewMoreButton };
        foreach (var control in CurrentActionsPanel.Children)
        {
            if (control is Separator) { menu.Items.Add(new Separator()); continue; }
            if (control is not Button button || button.Visibility != Visibility.Visible) continue;
            var item = new MenuItem { Header = button.Content, IsEnabled = button.IsEnabled };
            item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            menu.Items.Add(item);
        }
        var restore = new MenuItem { Header = "取消章节身份，还原为正文…", IsEnabled = !_sourceChanged && _selectedNode is { IsFrontMatter: false } && OperationSelection().Length == 1 };
        restore.Click += (_, _) => RestoreContainerToBody();
        var keep = new MenuItem { Header = "取消父级，保留为普通章节…", IsEnabled = restore.IsEnabled && _selectedNode!.Children.Count > 0 };
        keep.Click += (_, _) => RestoreContainerToBody(keepHeading: true);
        menu.Items.Insert(0, keep);
        menu.Items.Insert(0, restore);
        menu.Items.Insert(2, new Separator());
        menu.IsOpen = true;
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
