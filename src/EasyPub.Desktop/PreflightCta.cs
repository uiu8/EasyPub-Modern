using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// 转换前检查窗与任务中心里那个按钮说的话。
///
/// <para><b>这个表面只能跳转</b>（<c>NavigateToPreflightIssue</c> → 打开章节工作台并定位），
/// 所以每条文案都以「在工作台…」起头：它承诺的是"到了工作台你会看到这个入口"，
/// 而不是"点这里就会发生这件事"。原来的文案没有这层区分 ——「对比并删除重复正文」
/// 读起来像是这一下就会删掉一份，而工作台里的同一件事写的是「对比正文，选择保留…」，
/// 两处对**要不要删**给出了相反的预期。</para>
///
/// <para>非章节类的检查项（封面、制作设置、文件…）没有章节入口，退回按目标归类的通用说法。</para>
/// </summary>
internal static class PreflightCta
{
    internal static string For(ConversionPreflightIssue issue)
    {
        ArgumentNullException.ThrowIfNull(issue);
        // 「工作台数出的章数」与「转换会产出的章数」不一致。它不在 IssueResolutionPolicy 的表里
        // （那张表是**章节树里可处理的问题**），所以不走下面的通用兜底 ——
        // 否则这里会说「处理章节」，而用户真正要做的是保存章节树。
        if (issue.Code == PreflightIssueCodes.RepeatedHeadingSplit) return "在工作台保存章节树";
        var advice = IssueResolutionPolicy.Evaluate(issue);
        if (advice.HasChapterActions) return For(advice.Recommended);
        // 信息级的清理计划只是"看一眼确认"，与"去清理广告"不是一回事，措辞要分开。
        if (issue.Target == PreflightTargetKind.TextCleanup && issue.Severity == PreflightSeverity.Information)
            return "查看清理计划";
        return ReadinessEvaluator.ActionLabel(issue.Target);
    }

    internal static string For(ResolutionAction? action) => action?.Intent switch
    {
        ResolutionIntent.NavigateToSource => "在工作台核对原文",
        ResolutionIntent.CompareDuplicateBodies => "在工作台对比这两份",
        ResolutionIntent.ApplySuggestedTitle => "在工作台采用建议标题",
        ResolutionIntent.EditTreeTitle => "在工作台编辑标题",
        ResolutionIntent.ReviewHierarchy => "在工作台核对层级",
        ResolutionIntent.PromoteSelectedLine => "在工作台建立章节",
        ResolutionIntent.RestoreAsBody => "在工作台还原为正文",
        ResolutionIntent.CleanDuplicateTitles => "在工作台清理重复标题",
        ResolutionIntent.RepairMissingHeadings => "在工作台预览补建",
        ResolutionIntent.RepairUnnumberedHeadings => "在工作台预览补建",
        ResolutionIntent.RecoverStructure => "在工作台预览结构整理",
        ResolutionIntent.ReRecognizeAsNumeric => "在工作台重新识别",
        ResolutionIntent.AcquireCatalog => "在工作台获取参考目录",
        ResolutionIntent.AdoptCatalogChapters => "在工作台按目录补建",
        _ => "打开章节工作台处理",
    };
}
