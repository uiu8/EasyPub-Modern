namespace EasyPub.Core;

/// <summary>
/// Presentation grouping only: does not consume or change diagnostics. Maps a preflight issue onto
/// the same category axis the chapter workbench uses, so the two surfaces never disagree.
/// </summary>
public static class IssueCategory
{
    public static string For(ConversionPreflightIssue issue) => issue.Code switch
    {
        "chapter_unrecognized" or "chapter_heading_typo" or "chapter_unnumbered"
            or "numeric_chapters_suspected" or "chapter_number_gap" => ReviewCategories.Missing,
        "numeric_body_group" => ReviewCategories.Misrecognized,
        "chapter_repeated_sequence" or "chapter_content_duplicate" or "chapter_duplicate" => ReviewCategories.Duplicate,
        "chapter_number_order" or "volume_number_duplicate" or "chapter_level_gap"
            or "chapter_structure_suggested" => ReviewCategories.Structure,
        _ when issue.Code.Contains("volume") || issue.Code.Contains("level") => ReviewCategories.Structure,
        _ when issue.Target == PreflightTargetKind.TextCleanup => "广告 / 文本清理",
        _ when issue.Target == PreflightTargetKind.Chapters => ReviewCategories.Other,
        _ => "文件 / 制作设置",
    };
}
