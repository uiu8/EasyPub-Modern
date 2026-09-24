namespace EasyPub.Core;

/// <summary>
/// What pressing the button would do, worked out without doing any of it.
///
/// <para>The confirmation window shows this: the counts, the affected lines, and — for a mode that edits
/// the TXT — exactly which lines of the reader's file would change. The window and the applier answer from
/// the same call, so the numbers on screen cannot disagree with what happens next.</para>
/// </summary>
public sealed record RepairApplicationPreview(
    bool CanApply,
    RepairLandingMode Mode,
    string Message,
    int TreeChanges,
    int SourceOperations,
    IReadOnlyList<int> AffectedLines,
    bool SourceWillChange,
    IReadOnlyList<RepairCompileProblem> Problems,
    RepairVersionCheck Version)
{
    public static RepairApplicationPreview Blocked(RepairLandingMode mode, RepairVersionCheck version,
        string message, IReadOnlyList<RepairCompileProblem>? problems = null) =>
        new(false, mode, message, 0, 0, [], false, problems ?? [], version);
}

/// <summary>What one repair did, in the terms a person acted in.</summary>
public sealed record RepairApplicationResult(
    bool Changed,
    RepairLandingMode Mode,
    string Message,
    RepairCompilation? Compilation = null,
    SourceEditResult? Transaction = null,
    SourceTransitionResult? Transition = null)
{
    /// <summary>
    /// The state to carry on with. Set when the tree moved onto a new version, so the caller does not have to
    /// work out for itself which plan and which tree the book now has.
    /// </summary>
    public SourceTransitionState? State { get; init; }

    /// <summary>Which saved chapter identities did not survive the move onto the new text.</summary>
    public IReadOnlyList<SourceEntryRebind> LostIdentities => Transition?.Lost ?? [];

    public bool NeedsAttention => Transaction is { NeedsAttention: true }
        || Transition is { Succeeded: false };
}

/// <summary>
/// Carries out a repair the user confirmed — the one entry both the button and the tests go through.
///
/// <para>This exists because "the button does what the code does" was the requirement, and the only way to
/// make that true rather than hopeful is for there to be one thing to do. Before it, the window applied a
/// tree in memory and nothing anywhere could write the TXT; the transaction, the migration and the manifest
/// were reachable from a test harness and from nowhere else.</para>
///
/// <para>The two modes really do differ, and only in <b>which products are used</b>:</para>
///
/// <list type="bullet">
/// <item><see cref="RepairLandingMode.TreeOnly"/> — the tree changes. No transaction exists for it, and by
/// the design's invariant none ever should: there is nothing to journal because no file changes.</item>
/// <item><see cref="RepairLandingMode.EditSource"/> — the tree changes and the TXT is rewritten. The source
/// patch is rendered, checked for losslessness, frozen into an execution manifest, and carried out by
/// <see cref="SourceEditTransaction"/>; afterwards <see cref="SourceVersionTransition"/> moves the tree onto
/// the new text.</item>
/// </list>
///
/// <para>An action does not behave differently between them. That was the defect the whole refactor set out
/// to remove, and it is why both modes go through the same <see cref="RepairPlanCompiler"/>.</para>
/// </summary>
public sealed class ChapterRepairApplier
{
    private readonly string _transactionRoot;
    private readonly Func<ChapterTreeDocument, ChapterTreePlan?, CancellationToken, Task<string?>> _backupPathFor;
    private readonly TimeProvider _clock;

    /// <summary>
    /// <paramref name="backupPathFor"/> decides where the pre-edit copy goes, and is handed <b>both</b> the
    /// text and the tree the reader arranged.
    ///
    /// <para>The tree is part of it because a snapshot without one cannot later be restored as a full book
    /// state — the interface can only offer 「恢复原文与当时的章节树」 for versions that saved both. While this
    /// callback took a path and nothing else, the one caller there was had nothing to pass and every snapshot
    /// came out bytes-only, so that option existed in the restore layer and could never be chosen.</para>
    ///
    /// <para>When it is null the backup layer is used with the transaction root as its directory — the same
    /// folder the journal goes in. A second answer here would be a second backup policy.</para>
    /// </summary>
    public ChapterRepairApplier(string transactionRoot,
        Func<ChapterTreeDocument, ChapterTreePlan?, CancellationToken, Task<string?>>? backupPathFor = null,
        TimeProvider? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionRoot);
        _transactionRoot = Path.GetFullPath(transactionRoot);
        _backupPathFor = backupPathFor ?? DefaultBackup;
        _clock = clock ?? TimeProvider.System;
    }

    private Task<string?> DefaultBackup(ChapterTreeDocument document, ChapterTreePlan? tree,
        CancellationToken token) =>
        Task.FromResult<string?>(new SourceBackupStore(_transactionRoot).EnsureSnapshot(document, tree));

    /// <summary>
    /// Whether the plan can still be applied, and what it would do — from the repair outcome the confirmation
    /// window is already holding.
    ///
    /// <para>This is the overload the window calls on every checkbox change. It rebuilds the proposal from the
    /// same outcome and the same tree the apply path will rebuild it from, so the numbers on screen come from
    /// the same inputs as the work.</para>
    /// </summary>
    public RepairApplicationPreview Preview(AutoRepairOutcome outcome, ChapterTreeDocument document,
        RepairLandingMode mode, IReadOnlyCollection<ReferenceAction> chosen, bool buildVolumeLevels = false,
        string? chapterPattern = null)
    {
        var proposal = ProposalFor(outcome, document);
        return proposal is null
            ? RepairApplicationPreview.Blocked(mode, RepairVersionCheck.Ok, "这次没有可用的修复方案。")
            : Preview(proposal, document, DecisionsFor(proposal, mode, chosen), mode, chapterPattern,
                buildVolumeLevels);
    }

    /// <summary>
    /// Does it, from the repair outcome and the user's ticks.
    ///
    /// <para>Same inputs as the preview above, so "what the window showed" and "what happened" cannot come
    /// from two different plans.</para>
    /// </summary>
    public async Task<RepairApplicationResult> ApplyAsync(AutoRepairOutcome outcome, ChapterTreeDocument document,
        RepairLandingMode mode, IReadOnlyCollection<ReferenceAction> chosen, bool buildVolumeLevels = false,
        string? chapterPattern = null, ChapterTreeDocumentCache? cache = null,
        TextEncodingMode encodingMode = TextEncodingMode.Auto, CancellationToken token = default)
    {
        var proposal = ProposalFor(outcome, document);
        if (proposal is null)
            return new RepairApplicationResult(false, mode, "这次没有可用的修复方案。");
        return await ApplyAsync(proposal, document, DecisionsFor(proposal, mode, chosen), mode, chapterPattern,
            buildVolumeLevels, cache, encodingMode, token).ConfigureAwait(false);
    }

    /// <summary>
    /// The proposal the apply path will use, built from the outcome.
    ///
    /// <para>Built here rather than taken from the window's callback so that a caller replaying a repair from
    /// a test drives exactly the same object.</para>
    /// </summary>
    public static RepairProposal? ProposalFor(AutoRepairOutcome outcome, ChapterTreeDocument document)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentNullException.ThrowIfNull(document);
        if (outcome.Plan is null) return null;
        var version = RepairBaseVersionFactory.Capture(document.SourceSha256, document.Entries,
            document.RecognitionOptions, document.ChapterPattern);
        // The basis says where the plan came from, so a receipt can tell "aligned against a downloaded
        // directory" from "derived from the book itself". They are different claims about the same repair.
        var basis = outcome.CatalogFound ? "ReferenceCatalog" : SelfRepairPlanner.BasisName;
        return RepairProposalFactory.Create(basis, version, outcome.Plan, outcome.Catalog);
    }

    /// <summary>
    /// The user's ticks turned into decisions: every action in the plan, marked selected or not, with the
    /// effect its kind's policy allows in this mode.
    ///
    /// <para>Decisions are produced for <b>all</b> actions rather than only the ticked ones, because the
    /// manifest records what was decided about each — and "not selected" is a decision.</para>
    /// </summary>
    public static IReadOnlyList<RepairDecision> DecisionsFor(RepairProposal proposal, RepairLandingMode mode,
        IReadOnlyCollection<ReferenceAction> chosen)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var selected = chosen.Select(action => action.Key).ToHashSet(StringComparer.Ordinal);
        return proposal.Actions.Select(action => new RepairDecision(
            RepairActionIdentity.Compute(action),
            selected.Contains(action.Key),
            SourceEffectPolicies.Decide(action.Kind, mode))).ToArray();
    }

    /// <summary>
    /// Whether the plan can still be applied to the book in front of it, and what it would do.
    ///
    /// <para>Pure: it compiles and renders but writes nothing, so the window can call it on every checkbox
    /// change.</para>
    /// </summary>
    public RepairApplicationPreview Preview(RepairProposal proposal, ChapterTreeDocument document,
        IReadOnlyList<RepairDecision> decisions, RepairLandingMode mode,
        string? chapterPattern = null, bool buildVolumeLevels = false)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(decisions);

        // 1. Does the plan still describe this book? A plan that has drifted describes a book that no
        //    longer exists, and applying it would produce a tree nobody previewed.
        var version = RepairPlanValidator.Validate(proposal.BaseVersion, document,
            document.Entries, chapterPattern);
        if (!version.IsValid)
            return RepairApplicationPreview.Blocked(mode, version, version.Message);

        // 2. Can the decisions be carried out at all? An action promised as a source edit that cannot
        //    produce one fails the whole plan rather than landing in the tree only.
        var compilation = Compile(proposal, document, decisions, mode, buildVolumeLevels);
        var check = RepairDecisionCompiler.Check(proposal, decisions, action => CanEditSource(action, document));
        if (!compilation.CanApply)
            return RepairApplicationPreview.Blocked(mode, version, compilation.Describe(), compilation.Problems);

        var problems = compilation.Problems.Concat(check.Problems).ToArray();
        if (problems.Length > 0)
            return RepairApplicationPreview.Blocked(mode, version,
                "这些动作无法执行：" + string.Join("；", problems.Select(Describe)), problems);

        // 3. For a mode that rewrites the TXT, render it now and prove the render is lossless. Doing this
        //    here is what lets the window say "12 lines of your file will change" instead of "the TXT will
        //    be rewritten" — and it is the same render the transaction will write.
        var affected = new List<int>();
        var willChange = false;
        if (mode == RepairLandingMode.EditSource && compilation.SourcePatch is { } patch)
        {
            var original = SourceTextDocument.Load(document.SourcePath);
            var lossless = SourceRenderVerifier.Verify(original, original.Render());
            if (!lossless.IsLossless)
                return RepairApplicationPreview.Blocked(mode, version, lossless.Message);

            affected.AddRange(patch.Operations.Select(operation => operation switch
            {
                DeleteOriginalLine delete => delete.Line,
                ReplaceOriginalLine replace => replace.Line,
                InsertLineAtAnchor insert => insert.Anchor switch
                {
                    BeforeOriginalLine before => before.Line,
                    AfterOriginalLine after => after.Line,
                    _ => 0,
                },
                _ => 0,
            }).Where(line => line > 0));
            willChange = !patch.IsEmpty;
        }

        return new RepairApplicationPreview(true, mode,
            compilation.Describe(), compilation.TreePatch?.Mutations.Count ?? 0,
            compilation.SourcePatch?.Operations.Count ?? 0,
            affected.Distinct().Order().ToArray(), willChange, problems, version);
    }

    /// <summary>
    /// Does it.
    ///
    /// <para>For <see cref="RepairLandingMode.EditSource"/> the order is fixed and each step is there for a
    /// reason: compile into products, take the backup, render beside the source, freeze the manifest, replace
    /// atomically, then move the tree onto the new text. Nothing here decides <i>what</i> to write — that was
    /// decided when the user pressed the button, and the manifest is the record of it.</para>
    /// </summary>
    public async Task<RepairApplicationResult> ApplyAsync(RepairProposal proposal, ChapterTreeDocument document,
        IReadOnlyList<RepairDecision> decisions, RepairLandingMode mode, string? chapterPattern = null,
        bool buildVolumeLevels = false, ChapterTreeDocumentCache? cache = null,
        TextEncodingMode encodingMode = TextEncodingMode.Auto, CancellationToken token = default)
    {
        var preview = Preview(proposal, document, decisions, mode, chapterPattern, buildVolumeLevels);
        if (!preview.CanApply)
            return new RepairApplicationResult(false, mode, preview.Message);

        // The file has to be the version the tree describes before anything is compiled against it. The
        // transaction checks this too, but it can only say "something outside this program changed it" —
        // and the usual cause is this program, having already applied this very repair. Saying so is the
        // difference between "regenerate the plan" and "go and look at your file".
        if (!File.Exists(document.SourcePath))
            return new RepairApplicationResult(false, mode, "原文不存在，未做任何改动。");
        var onDisk = SourceFileHasher.HashOf(document.SourcePath);
        if (!string.Equals(onDisk, document.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return new RepairApplicationResult(false, mode,
                "原文已经不是这份章节树对应的那一版（可能刚刚应用过同一次修复）。请重新打开书稿并重新生成修复方案。");

        var compilation = Compile(proposal, document, decisions, mode, buildVolumeLevels);

        // TreeOnly never creates a transaction. That is not an optimisation: no file changes, so a journal
        // for it would be a record of nothing, and "the TXT is left exactly as it is" is the whole promise of
        // the mode.
        if (mode == RepairLandingMode.TreeOnly)
            return TreeResult(compilation, mode);

        if (compilation.SourcePatch is not { } patch || compilation.ExpectedResult is null)
            return new RepairApplicationResult(false, mode, "这次方案没有任何文本操作，未创建事务。", compilation);
        if (patch.IsEmpty)
            return TreeResult(compilation, mode);

        var original = SourceTextDocument.Load(document.SourcePath);
        var rendered = SourcePatchRenderer.Render(original, patch);
        var newSha = SourceFileHasher.HashOfRendered(original, rendered.Text);
        if (string.Equals(newSha, document.SourceSha256, StringComparison.OrdinalIgnoreCase))
            return TreeResult(compilation, mode);

        // Validate the exact confirmed tree before committing any bytes.
        var coordinates = SourceCoordinateMap.Build(original, patch, rendered.Text);
        var confirmedPlan = MaterialiseConfirmedPlan(document.CreatePlan(compilation.RebuiltEntries),
            coordinates, patch, newSha);

        var before = new SourceTransitionState(
            document.SourcePath,
            original,
            document,
            document.CreatePlan(document.Entries),
            null,
            null,
            null)
        {
            LoadedHash = document.SourceSha256,
            RenderedHash = document.SourceSha256,
        };

        // Taken before anything is written, and given the tree as well as the text: a snapshot with no tree
        // cannot be restored as a full book state, so that option would never be offered for a version this
        // program saved itself.
        var backupPath = await _backupPathFor(document, before.Plan, token).ConfigureAwait(false);

        SourceTransitionResult? transition = null;
        ChapterTreePlan? finalPlan = null;
        var transaction = new SourceEditTransaction(_transactionRoot, document.SourcePath, clock: _clock,
            afterReplace: async (_, inner) =>
            {
                transition = await SourceVersionTransition
                    .AfterReplaceAsync(before, patch, rendered.Text, document.SourcePath, cache, encodingMode,
                        oldCatalogPath: null, token: inner)
                    .ConfigureAwait(false);
                if (!transition.Succeeded)
                    throw new InvalidDataException("原文已替换，但版本迁移失败：" + transition.Message);
                finalPlan = confirmedPlan;
            });

        var manifest = BuildManifest(proposal, decisions, document, patch, compilation, original, newSha, backupPath);
        var edit = await transaction.ExecuteAsync(manifest, original, rendered.Text, backupPath, token)
            .ConfigureAwait(false);

        if (!edit.Succeeded)
            return new RepairApplicationResult(false, mode, edit.Message, compilation, edit, transition);

        var state = transition?.State;
        if (state is not null && finalPlan is not null)
            state = state with { Plan = finalPlan };

        var lost = transition?.Lost ?? [];
        return new RepairApplicationResult(true, mode,
            $"章节树与原文都已更新：{compilation.TreePatch?.Mutations.Count ?? 0} 条树变化，"
            + $"{patch.Operations.Count} 个文本操作（改 {rendered.Replaced}、删 {rendered.Deleted}、插 {rendered.Inserted} 行）"
            + (lost.Count == 0 ? "。" : $"；{lost.Count} 个章节身份没能跟过来。"),
            compilation, edit, transition)
        {
            State = state,
        };
    }

    private static RepairApplicationResult TreeResult(RepairCompilation compilation, RepairLandingMode mode)
    {
        var changed = compilation.TreePatch is { IsEmpty: false };
        return new RepairApplicationResult(changed, mode, changed
            ? "章节树已更新，原文未改动；没有创建文本事务。"
            : "章节树与原文均无变化。", compilation);
    }

    private static RepairCompilation Compile(RepairProposal proposal, ChapterTreeDocument document,
        IReadOnlyList<RepairDecision> decisions, RepairLandingMode mode, bool buildVolumeLevels) =>
        RepairPlanCompiler.Compile(new RepairCompileInput(proposal, document, decisions, mode,
            UseReferenceTitles: true, BuildVolumeLevels: buildVolumeLevels));

    /// <summary>
    /// Whether a source edit can be compiled for one action.
    ///
    /// <para>Two ways an action can reach the TXT, and asking only the first one was a defect:
    /// <see cref="RepairEffectCompiler.Describe"/> covers the actions whose effect is stated per action
    /// (retitling, folding a heading back), while a duplicate copy's physical deletion is stated by
    /// <see cref="RepairEffectCompiler.DescribeDuplicateDeletion"/> — a separate entry point, because the
    /// lines to delete have to be named rather than inferred. Asking <c>Describe</c> alone therefore answered
    /// "cannot edit the source" for every <see cref="SourceEffectPolicy.ExplicitOptIn"/> kind, and the whole
    /// plan was refused: a user who asked for a duplicate to leave the TXT was told the repair could not be
    /// applied at all.</para>
    /// </summary>
    private static bool CanEditSource(ReferenceAction action, ChapterTreeDocument document)
    {
        var effect = RepairEffectCompiler.Describe(action, RepairEffectCompiler.ChapterNumberOf(action),
            lineText: line => document.SourceLine(line)?.Text);
        if (RepairSourcePatchCompiler.Project(RepairActionIdentity.Compute(action), effect).Count > 0)
            return true;

        // The explicit-opt-in kinds are the ones whose removal effect is computed elsewhere, from the located
        // copies rather than from the action alone.
        return SourceEffectPolicies.NeedsExplicitOptIn(action.Kind);
    }

    /// <summary>
    /// The tree the book has after an edit, built from the tree the user confirmed rather than re-recognised.
    ///
    /// <para>Re-running recognition would produce a tree that depends on the rules in force now, and would
    /// throw away every manual arrangement the user had made — the exact loss
    /// <see cref="SourceVersionTransition"/> was built to avoid. Map the complete confirmed tree before
    /// writing; the later transition still reports old identities that could not follow. Never replay
    /// mutations by title or discard confirmed additions because they were absent from the old tree.
    /// </para>
    /// </summary>
    private static ChapterTreePlan MaterialiseConfirmedPlan(ChapterTreePlan confirmed,
        SourceCoordinateMap map, SourcePatch sourcePatch, string newSha)
    {
        // Index inserted headings by their original anchor; never search the new book
        // by title, because different volumes can have identical chapter titles.
        var insertedHeadings = sourcePatch.Operations.OfType<InsertLineAtAnchor>()
            .Where(op => op.Anchor is BeforeOriginalLine)
            .ToLookup(op => (((BeforeOriginalLine)op.Anchor).Line, op.Text));
        int? MapLine(int line) => line == map.OriginalLineCount + 1
            ? map.NewLineCount + 1 : map.TryMap(line);
        var entries = new List<ChapterTreeEntry>(confirmed.Entries.Count);
        foreach (var entry in confirmed.Entries)
        {
            int? heading = null;
            if (entry.TitleLineNumber is { } oldHeading)
            {
                var inserts = insertedHeadings[(oldHeading, entry.Title)].ToArray();
                if (inserts.Length > 1) throw new InvalidDataException("章节标题的插入位置不唯一。");
                heading = inserts.Length == 1 ? map.LineOfOperation(inserts[0].OperationId) : MapLine(oldHeading);
                if (heading is null) throw new InvalidDataException("确认保留的章节标题被源补丁删除，未写入原文。");
            }
            var ranges = new List<ChapterSourceRange>();
            foreach (var range in entry.ContentRanges)
                for (var line = range.StartLine; line <= range.EndLine; line++)
                {
                    if (MapLine(line) is not { } mapped) continue;
                    if (ranges.Count > 0 && ranges[^1].EndLine + 1 == mapped)
                        ranges[^1] = ranges[^1] with { EndLine = mapped };
                    else ranges.Add(new ChapterSourceRange(mapped, mapped));
                }
            entries.Add(entry with { TitleLineNumber = heading, ContentRanges = ranges });
        }
        var plan = confirmed with { SourceSha256 = newSha, Entries = entries };
        ChapterTreeDocument.ValidatePlan(plan, map.NewLineCount + 1);
        return plan;
    }

    private static RepairExecutionManifest BuildManifest(RepairProposal proposal,
        IReadOnlyList<RepairDecision> decisions, ChapterTreeDocument document,
        SourcePatch patch, RepairCompilation compilation, SourceTextDocument original, string newSha,
        string? backupPath)
    {
        // What each action actually did to the text, looked up in the patch that will be applied. A
        // conditional request with no operation behind it is recorded as downgraded rather than as applied:
        // the receipt has to be able to say which rows did not change the TXT while the ones beside them did.
        var outcomes = RepairDecisionCompiler.OutcomesFor(proposal, decisions, patch);
        var byId = decisions.ToDictionary(decision => decision.ActionId, StringComparer.Ordinal);

        var snapshots = proposal.Actions.Select((action, index) =>
        {
            var id = RepairActionIdentity.Compute(action);
            var decision = byId.TryGetValue(id, out var found)
                ? found
                : new RepairDecision(id, proposal.Plan.DefaultSelection.Contains(action),
                    SourceEffectPolicies.Decide(action.Kind, compilation.Mode));
            return new RepairDecisionSnapshot(id, action.Kind, action.Title ?? "", decision.Selected,
                decision.SourceEffect, outcomes.ElementAtOrDefault(index), action.Line > 0 ? action.Line : null,
                null);
        }).ToArray();

        return new RepairExecutionManifest(
            RepairExecutionManifest.CurrentSchemaVersion,
            "repair",
            proposal.Id,
            proposal.BaseVersion,
            snapshots,
            compilation.TreePatch ?? new TreePatch(patch.ActionSetId, [], compilation.ExpectedResult!),
            patch,
            compilation.ExpectedResult!,
            newSha,
            document.SourceSha256,
            compilation.Mode,
            backupPath,
            original.DefaultNewLine);
    }

    private static string Describe(RepairCompileProblem problem) => problem.Reason switch
    {
        RepairCompileProblemKind.NotExecutable => $"「{problem.Title}」只是发现，不能执行",
        RepairCompileProblemKind.SourceEditUnavailable => $"「{problem.Title}」要求改原文，但编不出安全的改动",
        RepairCompileProblemKind.UnknownAction => "方案里没有这个动作",
        _ => problem.Reason.ToString(),
    };
}
