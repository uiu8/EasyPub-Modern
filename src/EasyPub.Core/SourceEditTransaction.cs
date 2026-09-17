using System.Text;

namespace EasyPub.Core;

/// <summary>
/// Raised by <see cref="SourceEditTransaction.AfterStep"/> to stop a transaction at a known point.
///
/// <para>A distinct type rather than an <see cref="IOException"/> so the catching code can tell "the test
/// interrupted this" from "the disk refused": both must leave the same journal behind, but only the second
/// one means something is wrong with the machine.</para>
/// </summary>
public sealed class TransactionInterrupted(string state)
    : Exception($"测试在「{state}」这一步之后中断了事务。")
{
    public string State { get; } = state;
}

/// <summary>How a transaction ended.</summary>
public enum SourceEditOutcome{
    /// <summary>The source was replaced and everything after it completed.</summary>
    Committed,
    /// <summary>Nothing was replaced; the book is exactly as it was.</summary>
    RolledBack,
    /// <summary>It refused before touching anything — a precondition or a version check failed.</summary>
    Refused,
    /// <summary>Something went wrong after it started; the journal says how far it got.</summary>
    Failed,
}

/// <summary>What happened, in terms a person can act on.</summary>
public sealed record SourceEditResult(
    SourceEditOutcome Outcome,
    string Message,
    string? TransactionId = null,
    string? BackupPath = null,
    string? NewSourceSha256 = null,
    RepairRecoveryDecision? Recovery = null)
{
    public bool Succeeded => Outcome == SourceEditOutcome.Committed;

    /// <summary>True when the caller has to look at it — the file may be in a state nobody chose.</summary>
    public bool NeedsAttention => Outcome is SourceEditOutcome.Failed
        || Recovery is { NeedsAttention: true };
}

/// <summary>
/// Replaces a source file with the repaired text, in a way that can be interrupted at any point.
///
/// <para>The order is fixed and each step is there for a reason:</para>
///
/// <list type="number">
/// <item>Check that the file is still the version the plan was built against. If it is not, someone else
/// changed it and the plan describes a book that no longer exists.</item>
/// <item>Take the backup. A backup taken after the first write is not a backup.</item>
/// <item>Render to a temporary file <b>next to the source</b>. Next to it, because the replacement has to
/// be a rename within one volume — a copy across volumes can fail halfway and leave a half-written
/// book.</item>
/// <item>Write the execution manifest and flush it. This must be on disk <b>before</b> the source is
/// replaced, because the moment the file changes a crash leaves work that can only be finished from that
/// manifest.</item>
/// <item>Replace, atomically.</item>
/// <item>Only then record progress in the journal. The journal is allowed to be behind; the manifest is
/// not allowed to be missing.</item>
/// </list>
///
/// <para>Nothing here decides <i>what</i> to write. It is handed a manifest that was frozen earlier and
/// carries it out.</para>
/// </summary>
public sealed class SourceEditTransaction
{
    private readonly RepairJournalStore _journals;
    private readonly string _transactionId;
    private readonly string _sourcePath;
    private readonly string _transactionDirectory;
    private readonly string _siblingTempPath;
    private readonly Func<string, CancellationToken, Task>? _afterReplace;
    private readonly TimeProvider _clock;

    public SourceEditTransaction(string transactionRoot, string sourcePath,
        string? transactionId = null, Func<string, CancellationToken, Task>? afterReplace = null,
        TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _transactionId = transactionId ?? _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss") + "-"
            + Guid.NewGuid().ToString("N")[..8];
        _sourcePath = Path.GetFullPath(sourcePath);
        _journals = new RepairJournalStore(transactionRoot);
        _transactionDirectory = Path.Combine(transactionRoot, _transactionId);
        // Beside the source, not in the journal directory: the replacement is a rename and a rename does
        // not cross volumes.
        _siblingTempPath = _sourcePath + "." + _transactionId + ".easypub-tmp";
        _afterReplace = afterReplace;
    }

    public string TransactionId => _transactionId;
    public string TransactionDirectory => _transactionDirectory;
    public string SiblingTempPath => _siblingTempPath;

    /// <summary>
    /// Called after every step, for tests that need to interrupt the sequence at a known point.
    ///
    /// <para>Six steps means six places a crash can land, and the recovery rules differ at each of them. The
    /// only honest way to check that table is to actually stop there — in the middle of a real transaction,
    /// on a real file — rather than to hand-build a journal and ask what should happen next. Throwing from
    /// this hook is treated exactly like a step that failed.</para>
    ///
    /// <para>Null in production: nothing in the app sets it.</para>
    /// </summary>
    public Action<RepairJournalState>? AfterStep { get; init; }

    public async Task<SourceEditResult> ExecuteAsync(RepairExecutionManifest manifest,
        SourceTextDocument document, string renderedText, string? backupPath,
        CancellationToken token = default)
    {
        // 1. The file must still be what the plan was built against.
        if (!File.Exists(_sourcePath))
            return Refused("原文不存在，未做任何改动。");
        var current = await SourceFileHasher.HashOfAsync(_sourcePath, token).ConfigureAwait(false);
        if (!string.Equals(current, manifest.BaseVersion.BaseSourceSha256, StringComparison.OrdinalIgnoreCase))
            return Refused("原文已被本程序之外的东西改过，未做任何改动。请重新打开书稿并重新生成修复方案。");

        // The replacement would be a no-op. A journal for it would make recovery unable to tell the two
        // states apart, because both hashes would be the same value.
        var newSha = SourceFileHasher.HashOfRendered(document, renderedText);
        if (string.Equals(newSha, manifest.BaseVersion.BaseSourceSha256, StringComparison.OrdinalIgnoreCase))
            return Refused("这次修改不会改变原文的任何字节，因此没有创建事务。");

        Directory.CreateDirectory(_transactionDirectory);
        var startedAt = _clock.GetLocalNow();
        var journal = new RepairJournal(RepairJournal.CurrentSchemaVersion, _transactionId, _sourcePath,
            manifest.BaseVersion.BaseSourceSha256, newSha, RepairJournalState.Prepared,
            startedAt, startedAt);
        _journals.Save(journal);

        try
        {
            AfterStep?.Invoke(journal.State);

            // 2. Backup first.
            if (backupPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(_sourcePath, backupPath, overwrite: true);
                journal = journal.WithState(RepairJournalState.BackupCreated);
                _journals.Save(journal);
                AfterStep?.Invoke(journal.State);
            }

            // 3. Render beside the source, preserving the file's own encoding and BOM.
            //    The bytes come from the same helper the hash is computed with, so "the hash of what we
            //    wrote" and "what we wrote" cannot drift apart.
            var bytes = SourceFileHasher.BytesOf(document, renderedText);
            await File.WriteAllBytesAsync(_siblingTempPath, bytes, token).ConfigureAwait(false);
            journal = journal.WithState(RepairJournalState.TempWritten);
            _journals.Save(journal);
            AfterStep?.Invoke(journal.State);

            // 4. The manifest must be durable before the source changes.
            if (manifest.ExpectedNewSha256 != newSha)
                return await AbandonAsync(journal,
                    "渲染出来的字节与清单记录的预期版本不一致，已放弃本次事务，原文未改动。").ConfigureAwait(false);
            RepairExecutionManifestStore.Write(_transactionDirectory, manifest);

            // 5. Replace, atomically. The temporary file stays as the recovery copy of the new version.
            token.ThrowIfCancellationRequested();
            ReplaceAtomically();
            journal = journal.WithState(RepairJournalState.SourceReplaced);
            _journals.Save(journal);
            AfterStep?.Invoke(journal.State);

            // 6. Everything after the replacement is bookkeeping. It is safe to re-run, which is what
            //    makes recovery from here a matter of carrying on rather than of guessing.
            if (_afterReplace is not null)
                await _afterReplace(newSha, token).ConfigureAwait(false);
            journal = journal.WithState(RepairJournalState.DocumentReloaded);
            _journals.Save(journal);
            AfterStep?.Invoke(journal.State);

            journal = journal.WithState(RepairJournalState.StateMigrated);
            _journals.Save(journal);
            AfterStep?.Invoke(journal.State);

            DeleteSiblingTemp();
            journal = journal.WithState(RepairJournalState.Committed);
            _journals.Save(journal);
            AfterStep?.Invoke(journal.State);

            return new SourceEditResult(SourceEditOutcome.Committed,
                $"已改动原文；备份：{backupPath ?? "（无）"}。", _transactionId, backupPath, newSha);
        }
        catch (OperationCanceledException)
        {
            return await AbandonAsync(journal, "已取消，原文未改动。").ConfigureAwait(false);
        }
        catch (TransactionInterrupted)
        {
            // The injected interruption. The journal is left exactly where the crash landed — that is the
            // whole point of injecting one: recovery has to be asked what it makes of that state, so
            // rewriting the state first would be answering its own question. Nothing in this path touches
            // the source file, so the file still matches whatever the journal claims.
            var replaced = journal.HasClaimedReplacement;
            return new SourceEditResult(SourceEditOutcome.Failed,
                replaced ? "事务在替换原文之后中断，事务记录已保留，可继续完成。" : "事务在改动原文之前中断，原文未改动。",
                _transactionId, backupPath, replaced ? newSha : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // If the replacement already happened, the file is in a state the user agreed to; the message
            // has to say so rather than claim nothing changed.
            var replaced = journal.HasClaimedReplacement;
            _journals.Save(journal.WithState(replaced ? journal.State : RepairJournalState.RolledBack));
            return new SourceEditResult(SourceEditOutcome.Failed,
                replaced
                    ? $"原文已经改为新版本，但后续步骤失败：{ex.Message}。事务记录已保留，可继续完成。"
                    : $"未改动原文：{ex.Message}",
                _transactionId, backupPath, replaced ? newSha : null);
        }
    }

    /// <summary>
    /// Puts the new version in place as one step.
    ///
    /// <para><see cref="File.Replace"/> when it can, because it either happens or it does not. The
    /// fallback takes a copy of the current file first, so the promise "the old bytes are still
    /// somewhere" holds even on a file system where the atomic call is unavailable.</para>
    /// </summary>
    private void ReplaceAtomically()
    {
        try
        {
            File.Replace(_siblingTempPath, _sourcePath, destinationBackupFileName: null,
                ignoreMetadataErrors: true);
        }
        catch (PlatformNotSupportedException)
        {
            FallbackReplace();
        }
        catch (IOException)
        {
            // Some targets refuse File.Replace; the fallback is still safe because the backup was taken
            // before this point.
            FallbackReplace();
        }
    }

    private void FallbackReplace()
    {
        var staged = _sourcePath + ".replaced." + Guid.NewGuid().ToString("N")[..8];
        File.Move(_sourcePath, staged);
        try
        {
            File.Move(_siblingTempPath, _sourcePath);
        }
        catch
        {
            // Put the original back rather than leave the path empty.
            File.Move(staged, _sourcePath);
            throw;
        }
        File.Delete(staged);
    }

    private async Task<SourceEditResult> AbandonAsync(RepairJournal journal, string message)
    {
        DeleteSiblingTemp();
        if (!journal.HasClaimedReplacement)
        {
            _journals.Save(journal.WithState(RepairJournalState.RolledBack));
            return new SourceEditResult(SourceEditOutcome.RolledBack, message, _transactionId);
        }
        _journals.Save(journal);
        return new SourceEditResult(SourceEditOutcome.Failed, message, _transactionId);
    }

    private SourceEditResult Refused(string message) =>
        new(SourceEditOutcome.Refused, message, _transactionId);

    private void DeleteSiblingTemp()
    {
        try { if (File.Exists(_siblingTempPath)) File.Delete(_siblingTempPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Finishes a transaction that was interrupted.
///
/// <para>Reads the journal to see how far it got, reads the file to see what actually happened, and then does
/// the one thing the pair implies. It never re-plans and never re-interprets a decision: everything it needs
/// is in the manifest, which was frozen before any of this started.</para>
///
/// <para>For states past the replacement it <b>carries the work forward</b> rather than only reporting what
/// should happen. Reporting was the Phase 5a behaviour, and it left half the crash points needing a person to
/// finish something the program already knew how to finish. Nothing here ever replaces the source again —
/// the file already holds the version that was agreed to, and replacing it a second time is the one action
/// that could destroy a change made outside this program.</para>
/// </summary>
public static class SourceEditRecovery
{
    /// <summary>
    /// <paramref name="afterReplace"/> re-runs the bookkeeping that follows the replacement — the chapter
    /// tree migration, in practice. It is only invoked for a crash that happened <b>before</b> that work
    /// completed; the steps it performs are idempotent, so calling it once more cannot make things worse.
    /// </summary>
    public static async Task<SourceEditResult> RecoverAsync(string transactionRoot, string transactionId,
        Func<CancellationToken, Task>? afterReplace = null, CancellationToken token = default)
    {
        var journals = new RepairJournalStore(transactionRoot);
        var journal = journals.Load(transactionId);
        if (journal is null)
            return new SourceEditResult(SourceEditOutcome.Refused, "找不到这次事务的记录。", transactionId);

        if (!File.Exists(journal.SourcePath))
            return new SourceEditResult(SourceEditOutcome.Refused, "这次事务涉及的原文已经不在了。", transactionId);

        var current = await SourceFileHasher.HashOfAsync(journal.SourcePath, token).ConfigureAwait(false);
        var physical = RepairRecoveryPlanner.Classify(current, journal.BaseSha256, journal.ExpectedNewSha256);
        var decision = RepairRecoveryPlanner.Decide(journal.State, physical);

        switch (decision.Action)
        {
            case RepairRecoveryAction.Discard:
                journals.Save(journal.WithState(RepairJournalState.RolledBack));
                return new SourceEditResult(SourceEditOutcome.RolledBack,
                    "这次事务尚未改动原文，已作废它的记录。", transactionId, Recovery: decision);

            case RepairRecoveryAction.CleanUp:
                DeleteLeftovers(transactionRoot, journal);
                journals.Save(journal.WithState(RepairJournalState.Committed));
                return new SourceEditResult(SourceEditOutcome.Committed,
                    "这次事务已经完成，残留文件已清掉。", transactionId, Recovery: decision);

            case RepairRecoveryAction.Finish:
                // The file is the new version and the journal says the migration ran. All that is left is the
                // temporary file, which is exactly what the step before "Committed" removes.
                DeleteLeftovers(transactionRoot, journal);
                journals.Save(journal.WithState(RepairJournalState.Committed));
                return new SourceEditResult(SourceEditOutcome.Committed,
                    "这次事务已经完成，已收尾。", transactionId, Recovery: decision);

            case RepairRecoveryAction.ContinueReplace:
            {
                // The one square the recovery table lets a program finish on its own: the journal says the
                // transaction never claimed to have crossed the boundary, and the file confirms it. The
                // rendered bytes are already beside the source, so finishing is a rename — and the rename is
                // checked against the version the journal recorded first, because a temporary file that is
                // not the agreed version must not become the book.
                //
                // The manifest is deliberately not required here. A crash can land between writing the
                // temporary file and writing the manifest, so demanding one would make this square
                // impossible to finish — which would contradict the table that says it is the one square a
                // program may finish on its own. The journal already carries both hashes, which is all the
                // check needs.
                var sibling = journal.SourcePath + "." + transactionId + ".easypub-tmp";
                if (!File.Exists(sibling))
                    return new SourceEditResult(SourceEditOutcome.Failed,
                        "这次的临时文件已经不在了，无法继续完成替换。", transactionId, Recovery: decision);
                if (!string.Equals(SourceFileHasher.HashOf(sibling), journal.ExpectedNewSha256,
                        StringComparison.OrdinalIgnoreCase))
                    return new SourceEditResult(SourceEditOutcome.Failed,
                        "临时文件的内容不是这次事务记录的版本，已放弃，原文未改动。", transactionId,
                        Recovery: decision);

                try
                {
                    File.Move(sibling, journal.SourcePath, overwrite: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    return new SourceEditResult(SourceEditOutcome.Failed,
                        "继续完成替换时失败：" + error.Message + "。原文未改动。", transactionId,
                        Recovery: decision);
                }

                journals.Save(journal.WithState(RepairJournalState.SourceReplaced));
                if (afterReplace is not null)
                {
                    try
                    {
                        await afterReplace(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        // The file is already the new version; say so rather than claim nothing happened.
                        return new SourceEditResult(SourceEditOutcome.Failed,
                            $"原文已改为新版本，但后续步骤失败：{error.Message}。事务记录已保留。",
                            transactionId, NewSourceSha256: journal.ExpectedNewSha256, Recovery: decision);
                    }
                }
                journals.Save(journal.WithState(RepairJournalState.DocumentReloaded));
                journals.Save(journal.WithState(RepairJournalState.StateMigrated));
                DeleteLeftovers(transactionRoot, journal);
                journals.Save(journal.WithState(RepairJournalState.Committed));
                return new SourceEditResult(SourceEditOutcome.Committed,
                    decision.Reason + "（已补完）", transactionId,
                    NewSourceSha256: journal.ExpectedNewSha256, Recovery: decision);
            }

            case RepairRecoveryAction.RollForward:
            case RepairRecoveryAction.ReRunTransition:
            {
                // The replacement happened; the steps after it did not all finish. Re-run them — they are
                // idempotent — and never touch the source file itself.
                journal = journal.WithState(RepairJournalState.DocumentReloaded);
                journals.Save(journal);
                if (afterReplace is not null)
                {
                    try
                    {
                        await afterReplace(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        // The book is already the new version, so this is not a reason to claim nothing
                        // happened — but the caller has to know the in-memory models did not catch up.
                        journals.Save(journal);
                        return new SourceEditResult(SourceEditOutcome.Failed,
                            $"原文已经是新版本，但后续步骤再次失败：{error.Message}。事务记录已保留。",
                            transactionId, Recovery: decision);
                    }
                }
                journal = journal.WithState(RepairJournalState.StateMigrated);
                journals.Save(journal);
                DeleteLeftovers(transactionRoot, journal);
                journals.Save(journal.WithState(RepairJournalState.Committed));
                return new SourceEditResult(SourceEditOutcome.Committed,
                    decision.Reason + "（已补完，原文未被二次改动）", transactionId,
                    NewSourceSha256: journal.ExpectedNewSha256, Recovery: decision);
            }

            case RepairRecoveryAction.Conflict:
                journals.Save(journal.WithState(RepairJournalState.Conflict));
                return new SourceEditResult(SourceEditOutcome.Failed, decision.Reason, transactionId,
                    Recovery: decision);

            default:
                return new SourceEditResult(SourceEditOutcome.Failed,
                    decision.Reason + "（这一步需要人工处理）", transactionId, Recovery: decision);
        }
    }

    /// <summary>
    /// Removes what a finished transaction left behind: the sibling temporary, and the working directory
    /// holding the manifest and the rendered copy.
    ///
    /// <para>The journal itself is kept — it is the record of what happened. What goes is everything that
    /// nothing needs once the transaction is committed.</para>
    /// </summary>
    private static void DeleteLeftovers(string transactionRoot, RepairJournal journal)
    {
        try
        {
            var sibling = journal.SourcePath + "." + journal.TransactionId + ".easypub-tmp";
            if (File.Exists(sibling)) File.Delete(sibling);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        try
        {
            var directory = Path.Combine(Path.GetFullPath(transactionRoot), journal.TransactionId);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Every transaction this root still holds, for the window that reports unfinished work.</summary>
    public static IReadOnlyList<RepairJournal> Pending(string transactionRoot) =>
        new RepairJournalStore(transactionRoot).LoadAll()
            .Where(journal => journal.State is not (RepairJournalState.Committed
                or RepairJournalState.RolledBack or RepairJournalState.Conflict))
            .ToArray();

    /// <summary>
    /// Everything a person still has to be told about: in flight, or stopped because the file and the
    /// journal disagreed.
    ///
    /// <para><see cref="Pending"/> deliberately excludes a conflict — recovery cannot act on one — but a
    /// window that showed only <see cref="Pending"/> would report "nothing to finish" while a conflict sat
    /// there for ever. The two lists answer different questions and both are needed.</para>
    /// </summary>
    public static IReadOnlyList<RepairJournal> Outstanding(string transactionRoot) =>
        new RepairJournalStore(transactionRoot).LoadAll()
            .Where(journal => journal.State is not (RepairJournalState.Committed
                or RepairJournalState.RolledBack))
            .ToArray();

    /// <summary>
    /// Finishes every unfinished transaction under one root, oldest first.
    ///
    /// <para>This is the entry a window calls. It exists so that "the program starts and tidies up after a
    /// crash" and "a person presses a button" are the same call, rather than two implementations that agree
    /// only until one of them is edited.</para>
    ///
    /// <para>One transaction failing does not stop the others. Two books each holding an interrupted edit is
    /// exactly the case this is for, and stopping at the first would leave the second unfinished with no
    /// record of why.</para>
    /// </summary>
    public static async Task<SourceRecoveryReport> RecoverAllAsync(string transactionRoot,
        Func<RepairJournal, CancellationToken, Task>? afterReplace = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionRoot);
        var results = new List<SourceEditResult>();
        foreach (var journal in Pending(transactionRoot))
        {
            token.ThrowIfCancellationRequested();
            // Captured rather than closed over the loop variable, so each transaction's callback is told
            // which transaction it is finishing.
            var captured = journal;
            var callback = afterReplace is null
                ? null
                : new Func<CancellationToken, Task>(inner => afterReplace(captured, inner));
            results.Add(await RecoverAsync(transactionRoot, journal.TransactionId, callback, token)
                .ConfigureAwait(false));
        }
        return new SourceRecoveryReport(results);
    }
}

/// <summary>What one pass over the unfinished transactions did.</summary>
public sealed record SourceRecoveryReport(IReadOnlyList<SourceEditResult> Results)
{
    /// <summary>Nothing was unfinished, which is the ordinary case.</summary>
    public static SourceRecoveryReport Empty { get; } = new([]);

    /// <summary>The ones it finished on its own — the file is now the version that was agreed to.</summary>
    public IReadOnlyList<SourceEditResult> Completed =>
        Results.Where(result => result.Outcome == SourceEditOutcome.Committed).ToArray();

    /// <summary>The ones it abandoned without ever touching the file.</summary>
    public IReadOnlyList<SourceEditResult> Discarded =>
        Results.Where(result => result.Outcome == SourceEditOutcome.RolledBack).ToArray();

    /// <summary>The ones a person has to look at.</summary>
    public IReadOnlyList<SourceEditResult> NeedsAttention =>
        Results.Where(result => result.NeedsAttention).ToArray();

    public bool AnythingHappened => Results.Count > 0;

    /// <summary>One sentence for a status line, with the counts that are not zero.</summary>
    public string Describe()
    {
        if (Results.Count == 0) return "没有未完成的原文修改。";
        var parts = new List<string>();
        if (Completed.Count > 0) parts.Add($"补完 {Completed.Count} 次原文替换");
        if (Discarded.Count > 0) parts.Add($"作废 {Discarded.Count} 条未改动原文的记录");
        var untouched = Results.Count - Completed.Count - Discarded.Count - NeedsAttention.Count;
        if (untouched > 0) parts.Add($"{untouched} 条无需处理");
        if (NeedsAttention.Count > 0) parts.Add($"{NeedsAttention.Count} 条需要人工确认");
        return "上次的原文修改：" + string.Join("，", parts) + "。";
    }
}
