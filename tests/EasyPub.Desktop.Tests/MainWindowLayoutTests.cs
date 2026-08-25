using System.IO;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class MainWindowLayoutTests
{
    [Fact]
    public void Advanced_options_do_not_create_horizontal_overflow_or_hide_the_left_edge()
    {
        Exception? failure = null;
        var settingsPath = Path.Combine(Path.GetTempPath(), $"easypub-layout-settings-{Guid.NewGuid():N}.json");
        var recoveryPath = Path.Combine(Path.GetTempPath(), $"easypub-layout-recovery-{Guid.NewGuid():N}.json");
        var inputPath = Path.Combine(Path.GetTempPath(), $"easypub-layout-book-{Guid.NewGuid():N}.txt");
        var coverPath = Path.Combine(Path.GetTempPath(), $"easypub-layout-cover-{Guid.NewGuid():N}.png");
        var previousSettingsPath = Environment.GetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH");
        var previousRecoveryPath = Environment.GetEnvironmentVariable("EASYPUB_RECOVERY_PATH");
        var previousDisableSave = Environment.GetEnvironmentVariable("EASYPUB_DISABLE_SETTINGS_SAVE");

        try
        {
            Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH", settingsPath);
            Environment.SetEnvironmentVariable("EASYPUB_RECOVERY_PATH", recoveryPath);
            Environment.SetEnvironmentVariable("EASYPUB_DISABLE_SETTINGS_SAVE", "1");
            File.WriteAllText(inputPath, "第一章 雨夜\n正文");
            var thread = new Thread(() =>
            {
                MainWindow? window = null;
                try
                {
                    window = new MainWindow
                    {
                        Width = 1750,
                        Height = 860,
                        ShowInTaskbar = false,
                        WindowStyle = WindowStyle.None,
                        Opacity = 0,
                    };
                    window.Show();

                    var tabs = Assert.IsType<TabControl>(window.FindName("OptionsTabs"));
                    tabs.SelectedIndex = 7;
                    var status = Assert.IsType<TextBlock>(window.FindName("LegacyConfigStatusText"));
                    status.Text = @"已加载 C:\Users\13168\Documents\Codex\2026-08-22\easypub\outputs\EasyPubModern-v0.19.3-win-x64\config.xml · 应用 10 组，待实现 10 组";
                    var retention = Assert.IsType<ComboBox>(window.FindName("ValidationRetentionCombo"));
                    var scrollViewer = Assert.IsType<ScrollViewer>(window.FindName("MainContentScrollViewer"));
                    var advancedContent = Assert.IsType<Border>(window.FindName("AdvancedOptionsContent"));
                    var validationPanel = Assert.IsType<Grid>(window.FindName("ValidationOptionsPanel"));
                    var taskCenterButton = Assert.IsType<Button>(window.FindName("TaskCenterButton"));
                    var autoOpenTaskCenter = Assert.IsType<CheckBox>(window.FindName("AutoOpenTaskCenterCheck"));
                    Assert.False(autoOpenTaskCenter.IsChecked);
                    foreach (var width in new[] { 840d, 1180d, 1750d })
                    {
                        window.Width = width;
                        retention.Focus();
                        retention.BringIntoView();
                        window.UpdateLayout();

                        Assert.True(
                            scrollViewer.ExtentWidth <= scrollViewer.ViewportWidth + 1,
                            $"窗口宽度 {width:F0} 时发生横向溢出：ExtentWidth={scrollViewer.ExtentWidth:F1}, ViewportWidth={scrollViewer.ViewportWidth:F1}");
                        Assert.InRange(scrollViewer.HorizontalOffset, 0, 0.5);
                        Assert.True(
                            advancedContent.DesiredSize.Height <= advancedContent.ActualHeight + 1,
                            $"窗口宽度 {width:F0} 时高级选项被纵向裁切：DesiredHeight={advancedContent.DesiredSize.Height:F1}, ActualHeight={advancedContent.ActualHeight:F1}");
                        var advancedBounds = advancedContent.TransformToAncestor(window).TransformBounds(
                            new Rect(0, 0, advancedContent.ActualWidth, advancedContent.ActualHeight));
                        var validationBounds = validationPanel.TransformToAncestor(window).TransformBounds(
                            new Rect(0, 0, validationPanel.ActualWidth, validationPanel.ActualHeight));
                        Assert.True(
                            validationBounds.Bottom <= advancedBounds.Bottom + 1,
                            $"窗口宽度 {width:F0} 时结构验收行超出高级页：ValidationBottom={validationBounds.Bottom:F1}, ContentBottom={advancedBounds.Bottom:F1}");
                        Assert.Equal(width < 980 ? "任务" : "任务中心", taskCenterButton.Content);
                    }

                    var modernMode = Assert.IsType<RadioButton>(window.FindName("ModernModeRadio"));
                    var customMode = Assert.IsType<RadioButton>(window.FindName("CustomModeRadio"));
                    var fontSize = Assert.IsType<TextBox>(window.FindName("FontSizeText"));
                    var lineHeight = Assert.IsType<TextBox>(window.FindName("LineHeightText"));
                    var layoutTab = Assert.IsType<TabItem>(window.FindName("LayoutTab"));
                    modernMode.IsChecked = true;
                    Assert.Equal("105", fontSize.Text);
                    Assert.Equal("165", lineHeight.Text);

                    typeof(MainWindow).GetField("_optionTrackingReady", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
                    tabs.SelectedItem = layoutTab;
                    window.UpdateLayout();
                    fontSize.Text = "106";
                    Assert.True(customMode.IsChecked);
                    Assert.Contains("●", layoutTab.Header?.ToString());

                    var filesList = Assert.IsType<ListBox>(window.FindName("FilesList"));
                    Assert.IsType<TextBox>(window.FindName("BookSearchText"));
                    Assert.IsType<ComboBox>(window.FindName("BookFilterCombo"));
                    Assert.IsType<ComboBox>(window.FindName("BookSortCombo"));
                    Assert.IsType<Button>(window.FindName("QuickChapterButton"));
                    Assert.IsType<Button>(window.FindName("QuickCleanupButton"));
                    Assert.IsType<Button>(window.FindName("QuickMetadataButton"));
                    Assert.IsType<Button>(window.FindName("QuickIllustrationButton"));
                    Assert.IsType<Button>(window.FindName("QuickPreviewButton"));
                    var openCoverPreview = Assert.IsType<Button>(window.FindName("OpenCoverPreviewButton"));
                    var coverPreviewBorder = Assert.IsType<Border>(window.FindName("CoverPreviewBorder"));
                    Assert.Equal(ScrollBarVisibility.Disabled, ScrollViewer.GetHorizontalScrollBarVisibility(filesList));
                    var selectedSummary = Assert.IsType<TextBlock>(window.FindName("SelectedBookSummaryText"));
                    WriteTestCover(coverPath);
                    var book = new InputBookItem(inputPath);
                    window.InputBooks.Add(book);
                    filesList.SelectedItem = book;
                    window.UpdateLayout();
                    Assert.Contains("封面：无", selectedSummary.Text);
                    Assert.True(openCoverPreview.IsEnabled);
                    Assert.Equal(Visibility.Visible, coverPreviewBorder.Visibility);
                    Assert.Equal(112, coverPreviewBorder.ActualHeight, 0.5);
                    book.CoverImagePath = coverPath;
                    PumpDispatcherUntil(() => book.CoverThumbnail is not null, TimeSpan.FromSeconds(3));
                    Assert.NotNull(book.CoverThumbnail);
                    Assert.Equal(Visibility.Visible, book.CoverThumbnailVisibility);
                    Assert.Equal(Visibility.Collapsed, book.CoverThumbnailPlaceholderVisibility);
                    window.Width = 840;
                    window.UpdateLayout();
                    Assert.Equal(Visibility.Visible, coverPreviewBorder.Visibility);
                    var item = Assert.IsType<ListBoxItem>(filesList.ItemContainerGenerator.ContainerFromItem(book));
                    Assert.Contains(Path.GetFileNameWithoutExtension(inputPath), System.Windows.Automation.AutomationProperties.GetName(item));
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    window?.Close();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "主界面布局测试超时。");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH", previousSettingsPath);
            Environment.SetEnvironmentVariable("EASYPUB_RECOVERY_PATH", previousRecoveryPath);
            Environment.SetEnvironmentVariable("EASYPUB_DISABLE_SETTINGS_SAVE", previousDisableSave);
            if (File.Exists(settingsPath)) File.Delete(settingsPath);
            if (File.Exists(recoveryPath)) File.Delete(recoveryPath);
            if (File.Exists(inputPath)) File.Delete(inputPath);
            if (File.Exists(coverPath)) File.Delete(coverPath);
        }
    }

    private static void PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(15),
        };
        timer.Tick += (_, _) =>
        {
            if (!condition() && DateTime.UtcNow - started < timeout) return;
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Assert.True(condition(), "封面缩略图未在限定时间内加载完成。");
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
}
