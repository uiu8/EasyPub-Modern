using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 随快照一起保存的章节树。
///
/// 这份文件是"恢复到当时的完整书稿状态"唯一的数据来源。它只有两种结局可以接受：读回来和写进去一样，
/// 或者读不回来（当作没有）。**最不能接受的是读回一棵"大致能用的树"** —— 树里的行号在另一个版本里
/// 指向别的地方，而它看起来完全正常。
/// </summary>
public class SourceFullStateStoreTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "easypub-fullstate-tests-" + Guid.NewGuid().ToString("N"));

    public SourceFullStateStoreTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private async Task<(string BookPath, ChapterTreeDocument Document)> BookAsync(string? text = null)
    {
        var path = Path.Combine(_workspace, "书稿.txt");
        await File.WriteAllTextAsync(path, text ?? "第一章 起点\n正文一。\n第二章 中途\n正文二。\n",
            new UTF8Encoding(false));
        return (path, await ChapterTreeDocument.LoadAsync(path));
    }

    [Fact]
    public async Task A_saved_tree_reads_back_identical()
    {
        var (path, _) = await BookAsync();
        var document = await ChapterTreeDocument.LoadAsync(
            path,
            chapterPattern: "^第[一二]章.*$",
            hierarchy: new TocHierarchyOptions
            {
                Enabled = true,
                IncludeHtmlTocPage = true,
                NumericHeadingMinimumBodyLines = 13,
                Level1Pattern = "^卷",
                Level2Pattern = "^章",
            });
        var snapshotPath = Path.Combine(_workspace, "快照.tree.json");
        var snapshot = new SourceFullStateSnapshot(document.SourceSha256, document.LineCount,
            document.CreatePlan(document.Entries));

        Assert.True(SourceFullStateStore.Write(snapshotPath, snapshot));
        var read = SourceFullStateStore.Read(snapshotPath);

        Assert.NotNull(read);
        Assert.Equal(document.SourceSha256, read!.SourceSha256);
        Assert.Equal(document.LineCount, read.LineCount);
        Assert.Equal(document.ChapterPattern, read.Tree.ChapterPattern);
        Assert.Equal(document.RecognitionOptions, read.Tree.RecognitionOptions);
        Assert.Equal(document.Entries.Count, read.Tree.Entries.Count);
        foreach (var (expected, actual) in document.Entries.Zip(read.Tree.Entries))
        {
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Level, actual.Level);
            Assert.Equal(expected.IncludeInToc, actual.IncludeInToc);
            Assert.Equal(expected.TitleLineNumber, actual.TitleLineNumber);
            Assert.Equal(expected.IsFrontMatter, actual.IsFrontMatter);
            Assert.Equal(expected.HeadingLevel, actual.HeadingLevel);
            Assert.Equal(expected.RecognitionSource, actual.RecognitionSource);
            Assert.Equal(expected.StableKey, actual.StableKey);
            Assert.Equal(
                expected.ContentRanges.Select(range => (range.StartLine, range.EndLine)),
                actual.ContentRanges.Select(range => (range.StartLine, range.EndLine)));
        }
        _ = path;
    }

    [Fact]
    public async Task A_hand_edited_tree_survives_the_round_trip()
    {
        // 用户编排出来的样子才是这份文件存在的理由：一个不进入目录的章节、一个被改过的标题。
        var (_, document) = await BookAsync();
        var arranged = document.Entries.Select(entry => entry.Title switch
        {
            "第二章 中途" => entry with { IncludeInToc = false, Title = "第二章 改过的标题" },
            _ => entry,
        }).ToArray();
        var snapshotPath = Path.Combine(_workspace, "编排.tree.json");
        var plan = document.CreatePlan(arranged);

        Assert.True(SourceFullStateStore.Write(snapshotPath,
            new SourceFullStateSnapshot(document.SourceSha256, document.LineCount, plan)));
        var read = SourceFullStateStore.Read(snapshotPath)!;

        var second = read.Tree.Entries.Single(entry => entry.Title == "第二章 改过的标题");
        Assert.False(second.IncludeInToc);
    }

    [Fact]
    public async Task A_missing_file_reads_as_no_tree()
    {
        await BookAsync();
        Assert.Null(SourceFullStateStore.Read(Path.Combine(_workspace, "没有这个文件.tree.json")));
    }

    [Fact]
    public async Task A_corrupt_file_reads_as_no_tree()
    {
        // 宁可当作"没有保存的树"，也不要拿一棵读歪了的树去恢复。
        var (_, document) = await BookAsync();
        var path = Path.Combine(_workspace, "坏.tree.json");
        await File.WriteAllTextAsync(path, "{ 这不是 json", new UTF8Encoding(false));
        Assert.Null(SourceFullStateStore.Read(path));

        // 截断的 JSON 同样如此。
        var good = Path.Combine(_workspace, "好.tree.json");
        SourceFullStateStore.Write(good, new SourceFullStateSnapshot(document.SourceSha256, document.LineCount,
            document.CreatePlan(document.Entries)));
        var truncated = Path.Combine(_workspace, "截断.tree.json");
        await File.WriteAllTextAsync(truncated, (await File.ReadAllTextAsync(good))[..40], new UTF8Encoding(false));
        Assert.Null(SourceFullStateStore.Read(truncated));
    }

    [Fact]
    public async Task A_tree_whose_ranges_exceed_its_text_reads_as_no_tree()
    {
        // 行数记录得比树的范围还小：这是"树不属于这份文本"的形状，必须拒绝而不是照用。
        var (_, document) = await BookAsync();
        var path = Path.Combine(_workspace, "越界.tree.json");
        SourceFullStateStore.Write(path, new SourceFullStateSnapshot(document.SourceSha256, document.LineCount,
            document.CreatePlan(document.Entries)));

        var damaged = (await File.ReadAllTextAsync(path))
            .Replace($"\"LineCount\": {document.LineCount}", "\"LineCount\": 1", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, damaged, new UTF8Encoding(false));

        Assert.Null(SourceFullStateStore.Read(path));
    }

    [Fact]
    public async Task A_snapshot_written_by_the_store_carries_its_tree()
    {
        // 备份层与快照格式的接缝：存进去的树要能被读回来，而且"这份快照能恢复完整书稿状态"要能被问出来。
        var (path, document) = await BookAsync();
        var store = new SourceBackupStore(Path.Combine(_workspace, "备份"));

        var snapshotPath = store.EnsureSnapshot(document, document.CreatePlan(document.Entries));
        var hash = Path.GetFileNameWithoutExtension(snapshotPath).ToUpperInvariant();

        Assert.True(File.Exists(snapshotPath));
        Assert.True(store.HasSnapshotTree(path, hash));
        Assert.True(store.CanRestoreFullState(path));

        var snapshot = store.ReadSnapshotTree(path, hash);
        Assert.NotNull(snapshot);
        Assert.Equal(document.SourceSha256, snapshot!.SourceSha256);
        Assert.Equal(document.Entries.Count, snapshot.Tree.Entries.Count);
    }

    [Fact]
    public async Task A_store_without_a_tree_says_so()
    {
        var (path, document) = await BookAsync();
        var store = new SourceBackupStore(Path.Combine(_workspace, "备份"));

        // 不传树：只有字节，所以完整书稿状态恢复不被提供。
        var snapshotPath = store.EnsureSnapshot(document);
        var hash = Path.GetFileNameWithoutExtension(snapshotPath).ToUpperInvariant();

        Assert.False(store.HasSnapshotTree(path, hash));
        Assert.False(store.CanRestoreFullState(path));
        Assert.Null(store.ReadSnapshotTree(path, hash));
    }

    [Fact]
    public async Task A_tree_read_under_a_different_hash_is_refused()
    {
        var (path, document) = await BookAsync();
        var store = new SourceBackupStore(Path.Combine(_workspace, "备份"));
        var snapshotPath = store.EnsureSnapshot(document, document.CreatePlan(document.Entries));
        var hash = Path.GetFileNameWithoutExtension(snapshotPath).ToUpperInvariant();

        // 拿另一个哈希去问同一棵树：它不是那个版本的树。
        Assert.Null(store.ReadSnapshotTree(path, new string('A', hash.Length)));
    }

    [Fact]
    public async Task The_inventory_reports_which_snapshots_can_be_restored_in_full()
    {
        var (path, document) = await BookAsync();
        var store = new SourceBackupStore(Path.Combine(_workspace, "备份"));
        store.EnsureSnapshot(document); // 只有字节
        store.EnsureSnapshot(document, document.CreatePlan(document.Entries)); // 字节 + 树

        var manifest = store.Inventory().ReadBookManifest(path);
        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.FullStateSnapshotCount);

        var entries = store.Inventory().ListEntries(path);
        // 同一份字节只会有一个快照文件，所以这里检查的是"它带树"这件事被报出来了。
        Assert.Contains(entries, entry => entry.HasSavedTree);
        Assert.All(entries.Where(entry => entry.HasSavedTree), entry => Assert.Equal("含章节树", entry.TreeLabel));
    }
}
