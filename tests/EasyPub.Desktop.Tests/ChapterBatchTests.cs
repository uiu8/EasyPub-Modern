using System.IO;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using EasyPub.Desktop;
using System.Runtime.ExceptionServices;

namespace EasyPub.Desktop.Tests;

public class ChapterBatchTests
{
    [Fact]
    public async Task Empty_tree_offers_book_only_numeric_recognition_and_undo()
    {
        await Run((window, document, path) =>
        {
            var button = (Button)window.FindName("ReviewActionButton");
            Assert.Equal("识别并规范标题", button.Content);
            var keep = (Button)window.FindName("RecognizeNumericKeepButton");
            Assert.Equal(Visibility.Visible, keep.Visibility);
            Assert.Equal("仅识别，保留原标题", keep.Content);
            Assert.True(button.IsEnabled);
            Assert.Contains(window.SelectedLines, l => l.LineNumber == 1);
            if (Environment.GetEnvironmentVariable("EASYPUB_REVIEW_NUMERIC_INITIAL") is not null)
                return Task.CompletedTask;
            var global = window.GlobalNumericDefaults;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            var task = window.RecognizeSuggestedNumericChaptersAsync();
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } };
            timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            Assert.Equal(2, window.Roots.Count(n => !n.IsFrontMatter));
            Assert.False(window.InheritNumericDefaults);
            Assert.Equal(global, window.GlobalNumericDefaults);
            Assert.True(((CheckBox)window.FindName("NumericHeadingsCheck")).IsChecked);
            Assert.Equal("第一章 开端", window.Roots.First(n => !n.IsFrontMatter).Title);
            ((Button)window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain(window.Roots, n => !n.IsFrontMatter);
            Assert.False(((CheckBox)window.FindName("NumericHeadingsCheck")).IsChecked);
            Assert.True(window.InheritNumericDefaults);
            return Task.CompletedTask;
        }, "001：开端\n甲\n乙\n丙\n002：后续\n丁\n戊\n己", false);
    }

    [Fact]
    public async Task Numeric_recognition_can_keep_original_titles()
    {
        await Run((window, document, path) =>
        {
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            var task = window.RecognizeSuggestedNumericChaptersAsync(normalizeTitles: false);
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } };
            timer.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
            task.GetAwaiter().GetResult();
            Assert.Equal("001：开端", window.Roots.First(n => !n.IsFrontMatter).Title);
            Assert.Equal("002：后续", window.Roots.Where(n => !n.IsFrontMatter).Skip(1).Single().Title);
            return Task.CompletedTask;
        }, "001：开端\n甲\n乙\n丙\n002：后续\n丁\n戊\n己", false);
    }

    [Fact]
    public async Task Workbench_exposes_contextual_actions_and_category_filter()
    {
        await Run((window, document, path) =>
        {
            Assert.IsType<ComboBox>(window.FindName("IssueCategoryCombo"));
            window.NavigateToSourceLine(3);
            Assert.Equal("还原为上一章正文", ((Button)window.FindName("MergeButton")).Content);
            Assert.IsType<TextBlock>(window.FindName("ReviewExplanation"));
            Assert.False(((System.Windows.Controls.Primitives.Popup)window.FindName("ChapterActionsPopup")).IsOpen);
            var filter = (ComboBox)window.FindName("IssueCategoryCombo");
            filter.SelectedIndex = 1;
            var issues = (ComboBox)window.FindName("ChapterSuggestionsCombo");
            Assert.NotEmpty(issues.Items.Cast<object>());
            window.NavigateToSourceLine(3);
            Assert.Equal("疑似正文误识别", ((TextBlock)window.FindName("ReviewHeading")).Text);
            var onlyReview = (RadioButton)window.FindName("ReviewOnlyCheck");
            onlyReview.IsChecked = true;
            onlyReview.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            Assert.False(((RadioButton)window.FindName("AllChaptersRadio")).IsChecked);
            Assert.True(window.Roots.Single(n => n.TitleLineNumber == 5).IsReviewVisible);
            Assert.False(window.Roots.Single(n => n.TitleLineNumber == 11).IsReviewVisible);
            var allChapters = (RadioButton)window.FindName("AllChaptersRadio");
            allChapters.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            Assert.True(allChapters.IsChecked);
            Assert.False(onlyReview.IsChecked);
            Assert.True(window.Roots.Single(n => n.TitleLineNumber == 11).IsReviewVisible);
            var preview = (ListBox)window.FindName("SourceLinesList");
            Assert.Contains(window.SelectedLines, l => l.LineNumber == 3);
            preview.SelectedItem = window.SelectedLines.Single(l => l.LineNumber == 4);
            Assert.False(((Button)window.FindName("SplitButton")).IsEnabled);
            window.NavigateToSourceLine(3);
            var before = issues.Items.Count;
            ((Button)window.FindName("ConfirmNormalButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(before - 1, issues.Items.Count);
            Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("ReviewFeedback")).Visibility);
            Assert.Equal(6, window.Roots.Count(n => !n.IsFrontMatter));
            var more = (Button)window.FindName("WorkbenchMoreButton");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var menu = more.ContextMenu!;
            menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "视图").Items.OfType<MenuItem>().Single(i => (string)i.Header == "恢复本次已确认的提醒").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            Assert.Equal(before, issues.Items.Count);
            window.NavigateToSourceLine(3);
            if (Environment.GetEnvironmentVariable("EASYPUB_REVIEW_CAPTURE") is null)
            {
                ((Button)window.FindName("ReviewActionButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(4, window.Roots.Count(n => !n.IsFrontMatter));
                Assert.Equal(Visibility.Visible, ((TextBlock)window.FindName("ReviewFeedback")).Visibility);
                Assert.Contains(document.GetSourceLines(window.Roots.First(n => !n.IsFrontMatter).ToEntry()), l => l.LineNumber == 5);
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Filter_switch_is_responsive_for_a_large_chapter_tree()
    {
        var source = string.Join("\n", Enumerable.Range(1, 634).SelectMany(index =>
        {
            var number = index % 2 == 0 ? index : Math.Max(1, index - 1);
            return new[] { $"第{number}章 条目{index}", "正文内容" };
        }));
        await Run((window, document, path) =>
        {
            var reviewOnly = (RadioButton)window.FindName("ReviewOnlyCheck");
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            reviewOnly.IsChecked = true;
            reviewOnly.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            window.UpdateLayout();
            stopwatch.Stop();
            Assert.True(window.Roots.Count > 600);
            Assert.True(window.Roots.Any(node => node.IsReviewVisible), "待核对筛选不应清空全部章节。");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(16),
                $"切换待核对耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms，超过 16 ms 响应预算。");
            var allChapters = (RadioButton)window.FindName("AllChaptersRadio");
            stopwatch.Restart();
            allChapters.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            window.UpdateLayout();
            stopwatch.Stop();
            Assert.True(allChapters.IsChecked);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(16),
                $"切换全部章节耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms，超过 16 ms 响应预算。");
            return Task.CompletedTask;
        }, source, false);
    }

    [Fact]
    public async Task Group_without_predecessor_is_skipped_without_blocking_other_group()
    {
        await Run((window, document, path) =>
        {
            var nodes = window.Roots.Where(n => !n.IsFrontMatter).ToArray();
            window.SelectChapterForOperation(nodes[0], System.Windows.Input.ModifierKeys.None);
            window.SelectChapterForOperation(nodes[4], System.Windows.Input.ModifierKeys.Control);
            ((Button)window.FindName("MergeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(nodes[0], window.Roots);
            Assert.Equal(5, window.Roots.Count(n => !n.IsFrontMatter));
            Assert.Contains("跳过 1 组", ((TextBlock)window.FindName("ReviewFeedback")).Text);
            Assert.Contains(document.GetSourceLines(nodes[3].ToEntry()), l => l.LineNumber == 9);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mixed_groups_use_original_predecessors_and_undo(bool demote)
    {
        await Run((window, document, path) =>
        {
            var nodes = window.Roots.Where(n => !n.IsFrontMatter).ToArray();
            Assert.Equal(6, nodes.Length);
            window.SelectChapterForOperation(nodes[1], System.Windows.Input.ModifierKeys.None);
            window.SelectChapterForOperation(nodes[2], System.Windows.Input.ModifierKeys.Shift);
            window.SelectChapterForOperation(nodes[4], System.Windows.Input.ModifierKeys.Control);
            window.SelectChapterForOperation(nodes[5], System.Windows.Input.ModifierKeys.Control);
            Assert.Equal(4, nodes.Count(n => n.IsOperationSelected));
            Assert.All(nodes, n => Assert.True(n.IncludeInToc));
            ((Button)window.FindName(demote ? "DemoteButton" : "MergeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, window.Roots.Count(n => !n.IsFrontMatter));
            if (demote)
            {
                Assert.Equal(new[] { nodes[1], nodes[2] }, nodes[0].Children);
                Assert.Equal(new[] { nodes[4], nodes[5] }, nodes[3].Children);
            }
            else
            {
                Assert.Equal(Enumerable.Range(2, 5), document.GetSourceLines(nodes[0].ToEntry()).Select(l => l.LineNumber));
                Assert.Equal(Enumerable.Range(8, 5), document.GetSourceLines(nodes[3].ToEntry()).Select(l => l.LineNumber));
            }
            ((Button)window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(6, window.Roots.Count(n => !n.IsFrontMatter));
            Assert.All(window.Roots, n => Assert.Empty(n.Children));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Control_toggle_and_separated_selection()
    {
        await Run((window, document, path) =>
        {
            var nodes = window.Roots.Where(n => !n.IsFrontMatter).ToArray();
            window.SelectChapterForOperation(nodes[1], System.Windows.Input.ModifierKeys.None);
            window.SelectChapterForOperation(nodes[4], System.Windows.Input.ModifierKeys.Control);
            window.SelectChapterForOperation(nodes[4], System.Windows.Input.ModifierKeys.Control);
            Assert.True(nodes[1].IsOperationSelected);
            Assert.False(nodes[4].IsOperationSelected);
            window.SelectChapterForOperation(nodes[4], System.Windows.Input.ModifierKeys.Control);
            ((Button)window.FindName("MergeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(4, window.Roots.Count(n => !n.IsFrontMatter));
            Assert.Contains(document.GetSourceLines(nodes[0].ToEntry()), l => l.LineNumber == 3);
            Assert.Contains(document.GetSourceLines(nodes[3].ToEntry()), l => l.LineNumber == 9);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Merge_preserves_original_title_line()
    {
        await Run(async (window, document, path) =>
        {
            window.NavigateToSourceLine(3);
            ((Button)window.FindName("MergeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var first = window.Roots.First(n => !n.IsFrontMatter);
            Assert.Contains(document.GetSourceLines(first.ToEntry()), line => line.LineNumber == 3 && line.Text == "1：原文条目");
            var output = Path.ChangeExtension(path, ".epub");
            var plan = document.CreatePlan(window.Roots.Select(n => n.ToEntry()).ToArray());
            try
            {
                Task.Run(() => new EasyPubConverter().ConvertAsync(new ConversionRequest(path, output) { ChapterTree = plan })).GetAwaiter().GetResult();
                using var archive = System.IO.Compression.ZipFile.OpenRead(output);
                var chapters = archive.Entries.Where(e => e.FullName.StartsWith("OEBPS/chapter") && e.FullName.EndsWith(".html"));
                var text = string.Join("\n", chapters.Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }));
                Assert.Contains("1：原文条目", text);
                Assert.Equal(1, text.Split("1：原文条目").Length - 1);
            }
            finally { File.Delete(output); }
            await Task.CompletedTask;
        });
    }

    private static async Task Run(Func<ChapterEditorWindow, ChapterTreeDocument, string, Task> action, string? suppliedSource = null, bool numericEnabled = true)
    {
        var path = Path.Combine(Path.GetTempPath(), $"chapter-batch-{Guid.NewGuid():N}.txt");
        var source = suppliedSource ?? "第一章 A\n甲\n1：原文条目\n乙\n2：第二条目\n丙\n第二章 D\n丁\n3：第三条目\n戊\n4：第四条目\n己";
        await File.WriteAllTextAsync(path, source);
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path, hierarchy: new() { RecognizeNumericHeadings = numericEnabled, NumericHeadingMinimumBodyLines = 0 });
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                ChapterEditorWindow? window = null;
                App? captureApp = null;
                try
                {
                    var capture = Environment.GetEnvironmentVariable("EASYPUB_REVIEW_CAPTURE");
                    if (capture is not null && Application.Current is null) { captureApp = new App(); captureApp.InitializeComponent(); }
                    window = new ChapterEditorWindow(document) { Opacity = 0, ShowInTaskbar = false };
                    window.ConfirmAction = _ => true;
                    window.Show(); window.UpdateLayout();
                    action(window, document, path).GetAwaiter().GetResult();
                    if (capture is not null)
                    {
                        window.Opacity = 1;
                        window.Width = Environment.GetEnvironmentVariable("EASYPUB_REVIEW_SMALL") == "1" ? 920 : 1240;
                        window.Height = Environment.GetEnvironmentVariable("EASYPUB_REVIEW_SMALL") == "1" ? 620 : 820;
                        window.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                        typeof(MainWindow).Assembly.GetType("EasyPub.Desktop.ThemeManager")!.GetMethod("Apply")!.Invoke(null, [Environment.GetEnvironmentVariable("EASYPUB_REVIEW_THEME") ?? "Light", window]);
                        var frame = new System.Windows.Threading.DispatcherFrame();
                        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                        settle.Tick += (_, _) => { settle.Stop(); frame.Continue = false; };
                        settle.Start(); System.Windows.Threading.Dispatcher.PushFrame(frame);
                        typeof(MainWindow).Assembly.GetType("EasyPub.Desktop.ThemeManager")!.GetMethod("Apply")!.Invoke(null, [Environment.GetEnvironmentVariable("EASYPUB_REVIEW_THEME") ?? "Light", window]);
                        window.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                        bitmap.Render(window);
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                        using var stream = File.Create(capture); encoder.Save(stream);
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { window?.DiscardChangesAndClose(); captureApp?.Shutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }
}
