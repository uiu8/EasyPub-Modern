namespace EasyPub.Core;

/// <summary>
/// Presentation grouping only: does not consume or change diagnostics. Maps a preflight issue onto
/// the same category axis the chapter workbench uses, so the two surfaces never disagree.
/// </summary>
public static class IssueCategory
{
    public static string For(ConversionPreflightIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        // **章节类问题码的归类只有一处定义**（IssueResolutionPolicy.All）。
        //
        // 这里原来是第二份手写映射，而两份并不一致：报告窗口漏了 chapter_cross_title_duplicate /
        // chapter_numbering_variants / reference_chapter_absent（三个新码全部落进「其他提醒」），
        // 工作台则把 chapter_number_gap 归成「编号与层级」而不是「漏识别」。
        // 一个码在两处显示成两类，用户会合理地怀疑这是两件事。
        if (ForCode(issue.Code) is { } known) return known;
        // 非章节类的检查项（封面、制作设置、文件、清理计划…）不在那张表里，按目标归类。
        return issue.Target switch
        {
            PreflightTargetKind.TextCleanup => "广告 / 文本清理",
            PreflightTargetKind.Chapters => ReviewCategories.Other,
            _ => "文件 / 制作设置",
        };
    }

    /// <summary>
    /// 只看问题码的归类。章节类码返回表里的类别，其余返回 null —— **不猜**。
    /// 工作台给每个分组定类别时走这里，于是"一个码属于哪一类"全软件只有一处定义。
    /// </summary>
    public static string? ForCode(string? issueCode) => IssueResolutionPolicy.Define(issueCode)?.Category;
}
