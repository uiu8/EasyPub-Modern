using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// The one-line summary drawn between two chapter rows. Kept out of the window so it can be asserted
/// directly, because it reads fields a breakpoint may legitimately leave empty: a NumberGap downgraded by
/// <see cref="ChapterBreakpoints"/> for an implausible run carries no numbers at all, and formatting it as
/// if it did threw ArgumentOutOfRangeException from a dispatcher callback — which takes the window down
/// while simply opening the book.
/// </summary>
internal static class ChapterBreakpointLabel
{
    internal static string For(IReadOnlyList<ChapterBreakpoint> items)
    {
        var parts = new List<string>();
        if (items.FirstOrDefault(item => item.Kind == ChapterBreakpointKind.NumberGap) is { } gap)
            parts.Add(gap.MissingNumbers.Count switch
            {
                0 => "此处编号对不上",
                1 => $"此处跳过：缺第 {gap.MissingNumbers[0]} 章",
                _ => $"此处跳过：缺第 {gap.MissingNumbers[0]}–{gap.MissingNumbers[^1]} 章",
            });
        if (items.Any(item => item.Kind == ChapterBreakpointKind.HeadingTypo))
            parts.Add("编号写法有误");
        if (items.FirstOrDefault(item => item.Kind == ChapterBreakpointKind.ReferenceMissing) is { } reference)
            parts.Add(reference.MissingTitles.Count == 1
                ? $"目录里有「{reference.MissingTitles[0]}」"
                : $"目录里有 {reference.MissingTitles.Count} 章找不到");
        return parts.Count == 0 ? "" : "↕ " + string.Join(" · ", parts);
    }
}
