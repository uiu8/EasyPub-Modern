using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 把保存过的版本放回去。
///
/// 恢复不是一条特殊路径：它走同一个事务（前置检查、记录、原子替换、清单、续做），区别只有两点 ——
/// 目标文本来自快照而不是编译出来的补丁，以及事后那棵树是按恢复后的文本重新识别、还是用随快照一起
/// 保存的那一棵。
///
/// 这里要钉住的是**两种策略各恢复什么**，以及几条必须拒绝的情形：备份不在、目标树不属于这份备份、
/// 原文本已被外界改过、以及"已经是这个版本"。
/// </summary>
public class RestoreTransactionTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "easypub-restore-tests-" + Guid.NewGuid().ToString("N"));

    public RestoreTransactionTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const string VersionOne =
        "第一章 起点\n" +
        "正文一。\n" +
        "第二章 中途\n" +
        "正文二。\n";

    private const string VersionTwo =
        "第一章 起点\n" +
        "正文一。\n" +
        "第二章 中途\n" +
        "正文二。\n" +
        "第三章 新增\n" +
        "正文三。\n";

    private sealed record Fixture(string BookPath, string BackupPath, string TransactionRoot,
        string BackupDestination);

    /// <summary>写一份"当前版本"，把"要恢复的版本"存成备份，返回三者的路径。</summary>
    private async Task<Fixture> ArrangeAsync(string current, string saved, string? saveTree = null)
    {
        var bookPath = Path.Combine(_workspace, "书稿.txt");
        await File.WriteAllTextAsync(bookPath, current, new UTF8Encoding(false));
        // 备份存的是一份**书稿**的字节，所以来源本来就是一个 .txt —— 章节树按路径读取时要求这一点。
        var backupPath = Path.Combine(_workspace, "备份", "保存的版本.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        await File.WriteAllTextAsync(backupPath, saved, new UTF8Encoding(false));
        if (saveTree is not null)
        {
            var treeStore = new SourceBackupStore(Path.Combine(_workspace, "自动备份"));
            var document = await ChapterTreeDocument.LoadAsync(backupPath);
            treeStore.EnsureSnapshot(document, document.CreatePlan(document.Entries));
        }
        return new Fixture(bookPath, backupPath, Path.Combine(_workspace, "事务记录"),
            Path.Combine(_workspace, "恢复前备份", "旧版本.bak"));
    }

    private static async Task<SourceTransitionState> StateAsync(string path) =>
        await SourceTransitionState.LoadFromAsync(path);

    [Fact]
    public async Task Source_only_restore_brings_the_text_back()
    {
        var fixture = await ArrangeAsync(VersionTwo, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        var result = await restore.ExecuteAsync(state, fixture.BackupPath,
            backupDestination: fixture.BackupDestination);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(RestoreTargetPolicy.SourceOnly, result.Policy);
        Assert.Equal(VersionOne, await File.ReadAllTextAsync(fixture.BookPath));
        // 恢复后的状态是新版本的：哈希、文本、识别树都跟着换。
        Assert.NotNull(result.State);
        Assert.Equal(SourceFileHasher.HashOf(fixture.BookPath), result.State!.RecognitionTree!.SourceSha256);
        Assert.Equal(VersionOne, result.State.Source.Render());
    }

    [Fact]
    public async Task A_restore_takes_a_backup_of_what_it_replaces()
    {
        // 恢复本身也要能撤销：被换掉的那一版必须先存下来。
        var fixture = await ArrangeAsync(VersionTwo, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        var result = await restore.ExecuteAsync(state, fixture.BackupPath,
            backupDestination: fixture.BackupDestination);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(File.Exists(fixture.BackupDestination));
        Assert.Equal(VersionTwo, await File.ReadAllTextAsync(fixture.BackupDestination));
    }

    [Fact]
    public async Task A_restore_leaves_a_journal_that_says_it_finished()
    {
        var fixture = await ArrangeAsync(VersionTwo, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        var result = await restore.ExecuteAsync(state, fixture.BackupPath,
            backupDestination: fixture.BackupDestination);

        Assert.True(result.Succeeded, result.Message);
        var journals = new RepairJournalStore(fixture.TransactionRoot);
        Assert.False(journals.HasPending());
        Assert.Equal(RepairJournalState.Committed, journals.Load(result.Transaction!.TransactionId!)!.State);
    }

    [Fact]
    public async Task Restoring_the_version_that_is_already_there_is_refused()
    {
        var fixture = await ArrangeAsync(VersionOne, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        var result = await restore.ExecuteAsync(state, fixture.BackupPath);

        Assert.False(result.Succeeded);
        Assert.Contains("已经是这个版本", result.Message);
        // 没有创建任何事务：一个字节都没变的替换不该留下记录。
        Assert.False(new RepairJournalStore(fixture.TransactionRoot).HasPending());
    }

    [Fact]
    public async Task A_missing_backup_is_refused()
    {
        var fixture = await ArrangeAsync(VersionTwo, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        var result = await restore.ExecuteAsync(state, Path.Combine(_workspace, "不存在.txt"));

        Assert.False(result.Succeeded);
        Assert.Contains("备份文件已经不在了", result.Message);
        Assert.Equal(VersionTwo, await File.ReadAllTextAsync(fixture.BookPath));
    }

    [Fact]
    public async Task A_book_changed_from_outside_is_refused()
    {
        // 状态里记的文本与磁盘上的不一致 —— 程序看到的是别的东西，这时候"恢复"会覆盖掉未经它手的改动。
        var fixture = await ArrangeAsync(VersionTwo, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        await File.WriteAllTextAsync(fixture.BookPath, VersionTwo + "外界加的一行\n", new UTF8Encoding(false));
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        var result = await restore.ExecuteAsync(state, fixture.BackupPath);

        Assert.False(result.Succeeded);
        Assert.Contains("本程序之外", result.Message);
        Assert.Contains("外界加的一行", await File.ReadAllTextAsync(fixture.BookPath));
    }

    [Fact]
    public async Task Full_book_state_is_refused_without_a_saved_tree()
    {
        // 关键的一条：拿"重新识别的树"顶替"保存的树"，会把这次恢复本来要救回来的东西丢掉，
        // 却报告成功。所以没有保存的树时必须拒绝，并指出去用 SourceOnly。
        var fixture = await ArrangeAsync(VersionTwo, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        var result = await restore.ExecuteAsync(state, fixture.BackupPath,
            RestoreTargetPolicy.FullBookState);

        Assert.False(result.Succeeded);
        Assert.Contains("没有保存章节树", result.Message);
        Assert.Contains("只恢复原文", result.Message);
        Assert.Equal(VersionTwo, await File.ReadAllTextAsync(fixture.BookPath));
    }

    [Fact]
    public async Task Full_book_state_is_refused_when_the_tree_belongs_to_another_version()
    {
        var fixture = await ArrangeAsync(VersionTwo, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        // 一棵属于别的版本的树：行号在另一个版本里含义不同，不能拿来用。
        var elsewhere = await ChapterTreeDocument.LoadAsync(fixture.BookPath);
        var wrongTree = new SourceFullStateSnapshot("不属于这份备份的哈希", elsewhere.LineCount,
            elsewhere.CreatePlan(elsewhere.Entries));

        var result = await restore.ExecuteAsync(state, fixture.BackupPath,
            RestoreTargetPolicy.FullBookState, savedTree: wrongTree);

        Assert.False(result.Succeeded);
        Assert.Contains("对不上", result.Message);
        Assert.Equal(VersionTwo, await File.ReadAllTextAsync(fixture.BookPath));
    }

    [Fact]
    public async Task Full_book_state_restores_the_saved_tree_and_not_a_fresh_recognition()
    {
        // 用户的编排：把"第二章"改成不进入目录。恢复完整书稿状态之后，这个决定必须还在 ——
        // 重新识别永远不会得出它。
        const string text = "第一章 起点\n正文一。\n第二章 中途\n正文二。\n";
        var fixture = await ArrangeAsync(VersionTwo, text);
        var savedDocument = await ChapterTreeDocument.LoadAsync(fixture.BackupPath);
        var arranged = savedDocument.Entries.Select(entry => entry.Title == "第二章 中途"
            ? entry with { IncludeInToc = false }
            : entry).ToArray();
        var savedTree = new SourceFullStateSnapshot(savedDocument.SourceSha256, savedDocument.LineCount,
            savedDocument.CreatePlan(arranged));

        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);
        var result = await restore.ExecuteAsync(state, fixture.BackupPath,
            RestoreTargetPolicy.FullBookState, savedTree: savedTree);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.State);
        var second = result.State!.Plan!.Entries.Single(entry => entry.Title == "第二章 中途");
        Assert.False(second.IncludeInToc);
        // 而且它真的是这次恢复带回来的：重新识别的结果里这一条是选中进目录的。
        var fresh = await ChapterTreeDocument.LoadAsync(fixture.BookPath);
        Assert.True(fresh.Entries.Single(entry => entry.Title == "第二章 中途").IncludeInToc);
    }

    [Fact]
    public async Task A_restored_book_can_be_recognised_and_read_again()
    {
        var fixture = await ArrangeAsync(VersionTwo, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        var result = await restore.ExecuteAsync(state, fixture.BackupPath,
            backupDestination: fixture.BackupDestination);
        Assert.True(result.Succeeded, result.Message);

        // 换过版本的文件必须能被正常读回来，而且章节树对得上它的新哈希。
        var document = await ChapterTreeDocument.LoadAsync(fixture.BookPath);
        Assert.Equal(2, document.Entries.Count(entry => !entry.IsFrontMatter));
        Assert.Equal(result.State!.RecognitionTree!.SourceSha256, document.SourceSha256);
        // 临时文件不留下。
        Assert.Empty(Directory.GetFiles(_workspace, "*.easypub-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_chapter_whose_heading_survives_keeps_its_identity_across_a_restore()
    {
        // 恢复回一个更短的版本：第一章的标题行没有被动过，所以它的身份要跟过来，而不是被重新发明。
        var fixture = await ArrangeAsync(VersionTwo, VersionOne);
        var state = await StateAsync(fixture.BookPath);
        var restore = new RestoreTransaction(fixture.TransactionRoot, fixture.BookPath);

        var result = await restore.ExecuteAsync(state, fixture.BackupPath,
            backupDestination: fixture.BackupDestination);

        Assert.True(result.Succeeded, result.Message);
        // 第三章只存在于被换掉的那一版里，它的身份必须消失，而且理由要是"标题行没了"。
        var lost = Assert.Single(result.LostIdentities);
        Assert.Equal(SourceRebindStatus.LostNoBody, lost.Status);
        Assert.Contains("第三章", lost.Title);
        // 前两章都还在。
        Assert.Contains(result.State!.Plan!.Entries, entry => entry.Title == "第一章 起点");
        Assert.Contains(result.State.Plan.Entries, entry => entry.Title == "第二章 中途");
    }
}
