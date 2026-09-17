namespace EasyPub.Core;

/// <summary>
/// What the user decided about one planned action.
///
/// <para>The plan says what *could* be done; this says what *will* be. Keeping them apart is what stops
/// the compiler from making a decision on the user's behalf: "remove the duplicate copy from the
/// finished book" and "also delete those lines from the TXT" are two different answers, and before this
/// type existed there was nowhere to write the second one down — the compiler looked at the landing mode
/// and chose for itself.</para>
/// </summary>
public sealed record RepairDecision(
    string ActionId,
    bool Selected,
    SourceEffectDecision SourceEffect);

/// <summary>
/// Whether this action should touch the source text, and what happens when it cannot.
///
/// <para><see cref="ApplyIfAvailable"/> and <see cref="ApplyRequired"/> differ in *when* they fail, not
/// in whether they fail:</para>
///
/// <list type="bullet">
/// <item><see cref="ApplyIfAvailable"/> — no safe source edit could be compiled, so the action lands in
/// the tree only. The outcome is recorded as <see cref="SourceEffectOutcome.Downgraded"/> and shown, so
/// it is a visible decision rather than a silent one.</item>
/// <item><see cref="ApplyRequired"/> — the action was promised as a change to the TXT. Failing to
/// compile one aborts the whole plan, before anything is written.</item>
/// </list>
///
/// <para>A silent downgrade would be the worst of the three: the user would not learn that the thing
/// they asked for did not happen, and no later check would catch it — the tree and the source would
/// still agree with each other, so every consistency test would pass while the book stayed wrong.</para>
/// </summary>
public enum SourceEffectDecision
{
    /// <summary>This action does not need to touch the source at all.</summary>
    None,
    /// <summary>Touch the source if a safe edit can be compiled; otherwise land in the tree and say so.</summary>
    ApplyIfAvailable,
    /// <summary>Touch the source, or fail the whole plan.</summary>
    ApplyRequired,
}

/// <summary>What actually happened, recorded per action so the receipt can tell the two apart.</summary>
public enum SourceEffectOutcome
{
    /// <summary>The action kind never touches the source.</summary>
    NotApplicable,
    /// <summary>The user or the policy chose not to touch the source.</summary>
    NotRequested,
    /// <summary>A source operation covers this action.</summary>
    Applied,
    /// <summary>A source edit was requested but none could be compiled; landed in the tree only.</summary>
    Downgraded,
}

/// <summary>
/// What an action kind is *allowed* to do to the source, decided by the action itself and not by the
/// user.
///
/// <para>This exists to stop a specific mistake: if <c>InsertHeading</c> and <c>RetitleChapter</c>
/// defaulted to "apply if available", a user who explicitly chose to edit the source could end up with
/// a TXT that still has no heading and no title change — the feature they asked for quietly not
/// happening. Choosing to edit the source is a promise, and these actions keep it.</para>
/// </summary>
public enum SourceEffectPolicy
{
    /// <summary>The action kind does not involve the source.</summary>
    NotApplicable,
    /// <summary>Nice to have; may be downgraded without breaking the user's intent.</summary>
    Optional,
    /// <summary>In <see cref="RepairLandingMode.EditSource"/> this must change the source, or the plan fails.</summary>
    RequiredInEditSource,
    /// <summary>Only changes the source when the user explicitly asks for it.</summary>
    ExplicitOptIn,
}

/// <summary>Which product the user is building: the tree alone, or the tree plus a corrected TXT.</summary>
public enum RepairLandingMode
{
    /// <summary>The chapter tree changes; the TXT is left exactly as it is.</summary>
    TreeOnly,
    /// <summary>The chapter tree changes and the TXT is rewritten to match.</summary>
    EditSource,
}

/// <summary>
/// Maps an action kind to what it may do to the source, and combines that with the landing mode and the
/// user's explicit opt-ins to produce the decision for one action.
///
/// <para>The mapping lives here rather than in the window so that the policy and the confirmation UI
/// cannot disagree about what a checkbox means.</para>
/// </summary>
public static class SourceEffectPolicies
{
    public static SourceEffectPolicy For(ReferenceActionKind kind) => kind switch
    {
        // 标题已在原文里，收录它不需要动 TXT。
        ReferenceActionKind.AddChapter => SourceEffectPolicy.NotApplicable,
        // 只报告，没有可执行的动作。
        ReferenceActionKind.MissingBody => SourceEffectPolicy.NotApplicable,
        ReferenceActionKind.KeepExtra => SourceEffectPolicy.NotApplicable,
        // 卷层级完全建立在章节树上；原文里没有卷标题行可改。
        ReferenceActionKind.VolumeNote => SourceEffectPolicy.NotApplicable,
        // 把已有的一行解释为标题：只有需要规范化行文本时才动原文。
        ReferenceActionKind.AdoptHeading => SourceEffectPolicy.Optional,
        // 把标题并回正文在树上只是取消边界，文字本来就在正文流里。
        ReferenceActionKind.DemoteExtra => SourceEffectPolicy.Optional,
        // 用户要求标题改成 X，原文标题不同就该改。选了改原文却不动 TTTX 等于没做。
        ReferenceActionKind.Retitle => SourceEffectPolicy.RequiredInEditSource,
        // 重复副本默认只从成品排除；物理删除必须由用户明确要求。
        ReferenceActionKind.RemoveDuplicate => SourceEffectPolicy.ExplicitOptIn,
        _ => SourceEffectPolicy.NotApplicable,
    };

    /// <summary>
    /// The decision for one action.
    ///
    /// <para><paramref name="deleteDuplicateFromSource"/> is the user's answer to the second question the
    /// confirmation window asks about a duplicate copy. It can only ever make the effect *stronger*: a
    /// policy of <see cref="SourceEffectPolicy.RequiredInEditSource"/> is never weakened to
    /// <see cref="SourceEffectDecision.ApplyIfAvailable"/>, because that would be the user quietly
    /// giving up on what they asked for. A reader who does not want the TXT touched chooses the
    /// <see cref="RepairLandingMode.TreeOnly"/> mode, which is what that mode is for.</para>
    /// </summary>
    public static SourceEffectDecision Decide(ReferenceActionKind kind, RepairLandingMode mode,
        bool deleteDuplicateFromSource = false)
    {
        var policy = For(kind);
        if (policy == SourceEffectPolicy.NotApplicable) return SourceEffectDecision.None;
        if (mode == RepairLandingMode.TreeOnly)
        {
            // TreeOnly means the TXT is not touched, for every action without exception. This is not a
            // weakening of a policy — it is the mode the user picked.
            return SourceEffectDecision.None;
        }
        return policy switch
        {
            SourceEffectPolicy.Optional => SourceEffectDecision.ApplyIfAvailable,
            SourceEffectPolicy.RequiredInEditSource => SourceEffectDecision.ApplyRequired,
            SourceEffectPolicy.ExplicitOptIn => deleteDuplicateFromSource
                ? SourceEffectDecision.ApplyRequired
                : SourceEffectDecision.None,
            _ => SourceEffectDecision.None,
        };
    }

    /// <summary>
    /// Whether the confirmation window must offer the second question for this action kind. Only the
    /// kinds whose policy is <see cref="SourceEffectPolicy.ExplicitOptIn"/> need it.
    /// </summary>
    public static bool NeedsExplicitOptIn(ReferenceActionKind kind) =>
        For(kind) == SourceEffectPolicy.ExplicitOptIn;
}

/// <summary>
/// One action's stable identity.
///
/// <para>Built from everything that changes what the action *does*, and from nothing that only changes
/// how it is described. The description is excluded on purpose: rewording a sentence in the report must
/// not invalidate a receipt or break the link between a plan and its journal.</para>
///
/// <para>The schema version is part of the hash, so adding a field to the payload changes every id
/// rather than silently reusing ids that meant something else.</para>
/// </summary>
public static class RepairActionIdentity
{
    /// <summary>Bumped whenever the payload below gains, loses or reorders a field.</summary>
    public const int ActionSchemaVersion = 1;

    public static string Compute(ReferenceAction action) => RepairFingerprint.Compute(
        RepairFingerprint.Number(ActionSchemaVersion),
        action.Kind.ToString(),
        RepairFingerprint.Number(action.Line),
        action.Title ?? "",
        action.EntryId ?? "",
        action.Reference?.Title ?? "",
        action.Reference?.VolumeTitle ?? "",
        // Recommended is part of the identity because it is what decides whether the box starts ticked;
        // two actions that differ only there are different defaults, not the same action.
        RepairFingerprint.Flag(action.Recommended));

    /// <summary>
    /// A proposal-level identity over the ordered action ids, so two proposals differ whenever their
    /// action sets do.
    ///
    /// <para>Hashing the base version and the basis alone is not enough: importing directory A and then
    /// directory B for the same book with the same tree and the same rules produces identical inputs by
    /// that measure but completely different actions.</para>
    /// </summary>
    public static string ComputeProposal(string basis, RepairBaseVersion baseVersion, IEnumerable<string> orderedActionIds) =>
        RepairFingerprint.Compute(
            basis,
            baseVersion.BaseSourceSha256,
            baseVersion.BaseTreeFingerprint,
            baseVersion.RecognitionFingerprint,
            RepairFingerprint.ComputeLines(orderedActionIds));
}

/// <summary>
/// A repair plan bound to the exact book state it was computed against, together with the identity of
/// its action set.
///
/// <para>This is the "one complete preview" the confirmation window shows. It is deliberately not
/// called a candidate: a candidate sounds like one of several alternative fixes, and users are not
/// choosing between competing proposals — they are choosing which rows of one proposal to accept.</para>
/// </summary>
public sealed record RepairProposal(
    string Id,
    string Basis,
    RepairBaseVersion BaseVersion,
    ReferencePlan Plan,
    string? ReferenceCatalogFingerprint = null)
{
    public IReadOnlyList<ReferenceAction> Actions => Plan.Actions;

    /// <summary>Action ids in plan order. The order is part of the proposal identity.</summary>
    public IReadOnlyList<string> OrderedActionIds =>
        Actions.Select(RepairActionIdentity.Compute).ToArray();

    /// <summary>The decision every action starts with, before the user changes anything.</summary>
    public IReadOnlyList<RepairDecision> DefaultDecisions(RepairLandingMode mode,
        bool deleteDuplicateFromSource = false) =>
        Actions.Select(action => new RepairDecision(
            RepairActionIdentity.Compute(action),
            Plan.DefaultSelection.Contains(action),
            SourceEffectPolicies.Decide(action.Kind, mode, deleteDuplicateFromSource))).ToArray();
}

/// <summary>Builds proposals, so the id is never assembled by hand at a call site.</summary>
public static class RepairProposalFactory
{
    public static RepairProposal Create(string basis, RepairBaseVersion baseVersion, ReferencePlan plan,
        ReferenceCatalog? catalog = null)
    {
        var ids = plan.Actions.Select(RepairActionIdentity.Compute).ToArray();
        return new RepairProposal(
            RepairActionIdentity.ComputeProposal(basis, baseVersion, ids),
            basis,
            baseVersion,
            plan,
            ReferenceCatalogFingerprint.Compute(catalog));
    }
}
