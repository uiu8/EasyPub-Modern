using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// What the review card offers for one issue. Kept out of the window so the mapping can be asserted
/// directly: the rule is that a button must name an action that actually helps with *this* problem, and a
/// reminder with no such action must not fall back to one that looks plausible and does nothing —
/// "编辑成品标题…" under a skipped chapter is how a user ends up editing a title to fix a missing chapter.
/// </summary>
internal static class ChapterIssueAction
{
    /// <summary>Nothing can be repaired before the trade-off is explained, so the card only navigates.</summary>
    internal static bool IsInformational(string? code) => code is "chapter_structure_suggested" or "chapter_repeated_sequence" or "chapter_numbering_variants";

    /// <summary>
    /// The label for a reminder with no dedicated repair flow. <paramref name="canSplit"/> says whether the
    /// selected row can become a chapter, which is the one repair "漏识别" offers locally.
    /// </summary>
    internal static string ForGeneric(string? category, string? code, bool canSplit, string? suggestedTitle)
    {
        // A wrong chapter number has one exact repair and it beats every generic offer below.
        if (suggestedTitle is not null) return "修改为：" + suggestedTitle;
        // Volume, level and number-order mismatches are resolved through the actions menu, never by retitling.
        if (code is "chapter_number_order" or "volume_number_duplicate" or "chapter_level_gap") return "核对层级处理…";
        if (category == ReviewCategories.Missing)
            return canSplit ? "从选中行建立章节" : "到原文定位并核对…";
        return "编辑成品标题…";
    }
}
