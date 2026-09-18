using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// 工作台问题卡的 CTA 文案。
///
/// <para><b>它只说"这件事在工作台里叫什么"，不判断"该做什么"。</b>该做什么由
/// <see cref="IssueResolutionPolicy.Evaluate(ChapterReviewGroup, ChapterRepairContextSnapshot, IssueResolutionFacts)"/>
/// 从 finding + 上下文算出来；三个界面表面共用那一份语义，各自只说各自的话（设计文档 §5.4：
/// 统一的是解决意图，不是按钮字符串）。</para>
///
/// <para>这条分工不是形式。以前"文案"与"执行"是两套互不相干的 code switch，于是出现了
/// 按钮写着「编辑成品标题…」而点下去打开正文对比窗、写着「到原文定位并核对…」而点下去
/// 把选中行建立成章节这类错配。现在两者都从**同一个 intent** 出发，错配在结构上不可能出现。</para>
/// </summary>
internal static class ChapterIssueAction
{
    /// <summary>
    /// 把一个动作说成工作台里的按钮。
    /// </summary>
    /// <param name="action">
    /// 策略算出的动作。为 null（这一类没有章节处理入口）时给一个中性说法。
    /// </param>
    /// <param name="hint">
    /// surface 能提供的补充，例如建议的标题、本组的处数。**它只让文案更具体，
    /// 不能改变动作是什么** —— 动作已经由 <paramref name="action"/> 决定了。
    /// </param>
    internal static string CtaFor(ResolutionAction? action, string? hint = null) => action?.Intent switch
    {
        ResolutionIntent.PromoteSelectedLine => "从选中行建立章节",
        ResolutionIntent.ApplySuggestedTitle => hint is null ? "采用建议的标题…" : "修改为：" + hint,
        ResolutionIntent.EditTreeTitle => "编辑成品标题…",
        ResolutionIntent.ReviewHierarchy => "核对层级处理…",
        ResolutionIntent.NavigateToSource => "到原文定位并核对…",
        ResolutionIntent.CompareDuplicateBodies => "对比正文，选择保留…",
        ResolutionIntent.CleanDuplicateTitles => hint is null ? "检查并清理本组…" : $"检查并清理{hint}…",
        ResolutionIntent.RestoreAsBody => "还原为上一章正文",
        // 补建跳章区间里的漏识别标题：有本组处数时说出来，让用户点之前就知道会改几处。
        ResolutionIntent.RepairMissingHeadings => hint is null ? "修复本组漏识别标题…" : $"补建{hint}漏识别标题…",
        ResolutionIntent.RepairUnnumberedHeadings => "预览补建 / 获取参考目录…",
        ResolutionIntent.RecoverStructure => "预览结构整理…",
        ResolutionIntent.ReRecognizeAsNumeric => "识别并规范标题",
        ResolutionIntent.AcquireCatalog => "获取参考目录并对照…",
        ResolutionIntent.AdoptCatalogChapters => "按目录补建章节…",
        _ => "处理章节…",
    };

    /// <summary>这条提醒是否只该给出"去看一眼"的动作 —— 卡片不承诺任何修改。</summary>
    internal static bool IsNavigationOnly(ResolutionAdvice advice) =>
        advice.Recommended?.Execution == ResolutionExecutionKind.Navigation;
}
