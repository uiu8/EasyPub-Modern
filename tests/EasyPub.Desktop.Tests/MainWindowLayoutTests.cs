using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyPub.Core;
using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class MainWindowLayoutTests
{
    [Fact]
    public async Task Suggested_title_is_undoable_and_preserves_original_text()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        const string source = "第66章 甲\n正文\n第66章 乙\n正文\n第68章 丙\n正文";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var document = await ChapterTreeDocument.LoadAsync(path);
            RunInWindow(owner =>
            {
                var editor = new ChapterEditorWindow(document) { Owner = owner };
                try
                {
                    editor.Show(); editor.UpdateLayout(); editor.NavigateToSourceLine(3);
                    var fix = (Button)editor.FindName("CorrectNumberButton");
                    Assert.Equal(Visibility.Visible, fix.Visibility);
                    fix.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("第67章 乙", editor.Roots.Single(n => n.TitleLineNumber == 3).Title);
                    Assert.Equal("第66章 乙", editor.SelectedLines[0].Text);
                    Assert.Equal(Visibility.Collapsed, fix.Visibility);
                    ((Button)editor.FindName("UndoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal("第66章 乙", editor.Roots.Single(n => n.TitleLineNumber == 3).Title);
                    Assert.Equal(Visibility.Visible, fix.Visibility);
                }
                finally { editor.Close(); }
            });
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("notepad.exe", 1)]
    [InlineData("Notepad3.exe", 3)]
    [InlineData("notepad++.exe", 2)]
    [InlineData("Code.exe", 2)]
    public void External_editor_arguments_keep_paths_separate(string editor, int count)
    {
        var info = ChapterEditorWindow.CreateEditorStartInfo(editor, @"C:\books\a b.txt", 42);
        Assert.Equal(count, info.ArgumentList.Count);
        Assert.Contains(@"C:\books\a b.txt", info.ArgumentList.Last());
        if (count == 2) Assert.Contains("42", string.Join(" ", info.ArgumentList));
    }

    [Fact]
    public async Task Chapter_diagnostic_navigation_reaches_virtualized_distant_chapter()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            File.WriteAllText(path, string.Join("\n", Enumerable.Range(1, 1000).Select(i => $"第{i}章 标题\n正文")));
            var document = await ChapterTreeDocument.LoadAsync(path);
            RunInWindow(owner =>
            {
                var editor = new ChapterEditorWindow(document) { Owner = owner };
                try
                {
                    editor.Show();
                    editor.UpdateLayout();
                    editor.NavigateToSourceLine(1999);
                    var tree = (TreeView)editor.FindName("ChapterTree");
                    Assert.Equal("第1000章 标题", Assert.IsType<ChapterTreeNode>(tree.SelectedItem).Title);
                    Assert.Equal(1999, Assert.IsType<ChapterTreeSourceLine>(((ListBox)editor.FindName("SourceLinesList")).SelectedItem).LineNumber);
                    Assert.Null(editor.ResultPlan);
                }
                finally { editor.Close(); }
            });
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Startup_navigation_is_opt_in_but_explicit_profile_keeps_choices()
    {
        RunInWindow(window =>
        {
            var profile = ConversionProfile.Default with
            {
                Options = ConversionProfile.Default.Options with
                {
                    TocHierarchy = new() { IncludeHtmlTocPage = true, IncludeChapterTopNavigation = true },
                },
            };
            typeof(MainWindow).GetMethod("ApplyAppSettings", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [EasyPubAppSettings.Default with { LastProfile = profile }]);
            var field = typeof(MainWindow).GetField("_tocHierarchy", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var startup = (TocHierarchyOptions)field.GetValue(window)!;
            Assert.False(startup.IncludeHtmlTocPage);
            Assert.False(startup.IncludeChapterTopNavigation);
            typeof(MainWindow).GetMethod("ApplyProfile", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(window, [profile]);
            var explicitProfile = (TocHierarchyOptions)field.GetValue(window)!;
            Assert.True(explicitProfile.IncludeHtmlTocPage);
            Assert.True(explicitProfile.IncludeChapterTopNavigation);
            Assert.True(profile.Options.TocHierarchy.IncludeChapterTopNavigation);
        });
    }

    [Fact]
    public void Large_cleanup_latest_options_win_and_group_choices_roundtrip()
    {
        RunInWindow(owner =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
            var source = string.Join("\n", Enumerable.Repeat("正文\u200b\n请记住本站", 2000));
            var options = new TextCleanupOptions { RemoveInvisibleCharacters = true, RemoveSiteNotices = true };
            var constructor = typeof(TextCleanupWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var window = (TextCleanupWindow)constructor.Invoke(["大量记录验收", source, options]);
            window.Owner = owner;
            try
            {
                window.Show();
                var apply = (Button)window.FindName("ApplyRulesButton");
                var grid = (DataGrid)window.FindName("ChangesGrid");
                PumpDispatcherUntil(() => apply.IsEnabled && grid.Items.Count == 4000, TimeSpan.FromSeconds(4));
                var notice = (CheckBox)window.FindName("NoticeCheck");
                for (var i = 0; i < 9; i++)
                {
                    notice.IsChecked = i % 2 == 1;
                    notice.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, notice));
                }
                PumpDispatcherUntil(() => apply.IsEnabled && grid.Items.Count == 2000, TimeSpan.FromSeconds(4));
                Assert.False(window.Result.RemoveSiteNotices);
                var group = (CheckBox)window.FindName("GroupAdsCheck");
                group.IsChecked = true;
                group.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, group));
                Assert.Single(grid.Items.Cast<TextCleanupChangeRow>());
                var row = (TextCleanupChangeRow)grid.Items[0];
                Assert.Equal(2000, row.Members.Count);
                typeof(TextCleanupWindow).GetMethod("ToggleChange_Click", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(window, [new Button { Tag = row.Key }, new RoutedEventArgs()]);
                PumpDispatcherUntil(() => apply.IsEnabled, TimeSpan.FromSeconds(4));
                var restored = System.Text.Json.JsonSerializer.Deserialize<TextCleanupOptions>(System.Text.Json.JsonSerializer.Serialize(window.Result))!;
                Assert.Equal(2000, restored.ExcludedChangeKeys.Count);
                Assert.Equal(source, TextCleanupPipeline.Apply(source, restored).Text.Replace("\r\n", "\n"));
                Assert.Empty(options.ExcludedChangeKeys);
                Assert.True(options.RemoveSiteNotices);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Folding_supports_builtin_and_custom_rules_without_merging_distinct_rule_ids()
    {
        RunInWindow(owner =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
            var options = new TextCleanupOptions { RemoveInvisibleCharacters = true, CustomRules = [
                new() { Id = "first", Name = "同名", Pattern = "foo", Replacement = "bar", Order = 0 },
                new() { Id = "reverse", Name = "同名", Pattern = "bar", Replacement = "foo", Order = 1 },
                new() { Id = "third", Name = "同名", Pattern = "foo", Replacement = "bar", Order = 2 }] };
            var constructor = typeof(TextCleanupWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var window = (TextCleanupWindow)constructor.Invoke(["全部规则折叠", "正文\u200b\n正文\u200b\nfoo\nfoo", options]);
            window.Owner = owner;
            try
            {
                window.Show();
                var grid = (DataGrid)window.FindName("ChangesGrid");
                var apply = (Button)window.FindName("ApplyRulesButton");
                PumpDispatcherUntil(() => apply.IsEnabled && grid.Items.Count == 8, TimeSpan.FromSeconds(3));
                var check = (CheckBox)window.FindName("GroupAdsCheck");
                check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, check));
                Assert.Equal(4, grid.Items.Count);
                Assert.All(grid.Items.Cast<TextCleanupChangeRow>(), row => Assert.Equal(2, row.Members.Count));
                Assert.Equal(3, grid.Items.Cast<TextCleanupChangeRow>().Count(row => row.Change.CustomRuleId is not null));
                Assert.Contains("将替换 2", ((TextCleanupChangeRow)grid.Items[0]).Status);
                var before = window.Result;
                var toggle = new Button { Tag = ((TextCleanupChangeRow)grid.Items[0]).Key };
                typeof(TextCleanupWindow).GetMethod("ToggleChange_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [toggle, new RoutedEventArgs()]);
                PumpDispatcherUntil(() => apply.IsEnabled, TimeSpan.FromSeconds(3));
                Assert.Equal(2, window.Result.ExcludedChangeKeys.Count);
                var expand = (Button)window.FindName("ExpandAdGroupButton");
                expand.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, expand));
                Assert.Equal(2, grid.Items.Count);
                Assert.All(grid.Items.Cast<TextCleanupChangeRow>(), row => Assert.False(row.Change.IsApplied));
                Assert.Empty(before.ExcludedChangeKeys);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Managing_saved_ad_condition_does_not_add_removed_exception_back()
    {
        RunInWindow(owner =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
            var options = new TextCleanupOptions { RemoveSiteNotices = true, Advertisement = new AdvertisementRuleOptions().AddKeyword("请记住本站", true) };
            var dialog = new AdvertisementConditionWindow("请记住本站", options, "请记住本站", true) { Owner = owner };
            Exception? failure = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            try
            {
                dialog.Show();
                var save = (Button)dialog.FindName("SaveConditionButton");
                PumpDispatcherUntil(() => save.IsEnabled, TimeSpan.FromSeconds(3));
                timer.Tick += (_, _) =>
                {
                    var manager = dialog.OwnedWindows.OfType<BuiltinCleanupWindow>().FirstOrDefault();
                    if (manager is null) return;
                    timer.Stop();
                    try
                    {
                        Assert.False(FindVisualDescendants<ComboBox>(manager).First().IsEnabled);
                        var field = (TextBox)typeof(BuiltinCleanupWindow).GetField("_preserveKeywords", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
                        field.Text = "";
                        var button = FindVisualDescendants<Button>(manager).Single(b => b.Content?.ToString() == "保存并返回预览");
                        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                    }
                    catch (Exception ex) { failure = ex; manager.Close(); }
                };
                timer.Start();
                typeof(AdvertisementConditionWindow).GetMethod("ManageConditions_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(dialog, [dialog, new RoutedEventArgs()]);
                if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                PumpDispatcherUntil(() => save.IsEnabled, TimeSpan.FromSeconds(3));
                var candidate = (TextCleanupOptions)typeof(AdvertisementConditionWindow).GetField("_candidate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
                Assert.Empty(candidate.Advertisement.PreserveKeywords);
                Assert.Contains("新增广告删除 1", ((TextBlock)dialog.FindName("ImpactSummary")).Text);
                Assert.Single(options.Advertisement.PreserveKeywords);
                Assert.Null(dialog.Result);
            }
            finally { timer.Stop(); dialog.Close(); }
        });
    }

    [Fact]
    public void Distinct_ads_do_not_silently_appear_grouped()
    {
        RunInWindow(owner =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
            var source = string.Join("\n", Enumerable.Range(1, 8).Select(i => $"请记住本站 www.example.org 不同广告{i}"));
            var constructor = typeof(TextCleanupWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var window = (TextCleanupWindow)constructor.Invoke(["措辞不同的八条广告", source, new TextCleanupOptions { RemoveSiteNotices = true }]);
            window.Owner = owner;
            try
            {
                window.Show();
                var grid = (DataGrid)window.FindName("ChangesGrid");
                PumpDispatcherUntil(() => grid.Items.Count == 8 && ((Button)window.FindName("ApplyRulesButton")).IsEnabled, TimeSpan.FromSeconds(3));
                var check = (CheckBox)window.FindName("GroupAdsCheck");
                check.IsChecked = true; check.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, check));
                Assert.Equal(8, grid.Items.Count);
                Assert.Contains("未发现完全重复", ((TextBlock)window.FindName("GroupingSummaryText")).Text);
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("ExpandAdGroupButton")).Visibility);
                Assert.Empty(window.Result.ExcludedChangeKeys);
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Advertisement_draft_cancels_stale_analysis_and_keeps_settings_private()
    {
        RunInWindow(owner =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
            var initial = new TextCleanupOptions { RemoveSiteNotices = true };
            var dialog = new AdvertisementConditionWindow("请记住本站\n漏掉的推广\n普通正文", initial, "漏掉的推广") { Owner = owner };
            try
            {
                dialog.Show();
                var save = (Button)dialog.FindName("SaveConditionButton");
                var input = (TextBox)dialog.FindName("KeywordText");
                PumpDispatcherUntil(() => save.IsEnabled, TimeSpan.FromSeconds(3));
                Assert.Contains("新增广告删除 1", ((TextBlock)dialog.FindName("ImpactSummary")).Text);
                input.Text = "";
                Assert.False(save.IsEnabled);
                save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, save));
                Assert.Null(dialog.Result);
                input.Text = "请记住本站";
                ((ComboBox)dialog.FindName("IntentCombo")).SelectedIndex = 1;
                PumpDispatcherUntil(() => save.IsEnabled, TimeSpan.FromSeconds(3));
                Assert.Contains("不再被广告规则删除 1", ((TextBlock)dialog.FindName("ImpactSummary")).Text);
                Assert.Null(dialog.Result);
                Assert.Empty(initial.Advertisement.PreserveKeywords);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void Cleanup_context_routes_non_ad_and_duplicate_custom_names()
    {
        RunInWindow(owner =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
            var options = new TextCleanupOptions { RemoveInvisibleCharacters = true, CustomRules = [
                new() { Id = "a", Name = "同名", Pattern = "alpha", Replacement = "A" },
                new() { Id = "b", Name = "同名", Pattern = "beta", Replacement = "B" }] };
            var constructor = typeof(TextCleanupWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var window = (TextCleanupWindow)constructor.Invoke(["测试", "正文\u200b\nalpha\nbeta", options]);
            window.Owner = owner;
            try
            {
                window.Show();
                var grid = (DataGrid)window.FindName("ChangesGrid");
                PumpDispatcherUntil(() => ((Button)window.FindName("ApplyRulesButton")).IsEnabled && grid.Items.Count == 3, TimeSpan.FromSeconds(3));
                Assert.Equal(Visibility.Collapsed, ((Button)window.FindName("AddAdvertisementRuleButton")).Visibility);
                var actions = (WrapPanel)window.FindName("CurrentRuleActions");
                Assert.Contains("零宽", ((Button)actions.Children[0]).Content.ToString());
                grid.SelectedItem = grid.Items.Cast<TextCleanupChangeRow>().Single(row => row.Change.CustomRuleId == "b");
                Assert.Single(actions.Children.Cast<object>());
                Assert.Contains("同名", ((Button)actions.Children[0]).Content.ToString());
                var editor = new TextCleanupRuleManagerWindow(options.CustomRules, "beta", "b");
                Assert.Equal("beta", ((TextBox)editor.FindName("RulePatternText")).Text);
                editor.Close();
                var builtin = new BuiltinCleanupWindow(options, "正文", nameof(TextCleanupOptions.RemoveInvisibleCharacters));
                builtin.Show(); builtin.UpdateLayout();
                Assert.Equal(nameof(TextCleanupOptions.RemoveInvisibleCharacters), FindVisualDescendants<ComboBox>(builtin).First().SelectedValue);
                builtin.Close();
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Cleanup_quick_actions_update_preview_without_changing_opening_options()
    {
        RunInWindow(owner =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
            var initial = new TextCleanupOptions { RemoveSiteNotices = true };
            var constructor = typeof(TextCleanupWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var window = (TextCleanupWindow)constructor.Invoke(["测试书稿", "第一章 开始\n请记住本站\n请记住本站\n漏掉的特殊推广\n普通正文", initial]);
            window.Owner = owner;
            try
            {
                window.Show();
                var grid = (DataGrid)window.FindName("ChangesGrid");
                var apply = (Button)window.FindName("ApplyRulesButton");
                void Wait() => PumpDispatcherUntil(() => apply.IsEnabled && grid.Items.Count >= 0, TimeSpan.FromSeconds(3));
                void Click(string name) { var button = (Button)window.FindName(name); button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button)); Wait(); }
                void ToggleRow() { var button = new Button { Tag = ((TextCleanupChangeRow)grid.SelectedItem).Key }; typeof(TextCleanupWindow).GetMethod("ToggleChange_Click", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [button, new RoutedEventArgs()]); Wait(); }
                void EditCondition(string entry, string? keyword, bool preserve, bool cancel = false)
                {
                    var original = window.Result;
                    Exception? failure = null;
                    AdvertisementConditionWindow? dialog = null;
                    var initialized = false;
                    var started = DateTime.UtcNow;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                    timer.Tick += (_, _) =>
                    {
                        dialog ??= window.OwnedWindows.OfType<AdvertisementConditionWindow>().FirstOrDefault();
                        if (dialog is null) return;
                        try
                        {
                            if (!initialized)
                            {
                                if (entry == "RecentRuleButton") Assert.Equal("请记住本站", dialog.Keyword);
                                if (keyword is not null) ((TextBox)dialog.FindName("KeywordText")).Text = keyword;
                                ((ComboBox)dialog.FindName("IntentCombo")).SelectedIndex = preserve ? 1 : 0;
                                initialized = true;
                            }
                            Assert.Same(original, window.Result);
                            var save = (Button)dialog.FindName("SaveConditionButton");
                            if (!save.IsEnabled)
                            {
                                if (DateTime.UtcNow - started > TimeSpan.FromSeconds(4)) throw new TimeoutException("自动影响分析超时");
                                return;
                            }
                            var summary = ((TextBlock)dialog.FindName("ImpactSummary")).Text;
                            Assert.Contains(preserve ? "不再被广告规则删除" : "新增广告删除", summary);
                            var capture = Environment.GetEnvironmentVariable("EASYPUB_RULES_CAPTURE_PATH");
                            if (!string.IsNullOrWhiteSpace(capture)) { dialog.UpdateLayout(); CaptureWindowVisual(dialog, capture + "-dialog.png"); }
                            timer.Stop();
                            if (cancel) dialog.Close(); else save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, save));
                        }
                        catch (Exception exception) { failure = exception; timer.Stop(); dialog.Close(); }
                    };
                    timer.Start();
                    try { Click(entry); } finally { timer.Stop(); dialog?.Close(); }
                    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
                    if (cancel) Assert.Same(original, window.Result);
                }
                Wait(); Assert.Equal(2, grid.Items.Count);
                Assert.Null(window.FindName("AddPreserveKeywordButton"));
                Assert.Null(window.FindName("AddAdKeywordButton"));
                Assert.False(((Expander)window.FindName("RulesExpander")).IsExpanded);
                Assert.False(((Expander)window.FindName("DetailsExpander")).IsExpanded);
                var grouping = (CheckBox)window.FindName("GroupAdsCheck");
                grouping.IsChecked = true; grouping.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, grouping));
                Assert.Single(grid.Items.Cast<object>());
                Assert.Contains("2 条 → 1 项", ((TextBlock)window.FindName("GroupingSummaryText")).Text);
                ToggleRow(); Assert.Equal(2, window.Result.ExcludedChangeKeys.Count);
                ToggleRow(); Assert.Empty(window.Result.ExcludedChangeKeys);
                Click("ExpandAdGroupButton"); Assert.Equal(2, grid.Items.Count);
                Click("BackToGroupsButton"); Assert.Single(grid.Items.Cast<object>());
                grouping.IsChecked = false; grouping.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, grouping));
                var preview = (TextBox)window.FindName("PreviewText");
                Assert.Equal("请记住本站", preview.SelectedText);
                ((RadioButton)window.FindName("ProcessedPreviewRadio")).IsChecked = true;
                Assert.DoesNotContain("请记住本站", preview.Text);
                ((RadioButton)window.FindName("OriginalPreviewRadio")).IsChecked = true;
                Click("NextChangeButton"); Assert.Equal(1, grid.SelectedIndex);
                Click("PreviousChangeButton"); Assert.Equal(0, grid.SelectedIndex);
                ToggleRow(); Assert.Single(window.Result.ExcludedChangeKeys);
                EditCondition("AddAdvertisementRuleButton", null, true, cancel: true);
                EditCondition("AddAdvertisementRuleButton", null, true);
                Assert.Empty(grid.Items.Cast<object>());
                Assert.Contains("请记住本站", window.Result.Advertisement.PreserveKeywords);
                var resume = (Button)window.FindName("RecentRuleButton");
                Assert.True(resume.IsVisible && resume.IsEnabled);
                EditCondition("RecentRuleButton", null, true, cancel: true);
                Click("UndoConditionButton"); Assert.Empty(window.Result.Advertisement.PreserveKeywords);
                Assert.Single(window.Result.ExcludedChangeKeys);
                EditCondition("AddAdvertisementRuleButton", "请记住本站", true);
                EditCondition("RecentRuleButton", "漏掉的特殊推广", false);
                Assert.Single(grid.Items.Cast<object>());
                Assert.Contains("漏掉的特殊推广", window.Result.Advertisement.MatchKeywords);
                grouping.IsChecked = true; grouping.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, grouping));
                Assert.Contains("未发现完全重复", ((TextBlock)window.FindName("GroupingSummaryText")).Text);
                Assert.Empty(initial.Advertisement.MatchKeywords);
                Assert.Empty(initial.Advertisement.PreserveKeywords);
                Assert.Empty(initial.ExcludedChangeKeys);
                var search = (TextBox)window.FindName("ChangeSearchText");
                search.Text = "不存在";
                Assert.Equal("0 / 0", ((TextBlock)window.FindName("ChangePositionText")).Text);
                search.Text = "";
                var capturePath = Environment.GetEnvironmentVariable("EASYPUB_RULES_CAPTURE_PATH");
                if (!string.IsNullOrWhiteSpace(capturePath)) { window.UpdateLayout(); CaptureWindowVisual(window, capturePath + "-main.png"); }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Cleanup_customization_keeps_book_state_isolated()
    {
        RunInWindow(window =>
        {
            var book = new InputBookItem(Path.Combine(Path.GetTempPath(), "demo.txt"));
            book.SetCleanupOverride(new TextCleanupOptions { RemoveSiteNotices = true, Advertisement = new() { Pattern = "本书广告" } });
            Assert.Equal("本书广告", book.Clone().CleanupOverride!.Advertisement.Pattern);
            window.InputBooks.Add(book);
            var capture = typeof(MainWindow).GetMethod("CaptureProjectDocument", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.Equal("本书广告", ((EasyPubProjectDocument)capture.Invoke(window, null)!).Books.Single().CleanupOverride!.Advertisement.Pattern);
            var capturePath = Environment.GetEnvironmentVariable("EASYPUB_RULES_CAPTURE_PATH");
            var editor = new BuiltinCleanupWindow(book.CleanupOverride!, "第一章\n本书广告") { Owner = window };
            editor.Show(); editor.UpdateLayout();
            Assert.Equal(11, FindVisualDescendants<ComboBox>(editor).First().Items.Count);
            FindVisualDescendants<ComboBox>(editor).First().SelectedValue = nameof(TextCleanupOptions.RemoveSiteNotices);
            editor.UpdateLayout();
            if (!string.IsNullOrWhiteSpace(capturePath)) CaptureWindowVisual(editor, capturePath + "-rules.png");
            editor.Close();
            Assert.Equal("本书广告", book.CleanupOverride!.Advertisement.Pattern);
            book.SetCleanupOverride(null);
            Assert.Null(book.CleanupOverride);
            Assert.Null(typeof(MainWindow).Assembly.GetType("EasyPub.Desktop.WelcomeGuideWindow"));
        });
    }

    [Fact]
    public void Editable_combo_template_accepts_custom_text()
    {
        RunInWindow(window =>
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "EasyPub.Desktop", "App.xaml"))) root = root.Parent;
            Assert.NotNull(root);
            var document = System.Xml.Linq.XDocument.Load(Path.Combine(root.FullName, "src", "EasyPub.Desktop", "App.xaml"));
            System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var element = document.Descendants(ns + "Style").Single(node => (string?)node.Attribute("TargetType") == "ComboBox");
            element.SetAttributeValue(System.Xml.Linq.XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");
            var combo = new ComboBox { IsEditable = true, Style = (Style)System.Windows.Markup.XamlReader.Parse(element.ToString()) };
            combo.ApplyTemplate();
            var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
            editor.Text = "自定义分类";
            Assert.Equal("自定义分类", combo.Text);
            combo.Text = "zh-Hant";
            Assert.Equal("zh-Hant", editor.Text);
            combo.IsReadOnly = true;
            Assert.True(editor.IsReadOnly);
        });
    }

    [Fact]
    public void Minimum_width_activates_compact_layout_and_keeps_bottom_controls()
    {
        RunInWindow(window =>
        {
            window.Width = window.MinWidth;
            window.UpdateLayout();
            Assert.Equal(132, Assert.IsType<ColumnDefinition>(window.FindName("SidebarColumn")).Width.Value);
            Assert.True(Assert.IsType<ComboBox>(window.FindName("FormatCombo")).IsVisible);
            Assert.True(Assert.IsType<ComboBox>(window.FindName("LayoutModeCombo")).IsVisible);
            Assert.True(Assert.IsType<TextBox>(window.FindName("BookSearchText")).ActualWidth >= 150);
            var navigation = Assert.IsType<RadioButton>(window.FindName("ConvertNavigationButton"));
            navigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, navigation));
            window.UpdateLayout();
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Border>(window.FindName("ConversionSummaryCard")).Visibility);
            var pane = Assert.IsType<ConversionSettingsWindow>(window.FindName("ConversionSettingsPane"));
            var tabs = Assert.IsType<TabControl>(pane.FindName("CategoryTabs"));
            Assert.Equal(FontWeights.Normal, Assert.IsType<TabItem>(tabs.SelectedItem).FontWeight);
            var cancel = Assert.IsType<Button>(pane.FindName("CancelSettingsButton"));
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cancel));
            Assert.Equal(Visibility.Visible, Assert.IsType<Border>(window.FindName("ConversionSummaryCard")).Visibility);
        });
    }

    [Fact]
    public void Conversion_draft_feedback_tracks_changes_and_reverting()
    {
        RunInWindow(window =>
        {
            var navigation = Assert.IsType<RadioButton>(window.FindName("ConvertNavigationButton"));
            navigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, navigation));
            var pane = Assert.IsType<ConversionSettingsWindow>(window.FindName("ConversionSettingsPane"));
            var text = Assert.IsType<TextBox>(pane.FindName("OutputDirectoryText"));
            var feedback = Assert.IsType<TextBlock>(pane.FindName("DraftFeedbackText"));
            var original = text.Text;
            Assert.Equal("当前设置未修改", feedback.Text);
            text.Text = Path.Combine(Path.GetTempPath(), "changed-output");
            PumpDispatcherUntil(() => feedback.Text == "有未应用的修改", TimeSpan.FromSeconds(1));
            text.Text = original;
            PumpDispatcherUntil(() => feedback.Text == "当前设置未修改", TimeSpan.FromSeconds(1));
            text.Text = string.Empty;
            PumpDispatcherUntil(() => feedback.Text == "请选择输出目录。", TimeSpan.FromSeconds(1));
        });
    }

    [Fact]
    public void Ink_workspace_uses_five_task_pages_without_a_duplicate_chapter_page()
    {
        RunInWindow(window =>
        {
            Assert.IsType<TextBox>(window.FindName("BookSearchText"));
            Assert.IsType<Button>(window.FindName("SettingsButton"));
            Assert.Null(window.FindName("HeaderSubtitleText"));
            var shortcutManagerButton = Assert.IsType<Button>(window.FindName("ShortcutManagerButton"));
            Assert.IsType<Button>(window.FindName("AddBooksButton"));
            Assert.Null(window.FindName("AddBooksMenu"));
            Assert.IsType<MenuItem>(window.FindName("FavoriteFoldersMenu"));
            Assert.IsType<Border>(window.FindName("SidebarPanel"));
            Assert.IsType<Border>(window.FindName("BottomOperationBar"));
            Assert.DoesNotContain(FindVisualDescendants<Button>(window), button => Equals(button.Content, "☷") || Equals(button.Content, "▦"));
            Assert.IsType<Button>(window.FindName("InkManageIllustrationsButton"));
            Assert.IsType<Button>(window.FindName("QuickEditTextButton"));
            Assert.IsType<Button>(window.FindName("EditSourceTextButton"));
            var bottomFormat = Assert.IsType<ComboBox>(window.FindName("FormatCombo"));
            var bottomPreset = Assert.IsType<ComboBox>(window.FindName("LayoutModeCombo"));
            var managePreset = Assert.IsType<Button>(window.FindName("ManagePresetButton"));
            Assert.True(bottomFormat.IsVisible);
            Assert.True(bottomPreset.IsVisible);
            Assert.True(managePreset.IsVisible);
            Assert.Equal("原版兼容", ((ComboBoxItem)bottomPreset.SelectedItem).Content);

            var captureTheme = Environment.GetEnvironmentVariable("EASYPUB_SETTINGS_CAPTURE_THEME") ?? "Light";
            var settingsWindow = new SettingsWindow(
                captureTheme, "Comfortable", 100, true, false,
                Path.GetTempPath(), string.Empty, "notepad.exe", 1, false, 10, false, false,
                new Dictionary<string, string>(), 0, () => { });
            settingsWindow.Show();
            settingsWindow.UpdateLayout();
            Assert.Equal("notepad.exe", Assert.IsType<TextBox>(settingsWindow.FindName("TextEditorPathText")).Text);
            Assert.DoesNotContain(FindVisualDescendants<TextBox>(settingsWindow), textBox =>
                Equals(textBox.Text, "搜索设置"));
            Assert.True(Assert.IsType<Button>(settingsWindow.FindName("ResetAllDefaultsButton")).IsEnabled);
            var settingsCapturePath = Environment.GetEnvironmentVariable("EASYPUB_SETTINGS_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(settingsCapturePath)) CaptureWindowVisual(settingsWindow, settingsCapturePath);
            settingsWindow.Close();

            var settingsOpenedFromWorkspace = false;
            var closeSettingsTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            closeSettingsTimer.Tick += (_, _) =>
            {
                var opened = window.OwnedWindows.OfType<SettingsWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (opened is null) return;
                settingsOpenedFromWorkspace = true;
                closeSettingsTimer.Stop();
                opened.DialogResult = false;
            };
            closeSettingsTimer.Start();
            var settingsButton = Assert.IsType<Button>(window.FindName("SettingsButton"));
            settingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, settingsButton));
            Assert.True(settingsOpenedFromWorkspace, "从工作区点击设置按钮后应打开设置窗口。");

            var shortcutManagerOpened = false;
            var closeShortcutTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            closeShortcutTimer.Tick += (_, _) =>
            {
                var opened = window.OwnedWindows.OfType<ShortcutManagerWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (opened is null) return;
                shortcutManagerOpened = true;
                closeShortcutTimer.Stop();
                opened.DialogResult = false;
            };
            closeShortcutTimer.Start();
            shortcutManagerButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, shortcutManagerButton));
            Assert.True(shortcutManagerOpened, "主界面应提供可见且可用的快捷键管理入口。");

            (string Navigation, string Title, string Page)[] cases =
            {
                ("LibraryNavigationButton", "书库", "InkLibraryPage"),
                ("CoverNavigationButton", "封面信息", "InkCoverPage"),
                ("LayoutNavigationButton", "排版插图", "InkLayoutPage"),
                ("ConvertNavigationButton", "转换输出", "InkConvertPage"),
                ("TasksNavigationButton", "任务中心", "TaskCenterLanding"),
            };

            foreach (var (navigationName, title, pageName) in cases)
            {
                var navigation = Assert.IsType<RadioButton>(window.FindName(navigationName));
                navigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, navigation));
                window.UpdateLayout();
                Assert.True(navigation.IsChecked);
                Assert.Equal(title, Assert.IsType<TextBlock>(window.FindName("PageTitleText")).Text);
                Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName(pageName)).Visibility);

                foreach (var (_, _, otherPageName) in cases.Where(item => item.Page != pageName))
                    Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(window.FindName(otherPageName)).Visibility);
            }

            Assert.Equal(Visibility.Collapsed, Assert.IsType<RadioButton>(window.FindName("ChaptersNavigationButton")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<Grid>(window.FindName("InkChaptersPage")).Visibility);

            var layoutNavigation = Assert.IsType<RadioButton>(window.FindName("LayoutNavigationButton"));
            layoutNavigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, layoutNavigation));
            var marginsNavigation = Assert.IsType<RadioButton>(window.FindName("LayoutMarginsNav"));
            marginsNavigation.IsChecked = true;
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, Assert.IsType<StackPanel>(window.FindName("LayoutMarginsPanel")).Visibility);
            Assert.Equal(Visibility.Collapsed, Assert.IsType<StackPanel>(window.FindName("LayoutBasePanel")).Visibility);

            var visibleTopMargin = Assert.IsType<TextBox>(window.FindName("VisibleMarginTopText"));
            visibleTopMargin.Text = "24";
            Assert.Equal("24", Assert.IsType<TextBox>(window.FindName("PageMarginTopText")).Text);
            Assert.True(Assert.IsType<Border>(window.FindName("PreviewDeviceFrame")).Padding.Top >= 48);

            var deviceCombo = Assert.IsType<ComboBox>(window.FindName("KindleModelCombo"));
            deviceCombo.SelectedItem = deviceCombo.Items.OfType<KindleDeviceProfile>().Single(item => item.Id == "custom");
            Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("CustomKindleSizePanel")).Visibility);
            Assert.IsType<TextBox>(window.FindName("CustomKindleWidthText")).Text = "900";
            Assert.IsType<TextBox>(window.FindName("CustomKindleHeightText")).Text = "1200";
            window.UpdateLayout();
            Assert.Contains("900 × 1200", Assert.IsType<TextBlock>(window.FindName("PreviewDeviceStatusText")).Text);

            var setPreview = typeof(MainWindow).GetMethod("SetLayoutPreviewSample", BindingFlags.Instance | BindingFlags.NonPublic)!;
            setPreview.Invoke(window, ["第一章 分页测试", string.Join("\n\n", Enumerable.Repeat("这是一段用于验证 Kindle 书页翻页功能的较长正文。", 80))]);
            window.UpdateLayout();
            var nextPage = Assert.IsType<Button>(window.FindName("NextLayoutPreviewPageButton"));
            var pageText = Assert.IsType<TextBlock>(window.FindName("LayoutPreviewPageText"));
            var previewBody = Assert.IsType<TextBlock>(window.FindName("LayoutPreviewBody"));
            var firstPageBody = previewBody.Text;
            Assert.True(nextPage.IsEnabled);
            Assert.StartsWith("第 1 / ", pageText.Text);
            Assert.NotEqual("第 1 / 1 页", pageText.Text);
            nextPage.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, nextPage));
            window.UpdateLayout();
            Assert.StartsWith("第 2 / ", pageText.Text);
            Assert.NotEqual(firstPageBody, previewBody.Text);

            var fullWidthIndentCheck = Assert.IsType<CheckBox>(window.FindName("FullWidthIndentCheck"));
            var fullWidthIndentCount = Assert.IsType<ComboBox>(window.FindName("FullWidthIndentCountCombo"));
            fullWidthIndentCheck.IsChecked = true;
            setPreview.Invoke(window, ["第一章 缩进测试", "正文第一段。\n\n正文第二段。\n"]);
            window.UpdateLayout();
            Assert.StartsWith("　　正文第一段", previewBody.Text);
            fullWidthIndentCount.SelectedItem = fullWidthIndentCount.Items.OfType<ComboBoxItem>()
                .Single(item => Equals(item.Tag?.ToString(), "3"));
            window.UpdateLayout();
            Assert.StartsWith("　　　正文第一段", previewBody.Text);
            var captureProfile = typeof(MainWindow).GetMethod("CaptureProfile", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var optimizeMobiPackaging = Assert.IsType<CheckBox>(window.FindName("OptimizeMobiPackagingCheck"));
            Assert.Equal("优化超长小说编译速度", optimizeMobiPackaging.Content);
            Assert.False(optimizeMobiPackaging.IsChecked);
            optimizeMobiPackaging.IsChecked = true;
            var captured = Assert.IsType<ConversionProfile>(captureProfile.Invoke(window, null));
            Assert.True(captured.Options.AddFullWidthIndent);
            Assert.Equal(3, captured.Options.FullWidthIndentCount);
            Assert.True(captured.Options.Mobi.OptimizeContentPackaging);

            var convertFormatSummary = Assert.IsType<TextBlock>(window.FindName("ConvertFormatSummaryText"));
            bottomFormat.SelectedIndex = 0;
            Assert.Equal("EPUB", convertFormatSummary.Text);
            bottomFormat.SelectedIndex = 1;
            Assert.Equal("MOBI", convertFormatSummary.Text);

            Assert.Null(window.FindName("MainContentScrollViewer"));
            var mainContent = Assert.IsType<Grid>(window.FindName("MainContentGrid"));
            foreach (var width in new[] { 1120d, 1440d, 1750d })
            {
                window.Width = width;
                window.UpdateLayout();
                Assert.True(mainContent.ActualWidth <= window.ActualWidth,
                    $"窗口宽度 {width:F0} 时主内容区域宽于窗口：Main={mainContent.ActualWidth:F1}, Window={window.ActualWidth:F1}");
            }
        });
    }

    [Fact]
    public void Library_keeps_per_book_cover_preview_and_selected_book_summary()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-layout-book-{Guid.NewGuid():N}.txt");
        var coverPath = Path.Combine(Path.GetTempPath(), $"easypub-layout-cover-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllText(inputPath, "第一章 雨夜\n正文");
            WriteTestCover(coverPath);
            RunInWindow(window =>
            {
                var filesList = Assert.IsType<ListBox>(window.FindName("FilesList"));
                var selectedSummary = Assert.IsType<TextBlock>(window.FindName("SelectedBookSummaryText"));
                var coverPreview = Assert.IsType<Border>(window.FindName("CoverPreviewBorder"));
                var openCoverPreview = Assert.IsType<Button>(window.FindName("OpenCoverPreviewButton"));
                var book = new InputBookItem(inputPath);
                window.InputBooks.Add(book);
                filesList.SelectedItem = book;
                window.UpdateLayout();

                var rowSelectionCheckBox = Assert.Single(FindVisualDescendants<CheckBox>(filesList));
                Assert.True(rowSelectionCheckBox.IsChecked);
                rowSelectionCheckBox.IsChecked = false;
                window.UpdateLayout();
                Assert.Empty(filesList.SelectedItems);
                rowSelectionCheckBox.IsChecked = true;
                window.UpdateLayout();
                Assert.Same(book, filesList.SelectedItem);

                Assert.Contains("封面：无", selectedSummary.Text);
                Assert.True(openCoverPreview.IsEnabled);
                Assert.Equal(Visibility.Visible, coverPreview.Visibility);
                var bookListCard = Assert.IsType<Border>(window.FindName("BookListSection"));
                var coverPanel = Assert.IsType<Border>(window.FindName("CoverDropPanel"));
                var coverGap = Assert.IsType<ColumnDefinition>(window.FindName("CoverGapColumn"));
                Assert.True(bookListCard.CornerRadius.TopLeft >= 8 && bookListCard.BorderThickness.Left >= 1,
                    "书库列表应是独立的圆角卡片，而不是依靠中间分隔线。 ");
                Assert.True(coverPanel.CornerRadius.TopLeft >= 8 && coverPanel.BorderThickness.Left >= 1,
                    "所选书稿应是独立的圆角卡片。 ");
                Assert.True(coverGap.ActualWidth >= 12, "书库列表与所选书稿之间应保留明显卡片间距。 ");
                var quickEdit = Assert.IsType<Button>(window.FindName("QuickEditTextButton"));
                Assert.True(quickEdit.ActualHeight <= 36,
                    $"编辑原始 TXT 应为紧凑操作，当前高度 {quickEdit.ActualHeight:F1}px。 ");
                var quickMetadata = Assert.IsType<Button>(window.FindName("QuickMetadataButton"));
                Assert.IsType<Grid>(quickMetadata.Content);
                Assert.True(quickMetadata.ActualHeight >= 54, "封面信息快捷入口应保留图标、说明与足够点击面积。 ");
                Assert.IsType<ScrollViewer>(window.FindName("SelectedBookScrollViewer")).ScrollToBottom();
                window.UpdateLayout();
                var quickMetadataBottom = quickMetadata.TranslatePoint(new Point(0, quickMetadata.ActualHeight), coverPanel).Y;
                Assert.True(quickMetadataBottom < 560,
                    $"右侧工具距离所选书稿信息过远：按钮底部位于 {quickMetadataBottom:F1}px。 ");
                Assert.True(quickMetadataBottom <= coverPanel.ActualHeight - 8,
                    $"封面信息快捷入口被右侧卡片裁切：按钮底部 {quickMetadataBottom:F1}px，卡片高度 {coverPanel.ActualHeight:F1}px。 ");
                var quickCleanup = Assert.IsType<Button>(window.FindName("QuickCleanupButton"));
                var quickCleanupBottom = quickCleanup.TranslatePoint(new Point(0, quickCleanup.ActualHeight), coverPanel).Y;
                Assert.True(quickCleanup.IsVisible && quickCleanupBottom <= coverPanel.ActualHeight - 8,
                    $"文本清理快捷入口被右侧卡片裁切：按钮底部 {quickCleanupBottom:F1}px，卡片高度 {coverPanel.ActualHeight:F1}px。 ");

                book.CoverImagePath = coverPath;
                PumpDispatcherUntil(() => book.CoverThumbnail is not null, TimeSpan.FromSeconds(3));
                Assert.NotNull(book.CoverThumbnail);
                Assert.Equal(Visibility.Visible, book.CoverThumbnailVisibility);
                Assert.Equal(Visibility.Collapsed, book.CoverThumbnailPlaceholderVisibility);
                var capturePath = Environment.GetEnvironmentVariable("EASYPUB_LIBRARY_CAPTURE_PATH");
                if (!string.IsNullOrWhiteSpace(capturePath)) CaptureWindowVisual(window, capturePath);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(coverPath)) File.Delete(coverPath);
        }
    }

    [Fact]
    public void Unified_conversion_settings_exposes_five_categories_without_advanced_expanders()
    {
        RunInWindow(window =>
        {
            Assert.IsType<Button>(window.FindName("AdjustConversionSettingsButton"));
            var navigation = Assert.IsType<RadioButton>(window.FindName("ConvertNavigationButton"));
            navigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, navigation));
            window.UpdateLayout();

            var settings = Assert.IsType<ConversionSettingsWindow>(window.FindName("ConversionSettingsPane"));
            var host = Assert.IsType<Border>(window.FindName("ConversionSettingsPaneHost"));
            var adjust = Assert.IsType<Button>(window.FindName("AdjustConversionSettingsButton"));
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.Equal(Visibility.Collapsed, adjust.Visibility);
            var tabs = Assert.IsType<TabControl>(settings.FindName("CategoryTabs"));
            Assert.Equal(
                ["基本输出", "EPUB 输入", "MOBI / Kindle", "性能与文件", "验收与完成"],
                tabs.Items.OfType<TabItem>().Select(tab => tab.Header?.ToString() ?? string.Empty).ToArray());
            Assert.IsType<ComboBox>(settings.FindName("OutputFormatCombo"));
            Assert.IsType<RadioButton>(settings.FindName("PreserveEpubRadio"));
            Assert.IsType<TextBlock>(settings.FindName("KindleGenStatusText"));
            Assert.IsType<ComboBox>(settings.FindName("ParallelismCombo"));
            Assert.IsType<CheckBox>(settings.FindName("ArtifactValidationCheck"));
            Assert.DoesNotContain(FindVisualDescendants<Expander>(settings), _ => true);
            var cancel = Assert.IsType<Button>(settings.FindName("CancelSettingsButton"));
            cancel.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, cancel));
            Assert.Equal(Visibility.Collapsed, host.Visibility);
            Assert.Equal(Visibility.Visible, adjust.Visibility);
            adjust.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, adjust));
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, host.Visibility);
            Assert.Equal(Visibility.Collapsed, adjust.Visibility);
            var capturePath = Environment.GetEnvironmentVariable("EASYPUB_CONVERSION_SETTINGS_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath))
            {
                if (double.TryParse(Environment.GetEnvironmentVariable("EASYPUB_CAPTURE_WIDTH"), out var captureWidth)) window.Width = captureWidth;
                if (Environment.GetEnvironmentVariable("EASYPUB_CAPTURE_THEME") is { } theme)
                    typeof(MainWindow).Assembly.GetType("EasyPub.Desktop.ThemeManager")!.GetMethod("Apply")!.Invoke(null, [theme, window]);
                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    window.UpdateLayout();
                    var tabCapturePath = index == 0
                        ? capturePath
                        : Path.Combine(
                            Path.GetDirectoryName(capturePath)!,
                            $"{Path.GetFileNameWithoutExtension(capturePath)}-tab{index + 1}{Path.GetExtension(capturePath)}");
                    CaptureWindowVisual(window, tabCapturePath);
                }
            }
        });
    }

    [Fact]
    public void Cover_page_exposes_one_real_batch_metadata_button_in_the_top_bar()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-cover-batch-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputPath, "第一章 雨夜\n正文");
            RunInWindow(window =>
            {
                window.InputBooks.Add(new InputBookItem(inputPath));
                var coverNavigation = Assert.IsType<RadioButton>(window.FindName("CoverNavigationButton"));
                coverNavigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, coverNavigation));
                window.UpdateLayout();

                var batchButton = Assert.IsType<Button>(window.FindName("CoverBatchMetadataButton"));
                Assert.Equal("批量编辑", batchButton.Content);
                Assert.Single(FindVisualDescendants<Button>(window), button => Equals(button.Content, "批量编辑"));

                var batchEditorOpened = false;
                var closeBatchTimer = new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromMilliseconds(100),
                };
                closeBatchTimer.Tick += (_, _) =>
                {
                    var opened = window.OwnedWindows.OfType<BatchMetadataWindow>()
                        .FirstOrDefault(candidate => candidate.IsVisible);
                    if (opened is null) return;
                    batchEditorOpened = true;
                    closeBatchTimer.Stop();
                    opened.DialogResult = false;
                };
                closeBatchTimer.Start();
                batchButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, batchButton));
                Assert.True(batchEditorOpened, "封面信息页顶部的批量编辑按钮应打开真实批量元数据窗口。");
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
    }

    [Fact]
    public void Import_controls_metadata_mapping_and_kindle_models_are_complete()
    {
        RunInWindow(window =>
        {
            var addBooksButton = Assert.IsType<Button>(window.FindName("AddBooksButton"));
            Assert.Equal("添加书稿", addBooksButton.Content);
            Assert.Contains(FindVisualDescendants<Button>(window), button => Equals(button.Content, "导入文件夹"));

            Assert.IsType<Menu>(window.FindName("FavoriteImportMenu"));

            var coverNavigation = Assert.IsType<RadioButton>(window.FindName("CoverNavigationButton"));
            coverNavigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, coverNavigation));
            var mappingButton = Assert.IsType<Button>(window.FindName("MetadataMappingButton"));
            foreach (var width in new[] { 1120d, 1280d, 1440d })
            {
                window.Width = width;
                window.UpdateLayout();
                Assert.True(mappingButton.ActualWidth >= 180,
                    $"窗口宽度 {width:F0}px 时，文件夹元数据映射按钮被压缩为 {mappingButton.ActualWidth:F1}px。");
            }

            var mappingOpened = false;
            var closeMappingTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            closeMappingTimer.Tick += (_, _) =>
            {
                var opened = window.OwnedWindows.OfType<MetadataMappingWindow>().FirstOrDefault(candidate => candidate.IsVisible);
                if (opened is null) return;
                mappingOpened = true;
                closeMappingTimer.Stop();
                opened.DialogResult = false;
            };
            closeMappingTimer.Start();
            mappingButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, mappingButton));
            Assert.True(mappingOpened, "文件夹元数据映射按钮应能打开映射窗口。");

            var modelCombo = Assert.IsType<ComboBox>(window.FindName("KindleModelCombo"));
            var layoutNavigation = Assert.IsType<RadioButton>(window.FindName("LayoutNavigationButton"));
            layoutNavigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, layoutNavigation));
            window.UpdateLayout();
            Assert.True(modelCombo.Items.Count >= 4, "Kindle 型号选择至少应覆盖常用 6、6.8/7 和 10.2 英寸设备。");
            modelCombo.SelectedItem = modelCombo.Items.OfType<KindleDeviceProfile>().Single(item => item.Id == "kpw5");
            window.UpdateLayout();
            Assert.Equal(390, Assert.IsType<Border>(window.FindName("PreviewDeviceFrame")).Width);
            Assert.Equal(520, Assert.IsType<Border>(window.FindName("PreviewDeviceFrame")).Height);
            modelCombo.SelectedItem = modelCombo.Items.OfType<KindleDeviceProfile>().Single(item => item.Id == "scribe");
            window.UpdateLayout();
            Assert.Contains("Scribe", Assert.IsType<TextBlock>(window.FindName("PreviewDeviceStatusText")).Text);
            Assert.True(Assert.IsType<Border>(window.FindName("PreviewDeviceFrame")).Height >= 600);

            var convertNavigation = Assert.IsType<RadioButton>(window.FindName("ConvertNavigationButton"));
            convertNavigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, convertNavigation));
            window.UpdateLayout();
            Assert.Equal(Visibility.Visible, Assert.IsType<Button>(window.FindName("BrowseLegacyConfigButton")).Visibility);
            Assert.Equal(Visibility.Visible, Assert.IsType<TextBlock>(window.FindName("LegacyConfigStatusText")).Visibility);
        });
    }

    [Fact]
    public void Per_book_action_button_opens_a_working_context_menu()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-book-actions-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputPath, "第一章 测试\r\n正文");
            RunInWindow(window =>
            {
                var book = new InputBookItem(inputPath);
                window.InputBooks.Add(book);
                window.UpdateLayout();

                var list = Assert.IsType<ListBox>(window.FindName("FilesList"));
                list.ScrollIntoView(book);
                window.UpdateLayout();
                var actionButton = FindVisualDescendants<Button>(list)
                    .First(button => AutomationProperties.GetName(button) == "书稿操作");
                actionButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, actionButton));

                Assert.Same(book, list.SelectedItem);
                Assert.NotNull(actionButton.ContextMenu);
                Assert.True(actionButton.ContextMenu!.IsOpen);
                Assert.Contains(actionButton.ContextMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "编辑封面信息"));
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
    }

    [Fact]
    public void Library_plain_left_click_replaces_the_previous_selection()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"easypub-selection-a-{Guid.NewGuid():N}.txt");
        var secondPath = Path.Combine(Path.GetTempPath(), $"easypub-selection-b-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(firstPath, "第一章 A\r\n正文");
            File.WriteAllText(secondPath, "第一章 B\r\n正文");
            RunInWindow(window =>
            {
                var first = new InputBookItem(firstPath);
                var second = new InputBookItem(secondPath);
                window.InputBooks.Add(first);
                window.InputBooks.Add(second);

                var list = Assert.IsType<ListBox>(window.FindName("FilesList"));
                list.SelectedItem = first;
                list.ScrollIntoView(second);
                window.UpdateLayout();
                var secondRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(second));
                var click = new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = secondRow,
                };
                var clickHandler = typeof(MainWindow).GetMethod(
                    "FilesList_PreviewMouseLeftButtonDown",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(clickHandler);
                clickHandler!.Invoke(window, [list, click]);

                Assert.Single(list.SelectedItems);
                Assert.Same(second, list.SelectedItem);
            });
        }
        finally
        {
            if (File.Exists(firstPath)) File.Delete(firstPath);
            if (File.Exists(secondPath)) File.Delete(secondPath);
        }
    }

    [Fact]
    public void Library_ctrl_left_click_adds_and_removes_books_without_losing_other_selections()
    {
        RunInWindow(window =>
        {
            var first = new InputBookItem(Path.Combine(Path.GetTempPath(), "easypub-ctrl-selection-a.txt"));
            var second = new InputBookItem(Path.Combine(Path.GetTempPath(), "easypub-ctrl-selection-b.txt"));
            window.InputBooks.Add(first);
            window.InputBooks.Add(second);
            var list = Assert.IsType<ListBox>(window.FindName("FilesList"));
            list.SelectedItem = first;
            list.ScrollIntoView(second);
            window.UpdateLayout();
            var secondRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(second));

            MainWindow.ApplyLibrarySelection(list, secondRow, extendSelection: true);
            Assert.Equal(2, list.SelectedItems.Count);
            Assert.Contains(first, list.SelectedItems.Cast<InputBookItem>());
            Assert.Contains(second, list.SelectedItems.Cast<InputBookItem>());

            MainWindow.ApplyLibrarySelection(list, secondRow, extendSelection: true);
            Assert.Single(list.SelectedItems);
            Assert.Contains(first, list.SelectedItems.Cast<InputBookItem>());
        });
    }

    [Fact]
    public void Library_row_checkboxes_allow_independent_toggle_without_modifier_keys()
    {
        RunInWindow(window =>
        {
            var first = new InputBookItem(Path.Combine(Path.GetTempPath(), "easypub-checkbox-selection-a.txt"));
            var second = new InputBookItem(Path.Combine(Path.GetTempPath(), "easypub-checkbox-selection-b.txt"));
            window.InputBooks.Add(first);
            window.InputBooks.Add(second);
            var list = Assert.IsType<ListBox>(window.FindName("FilesList"));
            list.UnselectAll();
            list.ScrollIntoView(first);
            list.ScrollIntoView(second);
            window.UpdateLayout();

            var firstRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(first));
            var secondRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(second));
            var firstCheckBox = Assert.Single(FindVisualDescendants<CheckBox>(firstRow));
            var secondCheckBox = Assert.Single(FindVisualDescendants<CheckBox>(secondRow));
            var previewHandler = typeof(MainWindow).GetMethod(
                "FilesList_PreviewMouseLeftButtonDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(previewHandler);

            void PreviewCheckBoxClick(CheckBox checkBox)
            {
                var click = new System.Windows.Input.MouseButtonEventArgs(
                    System.Windows.Input.Mouse.PrimaryDevice,
                    Environment.TickCount,
                    System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
                    Source = checkBox,
                };
                previewHandler!.Invoke(window, [list, click]);
                Assert.False(click.Handled);
            }

            PreviewCheckBoxClick(firstCheckBox);
            firstCheckBox.IsChecked = true;
            PreviewCheckBoxClick(secondCheckBox);
            secondCheckBox.IsChecked = true;
            Assert.Equal(2, list.SelectedItems.Count);
            var capturePath = Environment.GetEnvironmentVariable("EASYPUB_LIBRARY_SELECTION_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath)) CaptureWindowVisual(window, capturePath);

            PreviewCheckBoxClick(firstCheckBox);
            firstCheckBox.IsChecked = false;
            Assert.Single(list.SelectedItems);
            Assert.Contains(second, list.SelectedItems.Cast<InputBookItem>());
        });
    }

    [Fact]
    public void Library_multi_selection_keeps_inspector_visible_and_cycles_without_changing_selection()
    {
        RunInWindow(window =>
        {
            var first = new InputBookItem(Path.Combine(Path.GetTempPath(), "easypub-inspector-a.txt"));
            var second = new InputBookItem(Path.Combine(Path.GetTempPath(), "easypub-inspector-b.txt"));
            window.InputBooks.Add(first);
            window.InputBooks.Add(second);
            var list = Assert.IsType<ListBox>(window.FindName("FilesList"));
            list.SelectAll();
            window.UpdateLayout();

            var inspector = Assert.IsType<Border>(window.FindName("CoverDropPanel"));
            var navigation = Assert.IsType<StackPanel>(window.FindName("SelectedBookNavigationPanel"));
            var position = Assert.IsType<TextBlock>(window.FindName("SelectedBookPositionText"));
            var title = Assert.IsType<TextBlock>(window.FindName("SelectedBookNameText"));
            var previous = Assert.IsType<Button>(window.FindName("PreviousSelectedBookButton"));
            var next = Assert.IsType<Button>(window.FindName("NextSelectedBookButton"));
            Assert.True(inspector.IsVisible);
            Assert.True(navigation.IsVisible);
            Assert.Equal("2 / 2", position.Text);
            Assert.Equal(second.DisplayName, title.Text);

            previous.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, previous));
            window.UpdateLayout();
            Assert.Equal(2, list.SelectedItems.Count);
            Assert.Equal("1 / 2", position.Text);
            Assert.Equal(first.DisplayName, title.Text);

            next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, next));
            window.UpdateLayout();
            Assert.Equal(2, list.SelectedItems.Count);
            Assert.Equal("2 / 2", position.Text);
            Assert.Equal(second.DisplayName, title.Text);
            Assert.True(Assert.IsType<Button>(window.FindName("QuickChapterButton")).IsEnabled);

            var capturePath = Environment.GetEnvironmentVariable("EASYPUB_LIBRARY_INSPECTOR_CAPTURE_PATH");
            if (!string.IsNullOrWhiteSpace(capturePath)) CaptureWindowVisual(window, capturePath);
        });
    }

    [Fact]
    public void Library_multi_selection_right_click_exposes_batch_actions()
    {
        RunInWindow(window =>
        {
            var first = new InputBookItem(Path.Combine(Path.GetTempPath(), "easypub-context-a.txt"));
            var second = new InputBookItem(Path.Combine(Path.GetTempPath(), "easypub-context-b.txt"));
            window.InputBooks.Add(first);
            window.InputBooks.Add(second);
            var list = Assert.IsType<ListBox>(window.FindName("FilesList"));
            list.SelectAll();
            list.ScrollIntoView(second);
            window.UpdateLayout();
            var secondRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(second));
            var rightClick = new System.Windows.Input.MouseButtonEventArgs(
                System.Windows.Input.Mouse.PrimaryDevice,
                Environment.TickCount,
                System.Windows.Input.MouseButton.Right)
            {
                RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
                Source = secondRow,
            };
            var handler = typeof(MainWindow).GetMethod(
                "FilesList_PreviewMouseRightButtonDown",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(handler);
            handler!.Invoke(window, [list, rightClick]);

            Assert.NotNull(secondRow.ContextMenu);
            var labels = secondRow.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToArray();
            Assert.Contains(labels, label => label?.StartsWith("批量编辑元数据", StringComparison.Ordinal) == true);
            Assert.Contains(labels, label => label?.StartsWith("检查所选", StringComparison.Ordinal) == true);
            Assert.Contains(labels, label => label?.StartsWith("转换所选", StringComparison.Ordinal) == true);
            secondRow.ContextMenu.IsOpen = false;
        });
    }

    [Fact]
    public void Conversion_requests_include_only_selected_library_books()
    {
        var firstPath = Path.Combine(Path.GetTempPath(), $"easypub-convert-a-{Guid.NewGuid():N}.txt");
        var secondPath = Path.Combine(Path.GetTempPath(), $"easypub-convert-b-{Guid.NewGuid():N}.txt");
        var outputPath = Path.Combine(Path.GetTempPath(), $"easypub-convert-output-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(firstPath, "第一章 A\r\n正文");
            File.WriteAllText(secondPath, "第一章 B\r\n正文");
            RunInWindow(window =>
            {
                var first = new InputBookItem(firstPath);
                var second = new InputBookItem(secondPath);
                window.InputBooks.Add(first);
                window.InputBooks.Add(second);
                Assert.IsType<TextBox>(window.FindName("OutputDirectoryText")).Text = outputPath;

                var list = Assert.IsType<ListBox>(window.FindName("FilesList"));
                list.SelectedItem = second;
                var buildRequests = typeof(MainWindow).GetMethod(
                    "BuildConversionRequestsAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(buildRequests);
                var task = Assert.IsAssignableFrom<Task<IReadOnlyList<ConversionRequest>>>(buildRequests!.Invoke(window, null));
                var requests = task.GetAwaiter().GetResult();

                var request = Assert.Single(requests);
                Assert.Equal(secondPath, request.InputPath);
                Assert.Single(Assert.IsType<ListBox>(window.FindName("ConversionSelectionList")).Items);
                Assert.True(Assert.IsType<Button>(window.FindName("ConvertButton")).IsEnabled);
            });
        }
        finally
        {
            if (File.Exists(firstPath)) File.Delete(firstPath);
            if (File.Exists(secondPath)) File.Delete(secondPath);
            if (Directory.Exists(outputPath)) Directory.Delete(outputPath, true);
        }
    }

    [Fact]
    public void Adding_txt_loads_a_lazy_preview_without_persisting_a_duplicate_tree()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-auto-tree-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputPath,
                "序章\r\n这是前置内容。\r\n\r\n第一章 雨夜\r\n第一章正文。\r\n\r\n第二章 天明\r\n第二章正文。");
            RunInWindow(window =>
            {
                var addFiles = typeof(MainWindow).GetMethod("AddFiles", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(addFiles);
                addFiles!.Invoke(window, [new[] { inputPath }]);

                var book = Assert.Single(window.InputBooks);
                var navigator = Assert.IsType<ListBox>(window.FindName("ChapterNavigatorList"));
                PumpDispatcherUntil(() => navigator.Items.Count >= 3, TimeSpan.FromSeconds(5));
                Assert.Null(book.ChapterTree);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
    }

    [Fact]
    public void Chapter_preview_and_editor_share_the_same_cached_document()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-tree-cache-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputPath, "第一章 雨夜\r\n正文。\r\n第二章 天明\r\n正文。");
            RunInWindow(window =>
            {
                var load = typeof(MainWindow).GetMethod(
                    "GetChapterDocumentAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(load);
                var arguments = new object?[]
                {
                    inputPath,
                    null,
                    new TocHierarchyOptions(),
                    TextEncodingMode.Auto,
                    null,
                    CancellationToken.None,
                };
                var first = Assert.IsAssignableFrom<Task<ChapterTreeDocument>>(load!.Invoke(window, arguments))
                    .GetAwaiter().GetResult();
                var second = Assert.IsAssignableFrom<Task<ChapterTreeDocument>>(load.Invoke(window, arguments))
                    .GetAwaiter().GetResult();

                Assert.Same(first, second);
                arguments[2] = new TocHierarchyOptions { RecognizeNumericHeadings = true, NumericHeadingMinimumBodyLines = 2 };
                var changed = Assert.IsAssignableFrom<Task<ChapterTreeDocument>>(load.Invoke(window, arguments))
                    .GetAwaiter().GetResult();
                Assert.NotSame(first, changed);
                arguments[2] = new TocHierarchyOptions { RecognizeNumericHeadings = true, NumericHeadingMinimumBodyLines = 3 };
                var changedMinimum = Assert.IsAssignableFrom<Task<ChapterTreeDocument>>(load.Invoke(window, arguments))
                    .GetAwaiter().GetResult();
                Assert.NotSame(changed, changedMinimum);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
    }

    [Fact]
    public void Library_exposes_chapter_structure_metadata_and_text_cleanup_as_equal_actions()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-chapter-workspace-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputPath,
                "第一章 雨夜\r\n第一章正文。\r\n\r\n第二章 天明\r\n这里有唯一关键词。\r\n第二章后续正文。");
            RunInWindow(window =>
            {
                var book = new InputBookItem(inputPath);
                window.InputBooks.Add(book);
                var files = Assert.IsType<ListBox>(window.FindName("FilesList"));
                files.SelectedItem = book;
                window.UpdateLayout();

                var chapter = Assert.IsType<Button>(window.FindName("QuickChapterButton"));
                var metadata = Assert.IsType<Button>(window.FindName("QuickMetadataButton"));
                var cleanup = Assert.IsType<Button>(window.FindName("QuickCleanupButton"));
                Assert.True(chapter.IsVisible && chapter.IsEnabled);
                Assert.True(metadata.IsVisible && metadata.IsEnabled);
                Assert.True(cleanup.IsVisible && cleanup.IsEnabled);
                Assert.IsType<Grid>(chapter.Content);
                Assert.IsType<Grid>(metadata.Content);
                Assert.IsType<Grid>(cleanup.Content);
                Assert.InRange(Math.Abs(chapter.ActualHeight - metadata.ActualHeight), 0, 1);
                Assert.InRange(Math.Abs(metadata.ActualHeight - cleanup.ActualHeight), 0, 1);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
    }

    [Fact]
    public void Library_inspector_presents_three_state_analysis_before_editing_tools()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-readiness-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputPath, "第一章 雨夜\r\n正文");
            RunInWindow(window =>
            {
                var book = new InputBookItem(inputPath);
                window.InputBooks.Add(book);
                Assert.IsType<ListBox>(window.FindName("FilesList")).SelectedItem = book;
                book.SetAnalysisSnapshot(new BookAnalysisSnapshot(
                    inputPath,
                    "TXT",
                    new FileInfo(inputPath).Length,
                    File.GetLastWriteTimeUtc(inputPath),
                    1,
                    ReadinessEvaluator.Evaluate([
                        new ConversionPreflightIssue(inputPath, PreflightSeverity.Warning, "chapter", "章节需要确认。", PreflightTargetKind.Chapters)]),
                    [new ConversionPreflightIssue(inputPath, PreflightSeverity.Warning, "chapter", "章节需要确认。", PreflightTargetKind.Chapters)],
                    DateTimeOffset.UtcNow));
                window.UpdateLayout();

                Assert.Equal("建议处理 1", Assert.IsType<TextBlock>(window.FindName("SelectedBookReadinessText")).Text);
                var viewIssues = Assert.IsType<Button>(window.FindName("ViewSelectedBookIssuesButton"));
                Assert.True(viewIssues.IsEnabled);
                Assert.Equal("查看并处理", viewIssues.Content);
                var scroll = Assert.IsType<ScrollViewer>(window.FindName("SelectedBookScrollViewer"));
                scroll.Height = 230;
                window.UpdateLayout();
                Assert.True(scroll.ScrollableHeight > 0);
                scroll.ScrollToBottom();
                window.UpdateLayout();
                Assert.True(scroll.VerticalOffset > 0);
                var cleanup = Assert.IsType<Button>(window.FindName("QuickCleanupButton"));
                var bottom = cleanup.TranslatePoint(new Point(0, cleanup.ActualHeight), scroll);
                Assert.InRange(bottom.Y, 0, scroll.ActualHeight + 1);
                scroll.Height = double.NaN;
                scroll.ScrollToTop();
                window.UpdateLayout();
                var capturePath = Environment.GetEnvironmentVariable("EASYPUB_READINESS_CAPTURE_PATH");
                if (!string.IsNullOrWhiteSpace(capturePath)) CaptureWindowVisual(window, capturePath);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
    }

    [Fact]
    public void Imported_txt_is_automatically_analyzed_without_opening_the_preflight_window()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-auto-analysis-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputPath, "第一章 雨夜\r\n正文");
            RunInWindow(window =>
            {
                Assert.IsType<ComboBox>(window.FindName("FormatCombo")).SelectedIndex = 0;
                var book = new InputBookItem(inputPath);
                window.InputBooks.Add(book);
                Assert.IsType<ListBox>(window.FindName("FilesList")).SelectedItem = book;

                PumpDispatcherUntil(() => book.AnalysisStatus == BookAnalysisStatus.Completed, TimeSpan.FromSeconds(8));

                Assert.True(book.HasBeenChecked);
                Assert.Equal("所选检查通过", book.ReadinessLabel);
                Assert.Equal(1, book.ChapterCandidateCount);
                Assert.Null(book.ChapterTree);
                var summary = Assert.IsType<TextBlock>(window.FindName("SelectedBookSummaryText"));
                Assert.Contains("章节树：已识别 1 项（未保存）", summary.Text);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
    }

    [Fact]
    public void Automatic_check_settings_show_all_rules_and_update_the_enabled_summary()
    {
        RunInWindow(owner =>
        {
            var dialog = new AutomaticCheckWindow(new AutomaticCheckOptions(), []) { Owner = owner };
            try
            {
                if (Application.Current is not null)
                {
                    var menu = Assert.IsType<Menu>(owner.FindName("FavoriteImportMenu"));
                    var top = Assert.IsType<MenuItem>(menu.Items[0]);
                    top.IsSubmenuOpen = true;
                    owner.UpdateLayout();
                    Assert.NotNull(top.Template.FindName("MenuSurface", top));
                    var folders = Assert.IsType<MenuItem>(owner.FindName("FavoriteFoldersMenu"));
                    Assert.NotNull(folders.ItemContainerStyle.BasedOn);
                    Assert.NotNull(folders.Template.FindName("MenuSurface", folders));
                    top.IsSubmenuOpen = false;
                }
                dialog.Show();
                dialog.UpdateLayout();
                var checks = FindVisualDescendants<CheckBox>(dialog).ToArray();
                Assert.Equal(1 + AutomaticCheckOptions.TargetLabels.Count + AutomaticCheckOptions.CleanupLabels.Count, checks.Length);
                var blankLines = checks.Single(check => Equals(check.Content, "合并连续空行"));
                blankLines.IsChecked = true;
                Assert.Contains(FindVisualDescendants<TextBlock>(dialog), text => text.Text.StartsWith("已开启：") && text.Text.Contains("合并连续空行"));
                checks.Single(check => Equals(check.Content, "开启自动检查")).IsChecked = false;
                Assert.False(blankLines.IsEnabled);
                Assert.Contains(FindVisualDescendants<TextBlock>(dialog), text => text.Text == "自动检查已关闭");
                checks.Single(check => Equals(check.Content, "开启自动检查")).IsChecked = true;
                var capture = Environment.GetEnvironmentVariable("EASYPUB_AUTOCHECK_CAPTURE");
                if (!string.IsNullOrWhiteSpace(capture)) CaptureWindowVisual(dialog, capture);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public void Epub_to_mobi_mode_is_visible_for_the_selected_epub()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-visible-epub-mode-{Guid.NewGuid():N}.epub");
        try
        {
            File.WriteAllBytes(inputPath, []);
            RunInWindow(window =>
            {
                var book = new InputBookItem(inputPath);
                window.InputBooks.Add(book);
                var files = Assert.IsType<ListBox>(window.FindName("FilesList"));
                files.SelectedItem = book;
                Assert.IsType<ComboBox>(window.FindName("FormatCombo")).SelectedIndex = 1;
                window.UpdateLayout();

                var panel = Assert.IsType<StackPanel>(window.FindName("EpubInputModePanel"));
                var mode = Assert.IsType<ComboBox>(window.FindName("EpubModeCombo"));
                Assert.Equal(Visibility.Visible, panel.Visibility);
                Assert.Equal("保留原 EPUB 版式（推荐）", ((ComboBoxItem)mode.Items[0]).Content);
                Assert.Equal("EasyPub 兼容重排", ((ComboBoxItem)mode.Items[1]).Content);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
    }

    [Fact]
    public void Task_center_landing_renders_tasks_with_read_only_progress()
    {
        RunInWindow(window =>
        {
            window.BookTasks.Add(new BookTaskViewModel(
                Path.Combine(Path.GetTempPath(), "task-center-input.txt"),
                Path.Combine(Path.GetTempPath(), "task-center-output.mobi")));

            var navigation = Assert.IsType<RadioButton>(window.FindName("TasksNavigationButton"));
            navigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, navigation));
            window.UpdateLayout();

            Assert.Equal(Visibility.Visible, Assert.IsType<Grid>(window.FindName("TaskCenterLanding")).Visibility);
        });
    }

    [Fact]
    public void Cover_page_displays_metadata_from_the_selected_books_folder_mapping()
    {
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-mapped-metadata-{Guid.NewGuid():N}.txt");
        var secondInputPath = Path.Combine(Path.GetTempPath(), $"easypub-cover-independent-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllText(inputPath, "第一章 测试\r\n正文");
            File.WriteAllText(secondInputPath, "第一章 第二本\r\n正文");
            RunInWindow(window =>
            {
                var book = new InputBookItem(inputPath);
                book.SetMetadataOverrides(
                    new BookMetadataOverrides { Publisher = "起点", Language = "zh-CN" },
                    Path.GetDirectoryName(inputPath));
                var secondBook = new InputBookItem(secondInputPath);
                secondBook.SetMetadataOverrides(
                    new BookMetadataOverrides { Publisher = "番茄", Language = "zh-CN" },
                    Path.GetDirectoryName(secondInputPath));
                window.InputBooks.Add(book);
                window.InputBooks.Add(secondBook);

                var filesList = Assert.IsType<ListBox>(window.FindName("FilesList"));
                filesList.SelectedItem = book;
                var navigation = Assert.IsType<RadioButton>(window.FindName("CoverNavigationButton"));
                navigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, navigation));
                window.UpdateLayout();

                Assert.Equal("起点", Assert.IsType<TextBox>(window.FindName("PublisherText")).Text);
                var coverBookCombo = Assert.IsType<ComboBox>(window.FindName("CoverBookCombo"));
                coverBookCombo.SelectedItem = secondBook;
                PumpDispatcherUntil(() => Equals(Assert.IsType<TextBox>(window.FindName("PublisherText")).Text, "番茄"), TimeSpan.FromSeconds(3));
                Assert.Same(book, filesList.SelectedItem);
                Assert.Equal("番茄", Assert.IsType<TextBox>(window.FindName("PublisherText")).Text);
                var capturePath = Environment.GetEnvironmentVariable("EASYPUB_COVER_CAPTURE_PATH");
                if (!string.IsNullOrWhiteSpace(capturePath)) CaptureWindowVisual(window, capturePath);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(secondInputPath)) File.Delete(secondInputPath);
        }
    }

    [Fact]
    public void Custom_cleanup_rule_enable_state_is_writable_for_the_inline_checkbox()
    {
        var row = new TextCleanupRuleRow(new TextCleanupCustomRule { Name = "测试规则", Pattern = "旧", Enabled = true });
        var enabledProperty = typeof(TextCleanupRuleRow).GetProperty(nameof(TextCleanupRuleRow.Enabled));

        Assert.NotNull(enabledProperty);
        Assert.True(enabledProperty!.CanWrite, "规则列表的启用复选框需要可写状态，不能双向绑定到只读属性。");
        enabledProperty.SetValue(row, false);
        Assert.False(row.Rule.Enabled);
    }

    [Fact]
    public void Applying_custom_cleanup_rules_commits_the_rule_currently_being_edited()
    {
        RunInWindow(owner =>
        {
            var capturePath = Environment.GetEnvironmentVariable("EASYPUB_RULES_CAPTURE_PATH");
            var manager = new TextCleanupRuleManagerWindow(
                [new TextCleanupCustomRule { Name = "测试规则", Pattern = "旧文本", Replacement = "旧替换", Enabled = true }],
                "旧文本")
            {
                Owner = owner,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
                Opacity = string.IsNullOrWhiteSpace(capturePath) ? 0 : 1,
            };
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Assert.IsType<TextBox>(manager.FindName("RulePatternText")).Text = "新文本";
                Assert.IsType<TextBox>(manager.FindName("RuleReplacementText")).Text = "新替换";
                Assert.IsType<TextCleanupRuleRow>(Assert.IsType<DataGrid>(manager.FindName("RulesGrid")).Items[0]).Enabled = false;
                manager.UpdateLayout();
                if (!string.IsNullOrWhiteSpace(capturePath)) CaptureWindowVisual(manager, capturePath);
                var applyButton = FindVisualDescendants<Button>(manager).Single(button => Equals(button.Content, "应用规则组"));
                applyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, applyButton));
            };
            timer.Start();

            Assert.True(manager.ShowDialog());
            Assert.Equal("新文本", manager.Rules.Single().Pattern);
            Assert.Equal("新替换", manager.Rules.Single().Replacement);
            Assert.False(manager.Rules.Single().Enabled);
        });
    }

    private static void RunInWindow(Action<MainWindow> assertion)
    {
        Exception? failure = null;
        var settingsPath = Path.Combine(Path.GetTempPath(), $"easypub-layout-settings-{Guid.NewGuid():N}.json");
        var recoveryPath = Path.Combine(Path.GetTempPath(), $"easypub-layout-recovery-{Guid.NewGuid():N}.json");
        var previousSettingsPath = Environment.GetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH");
        var previousRecoveryPath = Environment.GetEnvironmentVariable("EASYPUB_RECOVERY_PATH");
        var previousDisableSave = Environment.GetEnvironmentVariable("EASYPUB_DISABLE_SETTINGS_SAVE");
        try
        {
            Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH", settingsPath);
            Environment.SetEnvironmentVariable("EASYPUB_RECOVERY_PATH", recoveryPath);
            Environment.SetEnvironmentVariable("EASYPUB_DISABLE_SETTINGS_SAVE", "1");
            var thread = new Thread(() =>
            {
                MainWindow? window = null;
                App? captureApp = null;
                try
                {
                    var captureRequested = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_SETTINGS_CAPTURE_PATH"))
                        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_AUTOCHECK_CAPTURE"))
                        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_CHAPTER_CAPTURE_PATH"))
                        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_LIBRARY_CAPTURE_PATH"))
                        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_LIBRARY_SELECTION_CAPTURE_PATH"))
                        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_LIBRARY_INSPECTOR_CAPTURE_PATH"))
                        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_READINESS_CAPTURE_PATH"))
                        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_COVER_CAPTURE_PATH"))
                        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_CONVERSION_SETTINGS_CAPTURE_PATH"))
                        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_RULES_CAPTURE_PATH"));
                    if (captureRequested && Application.Current is null)
                    {
                        captureApp = new App();
                        captureApp.InitializeComponent();
                    }
                    window = new MainWindow { Width = 1440, Height = 900, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = captureRequested ? 1 : 0 };
                    window.Show();
                    assertion(window);
                }
                catch (Exception exception) { failure = exception; }
                finally
                {
                    window?.Close();
                    if (captureApp is not null && !captureApp.Dispatcher.HasShutdownStarted) captureApp.Shutdown();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(12)), "主界面布局测试超时。");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH", previousSettingsPath);
            Environment.SetEnvironmentVariable("EASYPUB_RECOVERY_PATH", previousRecoveryPath);
            Environment.SetEnvironmentVariable("EASYPUB_DISABLE_SETTINGS_SAVE", previousDisableSave);
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(recoveryPath)) File.Delete(recoveryPath);
        }
    }

    private static void PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            if (!condition() && DateTime.UtcNow - started < timeout) return;
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Assert.True(condition(), "异步界面状态未在限定时间内完成。");
    }

    private static void WriteTestCover(string path)
    {
        var pixels = new byte[]
        {
            0x30, 0x60, 0xE0, 0xFF, 0x30, 0x60, 0xE0, 0xFF,
            0x20, 0x40, 0xA0, 0xFF, 0x20, 0x40, 0xA0, 0xFF,
            0x10, 0x20, 0x60, 0xFF, 0x10, 0x20, 0x60, 0xFF,
        };
        var source = BitmapSource.Create(2, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }

    private static void CaptureWindowVisual(Window window, string path)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
