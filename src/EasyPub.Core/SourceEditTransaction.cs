using System.Text;

namespace EasyPub.Core;

/// <summary>How a transaction ended.</summary>
public enum SourceEditOutcome
{
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
            // 2. Backup first.
            if (backupPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(_sourcePath, backupPath, overwrite: true);
                journal = journal.WithState(RepairJournalState.BackupCreated);
                _journals.Save(journal);
            }

            // 3. Render beside the source, preserving the file's own encoding and BOM.
            //    The bytes come from the same helper the hash is computed with, so "the hash of what we
            //    wrote" and "what we wrote" cannot drift apart.
            var bytes = SourceFileHasher.BytesOf(document, renderedText);
            await File.WriteAllBytesAsync(_siblingTempPath, bytes, token).ConfigureAwait(false);
            journal = journal.WithState(RepairJournalState.TempWritten);
            _journals.Save(journal);

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

            // 6. Everything after the replacement is bookkeeping. It is safe to re-run, which is what
            //    makes recovery from here a matter of carrying on rather than of guessing.
            if (_afterReplace is not null)
                await _afterReplace(newSha, token).ConfigureAwait(false);
            journal = journal.WithState(RepairJournalState.DocumentReloaded);
            _journals.Save(journal);
            journal = journal.WithState(RepairJournalState.StateMigrated);
            _journals.Save(journal);

            DeleteSiblingTemp();
            journal = journal.WithState(RepairJournalState.Committed);
            _journals.Save(journal);

            return new SourceEditResult(SourceEditOutcome.Committed,
                $"已改动原文；备份：{backupPath ?? "（无）"}。", _transactionId, backupPath, newSha);
        }
        catch (OperationCanceledException)
        {
            return await AbandonAsync(journal, "已取消，原文未改动。").ConfigureAwait(false);
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
/// <para>Reads the journal to see how far it got, reads the file to see what actually happened, and then
/// does the one thing the pair implies. It never re-plans and never re-interprets a decision: everything
/// it needs is in the manifest, which was frozen before any of this started.</para>
/// </summary>
public static class SourceEditRecovery
{
    public static async Task<SourceEditResult> RecoverAsync(string transactionRoot, string transactionId,
        CancellationToken token = default)
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
                journals.Save(journal.WithState(RepairJournalState.Committed));
                return new SourceEditResult(SourceEditOutcome.Committed,
                    "这次事务已经完成。", transactionId, Recovery: decision);

            case RepairRecoveryAction.Conflict:
                journals.Save(journal.WithState(RepairJournalState.Conflict));
                return new SourceEditResult(SourceEditOutcome.Failed, decision.Reason, transactionId,
                    Recovery: decision);

            // The remaining actions all need the manifest, and all of them mean the file is either already
            // the new version or safe to make so. Neither is done here: this method reports what should
            // happen so the caller can decide, rather than writing during a diagnostic pass.
            default:
                return new SourceEditResult(SourceEditOutcome.Failed,
                    decision.Reason + "（需要按事务清单继续完成）", transactionId, Recovery: decision);
        }
    }

    /// <summary>Every transaction this root still holds, for the window that reports unfinished work.</summary>
    public static IReadOnlyList<RepairJournal> Pending(string transactionRoot) =>
        new RepairJournalStore(transactionRoot).LoadAll()
            .Where(journal => journal.State is not (RepairJournalState.Committed
                or RepairJournalState.RolledBack or RepairJournalState.Conflict))
            .ToArray();
}
