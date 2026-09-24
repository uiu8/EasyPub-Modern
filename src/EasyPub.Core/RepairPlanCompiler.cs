namespace EasyPub.Core;

/// <summary>What a set of decisions compiles to.</summary>
public sealed record RepairCompilation(
    bool CanApply,
    TreePatch? TreePatch,
    SourcePatch? SourcePatch,
    CanonicalChapterModel? ExpectedResult,
    IReadOnlyList<RepairCompileProblem> Problems,
    IReadOnlyList<SourcePatchProblem> SourceProblems,
    RepairLandingMode Mode,
    IReadOnlyList<int> RemovedLines)
{
    /// <summary>Confirmed tree in the original source coordinates; use only when no TXT rewrite occurred.</summary>
    public IReadOnlyList<ChapterTreeEntry> RebuiltEntries { get; init; } = [];
    /// <summary>True when a mode that promises to edit the TXT actually produced edits for it.</summary>
    public bool SourceChanged => SourcePatch is { IsEmpty: false };

    public string Describe() => CanApply
        ? $"可应用：{TreePatch?.Mutations.Count ?? 0} 条树变化"
          + (SourceChanged ? $"，{SourcePatch!.Operations.Count} 个文本操作" : "，不动原文")
        : "不可应用：" + string.Join("；",
            Problems.Select(problem => $"{problem.Kind}（{problem.Title}）")
                .Concat(SourceProblems.Select(problem => $"{problem.Rule}: {problem.Detail}")));
}

/// <summary>Everything <see cref="RepairPlanCompiler"/> needs. Passed as one object so the signature does
/// not grow a parameter every time the compiler learns something new.</summary>
public sealed record RepairCompileInput(
    RepairProposal Proposal,
    ChapterTreeDocument Document,
    IReadOnlyList<RepairDecision> Decisions,
    RepairLandingMode Mode,
    bool UseReferenceTitles = true,
    bool BuildVolumeLevels = false);

/// <summary>
/// Turns a proposal plus the user's decisions into the products that carry them out.
///
/// <para><b>Pure.</b> It reads the document, computes, and returns; it writes nothing and changes nothing.
/// That is what lets the confirmation window show exactly what pressing the button would do, and lets the
/// same call be repeated to prove it produces the same answer.</para>
///
/// <para>The mode decides <i>which products are used</i>, never what an action means. <c>TreeOnly</c>
/// compiles the tree patch and leaves the source patch unbuilt; <c>EditSource</c> compiles both. An action
/// does not behave differently between them — that was the defect the design set out to remove.</para>
/// </summary>
public static class RepairPlanCompiler
{
    public static RepairCompilation Compile(RepairCompileInput input)
    {
        var proposal = input.Proposal;
        var document = input.Document;

        // 1. Which actions the user selected.
        var selected = RepairSourcePatchCompiler.SelectedActions(proposal, input.Decisions);

        // 2. Whether those decisions can be carried out at all. An action that promised to edit the
        //    source, in a mode that edits the source, must be able to — otherwise the whole thing fails
        //    rather than quietly landing in the tree only.
        var effects = new List<(ReferenceAction Action, RepairEffect Effect)>();
        var context = new RepairEffectContext(document, proposal.Plan);
        foreach (var action in selected)
        {
            var effect = RepairEffectCompiler.Describe(action,
                RepairEffectCompiler.ChapterNumberOf(action), ownership: null,
                line => document.SourceLine(line)?.Text, context);
            effects.Add((action, effect));
        }

        var sourceOperations = new List<SourceOperation>();
        var compileProblems = new List<RepairCompileProblem>();
        var decisionsById = input.Decisions.ToDictionary(decision => decision.ActionId, StringComparer.Ordinal);

        foreach (var (action, initialEffect) in effects)
        {
            var effect = initialEffect;
            var actionId = RepairActionIdentity.Compute(action);
            var decision = decisionsById.GetValueOrDefault(actionId);
            if (decision is null) continue;

            if (input.Mode == RepairLandingMode.TreeOnly)
            {
                // The mode says the TXT is not touched. Nothing more to compile for this action.
                continue;
            }

            // An action whose source effect is opt-in has <b>two</b> answers, and Describe states the
            // conservative one: "drop the duplicate copy from the finished book, leave the text alone".
            // When the user asks for the stronger answer — the lines really leaving the TXT — the effect is
            // stated by DescribeDuplicateDeletion instead, which needs the located copies named.
            //
            // Without this branch the compiler saw "promised a source edit, projected no operation" and
            // refused the whole plan, so a reader who ticked "并从原文删除这一份" was told the repair could
            // not be applied at all.
            if (effect.Source is SourceContentUnchanged
                && decision.SourceEffect == SourceEffectDecision.ApplyRequired
                && SourceEffectPolicies.NeedsExplicitOptIn(action.Kind))
            {
                var removed = context.RemovedBy(action);
                if (removed.Count > 0)
                    effect = RepairEffectCompiler.DescribeDuplicateDeletion(action, removed.Order().ToArray());
            }

            if (effect.Source is SourceContentIndeterminate)
            {
                // Cannot say what this would do to the text. Refuse rather than guess.
                compileProblems.Add(new RepairCompileProblem(actionId, action.Title, action.Kind,
                    RepairCompileProblemKind.SourceEditUnavailable));
                continue;
            }

            var projected = RepairSourcePatchCompiler.Project(actionId, effect);
            if (projected.Count == 0 && decision.SourceEffect == SourceEffectDecision.ApplyRequired)
            {
                // Promised an edit and produced none: the one outcome that must not pass silently.
                compileProblems.Add(new RepairCompileProblem(actionId, action.Title, action.Kind,
                    RepairCompileProblemKind.SourceEditUnavailable));
                continue;
            }
            sourceOperations.AddRange(projected);
        }

        if (compileProblems.Count > 0)
            return new RepairCompilation(false, null, null, null, compileProblems, [], input.Mode, []);

        // 3. Build the tree. This is the existing applier, unchanged: the new layer decides *whether* to
        //    apply, not *how* — rewriting how the tree is built would put every existing guarantee at risk
        //    for no gain.
        var dropped = new List<int>();
        var rebuilt = ReferencePlanner.Apply(document, document.Entries, proposal.Plan, selected,
            input.UseReferenceTitles, input.BuildVolumeLevels, dropped);

        // 4. The source patch, normalised against the file it will be applied to.
        SourcePatch? sourcePatch = null;
        // ⚠️ **这个列表目前永远是空的，而这是安全的 —— 但它看起来像一道校验，容易被误判。**
        //
        // HANDOVER §8.6 第 1 条曾把它记成"形同虚设的校验"，那是**错的**：真正拦住不可应用补丁的是
        // 下面 SourcePatchNormalizer 的 validation，它失败时走 CanApply=false + Problems（第 5 个参数），
        // 与这个字段无关（实测 2026-09-18：全解决方案里 SourceProblems 只被 Describe() 读过）。
        //
        // 所以它是**一条从未被写入的提示通道**，不是一道漏掉的闸门。留着是因为
        // RepairCompilation 是公开记录，删字段会改位置签名；但下次有人怀疑它之前，
        // 先看这段：要加校验请加在 validation 那条路上，不要指望这里。
        var sourceProblems = new List<SourcePatchProblem>();
        if (input.Mode == RepairLandingMode.EditSource)
        {
            var withText = RepairSourcePatchCompiler.WithExpectedText(sourceOperations,
                line => document.SourceLine(line)?.Text);
            var validation = SourcePatchNormalizer.Normalize(proposal.Id, document.SourceSha256,
                withText, document.LineCount, line => document.SourceLine(line)?.Text);
            if (!validation.IsValid)
                return new RepairCompilation(false, null, null, null, [], validation.Problems, input.Mode, dropped);
            sourcePatch = validation.Patch;
        }

        // 5. What the tree is expected to be. Whoever carries the patch out can prove they built this.
        var canonical = CanonicalChapterModelFactory.Build(rebuilt, line => document.SourceLine(line)?.Text);
        var treePatch = new TreePatch(proposal.Id, BuildMutations(document, rebuilt, selected, dropped), canonical);

        return new RepairCompilation(true, treePatch, sourcePatch, canonical, [], sourceProblems, input.Mode, dropped)
            { RebuiltEntries = rebuilt };
    }

    /// <summary>
    /// The tree changes, derived by comparing what was there with what is there now.
    ///
    /// <para>Derived rather than declared because a declared list would be a second description of the same
    /// change, free to drift from what the applier actually did. A comparison cannot disagree with the
    /// result — it is computed from it.</para>
    ///
    /// <para>Matching is by <see cref="ChapterTreeEntry.StableKey"/>, not by id: a rebuild mints new GUIDs,
    /// so an id-based comparison between two compiles of the same decisions sees every entry as both new
    /// and removed. That is exactly how this failed the first time.</para>
    /// </summary>
    private static IReadOnlyList<ChapterTreeMutation> BuildMutations(ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry> rebuilt, IReadOnlyList<ReferenceAction> selected,
        IReadOnlyList<int> dropped)
    {
        var mutations = new List<ChapterTreeMutation>();
        var before = new Dictionary<string, ChapterTreeEntry>(StringComparer.Ordinal);
        foreach (var entry in document.Entries) before.TryAdd(entry.StableKey, entry);
        var after = rebuilt.Select(entry => entry.StableKey).ToHashSet(StringComparer.Ordinal);

        // An action that named a surviving line is the natural owner of a change to it; anything else is
        // owned by the repair as a whole. Without an owner the receipt could list changes but not their
        // reasons.
        string OwnerFor(int? line) => line is { } value
            ? selected.FirstOrDefault(action => action.Line == value) is { } match
                ? RepairActionIdentity.Compute(match)
                : "repair"
            : "repair";

        foreach (var entry in rebuilt)
        {
            var binding = entry.TitleLineNumber is { } line
                ? (SourceBinding)new OriginalLineBinding(line)
                : new NoSourceBinding();
            if (!before.TryGetValue(entry.StableKey, out var old))
            {
                mutations.Add(new InsertEntry(entry.StableKey, entry.Title, entry.Level, entry.IncludeInToc,
                    BodyAssignment.FromRanges(entry.ContentRanges))
                {
                    ActionId = OwnerFor(entry.TitleLineNumber),
                    Binding = binding,
                });
                continue;
            }
            if (old.Title != entry.Title)
                mutations.Add(new RetitleEntry(entry.StableKey, entry.Title)
                {
                    ActionId = OwnerFor(entry.TitleLineNumber),
                    Binding = binding,
                });
            if (old.Level != entry.Level)
                mutations.Add(new ChangeEntryLevel(entry.StableKey, entry.Level)
                {
                    ActionId = OwnerFor(entry.TitleLineNumber),
                    Binding = binding,
                });
            if (!SameLines(old.ContentRanges, entry.ContentRanges))
                mutations.Add(new ReassignEntryBody(entry.StableKey, BodyAssignment.FromRanges(entry.ContentRanges))
                {
                    ActionId = OwnerFor(entry.TitleLineNumber),
                    Binding = binding,
                });
        }

        // An entry that is gone. Whether its text stays is answered by the dropped lines: if none of its
        // body was dropped the text is still in the book, just under a different chapter.
        var droppedSet = dropped.ToHashSet();
        foreach (var entry in document.Entries)
        {
            if (after.Contains(entry.StableKey)) continue;
            var textStays = !entry.ContentRanges.Any(range =>
                Enumerable.Range(range.StartLine, Math.Max(0, range.EndLine - range.StartLine + 1))
                    .Any(droppedSet.Contains));
            mutations.Add(new RemoveEntry(entry.StableKey, textStays)
            {
                ActionId = OwnerFor(entry.TitleLineNumber),
                Binding = entry.TitleLineNumber is { } line
                    ? new OriginalLineBinding(line)
                    : new NoSourceBinding(),
            });
        }

        return mutations;
    }

    private static bool SameLines(IReadOnlyList<ChapterSourceRange> left, IReadOnlyList<ChapterSourceRange> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
            if (left[index].StartLine != right[index].StartLine || left[index].EndLine != right[index].EndLine)
                return false;
        return true;
    }
}
