namespace EasyPub.Core;

/// <summary>Which of the three bindings no longer matches.</summary>
public enum RepairVersionDrift
{
    /// <summary>Nothing changed; the plan still describes this book.</summary>
    None,
    /// <summary>The TXT bytes changed — the file was edited, replaced or restored outside EasyPub.</summary>
    Source,
    /// <summary>The chapter tree changed — someone edited the structure this plan was built from.</summary>
    Tree,
    /// <summary>The recognition rules changed — the same bytes now yield a different tree.</summary>
    Recognition,
    /// <summary>More than one of the above changed.</summary>
    Several,
}

/// <summary>The outcome of checking a plan against the book as it is now.</summary>
public sealed record RepairVersionCheck(RepairVersionDrift Drift, string Message)
{
    public bool IsValid => Drift == RepairVersionDrift.None;

    /// <summary>What the confirmation window puts in front of the user, in their words.</summary>
    public static RepairVersionCheck Ok { get; } =
        new(RepairVersionDrift.None, "方案与当前书稿一致。");
}

/// <summary>
/// Checks that a plan still describes the book in front of it.
///
/// <para>Each fingerprint is reported separately, because the three failures need different things from
/// the reader. A changed source means the file was edited elsewhere and they should look at it. A changed
/// tree means they already edited the structure — usually in this very window — and can simply regenerate.
/// Changed rules mean a setting moved. Saying only "the plan expired" makes all three look alike, and the
/// reader has no way to tell which one to fix.</para>
///
/// <para>There is deliberately no "carry on anyway" path. A half-matching plan produces a tree the user
/// never saw previewed, and the preview is the only thing that made the repair safe to accept.</para>
/// </summary>
public static class RepairPlanValidator
{
    public static RepairVersionCheck Validate(
        RepairBaseVersion expected,
        string currentSourceSha256,
        IEnumerable<ChapterTreeEntry> currentEntries,
        TocHierarchyOptions currentRecognition,
        string? currentChapterPattern)
    {
        var sourceChanged = !string.Equals(expected.BaseSourceSha256, currentSourceSha256, StringComparison.OrdinalIgnoreCase);
        var treeChanged = !string.Equals(expected.BaseTreeFingerprint,
            ChapterTreeFingerprint.Compute(currentEntries), StringComparison.Ordinal);
        var recognitionChanged = !string.Equals(expected.RecognitionFingerprint,
            RecognitionFingerprint.Compute(currentRecognition, currentChapterPattern), StringComparison.Ordinal);

        var changed = (sourceChanged ? 1 : 0) + (treeChanged ? 1 : 0) + (recognitionChanged ? 1 : 0);
        if (changed == 0) return RepairVersionCheck.Ok;

        var reasons = new List<string>();
        if (sourceChanged) reasons.Add("TXT 的内容变了");
        if (treeChanged) reasons.Add("章节树变了");
        if (recognitionChanged) reasons.Add("识别规则变了");
        var drift = changed > 1 ? RepairVersionDrift.Several
            : sourceChanged ? RepairVersionDrift.Source
            : treeChanged ? RepairVersionDrift.Tree
            : RepairVersionDrift.Recognition;

        return new RepairVersionCheck(drift,
            $"{string.Join("；", reasons)}。本次预览已经过期，请重新生成修复方案。"
            + (sourceChanged ? "（TXT 被本程序之外的东西改过，应用前请先核对原文。）" : ""));
    }

    /// <summary>Convenience overload for the common case, where the document carries the rules.</summary>
    public static RepairVersionCheck Validate(RepairBaseVersion expected, ChapterTreeDocument document,
        IEnumerable<ChapterTreeEntry> entries, string? chapterPattern = null) =>
        Validate(expected, document.SourceSha256, entries, document.RecognitionOptions, chapterPattern ?? document.ChapterPattern);
}

/// <summary>
/// Why a set of decisions cannot be compiled, or that it can.
///
/// <para>This is the Phase 3 half of the check. It can already tell that an action promised a source edit
/// and nothing in the plan can deliver one; what it cannot yet do is compile the edit itself, which
/// arrives with <c>SourcePatch</c>. Having the decision-level check here means the failure is reported
/// before any write path exists at all.</para>
/// </summary>
public sealed record RepairCompileCheck(bool CanApply, IReadOnlyList<RepairCompileProblem> Problems)
{
    public static RepairCompileCheck Ok { get; } = new(true, []);
}

/// <summary>One reason a decided action cannot become a plan.</summary>
public sealed record RepairCompileProblem(
    string ActionId,
    string Title,
    ReferenceActionKind Kind,
    RepairCompileProblemKind Reason);

public enum RepairCompileProblemKind
{
    /// <summary>An action was selected that cannot be performed at all.</summary>
    NotExecutable,
    /// <summary>The action promised a source edit, and the plan cannot produce a safe one.</summary>
    SourceEditUnavailable,
    /// <summary>A decision names an action that is not in this proposal.</summary>
    UnknownAction,
}

/// <summary>
/// Turns a set of decisions into a verdict on whether they can be applied.
///
/// <para>The rule that matters here: an action whose decision is
/// <see cref="SourceEffectDecision.ApplyRequired"/> but which cannot produce a source operation makes the
/// whole plan fail, rather than being quietly downgraded. See <see cref="SourceEffectDecision"/> for why a
/// silent downgrade is the one outcome that must not happen.</para>
/// </summary>
public static class RepairDecisionCompiler
{
    /// <summary>
    /// Checks the decisions against a proposal.
    ///
    /// <paramref name="canEditSource"/> is whether a source operation exists for an action — supplied by
    /// the caller because Phase 3 has no <c>SourcePatch</c> yet. Passing <c>null</c> means "nothing can
    /// edit the source", which is the honest answer today: every
    /// <see cref="SourceEffectDecision.ApplyRequired"/> action is reported as unavailable rather than
    /// silently accepted.
    /// </summary>
    public static RepairCompileCheck Check(RepairProposal proposal,
        IReadOnlyList<RepairDecision> decisions,
        Func<ReferenceAction, bool>? canEditSource = null)
    {
        var byId = proposal.Actions.ToDictionary(RepairActionIdentity.Compute, StringComparer.Ordinal);
        var problems = new List<RepairCompileProblem>();

        foreach (var decision in decisions)
        {
            if (!byId.TryGetValue(decision.ActionId, out var action))
            {
                problems.Add(new RepairCompileProblem(decision.ActionId, "", ReferenceActionKind.KeepExtra,
                    RepairCompileProblemKind.UnknownAction));
                continue;
            }
            if (!decision.Selected) continue;

            if (action.Kind is ReferenceActionKind.MissingBody or ReferenceActionKind.KeepExtra
                or ReferenceActionKind.VolumeNote)
            {
                // These are findings, not proposals. A selection that cannot be performed must be named,
                // not ignored — a checkbox the window draws but cannot honour is the exact defect the
                // confirmation window was built to remove.
                problems.Add(new RepairCompileProblem(decision.ActionId, action.Title, action.Kind,
                    RepairCompileProblemKind.NotExecutable));
                continue;
            }

            if (decision.SourceEffect != SourceEffectDecision.ApplyRequired) continue;
            if (canEditSource?.Invoke(action) == true) continue;
            problems.Add(new RepairCompileProblem(decision.ActionId, action.Title, action.Kind,
                RepairCompileProblemKind.SourceEditUnavailable));
        }

        return problems.Count == 0 ? RepairCompileCheck.Ok : new RepairCompileCheck(false, problems);
    }

    /// <summary>
    /// The outcome to record for each decision <b>before</b> a plan exists.
    ///
    /// <para>Without a patch there is nothing to look up, so a conditional request is reported as
    /// <see cref="SourceEffectOutcome.Downgraded"/>: "asked for the source to change, no operation carries
    /// it". That is the honest answer at this point and it is what the confirmation window shows before the
    /// plan is compiled.</para>
    ///
    /// <para>Once a plan exists, use <see cref="OutcomesFor"/>. Asking the compiled patch is the only way the
    /// recorded outcome can match what actually happened; a predicate overload invited call sites to answer
    /// from something other than the patch they were about to apply.</para>
    /// </summary>
    public static IReadOnlyList<SourceEffectOutcome> Outcomes(IReadOnlyList<RepairDecision> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);
        return decisions.Select(decision => decision.SourceEffect switch
        {
            SourceEffectDecision.None => SourceEffectOutcome.NotRequested,
            SourceEffectDecision.ApplyRequired => SourceEffectOutcome.Applied,
            _ => SourceEffectOutcome.Downgraded,
        }).ToArray();
    }

    /// <summary>
    /// The outcomes for a compiled plan, keyed by action id.
    ///
    /// <para>Takes the patch rather than a predicate so the call site cannot pass a different answer than
    /// the one the patch was built from.</para>
    /// </summary>
    public static IReadOnlyList<SourceEffectOutcome> OutcomesFor(RepairProposal proposal,
        IReadOnlyList<RepairDecision> decisions, SourcePatch? patch)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(decisions);

        var actionsWithOperations = (patch?.Operations ?? [])
            .Select(operation => operation.ActionId)
            .ToHashSet(StringComparer.Ordinal);

        return decisions.Select(decision => decision.SourceEffect switch
        {
            SourceEffectDecision.None => SourceEffectOutcome.NotRequested,
            // A required edit that compiled is applied; one that did not compile would have failed the
            // decision check before this point, so reaching here with no operation means the check was
            // skipped — reporting Applied would be a lie, so it is reported as downgraded.
            _ => actionsWithOperations.Contains(decision.ActionId)
                ? SourceEffectOutcome.Applied
                : SourceEffectOutcome.Downgraded,
        }).ToArray();
    }
}
