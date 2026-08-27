using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
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
    public void Ink_workspace_uses_six_independent_effect_matching_pages()
    {
        RunInWindow(window =>
        {
            Assert.IsType<TextBox>(window.FindName("BookSearchText"));
            Assert.IsType<Button>(window.FindName("SettingsButton"));
            Assert.Null(window.FindName("HeaderSubtitleText"));
            var shortcutManagerButton = Assert.IsType<Button>(window.FindName("ShortcutManagerButton"));
            Assert.IsType<Menu>(window.FindName("AddBooksMenu"));
            Assert.IsType<MenuItem>(window.FindName("FavoriteFoldersMenu"));
            Assert.IsType<Border>(window.FindName("SidebarPanel"));
            Assert.IsType<Border>(window.FindName("BottomOperationBar"));
            Assert.DoesNotContain(FindVisualDescendants<Button>(window), button => Equals(button.Content, "☷") || Equals(button.Content, "▦"));
            Assert.IsType<Button>(window.FindName("InkManageIllustrationsButton"));
            var bottomFormat = Assert.IsType<ComboBox>(window.FindName("FormatCombo"));
            var bottomPreset = Assert.IsType<ComboBox>(window.FindName("LayoutModeCombo"));
            Assert.Equal(Visibility.Visible, bottomFormat.Visibility);
            Assert.Equal(Visibility.Visible, bottomPreset.Visibility);
            Assert.Equal("原版兼容", ((ComboBoxItem)bottomPreset.SelectedItem).Content);

            var captureTheme = Environment.GetEnvironmentVariable("EASYPUB_SETTINGS_CAPTURE_THEME") ?? "Light";
            var settingsWindow = new SettingsWindow(
                captureTheme, "Comfortable", 100, true, false,
                Path.GetTempPath(), string.Empty, 1, false, 10, false, false,
                new Dictionary<string, string>(), 0, () => { });
            settingsWindow.Show();
            settingsWindow.UpdateLayout();
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
                ("ChaptersNavigationButton", "章节正文", "InkChaptersPage"),
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
            var captured = Assert.IsType<ConversionProfile>(captureProfile.Invoke(window, null));
            Assert.True(captured.Options.AddFullWidthIndent);
            Assert.Equal(3, captured.Options.FullWidthIndentCount);

            var convertFormat = Assert.IsType<ComboBox>(window.FindName("ConvertFormatCombo"));
            bottomFormat.SelectedIndex = 0;
            Assert.Equal(0, convertFormat.SelectedIndex);
            convertFormat.SelectedIndex = 1;
            Assert.Equal(1, bottomFormat.SelectedIndex);

            var scrollViewer = Assert.IsType<ScrollViewer>(window.FindName("MainContentScrollViewer"));
            foreach (var width in new[] { 1120d, 1440d, 1750d })
            {
                window.Width = width;
                window.UpdateLayout();
                Assert.True(scrollViewer.ExtentWidth <= scrollViewer.ViewportWidth + 1,
                    $"窗口宽度 {width:F0} 时发生横向溢出：ExtentWidth={scrollViewer.ExtentWidth:F1}, ViewportWidth={scrollViewer.ViewportWidth:F1}");
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

                Assert.Contains("封面：无", selectedSummary.Text);
                Assert.True(openCoverPreview.IsEnabled);
                Assert.Equal(Visibility.Visible, coverPreview.Visibility);
                var coverPanel = Assert.IsType<Border>(window.FindName("CoverDropPanel"));
                var quickMetadata = Assert.IsType<Button>(window.FindName("QuickMetadataButton"));
                var quickMetadataBottom = quickMetadata.TranslatePoint(new Point(0, quickMetadata.ActualHeight), coverPanel).Y;
                Assert.True(quickMetadataBottom < Math.Min(560, coverPanel.ActualHeight),
                    $"右侧工具距离所选书稿信息过远：按钮底部位于 {quickMetadataBottom:F1}px。 ");

                book.CoverImagePath = coverPath;
                PumpDispatcherUntil(() => book.CoverThumbnail is not null, TimeSpan.FromSeconds(3));
                Assert.NotNull(book.CoverThumbnail);
                Assert.Equal(Visibility.Visible, book.CoverThumbnailVisibility);
                Assert.Equal(Visibility.Collapsed, book.CoverThumbnailPlaceholderVisibility);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(coverPath)) File.Delete(coverPath);
        }
    }

    [Fact]
    public void Import_controls_metadata_mapping_and_kindle_models_are_complete()
    {
        RunInWindow(window =>
        {
            var addBooksMenu = Assert.IsType<Menu>(window.FindName("AddBooksMenu"));
            var addBooksRoot = Assert.IsType<MenuItem>(Assert.Single(addBooksMenu.Items));
            var fileCommand = Assert.IsType<MenuItem>(addBooksRoot.Items[0]);
            Assert.NotEqual("Segoe Fluent Icons", fileCommand.FontFamily.Source);

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
    public void Adding_txt_builds_a_default_chapter_tree()
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
                PumpDispatcherUntil(() => book.ChapterTree is not null, TimeSpan.FromSeconds(5));
                Assert.NotNull(book.ChapterTree);
                Assert.True(book.ChapterTree!.Entries.Count >= 3,
                    $"自动章节树只识别到 {book.ChapterTree.Entries.Count} 项。");
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
        try
        {
            File.WriteAllText(inputPath, "第一章 测试\r\n正文");
            RunInWindow(window =>
            {
                var book = new InputBookItem(inputPath);
                book.SetMetadataOverrides(
                    new BookMetadataOverrides { Publisher = "起点", Language = "zh-CN" },
                    Path.GetDirectoryName(inputPath));
                window.InputBooks.Add(book);

                var filesList = Assert.IsType<ListBox>(window.FindName("FilesList"));
                filesList.SelectedItem = book;
                var navigation = Assert.IsType<RadioButton>(window.FindName("CoverNavigationButton"));
                navigation.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, navigation));
                window.UpdateLayout();

                Assert.Equal("起点", Assert.IsType<TextBox>(window.FindName("PublisherText")).Text);
            });
        }
        finally
        {
            if (File.Exists(inputPath)) File.Delete(inputPath);
        }
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
                    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EASYPUB_SETTINGS_CAPTURE_PATH")) && Application.Current is null)
                    {
                        captureApp = new App();
                        captureApp.InitializeComponent();
                    }
                    window = new MainWindow { Width = 1440, Height = 900, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Opacity = 0 };
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
