using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// The strip text is built from fields a breakpoint may legitimately leave empty. A NumberGap downgraded
/// for an implausible run of missing numbers carries no numbers at all, and formatting it as though it did
/// crashed the window on open — these tests pin every shape the detector can actually produce.
/// </summary>
public class ChapterBreakpointLabelTests
{
    private static ChapterBreakpoint Gap(params int[] missing) =>
        new(ChapterBreakpointKind.NumberGap, "a", "b", 401, missing, [], "detail");

    private static ChapterBreakpoint Reference(params string[] titles) =>
        new(ChapterBreakpointKind.ReferenceMissing, "a", "b", 688, [], titles, "detail");

    private static ChapterBreakpoint Typo() =>
        new(ChapterBreakpointKind.HeadingTypo, "a", "b", 409, [], [], "detail");

    [Fact]
    public void A_downgraded_gap_with_no_numbers_does_not_throw()
    {
        // Every open of 择天记-class books reaches this: the gap is announced without naming numbers.
        var label = ChapterBreakpointLabel.For([Gap()]);
        Assert.Contains("编号对不上", label);
    }

    [Fact]
    public void A_gap_names_one_chapter_or_a_range()
    {
        Assert.Contains("缺第 689 章", ChapterBreakpointLabel.For([Gap(689)]));
        Assert.Contains("缺第 402–411 章", ChapterBreakpointLabel.For([Gap(402, 403, 411)]));
    }

    [Fact]
    public void Reference_titles_are_named_when_the_directory_has_one()
    {
        Assert.Contains("目录里有「第六百八十九章 奥秘」", ChapterBreakpointLabel.For([Reference("第六百八十九章 奥秘")]));
        Assert.Contains("目录里有 3 章找不到", ChapterBreakpointLabel.For([Reference("甲", "乙", "丙")]));
    }

    [Fact]
    public void Several_breaks_at_one_row_are_summarised_together()
    {
        var label = ChapterBreakpointLabel.For([Gap(689), Reference("第六百八十九章 奥秘")]);
        Assert.Contains("缺第 689 章", label);
        Assert.Contains("第六百八十九章 奥秘", label);
        Assert.StartsWith("↕", label);
    }

    [Fact]
    public void A_row_with_nothing_to_report_shows_no_strip()
    {
        Assert.Equal("", ChapterBreakpointLabel.For([]));
    }

    [Fact]
    public void A_typo_and_a_gap_can_coexist()
    {
        var label = ChapterBreakpointLabel.For([Typo(), Gap(411)]);
        Assert.Contains("编号写法有误", label);
        Assert.Contains("缺第 411 章", label);
    }
}
