using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 工作台顶部那**三行事实** —— 文档 §3.2。
///
/// <para>它们替换掉的是原来那个单一的「修复依据」状态条。那个模型把所有情况压成一个开关，
/// 于是总有一半是假的；这里钉住的是三行**各说各的、不能互相推导**：</para>
///
/// <list type="bullet">
/// <item>参考目录 —— 磁盘上 / 本次有没有一份可用的目录；</item>
/// <item>当前章节树 —— 它是怎么来的（这一行**不能**从上一行推出来）；</item>
/// <item>原文版本 —— 文件是否在程序之外被改过。</item>
/// </list>
///
/// <para>设 <c>EASYPUB_SCREENSHOT_DIR</c> 会把这个窗口写成 PNG，供人工核对外观。</para>
/// </summary>
public class WorkbenchContextBarTests
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
    private static string IsolateCatalogStore()
    {
        var previous = Environment.GetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH");
        var isolated = Path.Combine(Path.GetTempPath(), $"easypub-catalogs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolated);
        Environment.SetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH", isolated);
        return previous ?? "";
    }

    private static void RestoreCatalogStore(string previous) =>
        Environment.SetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH",
            string.IsNullOrEmpty(previous) ? null : previous);

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

    private static string Line(Window window, string name) =>
        (window.FindName(name) as TextBlock)?.Text
        ?? throw new InvalidOperationException($"状态条里找不到 {name}");

    [Fact]
    public void The_workbench_states_three_facts_as_three_separate_lines()
    {
        var book = Path.Combine(SampleRoot(), "缺陷样书.txt");
        var previous = IsolateCatalogStore();
        Exception? failure = null;
        string? catalogLine = null;
        string? treeLine = null;
        string? sourceLine = null;

        var thread = new Thread(() =>
        {
            // Application.Current 为 null 时它下面每个成员都会抛空引用 —— 单独跑这个测试就会崩，
            // 所以这里自己保证它存在，而不是依赖别的测试先建过它。
            if (Application.Current is null) _ = new Application();
            // 没有 SynchronizationContext 时，await 之后的续体跑在线程池上，
            // 接下来任何一次 SetValue 都会以"另一个线程拥有该对象"告终。
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var document = ChapterTreeDocument.LoadAsync(book).GetAwaiter().GetResult();
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

                catalogLine = Line(editor, "ContextCatalogText");
                treeLine = Line(editor, "ContextTreeText");
                sourceLine = Line(editor, "ContextSourceText");

                Save(editor, "20-工作台-三行事实");
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        RestoreCatalogStore(previous);

        Assert.Null(failure);

        // 干净状态下的样书：没有保存过目录、没有 provenance（旧项目语义）、原文没被外部改过。
        // **三行各说各的** —— 这正是单一开关做不到的。
        Assert.Equal("未加载", catalogLine);
        Assert.Contains("来源未知", treeLine);
        Assert.Equal("正常", sourceLine);
    }

    [Fact]
    public void A_verified_tree_is_named_as_such_even_when_no_catalog_is_loaded()
    {
        // 「清除目录记录」不等于「树回到本地识别」：树绑的是那次修复，不是那条记录。
        // 所以第一行可以是"未加载"而第二行是"目录校验后" —— 三行必须能同时成立。
        var book = Path.Combine(SampleRoot(), "缺陷样书.txt");
        var previous = IsolateCatalogStore();
        Exception? failure = null;
        string? catalogLine = null;
        string? treeLine = null;

        var thread = new Thread(() =>
        {
            if (Application.Current is null) _ = new Application();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try
            {
                var document = ChapterTreeDocument.LoadAsync(book).GetAwaiter().GetResult();
                var plan = document.CreatePlan(document.Entries) with
                {
                    Provenance = PersistedTreeProvenance.FromCatalog("样书目录"),
                };
                var editor = new ChapterEditorWindow(
                    document, document.RecognitionOptions, null, TextEncodingMode.Auto, savedPlan: plan)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false,
                };
                editor.Show();
                editor.UpdateLayout();
                editor.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                catalogLine = Line(editor, "ContextCatalogText");
                treeLine = Line(editor, "ContextTreeText");

                Save(editor, "21-工作台-目录树但无目录记录");
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        RestoreCatalogStore(previous);

        Assert.Null(failure);
        Assert.Equal("未加载", catalogLine);
        Assert.Contains("目录校验后", treeLine);
    }
}
