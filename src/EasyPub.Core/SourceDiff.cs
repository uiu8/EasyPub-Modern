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

        var operations = new List<SourceOperation>();
        var matched = Walk(from, to, (sourceLine, index) =>
        {
            var text = to.Lines[index].Text;
            // An inserted line is anchored to the old line it follows, which is what makes its position
            // unambiguous when the same text appears more than once. Nothing consumed yet means it goes in
            // front of line 1.
            var anchor = sourceLine > 0
                ? (SourceAnchor)new AfterOriginalLine(sourceLine)
                : new BeforeOriginalLine(1);
            operations.Add(new InsertLineAtAnchor(anchor, text)
            {
                OperationId = "ins-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ActionId = "restore",
            });
        });

        // A line of the old text that nothing matched has to go.
        for (var line = 1; line <= from.Lines.Count; line++)
        {
            if (matched.Contains(line)) continue;
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
