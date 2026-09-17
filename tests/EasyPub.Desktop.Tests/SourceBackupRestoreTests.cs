using System.IO;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 备份窗口上那两个新入口：恢复一个版本，和收尾一次没做完的原文修改。
///
/// <para>两条都在检查同一件事 —— **按钮调动的就是 Core 里那一层**，而不是窗口自己另写一条写文件的
/// 路。测试因此只经由窗口自己的入口，从不自己构造一个 <see cref="RestoreTransaction"/>：那样测的是
/// 那一层，而那一层已经有它自己的测试了。</para>
///
/// <para><c>EASYPUB_APP_SETTINGS_PATH</c> 是进程级环境变量，所以这一组必须和其他改它的测试串行跑
/// （<c>-- xUnit.MaxParallelThreads=1</c>）。</para>
/// </summary>
public class SourceBackupRestoreTests : IDisposable
{
    private const string OriginalText = "第一章 起点\n正文一。\n第二章 中途\n正文二。\n";
    private const string RepairedText = "第一章 起点\n正文一改。\n第二章 中途\n正文二。\n";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "easypub-restore-ui-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousSettings;
    private readonly string? _previousBackups;

    public SourceBackupRestoreTests()
    {
        Directory.CreateDirectory(_workspace);
        _previousSettings = Environment.GetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH");
        _previousBackups = Environment.GetEnvironmentVariable("EASYPUB_SOURCE_BACKUPS_PATH");
        Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH",
            Path.Combine(_workspace, "app-settings.json"));
        // 启动时读过一次的那个变量要显式清掉，让本次测试只依赖下面写出的设置文件。
        Environment.SetEnvironmentVariable("EASYPUB_SOURCE_BACKUPS_PATH", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH", _previousSettings);
        Environment.SetEnvironmentVariable("EASYPUB_SOURCE_BACKUPS_PATH", _previousBackups);
        try { Directory.Delete(_workspace, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private async Task<string> PrepareAsync()
    {
        var settings = AppSettingsStore.CreateDefault();
        await settings.SaveAsync(EasyPubAppSettings.Default with
        {
            SourceBackupRoot = Path.Combine(_workspace, "backups"),
            SourceBackupRetentionLimit = 5,
        });
        var book = Path.Combine(_workspace, "书稿.txt");
        await File.WriteAllTextAsync(book, OriginalText, Utf8);
        return book;
    }

    /// <summary>
    /// Builds the backup window on its own STA thread, runs <paramref name="work"/>, then closes it.
    ///
    /// <para>The dispatcher is pumped rather than blocked on: the window's <c>await</c> continuations are
    /// posted back to this very thread, so waiting on the task from inside it would stop the thread they
    /// need and the work would never finish.</para>
    ///
    /// <para>No <see cref="System.Windows.Application"/> is created. It is a process-wide singleton owned by
    /// whichever thread made it, and this suite has been dragged down by that before — whole runs collapsing
    /// from one failure to ninety. These windows resolve their theme only when one already exists, so not
    /// making one is both safe and sufficient.</para>
    /// </summary>
    private static T OnBackupWindow<T>(string book, Func<SourceBackupWindow, Task<T>> work)
    {
        T result = default!;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // Required, not defensive: with no SynchronizationContext the continuations of every
            // <c>await</c> inside the window run on the thread pool, so the next property set throws
            // "another thread owns this object" — and an exception thrown inside an <c>async void</c>
            // handler there does not fail a test, it kills the test host process.
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            SourceBackupWindow? window = null;
            var frame = new DispatcherFrame();
            try
            {
                // The construction is inside the try as well: a window that fails to build has to surface as
                // this test's failure, not leave the assertions below with nothing to look at.
                window = new SourceBackupWindow([book]) { ShowInTaskbar = false, Opacity = 0 };
                _ = window.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        window.Show();
                        window.UpdateLayout();
                        result = await work(window);
                    }
                    catch (Exception error) { failure = error; }
                    finally { frame.Continue = false; }
                });
                Dispatcher.PushFrame(frame);
            }
            catch (Exception error) { failure = error; }
            finally { try { window?.Close(); } catch { } }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "窗口线程没有在 60 秒内结束");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static Task<RestoreResult?> RestoreAsync(SourceBackupWindow window, string snapshot,
        RestoreTargetPolicy policy)
    {
        window.SelectEntry(window.Entries.Single(entry =>
            string.Equals(entry.Path, snapshot, StringComparison.OrdinalIgnoreCase)));
        return window.RestoreSelectedAsync(policy);
    }

    private static string Sha(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task The_restore_button_puts_a_saved_version_back_onto_the_book()
    {
        var book = await PrepareAsync();
        var store = SourceBackupStore.CreateDefault();
        store.EnsureBackup(book);
        var snapshot = store.EnsureSnapshot(await ChapterTreeDocument.LoadAsync(book));
        var originalSha = Sha(book);

        // 书稿后来变了 —— 程序修复过它，或者有人在外面改过。
        await File.WriteAllTextAsync(book, RepairedText, Utf8);
        Assert.NotEqual(originalSha, Sha(book));

        var restored = OnBackupWindow(book,
            window => RestoreAsync(window, snapshot, RestoreTargetPolicy.SourceOnly));

        Assert.NotNull(restored);
        Assert.True(restored!.Succeeded, restored.Message);
        Assert.Equal(originalSha, Sha(book));
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(book));
        // 恢复本身也是一次源文件改动，所以它同样留下一次事务记录 —— 恢复还能再恢复回去。
        Assert.NotNull(restored.Transaction);
        Assert.True(restored.Transaction!.Succeeded, restored.Transaction.Message);
    }

    [Fact]
    public async Task A_version_without_a_saved_tree_refuses_to_restore_the_chapter_tree()
    {
        var book = await PrepareAsync();
        var store = SourceBackupStore.CreateDefault();
        // 只有字节、没有树 —— 这正是「只保存原文」写出来的那种快照。
        var snapshot = store.EnsureSnapshot(await ChapterTreeDocument.LoadAsync(book));
        await File.WriteAllTextAsync(book, RepairedText, Utf8);
        var changedSha = Sha(book);

        var refused = OnBackupWindow(book,
            window => RestoreAsync(window, snapshot, RestoreTargetPolicy.FullBookState));

        Assert.NotNull(refused);
        Assert.False(refused!.Succeeded);
        Assert.Contains("没有保存章节树", refused.Message);
        // 拒绝发生在写之前：文件一个字节都没动。
        Assert.Equal(changedSha, Sha(book));
    }

    [Fact]
    public async Task A_version_with_a_saved_tree_can_restore_the_tree_as_well()
    {
        var book = await PrepareAsync();
        var store = SourceBackupStore.CreateDefault();
        var document = await ChapterTreeDocument.LoadAsync(book);
        var snapshot = store.EnsureSnapshot(document, document.CreatePlan(document.Entries));

        // 这份快照确实带着树，窗口因此可以把第二个选项摆出来。
        var entry = store.Inventory().ListEntries(book)
            .Single(item => string.Equals(item.Path, snapshot, StringComparison.OrdinalIgnoreCase));
        Assert.True(entry.HasSavedTree, "带树的快照在清单里没有标出章节树。");
        Assert.Equal("含章节树", entry.TreeLabel);

        await File.WriteAllTextAsync(book, RepairedText, Utf8);

        var restored = OnBackupWindow(book,
            window => RestoreAsync(window, snapshot, RestoreTargetPolicy.FullBookState));

        Assert.NotNull(restored);
        Assert.True(restored!.Succeeded, restored.Message);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(book));
        // 恢复到的是**当时保存的那棵树**，不是恢复后重新识别的一棵。
        Assert.NotNull(restored.State?.Plan);
        Assert.Equal(restored.RestoredSha256, restored.State!.Plan!.SourceSha256);
    }

    [Fact]
    public async Task The_finish_button_completes_an_interrupted_source_edit()
    {
        var book = await PrepareAsync();
        var store = SourceBackupStore.CreateDefault();

        // 一条停在 TempWritten 的记录：渲染好的字节已经躺在源文件旁边，替换还没有发生。
        // 这是恢复规则里唯一允许程序自己补完的一格，也正是"崩溃后重启"会留下的形状。
        var transactionId = Guid.NewGuid().ToString("N");
        var sibling = book + "." + transactionId + ".easypub-tmp";
        await File.WriteAllTextAsync(sibling, RepairedText, Utf8);
        new RepairJournalStore(store.DirectoryPath).Save(new RepairJournal(
            RepairJournal.CurrentSchemaVersion, transactionId, book, Sha(book),
            SourceFileHasher.HashOf(sibling), RepairJournalState.TempWritten,
            DateTimeOffset.Now, DateTimeOffset.Now));

        var report = OnBackupWindow(book, async window => await window.FinishPendingAsync());

        Assert.True(report.AnythingHappened);
        Assert.Single(report.Results);
        Assert.Equal(SourceEditOutcome.Committed, report.Results[0].Outcome);
        Assert.Empty(report.NeedsAttention);
        Assert.Equal(RepairedText, await File.ReadAllTextAsync(book));
        // 收尾之后不再有未完成的记录，也不再有残留的临时文件。
        Assert.Empty(SourceEditRecovery.Outstanding(store.DirectoryPath));
        Assert.False(File.Exists(sibling));

        // 再问一次：已经没有事情可做，而不是把同一个替换做第二遍。
        var again = OnBackupWindow(book, async window => await window.FinishPendingAsync());
        Assert.False(again.AnythingHappened);
        Assert.Equal(RepairedText, await File.ReadAllTextAsync(book));
    }
}
