using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 事务记录与执行清单的持久化。
///
/// 执行清单是让一次被中断的事务可恢复的那份文件：替换之后崩溃时，程序手里有补丁、有候选 id、有两个
/// 哈希，却**不知道章节树本该变成什么样**。补丁说的是哪几行变了，没有任何地方说本该存在哪几章。
///
/// 所以它必须能**完整**往返 —— 少一个字段，恢复时的那次比对就少一分依据。
/// </summary>
public class RepairJournalStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"easypub-journal-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string SampleRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "六卷缺陷样书");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 samples/六卷缺陷样书。");
    }

    // ── 事务记录 ────────────────────────────────────────────────

    [Fact]
    public void A_journal_round_trips()
    {
        var store = new RepairJournalStore(_root);
        var journal = new RepairJournal(RepairJournal.CurrentSchemaVersion, "tx-1", @"C:\书.txt",
            "AAAA", "BBBB", RepairJournalState.TempWritten, DateTimeOffset.Now, DateTimeOffset.Now);

        store.Save(journal);
        var loaded = store.Load("tx-1");

        Assert.NotNull(loaded);
        Assert.Equal("tx-1", loaded!.TransactionId);
        Assert.Equal("AAAA", loaded.BaseSha256);
        Assert.Equal("BBBB", loaded.ExpectedNewSha256);
        Assert.Equal(RepairJournalState.TempWritten, loaded.State);
    }

    [Fact]
    public void A_missing_journal_reads_as_absent()
    {
        Assert.Null(new RepairJournalStore(_root).Load("never-existed"));
    }

    [Fact]
    public void A_corrupt_journal_reads_as_absent_rather_than_throwing()
    {
        var store = new RepairJournalStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.PathFor("tx-bad"), "{ not json at all");

        Assert.Null(store.Load("tx-bad"));
        // 但它仍然"存在于磁盘上"，所以列举时不该被当成什么都没发生。
        Assert.Empty(store.LoadAll());
        Assert.False(store.HasPending());
    }

    /// <summary>
    /// 未完成的事务会挡住备份清理，所以"什么算未完成"必须是明确的。
    /// </summary>
    [Theory]
    [InlineData(RepairJournalState.Prepared, true)]
    [InlineData(RepairJournalState.BackupCreated, true)]
    [InlineData(RepairJournalState.TempWritten, true)]
    [InlineData(RepairJournalState.SourceReplaced, true)]
    [InlineData(RepairJournalState.DocumentReloaded, true)]
    [InlineData(RepairJournalState.StateMigrated, true)]
    [InlineData(RepairJournalState.Committed, false)]
    [InlineData(RepairJournalState.RolledBack, false)]
    [InlineData(RepairJournalState.Conflict, false)]
    public void Only_unfinished_states_count_as_pending(RepairJournalState state, bool pending)
    {
        var store = new RepairJournalStore(_root);
        store.Save(new RepairJournal(1, "tx", "书.txt", "A", "B", state, DateTimeOffset.Now, DateTimeOffset.Now));

        Assert.Equal(pending, store.HasPending());
    }

    [Fact]
    public void State_changes_stamp_the_update_time()
    {
        var created = DateTimeOffset.Now.AddMinutes(-5);
        var journal = new RepairJournal(1, "tx", "书.txt", "A", "B", RepairJournalState.Prepared,
            created, created);

        var moved = journal.WithState(RepairJournalState.TempWritten);

        Assert.Equal(RepairJournalState.TempWritten, moved.State);
        Assert.Equal(created, moved.CreatedAt);
        Assert.True(moved.UpdatedAt > created);
    }

    /// <summary>哪些状态已经宣称跨过替换边界 —— 恢复靠这个判断"记录领先于文件"是不是矛盾。</summary>
    [Theory]
    [InlineData(RepairJournalState.Prepared, false)]
    [InlineData(RepairJournalState.BackupCreated, false)]
    [InlineData(RepairJournalState.TempWritten, false)]
    [InlineData(RepairJournalState.SourceReplaced, true)]
    [InlineData(RepairJournalState.DocumentReloaded, true)]
    [InlineData(RepairJournalState.StateMigrated, true)]
    [InlineData(RepairJournalState.Committed, true)]
    public void Only_states_past_the_boundary_claim_a_replacement(RepairJournalState state, bool claimed)
    {
        var journal = new RepairJournal(1, "tx", "书.txt", "A", "B", state, DateTimeOffset.Now, DateTimeOffset.Now);
        Assert.Equal(claimed, journal.HasClaimedReplacement);
    }

    // ── 执行清单 ────────────────────────────────────────────────

    private static async Task<RepairExecutionManifest> ManifestFromSampleAsync()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var rawText = await File.ReadAllTextAsync(bookPath);
        var textDocument = SourceTextDocument.Parse(rawText);
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: document);
        var version = RepairBaseVersionFactory.Capture(document.SourceSha256, document.Entries,
            document.RecognitionOptions, null);
        var proposal = RepairProposalFactory.Create("ReferenceCatalog", version, outcome.Plan!, catalog);
        var decisions = proposal.Actions.Select(action => new RepairDecision(
            RepairActionIdentity.Compute(action),
            proposal.Plan.DefaultSelection.Contains(action),
            SourceEffectPolicies.Decide(action.Kind, RepairLandingMode.EditSource))).ToArray();

        var compiled = RepairPlanCompiler.Compile(new RepairCompileInput(proposal, document, decisions,
            RepairLandingMode.EditSource, BuildVolumeLevels: catalog.VolumeTitles.Count > 0));
        Assert.True(compiled.CanApply, compiled.Describe());

        var snapshots = proposal.Actions.Select(action =>
        {
            var id = RepairActionIdentity.Compute(action);
            var decision = decisions.First(d => d.ActionId == id);
            return new RepairDecisionSnapshot(id, action.Kind, action.Title, decision.Selected,
                decision.SourceEffect,
                decision.SourceEffect == SourceEffectDecision.None
                    ? SourceEffectOutcome.NotRequested : SourceEffectOutcome.Applied,
                action.Line > 0 ? action.Line : null,
                action.Kind == ReferenceActionKind.Retitle ? action.Title : null);
        }).ToArray();

        return new RepairExecutionManifest(
            RepairExecutionManifest.CurrentSchemaVersion, "tx-sample", proposal.Id, version,
            snapshots, compiled.TreePatch!, compiled.SourcePatch, compiled.ExpectedResult!,
            new string('A', 64), document.SourceSha256, RepairLandingMode.EditSource, null,
            textDocument.DefaultNewLine);
    }

    /// <summary>
    /// 真实样书的清单必须**完整**往返。少一个字段，恢复时那次比对就少一分依据 ——
    /// 而预期结果的正文哈希正是"建出来的树是不是当初商定那棵"这件事的唯一凭据。
    /// </summary>
    [Fact]
    public async Task The_sample_manifest_round_trips_completely()
    {
        var manifest = await ManifestFromSampleAsync();
        var before = manifest.ExpectedCanonicalResult;

        RepairExecutionManifestStore.Write(_root, manifest);
        var loaded = RepairExecutionManifestStore.Read(_root);

        Assert.NotNull(loaded);
        Assert.Equal(manifest.TransactionId, loaded!.TransactionId);
        Assert.Equal(manifest.ExpectedNewSha256, loaded.ExpectedNewSha256);
        Assert.Equal(manifest.BaseSnapshotSha256, loaded.BaseSnapshotSha256);
        Assert.Equal(manifest.LandingMode, loaded.LandingMode);
        Assert.Equal(manifest.DefaultNewLine, loaded.DefaultNewLine);
        Assert.Equal(manifest.BaseVersion, loaded.BaseVersion);
        Assert.Equal(manifest.Decisions.Count, loaded.Decisions.Count);
        Assert.Equal(manifest.TreePatch.Mutations.Count, loaded.TreePatch.Mutations.Count);
        Assert.Equal(manifest.SourcePatch!.Operations.Count, loaded.SourcePatch!.Operations.Count);

        // 预期结果必须逐字段相等 —— 这是恢复时用来证明"树建对了"的东西。
        Assert.True(loaded.ExpectedCanonicalResult.EqualsByContent(before),
            "清单往返之后预期结果变了：" + loaded.ExpectedCanonicalResult.DescribeFirstDifference(before));
        Assert.Equal(before.Chapters.Count, loaded.ExpectedCanonicalResult.Chapters.Count);
        Assert.Equal(
            before.Chapters.Sum(chapter => chapter.BodyContentHashes.Count),
            loaded.ExpectedCanonicalResult.Chapters.Sum(chapter => chapter.BodyContentHashes.Count));
    }

    /// <summary>文本操作的每一个字段都要还原，包括前置条件 —— 少了它，恢复就会盲目覆盖。</summary>
    [Fact]
    public async Task Source_operations_survive_the_round_trip_with_their_preconditions()
    {
        var manifest = await ManifestFromSampleAsync();
        RepairExecutionManifestStore.Write(_root, manifest);
        var loaded = RepairExecutionManifestStore.Read(_root)!;

        var original = manifest.SourcePatch!.Operations.OfType<ReplaceOriginalLine>().ToArray();
        var restored = loaded.SourcePatch!.Operations.OfType<ReplaceOriginalLine>().ToArray();

        Assert.Equal(original.Length, restored.Length);
        for (var index = 0; index < original.Length; index++)
        {
            Assert.Equal(original[index].Line, restored[index].Line);
            Assert.Equal(original[index].ExpectedOldText, restored[index].ExpectedOldText);
            Assert.Equal(original[index].NewText, restored[index].NewText);
            Assert.Equal(original[index].OperationId, restored[index].OperationId);
            Assert.Equal(original[index].ActionId, restored[index].ActionId);
        }
    }

    /// <summary>锚点是判别联合，往返之后必须还是原来那一支 —— 退化成一类会让插入落到别处。</summary>
    [Fact]
    public void Insert_anchors_keep_their_kind_across_the_round_trip()
    {
        var manifest = new RepairExecutionManifest(1, "tx", "p",
            new RepairBaseVersion("A", "T", "R"), [],
            new TreePatch("p", [], CanonicalChapterModel.Empty),
            new SourcePatch("p", "A",
            [
                new InsertLineAtAnchor(new BeforeOriginalLine(5, 2), "前") { OperationId = "a", ActionId = "x" },
                new InsertLineAtAnchor(new AfterOriginalLine(9), "后") { OperationId = "b", ActionId = "x" },
                new InsertLineAtAnchor(new EndOfFileAnchor(), "尾") { OperationId = "c", ActionId = "x" },
            ]),
            CanonicalChapterModel.Empty, "B", "A", RepairLandingMode.EditSource, null, "\n");

        RepairExecutionManifestStore.Write(_root, manifest);
        var loaded = RepairExecutionManifestStore.Read(_root)!;
        var inserts = loaded.SourcePatch!.Operations.OfType<InsertLineAtAnchor>().ToArray();

        Assert.Equal(3, inserts.Length);
        var before = Assert.IsType<BeforeOriginalLine>(inserts[0].Anchor);
        Assert.Equal(5, before.Line);
        Assert.Equal(2, before.Ordinal);
        Assert.IsType<AfterOriginalLine>(inserts[1].Anchor);
        Assert.IsType<EndOfFileAnchor>(inserts[2].Anchor);
    }

    /// <summary>没有源补丁时（TreeOnly）往返之后仍然是"没有"，不能变成空补丁。</summary>
    [Fact]
    public void A_tree_only_manifest_keeps_a_null_source_patch()
    {
        var manifest = new RepairExecutionManifest(1, "tx", "p", new RepairBaseVersion("A", "T", "R"), [],
            new TreePatch("p", [], CanonicalChapterModel.Empty), null, CanonicalChapterModel.Empty,
            "B", "A", RepairLandingMode.TreeOnly, null, null);

        RepairExecutionManifestStore.Write(_root, manifest);

        Assert.Null(RepairExecutionManifestStore.Read(_root)!.SourcePatch);
    }

    /// <summary>写完之后文件必须真的在那里，而且内容是完整的 JSON —— 这是替换源文件的前提。</summary>
    [Fact]
    public async Task The_manifest_is_on_disk_and_parses_before_anything_is_replaced()
    {
        var manifest = await ManifestFromSampleAsync();
        RepairExecutionManifestStore.Write(_root, manifest);

        var path = RepairExecutionManifestStore.PathFor(_root);
        Assert.True(File.Exists(path));
        var text = await File.ReadAllTextAsync(path);
        Assert.Contains("\"ExpectedNewSha256\"", text);
        Assert.Contains("\"ExpectedChapters\"", text);
        // 正文哈希必须真的在里面，而不只是段数。
        Assert.Contains("BodyContentHashes", text);
    }

    [Fact]
    public void Reading_a_manifest_that_is_not_there_gives_null()
    {
        Assert.Null(RepairExecutionManifestStore.Read(Path.Combine(_root, "nothing-here")));
    }
}
