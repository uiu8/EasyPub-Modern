using System.IO;
using System.Security.Cryptography;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 事务：真的把原文换成修复后的版本，而且随时可以被中断。
///
/// 顺序是固定的，每一步都有理由：
///   1. 先确认文件还是计划所依据的那一版 —— 不是就说明别人改过它，计划描述的书已经不存在
///   2. 先备份。第一次写入之后再做的备份不算备份
///   3. 渲染到**源文件旁边**的临时文件 —— 替换必须是一次卷内改名，跨卷的复制可能只做一半
///   4. 清单先落盘并 flush —— 文件一改，崩溃留下的工作就只能靠清单完成
///   5. 原子替换
///   6. 之后才记进度。journal 允许落后，清单不允许缺失
/// </summary>
public class SourceEditTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"easypub-tx-{Guid.NewGuid():N}");

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

    private sealed record Prepared(
        string BookPath, ChapterTreeDocument Document, RepairExecutionManifest Manifest,
        SourceTextDocument TextDocument, string RenderedText, string BaseSha, string NewSha);

    /// <summary>备好一次真事务所需的一切，全部在临时目录里，样书本体不动。</summary>
    private async Task<Prepared> PrepareAsync()
    {
        var sample = Path.Combine(SampleRoot(), "缺陷样书.txt");
        Directory.CreateDirectory(_root);
        var book = Path.Combine(_root, "缺陷样书.txt");
        File.Copy(sample, book);

        var rawText = await File.ReadAllTextAsync(book);
        var textDocument = SourceTextDocument.Parse(rawText);
        var document = await ChapterTreeDocument.LoadAsync(book);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(SampleRoot(), "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(book, currentDocument: document, reference: catalog);
        var version = RepairBaseVersionFactory.Capture(document.SourceSha256, document.Entries,
            document.RecognitionOptions, null);
        var proposal = RepairProposalFactory.Create("ReferenceCatalog", version, outcome.Plan!, catalog);
        var decisions = proposal.Actions.Select(a => new RepairDecision(RepairActionIdentity.Compute(a),
            proposal.Plan.DefaultSelection.Contains(a),
            SourceEffectPolicies.Decide(a.Kind, RepairLandingMode.EditSource))).ToArray();
        var compiled = RepairPlanCompiler.Compile(new RepairCompileInput(proposal, document, decisions,
            RepairLandingMode.EditSource, BuildVolumeLevels: catalog.VolumeTitles.Count > 0));
        Assert.True(compiled.CanApply, compiled.Describe());

        var rendered = SourcePatchRenderer.Render(textDocument, compiled.SourcePatch!);
        var newSha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rendered.Text)));

        var snapshots = proposal.Actions.Select(a =>
        {
            var id = RepairActionIdentity.Compute(a);
            var decision = decisions.First(d => d.ActionId == id);
            return new RepairDecisionSnapshot(id, a.Kind, a.Title, decision.Selected, decision.SourceEffect,
                decision.SourceEffect == SourceEffectDecision.None
                    ? SourceEffectOutcome.NotRequested : SourceEffectOutcome.Applied,
                a.Line > 0 ? a.Line : null, null);
        }).ToArray();

        var manifest = new RepairExecutionManifest(RepairExecutionManifest.CurrentSchemaVersion, "tx",
            proposal.Id, version, snapshots, compiled.TreePatch!, compiled.SourcePatch,
            compiled.ExpectedResult!, newSha, document.SourceSha256, RepairLandingMode.EditSource, null,
            textDocument.DefaultNewLine);

        return new Prepared(book, document, manifest, textDocument, rendered.Text, document.SourceSha256, newSha);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    // ── 顺利完成 ────────────────────────────────────────────────

    /// <summary>
    /// 真实样书走完一次事务：源文件被换成修复后的版本，备份留着，临时文件清掉，记录标记为完成。
    /// </summary>
    [Fact]
    public async Task A_real_replace_completes_on_the_sample_book()
    {
        var prepared = await PrepareAsync();
        var backup = Path.Combine(_root, "backup", "原始.bak");
        var transaction = new SourceEditTransaction(Path.Combine(_root, "journal"), prepared.BookPath);

        var result = await transaction.ExecuteAsync(prepared.Manifest, prepared.TextDocument,
            prepared.RenderedText, backup);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(SourceEditOutcome.Committed, result.Outcome);

        // 源文件已经是新版本。
        Assert.Equal(prepared.NewSha, Sha(prepared.BookPath));
        // 备份还在，而且就是旧版本。
        Assert.True(File.Exists(backup));
        Assert.Equal(prepared.BaseSha, Sha(backup));
        // 临时文件清掉了。
        Assert.False(File.Exists(transaction.SiblingTempPath));
        // 记录标记为完成。
        var journal = new RepairJournalStore(Path.Combine(_root, "journal")).Load(transaction.TransactionId)!;
        Assert.Equal(RepairJournalState.Committed, journal.State);
        Assert.False(new RepairJournalStore(Path.Combine(_root, "journal")).HasPending());
    }

    /// <summary>替换之后原文必须还能被正常读成一棵章节树 —— 否则等于把书改坏了。</summary>
    [Fact]
    public async Task The_replaced_file_still_loads_as_a_chapter_tree()
    {
        var prepared = await PrepareAsync();
        var transaction = new SourceEditTransaction(Path.Combine(_root, "journal"), prepared.BookPath);
        var result = await transaction.ExecuteAsync(prepared.Manifest, prepared.TextDocument,
            prepared.RenderedText, Path.Combine(_root, "backup.bak"));
        Assert.True(result.Succeeded, result.Message);

        var reloaded = await ChapterTreeDocument.LoadAsync(prepared.BookPath);

        Assert.Equal(prepared.NewSha, reloaded.SourceSha256);
        Assert.NotEmpty(reloaded.Entries);

        // 用现成的验证器，而不是在这里手写一套行数算术。
        //
        // 这里刻意**不**断言"每一行都被某个章节认领"。第六卷每章是两行相同的标题，识别阶段把紧邻
        // 重复的那一行从条目里剔除了（那正是"同名重复标题 0 组"的来由），所以那 100 行不属于任何
        // 章节正文 —— 而这是对的：它们不会出现在成品里。2503 行里被认领 2403 行，差的正是那 100 行。
        //
        // 旧的行守恒检查看不出这件事，因为它管的是"行有没有凭空消失"，不是"行有没有出现在不该出现
        // 的地方"。等价性检查（LandingModeEquivalenceTests）才是盯后者的那一层。
        RepairIntegrity.Verify(reloaded.Entries, reloaded.Entries, []);
        Assert.True(RepairIntegrity.Coverage(reloaded.Entries).Count <= reloaded.LineCount);
    }

    /// <summary>替换掉的字节就是清单里记的那些 —— 事务不得写出清单之外的任何东西。</summary>
    [Fact]
    public async Task What_lands_on_disk_is_the_rendered_text()
    {
        var prepared = await PrepareAsync();
        var transaction = new SourceEditTransaction(Path.Combine(_root, "journal"), prepared.BookPath);
        var result = await transaction.ExecuteAsync(prepared.Manifest, prepared.TextDocument,
            prepared.RenderedText, Path.Combine(_root, "backup.bak"));
        Assert.True(result.Succeeded, result.Message);

        Assert.Equal(prepared.RenderedText, await File.ReadAllTextAsync(prepared.BookPath));
        Assert.Equal(prepared.NewSha, result.NewSourceSha256);
    }

    // ── 拒绝：什么都还没动 ──────────────────────────────────────

    /// <summary>
    /// 文件在计划之后被人改过 → 拒绝，且**一个字节都不动**。
    /// 这是整条链路上最要紧的一道门。
    /// </summary>
    [Fact]
    public async Task A_file_changed_since_the_plan_is_refused_and_left_alone()
    {
        var prepared = await PrepareAsync();
        await File.AppendAllTextAsync(prepared.BookPath, "\n别人加的一行\n");
        var changed = Sha(prepared.BookPath);
        var transaction = new SourceEditTransaction(Path.Combine(_root, "journal"), prepared.BookPath);

        var result = await transaction.ExecuteAsync(prepared.Manifest, prepared.TextDocument,
            prepared.RenderedText, Path.Combine(_root, "backup.bak"));

        Assert.Equal(SourceEditOutcome.Refused, result.Outcome);
        Assert.Contains("已被本程序之外的东西改过", result.Message);
        Assert.Equal(changed, Sha(prepared.BookPath));
        // 连事务目录都不该建。
        Assert.False(Directory.Exists(transaction.TransactionDirectory));
    }

    /// <summary>
    /// no-op 补丁必须被拒。
    ///
    /// 原因不是"没必要"，而是**恢复会失去判别力**：两个哈希相同时，"还没替换"和"替换了但字节没变"
    /// 在磁盘上完全一样，恢复无法分辨。
    /// </summary>
    [Fact]
    public async Task A_repair_that_changes_no_bytes_is_refused()
    {
        var prepared = await PrepareAsync();
        var transaction = new SourceEditTransaction(Path.Combine(_root, "journal"), prepared.BookPath);

        // 渲染结果与原文相同 —— 于是 ExpectedNewSha == BaseSha。
        var result = await transaction.ExecuteAsync(prepared.Manifest, prepared.TextDocument,
            prepared.TextDocument.Render(), Path.Combine(_root, "backup.bak"));

        Assert.Equal(SourceEditOutcome.Refused, result.Outcome);
        Assert.Contains("不会改变原文的任何字节", result.Message);
        Assert.Equal(prepared.BaseSha, Sha(prepared.BookPath));
    }

    /// <summary>渲染出来的字节与清单记的预期版本不一致时必须放弃 —— 说明中间有人动过手。</summary>
    [Fact]
    public async Task Rendered_bytes_that_do_not_match_the_manifest_are_abandoned()
    {
        var prepared = await PrepareAsync();
        var transaction = new SourceEditTransaction(Path.Combine(_root, "journal"), prepared.BookPath);

        var result = await transaction.ExecuteAsync(prepared.Manifest, prepared.TextDocument,
            prepared.RenderedText + "\n多出来的一行\n", Path.Combine(_root, "backup.bak"));

        Assert.Equal(SourceEditOutcome.RolledBack, result.Outcome);
        Assert.Contains("不一致", result.Message);
        Assert.Equal(prepared.BaseSha, Sha(prepared.BookPath));
        Assert.False(File.Exists(transaction.SiblingTempPath));
    }

    [Fact]
    public async Task A_missing_source_is_refused()
    {
        var prepared = await PrepareAsync();
        File.Delete(prepared.BookPath);
        var transaction = new SourceEditTransaction(Path.Combine(_root, "journal"), prepared.BookPath);

        var result = await transaction.ExecuteAsync(prepared.Manifest, prepared.TextDocument,
            prepared.RenderedText, Path.Combine(_root, "backup.bak"));

        Assert.Equal(SourceEditOutcome.Refused, result.Outcome);
    }

    // ── 恢复 ────────────────────────────────────────────────────

    /// <summary>
    /// 替换成功但后续失败 → 记录停在替换之后；从记录恢复时应当被判为"继续完成"而不是重做替换。
    /// 这是"不重复应用补丁"那条规则在真实记录上的样子。
    /// </summary>
    [Fact]
    public async Task Recovery_after_a_completed_replace_never_replaces_again()
    {
        var prepared = await PrepareAsync();
        var journalRoot = Path.Combine(_root, "journal");
        var transaction = new SourceEditTransaction(journalRoot, prepared.BookPath, afterReplace: (_, _) =>
            throw new IOException("模拟替换之后的步骤失败"));

        var result = await transaction.ExecuteAsync(prepared.Manifest, prepared.TextDocument,
            prepared.RenderedText, Path.Combine(_root, "backup.bak"));

        // 文件已经换成新版本了，所以结果必须如实说明这一点，而不是声称"什么都没变"。
        Assert.Equal(SourceEditOutcome.Failed, result.Outcome);
        Assert.Contains("已经改为新版本", result.Message);
        Assert.Equal(prepared.NewSha, Sha(prepared.BookPath));

        // 从记录恢复：文件已是新版本，记录停在 SourceReplaced → 继续完成，绝不再次替换。
        var recovered = await SourceEditRecovery.RecoverAsync(journalRoot, transaction.TransactionId);
        Assert.Equal(RepairRecoveryAction.RollForward, recovered.Recovery!.Action);
        Assert.Equal(prepared.NewSha, Sha(prepared.BookPath));
    }

    /// <summary>
    /// 最危险的一格：记录说已替换，文件却是旧的 —— 恢复必须停手，绝不自动重做。
    /// 探针与单元测试都盯这一格。
    /// </summary>
    [Fact]
    public async Task Recovery_refuses_when_the_journal_claims_a_replace_the_file_does_not_show()
    {
        var prepared = await PrepareAsync();
        var journalRoot = Path.Combine(_root, "journal");
        var transaction = new SourceEditTransaction(journalRoot, prepared.BookPath);

        // 手工造出"记录说已替换、文件还是旧的"。
        new RepairJournalStore(journalRoot).Save(new RepairJournal(RepairJournal.CurrentSchemaVersion,
            transaction.TransactionId, prepared.BookPath, prepared.BaseSha, prepared.NewSha,
            RepairJournalState.SourceReplaced, DateTimeOffset.Now, DateTimeOffset.Now));

        var recovered = await SourceEditRecovery.RecoverAsync(journalRoot, transaction.TransactionId);

        Assert.Equal(RepairRecoveryAction.Conflict, recovered.Recovery!.Action);
        Assert.Equal(prepared.BaseSha, Sha(prepared.BookPath));
        Assert.Contains("不自动重做", recovered.Recovery.Reason);
    }

    [Fact]
    public async Task Recovery_of_an_unstarted_transaction_discards_it_without_touching_the_book()
    {
        var prepared = await PrepareAsync();
        var journalRoot = Path.Combine(_root, "journal");
        new RepairJournalStore(journalRoot).Save(new RepairJournal(RepairJournal.CurrentSchemaVersion,
            "tx-never-started", prepared.BookPath, prepared.BaseSha, prepared.NewSha,
            RepairJournalState.Prepared, DateTimeOffset.Now, DateTimeOffset.Now));

        var recovered = await SourceEditRecovery.RecoverAsync(journalRoot, "tx-never-started");

        Assert.Equal(SourceEditOutcome.RolledBack, recovered.Outcome);
        Assert.Equal(prepared.BaseSha, Sha(prepared.BookPath));
    }

    /// <summary>未完成的事务会被列出来 —— 界面靠它报告"还有没做完的事"。</summary>
    [Fact]
    public async Task Unfinished_transactions_are_listed()
    {
        var prepared = await PrepareAsync();
        var journalRoot = Path.Combine(_root, "journal");
        var store = new RepairJournalStore(journalRoot);
        store.Save(new RepairJournal(1, "a", prepared.BookPath, prepared.BaseSha, prepared.NewSha,
            RepairJournalState.TempWritten, DateTimeOffset.Now, DateTimeOffset.Now));
        store.Save(new RepairJournal(1, "b", prepared.BookPath, prepared.BaseSha, prepared.NewSha,
            RepairJournalState.Committed, DateTimeOffset.Now, DateTimeOffset.Now));

        var pending = SourceEditRecovery.Pending(journalRoot);

        Assert.Single(pending);
        Assert.Equal("a", pending[0].TransactionId);
        await Task.CompletedTask;
    }
}
