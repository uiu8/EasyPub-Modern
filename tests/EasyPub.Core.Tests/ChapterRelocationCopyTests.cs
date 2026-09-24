using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class ChapterRelocationCopyTests
{
    [Theory]
    [InlineData(RepairJournalState.BackupCreated)]
    [InlineData(RepairJournalState.SourceReplaced)]
    [InlineData(RepairJournalState.DocumentReloaded)]
    public async Task Interrupted_move_restores_copy_and_keeps_original(RepairJournalState stop)
    {
        var root = Path.Combine(Path.GetTempPath(), "move-interrupted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "original.txt");
        await File.WriteAllTextAsync(path, "第一章 甲\n正文甲\n第二章 乙\n正文乙\n第三章 丙\n正文丙");
        var bytes = File.ReadAllBytes(path);
        var doc = await ChapterTreeDocument.LoadAsync(path);
        var move = ChapterRelocationCopy.Prepare(doc, [doc.Entries.Single(e => e.Title == "第一章 甲").Id],
            doc.Entries.Single(e => e.Title == "第三章 丙").Id);
        move.AfterStep = state => { if (state == stop) throw new TransactionInterrupted(state.ToString()); };
        var result = await move.ExportAsync(new SourceBackupStore(Path.Combine(root, "store")));
        Assert.False(result.Succeeded);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(bytes, File.ReadAllBytes(result.CopyPath));
        Assert.Equal(bytes, File.ReadAllBytes(result.Transaction.BackupPath!));
        Assert.False(File.Exists(result.ProjectPath));
        if (stop != RepairJournalState.BackupCreated)
            Assert.Equal(SourceEditOutcome.RolledBack, result.Transaction.Outcome);
    }

    [Fact]
    public async Task Moving_forward_preserves_intermediate_chapter_and_rejects_shared_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "move-forward-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "original.txt");
        await File.WriteAllTextAsync(path, "第一章 甲\n正文甲\n第二章 乙\n正文乙\n第三章 丙\n正文丙");
        var doc = await ChapterTreeDocument.LoadAsync(path);
        var chapters = doc.Entries.Where(e => !e.IsFrontMatter).ToArray();
        var move = ChapterRelocationCopy.Prepare(doc, [chapters[0].Id], chapters[2].Id);
        var result = await move.ExportAsync(new SourceBackupStore(Path.Combine(root, "store")));
        Assert.True(result.Succeeded, result.Transaction.Message);
        Assert.Equal("第二章 乙\n正文乙\n第一章 甲\n正文甲\n第三章 丙\n正文丙", File.ReadAllText(result.CopyPath));
        var fragmented = doc.WithEntries([chapters[0] with { ContentRanges = [new(2, 2), new(4, 4)] }, chapters[1], chapters[2]]);
        Assert.Throws<InvalidDataException>(() => ChapterRelocationCopy.Prepare(fragmented, [chapters[0].Id], chapters[2].Id));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Moving_block_preserves_bytes_body_mapping_backup_and_eof(bool trailing)
    {
        var root = Path.Combine(Path.GetTempPath(), "move-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "原稿.txt");
        var text = "第一章 甲\r\n正文甲\n第三章 丙\r\n正文丙\n第二章 乙\r\n正文乙" + (trailing ? "\n" : "");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(true));
        var bytes = File.ReadAllBytes(path);
        var doc = await ChapterTreeDocument.LoadAsync(path);
        var move = ChapterRelocationCopy.Prepare(doc, [doc.Entries.Single(e => e.Title == "第二章 乙").Id], doc.Entries.Single(e => e.Title == "第三章 丙").Id);
        Assert.Equal(Enumerable.Range(1, 6), move.Lines.Select(l => l.OriginalLine).Order());
        var result = await move.ExportAsync(new SourceBackupStore(Path.Combine(root, "store")));
        Assert.True(result.Succeeded, result.Transaction.Message);
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(bytes, File.ReadAllBytes(result.Transaction.BackupPath!));
        Assert.True(File.Exists(result.CopyPath + ".movement.json"));
        var physical = SourceTextDocument.Load(result.CopyPath);
        Assert.Equal(new[] { "第一章 甲", "正文甲", "第二章 乙", "正文乙", "第三章 丙", "正文丙" }, physical.Lines.Select(l => l.Text));
        Assert.Equal(SourceTextDocument.Load(path).Lines.Select(l => l.Terminator), physical.Lines.Select(l => l.Terminator));
        var book = Assert.Single((await new EasyPubProjectStore(result.ProjectPath).LoadAsync()).Books);
        var reopened = await ChapterTreeDocument.LoadAsync(book.InputPath, existingPlan: book.ChapterTree);
        Assert.Equal(new[] { "第一章 甲", "第二章 乙", "第三章 丙" }, reopened.Entries.Where(e => !e.IsFrontMatter).Select(e => e.Title));
        Assert.Equal(new[] { "正文甲", "正文乙", "正文丙" }, reopened.Entries.SelectMany(reopened.GetSourceLines).Select(l => l.Text).Where(t => t.Length > 0));
        await File.AppendAllTextAsync(path, "外部修改");
        await Assert.ThrowsAsync<InvalidDataException>(() => move.ExportAsync(new SourceBackupStore(Path.Combine(root, "stale"))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Six_volume_21_chapter_physical_move_preserves_all_bodies_and_ownership(bool referenceBasis)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !Directory.Exists(Path.Combine(repo.FullName, "samples", "章节独立场景"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var fixture = Path.Combine(repo.FullName, "samples", "章节独立场景", "S3");
        if (referenceBasis)
            Assert.Equal(690, (await File.ReadAllLinesAsync(Path.Combine(fixture, "参考目录.txt"))).Count(line => line.StartsWith("第") && line.Contains("章")));
        var doc = await ChapterTreeDocument.LoadAsync(Path.Combine(fixture, "缺陷样书.txt"), hierarchy: new TocHierarchyOptions { Enabled = true });
        var original = File.ReadAllBytes(doc.SourcePath);
        var ids = Enumerable.Range(20, 21).Select(n => doc.Entries.Single(e => e.Title == $"第{n}章 卷2的旅程{n}").Id).ToArray();
        var move = ChapterRelocationCopy.Prepare(doc, ids, doc.Entries.Single(e => e.Title == "第41章 卷2的旅程41").Id);
        var root = Path.Combine(repo.FullName, "work", "p3-2-acceptance", "physical-" + Guid.NewGuid().ToString("N")[..8]);
        var result = await move.ExportAsync(new SourceBackupStore(root));
        Assert.True(result.Succeeded, result.Transaction.Message);
        var book = Assert.Single((await new EasyPubProjectStore(result.ProjectPath).LoadAsync()).Books);
        var reopened = await ChapterTreeDocument.LoadAsync(book.InputPath, existingPlan: book.ChapterTree);
        // Also recognize the actual TXT from scratch: this proves physical order, not just a saved tree.
        var recognized = await ChapterTreeDocument.LoadAsync(book.InputPath, hierarchy: new TocHierarchyOptions { Enabled = true });
        using var truth = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "真值清单.json")));
        var expected = truth.RootElement.GetProperty("chapters").EnumerateArray().ToArray();
        foreach (var actual in new[] { reopened, recognized })
        {
            var chapters = actual.Entries.Where(e => e.Level == 2 && !e.IsFrontMatter).ToArray();
            Assert.Equal(690, chapters.Length);
            var volume = 0; var rank = 0;
            foreach (var entry in actual.Entries.Where(e => !e.IsFrontMatter))
            {
                if (entry.Level == 1) { volume++; continue; }
                Assert.Equal(expected[rank].GetProperty("volume").GetInt32(), volume);
                Assert.Equal(expected[rank].GetProperty("title").GetString(), entry.Title);
                var body = string.Join('\n', actual.GetSourceLines(entry).Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
                Assert.Equal(expected[rank].GetProperty("body_sha256").GetString(), Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
                rank++;
            }
            Assert.Equal(6, volume);
        }
        await VerifyKindlingAsync(book.InputPath, book.ChapterTree!, root);
        Assert.Equal(original, File.ReadAllBytes(doc.SourcePath));
        Assert.Equal(original, File.ReadAllBytes(result.Transaction.BackupPath!));
    }

    private static async Task VerifyKindlingAsync(string inputPath, ChapterTreePlan plan, string root)
    {
        var engine = Environment.GetEnvironmentVariable("EASYPUB_TEST_KINDLING");
        if (string.IsNullOrWhiteSpace(engine)) return;
        Assert.True(File.Exists(engine), "EASYPUB_TEST_KINDLING 指向的 Kindling 不存在：" + engine);
        var output = Path.Combine(root, "移动后-kindling.mobi");
        var options = ConversionOptions.LegacyDefault with
        {
            ChapterPattern = "^不应重新识别$",
            AddFullWidthIndent = false,
            Mobi = new MobiOptions { Engine = KindleConversionEngine.Kindling, KindlingPath = engine },
        };
        var converted = await new EasyPubConverter().ConvertAsync(new(inputPath, output, Options: options)
        { ChapterTree = plan });
        Assert.Equal(plan.Entries.Count, converted.ChapterCount);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(bytes, 60, 8));
        Assert.True(bytes.Length > 1024);
        Assert.NotEmpty(Directory.GetFiles(root, "*.kindling-*.log"));
    }
}
