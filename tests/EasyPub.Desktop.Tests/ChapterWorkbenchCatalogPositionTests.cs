using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 对比重复正文的窗口必须说出**哪一份在原位置**。
///
/// <para>两段正文一模一样 —— 这正是"重复"的定义 —— 所以正文本身不能用来选。位置才是唯一能分辨
/// 它们的东西，而位置只有参考目录说得清。这一组检查的就是那句话有没有真的出现在窗口上。</para>
///
/// <para>目录按源哈希存在 <c>%LOCALAPPDATA%</c> 下，所以这里用 <c>EASYPUB_REFERENCE_CATALOGS_PATH</c>
/// 把它指到临时目录。那是进程级变量，所以这一组必须与其他用它的测试串行跑
/// （<c>-- xUnit.MaxParallelThreads=1</c>）。</para>
/// </summary>
public class ChapterWorkbenchCatalogPositionTests : IDisposable
{
    // 第一章在第 1 行和第 7 行各出现一次，正文相同；中间隔着第二章。相隔 6 行，不连续。
    private const string Source =
        "第一章 甲\n" +
        "正文一。\n" +
        "\n" +
        "第二章 乙\n" +
        "正文二。\n" +
        "\n" +
        "第一章 甲\n" +
        "正文一。\n";

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "easypub-catalog-position-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previous;

    public ChapterWorkbenchCatalogPositionTests()
    {
        Directory.CreateDirectory(_workspace);
        _previous = Environment.GetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH");
        Environment.SetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH", _workspace);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH", _previous);
        try { Directory.Delete(_workspace, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static async Task RunAsync(Func<ChapterEditorWindow, Task> action, string? catalogText)
    {
        var path = Path.Combine(Path.GetTempPath(), $"catalog-position-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, Source, new UTF8Encoding(false));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            if (catalogText is not null && ReferenceCatalogInput.ParseText(catalogText) is { } catalog)
                ReferenceCatalogInput.SaveCatalog(document.SourceSha256, catalog);

            Exception? failure = null;
            var thread = new Thread(() =>
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                ChapterEditorWindow? window = null;
                var frame = new DispatcherFrame();
                try
                {
                    window = new ChapterEditorWindow(document) { Opacity = 0, ShowInTaskbar = false };
                    window.ConfirmAction = _ => true;
                    _ = window.Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            window.Show();
                            window.UpdateLayout();
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            await action(window);
                        }
                        catch (Exception error) { failure = error; }
                        finally { frame.Continue = false; }
                    });
                    Dispatcher.PushFrame(frame);
                }
                catch (Exception error) { failure = error; }
                finally { try { window?.DiscardChangesAndClose(); } catch { } }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "工作台线程没有在 60 秒内结束");
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>两处同名章节，按出现顺序。</summary>
    private static ChapterTreeNode[] Copies(ChapterEditorWindow window) =>
        window.Roots.Where(node => node.Title == "第一章 甲").ToArray();

    [Fact]
    public async Task With_a_directory_the_window_names_the_copy_that_holds_the_original_position()
    {
        await RunAsync(window =>
        {
            var nodes = Copies(window);
            Assert.Equal(2, nodes.Length);
            Assert.Equal(1, nodes[0].TitleLineNumber);
            Assert.Equal(7, nodes[1].TitleLineNumber);

            var (line, text) = window.DescribeCatalogPosition(nodes);

            // 目录里这一章排在第二章之前，所以它在原位置是第 1 行那一份。
            Assert.Equal(1, line);
            Assert.Contains("参考目录把这一章对在第 1 行", text);
            Assert.Contains("前一份", text);
            Assert.Contains("相隔 6 行", text);
            return Task.CompletedTask;
        }, "第一章 甲\n第二章 乙\n");
    }

    [Fact]
    public async Task Without_a_directory_the_window_says_so_instead_of_guessing()
    {
        await RunAsync(window =>
        {
            var (line, text) = window.DescribeCatalogPosition(Copies(window));

            Assert.Null(line);
            Assert.Contains("没有参考目录", text);
            Assert.Contains("位置是唯一能分辨", text);
            return Task.CompletedTask;
        }, catalogText: null);
    }

    [Fact]
    public async Task A_chapter_the_directory_does_not_list_is_not_claimed_to_have_a_position()
    {
        await RunAsync(window =>
        {
            var (line, text) = window.DescribeCatalogPosition(Copies(window));

            Assert.Null(line);
            Assert.Contains("没有这一章", text);
            return Task.CompletedTask;
        }, "第二章 乙\n第三章 丙\n");
    }
}
