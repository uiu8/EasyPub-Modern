using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// What one repair will change, built for a reader who has to decide about it.
///
/// The window it replaces was one read-only TextBox: the verdict, then every unmatched reference
/// title, then the categorised account, and only then the list of changes — with 264 chapter titles in
/// between, and no way to strike anything out. This one leads with the four figures that answer "is my
/// book actually incomplete" (how many chapters are added, how many headings fold back, how many need
/// a human, how many are only the aggregator's leave notices), keeps every category one click away in
/// a sidebar that shows how much of each is still selected, and gives every actionable row a checkbox
/// seeded from the plan's own <see cref="ReferencePlan.DefaultSelection"/>.
/// </summary>
public sealed class RepairReviewWindow : Window
{
    /// <summary>
    /// A leave notice is never something to go looking for, so these two are not categories to act in:
    /// their counts belong in the header, where the split between them does its work.
    /// </summary>
    private static readonly string[] HeaderOnlyLabels = ["参考目录未匹配", "疑似公告"];

    private readonly AutoRepairOutcome _outcome;
    private readonly IReadOnlyList<ChapterTreeEntry> _before;
    private readonly ChapterTreeDocument? _previewDocument;
    private readonly Action<IReadOnlyCollection<ReferenceAction>> _apply;
    private readonly List<CategoryView> _categories = [];
    private readonly List<StatView> _stats = [];
    private readonly TextBlock _summaryTitle = new() { FontSize = 17, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _summaryDetail = new() { FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) };
    private Panel _changePanel = null!;

    private Panel _categoryList = null!;
    private ScrollViewer _detail = null!;
    private Panel _detailItems = null!;
    private TextBlock? _detailHeading;
    private TextBlock _selectionText = null!;
    private Button _applyButton = null!;
    private CheckBox? _verified;
    private int _selectedCategory;

    public RepairReviewWindow(AutoRepairOutcome outcome, IReadOnlyList<ChapterTreeEntry> before,
        Action<IReadOnlyCollection<ReferenceAction>> apply, ChapterTreeDocument? previewDocument = null)
    {
        _outcome = outcome;
        _before = before;
        _apply = apply;
        _previewDocument = previewDocument;
        Title = "目录修复 · 核对后应用";
        Name = "RepairReviewWindow";
        Width = 980;
        Height = 740;
        MinWidth = 760;
        MinHeight = 560;
        // The detail pane owns the scrolling, so the window must not grow to fit 251 announcement
        // titles; and it resizes, because how much of a long book one wants to see at once is a taste.
        ResizeMode = ResizeMode.CanResize;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        Content = BuildLayout();
        Refresh();
    }

    /// <summary>The actions the user left ticked, read from the window's own model.</summary>
    public IReadOnlyCollection<ReferenceAction> SelectedActions() =>
        _categories.SelectMany(category => category.Rows)
            .Where(row => row.Checkbox?.IsChecked == true)
            .Select(row => row.Action!)
            .ToArray();

    private UIElement BuildLayout()
    {
        var root = new DockPanel();
        var footer = BuildFooter();
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var body = new Grid { Margin = new Thickness(22, 16, 22, 0) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = BuildHeader();
        Grid.SetRow(header, 0);
        body.Children.Add(header);
        var panes = BuildPanes();
        Grid.SetRow(panes, 2);
        body.Children.Add(panes);
        var safety = BuildSafetyBar();
        Grid.SetRow(safety, 3);
        body.Children.Add(safety);
        root.Children.Add(body);
        return root;
    }

    private UIElement BuildHeader()
    {
        var stack = new StackPanel();
        stack.Children.Add(_summaryTitle);
        _summaryDetail.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        stack.Children.Add(_summaryDetail);
        stack.Children.Add(BuildStats());
        var card = new Border
        {
            Padding = new Thickness(22, 16, 22, 16),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    private UIElement BuildStats()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), Name = "RepairSummaryPanel" };
        // The first two move with the checkboxes, the last two do not: a chapter the directory numbered
        // but the text lacks, and one it never numbered, are facts about the directory rather than
        // choices, and no checkbox can change them. Each label says which it is.
        (string Label, Func<IReadOnlyDictionary<ReferenceActionKind, int>, int> Value, string Brush, string? Note)[] blocks =
        [
            ("补齐", chosen => chosen.GetValueOrDefault(ReferenceActionKind.AddChapter), "PrimaryTextBrush", null),
            ("处并回", chosen => chosen.GetValueOrDefault(ReferenceActionKind.DemoteExtra), "PrimaryTextBrush", null),
            ("章需你核对", _ => _outcome.UnmatchedWithNumber, "WarningBrush", null),
            ("章疑似公告", _ => _outcome.UnmatchedNoNumber, "SecondaryTextBrush", "通常无需处理"),
        ];
        foreach (var (label, value, brush, note) in blocks)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 40, 0) };
            var number = new TextBlock { FontSize = 28, FontWeight = FontWeights.SemiBold };
            number.SetResourceReference(TextBlock.ForegroundProperty, brush);
            stack.Children.Add(number);
            var caption = new TextBlock { Text = label, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            stack.Children.Add(caption);
            if (note is not null)
            {
                var hint = new TextBlock { Text = note, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), Opacity = 0.75 };
                hint.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
                stack.Children.Add(hint);
            }
            // Kept so cancelling a row updates the figure it belongs to without rebuilding the header.
            _stats.Add(new StatView(number, value));
            panel.Children.Add(stack);
        }
        return panel;
    }

    private UIElement BuildPanes()
    {
        _categoryList = BuildCategoryList();
        _changePanel = new StackPanel { Margin = new Thickness(0, 18, 0, 0), Name = "RepairChangeList" };
        _detail = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 16, 14, 16),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240), MinWidth = 190 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 320 });
        var sidebar = new Border { Padding = new Thickness(10, 12, 10, 12), Child = _categoryList };
        Grid.SetColumn(sidebar, 0);
        grid.Children.Add(sidebar);
        var separator = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
        separator.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetColumn(separator, 1);
        grid.Children.Add(separator);
        Grid.SetColumn(_detail, 2);
        grid.Children.Add(_detail);
        // The first category's rows are drawn before the first Refresh, which only updates labels.
        if (_categories.Count > 0)
        {
            _detail.Content = BuildDetail(_categories[0]);
            BuildDetailRows(_categories[0]);
        }
        var card = new Border { CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Child = grid };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    private Panel BuildCategoryList()
    {
        var panel = new StackPanel { Name = "RepairCategoryList" };
        foreach (var category in Categories())
        {
            var row = BuildCategoryRow(category, _categories.Count);
            category.Row = row;
            _categories.Add(category);
            panel.Children.Add(row);
        }
        return panel;
    }

    /// <summary>
    /// The categories a reader acts in, plus the two that only carry a figure. Each actionable row
    /// keeps a reference to the planned action behind it, which is what makes cancelling it real
    /// rather than a checkbox that changes nothing.
    /// </summary>
    private IReadOnlyList<CategoryView> Categories()
    {
        var views = new List<CategoryView>();
        foreach (var group in _outcome.Report.Where(group => !HeaderOnlyLabels.Contains(group.Label)))
        {
            var rows = group.Items.Select(item => new ReviewRow(item,
                item.Kind is null ? null : _outcome.Plan?.Actions.FirstOrDefault(action =>
                    action.Kind == item.Kind && action.Line == item.Line && action.Title == item.Title))).ToArray();
            views.Add(new CategoryView(group.Label, group.Unit, group.Summary, rows, rows.Any(row => row.Action is not null)));
        }
        // Readable but not selectable: nothing in them can be applied, and the numbered half is
        // exactly what the header asks the user to check.
        foreach (var group in _outcome.Report.Where(group => HeaderOnlyLabels.Contains(group.Label)))
            views.Add(new CategoryView(group.Label, group.Unit, group.Summary,
                group.Items.Select(item => new ReviewRow(item, null)).ToArray(), false));
        return views;
    }

    private Border BuildCategoryRow(CategoryView category, int index)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new TextBlock
        {
            Text = category.Selectable ? "\u2713" : "\u25CB",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, category.Selectable ? "SuccessBrush" : "SecondaryTextBrush");
        row.Children.Add(glyph);
        var name = new TextBlock
        {
            Text = category.Label,
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var count = new TextBlock
        {
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        Grid.SetColumn(count, 2);
        row.Children.Add(count);
        category.CountText = count;

        var border = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(8),
            Child = row,
        };
        border.SetResourceReference(Border.BackgroundProperty, index == 0 ? "SelectedSurfaceBrush" : "SubtleSurfaceBrush");
        var position = index;
        border.MouseLeftButtonUp += (_, _) => SelectCategory(position);
        return border;
    }

    private void SelectCategory(int index)
    {
        _selectedCategory = index;
        for (var position = 0; position < _categories.Count; position++)
            _categories[position].Row?.SetResourceReference(Border.BackgroundProperty,
                position == index ? "SelectedSurfaceBrush" : "SubtleSurfaceBrush");
        // Switching category is the one time the rows are rebuilt, and it is safe here: the user is
        // not mid-way through the rows they are leaving. The change list stays outside _detailItems so
        // it survives both this and every later Refresh.
        var category = _categories[index];
        _detail.Content = BuildDetail(category);
        _detailItems = (Panel)((StackPanel)_detail.Content).Children[1];
        BuildDetailRows(category);
        Refresh();
    }

    private Panel BuildDetail(CategoryView category)
    {
        var stack = new StackPanel();
        var blurb = new TextBlock
        {
            Text = category.Summary,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        };
        blurb.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        stack.Children.Add(blurb);
        _detailItems = new StackPanel();
        stack.Children.Add(_detailItems);
        stack.Children.Add(_changePanel);
        return stack;
    }

    /// <summary>Draws the rows of one category once, so a later Refresh never recreates them.</summary>
    private void BuildDetailRows(CategoryView category)
    {
        _detailHeading = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 4, 0, 8),
        };
        _detailItems.Children.Clear();
        _detailItems.Children.Add(_detailHeading);
        foreach (var row in category.Rows) _detailItems.Children.Add(BuildRow(row, category.Selectable));
    }

    /// <summary>
    /// What the tree becomes, grouped by the kind of change and summed up.
    ///
    /// The old window printed one line per entry and appended it after every chapter title it had,
    /// which is how a real book reached 286 lines — most of them the same six volumes reported as a
    /// removal and an addition each. Counting by kind turns that into a handful of rows a reader can
    /// take in at once, with the full list one click away for anyone who wants to check a line.
    /// </summary>
    private void BuildChangeList()
    {
        if (_changePanel is null) return;
        _changePanel.Children.Clear();
        var changes = PreviewChanges();

        var heading = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        heading.Text = changes.Count == 0 ? "实际变化　无" : $"实际变化　{changes.Count} 项，共 {changes.Select(c => c.Kind).Distinct().Count()} 类";
        _changePanel.Children.Add(heading);

        if (changes.Count == 0)
        {
            var none = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            none.Text = "按当前勾选，章节树不会改变。";
            _changePanel.Children.Add(none);
            return;
        }

        // One row per kind, each naming its own count and one concrete example, so "建立卷层级 2 个"
        // says what happened without listing every volume.
        foreach (var group in changes.GroupBy(change => change.Kind).OrderByDescending(group => group.Count()))
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };
            var summary = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
            summary.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            summary.Text = DescribeChangeGroup(group.Key, group.ToArray());
            row.Children.Add(summary);

            var detail = new Expander
            {
                Header = $"展开 {group.Count()} 条明细",
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
                Content = new StackPanel { Margin = new Thickness(12, 4, 0, 4) },
            };
            if (detail.Content is StackPanel detailList)
                foreach (var change in group)
                {
                    var line = new TextBlock
                    {
                        FontSize = 11.5,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 2),
                    };
                    line.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
                    line.Text = "· " + DescribeOne(change);
                    detailList.Children.Add(line);
                }
            row.Children.Add(detail);
            _changePanel.Children.Add(row);
        }
    }

    /// <summary>The one-line summary of a whole kind of change, with one concrete example.</summary>
    private static string DescribeChangeGroup(RepairChangeKind kind, IReadOnlyList<RepairChange> changes)
    {
        var count = changes.Count;
        var many = count > 1 ? $" {count}" : "";
        var example = count > 0 && changes[0].Title.Length > 0 ? $"（例：{changes[0].Title}）" : "";
        return kind switch
        {
            RepairChangeKind.RebuiltVolume => $"建立卷层级{many} 个{example}",
            RepairChangeKind.AddedChapter => $"收录章节{many} 章{example}",
            RepairChangeKind.RetitledChapter => $"标题改按目录写法{many} 章{example}",
            RepairChangeKind.FoldedIntoBody => $"标题并回正文{many} 处（文字全部保留）{example}",
            RepairChangeKind.RemovedDuplicate => $"移除重复正文{many} 处{example}",
            RepairChangeKind.RemovedEntry => $"移除章节项{many} 个{example}",
            RepairChangeKind.ReassignedBody => $"正文重新分配{many} 处{example}",
            RepairChangeKind.Reordered => $"位置调整{many} 处{example}",
            // This one already carries its own count in Title ("12 行").
            RepairChangeKind.RemovedLines => count == 0 ? "未移除任何原文" : $"从成品移除原文 {changes[0].Title}",
            _ => $"{kind}{many}{example}",
        };
    }

    /// <summary>One change, as a sentence, for the expanded list.</summary>
    private static string DescribeOne(RepairChange change) => change.Kind switch
    {
        RepairChangeKind.RebuiltVolume => $"{change.Title}　{change.Detail}" + (change.Line > 0 ? $"（原文行 {change.Line}）" : ""),
        RepairChangeKind.AddedChapter => $"{change.Title}（原文行 {change.Line}）",
        RepairChangeKind.RetitledChapter => $"{change.Title}　{change.Detail}（原文行 {change.Line}）",
        RepairChangeKind.RemovedLines => $"移除原文 {change.Title}：{change.Detail}",
        _ => change.Line > 0 ? $"{change.Title}　{change.Detail}（原文行 {change.Line}）" : $"{change.Title}　{change.Detail}",
    };

    /// <summary>
    /// The tree as the current selection would build it. Recomputed on every tick so the list always
    /// describes what pressing the button would actually do — the earlier version froze it at window
    /// construction and silently went stale the moment a checkbox moved.
    /// </summary>
    private IReadOnlyList<RepairChange> PreviewChanges()
    {
        var after = _outcome.Entries ?? _before;
        if (_previewDocument is not null && _outcome.Plan is not null)
        {
            var chosen = SelectedActions();
            // Rebuilding verifies the tree again, which is wasted work while the selection still
            // matches the preview that already produced it.
            var unchanged = chosen.Count == _outcome.Plan.DefaultSelection.Count()
                && chosen.ToHashSet().SetEquals(_outcome.Plan.DefaultSelection);
            after = unchanged ? _outcome.Entries ?? _before
                : chosen.Count == 0 ? _before
                : ChapterAutoRepair.RebuildWithSelection(_previewDocument, _outcome, chosen);
        }
        return RepairIntegrity.Describe(after, _before);
    }

    /// <summary>
    /// Re-reads everything except the rows themselves. The rows are deliberately left alone: they hold
    /// the checkboxes the user is clicking, so rebuilding them would recreate each box from
    /// <see cref="RepairReportItem.Recommended"/> and silently undo the cancellation that triggered
    /// this refresh. Only the labels that describe the selection change.
    /// </summary>
    private void Refresh()
    {
        var groups = _categories.Where(view => view.Selectable).ToArray();
        var total = groups.Sum(view => view.Rows.Count);
        var chosen = groups.Sum(view => view.Rows.Count(row => row.Checkbox?.IsChecked == true));
        foreach (var category in _categories)
            if (category.CountText is not null) category.CountText.Text = category.CountTextValue;
        // Counted once: each figure used to walk every row again, and Refresh runs on every click.
        var byKind = SelectedActions().GroupBy(action => action.Kind).ToDictionary(group => group.Key, group => group.Count());
        for (var index = 0; index < _stats.Count; index++)
            _stats[index].Number.Text = _stats[index].Value(byKind).ToString();

        _summaryTitle.Text = total == 0
            ? "本次没有需要应用的改动"
            : $"本次将做 {groups.Length} 类修改，共 {total} 项";
        _summaryDetail.Text = $"参考目录 {_outcome.ReferenceChapters} 章 · 已对齐 {_outcome.AlignedCount} 章 · " +
            $"保留目录外 {_outcome.Extra} 章 · 未匹配 {_outcome.Missing} 章";

        UpdateDetailHeading(chosen, total);
        // The change list follows the selection, so it always says what the button would do.
        BuildChangeList();
        _selectionText.Text = chosen == 0
            ? (total == 0 ? "本次没有需要应用的改动" : "未选任何修改 · 全部保持不变")
            : $"已选 {chosen} 项 · 取消勾选的不会执行";
        _applyButton.IsEnabled = chosen > 0 && (_verified?.IsChecked ?? true);
    }

    private void UpdateDetailHeading(int chosen, int total)
    {
        if (_detailItems is null || _detailHeading is null || _categories.Count == 0) return;
        var category = _categories[_selectedCategory];
        _detailHeading.SetResourceReference(TextBlock.ForegroundProperty,
            category.Selectable ? "PrimaryTextBrush" : "WarningBrush");
        _detailHeading.Text = category.Selectable
            ? $"将要修改　{chosen}/{total} 项"
            : $"{category.Label}　{category.Rows.Count} {category.Unit}";
    }

    private UIElement BuildRow(ReviewRow row, bool selectable)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (selectable && row.Action is not null)
        {
            var box = new CheckBox
            {
                IsChecked = row.Item.Recommended,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Tag = row.Action,
            };
            box.Checked += (_, _) => Refresh();
            box.Unchecked += (_, _) => Refresh();
            row.Checkbox = box;
            Grid.SetColumn(box, 0);
            grid.Children.Add(box);
        }
        var text = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        if (row.Item.Line > 0)
        {
            var where = new TextBlock
            {
                Text = $"第 {row.Item.Line} 行",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            where.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            head.Children.Add(where);
        }
        var what = new TextBlock
        {
            Text = row.Item.Title,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        what.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        head.Children.Add(what);
        text.Children.Add(head);
        if (row.Item.Detail is { Length: > 0 } detail)
        {
            var line = new TextBlock
            {
                // "→ what it becomes" only where a change is actually on the table; a row nobody can
                // select states why it is listed instead.
                Text = (selectable ? "→ " : "") + detail,
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            text.Children.Add(line);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var border = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(8),
            Opacity = selectable ? 1.0 : 0.72,
            Child = grid,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush");
        return border;
    }

    private UIElement BuildFooter()
    {
        var panel = new DockPanel { Margin = new Thickness(24, 12, 24, 18), Name = "RepairFooterPanel" };
        _selectionText = new TextBlock { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
        _selectionText.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        DockPanel.SetDock(_selectionText, Dock.Left);
        panel.Children.Add(_selectionText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        // Kept from the old window: when the directory does not match this file, nothing is applied
        // until the user states that they checked it is the directory for this edition.
        if (_outcome.Report.Any(group => group.Label == "来源与文件对不上"))
        {
            _verified = new CheckBox { Content = "已核实参考目录确属本书此版本", Margin = new Thickness(0, 6, 16, 0), VerticalAlignment = VerticalAlignment.Center };
            _verified.Checked += (_, _) => Refresh();
            _verified.Unchecked += (_, _) => Refresh();
            buttons.Children.Add(_verified);
        }
        var close = new Button { Content = "暂不应用", MinWidth = 104, Padding = new Thickness(18, 9, 18, 9), IsCancel = true, Margin = new Thickness(0, 0, 10, 0) };
        close.Click += (_, _) => DialogResult = false;
        _applyButton = new Button { Content = "应用所选修改", MinWidth = 140, Padding = new Thickness(20, 9, 20, 9), IsDefault = true };
        _applyButton.SetResourceReference(BackgroundProperty, "PrimaryActionBrush");
        _applyButton.SetResourceReference(ForegroundProperty, "PrimaryActionTextBrush");
        _applyButton.SetResourceReference(BorderBrushProperty, "PrimaryActionBrush");
        _applyButton.Click += (_, _) =>
        {
            var chosen = SelectedActions();
            if (chosen.Count == 0) return;
            _apply(chosen);
            DialogResult = true;
        };
        buttons.Children.Add(close);
        buttons.Children.Add(_applyButton);
        panel.Children.Add(buttons);
        return panel;
    }

    private UIElement BuildSafetyBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var glyph = new TextBlock { Text = "\u2713", FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush");
        row.Children.Add(glyph);
        row.Children.Add(new TextBlock
        {
            Text = "原始 TXT 不变 · 应用前自动备份 · 应用后可以撤销",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var card = new Border
        {
            Padding = new Thickness(18, 12, 18, 12),
            Margin = new Thickness(0, 12, 0, 0),
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(1),
            Child = row,
        };
        card.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    private sealed class StatView(TextBlock number, Func<IReadOnlyDictionary<ReferenceActionKind, int>, int> value)
    {
        public TextBlock Number { get; } = number;
        public Func<IReadOnlyDictionary<ReferenceActionKind, int>, int> Value { get; } = value;
    }

    private sealed class CategoryView(
        string label, string unit, string summary,
        IReadOnlyList<ReviewRow> rows, bool selectable)
    {
        public string Label { get; } = label;
        public string Unit { get; } = unit;
        public string Summary { get; } = summary;
        public IReadOnlyList<ReviewRow> Rows { get; } = rows;
        public bool Selectable { get; } = selectable;
        public Border? Row { get; set; }
        public TextBlock? CountText { get; set; }

        /// <summary>
        /// "11/12" while something is still ticked, "0/12" once the user has cleared the category, and
        /// the plain total for the categories that hold nothing selectable. The fraction is the whole
        /// point: it is the only place that shows what cancelling a row did to the other categories.
        /// </summary>
        public string CountTextValue
        {
            get
            {
                var selected = Rows.Count(row => row.Checkbox?.IsChecked == true);
                return Selectable ? $"{selected}/{Rows.Count}" : $"{Rows.Count} {Unit}";
            }
        }
    }

    private sealed class ReviewRow(RepairReportItem item, ReferenceAction? action)
    {
        public RepairReportItem Item { get; } = item;
        public ReferenceAction? Action { get; } = action;
        public CheckBox? Checkbox { get; set; }
    }
}
