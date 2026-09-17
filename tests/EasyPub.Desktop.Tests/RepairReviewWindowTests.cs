using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// The repair confirmation window used to be one read-only wall of text with no checkbox anywhere:
/// it applied <c>plan.DefaultSelection</c> the moment the user pressed the button, and the 264 chapter
/// titles pushed the actual changes hundreds of lines below the fold. These lock the replacement's
/// contract — every row can be cancelled, the numbers a reader decides with are on the first screen,
/// and the window can be resized instead of being pinned at one size.
/// </summary>
public class RepairReviewWindowTests
{
    /// <summary>
    /// A book that produces one of each kind of change: a chapter the directory has and the text does
    /// not (added), a heading the directory never numbered (folded back), a heading it simply does not
    /// list (kept until the user says otherwise), both halves of the mismatch, and a numbered entry
    /// with nothing behind it.
    /// </summary>
    private static async Task<(AutoRepairOutcome Outcome, IReadOnlyList<ChapterTreeEntry> Before, ChapterTreeDocument Document)> RepairOutcomeAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-review-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path,
            "第一章 起点\n正文一\n第二章 继续\n正文二\n" +
            "他忽然想起那年的雨\n这是正文段落，只是看起来像标题而已\n" +
            "番外 主角的人物卡\n卡片正文\n" +
            "第四章 断线\n正文四\n");
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var outcome = ChapterAutoRepair.Prepare(document,
                ReferenceCatalogInput.ParseText("第一章 起点\n第二章 继续\n第三章 收网\n第四章 断线\n请假一天"));
            return (outcome, document.Entries, document);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The tree the preview started from, which the window needs to describe the changes.</summary>
    private static RepairReviewWindow Build((AutoRepairOutcome Outcome, IReadOnlyList<ChapterTreeEntry> Before, ChapterTreeDocument Document) subject) =>
        new(subject.Outcome, subject.Before, _ => { });

    /// <summary>
    /// Named elements are found by walking the tree: this window is built in code, so its names never
    /// enter a name scope and <see cref="FrameworkElement.FindName"/> cannot resolve them.
    /// </summary>
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Descendants<T>(root).First(element => element.Name == name);

    [Fact]
    public async Task Every_selectable_row_starts_at_the_plans_own_default_and_maps_to_one_action()
    {
        var subject = await RepairOutcomeAsync();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = Build(subject);
                window.ShowInTaskbar = false;
                window.WindowStyle = WindowStyle.None;
                window.Opacity = 0;
                window.Show();
                window.UpdateLayout();

                var plan = subject.Outcome.Plan!;
                var boxes = Descendants<CheckBox>(window).ToArray();
                // Every change the plan can bring about is on screen, with the action behind it, so the
                // change list itself is what the user decides from — no category has to be opened first.
                var expectedTotal = subject.Outcome.Report.SelectMany(group => group.Items)
                    .Count(item => item.Selectable && item.Kind is not null);
                Assert.Equal(expectedTotal, boxes.Length);
                Assert.Equal(plan.DefaultSelection.Count(), boxes.Count(box => box.IsChecked == true));
                // Cancelling a row has to reach the action behind it, so the window exposes its own
                // reading of the checkboxes rather than re-deriving the selection at apply time.
                Assert.Equal(plan.DefaultSelection.ToHashSet(), window.SelectedActions().ToHashSet());

                // Unchecking one row drops exactly that action and nothing else.
                var first = boxes.First(box => box.IsChecked == true);
                first.IsChecked = false;
                window.UpdateLayout();
                Assert.Equal(plan.DefaultSelection.Count() - 1, window.SelectedActions().Count());

                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "窗口线程没有在 30 秒内结束");
        if (failure is not null) throw failure;
    }

    [Fact]
    public async Task The_window_can_be_resized_instead_of_being_pinned_to_one_size()
    {        var subject = await RepairOutcomeAsync();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = Build(subject);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.True(window.MinWidth >= 720, $"最小宽度只有 {window.MinWidth}，两栏会被挤坏");
                Assert.True(window.MinHeight >= 520, $"最小高度只有 {window.MinHeight}，明细区会看不见");
                // The detail pane holds the scroll, so the window itself must not size to its content:
                // 251 announcement titles would otherwise make it taller than the screen.
                Assert.Equal(SizeToContent.Manual, window.SizeToContent);
                Assert.True(double.IsNaN(window.Height) == false, "窗口没有初始高度");
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "窗口线程没有在 30 秒内结束");
        if (failure is not null) throw failure;
    }

    [Fact]
    public async Task The_figures_a_reader_decides_with_are_on_the_first_screen()
    {
        var subject = await RepairOutcomeAsync();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = Build(subject);
                window.ShowInTaskbar = false;
                window.WindowStyle = WindowStyle.None;
                window.Opacity = 0;
                window.Show();
                window.UpdateLayout();

                // Paired, not bare digits: "3" alone matches the line number of any row and would pass
                // even if the figures were missing altogether.
                var text = string.Concat(Descendants<TextBlock>(window).Select(block => block.Text + "\n"));
                var outcome = subject.Outcome;
                var selection = window.SelectedActions();
                Assert.Contains($"已对齐 {outcome.AlignedCount} 章", text);
                // Read by name: the figure and its caption are separate elements, so pairing them by
                // string concatenation was only ever a coincidence of how the layout stacked them.
                Assert.Equal(selection.Count(a => a.Kind == ReferenceActionKind.AddChapter) + " 章", Stat(window, "补齐漏识别"));
                Assert.Equal(selection.Count(a => a.Kind == ReferenceActionKind.DemoteExtra) + " 处", Stat(window, "标题并回正文"));
                Assert.Equal(selection.Count(a => a.Kind == ReferenceActionKind.DemoteExtra) + " 处", Stat(window, "标题并回正文"));
                Assert.Equal(outcome.UnmatchedWithNumber + " 章", Stat(window, "待你核对"));
                Assert.Equal(outcome.UnmatchedNoNumber + " 章", Stat(window, "疑似公告"));

                // The change list is on the left, in full: every kind of change the repair can make is
                // named there, so the reader does not have to open categories to find out what changed.
                var labels = window.CategoryLabels();
                Assert.Contains("建立卷层级", labels);
                Assert.Contains("收录章节", labels);
                Assert.Contains("标题改按目录写法", labels);

                var footer = Named<Panel>(window, "RepairFooterPanel");
                var footerText = string.Concat(Descendants<TextBlock>(footer).Select(block => block.Text));
                Assert.Contains($"{selection.Count} 项", footerText);

                // Cancelling a whole category must move the figure it belongs to; the first screen is
                // the one thing this redesign is for, and a stale number there is worse than no number.
                foreach (var box in Descendants<CheckBox>(window).Where(box => box.IsChecked == true).ToArray())
                    box.IsChecked = false;
                window.UpdateLayout();
                Assert.Equal("0 章", Stat(window, "补齐漏识别"));
                Assert.Equal("0 处", Stat(window, "标题并回正文"));
                // The two directory facts are not choices and must survive clearing everything.
                Assert.Equal(outcome.UnmatchedWithNumber + " 章", Stat(window, "待你核对"));
                Assert.Equal(outcome.UnmatchedNoNumber + " 章", Stat(window, "疑似公告"));
                // And the change list says so too, rather than keeping its opening figures.
                var cleared = string.Concat(Descendants<TextBlock>(window).Select(block => block.Text + "\n"));
                Assert.Contains("未选任何修改", cleared);

                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "窗口线程没有在 30 秒内结束");
        if (failure is not null) throw failure;
    }

    [Fact]
    public async Task The_sidebar_shows_how_much_of_each_category_is_still_selected()
    {
        var subject = await RepairOutcomeAsync();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = Build(subject);
                window.ShowInTaskbar = false;
                window.WindowStyle = WindowStyle.None;
                window.Opacity = 0;
                window.Show();
                window.UpdateLayout();

                var list = Named<Panel>(window, "RepairCategoryList");
                var before = string.Concat(Descendants<TextBlock>(list).Select(block => block.Text + "|"));

                var boxes = Descendants<CheckBox>(window).ToArray();
                var first = boxes.First(box => box.IsChecked == true);
                first.IsChecked = false;
                window.UpdateLayout();

                var after = string.Concat(Descendants<TextBlock>(list).Select(block => block.Text + "|"));
                // Cancelling a row has to be visible in the sidebar, which is the only place that
                // shows all categories at once.
                Assert.NotEqual(before, after);

                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "窗口线程没有在 30 秒内结束");
        if (failure is not null) throw failure;
    }

    /// <summary>
    /// One figure from the header, found by the name of its block. The number and its caption are
    /// separate elements, so this reads the number rather than pattern-matching two texts together.
    /// </summary>
    private static string Stat(DependencyObject window, string label)
    {
        var block = Named<Panel>(window, "Stat_" + label);
        return Descendants<TextBlock>(block).First().Text;
    }

    [Fact]
    public async Task A_group_the_alignment_does_by_itself_shows_a_count_not_a_fraction()
    {
        var subject = await RepairOutcomeAsync();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = Build(subject);
                window.ShowInTaskbar = false;
                window.WindowStyle = WindowStyle.None;
                window.Opacity = 0;
                window.Show();
                window.UpdateLayout();

                var list = Named<Panel>(window, "RepairCategoryList");
                var examined = 0;
                foreach (var group in list.Children.OfType<Panel>())
                {
                    if (group.Children.Count < 2 || group.Children[0] is not Border header || group.Children[1] is not Panel body) continue;
                    var texts = Descendants<TextBlock>((Grid)header.Child).Select(block => block.Text).ToArray();
                    var count = texts.LastOrDefault() ?? "";
                    var tickable = Descendants<CheckBox>(body).Any();
                    if (tickable || count.Length == 0 || count == "0") continue;
                    examined++;
                    // "0/2" beside the automatic volume level reads as "none of these will happen", when
                    // in fact both of them will: there is no box to tick, so there is no fraction to show.
                    Assert.DoesNotContain("/", count);
                }
                Assert.True(examined > 0, "没有检查到任何「自动」类分组，测试没有实际断言");

                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "窗口线程没有在 30 秒内结束");
        if (failure is not null) throw failure;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    /// <summary>
    /// The same walk over the logical tree. The list of changes is deep — a group inside a scroller
    /// inside a grid — and whether WPF has realised every nested panel in the visual tree at the moment
    /// a test looks is not what these tests are about; the content is.
    /// </summary>
    private static IEnumerable<T> LogicalDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T match) yield return match;
            foreach (var nested in LogicalDescendants<T>(node)) yield return nested;
        }
    }

    /// <summary>
    /// 传了 <c>previewDocument</c> 与不传，必须画出同一份清单。
    ///
    /// 这条测试的来历：确认窗在「传了 previewDocument」这条路径上整片空白 —— 标题写
    /// 「本次没有需要应用的改动」，每一类都是 0，而同一屏的页眉刚刚数出 437 章已对齐。
    /// 原因是 <c>PreviewChanges()</c> 从复选框读当前选择，而构造函数第一次调它的时候复选框
    /// 还没建出来，于是选择读成空、清单也就建成了空的。
    ///
    /// 生产代码**始终**传 previewDocument（<c>RunRepairReviewAsync</c> 传 <c>snapshot</c>），
    /// 而在此之前每一处测试都不传 —— 这个分支从来没有被看过一眼。这条断言把两条路径绑在一起：
    /// 谁再让它们分叉，这里立刻红。
    /// </summary>
    [Fact]
    public async Task A_preview_document_draws_the_same_change_list()
    {
        var subject = await RepairOutcomeAsync();
        var before = subject.Before;
        Exception? failure = null;
        var rows = new Dictionary<string, int>();
        var selected = new Dictionary<string, int>();
        var thread = new Thread(() =>
        {
            try
            {
                void Measure(string label, ChapterTreeDocument? preview)
                {
                    var window = preview is null
                        ? new RepairReviewWindow(subject.Outcome, before, _ => { })
                        : new RepairReviewWindow(subject.Outcome, before, _ => { }, preview);
                    window.ShowInTaskbar = false;
                    window.WindowStyle = WindowStyle.None;
                    window.Opacity = 0;
                    window.Show();
                    window.UpdateLayout();
                    rows[label] = LogicalDescendants<CheckBox>(window).Count();
                    selected[label] = window.SelectedActions().Count;
                    window.Close();
                }

                Measure("with", subject.Document);
                Measure("without", null);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "窗口线程没有在 60 秒内结束");
        if (failure is not null) throw failure;

        Assert.True(rows["without"] > 0, "不传 previewDocument 时本来就没有待勾选行，测试样本失效");
        Assert.Equal(rows["without"], rows["with"]);
        Assert.Equal(selected["without"], selected["with"]);
    }
}


