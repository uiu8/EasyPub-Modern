namespace EasyPub.Core;

/// <summary>
/// Projects a repair's three-dimensional effects into the physical patch that carries them out.
///
/// <para>The design calls the patches a projection of the effects, and this is that projection made
/// concrete: the same effects compile to the same operations every time, so "the landing mode only
/// decides which set of products to use" is a fact about the code rather than a description of it.</para>
///
/// <para>Nothing is invented here. A dimension that says "unchanged" produces no operation, and a
/// dimension that says "unknown" produces a refusal — the compiler's job is to carry out what was
/// decided, not to fill in what was not.</para>
/// </summary>
public static class RepairSourcePatchCompiler
{
    /// <summary>
    /// The operations one action's effect asks for.
    ///
    /// <para>Returns empty for an effect that does not touch the text. That is not a failure: dropping a
    /// duplicate copy from the finished book is exactly that case, and it is the default answer the whole
    /// confirmation window is built around.</para>
    /// </summary>
    public static IReadOnlyList<SourceOperation> Project(string actionId, RepairEffect effect)
    {
        switch (effect.Source)
        {
            case SourceContentUnchanged:
                return [];
            case SourceContentReplace replace:
                return replace.Replacements
                    .Select(intent => (SourceOperation)new ReplaceOriginalLine(intent.Line,
                        intent.ExpectedOldText, intent.NewText)
                    {
                        OperationId = $"rep-{intent.Line}",
                        ActionId = actionId,
                    })
                    .ToArray();
            case SourceContentDelete delete:
                return delete.Spans
                    .SelectMany(span => Enumerable.Range(span.Start, Math.Max(0, span.End - span.Start))
                        .Select(line => (SourceOperation)new DeleteOriginalLine(line, "")
                        {
                            // The expected text is filled in by the caller, which is the only side that
                            // can read the file. Leaving it empty here would silently disable the
                            // precondition check.
                            OperationId = $"del-{line}",
                            ActionId = actionId,
                        }))
                    .ToArray();
            case SourceContentInsert insert:
                return insert.Inserts
                    .Select((intent, index) => (SourceOperation)new InsertLineAtAnchor(intent.Anchor, intent.Text)
                    {
                        OperationId = $"ins-{index}",
                        ActionId = actionId,
                    })
                    .ToArray();
            case SourceContentIndeterminate:
                return [];
            default:
                return [];
        }
    }

    /// <summary>
    /// Fills in the text each operation expects to find, by reading the source.
    ///
    /// <para>A delete whose expected text is blank would match anything, which turns the precondition check
    /// into a formality. This is where that text comes from.</para>
    /// </summary>
    public static IReadOnlyList<SourceOperation> WithExpectedText(IEnumerable<SourceOperation> operations,
        Func<int, string?> lineText) =>
        operations.Select(operation => operation switch
        {
            DeleteOriginalLine delete when delete.ExpectedOldText.Length == 0 =>
                delete with { ExpectedOldText = lineText(delete.Line) ?? "" },
            _ => operation,
        }).ToArray();

    /// <summary>
    /// Which actions the decisions select, in plan order. Ordered by the plan rather than by the
    /// decisions, so a caller that hands them over in a different order still gets the same patch.
    /// </summary>
    public static IReadOnlyList<ReferenceAction> SelectedActions(RepairProposal proposal,
        IReadOnlyList<RepairDecision> decisions)
    {
        var selected = decisions.Where(decision => decision.Selected)
            .Select(decision => decision.ActionId)
            .ToHashSet(StringComparer.Ordinal);
        return proposal.Actions
            .Where(action => selected.Contains(RepairActionIdentity.Compute(action)))
            .ToArray();
    }
}
