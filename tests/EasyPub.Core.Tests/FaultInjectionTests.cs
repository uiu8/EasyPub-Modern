using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 事务在每一个阶段各崩一次，然后看恢复做了什么。
///
/// 六个步骤就是六个可以落地的崩溃点，而恢复规则在每一格都不同。要检查那张表，只能真的停在那些点上 ——
/// 在一笔真实的事务里、在一份真实的文件上 —— 而不是手工捏一条记录再问"接下来该怎样"。
///
/// 两件事在每个阶段都要成立：
///
///   1. **恢复之后，文件处于一个有人选过的状态**：要么是原文（替换还没发生），要么是新版本（已经发生）。
///      绝不能是"半新半旧"。
///   2. **恢复不会第二次替换原文。** 文件里已经是商定的那一版时再替换一次，是唯一能毁掉
///      "本程序之外有人改过它"的那个动作。
/// </summary>
public class FaultInjectionTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "easypub-fault-tests-" + Guid.NewGuid().ToString("N"));

    public FaultInjectionTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private const string OriginalText = "第一章 起点\n正文一。\n第二章 中途\n正文二。\n";
    private const string RepairedText = "第一章 起点\n正文一。\n第二章 改过的\n正文二。\n";

    /// <summary>The six points a crash can land on, in the order the transaction reaches them.</summary>
    public static TheoryData<RepairJournalState> CrashPoints =>
    [
        RepairJournalState.Prepared,
        RepairJournalState.BackupCreated,
        RepairJournalState.TempWritten,
        RepairJournalState.SourceReplaced,
        RepairJournalState.DocumentReloaded,
        RepairJournalState.StateMigrated,
    ];

    private sealed record Crash(string BookPath, string OriginalSha, string NewSha, string TransactionId,
        string TransactionRoot, string BackupPath, SourceEditResult Result);

    /// <summary>Runs a transaction that throws once it reaches <paramref name="crashAt"/>.</summary>
    private async Task<Crash> CrashAtAsync(RepairJournalState crashAt)
    {
        var bookPath = Path.Combine(_workspace, "书稿.txt");
        await File.WriteAllTextAsync(bookPath, OriginalText, new UTF8Encoding(false));
        var document = SourceTextDocument.Load(bookPath);
        var originalSha = SourceFileHasher.HashOf(bookPath);
        var newSha = SourceFileHasher.HashOfRendered(document, RepairedText);
        var transactionRoot = Path.Combine(_workspace, "事务记录");
        var backupPath = Path.Combine(_workspace, "备份", "原文.txt");

        var transaction = new SourceEditTransaction(transactionRoot, bookPath,
            afterReplace: (_, _) => Task.CompletedTask)
        {
            AfterStep = state =>
            {
                if (state == crashAt) throw new TransactionInterrupted(state.ToString());
            },
        };

        var manifest = Manifest(document, originalSha, newSha, backupPath);
        var result = await transaction.ExecuteAsync(manifest, document, RepairedText, backupPath);

        Assert.False(result.Succeeded);
        return new Crash(bookPath, originalSha, newSha, transaction.TransactionId, transactionRoot, backupPath,
            result);
    }

    private static RepairExecutionManifest Manifest(SourceTextDocument document, string baseSha, string newSha,
        string backupPath)
    {
        var entries = new ChapterTreeEntry("e1", "第一章 起点", 1, true, 1, [new ChapterSourceRange(2, 2)]);
        var version = RepairBaseVersionFactory.Capture(baseSha, [entries], new TocHierarchyOptions(), null);
        var patch = new SourcePatch("t", baseSha,
        [
            new ReplaceOriginalLine(3, "第二章 中途", "第二章 改过的") { OperationId = "rep-3", ActionId = "a" },
        ]);
        var expected = CanonicalChapterModelFactory.Build([entries],
            line => (line == 2 ? "正文一。" : null));
        return new RepairExecutionManifest(RepairExecutionManifest.CurrentSchemaVersion, "tx", "p", version, [],
            new TreePatch("t", [], expected), patch, expected, newSha, baseSha,
            RepairLandingMode.EditSource, backupPath, document.DefaultNewLine);
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task A_crash_leaves_the_source_as_one_version_or_the_other(RepairJournalState crashAt)
    {
        var crash = await CrashAtAsync(crashAt);
        var onDisk = SourceFileHasher.HashOf(crash.BookPath);

        Assert.True(onDisk == crash.OriginalSha || onDisk == crash.NewSha,
            $"崩在 {crashAt} 之后，文件既不是原文也不是新版本。");
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task A_crash_that_landed_before_the_replacement_leaves_the_original_untouched(
        RepairJournalState crashAt)
    {
        var crash = await CrashAtAsync(crashAt);
        if (crashAt is not (RepairJournalState.Prepared or RepairJournalState.BackupCreated
            or RepairJournalState.TempWritten)) return;

        Assert.Equal(crash.OriginalSha, SourceFileHasher.HashOf(crash.BookPath));
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(crash.BookPath));
        // 备份规则：从 BackupCreated 起，被替换之前就已经存下了原文。
        if (crashAt == RepairJournalState.BackupCreated)
            Assert.Equal(OriginalText, await File.ReadAllTextAsync(crash.BackupPath));
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task Recovery_brings_every_crash_point_to_a_finished_state(RepairJournalState crashAt)
    {
        var crash = await CrashAtAsync(crashAt);
        var beforeRecovery = SourceFileHasher.HashOf(crash.BookPath);

        var recovered = await SourceEditRecovery.RecoverAsync(crash.TransactionRoot, crash.TransactionId);

        var journals = new RepairJournalStore(crash.TransactionRoot);
        var final = journals.Load(crash.TransactionId)!.State;
        var afterRecovery = SourceFileHasher.HashOf(crash.BookPath);

        // 恢复之后不再有"未完成"的记录：要么作废、要么收尾，要么明确交给人工。
        Assert.False(journals.HasPending(),
            $"崩在 {crashAt}、恢复之后记录仍停在 {final}。恢复返回：{recovered.Outcome}｜{recovered.Message}"
            + $"｜判定 {recovered.Recovery?.Action}");

        if (crashAt is RepairJournalState.Prepared or RepairJournalState.BackupCreated)
        {
            // 替换还没开始，作废它不会留下任何改动。
            Assert.Equal(RepairJournalState.RolledBack, final);
            Assert.Equal(crash.OriginalSha, afterRecovery);
            Assert.Equal(SourceEditOutcome.RolledBack, recovered.Outcome);
        }
        else if (crashAt == RepairJournalState.TempWritten)
        {
            // 唯一允许自动继续的一格：记录说它从未越过分界线，文件也证实了这一点。
            // 渲染好的字节已经躺在源文件旁边，所以"继续"就是一次改名 —— 而改名之前先对着记录里的哈希核对。
            //
            // 这一格刻意**不依赖清单**：崩溃可以正好落在"写完临时文件"与"写清单"之间，
            // 那时要求清单就等于宣布这一格永远无法自动完成，与"它是唯一可以自动完成的一格"自相矛盾。
            Assert.Equal(RepairRecoveryAction.ContinueReplace, recovered.Recovery!.Action);
            Assert.Equal(SourceEditOutcome.Committed, recovered.Outcome);
            Assert.Equal(RepairJournalState.Committed, final);
            Assert.Equal(crash.NewSha, afterRecovery);
        }
        else
        {
            // 替换已经发生：恢复只补完剩下的簿记，绝不第二次替换。
            Assert.Equal(RepairJournalState.Committed, final);
            Assert.Equal(SourceEditOutcome.Committed, recovered.Outcome);
            Assert.Equal(crash.NewSha, afterRecovery);
            Assert.Equal(beforeRecovery, afterRecovery);
            Assert.False(Directory.Exists(Path.Combine(crash.TransactionRoot, crash.TransactionId)),
                "恢复收尾之后仍留着事务的工作目录。");
            Assert.False(File.Exists(crash.BookPath + "." + crash.TransactionId + ".easypub-tmp"),
                "恢复收尾之后仍留着 sibling 临时文件。");
        }
    }

    [Theory]
    [MemberData(nameof(CrashPoints))]
    public async Task Recovery_never_replaces_the_source_a_second_time(RepairJournalState crashAt)
    {
        var crash = await CrashAtAsync(crashAt);

        await SourceEditRecovery.RecoverAsync(crash.TransactionRoot, crash.TransactionId);
        var once = SourceFileHasher.HashOf(crash.BookPath);
        // 再恢复一次：一个已经收尾的事务不该再动任何东西。
        await SourceEditRecovery.RecoverAsync(crash.TransactionRoot, crash.TransactionId);
        var twice = SourceFileHasher.HashOf(crash.BookPath);

        Assert.Equal(once, twice);

        // 替换真的发生过的那几格（包括恢复自己补完的那一格），文件必须停在新版本上。
        // 文件里已经是商定的那一版时再替换一次，是恢复唯一能毁掉"本程序之外有人改过它"的动作。
        if (crashAt is RepairJournalState.TempWritten or RepairJournalState.SourceReplaced
            or RepairJournalState.DocumentReloaded or RepairJournalState.StateMigrated)
            Assert.Equal(crash.NewSha, twice);
        else
            Assert.Equal(crash.OriginalSha, twice);
    }

    [Fact]
    public async Task A_crash_after_the_replacement_rolls_the_bookkeeping_forward()
    {
        // SourceReplaced 之后崩溃：替换已成事实，但替换之后的回调（版本迁移）还没跑。
        // 恢复必须把它补上 —— 否则内存里的树会一直指着旧行号。
        var crash = await CrashAtAsync(RepairJournalState.SourceReplaced);
        var reran = false;

        var recovered = await SourceEditRecovery.RecoverAsync(crash.TransactionRoot, crash.TransactionId,
            afterReplace: _ =>
            {
                reran = true;
                return Task.CompletedTask;
            });

        Assert.True(reran, "崩在 SourceReplaced，恢复却没有补做替换之后的步骤。");
        Assert.Equal(SourceEditOutcome.Committed, recovered.Outcome);
        Assert.Equal(crash.NewSha, SourceFileHasher.HashOf(crash.BookPath));
    }

    [Fact]
    public async Task A_crash_after_the_migration_does_not_run_it_again()
    {
        // StateMigrated 之后崩溃：迁移已经跑过，重跑一遍是纯粹的浪费，也可能带来不必要的副作用。
        var crash = await CrashAtAsync(RepairJournalState.StateMigrated);
        var reran = false;

        var recovered = await SourceEditRecovery.RecoverAsync(crash.TransactionRoot, crash.TransactionId,
            afterReplace: _ =>
            {
                reran = true;
                return Task.CompletedTask;
            });

        Assert.False(reran, "崩在 StateMigrated，恢复却重跑了迁移。");
        Assert.Equal(SourceEditOutcome.Committed, recovered.Outcome);
    }

    [Fact]
    public async Task A_file_changed_from_outside_is_a_conflict_and_is_left_alone()
    {
        // 有人把旧版本放了回去（或者改了别的）：记录说替换过，文件却不是新版本。
        // 这时候再替换一次就会毁掉那个人刚做的事 —— 必须停下来。
        var crash = await CrashAtAsync(RepairJournalState.SourceReplaced);
        await File.WriteAllTextAsync(crash.BookPath, OriginalText + "别人加的一行\n", new UTF8Encoding(false));
        var tampered = SourceFileHasher.HashOf(crash.BookPath);

        var recovered = await SourceEditRecovery.RecoverAsync(crash.TransactionRoot, crash.TransactionId);

        Assert.Equal(RepairRecoveryAction.Conflict, recovered.Recovery!.Action);
        Assert.True(recovered.NeedsAttention);
        Assert.Equal(tampered, SourceFileHasher.HashOf(crash.BookPath));
        Assert.Equal(RepairJournalState.Conflict,
            new RepairJournalStore(crash.TransactionRoot).Load(crash.TransactionId)!.State);
    }

    [Fact]
    public async Task Recovery_of_a_transaction_that_does_not_exist_is_refused()
    {
        await CrashAtAsync(RepairJournalState.Prepared);
        var recovered = await SourceEditRecovery.RecoverAsync(Path.Combine(_workspace, "事务记录"), "没有这个事务");
        Assert.False(recovered.Succeeded);
        Assert.Contains("找不到", recovered.Message);
    }
}
