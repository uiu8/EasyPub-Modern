using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 打开真实工作台、断言它顶部那几行、并可选地渲染成 PNG 的公共装置。
///
/// <para>收在这里而不是每个测试类各抄一遍，是因为其中有三件事**必须做对**，抄错一件
/// 就会得到一个永远通过（或永远失败）的测试：</para>
///
/// <list type="bullet">
/// <item><c>Application.Current</c> 为 null 时它下面每个成员都会抛空引用 —— 单独跑一个窗口测试
/// 就会崩，所以不能依赖别的测试先建过它。</item>
/// <item>没有 <c>SynchronizationContext</c> 时，<c>await</c> 之后的续体跑在线程池上，
/// 接下来任何一次 <c>SetValue</c> 都会以"另一个线程拥有该对象"告终。</item>
/// <item>窗口里抛出的异常必须被抓住带回调用线程，否则它会消失在一条已经结束的线程里，
/// 测试看起来是通过的。</item>
/// </list>
/// </summary>
internal static class WorkbenchHarness
{
    /// <summary>设了它才会真的写 PNG；没设时只断言渲染本身是有效的。</summary>
    internal static string ScreenshotDirectory =>
        Environment.GetEnvironmentVariable("EASYPUB_SCREENSHOT_DIR") ?? "";

    internal static string SampleRoot()
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

    /// <summary>
    /// 把"本书保存过的目录"重定向到一个空目录，并返回原来的值。
    ///
    /// <para><b>为什么要动存储根，而不是复制一份样书：</b>SHA 是**内容**的哈希，所以
    /// <c>File.Copy</c> 出来的副本 SHA 与样书一模一样，照样命中别的测试写下的记录。
    /// 真正要隔离的是那个存储根，它由 <c>EASYPUB_REFERENCE_CATALOGS_PATH</c> 决定 ——
    /// <c>CatalogsRoot()</c> 每次都读环境变量，所以进程内改也生效。</para>
    ///
    /// <para>它要求测试**串行**跑（<c>-m:1 -- xUnit.MaxParallelThreads=1</c>），
    /// 否则环境变量是进程级的，会串到别的测试上。</para>
    /// </summary>
    internal static string IsolateCatalogStore()
    {
        var previous = Environment.GetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH");
        var isolated = Path.Combine(Path.GetTempPath(), $"easypub-catalogs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolated);
        Environment.SetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH", isolated);
        return previous ?? "";
    }

    internal static void RestoreCatalogStore(string previous) =>
        Environment.SetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH",
            string.IsNullOrEmpty(previous) ? null : previous);

    /// <summary>在一条 STA 线程上跑一段窗口代码，返回里面抛出的异常（没有则 null）。</summary>
    internal static Exception? OnStaThread(Action body)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            if (Application.Current is null)
            {
                var app = new App();
                app.InitializeComponent();
            }
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { body(); }
            catch (Exception exception) { captured = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }

    internal static void Save(Window window, string name)
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

    /// <summary>读状态条里某一行；名字写错时立刻失败，而不是拿到一个 null 去比较。</summary>
    internal static string Line(Window window, string name) =>
        (window.FindName(name) as TextBlock)?.Text
        ?? throw new InvalidOperationException($"状态条里找不到 {name}");

    /// <summary>把工作台窗口摆到屏幕外并完成一次布局 —— 渲染之前必须做，否则尺寸是 0。</summary>
    internal static ChapterEditorWindow ShowOffscreen(ChapterEditorWindow editor)
    {
        editor.WindowStartupLocation = WindowStartupLocation.Manual;
        editor.Left = -4000;
        editor.Top = -4000;
        editor.ShowInTaskbar = false;
        editor.Show();
        editor.UpdateLayout();
        editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        return editor;
    }
}
