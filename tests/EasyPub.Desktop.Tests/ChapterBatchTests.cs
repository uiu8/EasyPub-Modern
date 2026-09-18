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
    public async Task Rule_guidance_tracks_pending_changes_without_replacing_the_tree()
    {
        await Run((window, document, path) =>
        {
            var before = window.Roots.Select(n => n.ToEntry()).ToArray();
            var numeric = (CheckBox)window.FindName("NumericHeadingsCheck");
            var summary = (TextBlock)window.FindName("EffectiveRulesText");
            numeric.IsChecked = true;
            Assert.Contains("数字章节开启", summary.Text);
            Assert.Contains("本书独立", summary.Text);
            Assert.Contains("当前树尚未重新识别", summary.Text);
            Assert.Contains("规则已调整", ((TextBlock)window.FindName("WorkflowHintText")).Text);
            Assert.Equal(before, window.Roots.Select(n => n.ToEntry()).ToArray());
            numeric.IsChecked = false;
            Assert.Contains("数字章节关闭", summary.Text);
            Assert.DoesNotContain("尚未重新识别", summary.Text);
            Assert.Equal(before, window.Roots.Select(n => n.ToEntry()).ToArray());
            return Task.CompletedTask;
        }, "第一章 开始\n正文", numericEnabled: false);
    }

    [Fact]
    public async Task Reference_repair_apply_undo_and_redo_preserve_complete_content()
    {
        await Run((window,document,path) =>
        {
            var original=window.Roots.Select(n=>n.ToEntry()).ToArray();
            var catalog=new ReferenceCatalog("test","测试",[
                new("第一章 开始",ReferenceNodeKind.Chapter,null,null),
                new("第二章 继续",ReferenceNodeKind.Chapter,null,null)]);
            var outcome=ChapterAutoRepair.Prepare(document.WithEntries(original),catalog);
            window.ApplyRepairEntries(document.WithEntries(outcome.Entries!));
            var after=window.Roots.Select(n=>n.ToEntry()).ToArray();
            RepairIntegrity.Verify(original,after,outcome.RemovedSourceLines);
            ((Button)window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var restored=window.Roots.Select(n=>n.ToEntry()).ToArray();
            Assert.Equal(original.Select(e=>e.Title),restored.Select(e=>e.Title));
            Assert.Equal(RepairIntegrity.Coverage(original).Order(),RepairIntegrity.Coverage(restored).Order());
            ((Button)window.FindName("RedoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(after.Select(e=>e.Title),window.Roots.Select(n=>n.Title));
            return Task.CompletedTask;
        }, "第一章 开始\n正文一\n\n第二章 继续\n正文二\n\n第二章 继续\n正文二", numericEnabled:false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Duplicate_copy_can_be_removed_in_either_direction_and_undone(bool removeFirst)
    {
        const string body = "旅人沿着河流继续前进，终于发现远方城堡的灯光，夜幕渐渐降临。";
        await Run((window, document, path) =>
        {
            var before = window.Roots.Select(n => n.ToEntry()).ToArray();
            var nodes = window.Roots.Where(n => !n.IsFrontMatter).ToArray();
            window.NavigateToSourceLine(3);
            Assert.Equal("对比正文，选择保留…", ((Button)window.FindName("ReviewActionButton")).Content);
            var positions = (ComboBox)window.FindName("GroupLocationsCombo");
            Assert.Equal(2, positions.Items.Count);
            Assert.Equal(Visibility.Visible, positions.Visibility);
            positions.SelectedIndex = 0;
            Assert.Equal(1, ((ChapterTreeSourceLine)((ListBox)window.FindName("SourceLinesList")).SelectedItem).LineNumber);
            positions.SelectedIndex = 1;
            Assert.Equal(3, ((ChapterTreeSourceLine)((ListBox)window.FindName("SourceLinesList")).SelectedItem).LineNumber);
            ((TextBox)window.FindName("ChapterSearchText")).Text = "不存在的标题";
            window.LocateDuplicateChapter(1);
            Assert.Equal("", ((TextBox)window.FindName("ChapterSearchText")).Text);
            Assert.Equal(1, ((ChapterTreeSourceLine)((ListBox)window.FindName("SourceLinesList")).SelectedItem).LineNumber);
            window.RemoveDuplicateCopy(nodes[removeFirst ? 0 : 1].Id, nodes[removeFirst ? 1 : 0].Id);
            Assert.Single(window.Roots.Where(n => !n.IsFrontMatter));
            Assert.DoesNotContain(((ComboBox)window.FindName("ChapterSuggestionsCombo")).Items.Cast<ConversionPreflightIssue>(), i => i.Code == "chapter_content_duplicate");
            ((Button)window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(before.Select(e => e.Id), window.Roots.Select(n => n.Id));
            Assert.Equal(before.SelectMany(e => e.ContentRanges), window.Roots.SelectMany(n => n.ContentRanges));
            ((Button)window.FindName("RedoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(window.Roots.Where(n => !n.IsFrontMatter));
            return Task.CompletedTask;
        }, $"第三十四章 转折\n{body}\n第三十四章 转折\n{body}", false);
    }

    [Fact]
    public async Task Ordinary_chapter_has_no_empty_review_locations()
    {
        await Run((window, document, path) =>
        {
            window.NavigateToSourceLine(1);
            Assert.Equal(Visibility.Collapsed, ((ComboBox)window.FindName("GroupLocationsCombo")).Visibility);
            return Task.CompletedTask;
        }, "第一章 开始\n正文\n第二章 后续\n正文", false);
    }

    [Fact]
    public async Task Structure_banner_opens_guidance_and_can_be_dismissed_and_restored()
    {
        var source = string.Join("\n", Enumerable.Range(0, 3).SelectMany(_ => new[] { "第一章 甲", "正文", "第二章 乙", "正文", "第三章 丙", "正文", "第二十章 丁", "正文" }));
        await Run((window, document, path) =>
        {
            var banner = (Border)window.FindName("StructureSuggestionBanner");
            Assert.Equal(Visibility.Visible, banner.Visibility);
            var buttons = ((StackPanel)banner.Child).Children.OfType<WrapPanel>().Single().Children.OfType<Button>().ToArray();
            buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal("预览结构整理…", ((Button)window.FindName("ReviewActionButton")).Content);
            buttons[2].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Visibility.Collapsed, banner.Visibility);
            window.RestoreConfirmedGroups();
            Assert.Equal(Visibility.Visible, banner.Visibility);
            return Task.CompletedTask;
        }, source, false);
    }

    [Fact]
    public async Task Recovery_preview_applies_and_undo_restores_original_tree()
    {
        await Run((window, document, path) =>
        {
            var original = window.Roots.Select(n => n.Title).ToArray();
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            var clock = System.Diagnostics.Stopwatch.StartNew();
            timer.Tick += (_, _) =>
            {
                foreach (Window dialog in window.OwnedWindows)
                    if (dialog.Title == "预览编号分组与结构修复") { dialog.DialogResult = true; break; }
                if (window.Roots.Any(n => n.Title.StartsWith("编号段")) || clock.Elapsed.TotalSeconds > 8) frame.Continue = false;
            };
            timer.Start();
            try
            {
                window.Dispatcher.BeginInvoke(() => ((Button)window.FindName("RecoverStructureButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                System.Windows.Threading.Dispatcher.PushFrame(frame);
            }
            finally { timer.Stop(); }
            Assert.Contains(window.Roots, n => n.Title.StartsWith("编号段") && n.Children.Count == 3);
            ((Button)window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(original, window.Roots.Select(n => n.Title));
            return Task.CompletedTask;
        }, "第一章 开始\n甲\n第二章 后续\n乙\n第三章 结束\n丙", false);
    }

    [Fact]
    public async Task Conclusion_can_remain_a_chapter_without_owning_later_chapters()
    {
        await Run((window, document, path) =>
        {
            window.NavigateToSourceLine(5);
            window.RestoreContainerToBody(keepHeading: true);
            var volume = window.Roots.Single(n => n.Title == "第一卷 正卷");
            Assert.Equal(new[] { "第一章 前文", "第二卷 结语", "第二章 后文" }, volume.Children.Select(n => n.Title));
            Assert.All(volume.Children, n => { Assert.Empty(n.Children); Assert.Equal(2, n.Level); });
            Assert.Contains(window.Roots, n => n.Title == "第三卷 真正的下一卷" && n.Children.Count == 1);
            ((Button)window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Single(window.Roots.Single(n => n.Title == "第二卷 结语").Children);
            ((Button)window.FindName("RedoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(3, window.Roots.Single(n => n.Title == "第一卷 正卷").Children.Count);
            return Task.CompletedTask;
        }, "第一卷 正卷\n介绍\n第一章 前文\n甲\n第二卷 结语\n说明\n第二章 后文\n乙\n第三卷 真正的下一卷\n介绍\n第一章 新卷\n丙", false, true);
    }

    [Fact]
    public async Task Removing_false_volume_keeps_following_chapters_in_previous_volume()
    {
        await Run((window, document, path) =>
        {
            window.NavigateToSourceLine(5);
            window.RestoreContainerToBody();
            var volume = window.Roots.Single(n => n.Title == "第一卷 正卷");
            Assert.Equal(new[] { "第一章 前文", "第二章 后文", "第三章 继续" }, volume.Children.Select(n => n.Title));
            Assert.All(volume.Children, n => Assert.Equal(2, n.Level));
            Assert.Contains(document.GetSourceLines(volume.Children[0].ToEntry()), l => l.LineNumber == 5);
            var entries = window.Roots.SelectMany(n => new[] { n.ToEntry() }.Concat(n.Children.Select(c => c.ToEntry()))).ToArray();
            var lines = entries.SelectMany(n => (n.TitleLineNumber is int line ? new[] { line } : Array.Empty<int>())
                .Concat(n.ContentRanges.SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1)))).ToArray();
            Assert.Equal(Enumerable.Range(1, document.LineCount), lines);
            var plan = document.CreatePlan(entries);
            var reloaded = Task.Run(() => ChapterTreeDocument.LoadAsync(path, existingPlan: plan)).GetAwaiter().GetResult();
            Assert.Equal(entries.Select(e => (e.Title, e.Level, e.TitleLineNumber)),
                reloaded.Entries.Select(e => (e.Title, e.Level, e.TitleLineNumber)));
            ((Button)window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Contains(window.Roots, n => n.Title.StartsWith("第二卷"));
            return Task.CompletedTask;
        }, "第一卷 正卷\n介绍\n第一章 前文\n甲\n第二卷 其实是结语\n说明\n第二章 后文\n乙\n第三章 继续\n丙", false, true);
    }

    [Fact]
    public async Task False_volume_can_be_restored_without_losing_children_or_source()
    {
        await Run((window, document, path) =>
        {
            window.NavigateToSourceLine(3);
            Assert.NotEmpty(window.Roots.Single(n => n.Title.StartsWith("第二卷")).Children);
            window.RestoreContainerToBody();
            Assert.DoesNotContain(window.Roots, n => n.Title.StartsWith("第二卷"));
            Assert.Contains(window.Roots, n => n.Title == "第一章 正文");
            Assert.Contains(window.Roots, n => n.Title == "第二章 后续");
            var rows = window.Roots.SelectMany(n => n.ContentRanges.SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1))
                .Concat(n.TitleLineNumber is int line ? new[] { line } : Array.Empty<int>())).Order().ToArray();
            Assert.Equal(Enumerable.Range(1, document.LineCount), rows);
            ((Button)window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(2, window.Roots.Single(n => n.Title.StartsWith("第二卷")).Children.Count);
            return Task.CompletedTask;
        }, "第一卷 前言\n说明\n第二卷 这其实是作者的话\n解释\n第一章 正文\n甲\n第二章 后续\n乙", false, true);
    }

    [Fact]
    public async Task Missing_heading_group_can_be_repaired_and_undone()
    {
        await Run((window, document, path) =>
        {
            var issue = ChapterReviewAnalyzer.Analyze(document).Groups.Single(g => g.Issue.Code == "chapter_heading_typo").Issue;
            window.NavigateToSuggestion(issue);
            var action = (Button)window.FindName("ReviewActionButton");
            Assert.Equal("修复本组漏识别标题…", action.Content);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                foreach (Window dialog in window.OwnedWindows)
                    if (dialog.Title == "核对漏识别标题") { timer.Stop(); dialog.DialogResult = true; break; }
            };
            timer.Start();
            try { action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); } finally { timer.Stop(); }
            Assert.Contains(window.Roots, n => n.Title == "第658章 回归");
            ((Button)window.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.DoesNotContain(window.Roots, n => n.Title == "第658章 回归");
            return Task.CompletedTask;
        }, "第657章 开始\n正文\n\n第658张 回归\n\n后续正文\n\n第660章 结束\n正文", false);
    }

    [Fact]
    public async Task Sparse_review_filter_does_not_realize_hidden_chapters_in_expanded_volumes()
    {
        var lines = new List<string>();
        var targetLine = 0;
        for (var index = 1; index <= 634; index++)
        {
            if (index % 110 == 1) lines.Add($"第{index / 110 + 1}卷 分卷");
            if (index == 457) targetLine = lines.Count + 1;
            lines.Add($"第{(index is 457 or 579 ? index - 1 : index)}章 条目{index}");
            lines.AddRange(Enumerable.Repeat("正文内容，用于验证长篇小说的章节筛选。", 140));
        }
        await Run((window, document, path) =>
        {
            var tree = (TreeView)window.FindName("ChapterTree");
            foreach (var root in window.Roots) root.IsExpanded = true;
            window.NavigateToSourceLine(targetLine);
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var review = (RadioButton)window.FindName("ReviewOnlyCheck");
            var all = (RadioButton)window.FindName("AllChaptersRadio");
            for (var repeat = 0; repeat < 3; repeat++)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                review.IsChecked = true;
                review.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                timer.Stop();
                var realized = CountContainers(tree);
                Assert.True(realized < 100 && timer.ElapsedMilliseconds < 150,
                    $"Sparse review: {timer.Elapsed.TotalMilliseconds:F1} ms, {realized} realized containers.");
                timer.Restart();
                all.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                timer.Stop();
                Assert.True(timer.ElapsedMilliseconds < 150, $"All chapters: {timer.Elapsed.TotalMilliseconds:F1} ms.");
            }
            Assert.Equal(634, window.Roots.Sum(n => n.Children.Count));
            return Task.CompletedTask;
        }, string.Join("\n", lines), false, hierarchyEnabled: true);
    }

    private static int CountContainers(DependencyObject parent)
    {
        var count = 0;
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            count += (child is TreeViewItem ? 1 : 0) + CountContainers(child);
        }
        return count;
    }

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
            filter.SelectedIndex = 2;   // 0 全部问题 · 1 漏识别 · 2 误识别
            var issues = (ComboBox)window.FindName("ChapterSuggestionsCombo");
            Assert.NotEmpty(issues.Items.Cast<object>());
            window.NavigateToSourceLine(3);
            Assert.Equal("疑似正文被识别为章节", ((TextBlock)window.FindName("ReviewHeading")).Text);
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
            menu.Items.OfType<MenuItem>().Single(i => (string)i.Header == "视图").Items.OfType<MenuItem>().Single(i => (string)i.Header == "全部恢复").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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
            var eventElapsed = stopwatch.Elapsed;
            window.UpdateLayout();
            stopwatch.Stop();
            Assert.True(window.Roots.Count > 600);
            Assert.True(window.Roots.Any(node => node.IsReviewVisible), "待核对筛选不应清空全部章节。");
            Assert.True(eventElapsed < TimeSpan.FromMilliseconds(16),
                $"待核对点击处理耗时 {eventElapsed.TotalMilliseconds:F0} ms，超过 16 ms 响应预算。");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(150),
                $"切换待核对完成布局耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms。");
            var allChapters = (RadioButton)window.FindName("AllChaptersRadio");
            stopwatch.Restart();
            allChapters.RaiseEvent(new RoutedEventArgs(RadioButton.ClickEvent));
            eventElapsed = stopwatch.Elapsed;
            window.UpdateLayout();
            stopwatch.Stop();
            Assert.True(allChapters.IsChecked);
            Assert.True(eventElapsed < TimeSpan.FromMilliseconds(16),
                $"全部章节点击处理耗时 {eventElapsed.TotalMilliseconds:F0} ms，超过 16 ms 响应预算。");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(150),
                $"切换全部章节完成布局耗时 {stopwatch.Elapsed.TotalMilliseconds:F0} ms。");
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

    private static async Task Run(Func<ChapterEditorWindow, ChapterTreeDocument, string, Task> action, string? suppliedSource = null, bool numericEnabled = true, bool hierarchyEnabled = false)
    {
        var path = Path.Combine(Path.GetTempPath(), $"chapter-batch-{Guid.NewGuid():N}.txt");
        var source = suppliedSource ?? "第一章 A\n甲\n1：原文条目\n乙\n2：第二条目\n丙\n第二章 D\n丁\n3：第三条目\n戊\n4：第四条目\n己";
        await File.WriteAllTextAsync(path, source);
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path, hierarchy: new() { Enabled = hierarchyEnabled, RecognizeNumericHeadings = numericEnabled, NumericHeadingMinimumBodyLines = 0 });
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
