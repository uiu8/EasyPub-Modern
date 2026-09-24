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
    private CancellationTokenSource? _catalogRequest;
    private string? _breakpointSignature;
    private CancellationTokenSource? _breakpointRequest;
    private bool _editorClosed;
    private ChapterBreakpoint[] _numberingNotes = [];

    /// <summary>
    /// What counts as a recoverable heading when the review panel offers to rebuild one. Kept per
    /// editor rather than global: the right answer depends on the book in front of the user, and a
    /// widened setting that suits one release will invent candidates in the next.
    /// </summary>
    internal MissingChapterHeadingOptions HeadingRepairOptions { get; set; } = new();

    private Button? _headingRepairOptionsButton;

    /// <summary>
    /// The judgement calls behind "which headings did the recogniser miss", made editable. Every one
    /// of these widens the search, so the dialog states what each widening costs as well as what it
    /// finds, and the result is still a candidate list the user ticks item by item.
    /// </summary>
    private void EditHeadingRepairOptions()
    {
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "这些开关决定“什么算漏识别标题”。放宽会找到更多，也会带来更多需要核对的候选；"
                 + "无论怎么放宽，补建都只发生在原文已有的行上，原始 TXT 不变。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
        });
        var gapRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        gapRow.Children.Add(new TextBlock { Text = "两章之间最多补：", VerticalAlignment = VerticalAlignment.Center });
        var gap = new TextBox { Text = HeadingRepairOptions.MaximumGap.ToString(), Width = 70 };
        gapRow.Children.Add(gap);
        gapRow.Children.Add(new TextBlock { Text = " 章", VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(gapRow);
        var isolated = new CheckBox
        {
            Content = "只认独立成行（上下有空行）的标题",
            IsChecked = HeadingRepairOptions.RequireIsolatedLine,
            Margin = new Thickness(0, 0, 0, 8),
        };
        panel.Children.Add(isolated);
        var typos = new CheckBox
        {
            Content = "允许按编号纠错表纠正章号里的错字（如「第一百零久章」）",
            IsChecked = HeadingRepairOptions.AllowNumberTypos,
            Margin = new Thickness(0, 0, 0, 8),
        };
        panel.Children.Add(typos);
        var prefix = new CheckBox
        {
            Content = "允许标题缺少「第」字（如「一百零二章 归途」）",
            IsChecked = HeadingRepairOptions.AllowMissingPrefix,
            Margin = new Thickness(0, 0, 0, 12),
        };
        panel.Children.Add(prefix);
        var note = new TextBlock
        {
            Text = "分卷重新计数的书，两章之间章号会差很多；把上限调小可以避免把这种编号约定当成缺正文。",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 0, 0, 14),
        };
        note.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        panel.Children.Add(note);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
        var save = new Button { Content = "应用", IsDefault = true };
        buttons.Children.Add(cancel); buttons.Children.Add(save);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = "补建范围", Owner = Window.GetWindow(this), Width = 520, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Content = panel,
        };
        dialog.SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        dialog.SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        save.Click += (_, _) =>
        {
            if (!int.TryParse(gap.Text, out var limit) || limit < 1 || limit > 500)
            {
                ShowReviewFeedback("上限请填 1–500 之间的整数。");
                return;
            }
            HeadingRepairOptions = new MissingChapterHeadingOptions
            {
                MaximumGap = limit,
                RequireIsolatedLine = isolated.IsChecked == true,
                AllowNumberTypos = typos.IsChecked == true,
                AllowMissingPrefix = prefix.IsChecked == true,
            };
            dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() != true) return;
        // The findings on screen were computed with the previous settings, so they have to be redone.
        InvalidateBreakpoints();
        ShowReviewFeedback($"补建范围已更新：两章之间最多补 {HeadingRepairOptions.MaximumGap} 章"
            + $"、{(HeadingRepairOptions.RequireIsolatedLine ? "只认独立成行" : "也认紧挨段落的标题")}"
            + $"、{(HeadingRepairOptions.AllowNumberTypos ? "允许章号纠错" : "不做章号纠错")}。请重新核对跳章区间。");
    }

    private ChapterReviewGroup? ReviewGroup(ConversionPreflightIssue? issue) => issue is null ? null
        : _reviewGroupIndex.TryGetValue((issue.Code, issue.LineNumber), out var group)
            ? group
            : _reviewGroups.FirstOrDefault(g => g.Issue.Code == issue.Code && g.Issue.LineNumber == issue.LineNumber);

    private string ReviewKey(ChapterReviewGroup group) => _document.SourceSha256 + "|" + group.Issue.Code + "|"
        + string.Join(",", group.Lines) + "|" + group.Issue.Message + "|"
        + string.Join(";", Flatten().Where(n => group.NodeIds.Contains(n.Id)).Select(n =>
            n.Id + ":" + n.Title + ":" + n.Level + ":" + string.Join(",", n.ContentRanges.Select(r => r.StartLine + "-" + r.EndLine))));

    private string ReviewCategory(ConversionPreflightIssue issue) => ReviewGroup(issue)?.Category ?? ReviewCategories.Other;

    /// <summary>
    /// 这条提醒在当前上下文下的建议 —— **唯一语义来源**。
    ///
    /// <para>卡片上的按钮文案与点下去执行的那条分支必须读**同一个结果**。以前它们各自
    /// switch 一遍问题码，于是出现说法与做法不符：按钮写着「编辑成品标题…」而点下去打开的是
    /// 正文对比窗，写着「到原文定位并核对…」而点下去把选中行建立成了章节。</para>
    ///
    /// <para>上下文取缓存的快照（<c>_context</c>），不是现捕 —— 捕获过程要读磁盘上的目录记录，
    /// 而这里在每次选中变化时都会被调用。</para>
    /// </summary>
    private ResolutionAdvice AdviceFor(ConversionPreflightIssue? issue, ChapterReviewGroup? group)
    {
        if (issue is null) return ResolutionAdvice.None("未选择提醒。");
        var finding = group ?? new ChapterReviewGroup(issue, ReviewCategory(issue), [], [], []);
        return IssueResolutionPolicy.Evaluate(finding, _context, FactsFor(finding));
    }

    /// <summary>
    /// 工作台手里的本地证据。**它对每一组都是"查过"**（true / false 都确定，不会是"没查过"）——
    /// 报告窗口拿不出这些，所以那边的建议更保守（§8.9 第 2 条）。
    /// </summary>
    /// <param name="gapCandidates">
    /// 跳章那一类的本地候选。可以传进来是为了**全文只扫一次**：统计整本书的工作量时，
    /// 每一组跳章都会问同一个问题。
    /// </param>
    private IssueResolutionFacts FactsFor(ChapterReviewGroup group,
        IReadOnlyList<MissingChapterHeading>? gapCandidates = null) => new(
        HasLocalHeadingCandidate: group.Issue.Code == "chapter_number_gap"
            && (gapCandidates ?? FindLocalHeadingCandidates()).Count > 0,
        HasSuggestedTitle: group.Issue.Code is "chapter_number_order" or "volume_number_duplicate" or "chapter_level_gap"
            && SuggestedTitle() is not null,
        CanSplitSelectedLine: OperationSelection().Length > 0 && CanSplitSelectedLine());

    /// <summary>
    /// 这本书的工作量分解 —— 顶部那句"下一步"读的就是它。
    ///
    /// <para>它和卡片上的按钮读的是**同一个** <see cref="IssueResolutionPolicy"/>，所以顶部的结论
    /// 与列表里逐条的标注永远一致，不会出现"总结说本地能修、点进去却要目录"。</para>
    ///
    /// <para>提醒还没算出来时返回 null —— 那时说"没有待核对的提醒"是错的，
    /// 而一句错话比一句"正在检查"更糟。</para>
    /// </summary>
    internal ChapterWorkload? Workload()
    {
        if (_reviewGroups.Length == 0) return null;
        // 全文只扫一次：下面每一组跳章都会问"本地有没有候选"。
        var gapCandidates = FindLocalHeadingCandidates();
        return IssueResolutionPolicy.Summarize(_reviewGroups, _context, group => FactsFor(group, gapCandidates));
    }

    /// <summary>跳章区间里本地能补建的候选。与"批量补建全书"读的是同一个搜索结果。</summary>
    private IReadOnlyList<MissingChapterHeading> FindLocalHeadingCandidates() =>
        MissingChapterHeadings.Find(_document, Flatten().Select(node => node.ToEntry()).ToArray(), HeadingRepairOptions);

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
        _referenceCatalog = ReferenceCatalogInput.ReadCatalog(saved);
        return _referenceCatalog;
    }

    /// <summary>
    /// 「忘记这份目录」—— **只删本书记住的记录，不动章节树**（设计文档 §3.3）。
    ///
    /// <para>它与「重新识别章节（会替换手工调整）…」是两件独立的事，而以前只有后者存在 ——
    /// 于是"不想再用这份目录了"只能靠重新识别来表达，而那会连手工编排一起丢掉。</para>
    ///
    /// <para>这里复用 <see cref="InvalidateBreakpoints"/>：它清掉的只是**内存里那份目录的副本**，
    /// 树上的 provenance 不受影响。所以忘掉之后第一行变成「未加载」而第二行仍是「目录校验后」——
    /// 两行同时成立，而且都是真的。</para>
    /// </summary>
    internal void ForgetSavedCatalog()
    {
        ReferenceCatalogInput.Forget(_document.SourceSha256);
        InvalidateBreakpoints();
    }

    /// <summary>Same source hash, so the directory that was found for the old tree still belongs to this one.</summary>
    private void InvalidateBreakpoints()
    {
        _referenceCatalog = null;
        _referenceCatalogLoaded = false;
        _breakpointSignature = null;
        // 目录的逐章定位只取决于原文行和目录本身，不取决于当前树；但版本一换，它描述的就是另一本书了。
        _catalogLocation = null;
        _catalogLocationLoaded = false;
        _catalogLocationFailure = null;
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
        FetchCatalogButton.Content = _fetchingCatalog ? "取消获取目录" : catalog is null
            ? "获取参考目录（联网）"
            : $"查看 / 更换目录（{catalog.Titles.Count} 章）";
        FetchCatalogButton.IsEnabled = true;
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
        if (_fetchingCatalog) { _catalogRequest?.Cancel(); return; }
        if (SavedReference() is not null) { CatalogAssist_Click(sender, e); return; }
        using var request = new CancellationTokenSource();
        _catalogRequest = request;
        EventHandler cancelOnClose = (_, _) => request.Cancel();
        Closed += cancelOnClose;
        _fetchingCatalog = true;
        RefreshCatalogState();
        ShowReviewFeedback("正在按书源顺序搜索公开目录…");
        try
        {
            var url = await Task.Run(() => ReferenceCatalogInput.FindLocalBookUrl(_document.SourcePath));
            var query = string.IsNullOrWhiteSpace(url) ? ChapterAutoRepair.ExtractBookName(_document.SourcePath) : url;
            var preferred = await ChapterAutoRepair.LoadPreferredSourcesAsync(_document.SourcePath);
            var catalogs = await new ReferenceCatalogClient().DiscoverAsync(query, request.Token, preferred);
            request.Token.ThrowIfCancellationRequested();
            if (ReferenceCatalogInput.Pick(catalogs) is not { } catalog)
            {
                ShowReviewFeedback("未获取到可用目录。可粘贴书籍网址／编号，或导入目录文字。");
                CatalogAssist_Click(this, new RoutedEventArgs());
                return;
            }
            ReferenceCatalogInput.SaveCatalog(_document.SourceSha256, catalog, query,
                _document.RecognitionOptions.NumericHeadingMinimumBodyLines);
            _referenceCatalog = catalog;
            _referenceCatalogLoaded = true;
            _breakpointSignature = null;
            ApplyBreakpoints();
            ShowReviewFeedback($"已获取目录：{catalog.PageTitle}（{catalog.Titles.Count} 章）。"
                + "章节树上标出的目录未匹配项仍需对照原文核实。");
        }
        catch (OperationCanceledException) { ShowReviewFeedback("已取消获取目录。"); }
        catch (Exception error)
        {
            ShowReviewFeedback("获取目录失败：" + error.Message + "。可点「选择参考目录…」粘贴网址或导入目录文字。");
        }
        finally
        {
            _fetchingCatalog = false;
            _catalogRequest = null;
            Closed -= cancelOnClose;
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

    private async void ApplyBreakpoints()
    {
        if (_editorClosed || _document is null || Roots is null) return;
        var nodes = Flatten().ToArray();
        var signature = BreakpointSignature(nodes);
        if (signature == _breakpointSignature) return;
        _breakpointSignature = signature;
        _breakpointRequest?.Cancel();
        using var request = new CancellationTokenSource();
        _breakpointRequest = request;
        var document = _document;
        var entries = nodes.Select(node => node.ToEntry()).ToArray();
        var catalog = SavedReference();
        try
        {
        var breaks = await Task.Run(() => ChapterBreakpoints.Between(document, entries, catalog, request.Token), request.Token);
        if (_editorClosed || request.IsCancellationRequested || signature != _breakpointSignature) return;
        _numberingNotes = breaks.Where(item => item.Kind == ChapterBreakpointKind.NumberGap && item.MissingNumbers.Count == 0).ToArray();
        NumberingNotesButton.Content = $"编号差异 {_numberingNotes.Length} 处…";
        NumberingNotesButton.Visibility = _numberingNotes.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        var byAnchor = breaks.Except(_numberingNotes).Where(item => item.AnchorId is not null || item.NextId is not null)
            .GroupBy(item => item.AnchorId ?? item.NextId!).ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var node in nodes)
        {
            if (!byAnchor.TryGetValue(node.Id, out var items)) { node.ClearBreak(); continue; }
            node.BreakLabel = BreakLabel(items);
            node.BreakDetail = string.Join("\n", items.Select(item => item.Detail));
            node.BreakAccent = items.Any(item => item.Kind != ChapterBreakpointKind.NumberGap) ? "Error" : "Warning";
        }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { _breakpointSignature = null; ShowReviewFeedback("断点核对未完成：" + error.Message); }
        finally { if (ReferenceEquals(_breakpointRequest, request)) _breakpointRequest = null; }
    }

    /// <summary>Identity of the tree plus the directory, so an unchanged tree is never analysed twice.</summary>
    private string BreakpointSignature(IReadOnlyList<ChapterTreeNode> nodes) => string.Join("|",
        _document.SourceSha256, System.Text.Json.JsonSerializer.Serialize(SavedReference()),
        nodes.Count,
        string.Join(";", nodes.Select(node => node.Id + ":" + node.Title + ":" + node.Level + ":"
            + string.Join(",", node.ContentRanges.Select(r => r.StartLine + "-" + r.EndLine)))));

    private static string BreakLabel(IReadOnlyList<ChapterBreakpoint> items) => ChapterBreakpointLabel.For(items);

    private void NumberingNotes_Click(object sender, RoutedEventArgs e)
    {
        var dialog = ThemedWindow("编号差异 · 不作为缺章结论", 850, 580);
        var panel = new DockPanel { Margin = new Thickness(18) };
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var locate = new Button { Content = "定位选中位置" };
        footer.Children.Add(locate); footer.Children.Add(new Button { Content = "关闭", IsCancel = true });
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        var list = new ListBox { ItemsSource = _numberingNotes, DisplayMemberPath = "Detail" };
        panel.Children.Add(list); dialog.Content = panel;
        locate.Click += (_, _) =>
        {
            if (list.SelectedItem is not ChapterBreakpoint item) return;
            var node = Flatten().FirstOrDefault(n => n.Id == (item.NextId ?? item.AnchorId));
            if (node?.TitleLineNumber is not int line) return;
            dialog.Close(); LocateDuplicateChapter(line);
        };
        dialog.ShowDialog();
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
        ReviewSequencePairsButton.Visibility = Visibility.Collapsed;
        if (ReviewHeading is null || _document is null) return;
        RefreshReviewGroupPreview();
        var selected = OperationSelection();
        var count = selected.Length;
        var issue = CurrentReviewIssue();
        var category = issue is null ? null : ReviewCategory(issue);
        var group = ReviewGroup(issue);
        ConfirmNormalButton.IsEnabled = issue is not null && count <= 1;
        ConfirmNormalButton.Visibility = issue is not null && count <= 1 ? Visibility.Visible : Visibility.Collapsed;
        // A reminder should offer its repair without first having to select a chapter: "待核对" mode shows no
        // selection at all, and the gap list is exactly where the user is working. Split still needs a row,
        // so its button waits for one — every other action carries its own subject.
        var actionableWithoutSelection = category == ReviewCategories.Missing;
        ReviewActionButton.Visibility = (count == 0 && !actionableWithoutSelection) || _resultMessage is not null
            ? Visibility.Collapsed : Visibility.Visible;
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
            ReviewSequencePairsButton.Visibility = Visibility.Visible;
            ReviewSequencePairsButton.IsEnabled = !_sourceChanged;
            ReviewHeading.Text = "疑似重复拼接区段";
            ReviewExplanation.Text = "连续多章在另一处重复出现。先核对两段前后衔接，不要当成正常分卷。";
            ReviewDetailsText.Text = issue.Message;
            ReviewActionButton.Content = "定位另一段开头";
            ReviewActionButton.Visibility = Visibility.Visible;
            ReviewActionButton.IsEnabled = !_sourceChanged;
            ActionScopeText.Text = "定位核对两段衔接，或直接逐对核对本区段；每次只处理一对，可撤销，原始 TXT 不变。";
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
        // A gap is a missing chapter. The only two honest ways out are finding the heading the tree failed to
        // recognise, or comparing against the official directory — so the button offers exactly those, and
        // never the generic "edit the finished title" fallback, which cannot add the chapter back.
        if (issue?.Code == "chapter_number_gap")
        {
            var entries = Flatten().Select(node => node.ToEntry()).ToArray();
            var fixable = MissingChapterHeadings.Find(_document, entries, HeadingRepairOptions);
            var related = _selectedNode?.TitleLineNumber is int gapLine
                ? fixable.Count(candidate => candidate.NextLine == gapLine) : 0;
            ReviewHeading.Text = "跳章区间 · 章节号之间断了";
            ReviewExplanation.Text = related > 0
                ? "这一段里找到了可补建的标题（疑似漏识别或标题写错），补建后章节树会接上。"
                : "这一段在原文里找不到对应标题——可能是漏识别，也可能源文本本身缺这一章。软件不会凭空造出章节。";
            ReviewDetailsText.Text = issue.Message + "\n" + (related > 0
                ? "「补建本组…」只补建已在原文中定位到的标题，原始 TXT 不变，可撤销。"
                : "可先对照参考目录确认这一章是否真的存在；仍找不到就说明源文件缺这一章，需要换来源补齐。"
                  + "全部跳章区间的批量入口在「专项处理 ▾ → 批量修复跳章区间漏识别标题…」。");
            ReviewActionButton.Visibility = Visibility.Visible;
            ReviewActionButton.Content = related > 0
                ? $"补建本组 {related} 个漏识别标题…"
                : fixable.Count > 0 ? $"批量补建全书的 {fixable.Count} 处漏识别标题…" : "获取参考目录并对照…";
            ReviewActionButton.IsEnabled = !_sourceChanged;
            ActionScopeText.Text = related > 0 || fixable.Count > 0
                ? "只补建已确认的标题 · 原始 TXT 不变 · 可撤销"
                : "不修改任何内容 · 只用于核对";
            // The search behind those numbers is a set of judgement calls — how wide a gap to bridge,
            // whether a heading must stand alone, whether a mistyped number may be corrected. A book
            // that finds nothing usually needs one of them widened, and saying which one is the whole
            // point of exposing them rather than hard-coding an answer. The button is created once:
            // this branch runs on every selection change.
            if (_headingRepairOptionsButton is null)
            {
                _headingRepairOptionsButton = new Button
                {
                    Content = "补建范围…",
                    Margin = new Thickness(6, 0, 0, 0),
                    ToolTip = "调整“什么算漏识别标题”的判断范围；放宽后候选会变多，仍需逐条核对",
                };
                _headingRepairOptionsButton.Click += (_, _) => EditHeadingRepairOptions();
                if (ReviewActionButton.Parent is Panel actionRow) actionRow.Children.Add(_headingRepairOptionsButton);
            }
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
        // 算一次，文案与执行都用它。**两处各算一遍正是错配的来源** —— 以前这里按类别拼字符串，
        // 而 ReviewAction_Click 按问题码分派，两边对同一条提醒的"该做什么"从来没有被核对过。
        var resolution = AdviceFor(issue, group);
        var restore = count > 1 || resolution.Recommended?.Intent == ResolutionIntent.RestoreAsBody;
        ReviewActionButton.Content = restore ? targets.Length > 1 ? $"还原{(count > 1 ? "已选" : "本组")} {targets.Length} 处为正文" : "还原为上一章正文"
            : ChapterIssueAction.CtaFor(resolution.Recommended, SuggestedTitle());
        // 按钮的说明就是这条建议自己的解释 —— 它随上下文变（有目录 / 没目录 / 原文已变），
        // 不再是一句写死的固定文案。第二条路也一并说出来：一条提醒常常有两个诚实的出口，
        // 只显示被推荐的那个会让用户以为没有别的选择。
        var alternative = resolution.AvailableActions.FirstOrDefault(a => a.Intent != resolution.RecommendedIntent);
        ReviewActionButton.ToolTip = resolution.Recommended is { } chosen
            ? chosen.Explanation + (alternative is null ? "" : $"\n\n另一条路：{ChapterIssueAction.CtaFor(alternative)}")
            : null;
        // 这条提醒属于哪一堆 —— 与顶部那三个数字同源（§5.6"前者在后"）。
        // 措辞是给用户看的，所以不直接用枚举名。
        if (ReviewBasisTag is not null && ReviewBasisTagText is not null)
        {
            var kind = IssueResolutionPolicy.Classify(resolution);
            ReviewBasisTagText.Text = kind switch
            {
                ChapterWorkloadKind.NeedsCatalog => "需要参考目录",
                ChapterWorkloadKind.Local => "本地就能修",
                _ => "需要你核对",
            };
            // 三堆各一个颜色，和效果图一致：暖色＝卡住了，绿色＝能自己办，灰色＝中性。
            // 颜色写在这里而不是主题字典里：这三堆是这份界面的固定词汇，不该随主题改名。
            var (fill, ink) = kind switch
            {
                ChapterWorkloadKind.NeedsCatalog => (System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xEC), System.Windows.Media.Color.FromRgb(0x9A, 0x34, 0x12)),
                ChapterWorkloadKind.Local => (System.Windows.Media.Color.FromRgb(0xED, 0xF7, 0xEF), System.Windows.Media.Color.FromRgb(0x16, 0x65, 0x34)),
                _ => (System.Windows.Media.Color.FromRgb(0xF1, 0xF3, 0xF7), System.Windows.Media.Color.FromRgb(0x3F, 0x4A, 0x5A)),
            };
            ReviewBasisTag.Background = new System.Windows.Media.SolidColorBrush(fill);
            ReviewBasisTagText.Foreground = new System.Windows.Media.SolidColorBrush(ink);
            ReviewBasisTag.Visibility = Visibility.Visible;
        }
        var plan = PlanBatchAction(targets, merge: true);
        // 只有"把选中行建立成章节"需要先选中一行；其余动作各自带着自己的对象，或者只导航。
        // 以前这里按**类别**判断（漏识别一律要求先选中），于是"目录里有、树里没有"这种
        // 根本不需要选中行的提醒也被禁用了按钮。
        var needsSelection = resolution.Recommended?.Intent == ResolutionIntent.PromoteSelectedLine && count == 0;
        ReviewActionButton.IsEnabled = !_sourceChanged && !needsSelection
            && (restore ? count > 0 && plan.Any(p => p.Reason is null) : true);
        ActionScopeText.Text = restore ? DescribeBatchPlan(plan) : count == 1 ? $"当前章节：{_selectedNode?.Title}" : "请选择左侧章节。";
        if (_sourceChanged) ActionScopeText.Text = "原始 TXT 已变化，请先重新识别；当前行号不能继续编辑。";
    }

    private async void ReviewAction_Click(object sender, RoutedEventArgs e)
    {
        if (!ReviewActionButton.IsEnabled || _sourceChanged) return;
        var issue = CurrentReviewIssue();
        // **与卡片文案读同一个 advice。** 这一句是这次重构的全部意义：按钮说什么，
        // 这一下就做什么。以前这里是另一套按问题码的 switch，与拼按钮文案的那套从未被核对过。
        var intent = AdviceFor(issue, ReviewGroup(issue)).Recommended?.Intent ?? ResolutionIntent.Unknown;
        // 按钮文字与这条 intent 是同一个来源；把它记下来，事后就能回答
        // "他按的那个按钮，代码走的是哪条分支"。
        InteractionLog.Decision("执行提醒动作", new { code = issue?.Code, intent = intent.ToString() });
        var before = _undo.Count;
        var numericGroup = OperationSelection().Length == 1 && OnlyCurrentIssueCheck.IsChecked != true ? CurrentNumericGroup(issue) : [];
        if (numericGroup.Length > 1) SetOperationSelection(numericGroup);
        if (OperationSelection().Length > 1) Merge_Click(sender, e);
        else switch (intent)
        {
            case ResolutionIntent.RestoreAsBody:
                Merge_Click(sender, e);
                break;
            case ResolutionIntent.CleanDuplicateTitles:
                CleanDuplicateTitles(ReviewGroup(issue)?.NodeIds.ToHashSet());
                break;
            case ResolutionIntent.PromoteSelectedLine:
                Split_Click(sender, e);
                break;
            case ResolutionIntent.ApplySuggestedTitle:
                CorrectNumber_Click(sender, e);
                break;
            case ResolutionIntent.EditTreeTitle:
                if (_selectedNode is not null) EditTitle(_selectedNode);
                break;
            case ResolutionIntent.RepairMissingHeadings:
            {
                // 选中的行本身就在候选里时只补它，否则补全区间 —— 这是"本组"与"全书"的区别。
                var candidates = FindLocalHeadingCandidates();
                var line = _selectedNode?.TitleLineNumber is int selected && candidates.Any(c => c.NextLine == selected)
                    ? selected : (int?)null;
                RepairMissingHeadings(line);
                return;
            }
            case ResolutionIntent.RepairUnnumberedHeadings:
            case ResolutionIntent.AdoptCatalogChapters:
                CatalogAssist_Click(sender, e);
                return;
            case ResolutionIntent.RecoverStructure:
                RecoverStructure_Click(sender, e);
                return;
            case ResolutionIntent.CompareDuplicateBodies:
                CompareDuplicateContents(ReviewGroup(issue));
                return;
            case ResolutionIntent.ReviewHierarchy:
                ReviewMore_Click(sender, e);
                return;
            case ResolutionIntent.ReRecognizeAsNumeric:
                await RecognizeSuggestedNumericChaptersAsync(normalizeTitles: true);
                return;
            case ResolutionIntent.AcquireCatalog:
                FetchCatalog_Click(sender, e);
                return;
            case ResolutionIntent.NavigateToSource:
            {
                // 重复区段有两个开头：第一次先去前段，再点一次去后段。
                var lines = ReviewGroup(issue)?.Lines ?? [];
                var first = lines.Count > 0 ? lines[0] : issue?.LineNumber ?? 0;
                if (first > 0)
                    NavigateToSourceLine(_selectedNode?.TitleLineNumber == first && issue?.LineNumber is int other ? other : first);
                return;
            }
            default:
                break;
        }
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
        // 两处都写：卡片给"现在是什么状态"，提示条给"刚刚发生了什么"。
        //
        // 截图里这两句在 _resultMessage 这条路径上是同一句话，看起来像重复 —— 但**它们不是**：
        // ChapterBatchTests 有两处断言"操作之后提示条必须可见"，而那锁的正是"用户做完一件事
        // 能得到一句反馈"。想让它们不重样，要改的是两张纸各自说什么，而不是让其中一张闭嘴；
        // 那是另一件事，不在本轮范围内。
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
        SetReviewResult("已确认本组正常；应用并返回后随项目保存。原文或相关章节变化后重新提示；更多 → 视图可恢复。");
        UpdateSaveState();
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
