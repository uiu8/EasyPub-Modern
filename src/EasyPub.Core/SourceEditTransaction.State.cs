using System.Security.Cryptography;

namespace EasyPub.Core;

/// <summary>
/// Where the source file actually is, read from its bytes rather than from what the app remembers doing.
///
/// <para>The journal is written by this application and is exactly the thing most likely to be behind —
/// a crash lands between doing something and recording it. So the transaction boundary is decided by the
/// file: <see cref="Base"/> means the commit has not happened, <see cref="Expected"/> means it has.
/// The journal only says what the app *thinks* it was doing, and is used to check the two agree.</para>
/// </summary>
public enum PhysicalCommitState
{
    /// <summary>The file is still the version the plan was computed against.</summary>
    Base,
    /// <summary>The file is the version the plan said it would become.</summary>
    Expected,
    /// <summary>Neither — something outside this transaction changed the file.</summary>
    Conflict,
}

/// <summary>How far the application believed it had got.</summary>
public enum RepairJournalState
{
    Prepared,
    BackupCreated,
    TempWritten,
    SourceReplaced,
    DocumentReloaded,
    StateMigrated,
    Committed,
    /// <summary>Recovery gave up; a person has to look.</summary>
    Conflict,
    /// <summary>Abandoned before any file was replaced.</summary>
    RolledBack,
}

/// <summary>What recovery decided to do, and why.</summary>
public enum RepairRecoveryAction
{
    /// <summary>Nothing was replaceable; the transaction can be dropped with no side effects.</summary>
    Discard,
    /// <summary>The replacement had not happened yet and may be performed.</summary>
    ContinueReplace,
    /// <summary>The replacement happened; carry the rest of the transaction forward.</summary>
    RollForward,
    /// <summary>The remaining metadata steps can be re-run; they are idempotent.</summary>
    ReRunTransition,
    /// <summary>The last checks passed; mark it done and tidy up.</summary>
    Finish,
    /// <summary>Nothing left to do but remove leftovers.</summary>
    CleanUp,
    /// <summary>The journal and the file disagree. Stop; do not guess.</summary>
    Conflict,
}

/// <summary>A recovery decision, with the reason stated so it can be shown to a person.</summary>
public sealed record RepairRecoveryDecision(RepairRecoveryAction Action, string Reason)
{
    public bool NeedsAttention => Action == RepairRecoveryAction.Conflict;
}

/// <summary>
/// Decides what to do about a transaction that was interrupted.
///
/// <para>Two rules shape the whole table.</para>
///
/// <para><b>The file decides, not the journal.</b> A journal that says the source was replaced while the
/// file still holds the old bytes is a disagreement, not an instruction — and the most likely cause is a
/// person putting the old version back. Replacing it again would silently destroy the thing they just
/// did.</para>
///
/// <para><b>Only "the replacement had not been claimed yet" may be finished automatically.</b> Every other
/// combination where the journal is ahead of the file is a conflict. That single exception is safe because
/// the journal saying <see cref="RepairJournalState.TempWritten"/> is a statement that the transaction
/// never crossed the boundary; anything past that point has claimed a commit the file does not show.</para>
/// </summary>
public static class RepairRecoveryPlanner
{
    public static PhysicalCommitState Classify(string currentSha256, string baseSha256, string expectedNewSha256)
    {
        if (string.Equals(currentSha256, baseSha256, StringComparison.OrdinalIgnoreCase))
            return PhysicalCommitState.Base;
        if (string.Equals(currentSha256, expectedNewSha256, StringComparison.OrdinalIgnoreCase))
            return PhysicalCommitState.Expected;
        return PhysicalCommitState.Conflict;
    }

    public static RepairRecoveryDecision Decide(RepairJournalState journal, PhysicalCommitState physical)
    {
        if (physical == PhysicalCommitState.Conflict)
            return new RepairRecoveryDecision(RepairRecoveryAction.Conflict,
                "原文既不是本次事务开始时的版本，也不是它应该变成的版本 —— 有本程序之外的东西改过它，请人工核对。");

        if (physical == PhysicalCommitState.Expected)
        {
            return journal switch
            {
                RepairJournalState.Prepared or RepairJournalState.BackupCreated
                    => new RepairRecoveryDecision(RepairRecoveryAction.Conflict,
                        "记录说替换还没开始，但文件已经是替换后的版本 —— 两者对不上，请人工核对。"),
                RepairJournalState.TempWritten or RepairJournalState.SourceReplaced
                    or RepairJournalState.DocumentReloaded
                    => new RepairRecoveryDecision(RepairRecoveryAction.RollForward,
                        "文件已经是替换后的版本，继续完成这次事务的其余步骤。"),
                RepairJournalState.StateMigrated
                    => new RepairRecoveryDecision(RepairRecoveryAction.Finish,
                        "文件已经是替换后的版本，剩下的检查通过后即可收尾。"),
                RepairJournalState.Committed
                    => new RepairRecoveryDecision(RepairRecoveryAction.CleanUp,
                        "这次事务已经完成，只需清掉残留的临时文件。"),
                _ => new RepairRecoveryDecision(RepairRecoveryAction.Conflict,
                    $"记录的状态（{journal}）与文件的实际版本无法对应，请人工核对。"),
            };
        }

        // physical == Base: the replacement has not happened.
        return journal switch
        {
            RepairJournalState.Prepared or RepairJournalState.BackupCreated
                => new RepairRecoveryDecision(RepairRecoveryAction.Discard,
                    "替换还没开始，放弃这次事务不会留下任何改动。"),
            // The one state that may be carried on: it says the transaction never claimed to have crossed
            // the boundary, so finishing the replacement is just doing what was already decided.
            RepairJournalState.TempWritten
                => new RepairRecoveryDecision(RepairRecoveryAction.ContinueReplace,
                    "替换还没执行，可以安全地继续完成它；重新渲染的字节必须与记录一致。"),
            // Everything from here on has claimed a commit the file does not show. Refuse to guess which
            // of "the replace failed" and "someone put the old file back" happened.
            RepairJournalState.SourceReplaced or RepairJournalState.DocumentReloaded
                or RepairJournalState.StateMigrated
                => new RepairRecoveryDecision(RepairRecoveryAction.Conflict,
                    "记录说已经替换过原文，但文件仍是替换前的版本 —— 可能有人把旧版本放回来了。"
                    + "为避免覆盖那次恢复，这里不自动重做，请人工核对。"),
            RepairJournalState.Committed
                => new RepairRecoveryDecision(RepairRecoveryAction.Conflict,
                    "记录说这次事务已经完成，但文件不是它应有的版本 —— 请人工核对。"),
            RepairJournalState.RolledBack
                => new RepairRecoveryDecision(RepairRecoveryAction.Discard,
                    "这次事务已经放弃过，清掉残留记录即可。"),
            _ => new RepairRecoveryDecision(RepairRecoveryAction.Conflict,
                $"记录的状态（{journal}）无法对应文件的实际版本，请人工核对。"),
        };
    }
}

/// <summary>
/// What has to be true before this application may write to a source file.
///
/// <para>All four, not any of them. An earlier version of this check asked whether the source directory
/// <i>and</i> the backup directory were both unwritable, which is a far weaker question: a read-only source
/// directory fails the very first requirement — there is nowhere to put the sibling temporary file the
/// replacement is built from — and the old check would not have noticed.</para>
/// </summary>
public sealed record SourceEditPreflight(
    bool CanReplaceSourceFile,
    bool CanCreateSiblingTemp,
    bool CanWriteJournal,
    bool CanWriteBackupTarget)
{
    public bool CanEditSource =>
        CanReplaceSourceFile && CanCreateSiblingTemp && CanWriteJournal && CanWriteBackupTarget;

    /// <summary>Which capability is missing, so the window can say what to fix rather than "cannot edit".</summary>
    public IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        if (!CanReplaceSourceFile) missing.Add("替换源文件");
        if (!CanCreateSiblingTemp) missing.Add("在源文件同目录建临时文件");
        if (!CanWriteJournal) missing.Add("写事务记录");
        if (!CanWriteBackupTarget) missing.Add("写备份");
        return missing;
    }

    public string Describe() => CanEditSource
        ? "可以改动原文。"
        : "不能改动原文，缺少：" + string.Join("、", Missing()) + "。";
}

/// <summary>Probes the filesystem to answer <see cref="SourceEditPreflight"/> for a real path.</summary>
public static class SourceEditPreflightInspector
{
    /// <summary>
    /// Checks each capability by actually trying it, because "is it read-only" has many answers — a
    /// read-only attribute, a missing permission, a full disk, a locked file — and only an attempt
    /// distinguishes them. Everything it creates is removed before returning.
    /// </summary>
    public static SourceEditPreflight Inspect(string sourcePath, string journalDirectory,
        string backupDirectory)
    {
        var full = Path.GetFullPath(sourcePath);
        var directory = Path.GetDirectoryName(full);
        if (directory is null || !File.Exists(full))
            return new SourceEditPreflight(false, false, false, false);

        var canReplace = CanOpenForWrite(full);
        var canTemp = CanCreateAndDelete(directory, Path.GetFileName(full) + ".preflight");
        var canJournal = CanCreateAndDelete(journalDirectory, "preflight");
        var canBackup = CanCreateAndDelete(backupDirectory, "preflight");
        return new SourceEditPreflight(canReplace, canTemp, canJournal, canBackup);
    }

    private static bool CanOpenForWrite(string file)
    {
        try
        {
            // FileShare.Read so the attempt does not fail merely because something else is reading it —
            // the question is whether this process may write, not whether it has the file to itself.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            return stream.CanWrite;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool CanCreateAndDelete(string directory, string name)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, name + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.WriteByte(0);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Computes the hash a file has right now, for comparing against a plan.</summary>
public static class SourceFileHasher
{
    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static async Task<string> HashOfAsync(string path, CancellationToken token = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    /// <summary>
    /// The bytes a rendered document will occupy on disk, and their hash.
    ///
    /// <para>The preamble comes from the document's own encoding, which is the same source the transaction
    /// writes from. Hashing the text alone gives a different answer for a file that has a byte-order mark,
    /// and the difference only shows up as a mismatch <b>after</b> the source has been replaced — the worst
    /// possible moment to discover it.</para>
    /// </summary>
    public static byte[] BytesOf(SourceTextDocument document, string renderedText)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(renderedText);
        return document.Encoding.GetPreamble()
            .Concat(document.Encoding.GetBytes(renderedText))
            .ToArray();
    }

    public static string HashOfRendered(SourceTextDocument document, string renderedText) =>
        Convert.ToHexString(SHA256.HashData(BytesOf(document, renderedText)));
}
