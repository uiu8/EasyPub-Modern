using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
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
                    }
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
        }
    }
}
