namespace EasyPub.Core;

/// <summary>参考目录在本会话里的**可用性** —— 它不描述"目录的一切"。</summary>
public enum CatalogAvailability
{
    /// <summary>没有可取用的目录。</summary>
    None,

    /// <summary>磁盘上有本书保存过的目录，但本次还没有明确要用它。</summary>
    Saved,

    /// <summary>本次明确要用某一份（调用方指定的，或用户刚选定的）。</summary>
    Selected,
}

/// <summary>
/// 工作台顶部那三行事实的**不可变快照** —— 每次生成、只读。
///
/// <para><b>它不是"新的可变真相"。</b>真实状态仍分别住在 <c>ChapterTreeDocument</c> /
/// <c>ReferenceCatalogInput</c> / <c>PersistedTreeProvenance</c> / 用户当前选择里；这个快照只是
/// **某一刻从那些来源捕出来的一个视图**。若它自己再存一份并到处改，那就只是把"状态散落"
/// 升级成"状态散落 + 一个会过期的汇总副本"。</para>
///
/// <para>三行回答三个**不能互相推导**的问题：</para>
///
/// <list type="bullet">
/// <item><b>参考目录</b>：磁盘上 / 本次有没有一份可用的目录。</item>
/// <item><b>当前章节树</b>：它是怎么来的。这一行**不能**从第一行推出来 ——
/// "有目录"和"树是按它改的"是两件事：用户可以按目录修完树再清掉目录记录
/// （于是第一行是"未加载"而第二行是"目录校验后"），也可以只获取目录却从不使用
/// （第一行有目录而第二行是"本地识别"）。</item>
/// <item><b>原文版本</b>：文件是否在程序之外被改过。</item>
/// </list>
/// </summary>
public sealed record ChapterRepairContextSnapshot(
    CatalogAvailability CatalogAvailability,
    string? CatalogSource,
    int CatalogChapterCount,
    TreeStateSummary Tree,
    bool SourceChanged)
{
    /// <summary>第一行的用户可读描述。</summary>
    public string CatalogLine => CatalogAvailability switch
    {
        CatalogAvailability.Selected => $"已选用「{CatalogSource}」· {CatalogChapterCount} 章",
        CatalogAvailability.Saved => $"已保存「{CatalogSource}」· {CatalogChapterCount} 章（本次尚未选用）",
        _ => "未加载",
    };

    /// <summary>
    /// 第二行的用户可读描述 —— **正交事实的组合**，不是一个模式名。
    ///
    /// <para>"目录校验后 + 手工修改 3 项"这种状态是真实存在的（按目录修过、之后又动过几章），
    /// 单值枚举只能说出其中一半。</para>
    /// </summary>
    public string TreeLine
    {
        get
        {
            var origin = Tree.BaseOrigin switch
            {
                TreeBaseOrigin.Reference => "目录校验后",
                TreeBaseOrigin.Local => "本地识别",
                // 旧项目没有 provenance。**不猜**：显示"未知"比错误地标成"目录校验后"安全。
                _ => "来源未知（旧项目）",
            };
            return Tree.ManualEditCount > 0 ? $"{origin} + 手工修改 {Tree.ManualEditCount} 项" : origin;
        }
    }

    /// <summary>
    /// 不知道上下文时的快照（报告窗口只有一条 issue，没有文档也没有章节树）。
    ///
    /// <para>它**如实说"没有可取用的目录"**，而不是猜一个"可能有"。猜高会让界面显示一份
    /// 并不存在的目录，猜低会让用户以为可以去获取 —— 两种都是假状态。</para>
    /// </summary>
    public static ChapterRepairContextSnapshot Unknown { get; } = new(
        CatalogAvailability.None, null, 0,
        TreeStateSummary.From(PersistedTreeProvenance.Unknown), false);
}

/// <summary>
/// 从**权威来源**捕一个快照。它只读，不写任何东西。
///
/// <para>UI 拿到快照后只读地显示，不再自己推断状态 —— 这正是"三个事实"能被如实显示的前提。</para>
/// </summary>
public static class ChapterRepairContextFactory
{
    public static ChapterRepairContextSnapshot Capture(
        ChapterTreeDocument document,
        PersistedTreeProvenance? provenance,
        bool sourceChanged,
        ReferenceCatalog? selectedCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var catalog = selectedCatalog;
        var availability = catalog is not null ? CatalogAvailability.Selected : CatalogAvailability.None;
        if (catalog is null
            && ReferenceCatalogInput.ReadCatalog(ReferenceCatalogInput.Load(document.SourceSha256)) is { } saved)
        {
            catalog = saved;
            availability = CatalogAvailability.Saved;
        }

        return new ChapterRepairContextSnapshot(
            availability,
            catalog?.Source,
            catalog?.Titles.Count ?? 0,
            TreeStateSummary.From(provenance ?? PersistedTreeProvenance.Unknown),
            sourceChanged);
    }
}
