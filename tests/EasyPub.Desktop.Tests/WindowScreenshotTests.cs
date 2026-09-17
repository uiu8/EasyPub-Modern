using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// Renders the confirmation window and the pieces of the workbench a reader actually looks at, into
/// PNG files. Every other test in this project asserts on objects; this one produces a picture, because
/// the failure that matters most here — "the panel says two problems while forty chapters are missing" —
/// is a claim about what a person sees, and nothing but a picture can be checked against it.
///
/// <para>
/// Set <c>EASYPUB_SCREENSHOT_DIR</c> to write the files somewhere; without it the test still renders
/// (so it fails when rendering breaks) and skips writing.
/// </para>
/// </summary>
public class WindowScreenshotTests
{
    private static async Task<(AutoRepairOutcome Outcome, IReadOnlyList<ChapterTreeEntry> Before)> OutcomeAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-shot-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path,
            "第一章 起点\n正文一\n第二章 继续\n正文二\n" +
            "他忽然想起那年的雨\n这是正文段落，只是看起来像标题而已\n" +
            "番外 主角的人物卡\n卡片正文\n" +
            "第四章 断线\n正文四\n");
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var outcome = ChapterAutoRepair.Prepare(document,
                ReferenceCatalogInput.ParseText("第一章 起点\n第二章 继续\n第三章 收网\n第四章 断线\n请假一天"));
            return (outcome, document.Entries);
        }
        finally { File.Delete(path); }
    }

    private static void Save(FrameworkElement element, string name)
    {
        var width = (int)Math.Ceiling(element.ActualWidth);
        var height = (int)Math.Ceiling(element.ActualHeight);
        Assert.True(width > 0 && height > 0, $"{name} 没有布局尺寸（{width}×{height}）");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        Assert.True(stream.Length > 1024, $"{name} 渲染出的 PNG 只有 {stream.Length} 字节");
        var directory = Environment.GetEnvironmentVariable("EASYPUB_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, name + ".png"), stream.ToArray());
    }

    [Fact]
    public async Task The_confirmation_window_renders_and_is_captured()
    {
        var subject = await OutcomeAsync();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new RepairReviewWindow(subject.Outcome, subject.Before, _ => { })
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -4000,
                    Top = -4000,
                    ShowInTaskbar = false,
                };
                window.Show();
                window.UpdateLayout();
                // Let the layout settle before rendering; a window that has only just been shown can
                // report its size before its panes have measured.
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                window.UpdateLayout();
                Save(window, "01-修复确认窗");
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)), "渲染线程没有在 45 秒内结束");
        if (failure is not null) throw failure;
    }
}
