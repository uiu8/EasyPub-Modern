using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 应用一次修复 —— 按钮和测试走的都是这一个入口。
///
/// 这一层存在的理由只有一条：让"按钮能做到代码能做的事"成立，而不是寄希望于它成立。在它之前，
/// 窗口只在内存里换一棵树，而事务、版本迁移、执行清单只有测试夹具够得着。
///
/// 两种模式**只在"用哪套产物"上不同**：TreeOnly 编译树补丁并且从不创建事务（没有文件改变，
/// 为它建一条记录等于记录"什么都没发生"）；EditSource 两样都编译并真的写文件。
/// </summary>
public class ChapterRepairApplierTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "easypub-applier-tests-" + Guid.NewGuid().ToString("N"));

    public ChapterRepairApplierTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private sealed record Subject(string BookPath, ChapterTreeDocument Document, RepairProposal Proposal,
        ReferenceCatalog Catalog, string OriginalSha);

    private async Task<Subject> LoadAsync(bool copySample = true)
    {
        var bookPath = Path.Combine(_workspace, "书稿.txt");
        if (copySample) File.Copy(Path.Combine(SampleRoot(), "缺陷样书.txt"), bookPath, true);
        else await File.WriteAllTextAsync(bookPath, SmallBook, new UTF8Encoding(false));

        var catalogPath = Path.Combine(SampleRoot(), "目录.txt");
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(catalogPath, Encoding.UTF8))!;
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: document);
        var version = RepairBaseVersionFactory.Capture(document.SourceSha256, document.Entries,
            document.RecognitionOptions, null);
        var proposal = RepairProposalFactory.Create("ReferenceCatalog", version, outcome.Plan!, catalog);
        return new Subject(bookPath, document, proposal, catalog, document.SourceSha256);
    }

    private const string SmallBook =
        "第一章 起点\n正文一。\n第二章 中途\n正文二。\n第三章 终点\n正文三。\n";

    private static IReadOnlyList<RepairDecision> Decisions(Subject subject, RepairLandingMode mode) =>
        subject.Proposal.DefaultDecisions(mode);

    private ChapterRepairApplier Applier() => new(Path.Combine(_workspace, "事务记录"),
        backupPathFor: (_, _, _) => Task.FromResult<string?>(Path.Combine(_workspace, "备份", "修复前.txt")));

    // ── 预览：窗口上显示的数字必须就是接下来会发生的事 ──

    [Fact]
    public async Task Tree_only_preview_says_the_source_is_not_touched()
    {
        var subject = await LoadAsync(copySample: false);
        var preview = Applier().Preview(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.TreeOnly), RepairLandingMode.TreeOnly);

        Assert.True(preview.CanApply, preview.Message);
        Assert.False(preview.SourceWillChange);
        Assert.Equal(0, preview.SourceOperations);
        Assert.Empty(preview.AffectedLines);
    }

    [Fact]
    public async Task Edit_source_preview_lists_the_lines_that_would_change()
    {
        var subject = await LoadAsync();
        var preview = Applier().Preview(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);

        Assert.True(preview.CanApply, preview.Message);
        Assert.True(preview.SourceWillChange);
        Assert.Equal(69, preview.SourceOperations);
        Assert.Equal(69, preview.AffectedLines.Count);
        // 行号必须落在真实的原文范围里。
        Assert.All(preview.AffectedLines, line => Assert.InRange(line, 1, 2502));
    }

    [Fact]
    public async Task Preview_refuses_a_plan_built_against_a_different_book()
    {
        var subject = await LoadAsync();
        // 换一份原文：方案描述的那本书已经不存在了。
        await File.WriteAllTextAsync(subject.BookPath, SmallBook, new UTF8Encoding(false));
        var changed = await ChapterTreeDocument.LoadAsync(subject.BookPath);

        var preview = Applier().Preview(subject.Proposal, changed,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);

        Assert.False(preview.CanApply);
        Assert.False(preview.Version.IsValid);
        Assert.Contains("过期", preview.Message);
    }

    // ── TreeOnly：不写文件 ──

    [Fact]
    public async Task Tree_only_changes_nothing_on_disk_and_creates_no_transaction()
    {
        var subject = await LoadAsync();
        var before = await File.ReadAllBytesAsync(subject.BookPath);

        var result = await Applier().ApplyAsync(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.TreeOnly), RepairLandingMode.TreeOnly);

        Assert.True(result.Changed, result.Message);
        Assert.Null(result.Transaction);
        Assert.Null(result.State);
        Assert.Equal(before, await File.ReadAllBytesAsync(subject.BookPath));
        Assert.False(Directory.Exists(Path.Combine(_workspace, "事务记录"))
            && new RepairJournalStore(Path.Combine(_workspace, "事务记录")).HasPending());
    }

    // ── EditSource：真的写文件 ──

    [Fact]
    public async Task The_appliers_own_snapshot_carries_the_tree_so_the_version_can_be_restored_whole()
    {
        // 不传 backupPathFor 时，应用器自己经备份层取快照，并且**把树一起交出去**。少了这一步，
        // 恢复层里的「恢复原文与当时的章节树」就永远选不了 —— 每一份快照都只有字节。
        var subject = await LoadAsync();
        var root = Path.Combine(_workspace, "自带备份");
        var applier = new ChapterRepairApplier(root);

        var result = await applier.ApplyAsync(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);
        Assert.True(result.Changed, result.Message);

        var store = new SourceBackupStore(root);
        var snapshot = Assert.Single(store.Inventory().ListEntries(subject.BookPath),
            entry => entry.Kind == SourceBackupKind.Snapshot);
        Assert.True(snapshot.HasSavedTree, "应用器保存的快照里没有章节树。");

        // 而且那份树确实是这次修复之前、用户编排过的那一棵。
        var saved = store.ReadSnapshotTree(subject.BookPath, snapshot.Sha256);
        Assert.NotNull(saved);
        Assert.Equal(subject.OriginalSha, saved!.SourceSha256);
        Assert.Equal(subject.Document.Entries.Count, saved.Tree.Entries.Count);
    }

    [Fact]
    public async Task Edit_source_rewrites_the_book_and_moves_the_tree()
    {
        var subject = await LoadAsync();
        var result = await Applier().ApplyAsync(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);

        Assert.True(result.Changed, result.Message);
        Assert.NotNull(result.Transaction);
        Assert.Equal(SourceEditOutcome.Committed, result.Transaction!.Outcome);
        Assert.NotNull(result.State);

        // 盘上的字节真的变了，而且新哈希与事务记录一致。
        var onDisk = SourceFileHasher.HashOf(subject.BookPath);
        Assert.NotEqual(subject.OriginalSha, onDisk);
        Assert.Equal(onDisk, result.State!.RecognitionTree!.SourceSha256);
        Assert.Equal(onDisk, result.State.RenderedHash);

        // 69 个替换之后，行数不变、被改的 69 行内容变了。
        var patched = SourceTextDocument.Load(subject.BookPath);
        Assert.Equal(subject.Document.LineCount, patched.Lines.Count + 1);
        Assert.NotEqual(subject.OriginalSha, onDisk);

        // 迁移后的树必须能通过正式校验。
        ChapterTreeDocument.ValidatePlan(result.State.Plan!, result.State.RecognitionTree.LineCount);
    }

    [Fact]
    public async Task Edit_source_takes_a_backup_before_touching_anything()
    {
        var subject = await LoadAsync();
        var result = await Applier().ApplyAsync(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);

        Assert.True(result.Changed, result.Message);
        var backup = Path.Combine(_workspace, "备份", "修复前.txt");
        Assert.True(File.Exists(backup));
        // 备份就是修复前的那一版，一个字节都不差。
        Assert.Equal(subject.OriginalSha, SourceFileHasher.HashOf(backup));
    }

    [Fact]
    public async Task Edit_source_leaves_a_committed_journal()
    {
        var subject = await LoadAsync();
        var result = await Applier().ApplyAsync(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);

        Assert.True(result.Changed, result.Message);
        var journals = new RepairJournalStore(Path.Combine(_workspace, "事务记录"));
        Assert.False(journals.HasPending());
        Assert.Equal(RepairJournalState.Committed,
            journals.Load(result.Transaction!.TransactionId!)!.State);
    }

    [Fact]
    public async Task Edit_source_backs_the_decision_up_with_an_execution_manifest()
    {
        // 崩溃之后要能接着做完，靠的就是这份在替换之前就落盘的清单。
        var subject = await LoadAsync();
        var result = await Applier().ApplyAsync(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);

        Assert.True(result.Changed, result.Message);
        var directory = Path.Combine(_workspace, "事务记录", result.Transaction!.TransactionId!);
        var manifest = RepairExecutionManifestStore.Read(directory);
        Assert.NotNull(manifest);
        Assert.Equal(RepairLandingMode.EditSource, manifest!.LandingMode);
        Assert.Equal(subject.Proposal.Id, manifest.ProposalId);
        Assert.NotNull(manifest.SourcePatch);
        Assert.Equal(69, manifest.SourcePatch!.Operations.Count);
        Assert.Equal(subject.OriginalSha, manifest.BaseVersion.BaseSourceSha256);
        // 决策的落地结果要如实记录：69 个替换来自 69 个 Retitle 动作。
        Assert.Equal(69, manifest.Decisions.Count(d => d.Outcome == SourceEffectOutcome.Applied));
    }

    [Fact]
    public async Task The_two_modes_do_not_disagree_about_what_an_action_means()
    {
        // 这是整次重构要消灭的那个缺陷：同一个动作在两种模式下语义必须相同，只有"用哪套产物"不同。
        var subject = await LoadAsync();
        var treeOnly = Applier().Preview(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.TreeOnly), RepairLandingMode.TreeOnly);
        var editSource = Applier().Preview(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);

        // 树变化数一样：模式不决定动作做什么。
        Assert.Equal(treeOnly.TreeChanges, editSource.TreeChanges);
        // 文本操作数不一样：EditSource 多出那 69 个替换。
        Assert.Equal(0, treeOnly.SourceOperations);
        Assert.Equal(69, editSource.SourceOperations);
    }

    [Fact]
    public async Task An_already_repaired_book_is_refused_rather_than_rewritten_identically()
    {
        var subject = await LoadAsync();
        var applier = Applier();
        var first = await applier.ApplyAsync(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);
        Assert.True(first.Changed, first.Message);

        // 再跑一次同样的方案：原文已经不是它描述的那一版了，必须拒绝而不是又写一遍。
        var again = await applier.ApplyAsync(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);

        Assert.False(again.Changed);
        // 措辞要对得上原因：文件是本程序改的，不是"被别的程序改过"。
        Assert.Contains("重新生成修复方案", again.Message);
        Assert.Contains("应用过", again.Message);
    }

    [Fact]
    public async Task A_small_book_survives_the_round_trip_unchanged_in_shape()
    {
        // 用一本小书做精确核对：行数、每一行的内容、以及最终树。
        var subject = await LoadAsync(copySample: false);
        var before = SourceTextDocument.Load(subject.BookPath);
        var result = await Applier().ApplyAsync(subject.Proposal, subject.Document,
            Decisions(subject, RepairLandingMode.EditSource), RepairLandingMode.EditSource);

        if (!result.Changed)
        {
            // 这本小书与目录对不上，方案可能没有任何文本操作 —— 那就必须明确拒绝，而不是假装成功。
            Assert.Contains("没有", result.Message);
            Assert.Equal(subject.OriginalSha, SourceFileHasher.HashOf(subject.BookPath));
            return;
        }

        var after = SourceTextDocument.Load(subject.BookPath);
        Assert.Equal(before.Lines.Count, after.Lines.Count);
        Assert.Equal(SourceFileHasher.HashOf(subject.BookPath), result.State!.RenderedHash);
    }

    [Fact]
    public async Task A_self_repair_writes_the_text_when_there_is_no_catalog()
    {
        // 无目录时的「改原文」：这是 Phase 7 要开的那条路。
        // 重复标题是不依赖外部目录就能判定的问题，所以自修复方案在 EditSource 下应该真的删掉多余的行。
        //
        // **0-A 之后夹具改成"恰好一份有正文、另一份是空章"**：新模型只承认这一种 keep-target 证据，
        // 也只有在它成立时才默认勾选动作。原来的夹具是「两份都带正文的整章重复」，它现在仍然生成动作，
        // 但默认不勾选 —— 因为"哪一份是正本"没有任何本地证据，位置先后不是证据。
        // 这条路径没有被削弱：软件只在**结构事实**支持时才替读者做删除决定。
        const string body = "这一段正文写得足够长，长到比较器愿意为它计算相似度，而不是因为太短就直接跳过。";
        var book = Path.Combine(_workspace, "自修复书稿.txt");
        await File.WriteAllTextAsync(book,
            $"第一章 起点\n{body}\n第一章 起点\n第二章 继续\n正文三\n", new UTF8Encoding(false));
        var document = await ChapterTreeDocument.LoadAsync(book);
        var outcome = ChapterAutoRepair.Prepare(document, catalog: null);

        Assert.False(outcome.CatalogFound);
        Assert.NotNull(outcome.Plan);
        // 重复标题的第二问："并从原文删除这一份"。它的策略是 ExplicitOptIn —— 默认只从成品里排除副本，
        // 动读者的文件必须被明确要求。这里就是窗口上那个开关所做的事。
        var chosen = outcome.Plan!.Actions.Where(outcome.Plan.DefaultSelection.Contains).ToArray();
        Assert.NotEmpty(chosen);
        var decisions = chosen.Select(action => new RepairDecision(
            RepairActionIdentity.Compute(action), Selected: true,
            SourceEffectPolicies.Decide(action.Kind, RepairLandingMode.EditSource,
                deleteDuplicateFromSource: true))).ToArray();

        var applier = new ChapterRepairApplier(Path.Combine(_workspace, "自修复事务"),
            backupPathFor: (_, _, _) => Task.FromResult<string?>(Path.Combine(_workspace, "自修复备份", "修复前.txt")));
        var proposal = RepairProposalFactory.Create(SelfRepairPlanner.BasisName,
            RepairBaseVersionFactory.Capture(document.SourceSha256, document.Entries,
                document.RecognitionOptions, null), outcome.Plan);
        var preview = applier.Preview(proposal, document, decisions, RepairLandingMode.EditSource);
        Assert.True(preview.CanApply, preview.Message);
        Assert.True(preview.SourceWillChange);
        // 重复的那一行（原文第 3 行）会被删掉。
        Assert.Contains(3, preview.AffectedLines);

        var result = await applier.ApplyAsync(proposal, document, decisions, RepairLandingMode.EditSource);
        Assert.True(result.Changed, result.Message);

        var after = SourceTextDocument.Load(book);
        // 夹具是 5 行，删掉第 3 行那个空章标题后剩 4 行（原夹具 6 行、剩 5 行）。
        Assert.Equal(4, after.Lines.Count);
        Assert.Equal(1, after.Lines.Count(line => line.Text == "第一章 起点"));
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
}
