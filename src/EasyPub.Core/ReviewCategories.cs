namespace EasyPub.Core;

/// <summary>
/// The single classification axis for chapter review. A category names the problem the reader has
/// to judge, not the code that detected it. Button behaviour is dispatched by
/// <see cref="IssueResolutionPolicy"/> from the issue code plus the current context — never by
/// category — so renaming or merging a category can never silently disable an action.
///
/// <para>哪一个码属于哪一类，只有 <see cref="IssueResolutionPolicy.All"/> 一处定义
/// （经 <see cref="IssueCategory"/> 暴露）。这里只提供五个类别名本身。</para>
/// </summary>
public static class ReviewCategories
{
    /// <summary>Content exists in the text but never became a chapter.</summary>
    public const string Missing = "漏识别";

    /// <summary>Body text was mistaken for a chapter heading.</summary>
    public const string Misrecognized = "误识别";

    /// <summary>The same content appears more than once.</summary>
    public const string Duplicate = "重复";

    /// <summary>Chapter number, volume or level does not line up.</summary>
    public const string Structure = "编号与层级";

    /// <summary>Reported for completeness; needs a human decision every time.</summary>
    public const string Other = "其他提醒";

    /// <summary>Filter order shown in the workbench.</summary>
    public static IReadOnlyList<string> All => [Missing, Misrecognized, Duplicate, Structure, Other];
}
