using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 用《六卷缺陷样书》走一遍**按钮那条路**，并把修复确认窗拍下来。
///
/// 与其他测试的区别在于它不构造任何东西：它调用的是两个按钮共用的那个方法
/// （<c>RunRepairReviewAsync</c>，参数就是目录对话框里选定或粘贴的那份目录），
/// 所以它通过就等于「按钮能到达统一确认窗」通过 —— 而不是"另有一条测试专用的路能到达"。
///
/// 目录直接传给方法，因此不联网：等价于用户已经粘贴好目录再点按钮的情形。
/// 关窗即为「不应用」，所以这个测试不会改动任何书稿，也不会写备份。
///
/// <para>设 <c>EASYPUB_SCREENSHOT_DIR</c> 写出 PNG。</para>
/// </summary>
public class SampleBookRepairFlowTests
{
    private static string ScreenshotDirectory =>
        Environment.GetEnvironmentVariable("EASYPUB_SCREENSHOT_DIR") ?? "";

    private static string SampleRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "六卷缺陷样书");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 samples/六卷缺陷样书。");
    }

    private static void Save(Window window, string name)
    {
        window.UpdateLayout();
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        Assert.True(width > 0 && height > 0, $"{name} 没有布局尺寸（{width}×{height}）");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        Assert.True(stream.Length > 4096, $"{name} 渲染出的 PNG 只有 {stream.Length} 字节");
        if (string.IsNullOrWhiteSpace(ScreenshotDirectory)) return;
        Directory.CreateDirectory(ScreenshotDirectory);
        File.WriteAllBytes(Path.Combine(ScreenshotDirectory, name + ".png"), stream.ToArray());
    }

    [Fact]
    public void The_shared_entry_opens_the_repair_review_window_for_the_sample_book()
    {
        var root = SampleRoot();
        // 样书留一份副本：这个测试不会改它，但确认窗会读它做原文预览，路径必须在测试期间有效。
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var catalog = ReferenceCatalogInput.ParseText(
            File.ReadAllText(Path.Combine(root, "目录.txt"), Encoding.UTF8));
        Assert.NotNull(catalog);

        Exception? failure = null;
        string? capturedTitle = null;
        var thread = new Thread(() =>
        {
            // Application.Current 是 null 时它下面的每一个成员都会抛空引用 —— 整套测试里
            // 只要有别的测试先建过 Application 就看不出来，单独跑这个测试就必崩。
            // 所以这里自己保证它存在，而不是依赖运行顺序。
            if (Application.Current is null) _ = new Application();
            // 这一句是必需的，不是保险：没有 SynchronizationContext 时，await 之后的续体
            // 跑在线程池上，接下来任何一次 SetValue 都会以"另一个线程拥有该对象"告终。
            // 真实应用里 WPF 的消息循环会装上它，手工搭的 STA 线程不会 —— 少了它就等于
            // 在一个和产品不一样的线程模型下测产品。
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var document = ChapterTreeDocument.LoadAsync(bookPath).GetAwaiter().GetResult();
                var editor = new ChapterEditorWindow(document)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false,
                };
                editor.Show();
                editor.UpdateLayout();
                editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                var entry = typeof(ChapterEditorWindow).GetMethod("RunRepairReviewAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("统一修复入口 RunRepairReviewAsync 不见了");
                var task = (Task)entry.Invoke(editor, [catalog, null])!;

                var before = Application.Current.Windows.OfType<Window>().ToHashSet();
                var captured = false;
                var attempts = 0;
                var timer = new DispatcherTimer(DispatcherPriority.Background, editor.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(50),
                };
                timer.Tick += (_, _) =>
                {
                    if (++attempts > 400) { timer.Stop(); return; }
                    var dialog = Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => !before.Contains(w) && w.IsVisible);
                    if (dialog is null) return;
                    timer.Stop();
                    dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    capturedTitle = dialog.Title;
                    Save(dialog, "10-六卷样书-修复确认窗");
                    captured = true;
                    // 关窗 = 用户选择「不应用」，所以书稿与备份都不受影响。
                    dialog.Close();
                };
                timer.Start();

                var deadline = DateTime.UtcNow.AddSeconds(60);
                while (!captured && attempts <= 400 && DateTime.UtcNow < deadline)
                    editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                // 关窗后总入口还要跑完收尾，把它的异常暴露出来而不是吞掉。
                var settled = DateTime.UtcNow.AddSeconds(15);
                while (!task.IsCompleted && DateTime.UtcNow < settled)
                    editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
                if (task.IsFaulted) task.GetAwaiter().GetResult();

                Assert.True(captured, "统一修复入口没有打开修复确认窗");
                Assert.Equal("目录修复 · 核对后应用", capturedTitle);
                editor.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(120)), "样书流程线程没有在 120 秒内结束");
        // 带上完整异常链与堆栈：只 throw 原异常的话 xUnit 只显示最后一行，定位不到是谁在别的线程上碰了 UI。
        if (failure is not null)
            throw new InvalidOperationException("样书流程失败：" + failure, failure);
    }
}
