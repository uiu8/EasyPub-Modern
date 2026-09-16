using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// What one repair will change, built for a reader who has to decide about it.
///
/// The window it replaces was one read-only TextBox: the verdict, then every unmatched reference title,
/// then the categorised account, and only then the list of changes — with 264 chapter titles in between,
/// and no way to strike anything out. Worse, the list of what actually changed sat at the bottom of the
/// right pane, below the detail rows, which is the one place nobody scrolls to; a reader who wanted to
/// know "what is this going to do to my book" had to pick a category, scroll past its rows, and read a
/// summary that only described that category.
///
/// Here the change list *is* the left column: one group per kind of change, and under each group every
/// change of that kind with its own checkbox. So the column that lists the changes is the column that
/// decides them, and a tick is visible against the change it governs rather than only against the
/// proposal that produced it. The right column shows the one change the reader is looking at, or — until
/// they pick one — what the repair is about to do, in figures.
///
/// The two directory facts that are not changes (the entries the directory lists and the text lacks, and
/// the ones that look like the aggregator's leave notices) sit under their own heading at the bottom of
/// the list, so a list of the changes is not mistaken for a list of everything known.
/// </summary>
public sealed class RepairReviewWindow : Window
{
    /// <summary>
    /// A leave notice is never something to go looking for, so these two are not categories to act in:
    /// their counts belong in the header, where the split between them does its work.
    /// </summary>
    private static readonly string[] HeaderOnlyLabels = ["参考目录未匹配", "疑似公告"];

    /// <summary>
    /// The groups of the change list, in the order the repair works in: the kinds an action drives, then
    /// the kinds the alignment does by itself, then the entries that only leave the tree. Every group
    /// exists whether or not it is producing changes right now, so the list describes the whole repair
    /// instead of rearranging itself as boxes are ticked; a group with nothing in it says so rather than
    /// vanishing.
    /// </summary>
    private static readonly (ReferenceActionKind?, RepairChangeKind)[] GroupKeys =
    [
        (ReferenceActionKind.AddChapter, RepairChangeKind.AddedChapter),
        (ReferenceActionKind.Retitle, RepairChangeKind.RetitledChapter),
        (ReferenceActionKind.DemoteExtra, RepairChangeKind.RemovedEntry),
        (ReferenceActionKind.KeepExtra, RepairChangeKind.RemovedEntry),
        (ReferenceActionKind.RemoveDuplicate, RepairChangeKind.RemovedDuplicate),
        (null, RepairChangeKind.RebuiltVolume),
        (null, RepairChangeKind.ChangedLevel),
        (null, RepairChangeKind.ReassignedBody),
        (null, RepairChangeKind.Reordered),
        (null, RepairChangeKind.RemovedLines),
        (null, RepairChangeKind.RemovedEntry),
    ];

    private readonly AutoRepairOutcome _outcome;
    private readonly IReadOnlyList<ChapterTreeEntry> _before;
    private readonly ChapterTreeDocument? _previewDocument;
    private readonly Action<IReadOnlyCollection<ReferenceAction>> _apply;
    private readonly Dictionary<string, ReferenceAction> _actionByKey = new(StringComparer.Ordinal);
    private readonly List<StatView> _stats = [];
    private readonly TextBlock _summaryTitle = new() { FontSize = 17, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _summaryDetail = new() { FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) };

    /// <summary>
    /// The change list, one entry per kind of change, in the order the repair works in. The groups are
    /// built once; what is inside them is rebuilt on every tick, because that is the part the answer
    /// changes for. A change that was already listed keeps its row — and its checkbox — across a
    /// refresh: the tick is the user's decision, and the refresh is something the tick itself caused.
    /// </summary>
    private readonly List<ChangeKindView> _changeKinds = [];
    private readonly Dictionary<(ReferenceActionKind?, RepairChangeKind), List<ReviewRow>> _rowsByKind = [];
    private readonly List<ReviewRow> _rows = [];
    private readonly Dictionary<string, ReviewRow> _rowByKey = new(StringComparer.Ordinal);

    private Panel _categoryList = null!;
    private Panel _factList = null!;
    private ScrollViewer _detail = null!;
    private TextBlock _selectionText = null!;
    private Button _applyButton = null!;
    private CheckBox? _verified;
    private ReviewRow? _focused;
    private bool _building;

    public RepairReviewWindow(AutoRepairOutcome outcome, IReadOnlyList<ChapterTreeEntry> before,
        Action<IReadOnlyCollection<ReferenceAction>> apply, ChapterTreeDocument? previewDocument = null)
    {
        _outcome = outcome;
        _before = before;
        _apply = apply;
        _previewDocument = previewDocument;
        if (outcome.Plan is not null)
            foreach (var action in outcome.Plan.Actions) _actionByKey[action.Key] = action;
        Title = "目录修复 · 核对后应用";
        Name = "RepairReviewWindow";
        Width = 1080;
        Height = 780;
        MinWidth = 760;
        MinHeight = 560;
        // The list owns the scrolling, so the window must not grow to fit 251 announcement titles; and it
        // resizes, because how much of a long book one wants to see at once is a taste.
        ResizeMode = ResizeMode.CanResize;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        foreach (var key in GroupKeys)
        {
            var rows = new List<ReviewRow>();
            _rowsByKind[key] = rows;
            _changeKinds.Add(new ChangeKindView(key, rows));
        }
        Content = BuildLayout();
        BuildChangeList();
        // The rows have to exist before the first Refresh asks them what is ticked: they are the only
        // record of the user's choice, so a refresh that ran first would count an empty selection and
        // then have nothing left to correct it with — the boxes are built from it, not the other way.
        RefreshRows(PreviewChanges());
        Refresh();
    }

    /// <summary>
    /// The actions the user left ticked. Read from the checkboxes themselves rather than from a parallel
    /// set: there is one place a tick can live, so the button and the numbers cannot disagree with what
    /// is on screen.
    /// </summary>
    public IReadOnlyCollection<ReferenceAction> SelectedActions() =>
        _rows.Where(row => row.Checkbox?.IsChecked == true).Select(row => row.Action!).ToArray();

    /// <summary>The groups of the change list, top to bottom. Stable for tests.</summary>
    public IReadOnlyList<string> CategoryLabels() =>
        _categoryList.Children.OfType<FrameworkElement>().Select(element => element.Tag as string ?? "").ToArray();
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
        (string Label, Func<IReadOnlyCollection<ReferenceAction>, int> Value, string Brush, string? Note)[] blocks =
        [
            ("补齐", chosen => chosen.Count(action => action.Kind == ReferenceActionKind.AddChapter), "PrimaryTextBrush", null),
            ("处并回", chosen => chosen.Count(action => action.Kind == ReferenceActionKind.DemoteExtra), "PrimaryTextBrush", null),
            ("章需你核对", _ => _outcome.UnmatchedWithNumber, "WarningBrush", null),
            ("章疑似公告", _ => _outcome.UnmatchedNoNumber, "SecondaryTextBrush", "通常无需处理"),
        ];
        foreach (var (label, value, brush, note) in blocks)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 0, 40, 0), Name = "Stat_" + label };
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
        // Two areas in one column: the changes, which are re-ordered as the counts move, and the
        // directory facts below them, which never move.
        _categoryList = new StackPanel { Name = "RepairCategoryList" };
        _factList = new StackPanel { Name = "RepairFactList" };
        var area = new StackPanel();
        area.Children.Add(_categoryList);
        area.Children.Add(_factList);
        var sidebar = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(10, 12, 10, 12),
            Content = area,
        };
        _detail = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(20, 16, 14, 16),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.35, GridUnitType.Star), MinWidth = 380 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 280 });
        Grid.SetColumn(sidebar, 0);
        grid.Children.Add(sidebar);
        var separator = new Border { BorderThickness = new Thickness(1, 0, 0, 0) };
        separator.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        Grid.SetColumn(separator, 1);
        grid.Children.Add(separator);
        Grid.SetColumn(_detail, 2);
        grid.Children.Add(_detail);
        var card = new Border { CornerRadius = new CornerRadius(11), BorderThickness = new Thickness(1), Child = grid };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        return card;
    }

    /// <summary>
    /// Builds the change list: one group per kind of change the repair can produce, so the list is a
    /// description of the whole repair rather than of whatever the current selection happens to leave.
    /// A group with nothing in it is dimmed and pushed below the ones that are happening, but it stays:
    /// "标题改按目录写法 0 章" is an answer, and a list that hides it makes the reader wonder.
    /// </summary>
    private void BuildChangeList()
    {
        _categoryList.Children.Clear();
        foreach (var view in _changeKinds)
        {
            var group = new StackPanel { Tag = view.Name, Name = $"ChangeGroup_{view.Key.Item1}_{view.Key.Item2}" };
            view.Container = group;
            var header = BuildGroupHeader(view);
            group.Children.Add(header);
            view.Header = header;
            var body = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            group.Children.Add(body);
            view.Body = body;
            _categoryList.Children.Add(group);
        }

        // Readable but not selectable: nothing in them can be applied, and the numbered half is exactly
        // what the header asks the user to check.
        var facts = _outcome.Report.Where(group => HeaderOnlyLabels.Contains(group.Label)).ToArray();
        if (facts.Length == 0) return;
        var rule = new Border { Height = 1, Margin = new Thickness(2, 12, 2, 10) };
        rule.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
        _factList.Children.Add(rule);
        var caption = new TextBlock
        {
            Text = "目录本身的情况　不会改动书稿",
            FontSize = 11,
            Margin = new Thickness(4, 0, 0, 6),
            Opacity = 0.8,
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        _factList.Children.Add(caption);
        foreach (var group in facts)
            _factList.Children.Add(BuildFactRow(group));
    }

    private Border BuildGroupHeader(ChangeKindView view)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
        };
        row.Children.Add(glyph);
        view.Glyph = glyph;
        var name = new TextBlock
        {
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        name.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        view.NameText = name;
        var count = new TextBlock
        {
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        Grid.SetColumn(count, 2);
        row.Children.Add(count);
        view.CountText = count;

        var border = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(7),
            Child = row,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush");
        border.MouseLeftButtonUp += (_, _) => ShowGroupOverview(view);
        return border;
    }

    /// <summary>
    /// The changes of one kind as the current selection would produce them. Each change is matched back
    /// to the action that caused it by what the plan itself said — the window used to guess the action
    /// from the line number, and the guess failed on exactly the rows the user had to decide about,
    /// leaving them with no checkbox and no way to reach them.
    /// </summary>
    private void RefreshRows(IReadOnlyList<RepairChange> changes)
    {
        foreach (var rows in _rowsByKind.Values) rows.Clear();
        _rows.Clear();
        foreach (var change in changes)
        {
            if (!_rowsByKind.TryGetValue(ChangeGroupKey(change), out var rows)) continue;
            var action = change.ActionKey is { } key ? _actionByKey.GetValueOrDefault(key) : null;
            // Keyed by the action where there is one: two changes can share an action — an entry that
            // folded back into the body is both the entry leaving the tree and the body line it left
            // behind — and both rows must read the same decision.
            var rowKey = action?.Key ?? $"auto|{change.Kind}|{change.Line}|{change.Title}";
            var row = _rowByKey.GetValueOrDefault(rowKey);
            if (row is null)
            {
                row = new ReviewRow(change, action);
                _rowByKey[rowKey] = row;
            }
            rows.Add(row);
            _rows.Add(row);
        }
    }

    /// <summary>
    /// Draws the rows of every group, then puts the groups that are happening above the ones that are
    /// not. Rows are created once per change and then only moved or relabelled: rebuilding a checkbox
    /// would recreate it from <see cref="ReferenceAction.Recommended"/> and silently undo the
    /// cancellation that triggered the refresh.
    /// </summary>
    private void RedrawRows()
    {
        foreach (var view in _changeKinds)
        {
            view.Body!.Children.Clear();
            foreach (var row in view.Rows) view.Body.Children.Add(BuildRow(row));
        }
        _categoryList.Children.Clear();
        foreach (var view in _changeKinds
                     .OrderBy(view => view.Rows.Count == 0 ? 1 : 0)
                     .ThenBy(view => GroupOrder(view.Key)))
        {
            if (view.Container is not null) _categoryList.Children.Add(view.Container);
        }
    }

    /// <summary>
    /// One change, with a checkbox when the plan names the action behind it. The checkbox is what makes
    /// cancelling real: it carries the action itself, so unchecking it removes that action from what the
    /// button will apply. A change with no action gets no box and says "自动" instead — the alignment
    /// does it whatever the user ticks.
    /// </summary>
    private Border BuildRow(ReviewRow row)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (row.Action is { } action)
        {
            // The checkbox outlives the row it was drawn in, so it may still be attached to the previous
            // one: detach before re-adding, or the tree refuses the duplicate parent.
            var box = row.Checkbox ??= BuildCheckbox(action);
            if (box.Parent is Panel previous) previous.Children.Remove(box);
            Grid.SetColumn(box, 0);
            grid.Children.Add(box);
        }
        else
        {
            var marker = new TextBlock
            {
                Text = "自动",
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Opacity = 0.75,
            };
            marker.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            Grid.SetColumn(marker, 0);
            grid.Children.Add(marker);
        }

        var text = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        if (row.Change.Line > 0)
        {
            var where = new TextBlock
            {
                Text = $"第 {row.Change.Line} 行",
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            };
            where.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
            head.Children.Add(where);
        }
        var what = new TextBlock
        {
            Text = row.Change.Title,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };
        what.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        head.Children.Add(what);
        text.Children.Add(head);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var border = new Border
        {
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(7),
            Opacity = row.Action is not null ? 1.0 : 0.7,
            Child = grid,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush");
        border.MouseLeftButtonUp += (_, _) =>
        {
            _focused = row;
            ShowDetail(row);
        };
        return border;
    }

    /// <summary>
    /// The checkbox for one change. It starts at the plan's own recommendation and then belongs to the
    /// user: the element is kept on the row and reused, so a tick made here is never recreated from the
    /// plan by a later refresh.
    /// </summary>
    private CheckBox BuildCheckbox(ReferenceAction action)
    {
        var box = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            Tag = action.Key,
        };
        // The recommendation is applied after the handlers are wired, and under the build flag: setting
        // IsChecked raises Checked, and a refresh started from inside the construction of the very rows
        // it would rebuild is a refresh that runs on half-built state. That is how the header once kept
        // the figures it had computed for an empty selection while the boxes on screen were ticked.
        var start = action.Recommended;
        box.Checked += (_, _) => { if (!_building) Refresh(); };
        box.Unchecked += (_, _) => { if (!_building) Refresh(); };
        _building = true;
        try { box.IsChecked = start; }
        finally { _building = false; }
        return box;
    }

    private Border BuildFactRow(RepairReportGroup group)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var glyph = new TextBlock
        {
            Text = "\u25CB",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        row.Children.Add(glyph);
        var name = new TextBlock
        {
            Text = group.Label,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);
        var count = new TextBlock
        {
            Text = $"{group.Items.Count} {group.Unit}",
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        count.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        Grid.SetColumn(count, 2);
        row.Children.Add(count);
        var border = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 2),
            CornerRadius = new CornerRadius(7),
            Child = row,
            Opacity = 0.85,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush");
        border.MouseLeftButtonUp += (_, _) => ShowFacts(group);
        return border;
    }

    /// <summary>
    /// The right column. Until the reader picks something it says what the repair is about to do, in the
    /// figures the header leads with; picking a change, a group or a directory fact replaces it with that
    /// one thing explained.
    /// </summary>
    private void ShowOverview()
    {
        _focused = null;
        var stack = new StackPanel();
        var heading = new TextBlock
        {
            Text = "本次修复将做什么",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
            TextWrapping = TextWrapping.Wrap,
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        stack.Children.Add(heading);
        stack.Children.Add(Paragraph("左栏是这次修复的完整改动清单：每一组是一类改动，每一行是一项，" +
            "行前的方框决定它是否执行。取消勾选的不会执行，数字会跟着变。"));
        stack.Children.Add(Paragraph("标注「自动」的行没有方框：它们是修复过程中必然发生的连带改动（例如把章归入卷），" +
            "无法单独取消。"));
        var groups = _changeKinds.Where(view => view.Rows.Count > 0).ToArray();
        if (groups.Length == 0)
        {
            stack.Children.Add(Paragraph("按当前勾选，章节树不会改变。"));
            _detail.Content = stack;
            return;
        }
        foreach (var view in groups)
        {
            var line = new TextBlock
            {
                Text = $"· {view.NameText?.Text}　{view.Rows.Count} {view.Unit}",
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap,
            };
            line.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            stack.Children.Add(line);
        }
        var note = Paragraph("点左栏任意一行，这里会说明那一项具体改了什么。");
        note.Margin = new Thickness(0, 10, 0, 0);
        stack.Children.Add(note);
        _detail.Content = stack;
    }

    private TextBlock Paragraph(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        return block;
    }

    /// <summary>One change, explained: where it is, what the plan said about it, and what it will do.</summary>
    private void ShowDetail(ReviewRow row)
    {
        var stack = new StackPanel();
        var kind = _changeKinds.First(view => view.Key == ChangeGroupKey(row.Change));
        var heading = new TextBlock
        {
            Text = row.Change.Title,
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap,
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        stack.Children.Add(heading);
        var category = new TextBlock
        {
            Text = kind.Name + "　" + (row.Change.Line > 0 ? $"原文第 {row.Change.Line} 行" : "不指向具体原文行"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap,
        };
        category.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
        stack.Children.Add(category);
        if (row.Change.Detail is { Length: > 0 } detail) stack.Children.Add(Paragraph(detail));
        stack.Children.Add(Paragraph(BlurbOf(ChangeGroupKey(row.Change))));
        stack.Children.Add(Paragraph(row.Action is null
            ? "这一项没有可取消的选项：它由本次对齐自动完成。"
            : $"这一项来自参考目录的一条计划：{row.Action.Detail}"));
        _detail.Content = stack;
    }

    /// <summary>The list of one directory fact, which is information and never a choice.</summary>
    private void ShowFacts(RepairReportGroup group)
    {
        _focused = null;
        var stack = new StackPanel();
        var heading = new TextBlock
        {
            Text = $"{group.Label}　{group.Items.Count} {group.Unit}",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        stack.Children.Add(heading);
        stack.Children.Add(Paragraph(group.Summary));
        foreach (var item in group.Items)
        {
            var text = new StackPanel();
            var head = new TextBlock
            {
                Text = item.Title,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            };
            head.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
            text.Children.Add(head);
            if (item.Detail is { Length: > 0 } detail)
            {
                var line = new TextBlock { Text = detail, FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
                line.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryTextBrush");
                text.Children.Add(line);
            }
            var border = new Border
            {
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 6),
                CornerRadius = new CornerRadius(8),
                Opacity = 0.8,
                Child = text,
            };
            border.SetResourceReference(Border.BackgroundProperty, "SubtleSurfaceBrush");
            stack.Children.Add(border);
        }
        _detail.Content = stack;
    }

    private void ShowGroupOverview(ChangeKindView view)
    {
        _focused = null;
        var stack = new StackPanel();
        var heading = new TextBlock
        {
            Text = $"{view.Name}　{view.Rows.Count} {view.Unit}",
            FontSize = 14.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
        };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryTextBrush");
        stack.Children.Add(heading);
        stack.Children.Add(Paragraph(BlurbOf(view.Key)));
        if (view.Rows.Count == 0) stack.Children.Add(Paragraph("这一类改动目前不会发生。"));
        _detail.Content = stack;
    }

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
        // The plan is handed over so every change can name the action that caused it, instead of the
        // window guessing from line numbers.
        return _outcome.Plan is null
            ? RepairIntegrity.Describe(after, _before)
            : RepairIntegrity.DescribeWithActions(_outcome.Plan, after, _before);
    }

    /// <summary>
    /// Re-reads the changes and the numbers. The groups keep their identity, and every row that is still
    /// there keeps its checkbox: the only things that change are the contents of the groups, the counts,
    /// and the order the groups are shown in.
    /// </summary>
    private void Refresh()
    {
        var changes = PreviewChanges();
        RefreshRows(changes);
        RedrawRows();

        var total = 0;
        foreach (var view in _changeKinds)
        {
            var tickable = view.Rows.Count(row => row.Action is not null);
            total += tickable;
            view.NameText!.Text = view.Name;
            view.CountText!.Text = view.Rows.Count == 0
                ? "0"
                // A fraction only where there is something to choose: "0/2" beside the automatic volume
                // level reads as "none of these will happen", when in fact both of them will.
                : tickable > 0 ? $"{view.SelectedCount}/{view.Rows.Count}" : $"{view.Rows.Count} {view.Unit}";
            view.Glyph!.Text = view.Rows.Count == 0 ? "\u25CB" : tickable > 0 ? "\u2713" : "\u25CF";
            view.Glyph.SetResourceReference(TextBlock.ForegroundProperty,
                view.Rows.Count == 0 ? "SecondaryTextBrush" : tickable > 0 ? "SuccessBrush" : "WarningBrush");
            view.Container!.Opacity = view.Rows.Count == 0 ? 0.45 : 1.0;
        }

        // Read after the rows are drawn, not before: the boxes are the only record of what is selected,
        // and a box that did not exist when the selection was read is a selection counted as empty. That
        // is how the header came to show zeros beside a list of ticked changes.
        var chosen = SelectedActions();
        for (var index = 0; index < _stats.Count; index++)
            _stats[index].Number.Text = _stats[index].Value(chosen).ToString();

        var kindsInPlay = _changeKinds.Count(view => view.Rows.Count > 0);
        _summaryTitle.Text = total == 0
            ? "本次没有需要应用的改动"
            : $"本次将做 {kindsInPlay} 类修改，共 {total} 项";
        _summaryDetail.Text = $"参考目录 {_outcome.ReferenceChapters} 章 · 已对齐 {_outcome.AlignedCount} 章 · " +
            $"保留目录外 {_outcome.Extra} 章 · 未匹配 {_outcome.Missing} 章";

        if (_focused is not null && _rows.Contains(_focused)) ShowDetail(_focused);
        else ShowOverview();

        _selectionText.Text = chosen.Count == 0
            ? (total == 0 ? "本次没有需要应用的改动" : "未选任何修改 · 全部保持不变")
            : $"已选 {chosen.Count} 项 · 取消勾选的不会执行";
        _applyButton.IsEnabled = chosen.Count > 0 && (_verified?.IsChecked ?? true);
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

    private sealed class StatView(TextBlock number, Func<IReadOnlyCollection<ReferenceAction>, int> value)
    {
        public TextBlock Number { get; } = number;
        public Func<IReadOnlyCollection<ReferenceAction>, int> Value { get; } = value;
    }

    /// <summary>
    /// Which group of the change list a change belongs to.
    ///
    /// The kind of change is not enough on its own: an entry leaving the tree is a
    /// <see cref="RepairChangeKind.RemovedEntry"/> whether the plan folded its heading back into the body
    /// or dropped a duplicate copy, and those are two different things to decide about. The action kind
    /// says which, so it leads — and when there is no action the change kind does.
    /// </summary>
    private static (ReferenceActionKind?, RepairChangeKind) ChangeGroupKey(RepairChange change) =>
        (change.ActionKind, change.Kind);

    /// <summary>One group of the change list: a kind of change, its heading, and the rows it holds.</summary>
    private sealed class ChangeKindView(
        (ReferenceActionKind?, RepairChangeKind) key, List<ReviewRow> rows)
    {
        public (ReferenceActionKind?, RepairChangeKind) Key { get; } = key;
        public string Name { get; } = LabelOf(key);
        public string Unit { get; } = UnitOf(key);
        public List<ReviewRow> Rows { get; } = rows;
        public StackPanel? Container { get; set; }
        public Border? Header { get; set; }
        public StackPanel? Body { get; set; }
        public TextBlock? Glyph { get; set; }
        public TextBlock? NameText { get; set; }
        public TextBlock? CountText { get; set; }

        public int SelectedCount => Rows.Count(row => row.Checkbox?.IsChecked == true);
    }

    /// <summary>The position of a group in the change list, used to keep it there as counts move.</summary>
    private static int GroupOrder((ReferenceActionKind?, RepairChangeKind) key) => Array.IndexOf(GroupKeys, key);

    /// <summary>The heading of one group of the change list.</summary>
    private static string LabelOf((ReferenceActionKind?, RepairChangeKind) key) => key switch
    {
        (ReferenceActionKind.AddChapter, _) => "收录章节",
        (ReferenceActionKind.Retitle, _) => "标题改按目录写法",
        (ReferenceActionKind.DemoteExtra, _) => "标题并回正文",
        (ReferenceActionKind.RemoveDuplicate, _) => "移除重复正文",
        (ReferenceActionKind.KeepExtra, _) => "保留目录外章节",
        (ReferenceActionKind.VolumeNote, _) => "卷结构提示",
        (null, RepairChangeKind.RebuiltVolume) => "建立卷层级",
        (null, RepairChangeKind.ChangedLevel) => "章节归入卷层级",
        (null, RepairChangeKind.ReassignedBody) => "正文重新分配",
        (null, RepairChangeKind.Reordered) => "位置调整",
        (null, RepairChangeKind.RemovedLines) => "从成品移除原文",
        (null, RepairChangeKind.RemovedEntry) => "移除章节项",
        (null, RepairChangeKind.FoldedIntoBody) => "标题并回正文",
        (null, RepairChangeKind.AddedChapter) => "收录章节",
        (null, RepairChangeKind.RetitledChapter) => "标题改按目录写法",
        (null, RepairChangeKind.RemovedDuplicate) => "移除重复正文",
        _ => "其他改动",
    };

    /// <summary>The unit one change of this kind is counted in, for the "2 卷 / 14 章" fraction.</summary>
    private static string UnitOf((ReferenceActionKind?, RepairChangeKind) key) => key switch
    {
        (null, RepairChangeKind.RebuiltVolume) => "卷",
        (null, RepairChangeKind.RemovedLines) => "行",
        (null, RepairChangeKind.RemovedDuplicate or RepairChangeKind.ReassignedBody or RepairChangeKind.Reordered) => "处",
        (ReferenceActionKind.RemoveDuplicate or ReferenceActionKind.DemoteExtra, _) => "处",
        _ => "章",
    };

    /// <summary>What this kind of change means, said plainly, in the pane beside it.</summary>
    private static string BlurbOf((ReferenceActionKind?, RepairChangeKind) key) => key switch
    {
        (ReferenceActionKind.AddChapter, _) => "目录里有、原文中也找到了，勾选后收录为章节。取消勾选的不会收录。",
        (ReferenceActionKind.Retitle, _) => "章已存在，标题改用参考目录的写法。原文文字不变，只改标题。",
        (ReferenceActionKind.DemoteExtra, _) => "这些标题不再算章节，其下文字全部并回正文，一个字都不会丢。",
        (ReferenceActionKind.RemoveDuplicate, _) => "同一章在原文出现多次而目录只列一次。只保留与目录位置一致的一份，移除的每一行都会留档。",
        (ReferenceActionKind.KeepExtra, _) => "目录没有列出这些章节。默认保留；勾选会把标题并回正文。",
        (ReferenceActionKind.VolumeNote, _) => "参考目录的卷结构在原文里落不了地，仅作提示。",
        (null, RepairChangeKind.RebuiltVolume) => "原文里靠章号重新计数判断出来的分卷，已建成卷层级。正文一行未动，无法单独取消。",
        (null, RepairChangeKind.ChangedLevel) => "章在章节树里的层级变了（归入卷，或从卷里退出来）。这是其他修改的连带结果，无法单独取消。",
        (null, RepairChangeKind.ReassignedBody) => "条目还在，但它覆盖的正文范围换了。原文没有任何一行丢失。",
        (null, RepairChangeKind.Reordered) => "条目在章节树里的顺序变了，正文内容不变。",
        (null, RepairChangeKind.RemovedLines) => "这些原文行不进成品，已单独留档，可随时撤销。",
        (null, RepairChangeKind.RemovedEntry) => "这些条目会从章节树里移除。正文是否随之移除，以「从成品移除原文」那一组为准。",
        (null, RepairChangeKind.FoldedIntoBody) => "这些标题不再算章节，其下文字全部并回正文，一个字都不会丢。",
        _ => "这一类改动目前没有发生。",
    };

    private sealed class ReviewRow(RepairChange change, ReferenceAction? action)
    {
        public RepairChange Change { get; } = change;
        public ReferenceAction? Action { get; } = action;
        public CheckBox? Checkbox { get; set; }
    }
}
