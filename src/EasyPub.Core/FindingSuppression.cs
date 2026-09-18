namespace EasyPub.Core;

/// <summary>
/// 一个 finding **涉及哪些节点与行** —— 抑制判断的作用域。
///
/// <para>抑制不能只看"某一行是否已验证"：这几类 finding 天然跨多个对象，
/// 单行判断对它们没有意义。</para>
///
/// <list type="bullet">
/// <item><c>chapter_duplicate</c> —— 同名的两个节点</item>
/// <item><c>chapter_cross_title_duplicate</c> —— 两个**不同标题**的节点</item>
/// <item><c>chapter_repeated_sequence</c> —— 一整段正文范围</item>
/// </list>
/// </summary>
public sealed record FindingScope(IReadOnlyList<string> NodeIds, IReadOnlyList<int> Lines)
{
    /// <summary>从分析结果里的一条分组直接取作用域。</summary>
    public static FindingScope Of(ChapterReviewGroup group) => new(group.NodeIds, group.Lines);

    public static FindingScope Empty { get; } = new([], []);

    /// <summary>这一处是否**完全**落在已验证范围内。</summary>
    public bool IsEmpty => NodeIds.Count == 0 && Lines.Count == 0;
}

/// <summary>
/// 参考目录**已经验证过**哪些节点与行 —— 抑制判断唯一需要的输入。
///
/// <para>它替掉了原来的一个全局 <c>bool referenceAligned</c>。那个 bool 的问题是：只要有一个
/// 手工改过的节点，它整本书就变回 false，于是六类本地提醒**全部**重新打开——用户看到的现象是
/// "我明明按目录对齐过了，怎么又冒出这么多问题"，而原因只是他自己动过一章。</para>
///
/// <para>现在验证状态是**逐节点、逐行**的，抑制判断也就跟着逐 finding 做。</para>
/// </summary>
public sealed record ReferenceVerificationIndex(
    IReadOnlySet<string> VerifiedNodeIds,
    IReadOnlySet<int> VerifiedLines,
    string? CatalogFingerprint = null)
{
    /// <summary>没有目录参与 —— 什么都抑制不了。</summary>
    public static ReferenceVerificationIndex None { get; } =
        new(new HashSet<string>(), new HashSet<int>());

    public bool IsEmpty => VerifiedNodeIds.Count == 0;

    /// <summary>某一行是否落在已验证范围内。用于单行 finding。</summary>
    public bool IsLineVerified(int line) => VerifiedLines.Contains(line);

    /// <summary>这一处是否**完全**被验证覆盖。节点优先，其次行。</summary>
    public bool FullyCovers(FindingScope scope)
    {
        if (scope.NodeIds.Count > 0) return scope.NodeIds.All(VerifiedNodeIds.Contains);
        if (scope.Lines.Count > 0) return scope.Lines.All(VerifiedLines.Contains);
        return false;
    }

    /// <summary>参考目录是不是**已经回答了这个问题**。
    ///
    /// <para>注意这**不等于**"涉及的位置都被验证过" —— 那只是前提。真正要问的是
    /// 目录的证据在逻辑上是否推翻了这个 detector 所问的问题（见
    /// <see cref="FindingSuppressionPolicy"/>）。</para>
    /// </summary>
    public static ReferenceVerificationIndex Build(IReadOnlyList<ChapterTreeEntry> entries)
    {
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var lines = new HashSet<int>();
        foreach (var entry in entries)
        {
            if (entry.IsFrontMatter || entry.RecognitionSource != "reference") continue;
            nodes.Add(entry.Id);
            if (entry.TitleLineNumber is int title) lines.Add(title);
            foreach (var range in entry.ContentRanges)
                for (var line = range.StartLine; line <= range.EndLine; line++) lines.Add(line);
        }
        return new(nodes, lines);
    }
}

/// <summary>对一条 finding 的处置。</summary>
public enum SuppressionDecisionKind
{
    /// <summary>照常显示。</summary>
    Report,

    /// <summary>显示，但补上目录给出的上下文。</summary>
    ReportWithReferenceContext,

    /// <summary>降级（例如从"提醒"降为"信息"）。目前未使用，保留给后续按 detector 细化。</summary>
    Downgrade,

    /// <summary>目录证据已经回答了同一个问题 —— 不再重复显示。</summary>
    Superseded,
}

/// <summary>处置结果，带**理由** —— UI 要靠它解释"为什么这条不再显示"。</summary>
public sealed record SuppressionDecision(SuppressionDecisionKind Kind, string Reason)
{
    public bool IsSuppressed => Kind == SuppressionDecisionKind.Superseded;

    public static SuppressionDecision Report(string reason) => new(SuppressionDecisionKind.Report, reason);
}

/// <summary>
/// 逐 detector 决定一条 finding 该不该因为参考目录而不再重复显示。
///
/// <para><b>核心不是"覆盖"，而是"目录证据是否在逻辑上 supersede 了这个 detector 所问的问题"。</b>
/// 这是本策略唯一容易做错的地方：把"这个节点被目录验证过"当成"所有关于它的本地 finding 都已失效"。</para>
///
/// <para>反例（<c>chapter_content_duplicate</c>）：参考目录明确有第十章、第十一章，两章都成功
/// reference-verified —— <b>但源 TXT 因抓取错误，二者正文完全一样</b>。章节身份被目录验证，
/// 并不能否定"正文被错误复制"。同理 <c>chapter_repeated_sequence</c>：目录<b>反而可能帮助证明</b>
/// "这段正文在源文件里重复拼接了"。</para>
///
/// <para>所以 detector 分两类：</para>
///
/// <list type="table">
/// <item>
///   <term>可被 supersede</term>
///   <description>目录证据回答的是**同一个问题**：这一段到底是什么章、编号该是多少、两个位置
///   是不是两个独立的章。目录一旦给出答案，本地猜测就没有存在意义。</description>
/// </item>
/// <item>
///   <term>不可被 supersede</term>
///   <description>目录证据回答的是**另一个问题**。章节身份正确，不蕴含正文没有重复。</description>
/// </item>
/// </list>
/// </summary>
public static class FindingSuppressionPolicy
{
    /// <summary>
    /// 目录证据**推翻不了**的 detector。
    ///
    /// <para>分两组，理由不同：</para>
    ///
    /// <list type="bullet">
    /// <item><b>关于正文内容</b>的这三个 —— 目录验证的是**章节身份与位置**，两者互不蕴含。
    /// 章节身份正确，不意味着正文没有被错误复制。</item>
    /// <item><b>关于完整性</b>的 <c>chapter_number_gap</c> —— 有目录时它反而**更该说**：
    /// 目录知道哪些章存在，所以此时的编号间隔意味着"原文确实缺少官方版本有的章"，
    /// 而不是无目录时那种"可能只是漏识别"。</item>
    /// </list>
    /// </summary>
    private static readonly HashSet<string> CannotBeSupersededByCatalog = new(StringComparer.Ordinal)
    {
        "chapter_content_duplicate",
        "chapter_cross_title_duplicate",
        "chapter_repeated_sequence",
        "chapter_number_gap",
    };

    public static SuppressionDecision Decide(
        string issueCode, FindingScope scope, ReferenceVerificationIndex verification)
    {
        if (verification.IsEmpty)
            return SuppressionDecision.Report("没有参考目录参与验证。");

        var covered = verification.FullyCovers(scope);

        if (CannotBeSupersededByCatalog.Contains(issueCode))
            return covered
                ? new SuppressionDecision(SuppressionDecisionKind.ReportWithReferenceContext,
                    "目录已验证这些位置，但它回答的是另一个问题（正文内容 / 完整性）—— 仍然显示。")
                : SuppressionDecision.Report("目录没有覆盖这一处，照常显示。");

        return covered
            ? new SuppressionDecision(SuppressionDecisionKind.Superseded,
                "目录已经回答了同一个问题，不再重复报告本地猜测。")
            : SuppressionDecision.Report("目录没有覆盖这一处，照常显示。");
    }
}
