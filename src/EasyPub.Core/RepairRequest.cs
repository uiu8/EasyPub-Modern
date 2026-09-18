namespace EasyPub.Core;

/// <summary>
/// 本次修复用哪个依据 —— **由调用方显式给出**，不由 <see cref="ChapterAutoRepair.RepairAsync"/> 自己猜。
///
/// <para>这个类型存在的理由：以前 <c>RepairAsync(..., reference: null)</c> 的语义是
/// "请自动决定依据" —— 它会依次读本书已保存的目录、联网抓取、拿不到再退回自修复。于是同一个按钮
/// 会产生四种结果（目录方案 / 自修复方案 / 弹出目录对话框 / 带失败判词的自修复方案），
/// 调用方和用户都无法预知。</para>
///
/// <para>判别类型让非法组合在**类型层**就不存在：<see cref="CurrentTreeHeuristicRequest"/> 不带目录，
/// <see cref="ReferenceRepairRequest"/> 必须带目录 —— 没有"自修复 + 目录"或"用目录 + 空目录"
/// 这类状态需要靠运行时检查兜底。</para>
/// </summary>
public abstract record RepairRequest;

/// <summary>
/// 只修**当前章节树自身就能判定**的问题。**不读已保存的目录，不联网。**
///
/// <para>名字刻意不叫 <c>SelfRepairRequest</c>：它保证的只有"本次不重新读取参考目录"，
/// **不**保证"输入树从未受过目录影响" —— 当前树可能已被目录校验过、被用户手工改过，或经过版本迁移。
/// UI 上的对应说法是「基于当前章节树检查」。</para>
/// </summary>
public sealed record CurrentTreeHeuristicRequest : RepairRequest;

/// <summary>
/// 用**调用方给定的**这份参考目录修复。
///
/// <para>目录缺失不是"再想想办法"，而是调用方的错误：它必须先用
/// <see cref="ChapterAutoRepair.AcquireCatalogAsync"/> 显式取到一份目录，
/// 或者明确改走 <see cref="CurrentTreeHeuristicRequest"/>。</para>
/// </summary>
public sealed record ReferenceRepairRequest(ReferenceCatalog Catalog) : RepairRequest;

/// <summary>
/// 取目录的结果 —— **磁盘与网络只发生在这一步**。
///
/// <para>它把"取到了什么、为什么没取到、花了多久"如实带回给调用方，让**调用方**（而不是
/// <see cref="ChapterAutoRepair.RepairAsync"/>）决定下一步：用这份目录、退到自修复，
/// 还是把失败原因显示给用户。</para>
/// </summary>
public sealed record CatalogAcquisition(
    ReferenceCatalog? Catalog,
    string? Error,
    IReadOnlyList<ReferenceCatalogClient.SourceTiming> Timings,
    long ElapsedMs)
{
    /// <summary>调用方自己已经拿着目录（例如用户刚从目录面板选定）。</summary>
    public static CatalogAcquisition Given(ReferenceCatalog catalog) => new(catalog, null, [], 0);

    /// <summary>没有取目录，也不需要报错（例如明确只要求用当前章节树）。</summary>
    public static CatalogAcquisition NotRequested { get; } = new(null, null, [], 0);
}
