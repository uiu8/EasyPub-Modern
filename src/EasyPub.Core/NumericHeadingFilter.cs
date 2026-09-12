namespace EasyPub.Core;

internal static class NumericHeadingFilter
{
    // Measure against the original candidates, not an already merged list: short
    // numbered lists must not grow into artificial chapters as candidates vanish.
    internal static HashSet<int> AcceptedLines(IReadOnlyList<string> lines, IReadOnlyList<int> headings, int minimum)
    {
        var accepted = new HashSet<int>();
        for (var i = 0; i < headings.Count; i++)
        {
            var start = headings[i];
            var end = i + 1 < headings.Count ? headings[i + 1] : lines.Count;
            var count = 0;
            for (var line = start + 1; line < end && count < minimum; line++)
                if (!string.IsNullOrWhiteSpace(lines[line]) && !TextCleanupPipeline.IsRemovedLine(lines[line])) count++;
            if (count >= minimum) accepted.Add(start);
        }
        return accepted;
    }
}
