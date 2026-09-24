namespace EasyPub.Core;

/// <summary>Splits body ranges in reading order, retaining the selected line in the new chapter.</summary>
public sealed record ChapterContentSplit(
    IReadOnlyList<ChapterSourceRange> Before,
    IReadOnlyList<ChapterSourceRange> After)
{
    public static ChapterContentSplit? At(IReadOnlyList<ChapterSourceRange> ranges, int line)
    {
        // Range order is meaningful after chapter moves and merges; never sort by source line.
        var index = -1;
        for (var i = 0; i < ranges.Count; i++)
            if (line >= ranges[i].StartLine && line <= ranges[i].EndLine)
            {
                index = i;
                break;
            }
        if (index < 0 || (index == 0 && line == ranges[0].StartLine)) return null;

        var before = ranges.Take(index).ToList();
        var selected = ranges[index];
        if (line > selected.StartLine) before.Add(new(selected.StartLine, line - 1));
        var after = new List<ChapterSourceRange> { new(line, selected.EndLine) };
        after.AddRange(ranges.Skip(index + 1));
        return new(before, after);
    }
}
