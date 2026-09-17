using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 「原文已变化 · 刷新」在原文被外部改过时到底做了什么。
///
/// <para>它以前是重新识别：在记事本里改一个错字再刷新，全部手工编排就没了，而界面上没有任何一处说
/// 过这件事会发生。现在它是一次版本迁移 —— 保住标题、层级和目录勾选，只把行号搬到新版本上，并列出
/// 真的没能跟过来的那几章。</para>
///
/// <para>测试按的是**按钮那一格**（把 <c>RefreshSourceButton</c> 当 sender 交进去），不是迁移方法
/// 本身：两者走同一个入口，而这一条正是要证明的事。</para>
/// </summary>
public class ChapterWorkbenchMigrationTests
{
    private const string Source =
        "第一章 起点\n正文一。\n第二章 中途\n正文二。\n第三章 转折\n正文三。\n";

    /// <summary>
    /// Runs the workbench on its own STA thread and pumps its dispatcher while <paramref name="action"/>
    /// runs.
    ///
    /// <para>Pumping rather than blocking on the task: the window's <c>await</c> continuations are posted
    /// back to this very thread, so <c>GetAwaiter().GetResult()</c> here does not wait for them — it stops
    /// the thread they need, and the access check then fails from the wrong one.</para>
    /// </summary>
    private static async Task RunAsync(Func<ChapterEditorWindow, string, Task> action)
    {
        var path = Path.Combine(Path.GetTempPath(), $"workbench-migration-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, Source, new UTF8Encoding(false));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                // Required, not defensive: with no SynchronizationContext the continuations of every
                // <c>await</c> inside the window run on the thread pool, so the next property set throws
                // "another thread owns this object" — and an exception thrown inside an <c>async void</c>
                // handler there does not fail a test, it kills the test host process. A real application
                // installs this through its message loop; a hand-built STA thread does not.
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                ChapterEditorWindow? window = null;
                var frame = new DispatcherFrame();
                try
                {
                    // The construction is inside the try as well: a window that fails to build is a failure
                    // this test has to report, not one that silently leaves the assertions below with nothing
                    // to look at.
                    window = new ChapterEditorWindow(document) { Opacity = 0, ShowInTaskbar = false };
                    window.ConfirmAction = _ => true;
                    _ = window.Dispatcher.InvokeAsync(async () =>
                    {
                        try
                        {
                            window.Show();
                            window.UpdateLayout();
                            // Showing the window runs a version check of its own; wait for it to finish, or
                            // the explicit one below is skipped by its own guard and nothing notices the edit.
                            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                            await action(window, path);
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

    private static Button RefreshButton(ChapterEditorWindow window) =>
        (Button)window.FindName("RefreshSourceButton");

    [Fact]
    public async Task An_outside_edit_migrates_the_tree_instead_of_re_recognising_it()
    {
        await RunAsync(async (window, path) =>
        {
            var titles = window.Roots.Select(node => node.Title).ToArray();
            Assert.NotEmpty(titles);

            // 有人在记事本里改了一句正文。
            await File.WriteAllTextAsync(path, Source.Replace("正文一。", "正文一，改过一句。"),
                new UTF8Encoding(false));
            await window.CheckSourceVersionAsync();
            Assert.Equal("原文已变化 · 刷新", RefreshButton(window).Content);

            await window.RebuildOrMigrateAsync(RefreshButton(window));

            var migration = window.LastMigration;
            Assert.NotNull(migration);
            Assert.True(migration!.Succeeded, migration.Message);
            Assert.False(migration.FellBackToRecognition);
            // 每一章都在 —— 改的是正文，没有一章的标题行被碰过。
            Assert.All(migration.Rebinds, rebind => Assert.True(rebind.Survived, rebind.Title));
            // 界面上的树还是那三章，而且按钮回到了"刷新原文"：窗口已经在新版本上。
            Assert.Equal(titles, window.Roots.Select(node => node.Title).ToArray());
            Assert.Equal("刷新原文", RefreshButton(window).Content);
        });
    }

    [Fact]
    public async Task An_outside_edit_that_removes_a_chapter_reports_which_one_went()
    {
        await RunAsync(async (window, path) =>
        {
            var titles = window.Roots.Select(node => node.Title).ToArray();
            await File.WriteAllTextAsync(path, Source.Replace("第二章 中途\n正文二。\n", ""),
                new UTF8Encoding(false));
            await window.CheckSourceVersionAsync();

            await window.RebuildOrMigrateAsync(RefreshButton(window));

            var migration = window.LastMigration;
            Assert.NotNull(migration);
            Assert.True(migration!.Succeeded, migration.Message);
            // 报告的是**哪一章**没了 —— "原文已变化"说不出这件事。
            Assert.Contains(migration.Lost, rebind => rebind.Title == "第二章 中途");
            Assert.DoesNotContain("第二章 中途", window.Roots.Select(node => node.Title));
            Assert.Equal(titles.Length - 1, window.Roots.Count);
        });
    }

    [Fact]
    public async Task The_re_recognise_button_still_re_recognises()
    {
        await RunAsync(async (window, path) =>
        {
            var titles = window.Roots.Select(node => node.Title).ToArray();
            // 原文变了，但用户按的是「重新识别」：那是明确要求丢掉手调树的那条路，不该被改成迁移。
            await File.WriteAllTextAsync(path, Source.Replace("正文一。", "正文一，改过一句。"),
                new UTF8Encoding(false));
            await window.CheckSourceVersionAsync();

            await window.RebuildOrMigrateAsync(window.FindName("RebuildRulesButton"));

            Assert.Null(window.LastMigration);
            Assert.Equal(titles.Length, window.Roots.Count);
            Assert.Equal("刷新原文", RefreshButton(window).Content);
        });
    }
}
