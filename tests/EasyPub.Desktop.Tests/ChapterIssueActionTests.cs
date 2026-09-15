using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// The review card is the only place a reminder turns into an action, so each category has to offer one
/// that fits. The case that prompted these tests: a skipped chapter was offered "编辑成品标题…" — a
/// plausible-looking button that cannot restore a missing chapter, and the batch entry for skipped
/// stretches was only reachable through a menu.
/// </summary>
public class ChapterIssueActionTests
{
    [Fact]
    public void A_missing_chapter_never_offers_a_title_edit()
    {
        // 漏识别 can only be repaired by turning a source row into a chapter.
        Assert.Equal("从选中行建立章节", ChapterIssueAction.ForGeneric(ReviewCategories.Missing, "chapter_unrecognized", canSplit: true, suggestedTitle: null));
        // Without a row there is nothing to build, so the button must not promise a repair.
        Assert.DoesNotContain("建立", ChapterIssueAction.ForGeneric(ReviewCategories.Missing, "chapter_unrecognized", canSplit: false, suggestedTitle: null));
        Assert.DoesNotContain("编辑", ChapterIssueAction.ForGeneric(ReviewCategories.Missing, "chapter_unrecognized", canSplit: false, suggestedTitle: null));
    }

    [Fact]
    public void A_numbering_problem_without_a_correction_offers_the_check_that_fits_it()
    {
        // No neighbouring chapters to infer a number from, so the only honest offer is the actions menu.
        foreach (var code in new[] { "chapter_number_order", "volume_number_duplicate", "chapter_level_gap" })
            Assert.Equal("核对层级处理…", ChapterIssueAction.ForGeneric(ReviewCategories.Structure, code, canSplit: true, suggestedTitle: null));
    }

    [Fact]
    public void A_wrong_title_still_offers_the_corrected_one()
    {
        // 417 → 第四百零十八章 → 419 has an exact correction, which must outrank the generic "check levels".
        Assert.Equal("修改为：第三百章 归来", ChapterIssueAction.ForGeneric(ReviewCategories.Structure, "chapter_number_order", canSplit: false, suggestedTitle: "第三百章 归来"));
    }

    [Fact]
    public void Every_category_reachable_in_the_workbench_gets_a_label()
    {
        foreach (var category in new[]
                 {
                     ReviewCategories.Missing, ReviewCategories.Misrecognized,
                     ReviewCategories.Duplicate, ReviewCategories.Structure, ReviewCategories.Other,
                 })
            Assert.False(string.IsNullOrWhiteSpace(ChapterIssueAction.ForGeneric(category, "whatever", canSplit: true, suggestedTitle: null)));
    }

    [Fact]
    public void The_suppressed_after_alignment_codes_stay_in_step_with_the_analyzer()
    {
        // The workbench groups by IssueCategory, so a code added to the analyzer without a category would
        // silently land in 其他提醒. This pins the ones the chapter surfaces actually show.
        var expected = new Dictionary<string, string>
        {
            ["chapter_unrecognized"] = ReviewCategories.Missing,
            ["chapter_heading_typo"] = ReviewCategories.Missing,
            ["chapter_unnumbered"] = ReviewCategories.Missing,
            ["numeric_chapters_suspected"] = ReviewCategories.Missing,
            ["chapter_number_gap"] = ReviewCategories.Missing,
            ["numeric_body_group"] = ReviewCategories.Misrecognized,
            ["chapter_repeated_sequence"] = ReviewCategories.Duplicate,
            ["chapter_content_duplicate"] = ReviewCategories.Duplicate,
            ["chapter_duplicate"] = ReviewCategories.Duplicate,
            ["chapter_number_order"] = ReviewCategories.Structure,
            ["volume_number_duplicate"] = ReviewCategories.Structure,
            ["chapter_level_gap"] = ReviewCategories.Structure,
            ["chapter_structure_suggested"] = ReviewCategories.Structure,
        };
        foreach (var (code, category) in expected)
        {
            var issue = new ConversionPreflightIssue("/tmp/book.txt", PreflightSeverity.Warning, code, "message",
                PreflightTargetKind.Chapters, 1);
            Assert.Equal(category, IssueCategory.For(issue));
        }
    }
}
