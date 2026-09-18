namespace EasyPub.Core;

/// <summary>当前章节树的**基础来源**。</summary>
public enum TreeBaseOrigin
{
    /// <summary>本地识别，从未被参考目录校验过。</summary>
    Local,

    /// <summary>被参考目录校验过 —— 哪怕只应用了其中一部分。</summary>
    Reference,

    /// <summary>
    /// 旧项目没有 provenance 字段。
    ///
    /// <para><b>不猜。</b>不能用"<see cref="ChapterTreePlan.ReferenceCatalog"/> 非空"推断成
    /// <see cref="Reference"/>：那个字段证明的只是"这本书保存过一份目录"。UI 对这种情况显示
    /// 「来源未知（旧项目）」，比错误地标成"目录校验后"安全。</para>
    /// </summary>
    UnknownLegacy,
}

/// <summary>
/// **持久化**的树来源 —— 与 <see cref="ChapterTreePlan.ReferenceCatalog"/> 并存，但语义完全不同。
///
/// <para>那个字段证明的是"这本书**保存过**一份目录"：<c>ChapterEditorWindow.Save_Click</c> 无条件写它，
/// 所以"打开一本本地识别的书 → 获取参考目录 → 只看不用 → 保存章节树"之后，
/// 树仍然是 <see cref="TreeBaseOrigin.Local"/> 而它却已经非空。</para>
///
/// <para>所以不能用它推断树的来源。树到底经历过什么，必须**自己记**。</para>
/// </summary>
public sealed record PersistedTreeProvenance(
    int SchemaVersion,
    TreeBaseOrigin BaseOrigin,
    string? AppliedCatalogFingerprint,
    int ManualRevision)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>旧项目：没有这个字段，于是不猜。</summary>
    public static PersistedTreeProvenance Unknown { get; } =
        new(CurrentSchemaVersion, TreeBaseOrigin.UnknownLegacy, null, 0);

    /// <summary>刚从本地识别建起来的一棵树。</summary>
    public static PersistedTreeProvenance Local { get; } =
        new(CurrentSchemaVersion, TreeBaseOrigin.Local, null, 0);

    /// <summary>按目录校验过（可能只应用了一部分 action）。</summary>
    public static PersistedTreeProvenance FromCatalog(string? fingerprint) =>
        new(CurrentSchemaVersion, TreeBaseOrigin.Reference, fingerprint, 0);
}

/// <summary>
/// 当前树状态的**正交事实** —— 不是一个枚举。
///
/// <para>真实的树可以同时是"按目录校验过 + 用户手工改过 3 章"。单值枚举会丢掉其中一半，
/// 而用户需要知道的恰恰是两半：它原来是不是目录树、以及后来被谁动过。</para>
/// </summary>
public sealed record TreeStateSummary(
    TreeBaseOrigin BaseOrigin,
    string? ReferenceCatalogFingerprint,
    int ManualEditCount)
{
    public static TreeStateSummary Unknown { get; } = new(TreeBaseOrigin.UnknownLegacy, null, 0);

    public static TreeStateSummary From(PersistedTreeProvenance provenance) =>
        new(provenance.BaseOrigin, provenance.AppliedCatalogFingerprint, provenance.ManualRevision);
}

/// <summary>
/// **最近一次**目录应用的审计摘要 —— 历史，**不是**当前状态。
///
/// <para>它不能单独决定"当前树有多少是 reference-derived"：用户可以继续手工改、撤销、
/// 再做第二次目录修复。所以它与 <see cref="TreeStateSummary"/> 分开存，
/// 不进 <see cref="PersistedTreeProvenance"/>。</para>
/// </summary>
public sealed record ReferenceApplicationSummary(
    string? ProposalId,
    int AppliedCount,
    int SkippedCount,
    int Revision);
