using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 事务恢复的决策表。
///
/// 两条规则决定了整张表：
///
///   1. **文件说了算，不是记录说了算。** 记录说"已经替换过"而文件还是旧的，这是一个矛盾而不是一条
///      指令 —— 最可能的成因是人把旧版本放了回去，再替换一次就会把他刚做的事悄悄毁掉。
///   2. **只有"尚未宣称跨过替换"这一种情况可以自动完成。** 其余"记录领先于文件"的组合一律进冲突。
///      那一个例外之所以安全，是因为 <see cref="RepairJournalState.TempWritten"/> 本身就是一句
///      "本次事务从未跨过边界"的话。
/// </summary>
public class RepairRecoveryPlannerTests
{
    private const string Base = "AAAA";
    private const string Expected = "BBBB";

    // ── 物理状态由磁盘决定 ──────────────────────────────────────

    [Fact]
    public void The_physical_state_comes_from_the_file()
    {
        Assert.Equal(PhysicalCommitState.Base, RepairRecoveryPlanner.Classify(Base, Base, Expected));
        Assert.Equal(PhysicalCommitState.Expected, RepairRecoveryPlanner.Classify(Expected, Base, Expected));
        Assert.Equal(PhysicalCommitState.Conflict, RepairRecoveryPlanner.Classify("CCCC", Base, Expected));
    }

    [Fact]
    public void Hash_comparison_ignores_case()
    {
        Assert.Equal(PhysicalCommitState.Base,
            RepairRecoveryPlanner.Classify(Base.ToLowerInvariant(), Base, Expected));
    }

    // ── 完整决策表 ──────────────────────────────────────────────

    /// <summary>
    /// 磁盘 = BaseSha（替换尚未发生）时的每一格。
    ///
    /// 注意每一格的结果：只有 <c>TempWritten</c> 可以继续替换，其余一律冲突。
    /// </summary>
    [Theory]
    [InlineData(RepairJournalState.Prepared, RepairRecoveryAction.Discard)]
    [InlineData(RepairJournalState.BackupCreated, RepairRecoveryAction.Discard)]
    [InlineData(RepairJournalState.TempWritten, RepairRecoveryAction.ContinueReplace)]
    [InlineData(RepairJournalState.SourceReplaced, RepairRecoveryAction.Conflict)]
    [InlineData(RepairJournalState.DocumentReloaded, RepairRecoveryAction.Conflict)]
    [InlineData(RepairJournalState.StateMigrated, RepairRecoveryAction.Conflict)]
    [InlineData(RepairJournalState.Committed, RepairRecoveryAction.Conflict)]
    [InlineData(RepairJournalState.RolledBack, RepairRecoveryAction.Discard)]
    public void With_the_file_unchanged(RepairJournalState journal, RepairRecoveryAction expected)
    {
        var decision = RepairRecoveryPlanner.Decide(journal, PhysicalCommitState.Base);
        Assert.Equal(expected, decision.Action);
    }

    /// <summary>
    /// 磁盘 = ExpectedNewSha（替换已经发生）。<c>TempWritten</c> 落在这里是合法的 ——
    /// 记录只是落后于文件事实。
    /// </summary>
    [Theory]
    [InlineData(RepairJournalState.Prepared, RepairRecoveryAction.Conflict)]
    [InlineData(RepairJournalState.BackupCreated, RepairRecoveryAction.Conflict)]
    [InlineData(RepairJournalState.TempWritten, RepairRecoveryAction.RollForward)]
    [InlineData(RepairJournalState.SourceReplaced, RepairRecoveryAction.RollForward)]
    [InlineData(RepairJournalState.DocumentReloaded, RepairRecoveryAction.RollForward)]
    [InlineData(RepairJournalState.StateMigrated, RepairRecoveryAction.Finish)]
    [InlineData(RepairJournalState.Committed, RepairRecoveryAction.CleanUp)]
    public void With_the_file_replaced(RepairJournalState journal, RepairRecoveryAction expected)
    {
        var decision = RepairRecoveryPlanner.Decide(journal, PhysicalCommitState.Expected);
        Assert.Equal(expected, decision.Action);
    }

    /// <summary>磁盘既不是 Base 也不是 Expected：无论记录说什么都停手。</summary>
    [Theory]
    [InlineData(RepairJournalState.Prepared)]
    [InlineData(RepairJournalState.TempWritten)]
    [InlineData(RepairJournalState.SourceReplaced)]
    [InlineData(RepairJournalState.DocumentReloaded)]
    [InlineData(RepairJournalState.StateMigrated)]
    [InlineData(RepairJournalState.Committed)]
    public void With_something_else_in_the_file(RepairJournalState journal)
    {
        var decision = RepairRecoveryPlanner.Decide(journal, PhysicalCommitState.Conflict);
        Assert.Equal(RepairRecoveryAction.Conflict, decision.Action);
        Assert.True(decision.NeedsAttention);
    }

    /// <summary>
    /// 整张表里**只有一格**允许自动继续替换。多一格就意味着某处的自动行为会覆盖用户的动作。
    /// </summary>
    [Fact]
    public void Exactly_one_cell_allows_finishing_the_replacement_automatically()
    {
        var allowed = new List<(RepairJournalState Journal, PhysicalCommitState Physical)>();
        foreach (var journal in Enum.GetValues<RepairJournalState>())
        foreach (var physical in Enum.GetValues<PhysicalCommitState>())
            if (RepairRecoveryPlanner.Decide(journal, physical).Action == RepairRecoveryAction.ContinueReplace)
                allowed.Add((journal, physical));

        var only = Assert.Single(allowed);
        Assert.Equal(RepairJournalState.TempWritten, only.Journal);
        Assert.Equal(PhysicalCommitState.Base, only.Physical);
    }

    /// <summary>
    /// 每一格都要给出理由 —— 进冲突时人要看得懂发生了什么，否则"请人工核对"等于什么都没说。
    /// </summary>
    [Fact]
    public void Every_decision_explains_itself()
    {
        foreach (var journal in Enum.GetValues<RepairJournalState>())
        foreach (var physical in Enum.GetValues<PhysicalCommitState>())
        {
            var decision = RepairRecoveryPlanner.Decide(journal, physical);
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason), $"{journal} + {physical} 没有理由");
            Assert.True(decision.Reason.Length > 8, $"{journal} + {physical} 的理由太短：{decision.Reason}");
        }
    }

    /// <summary>
    /// 「记录说已替换、文件却是旧的」这条必须明确提到"不自动重做" ——
    /// 这正是最容易被写成自动重试、从而覆盖用户恢复动作的那一格。
    /// </summary>
    [Fact]
    public void The_dangerous_cell_says_why_it_will_not_retry()
    {
        var decision = RepairRecoveryPlanner.Decide(RepairJournalState.SourceReplaced, PhysicalCommitState.Base);

        Assert.Equal(RepairRecoveryAction.Conflict, decision.Action);
        Assert.Contains("不自动重做", decision.Reason);
        Assert.Contains("请人工核对", decision.Reason);
    }

    // ── 写权限前置条件 ──────────────────────────────────────────

    /// <summary>四项能力**全部**具备才允许改原文，缺一不可。</summary>
    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void A_missing_capability_blocks_editing_the_source(bool replace, bool temp, bool journal,
        bool backup)
    {
        var preflight = new SourceEditPreflight(replace, temp, journal, backup);
        Assert.False(preflight.CanEditSource);
        Assert.NotEmpty(preflight.Missing());
    }

    [Fact]
    public void All_four_capabilities_allow_editing_the_source()
    {
        var preflight = new SourceEditPreflight(true, true, true, true);

        Assert.True(preflight.CanEditSource);
        Assert.Empty(preflight.Missing());
    }

    /// <summary>说的是缺哪一项，不是笼统的"不能改原文" —— 否则用户不知道该修什么。</summary>
    [Fact]
    public void The_description_names_the_missing_capabilities()
    {
        var preflight = new SourceEditPreflight(true, false, false, true);
        var described = preflight.Describe();

        Assert.Contains("同目录建临时文件", described);
        Assert.Contains("写事务记录", described);
        Assert.DoesNotContain("写备份", described);
    }

    /// <summary>
    /// 只读的源目录必须被拒 —— 这正是旧检查漏掉的情形：它问的是"源目录和备份目录**都**不可写吗"，
    /// 而源目录只读时备份目录通常可写，于是那个问题答"否"，却根本无法替换源文件。
    /// </summary>
    [Fact]
    public void A_read_only_source_directory_is_refused_even_when_backups_are_writable()
    {
        var preflight = new SourceEditPreflight(
            CanReplaceSourceFile: true, CanCreateSiblingTemp: false,
            CanWriteJournal: true, CanWriteBackupTarget: true);

        Assert.False(preflight.CanEditSource);
    }

    /// <summary>在真实文件系统上探测：临时目录应当四项都通过。</summary>
    [Fact]
    public async Task Probing_a_writable_location_passes_every_check()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"easypub-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var source = Path.Combine(workspace, "书.txt");
            await File.WriteAllTextAsync(source, "第一章\n正文\n");

            var preflight = SourceEditPreflightInspector.Inspect(source,
                Path.Combine(workspace, "journal"), Path.Combine(workspace, "backup"));

            Assert.True(preflight.CanEditSource, preflight.Describe());
            // 探测过程不得留下任何东西。
            Assert.Empty(Directory.GetFiles(workspace, "*.preflight*", SearchOption.AllDirectories));
        }
        finally { try { Directory.Delete(workspace, true); } catch { } }
    }

    [Fact]
    public void Probing_a_missing_file_fails_every_check()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"easypub-missing-{Guid.NewGuid():N}.txt");
        var preflight = SourceEditPreflightInspector.Inspect(missing,
            Path.GetTempPath(), Path.GetTempPath());

        Assert.False(preflight.CanEditSource);
        Assert.Equal(4, preflight.Missing().Count);
    }
}
