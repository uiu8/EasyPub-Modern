namespace EasyPub.Core;

/// <summary>
/// 解决**意图** —— 三个界面表面之间的公共词汇表。
///
/// <para><b>它统一的是业务语义，不是按钮字符串。</b>同一个 intent 在报告窗口里可以是
/// 「打开章节工作台处理…」，在工作台里可以是「定位疑似标题…」—— 两个 CTA 本来就该不同，
/// 但它们必须指向**同一件事**。现在的问题不是文案不同，而是三处连语义都不同：
/// 有的跳转、有的就地执行、有的退化成「处理章节」兜底。</para>
///
/// <para>新增一个 intent 的代价是它必须**有真实的执行者** —— 这三个 surface 里至少有一处
/// 点下去真的会做这件事。没有执行者的 intent 是死代码，会变成新的"看起来合理、实际什么也不做"。</para>
/// </summary>
public enum ResolutionIntent
{
    /// <summary>这一类检查项没有章节处理入口（非章节类的问题码）。调用方自行兜底。</summary>
    Unknown,

    /// <summary>跳到原文里对应的行，由人核对。</summary>
    NavigateToSource,

    /// <summary>打开正文对比窗，看清两份的差异。</summary>
    CompareDuplicateBodies,

    /// <summary>采用建议的标题（已经算出确定答案时）。</summary>
    ApplySuggestedTitle,

    /// <summary>手工编辑某一章的标题。**这不是解决方案** —— 它只是"没有更好的办法"时的入口。</summary>
    EditTreeTitle,

    /// <summary>打开层级处理菜单（设为子章节 / 移出父章节）。</summary>
    ReviewHierarchy,

    /// <summary>把选中的原文行建立成章节。</summary>
    PromoteSelectedLine,

    /// <summary>把被误识别成章节的数字条目还原为上一章的正文。</summary>
    RestoreAsBody,

    /// <summary>清理同名章节里的重复项。</summary>
    CleanDuplicateTitles,

    /// <summary>补建跳章区间里疑似漏识别的标题（本地启发式，只在区间内找）。</summary>
    RepairMissingHeadings,

    /// <summary>补建异常长章里疑似无编号的标题（专项工具，可调行数阈值）。</summary>
    RepairUnnumberedHeadings,

    /// <summary>重建编号分组与结构（专项工具，先预览）。</summary>
    RecoverStructure,

    /// <summary>按数字标题规则重新识别本书。**会替换当前章节树**，不改原始 TXT。</summary>
    ReRecognizeAsNumeric,

    /// <summary>获取或选用一份参考目录。**它本身不修改任何东西。**</summary>
    AcquireCatalog,

    /// <summary>按已有的参考目录补建章节树里缺失的章。</summary>
    AdoptCatalogChapters,
}

/// <summary>
/// 这个动作**怎么执行** —— 没有它，Coordinator 和 UI 拿到 advice 后仍要 switch 猜行为，
/// 那只是把原来的三套 switch 从 <c>IssueCode</c> 换成了 <c>Intent</c>。
///
/// <para>注意 <see cref="Navigation"/> / <see cref="AcquireCatalog"/> / <see cref="ReRecognizeTree"/> /
/// <see cref="OpenSpecializedTool"/> **根本不存在** TreeOnly/EditSource 之分 ——
/// 只有 <see cref="GenerateRepairProposal"/> 才有落地模式，而且能否真的改原文要到预览时才算得出来。</para>
/// </summary>
public enum ResolutionExecutionKind
{
    /// <summary>定位到某一行，或打开一个只读的对比窗。</summary>
    Navigation,

    /// <summary>获取参考目录。不存在 TreeOnly / EditSource。</summary>
    AcquireCatalog,

    /// <summary>重新识别，直接替换章节树，不修改原始 TXT。</summary>
    ReRecognizeTree,

    /// <summary>打开一个专项处理工具。</summary>
    OpenSpecializedTool,

    /// <summary>生成修复方案供逐项勾选 —— 只有这一类才有落地模式。</summary>
    GenerateRepairProposal,
}

/// <summary>
/// 这个动作**若要执行，是否必须有参考目录**。
///
/// <para>不用 <c>bool</c> 是因为"需要目录"有四种强度，压成两值就表达不出
/// "有更好"与"没有就下不了结论"的区别。</para>
///
/// <para><b>边界</b>：它只描述**某一个具体动作**，不描述问题码。
/// 尤其 <see cref="RequiredForAutomaticAction"/> 只能挂在真正依赖目录的动作上 ——
/// 不能写成"<c>chapter_duplicate</c> 的安全自动删除需要目录"，因为本地的正文完全相同证据
/// 加上可靠保留目标，理论上也可能足够。</para>
/// </summary>
public enum CatalogRole
{
    /// <summary>完全不需要。</summary>
    None,

    /// <summary>有更好，没有也能做。</summary>
    Helpful,

    /// <summary>要下结论必须有（例如判定"缺的是哪几章"）。</summary>
    RequiredForConclusion,

    /// <summary>要自动执行必须有。</summary>
    RequiredForAutomaticAction,
}

/// <summary>这个动作需要人多深地参与。名称描述的是**软件做到哪一步**，不是风险。</summary>
public enum AutomationLevel
{
    /// <summary>点一下就完成，没有可选项。</summary>
    OneClick,

    /// <summary>先生成结果供逐项勾选，确认后才应用。</summary>
    Confirmed,

    /// <summary>软件只负责把材料摆出来，判断完全由人做。</summary>
    Manual,
}

/// <summary>动作的执行风险。用于决定文案要多重，以及是否需要额外确认。</summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>
/// **静态层**：只回答"这是什么问题、谁发现的"。
///
/// <para>它<b>不得</b>决定 CTA、目录需求、自动化程度、风险或解决器 —— 那些都依赖当前 finding
/// 与上下文，一律住在 <see cref="ResolutionAction"/> 上。</para>
///
/// <para><see cref="Category"/> 在这里是**唯一来源**：报告窗口原来用
/// <see cref="IssueCategory.For"/>、工作台原来用 <c>ChapterReviewAnalyzer</c> 各自算一遍，
/// 两套都漏了对方覆盖的码（新码在报告窗口落进「其他提醒」，而 <c>chapter_number_gap</c>
/// 在工作台被归成「编号与层级」）。现在两处都从这里取。</para>
/// </summary>
public sealed record IssueDefinition(string IssueCode, string Category, string Detector);

/// <summary>
/// 一个可执行的解决动作。**需求与风险挂在动作上，不挂在整条 advice 上** ——
/// 因为一条 finding 可以有多个动作，而它们的目录需求、自动化程度、风险常常完全不同。
///
/// <para>刻意<b>没有</b> <c>CanEditSource</c>：对
/// <see cref="ResolutionExecutionKind.Navigation"/> / <see cref="ResolutionExecutionKind.AcquireCatalog"/> /
/// <see cref="ResolutionExecutionKind.ReRecognizeTree"/> / <see cref="ResolutionExecutionKind.OpenSpecializedTool"/>
/// 根本不存在 TreeOnly/EditSource；而对 <see cref="ResolutionExecutionKind.GenerateRepairProposal"/>，
/// 能否改原文取决于本次方案里有哪些动作、用户勾了哪些、<c>SourceEffectDecision</c>、
/// 原文补丁能否编译、预检是否通过、源目录与备份是否可写、SHA 是否仍匹配 ——
/// 在策略阶段写一个 bool，很容易变成一个假承诺。</para>
/// </summary>
public sealed record ResolutionAction(
    ResolutionIntent Intent,
    ResolutionExecutionKind Execution,
    CatalogRole CatalogRole,
    AutomationLevel Automation,
    RiskLevel Risk,
    string Explanation);

/// <summary>一条 finding 在当前上下文下的完整建议。纯计算结果，不持有状态。</summary>
public sealed record ResolutionAdvice(
    IReadOnlyList<ResolutionAction> AvailableActions,
    ResolutionIntent? RecommendedIntent,
    string Summary)
{
    /// <summary>这一类问题是否有章节处理入口。为 false 时调用方走自己的兜底文案。</summary>
    public bool HasChapterActions => AvailableActions.Count > 0;

    /// <summary>被推荐的那个动作；没有可用动作时为 null。</summary>
    /// <exception cref="InvalidOperationException">
    /// 推荐了一个不在 <see cref="AvailableActions"/> 里的动作。这**只可能是编码错误**
    /// （推荐与动作在同一处构造），而静默返回 null 会让界面悄悄退化成"处理章节…"兜底 ——
    /// 那正是这次重构要消灭的东西，所以这里宁可响亮地失败。
    /// </exception>
    public ResolutionAction? Recommended =>
        RecommendedIntent is not { } intent
            ? null
            : AvailableActions.FirstOrDefault(a => a.Intent == intent)
              ?? throw new InvalidOperationException($"推荐的动作 {intent} 不在可用动作列表里。");

    /// <summary>没有章节处理入口时的空建议。</summary>
    public static ResolutionAdvice None(string summary) => new([], null, summary);
}

/// <summary>
/// **与这一条 finding 有关**的本地证据。
///
/// <para>它不是全书状态，所以不进 <see cref="ChapterRepairContextSnapshot"/> —— 那个快照回答的是
/// "这本书现在处于什么状况"，而这里回答的是"就这一条提醒而言，本地已经查到了什么"。
/// 混在一起会让快照变成什么都装的袋子，而它自己的契约写明了"不是新的可变真相"。</para>
///
/// <para>三个字段恰好是三处 surface 各自已经算过、但只有工作台知道的东西：报告窗口手里
/// 只有一条 <c>ConversionPreflightIssue</c>，没有文档也没有选中行，所以它传
/// <see cref="None"/>，拿到的就是"通用建议" —— 这<b>正好解释了为什么三个 surface 的 CTA
/// 本来就该不同</b>，而不是各写各的 switch。</para>
/// </summary>
/// <param name="HasLocalHeadingCandidate">
/// 本地有没有可补建的标题候选。**三态**：<c>true</c> 有、<c>false</c> 查过且没有、<c>null</c> 没查过。
///
/// <para>"没查过"必须与"查过没有"分开。报告窗口没有文档，无从知道本地有没有候选；
/// 把它当成 <c>false</c> 会让报告窗口对每一条跳章都推荐「获取参考目录」——
/// 而其中不少其实是本地漏识别，根本不需要目录。<b>那正是这份文档反复要消灭的假状态。</b></para>
/// </param>
public sealed record IssueResolutionFacts(
    bool? HasLocalHeadingCandidate = null,
    bool HasSuggestedTitle = false,
    bool CanSplitSelectedLine = false)
{
    public static IssueResolutionFacts None { get; } = new();
}

/// <summary>
/// <c>IssueCode + ChapterRepairContext + 本地证据 → ResolutionAdvice</c>。
///
/// <para><b>为什么不能用 <c>IssueCode → 一个静态答案</c></b>：同一个码在不同上下文下含义不同。
/// <c>chapter_number_gap</c> 在无目录时是"可能只是漏识别标题"，该先本地找候选；
/// 在有目录且已验证时是"目录里有这些章、原文里没定位到"，目录证据的意义强得多。
/// 所以 <c>NeedsCatalog</c> 不能写死成 bool，也不能绑死某个解决器。</para>
///
/// <para><b>它是纯函数</b>：不读磁盘、不联网、不改任何状态。所有输入都在参数里。</para>
/// </summary>
public static class IssueResolutionPolicy
{
    /// <summary>原始 TXT 被程序之外改过时，行号可能已失效，所有基于行号的动作都要让路。</summary>
    private const string SourceChangedSummary =
        "原始 TXT 已被其他程序修改，当前章节树里的行号可能已经对不上。请先重新识别章节，或把现有编排迁移到新版本，再处理这条提醒。";

    private static readonly IssueDefinition[] Definitions =
    [
        new("chapter_number_gap", ReviewCategories.Missing, "ChapterDiagnostics"),
        new("chapter_unrecognized", ReviewCategories.Missing, "ChapterDiagnostics"),
        new("chapter_unnumbered", ReviewCategories.Missing, "UnnumberedHeadings"),
        new("chapter_heading_typo", ReviewCategories.Missing, "MissingChapterHeadings"),
        new("numeric_chapters_suspected", ReviewCategories.Missing, "FindNumericChapters"),
        new("reference_chapter_absent", ReviewCategories.Missing, "ChapterReviewAnalyzer"),
        new("numeric_body_group", ReviewCategories.Misrecognized, "ChapterReviewAnalyzer"),
        new("chapter_duplicate", ReviewCategories.Duplicate, "ChapterDiagnostics"),
        new("chapter_content_duplicate", ReviewCategories.Duplicate, "ChapterContentDuplicates"),
        new("chapter_cross_title_duplicate", ReviewCategories.Duplicate, "ChapterContentDuplicates.FindCrossTitle"),
        new("chapter_repeated_sequence", ReviewCategories.Duplicate, "ChapterRepeatedSequences"),
        new("chapter_number_order", ReviewCategories.Structure, "ChapterDiagnostics"),
        new("volume_number_duplicate", ReviewCategories.Structure, "ChapterDiagnostics"),
        new("chapter_level_gap", ReviewCategories.Structure, "ChapterDiagnostics"),
        new("chapter_structure_suggested", ReviewCategories.Structure, "ChapterStructureSuggestion"),
        new("chapter_numbering_variants", ReviewCategories.Structure, "ChapterReviewAnalyzer"),
    ];

    private static readonly Dictionary<string, IssueDefinition> ByCode =
        Definitions.ToDictionary(d => d.IssueCode, StringComparer.Ordinal);

    /// <summary>章节类问题码的全集。非章节类的码（封面、制作设置等）不在这里。</summary>
    public static IReadOnlyList<IssueDefinition> All => Definitions;

    /// <summary>
    /// 这个码是什么问题、谁发现的。未知码返回 null —— **不猜一个默认类别**：
    /// 把未知码静默归进「其他提醒」，正是新码在报告窗口里消失的原因。
    /// </summary>
    public static IssueDefinition? Define(string? issueCode) =>
        issueCode is not null && ByCode.TryGetValue(issueCode, out var definition) ? definition : null;

    /// <summary>结合 finding 与上下文算出建议。</summary>
    public static ResolutionAdvice Evaluate(
        ChapterReviewGroup finding,
        ChapterRepairContextSnapshot context,
        IssueResolutionFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(finding);
        ArgumentNullException.ThrowIfNull(context);
        var local = facts ?? IssueResolutionFacts.None;
        var code = finding.Issue.Code;

        // 行号失效是**所有**基于行号的动作的共同前提，所以它在最前面，且不分问题码。
        // 以前这个判断散在工作台各处的 if 里，每加一个按钮就要记得再加一次。
        if (context.SourceChanged) return SourceChangedAdvice();

        return code switch
        {
            "chapter_number_gap" => GapAdvice(local, context),
            "chapter_unrecognized" => UnrecognizedAdvice(local, context),
            "chapter_unnumbered" => UnnumberedAdvice(context),
            "chapter_heading_typo" => HeadingTypoAdvice(context),
            "numeric_chapters_suspected" => NumericSuspectedAdvice(),
            "numeric_body_group" => RestoreAsBodyAdvice(),
            "chapter_duplicate" => DuplicateTitlesAdvice(context),
            "chapter_content_duplicate" => DuplicateBodiesAdvice(
                "同名章节的正文重复。软件不会替你决定留哪一份 —— 打开对比看清差异后再选。"),
            "chapter_cross_title_duplicate" => DuplicateBodiesAdvice(
                "标题不同但正文相同，多半是源文件重复拼接后又改了标题。软件不会自动删除，也不会按内容替换标题。"),
            "chapter_repeated_sequence" => RepeatedSequenceAdvice(),
            "chapter_number_order" => HierarchyAdvice(local,
                "编号顺序与所在位置不符。可能只是分卷重新编号，先看原文再决定是否调整。"),
            "volume_number_duplicate" => HierarchyAdvice(local,
                "同一个卷号出现两次。层级不会自动修改，核对原文后选择设为子章节或移出父章节。"),
            "chapter_level_gap" => HierarchyAdvice(local,
                "目录层级跳过了一级。层级不会自动修改，核对原文后选择设为子章节或移出父章节。"),
            "chapter_structure_suggested" => StructureSuggestedAdvice(),
            "chapter_numbering_variants" => NumberingVariantsAdvice(),
            "reference_chapter_absent" => ReferenceAbsentAdvice(context),
            _ => ResolutionAdvice.None("这一类检查项没有章节处理入口。"),
        };
    }

    /// <summary>
    /// 只有一条 finding、手上没有文档的调用方（报告窗口、任务中心）用这个重载。
    ///
    /// <para>它拿到的必然是**通用建议** —— 没有选中行，所以"把选中行建立成章节"不会出现。
    /// 这不是降级：报告窗口本来就不能就地执行，它只能跳转到工作台。</para>
    /// </summary>
    public static ResolutionAdvice Evaluate(
        ConversionPreflightIssue issue,
        ChapterRepairContextSnapshot? context = null,
        IssueResolutionFacts? facts = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var group = new ChapterReviewGroup(issue, IssueCategory.For(issue), [], [], []);
        return Evaluate(group, context ?? ChapterRepairContextSnapshot.Unknown, facts);
    }

    private static ResolutionAdvice SourceChangedAdvice() =>
        new(
            [
                new ResolutionAction(ResolutionIntent.ReRecognizeAsNumeric,
                    ResolutionExecutionKind.ReRecognizeTree, CatalogRole.None,
                    AutomationLevel.Confirmed, RiskLevel.High,
                    "按当前识别规则重新读一遍原文。**会替换当前章节树**，手工做过的调整会丢失；原始 TXT 不变。"),
                new ResolutionAction(ResolutionIntent.NavigateToSource,
                    ResolutionExecutionKind.Navigation, CatalogRole.None,
                    AutomationLevel.Manual, RiskLevel.Low,
                    "先只到原文里看，不做任何修改。"),
            ],
            ResolutionIntent.ReRecognizeAsNumeric,
            SourceChangedSummary);

    private static ResolutionAdvice GapAdvice(IssueResolutionFacts facts, ChapterRepairContextSnapshot context)
    {
        var localRepair = new ResolutionAction(ResolutionIntent.RepairMissingHeadings,
            ResolutionExecutionKind.GenerateRepairProposal, CatalogRole.None,
            AutomationLevel.Confirmed, RiskLevel.Medium,
            "只在跳章区间内、按编号格式错误找候选。补建前逐条预览；它不能判定缺的是哪几章。");

        var navigate = new ResolutionAction(ResolutionIntent.NavigateToSource,
            ResolutionExecutionKind.Navigation, CatalogRole.None,
            AutomationLevel.OneClick, RiskLevel.Low,
            "跳到原文里断号的那一段，看那里到底有没有标题。");

        // 已经有选用的目录时，"获取目录"不再是用户要做的事 —— 该做的是按它补建。
        var catalogAction = context.CatalogAvailability == CatalogAvailability.Selected
            ? new ResolutionAction(ResolutionIntent.AdoptCatalogChapters,
                ResolutionExecutionKind.OpenSpecializedTool, CatalogRole.None,
                AutomationLevel.Confirmed, RiskLevel.Medium,
                "按当前选用的目录补建原文里没定位到的章节。目录只提供标题与位置，正文不会被凭空造出来。")
            : new ResolutionAction(ResolutionIntent.AcquireCatalog,
                ResolutionExecutionKind.AcquireCatalog, CatalogRole.RequiredForConclusion,
                AutomationLevel.Confirmed, RiskLevel.Low,
                "获取或选用一份参考目录。**这一步不改任何东西**；要按它补建仍需另行确认。");

        // **没查过本地证据时（报告窗口、任务中心），两个出口都不预设。**
        // 推荐"到原文核对"：它对跳章永远成立，也不暗示必须先弄一份目录。
        // 报告窗口的按钮本来就是"去工作台"，它不该替工作台承诺用哪种手段 ——
        // 工作台拿到真实证据后会自己重算，届时该说「补建本组 3 个…」就说那个。
        if (facts.HasLocalHeadingCandidate is null)
            return new([navigate, localRepair, catalogAction], ResolutionIntent.NavigateToSource,
                "章节号在这段断了。可能是漏识别了一个标题，也可能源文本本身就没有这一章 —— 到原文看一眼就知道该走哪条路。");

        return facts.HasLocalHeadingCandidate.Value
            ? new([localRepair, catalogAction], ResolutionIntent.RepairMissingHeadings,
                "这段区间里有编号写法异常的疑似标题，可以先在本地核对。注意这不等于确认缺章 —— 缺的是哪几章只有参考目录能回答。")
            // 推荐**动作自己的** intent，不写死 AcquireCatalog：已经选用目录时这个动作换来换去，
            // 写死会让推荐指向一个不存在的动作，界面随即悄悄退化。
            : new([catalogAction, localRepair], catalogAction.Intent,
                "本地没有找到可以补建的候选。要判断缺的是哪几章，需要参考目录；没有目录时仍可人工到原文逐行核对。");
    }

    private static ResolutionAdvice UnrecognizedAdvice(IssueResolutionFacts facts, ChapterRepairContextSnapshot context)
    {
        var navigate = new ResolutionAction(ResolutionIntent.NavigateToSource,
            ResolutionExecutionKind.Navigation, CatalogRole.None,
            AutomationLevel.OneClick, RiskLevel.Low,
            "跳到原文里那一行，由你判断它是不是真的该成为一章。");

        var promote = new ResolutionAction(ResolutionIntent.PromoteSelectedLine,
            ResolutionExecutionKind.GenerateRepairProposal, CatalogRole.None,
            AutomationLevel.Confirmed, RiskLevel.Medium,
            "把当前选中的原文行建立成章节。只有你确认它确实是标题时才用。");

        var catalog = CatalogFor(context, CatalogRole.RequiredForConclusion,
            "目录能给出这一章原本的标题与位置。");

        // 选中的行能不能立成章，只有工作台知道 —— 报告窗口没有选中行，所以它拿到的是"先去看原文"。
        return facts.CanSplitSelectedLine
            ? new([promote, navigate, catalog], ResolutionIntent.PromoteSelectedLine,
                "软件无法安全地自动判断这一段该不该立成章 —— 猜错就是凭空造章节。可以人工核对；参考目录能显著提高确定性。")
            : new([navigate, catalog], ResolutionIntent.NavigateToSource,
                "软件无法安全地自动判断这一段该不该立成章。请先到原文核对；也可以获取参考目录，它比人眼更快给出结论。");
    }

    private static ResolutionAdvice UnnumberedAdvice(ChapterRepairContextSnapshot context) =>
        new(
            [
                new ResolutionAction(ResolutionIntent.RepairUnnumberedHeadings,
                    ResolutionExecutionKind.OpenSpecializedTool, CatalogRole.None,
                    AutomationLevel.Confirmed, RiskLevel.Low,
                    "打开目录辅助修复：调整“多长的章才算异常长”的阈值后预览补建。不会自动拆分正文。"),
                CatalogFor(context, CatalogRole.Helpful, "目录里有这一章时，可以直接用它的标题原文，不必靠行数去猜。"),
            ],
            ResolutionIntent.RepairUnnumberedHeadings,
            "异常长章里出现了疑似无编号的标题。行数阈值决定查出多少条 —— 放宽后候选变多，仍需逐条核对。");

    private static ResolutionAdvice HeadingTypoAdvice(ChapterRepairContextSnapshot context) =>
        new(
            [
                new ResolutionAction(ResolutionIntent.RepairMissingHeadings,
                    ResolutionExecutionKind.GenerateRepairProposal, CatalogRole.None,
                    AutomationLevel.Confirmed, RiskLevel.Medium,
                    "预览补建这些标题。只在跳章区间内按编号格式找候选 —— 区间之外的漏识别不会出现在这里。"),
                CatalogFor(context, CatalogRole.Helpful, "目录能确认这些标题原本该怎么写。"),
            ],
            ResolutionIntent.RepairMissingHeadings,
            "跳章区间里发现了编号写法异常的疑似标题。补建会同时改动章节树与原文，应用前会列出将改动的行。");

    private static ResolutionAdvice NumericSuspectedAdvice() =>
        new(
            [
                new ResolutionAction(ResolutionIntent.ReRecognizeAsNumeric,
                    ResolutionExecutionKind.ReRecognizeTree, CatalogRole.None,
                    AutomationLevel.Confirmed, RiskLevel.Medium,
                    "按数字标题规则重新识别本书。**会替换当前章节树**，手工调整会丢失；不会修改原始 TXT。"),
            ],
            ResolutionIntent.ReRecognizeAsNumeric,
            "一个字都没被识别成章节，但原文里有多处疑似数字章节。这通常是识别选项的问题，不是原文的问题。");

    private static ResolutionAdvice RestoreAsBodyAdvice() =>
        new(
            [
                new ResolutionAction(ResolutionIntent.RestoreAsBody,
                    ResolutionExecutionKind.GenerateRepairProposal, CatalogRole.None,
                    AutomationLevel.Confirmed, RiskLevel.Low,
                    "把这些数字条目还原成上一章的正文。原始条目与正文都完整保留。"),
            ],
            ResolutionIntent.RestoreAsBody,
            "普通章节之间混进了一串数字条目，多数正文很短 —— 更像正文里的列举被当成了章节。");

    private static ResolutionAdvice DuplicateTitlesAdvice(ChapterRepairContextSnapshot context) =>
        new(
            [
                new ResolutionAction(ResolutionIntent.CleanDuplicateTitles,
                    ResolutionExecutionKind.GenerateRepairProposal, CatalogRole.None,
                    AutomationLevel.Confirmed, RiskLevel.Medium,
                    "先看同名各份各自的正文，再决定清理哪些。默认只清理正文为空的重复标题。"),
                new ResolutionAction(ResolutionIntent.NavigateToSource,
                    ResolutionExecutionKind.Navigation, CatalogRole.None,
                    AutomationLevel.OneClick, RiskLevel.Low,
                    "逐个跳到原文核对这几处标题。"),
                CatalogFor(context, CatalogRole.Helpful, "目录能确认哪一份才是真正的这一章。"),
            ],
            ResolutionIntent.CleanDuplicateTitles,
            "同一层级出现同名章节。同名只能说明它们是候选，不能说明哪一份是多余的 —— 正文为空的那几份证据最强，其余仍需你判断。");

    private static ResolutionAdvice DuplicateBodiesAdvice(string summary) =>
        new(
            [
                new ResolutionAction(ResolutionIntent.CompareDuplicateBodies,
                    ResolutionExecutionKind.Navigation, CatalogRole.None,
                    AutomationLevel.Manual, RiskLevel.Low,
                    "打开正文对比：两份逐行并排，看清差异后选择保留哪一份。"),
            ],
            ResolutionIntent.CompareDuplicateBodies,
            summary);

    private static ResolutionAdvice RepeatedSequenceAdvice() =>
        new(
            [
                new ResolutionAction(ResolutionIntent.NavigateToSource,
                    ResolutionExecutionKind.Navigation, CatalogRole.None,
                    AutomationLevel.OneClick, RiskLevel.Low,
                    "跳到这段重复拼接的起点，逐处核对。软件不会自动删除任何一段。"),
            ],
            ResolutionIntent.NavigateToSource,
            "同一段内容在原文里连续出现了两次。多出版本通常要整段删掉，但删哪一段必须由人判断。");

    private static ResolutionAdvice HierarchyAdvice(IssueResolutionFacts facts, string summary)
    {
        var hierarchy = new ResolutionAction(ResolutionIntent.ReviewHierarchy,
            ResolutionExecutionKind.OpenSpecializedTool, CatalogRole.None,
            AutomationLevel.Manual, RiskLevel.Low,
            "打开层级处理：核对前后章号与层级后，选择设为子章节或移出父章节。层级永远不会自动修改。");
        // 章号能从相邻章节推出来时有一个**确定答案**，它必须压过通用的"核对层级"。
        // 这条规则在工作台原来那份手写映射里就是第一句 —— 策略漏掉它，按钮就会说"核对层级"
        // 而用户明明能看见「修改为：第三百章 归来」。
        if (!facts.HasSuggestedTitle) return new([hierarchy], ResolutionIntent.ReviewHierarchy, summary);
        var apply = new ResolutionAction(ResolutionIntent.ApplySuggestedTitle,
            ResolutionExecutionKind.GenerateRepairProposal, CatalogRole.None,
            AutomationLevel.Confirmed, RiskLevel.Low,
            "采用算出来的正确编号。改的是章节标题，不改正文。");
        return new([apply, hierarchy], ResolutionIntent.ApplySuggestedTitle, summary);
    }

    private static ResolutionAdvice StructureSuggestedAdvice() =>
        new(
            [
                new ResolutionAction(ResolutionIntent.RecoverStructure,
                    ResolutionExecutionKind.OpenSpecializedTool, CatalogRole.None,
                    AutomationLevel.Confirmed, RiskLevel.Medium,
                    "先预览结构整理的结果，确认后才应用。它重建编号分组，不修改原始 TXT。"),
            ],
            ResolutionIntent.RecoverStructure,
            "章节的编号分组可能整体错位（例如全部被识别成同级）。这一步会重排层级，必须先看清预览。");

    private static ResolutionAdvice NumberingVariantsAdvice() =>
        new(
            [
                new ResolutionAction(ResolutionIntent.ApplySuggestedTitle,
                    ResolutionExecutionKind.GenerateRepairProposal, CatalogRole.None,
                    AutomationLevel.Confirmed, RiskLevel.Low,
                    "把同一章的多种编号写法统一成一种。改的是章节标题，不改正文。"),
                new ResolutionAction(ResolutionIntent.NavigateToSource,
                    ResolutionExecutionKind.Navigation, CatalogRole.None,
                    AutomationLevel.OneClick, RiskLevel.Low,
                    "逐项跳到原文核对编号。"),
            ],
            ResolutionIntent.ApplySuggestedTitle,
            "同一层级有多处编号顺序差异，已合并展示。可能涉及编号重启、体系混用或顺序调整 —— 不据此认定缺章，也不自动改号。");

    private static ResolutionAdvice ReferenceAbsentAdvice(ChapterRepairContextSnapshot context) =>
        new(
            [
                new ResolutionAction(ResolutionIntent.AdoptCatalogChapters,
                    ResolutionExecutionKind.OpenSpecializedTool, CatalogRole.None,
                    AutomationLevel.Confirmed, RiskLevel.Medium,
                    "打开目录辅助修复，按目录补建这些章节。目录只提供标题与位置，正文不会被凭空造出来。"),
                new ResolutionAction(ResolutionIntent.NavigateToSource,
                    ResolutionExecutionKind.Navigation, CatalogRole.None,
                    AutomationLevel.Manual, RiskLevel.Low,
                    "先到原文里核对这几章是不是真的不存在。"),
            ],
            ResolutionIntent.AdoptCatalogChapters,
            context.CatalogAvailability == CatalogAvailability.None
                ? "参考目录里有这些章，章节树里没有。它们要么原文里确实没有（软件不会补造正文），要么没被识别到。"
                : "当前选用的目录里有这些章，章节树里没有。它们要么原文里确实没有（软件不会补造正文），要么没被识别到。");

    /// <summary>
    /// "获取目录"这个动作在已经选用目录时不该再出现 —— 此时该说的是"按它核对"。
    /// 这是 <see cref="ChapterRepairContextSnapshot"/> 真正被用上的地方：同一句建议，
    /// 在情况 B（有目录但还没用）与情况 F（已经按目录对齐过）下必须不同。
    /// </summary>
    private static ResolutionAction CatalogFor(
        ChapterRepairContextSnapshot context, CatalogRole role, string explanation) =>
        context.CatalogAvailability == CatalogAvailability.Selected
            ? new ResolutionAction(ResolutionIntent.AdoptCatalogChapters,
                ResolutionExecutionKind.OpenSpecializedTool, CatalogRole.None,
                AutomationLevel.Confirmed, RiskLevel.Low,
                "按当前选用的目录逐条核对。" + explanation)
            : new ResolutionAction(ResolutionIntent.AcquireCatalog,
                ResolutionExecutionKind.AcquireCatalog, role,
                AutomationLevel.Confirmed, RiskLevel.Low,
                "获取或选用一份参考目录。**这一步不改任何东西。**" + explanation);
}
