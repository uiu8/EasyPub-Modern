namespace EasyPub.Core;

/// <summary>
/// What "restore this backup" brings back.
///
/// <para>The two answers are genuinely different products, not two ways of doing one thing. Restoring the
/// text is always possible; restoring the text <b>and</b> the chapter tree the reader had arranged is only
/// possible when that tree was saved beside the bytes.</para>
/// </summary>
public enum RestoreTargetPolicy
{
    /// <summary>
    /// The text comes back; the chapter tree is recognised again from it.
    ///
    /// <para>Every manual decision — a chapter the directory named that the text lacked, a heading demoted
    /// back into the body, a title corrected by hand — is gone, because those were decisions and there is
    /// nothing to recognise them from. This is the default: it is always available, it needs nothing extra
    /// saved, and it is what a reader who just wants their text back is asking for.</para>
    /// </summary>
    SourceOnly,

    /// <summary>
    /// The text and the chapter tree that was saved with it both come back.
    ///
    /// <para>Refused when that version has no saved tree. Substituting a freshly recognised tree would
    /// report success while quietly discarding the very thing the policy exists to bring back.</para>
    /// </summary>
    FullBookState,
}

public enum RestoreOutcome
{
    /// <summary>The source file is now the restored version.</summary>
    Restored,
    /// <summary>Nothing was written: a precondition failed, or the file was already that version.</summary>
    Refused,
    /// <summary>The file may be in a state nobody chose; the journal says how far it got.</summary>
    Failed,
}

/// <summary>What a restore did, in terms a person can act on.</summary>
public sealed record RestoreResult(
    RestoreOutcome Outcome,
    string Message,
    RestoreTargetPolicy Policy,
    string? BackupPath = null,
    string? RestoredSha256 = null,
    SourceEditResult? Transaction = null,
    SourceTransitionResult? Transition = null)
{
    /// <summary>
    /// The state the program should carry on with. Set only on <see cref="RestoreOutcome.Restored"/>: after a
    /// refusal the caller keeps what it had, and after a failure the file may be in a state nobody chose.
    /// </summary>
    public SourceTransitionState? State { get; init; }

    public bool Succeeded => Outcome == RestoreOutcome.Restored;

    /// <summary>Which saved chapters did not survive the move onto the restored text.</summary>
    public IReadOnlyList<SourceEntryRebind> LostIdentities => Transition?.Lost ?? [];

    public bool NeedsAttention => Outcome == RestoreOutcome.Failed
        || Transaction is { NeedsAttention: true };
}

/// <summary>
/// Puts a saved version back, through the same machinery a repair uses.
///
/// <para>A restore is <b>not</b> a special path. It is the same transaction — preflight, journal, atomic
/// replace, manifest, roll-forward — and the same version migration. The only thing that differs is where the
/// target text comes from (a snapshot instead of a compiled patch) and what the chapter tree should be
/// afterwards (re-recognised, or the one saved with it). Keeping it that way is the point: two ways to write
/// a source file would mean two places for the backup-before-write rule to be forgotten.</para>
///
/// <para>The sequence:</para>
///
/// <list type="number">
/// <item>Refuse before touching anything if the target cannot be written, or if the saved tree the caller
/// asked for is not there.</item>
/// <item>Work out the operations that turn the current text into the saved one — by comparing them line by
/// line, not by re-running whatever produced the current version.</item>
/// <item>Hand those operations to <see cref="SourceEditTransaction"/>, which takes a fresh backup of the
/// current version first, so a restore is itself undoable.</item>
/// <item>Move the chapter tree from the current version to the restored one with
/// <see cref="SourceVersionTransition"/>, so a saved chapter whose heading line is untouched keeps its
/// identity instead of being re-invented.</item>
/// </list>
/// </summary>
public sealed class RestoreTransaction
{
    private readonly string _transactionRoot;
    private readonly string _sourcePath;
    private readonly TimeProvider _clock;

    public RestoreTransaction(string transactionRoot, string sourcePath, TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        _transactionRoot = Path.GetFullPath(transactionRoot);
        _sourcePath = Path.GetFullPath(sourcePath);
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Restores one saved version.
    ///
    /// <para><paramref name="current"/> is the state the running program holds. Its text must be the text
    /// currently on disk; when it is not, the caller is looking at a book that changed underneath it and the
    /// honest answer is to refuse and let them reload.</para>
    /// </summary>
    public async Task<RestoreResult> ExecuteAsync(
        SourceTransitionState current,
        string backupPath,
        RestoreTargetPolicy policy = RestoreTargetPolicy.SourceOnly,
        ChapterTreeDocumentCache? cache = null,
        SourceFullStateSnapshot? savedTree = null,
        string? backupDestination = null,
        TextEncodingMode encodingMode = TextEncodingMode.Auto,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);

        if (!File.Exists(backupPath))
            return Refused(policy, "这份备份文件已经不在了，未做任何改动。");
        if (!File.Exists(_sourcePath))
            return Refused(policy, "原文不存在，未做任何改动。");

        var targetDocument = SourceTextDocument.Load(backupPath, encodingMode);
        var targetText = targetDocument.Render();
        var targetSha = SourceFileHasher.HashOf(backupPath);

        // The tree has to be the one saved with these exact bytes. A tree from another version has line
        // numbers that mean something else, and re-binding it would move every chapter by however much the
        // two versions differ.
        if (policy == RestoreTargetPolicy.FullBookState)
        {
            if (savedTree is null)
                return Refused(policy, "这个版本没有保存章节树，无法恢复到当时的完整书稿状态。可以改用「只恢复原文」。");
            if (!string.Equals(savedTree.SourceSha256, targetSha, StringComparison.OrdinalIgnoreCase))
                return Refused(policy, "保存的章节树不属于这份备份，二者对不上，未做任何改动。");
        }

        // Already this version: a replace would be a no-op, and a journal for it could not tell the two
        // states apart because both hashes would be the same value.
        var onDisk = await SourceFileHasher.HashOfAsync(_sourcePath, token).ConfigureAwait(false);
        if (string.Equals(onDisk, targetSha, StringComparison.OrdinalIgnoreCase))
            return new RestoreResult(RestoreOutcome.Refused, "原文已经是这个版本，没有需要恢复的东西。", policy,
                backupPath, targetSha);
        if (!string.Equals(onDisk, current.RenderedHash, StringComparison.OrdinalIgnoreCase))
            return Refused(policy, "原文已被本程序之外的东西改过，未做任何改动。请重新打开书稿后再恢复。");

        var preflight = SourceEditPreflightInspector.Inspect(_sourcePath, _transactionRoot,
            Path.GetDirectoryName(_sourcePath)!);
        if (!preflight.CanEditSource)
            return Refused(policy, "现在不能改动原文：" + string.Join("、", preflight.Missing()) + "。");

        var patch = SourceDiff.Patch(current.Source, targetDocument);
        var coordinates = SourceCoordinateMap.Build(current.Source, patch, targetText);
        var verification = coordinates.Verify(current.Source, patch, targetText);
        if (!verification.IsSound)
            return Refused(policy, "无法把当前版本与这份备份对齐，未做任何改动：" + verification.Message);

        // What the book is supposed to be afterwards, worked out before anything is written. For a
        // full-state restore that is the saved tree; otherwise it is what the restored text recognises to.
        // Freezing it here is what lets recovery read the answer instead of re-deriving it against rules
        // that may have changed since.
        var restoredPlan = policy == RestoreTargetPolicy.FullBookState && savedTree is not null
            ? savedTree.Tree
            : RecognisePlan(backupPath, encodingMode);
        var expected = CanonicalChapterModelFactory.Build(restoredPlan.Entries,
            line => targetDocument.Lines.ElementAtOrDefault(line - 1)?.Text);

        // The migration runs after the swap, and lands the tree on the restored text.
        var next = current;
        SourceTransitionResult? transition = null;
        var transaction = new SourceEditTransaction(_transactionRoot, _sourcePath, clock: _clock, afterReplace:
            async (newSha, inner) =>
            {
                transition = await SourceVersionTransition
                    .AfterReplaceAsync(current, patch, targetText, _sourcePath, cache, encodingMode,
                        oldCatalogPath: null, token: inner)
                    .ConfigureAwait(false);
                // FullBookState means the saved tree is what the book has now, not a fresh recognition of it.
                // The migration still runs first: it is what reports which saved identities could not follow.
                next = policy == RestoreTargetPolicy.FullBookState && savedTree is not null
                    ? transition.State with { Plan = savedTree.Tree }
                    : transition.State;
            });

        var manifest = BuildManifest(current, patch, targetSha, policy, backupPath, targetDocument, expected);
        var edit = await transaction.ExecuteAsync(manifest, targetDocument, targetText, backupDestination, token)
            .ConfigureAwait(false);

        if (!edit.Succeeded)
            return new RestoreResult(
                edit.Outcome == SourceEditOutcome.Failed ? RestoreOutcome.Failed : RestoreOutcome.Refused,
                edit.Message, policy, backupPath, targetSha, edit, transition);

        return new RestoreResult(RestoreOutcome.Restored,
            DescribeRestore(policy, transition, edit), policy, backupPath, targetSha, edit, transition)
        {
            State = next,
        };
    }

    /// <summary>
    /// What the restored text recognises to — the tree a source-only restore ends up with.
    ///
    /// <para>Recognised from the bytes already read, not by loading the backup path: the backup is a `.bak`
    /// file and the path-based loader refuses anything that is not a `.txt`.</para>
    /// </summary>
    private static ChapterTreePlan RecognisePlan(string backupPath, TextEncodingMode encodingMode)
    {
        var document = ChapterTreeDocument.Load(backupPath, File.ReadAllBytes(backupPath),
            encodingMode: encodingMode, cancellationToken: CancellationToken.None);
        return document.CreatePlan(document.Entries);
    }

    /// <summary>
    /// What the migration should bring the book to, frozen before anything is written.
    ///
    /// <para>Recovery reads this instead of re-planning: after a crash the current text may already be the
    /// restored version, and running the recognition pass again would produce a tree that depends on the
    /// rules in force at recovery time rather than the ones the reader agreed to.</para>
    /// </summary>
    private static RepairExecutionManifest BuildManifest(SourceTransitionState current, SourcePatch patch,
        string targetSha, RestoreTargetPolicy policy, string backupPath,
        SourceTextDocument targetDocument, CanonicalChapterModel expected)
    {
        return new RepairExecutionManifest(
            RepairExecutionManifest.CurrentSchemaVersion,
            "restore",
            "restore:" + policy,
            RepairBaseVersionFactory.Capture(current.SourceSha256, current.Plan?.Entries ?? [],
                current.RecognitionTree?.RecognitionOptions ?? new TocHierarchyOptions(), null),
            [],
            new TreePatch("restore", [], expected),
            patch,
            expected,
            targetSha,
            current.RenderedHash,
            RepairLandingMode.EditSource,
            backupPath,
            targetDocument.DefaultNewLine);
    }

    private static string DescribeRestore(RestoreTargetPolicy policy, SourceTransitionResult? transition,
        SourceEditResult edit)
    {
        var what = policy == RestoreTargetPolicy.FullBookState ? "原文与章节树" : "原文";
        var lost = transition?.Lost ?? [];
        var detail = lost.Count == 0
            ? ""
            : $"；有 {lost.Count} 个章节身份没能跟过来（{string.Join("、", lost.Take(3).Select(item => item.Title))}"
                + (lost.Count > 3 ? " 等" : "") + "）";
        return $"已恢复{what}。{edit.Message}{detail}";
    }

    private RestoreResult Refused(RestoreTargetPolicy policy, string message) =>
        new(RestoreOutcome.Refused, message, policy);
}
