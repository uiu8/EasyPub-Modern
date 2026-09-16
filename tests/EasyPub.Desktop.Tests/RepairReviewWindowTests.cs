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
    private static async Task<(AutoRepairOutcome Outcome, IReadOnlyList<ChapterTreeEntry> Before)> RepairOutcomeAsync()
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
            return (outcome, document.Entries);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The tree the preview started from, which the window needs to describe the changes.</summary>
    private static RepairReviewWindow Build((AutoRepairOutcome Outcome, IReadOnlyList<ChapterTreeEntry> Before) subject) =>
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
                var expectedTotal = subject.Outcome.Report.SelectMany(group => group.Items).Count(item => item.Kind is not null);
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
    {
        var subject = await RepairOutcomeAsync();
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
                Assert.Contains($"{selection.Count(a => a.Kind == ReferenceActionKind.AddChapter)}\n补齐", text);
                Assert.Contains($"{selection.Count(a => a.Kind == ReferenceActionKind.DemoteExtra)}\n处并回", text);
                Assert.Contains($"{outcome.UnmatchedWithNumber}\n章需你核对", text);
                Assert.Contains($"{outcome.UnmatchedNoNumber}\n章疑似公告", text);

                // A category's own rows are drawn, so the wall of titles is behind a click rather than
                // in front of the numbers.
                Assert.Contains("标题并回正文", text);

                var footer = Named<Panel>(window, "RepairFooterPanel");
                var footerText = string.Concat(Descendants<TextBlock>(footer).Select(block => block.Text));
                Assert.Contains($"{selection.Count} 项", footerText);

                // Cancelling a whole category must move the figure it belongs to; the first screen is
                // the one thing this redesign is for, and a stale number there is worse than no number.
                foreach (var box in Descendants<CheckBox>(window).Where(box => box.IsChecked == true).ToArray())
                    box.IsChecked = false;
                window.UpdateLayout();
                var cleared = string.Concat(Descendants<TextBlock>(window).Select(block => block.Text + "\n"));
                Assert.Contains("0\n补齐", cleared);
                Assert.Contains("0\n处并回", cleared);
                // The two directory facts are not choices and must survive clearing everything.
                Assert.Contains($"{outcome.UnmatchedWithNumber}\n章需你核对", cleared);
                Assert.Contains($"{outcome.UnmatchedNoNumber}\n章疑似公告", cleared);

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
}
