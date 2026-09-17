namespace EasyPub.Core;

/// <summary>
/// Derives the operations that turn one version of a text into another.
///
/// <para>A repair produces its own patch, but a <b>restore</b> does not: the target is a version that was
/// saved earlier, and the only two things in hand are the current text and that one. Working out the
/// operations from the two texts is what lets a restore go through exactly the same migration path as a
/// repair — one coordinate map, one set of rebinding rules, one place where emptiness is refused.</para>
///
/// <para>The result is deliberately the <b>cheapest</b> script, not merely a working one: a version saved
/// three edits ago differs from the current one in a handful of lines, and a script that replaced all
/// three thousand of them would report every chapter as "its heading line was rewritten" and throw away
/// identities that never changed. The longest common subsequence is what keeps the untouched lines
/// untouched.</para>
///
/// <para>A diff that would cost more than the ceiling is reported as "everything differs" rather than
/// attempted: on a pair of books that genuinely share nothing the answer is the same, and the caller gets
/// that answer instead of a quadratic blow-up.</para>
/// </summary>
public static class SourceDiff
{
    /// <summary>Upper bound on the subsequence table. 2500×2500 is 6.25M cells, comfortably inside it.</summary>
    public const long MaximumCells = 40_000_000L;

    /// <summary>
    /// The operations that turn <paramref name="from"/> into <paramref name="to"/>, keyed by lines of
    /// <paramref name="from"/>.
    /// </summary>
    public static IReadOnlyList<SourceOperation> Operations(SourceTextDocument from, SourceTextDocument to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);

        // The new lines that have no original are collected rather than turned into operations here: whether
        // one of them is really an <b>edit</b> of a line depends on the lines after it, and that is only known
        // once the walk has finished.
        var inserts = new List<(int Anchor, int Index)>();
        var matched = Walk(from, to, (anchor, index) => inserts.Add((anchor, index)));

        var operations = new List<SourceOperation>();
        var replaced = new HashSet<int>();

        // One batch of inserts — the ones anchored after the same old line — sits where a run of consecutive
        // old lines was dropped. The first min(inserted, dropped) of each are paired up line by line and
        // written as <b>replacements</b> rather than as a delete plus an insert.
        //
        // Not for brevity. A delete plus an insert leaves the coordinate map with no answer for that line, so
        // every chapter whose body contained it loses it: measured on a book where one body sentence was
        // edited outside the program, that chapter came back as "no body left" and its identity was dropped.
        // A replacement keeps the line, which is what actually happened to it.
        foreach (var group in inserts.GroupBy(insert => insert.Anchor).OrderBy(group => group.Key))
        {
            var anchor = group.Key;
            var batch = group.OrderBy(insert => insert.Index).ToArray();

            // The old lines this batch landed among: the unmatched run that starts right after the anchor.
            var dropped = new List<int>();
            for (var line = anchor + 1; line <= from.Lines.Count && !matched.Contains(line); line++)
                dropped.Add(line);

            var paired = 0;
            // The batch at the very top of the file is never paired. "Insert before line 1" and "replace line
            // 1" are not the same position to the renderer: the insert takes the default terminator, while the
            // replacement keeps the terminator the old line had — and on an empty file that line has none, so
            // pairing there glues the first two lines together.
            if (anchor > 0)
                while (paired < batch.Length && paired < dropped.Count)
                {
                    var line = dropped[paired];
                    operations.Add(new ReplaceOriginalLine(line, from.Lines[line - 1].Text,
                        to.Lines[batch[paired].Index].Text)
                    {
                        OperationId = "rep-" + line.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ActionId = "restore",
                    });
                    replaced.Add(line);
                    paired++;
                }

            if (paired == batch.Length) continue;

            // More new lines than dropped ones: the rest are genuine insertions, and they belong <b>after the
            // last line this batch replaced</b>. Anchoring them at the original anchor would place them before
            // the replacements, and the file would come out reordered; anchoring them at one of the dropped
            // lines is worse — the renderer skips a deleted line and its trailing inserts with it, so they
            // would vanish from the output altogether.
            //
            // Nothing was paired only at the very top of the file, and there the anchor is where they go.
            var at = paired > 0 ? dropped[paired - 1] : anchor;
            for (var rest = paired; rest < batch.Length; rest++)
                operations.Add(new InsertLineAtAnchor(
                    at > 0 ? new AfterOriginalLine(at) : new BeforeOriginalLine(1), to.Lines[batch[rest].Index].Text)
                {
                    // Zero-padded so the renderer's ordinal sort of same-anchor inserts is the text's order:
                    // "ins-10" sorts before "ins-9", and a batch of ten is an ordinary batch.
                    OperationId = "ins-" + batch[rest].Index.ToString("D6",
                        System.Globalization.CultureInfo.InvariantCulture),
                    ActionId = "restore",
                });
        }

        // A line of the old text that nothing matched, and that no replacement already claimed, has to go.
        for (var line = 1; line <= from.Lines.Count; line++)
        {
            if (matched.Contains(line) || replaced.Contains(line)) continue;
            operations.Add(new DeleteOriginalLine(line, from.Lines[line - 1].Text)
            {
                OperationId = "del-" + line.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ActionId = "restore",
            });
        }

        return operations;
    }

    /// <summary>The patch a restore needs. Empty when the two versions are the same text.</summary>
    public static SourcePatch Patch(SourceTextDocument from, SourceTextDocument to, string actionSetId = "restore") =>
        new(actionSetId, "derived", Operations(from, to));

    /// <summary>
    /// Pairs up the new text's lines with the old text's, longest common subsequence first, calling
    /// <paramref name="onInserted"/> for each new line that has no original.
    /// </summary>
    private static HashSet<int> Walk(SourceTextDocument from, SourceTextDocument to,
        Action<int, int> onInserted)
    {
        var rows = from.Lines.Count;
        var columns = to.Lines.Count;
        var matched = new HashSet<int>();

        // Too large to align: report every new line as unmatched. The caller then inserts the whole target
        // and deletes the whole original, which is a correct script for two texts that share nothing.
        if ((long)rows * columns > MaximumCells)
        {
            for (var index = 0; index < columns; index++) onInserted(0, index);
            return matched;
        }

        // suffixes[i, j] = length of the longest common subsequence of from[i..] and to[j..].
        var suffixes = new int[rows + 1, columns + 1];
        for (var i = rows - 1; i >= 0; i--)
            for (var j = columns - 1; j >= 0; j--)
                suffixes[i, j] = string.Equals(from.Lines[i].Text, to.Lines[j].Text, StringComparison.Ordinal)
                    ? suffixes[i + 1, j + 1] + 1
                    : Math.Max(suffixes[i + 1, j], suffixes[i, j + 1]);

        // Walk forward, following the table. Every line of the new text is emitted once, in order, so the
        // loop is driven by j: it either consumes a matching old line, or becomes an insert. Old lines that
        // no new line matched are simply never consumed, and become the deletions.
        int row = 0, column = 0, lastConsumed = 0;
        while (column < columns)
        {
            if (row < rows && string.Equals(from.Lines[row].Text, to.Lines[column].Text, StringComparison.Ordinal))
            {
                lastConsumed = row + 1;
                matched.Add(lastConsumed);
                row++;
                column++;
                continue;
            }
            // Move in the direction that keeps the subsequence longest. Ties consume an old line, so the
            // script reads as "this line was removed" before "this line was added".
            if (row < rows && suffixes[row + 1, column] >= suffixes[row, column + 1])
            {
                row++;
                continue;
            }
            onInserted(lastConsumed, column);
            column++;
        }
        return matched;
    }
}
