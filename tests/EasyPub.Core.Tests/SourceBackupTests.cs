using System.IO;
using System.Security.Cryptography;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 备份层的行为契约。
///
/// 这一层最容易出错的地方不是"备份没做成"，而是"备份做成了但顺手改了别的东西"。所以每个测试都
/// 盯着同一件事：**书稿的字节在整个过程中一个都没变**。Phase 5 之前这一层不允许有任何写回能
/// 力 —— 恢复备份本身也是一次源文件改动，需要事务、journal 与崩溃恢复，不是复制文件那么简单。
/// </summary>
public class SourceBackupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"easypub-backup-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string WriteBook(string name, string text)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task A_first_backup_lands_in_the_new_layout_with_a_manifest()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root);

        var baseline = store.EnsureBackup(book);

        Assert.True(File.Exists(baseline));
        Assert.Equal(SourceBackupLayout.BaselinePath(store.Folder(book)), baseline);
        Assert.True(File.Exists(SourceBackupLayout.ManifestPath(store.Folder(book))));

        var manifest = store.Inventory().ReadBookManifest(book);
        Assert.NotNull(manifest);
        Assert.Equal(Path.GetFullPath(book), manifest!.SourcePath);
        Assert.Equal("书.txt", manifest.FileName);
        Assert.Equal(Sha(book), manifest.BaselineSha256);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_second_backup_does_not_replace_the_first_seen_original()
    {
        var book = WriteBook("书.txt", "第一版\n");
        var store = new SourceBackupStore(_root);
        var first = store.EnsureBackup(book);
        var firstBytes = File.ReadAllBytes(first);

        File.WriteAllText(book, "第二版\n");
        var second = store.EnsureBackup(book);

        Assert.Equal(first, second);
        Assert.Equal(firstBytes, File.ReadAllBytes(second));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_snapshot_is_content_addressed_and_never_duplicated()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root);
        var document = await ChapterTreeDocument.LoadAsync(book);

        var first = store.EnsureSnapshot(document);
        var second = store.EnsureSnapshot(document);

        Assert.Equal(first, second);
        Assert.Single(store.Inventory().ListEntries(book).Where(entry => entry.Kind == SourceBackupKind.Snapshot));
        Assert.Equal(Sha(book), Path.GetFileNameWithoutExtension(first));
    }

    [Fact]
    public async Task A_snapshot_refuses_when_the_book_changed_underneath()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root);
        var document = await ChapterTreeDocument.LoadAsync(book);
        File.WriteAllText(book, "第1章 开始\n正文改过了\n");

        Assert.Throws<InvalidDataException>(() => store.EnsureSnapshot(document));
    }

    /// <summary>整层最重要的一条：备份动作不改变书稿的任何一个字节。</summary>
    [Fact]
    public async Task Backing_up_and_pruning_never_touches_the_book()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var before = Sha(book);
        var beforeTime = File.GetLastWriteTimeUtc(book);
        var store = new SourceBackupStore(_root) { SnapshotRetentionLimit = 1 };
        var document = await ChapterTreeDocument.LoadAsync(book);

        store.EnsureBackup(book);
        for (var index = 0; index < 3; index++) store.EnsureSnapshot(document);
        store.Inventory().PruneAll(1);

        Assert.Equal(before, Sha(book));
        Assert.Equal(beforeTime, File.GetLastWriteTimeUtc(book));
    }

    [Fact]
    public async Task The_baseline_survives_a_prune_that_removes_every_snapshot()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root);
        var document = await ChapterTreeDocument.LoadAsync(book);
        var baseline = store.EnsureBackup(book);
        store.EnsureSnapshot(document);

        var result = store.Inventory().Prune(book, SourceBackupRetention.MinimumSnapshotLimit);
        _ = result;
        // 保留数下限是 1，所以那一份快照应当留下；首次原文无论如何都必须还在。
        Assert.True(File.Exists(baseline));
        var entries = store.Inventory().ListEntries(book);
        Assert.Contains(entries, entry => entry.Kind == SourceBackupKind.Baseline);
    }

    [Fact]
    public async Task The_baseline_is_listed_last_so_it_never_reads_as_just_another_snapshot()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root);
        var document = await ChapterTreeDocument.LoadAsync(book);
        store.EnsureBackup(book);
        store.EnsureSnapshot(document);

        var entries = store.Inventory().ListEntries(book);
        Assert.Equal(SourceBackupKind.Baseline, entries[^1].Kind);
        Assert.Equal("首次原文", entries[^1].KindLabel);
    }

    /// <summary>
    /// 清单丢了，备份不能被隐藏。索引只是索引：列出来的东西以文件夹里的文件为准。
    /// </summary>
    [Fact]
    public async Task A_lost_manifest_does_not_hide_the_backups()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root);
        var document = await ChapterTreeDocument.LoadAsync(book);
        store.EnsureBackup(book);
        store.EnsureSnapshot(document);
        File.Delete(SourceBackupLayout.ManifestPath(store.Folder(book)));

        var entries = store.Inventory().ListEntries(book);
        Assert.Equal(2, entries.Count);

        // 按文件夹发现时仍然能被数到，只是路径未知。
        var books = store.Inventory().ListBooks();
        Assert.Single(books);
        Assert.Equal(1, books[0].SnapshotCount);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_corrupt_manifest_is_treated_as_absent_not_as_an_error()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root);
        store.EnsureBackup(book);
        File.WriteAllText(SourceBackupLayout.ManifestPath(store.Folder(book)), "{ this is not json");

        Assert.Null(store.Inventory().ReadBookManifest(book));
        Assert.Single(store.Inventory().ListEntries(book));
        await Task.CompletedTask;
    }

    /// <summary>
    /// 早期版本留在书稿旁边的 .bak 必须仍然可见。看不见的备份等于没有备份。
    /// </summary>
    [Fact]
    public async Task Backups_left_beside_the_book_by_an_older_version_stay_visible()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var legacy = book + "." + Guid.NewGuid().ToString("N") + ".bak";
        File.Copy(book, legacy);

        var found = new SourceBackupStore(_root).FindBackups(book);

        Assert.Contains(legacy, found);
        await Task.CompletedTask;
    }

    /// <summary>不是本程序生成的 .bak 不能被认领 —— 读者自己放在那里的文件不归我们管。</summary>
    [Fact]
    public async Task An_unrelated_bak_beside_the_book_is_not_claimed()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var unrelated = book + ".我的手动备份.bak";
        File.Copy(book, unrelated);

        var found = new SourceBackupStore(_root).FindBackups(book);

        Assert.DoesNotContain(unrelated, found);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Exporting_writes_a_copy_and_leaves_both_source_and_backup_untouched()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root);
        var baseline = store.EnsureBackup(book);
        var bookSha = Sha(book);
        var backupSha = Sha(baseline);
        var destination = Path.Combine(_root, "导出", "副本.txt");

        var inventory = store.Inventory();
        var size = await inventory.ExportAsync(baseline, destination);

        Assert.True(size > 0);
        Assert.Equal(bookSha, Sha(destination));
        Assert.Equal(bookSha, Sha(book));
        Assert.Equal(backupSha, Sha(baseline));
    }

    /// <summary>
    /// 有未完成事务的书不清快照。Phase 5 才会写 journal，但这条规则现在就得在，
    /// 否则等它开始有意义时很容易被忘掉。
    /// </summary>
    [Fact]
    public async Task Pruning_refuses_while_a_transaction_journal_exists()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root) { SnapshotRetentionLimit = 1 };
        var document = await ChapterTreeDocument.LoadAsync(book);
        store.EnsureSnapshot(document);
        Directory.CreateDirectory(Path.Combine(store.Folder(book), "journal"));

        var result = store.Inventory().Prune(book, 1);

        Assert.NotNull(result.RefusedBecause);
        Assert.Empty(result.Removed);
        Assert.Contains("未完成的事务", result.RefusedBecause!);
    }

    [Fact]
    public async Task Two_books_with_the_same_file_name_get_separate_folders()
    {
        var first = WriteBook(Path.Combine("甲", "书.txt"), "甲\n");
        var second = WriteBook(Path.Combine("乙", "书.txt"), "乙\n");
        var store = new SourceBackupStore(_root);

        var a = store.EnsureBackup(first);
        var b = store.EnsureBackup(second);

        Assert.NotEqual(store.Folder(first), store.Folder(second));
        Assert.NotEqual(a, b);
        Assert.Equal("甲\n", File.ReadAllText(a, Encoding.UTF8).Replace("\r\n", "\n"));
        Assert.Equal("乙\n", File.ReadAllText(b, Encoding.UTF8).Replace("\r\n", "\n"));
    }

    /// <summary>
    /// 自动清理必须用 store 上的保留数，而不是默认值。
    ///
    /// 这条测试的来历：<c>EnsureSnapshot</c> 内部调 <c>Inventory()</c>，而 inventory 当时用的是
    /// 默认的 20 —— 于是把保留数设成 3 也完全不生效，快照一直堆到 20 份。这类"设置读了但没用"
    /// 的错误不会报任何异常，只会让用户在磁盘上慢慢攒下几百份备份。
    /// </summary>
    [Fact]
    public async Task Automatic_pruning_honours_the_stores_own_retention_limit()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文 0\n");
        for (var index = 0; index < 6; index++)
        {
            await File.WriteAllTextAsync(book, $"第1章 开始\n正文 {index}\n", new UTF8Encoding(false));
            var document = await ChapterTreeDocument.LoadAsync(book);
            new SourceBackupStore(_root) { SnapshotRetentionLimit = 3 }.EnsureSnapshot(document);
            // 文件系统时间戳的分辨率有限，拉开间隔才能让"最近 3 份"是确定的。
            await Task.Delay(20);
        }

        var store = new SourceBackupStore(_root) { SnapshotRetentionLimit = 3 };
        var snapshots = store.Inventory().ListEntries(book)
            .Count(entry => entry.Kind == SourceBackupKind.Snapshot);

        Assert.Equal(3, snapshots);
        Assert.True(File.Exists(SourceBackupLayout.BaselinePath(store.Folder(book))),
            "保留策略不该动首次原文");
    }

    /// <summary>同一时间戳下的顺序必须是全序，否则每次清理保留的集合都不一样。</summary>
    [Fact]
    public async Task Pruning_is_stable_when_timestamps_tie()
    {
        var book = WriteBook("书.txt", "第1章 开始\n正文\n");
        var store = new SourceBackupStore(_root);
        var snapshots = Path.Combine(store.Folder(book), SourceBackupLayout.SnapshotsFolder);
        Directory.CreateDirectory(snapshots);
        // 三份同样大小、同样时间戳的快照，只有内容校验值不同。
        var stamp = DateTime.UtcNow;
        for (var index = 1; index <= 3; index++)
        {
            var path = Path.Combine(snapshots, new string((char)('A' + index), 64) + ".bak");
            await File.WriteAllTextAsync(path, $"内容 {index}");
            File.SetLastWriteTimeUtc(path, stamp);
        }

        var first = store.Inventory().Prune(book, 1);
        var second = store.Inventory().Prune(book, 1);

        Assert.Equal(2, first.Removed.Count);
        Assert.Empty(second.Removed);
    }
}
