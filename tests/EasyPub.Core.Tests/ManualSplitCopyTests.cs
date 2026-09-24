using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class ManualSplitCopyTests
{
    private static (string Root, ChapterTreeDocument Document, byte[] Bytes) Fixture(string? pattern = null, bool trailingNewline = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-split-copy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "原稿.txt");
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            "第一章 原章\r\n正文甲\r\n正文乙\r\n正文丙\r\n第二章 后章\r\n正文丁\r\n正文戊" + (trailingNewline ? "\r\n" : ""))).ToArray();
        File.WriteAllBytes(path, bytes);
        var doc = ChapterTreeDocument.Load(path, bytes, chapterPattern: pattern);
        return (root, doc.WithEntries([new("merged", "合并章节", 1, true, 1,
            [new(6, trailingNewline ? 8 : 7), new(2, 4)]) { RecognitionSource = "manual" }]), bytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_preserves_original_encoding_body_order_and_reopen_plan(bool trailingNewline)
    {
        var (root, doc, bytes) = Fixture(trailingNewline: trailingNewline);
        var prepared = ManualSplitCopy.Prepare(doc, "merged", 3, "第三章 新边界");
        var result = await prepared.ExportAsync(new SourceBackupStore(Path.Combine(root, "backups")));
        Assert.True(result.Succeeded, result.Transaction.Message);
        Assert.Equal(bytes, File.ReadAllBytes(doc.SourcePath));
        Assert.Equal(bytes, File.ReadAllBytes(result.Transaction.BackupPath!));
        var expected = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            "第一章 原章\r\n正文甲\r\n第三章 新边界\r\n正文乙\r\n正文丙\r\n第二章 后章\r\n正文丁\r\n正文戊" + (trailingNewline ? "\r\n" : ""))).ToArray();
        Assert.Equal(expected, File.ReadAllBytes(result.CopyPath));
        var project = await new EasyPubProjectStore(result.ProjectPath).LoadAsync();
        var saved = Assert.Single(project.Books).ChapterTree!;
        var reopened = await ChapterTreeDocument.LoadAsync(result.CopyPath, existingPlan: saved);
        Assert.Equal(new[] { "合并章节", "第三章 新边界" }, reopened.Entries.Select(e => e.Title));
        Assert.Equal(3, reopened.Entries[1].TitleLineNumber);
        var expectedBody = trailingNewline ? new[] { "正文丁", "正文戊", "", "正文甲", "正文乙", "正文丙" }
            : new[] { "正文丁", "正文戊", "正文甲", "正文乙", "正文丙" };
        Assert.Equal(expectedBody, reopened.Entries
            .SelectMany(e => e.ContentRanges).SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1))
            .Select(n => reopened.SourceLine(n)!.Text));
        Assert.Equal("\r\n", SourceTextDocument.Load(result.CopyPath).DefaultNewLine);
    }

    [Fact]
    public void Unrecognized_title_is_refused_before_export()
    {
        var (_, doc, bytes) = Fixture("^第[一二]章 .+$");
        var error = Assert.Throws<InvalidDataException>(() => ManualSplitCopy.Prepare(doc, "merged", 3, "第三章 新边界"));
        Assert.Contains("识别规则", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(doc.SourcePath));
    }

    [Fact]
    public async Task Changed_source_is_refused_after_preparation()
    {
        var (root, doc, _) = Fixture();
        var prepared = ManualSplitCopy.Prepare(doc, "merged", 3, "第三章 新边界");
        File.AppendAllText(doc.SourcePath, "外部编辑");
        await Assert.ThrowsAsync<InvalidDataException>(() => prepared.ExportAsync(new SourceBackupStore(Path.Combine(root, "backups"))));
        Assert.False(Directory.Exists(Path.Combine(root, "backups")));
    }

    [Theory]
    [InlineData(RepairJournalState.BackupCreated)]
    [InlineData(RepairJournalState.SourceReplaced)]
    [InlineData(RepairJournalState.DocumentReloaded)]
    public async Task Interrupted_copy_keeps_original_and_restores_copy(RepairJournalState stop)
    {
        var (root, doc, bytes) = Fixture();
        var prepared = ManualSplitCopy.Prepare(doc, "merged", 3, "第三章 新边界");
        prepared.AfterStep = state => { if (state == stop) throw new TransactionInterrupted(state.ToString()); };
        var result = await prepared.ExportAsync(new SourceBackupStore(Path.Combine(root, "backups")));
        Assert.False(result.Succeeded);
        Assert.Equal(bytes, File.ReadAllBytes(doc.SourcePath));
        Assert.Equal(bytes, File.ReadAllBytes(result.CopyPath));
        Assert.Equal(bytes, File.ReadAllBytes(result.Transaction.BackupPath!));
        Assert.False(File.Exists(result.ProjectPath));
        if (stop != RepairJournalState.BackupCreated)
        {
            Assert.Equal(SourceEditOutcome.RolledBack, result.Transaction.Outcome);
            Assert.NotEmpty(Directory.GetFiles(Path.GetDirectoryName(result.CopyPath)!, "*.failed-*"));
        }
    }
}
