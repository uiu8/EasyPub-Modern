using System.IO;
using System.Runtime.ExceptionServices;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class TextCleanupPreviewNavigatorTests
{
    [Fact]
    public void Navigator_locates_a_change_beyond_the_initial_preview_limit()
    {
        var source = string.Join('\n', Enumerable.Range(1, 8_000).Select(index =>
            index == 7_900 ? "001 雨夜" : $"第 {index:D4} 行正文内容正文内容正文内容"));
        var preview = TextCleanupPipeline.Apply(source, new TextCleanupOptions { NormalizeChapterNumbers = true });
        var change = Assert.Single(preview.Changes);

        var view = TextCleanupPreviewNavigator.Create(preview, change, maximumCharacters: 2_000);

        Assert.True(view.IsWindowed);
        Assert.Contains("第一章 雨夜", view.Text);
        Assert.Equal("第一章 雨夜", view.Text.Substring(view.SelectionStart, view.SelectionLength));
        Assert.Contains("原文第 7900 行", view.LocationText);
    }

    [Fact]
    public void Navigator_locates_a_removed_line_at_the_nearest_retained_text()
    {
        var preview = TextCleanupPipeline.Apply(
            "第一行\n本书来自某某下载站\n第三行",
            new TextCleanupOptions { RemoveSiteNotices = true });
        var change = Assert.Single(preview.Changes);

        var view = TextCleanupPreviewNavigator.Create(preview, change);

        Assert.Contains("已删除", view.LocationText);
        Assert.Equal("第三行", view.Text.Substring(view.SelectionStart, view.SelectionLength));
    }

    [Fact]
    public void Cleanup_window_clicking_a_change_selects_the_processed_text()
    {
        Exception? failure = null;
        var path = Path.Combine(Path.GetTempPath(), $"easypub-cleanup-navigation-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "001 雨夜\n正文内容。");
        try
        {
            var thread = new Thread(() =>
            {
                TextCleanupWindow? window = null;
                try
                {
                    var constructor = typeof(TextCleanupWindow).GetConstructor(
                        BindingFlags.Instance | BindingFlags.NonPublic,
                        binder: null,
                        [typeof(string), typeof(string), typeof(TextCleanupOptions)],
                        modifiers: null)!;
                    window = (TextCleanupWindow)constructor.Invoke([
                        path,
                        File.ReadAllText(path),
                        new TextCleanupOptions { NormalizeChapterNumbers = true },
                    ]);
                    window.ShowInTaskbar = false;
                    window.WindowStyle = WindowStyle.None;
                    window.Opacity = 0;
                    window.Show();
                    window.UpdateLayout();

                    var grid = Assert.IsType<DataGrid>(window.FindName("ChangesGrid"));
                    PumpDispatcherUntil(() => grid.Items.Count > 0, TimeSpan.FromSeconds(5));
                    grid.SelectedIndex = 0;
                    window.UpdateLayout();

                    var preview = Assert.IsType<TextBox>(window.FindName("PreviewText"));
                    var location = Assert.IsType<TextBlock>(window.FindName("PreviewLocationText"));
                    Assert.Equal("第一章 雨夜", preview.SelectedText);
                    Assert.Contains("原文第 1 行", location.Text);
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
            Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "文本清理定位界面测试超时。");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void PumpDispatcherUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (condition()) return;
        var frame = new System.Windows.Threading.DispatcherFrame();
        var started = DateTime.UtcNow;
        var timer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
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
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Assert.True(condition(), "文本清理预览未在限定时间内完成。");
    }

}
