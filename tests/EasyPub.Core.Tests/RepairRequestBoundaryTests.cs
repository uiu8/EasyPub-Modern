using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// §8.6(b) 里属于**依据边界**的两条状态组合测试。
///
/// <para>它们锁的是同一件事的两面：<b>本次用哪个依据，只由调用方给的请求决定</b>，
/// 不由磁盘上碰巧有什么决定。</para>
///
/// <list type="number">
/// <item>磁盘上有记录，而调用方明确要求只用当前章节树 → **不读它**；</item>
/// <item>调用方手上拿着一份目录，而磁盘上是另一份 → **只用手上那份**。</item>
/// </list>
///
/// <para>两条都不动全局状态：临时书的 <c>SourceSha256</c> 是随机的，所以它们读不到别的测试
/// 写下的记录；而它们自己写下的那条在 <c>finally</c> 里删掉。这比改
/// <c>EASYPUB_REFERENCE_CATALOGS_PATH</c> 安全 —— 环境变量是进程级的，而 Core 测试默认并行。</para>
/// </summary>
public class RepairRequestBoundaryTests
{
    private static async Task<(string Path, ChapterTreeDocument Document)> BookAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-boundary-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        return (path, await ChapterTreeDocument.LoadAsync(path));
    }

    private static ReferenceCatalog Catalog(params string[] titles) =>
        ReferenceCatalogInput.ParseText(string.Join("\n", titles))!;

    /// <summary>
    /// 一条同名重复，其中第二份**正文为空** —— 这是树自身就能看出的证据，
    /// 不需要任何书外知识。用它来证明"没读目录也照样有方案"。
    /// </summary>
    private const string RepeatedTitleBook =
        "第一章 起点\n正文一。\n第一章 起点\n第二章 继续\n正文二。\n";

    [Fact]
    public async Task A_saved_catalog_is_not_read_when_the_caller_asked_for_the_current_tree_only()
    {
        var (path, document) = await BookAsync(RepeatedTitleBook);
        // 磁盘上**确实有一份**本书的目录 —— 这样"没读到"才不是因为它根本不存在。
        ReferenceCatalogInput.SaveCatalog(document.SourceSha256,
            Catalog("第一章 起点", "第二章 继续", "第三章 之后"));
        try
        {
            var outcome = await ChapterAutoRepair.RepairAsync(path, new CurrentTreeHeuristicRequest(),
                currentDocument: document);

            Assert.False(outcome.CatalogFound);
            Assert.Null(outcome.Catalog);
            // 关键的另一半：**它确实做了事**。"没读目录"不等于"什么都做不了" ——
            // 同名重复就在树自身能看出的范围内，这正是无目录路径存在的理由。
            Assert.NotNull(outcome.Plan);
            Assert.Contains(outcome.Plan!.Actions, action => action.Kind == ReferenceActionKind.RemoveDuplicate);
        }
        finally { ReferenceCatalogInput.Forget(document.SourceSha256); }
    }

    [Fact]
    public async Task A_request_naming_one_catalog_uses_that_one_and_never_the_saved_one()
    {
        var (path, document) = await BookAsync(RepeatedTitleBook);
        // 磁盘上是一份**规模明显不同**的目录。若实现偷偷读了它，下面的章数断言会立刻失败。
        ReferenceCatalogInput.SaveCatalog(document.SourceSha256, Catalog("第一章 起点"));
        try
        {
            var given = Catalog("第一章 起点", "第二章 继续", "第三章 之后");
            var outcome = await ChapterAutoRepair.RepairAsync(path, new ReferenceRepairRequest(given),
                currentDocument: document);

            Assert.True(outcome.CatalogFound);
            Assert.Equal(given.Titles.Count, outcome.Catalog!.Titles.Count);
            // 磁盘上那份只有 1 章；用上它就说明选错了来源。
            Assert.NotEqual(1, outcome.Catalog.Titles.Count);
        }
        finally { ReferenceCatalogInput.Forget(document.SourceSha256); }
    }

    /// <summary>
    /// 没有请求、也没有取目录的调用方，不该让 <c>RepairAsync</c> 自己去磁盘上找依据 ——
    /// 判别类型让"用目录 + 空目录"和"自修复 + 目录"这两类状态在类型层就不存在。
    /// 这条用一个**磁盘上有记录**的书来证明：即使有，不传请求也不算数。
    /// </summary>
    [Fact]
    public async Task Nothing_on_disk_becomes_the_basis_unless_the_request_says_so()
    {
        var (path, document) = await BookAsync(RepeatedTitleBook);
        ReferenceCatalogInput.SaveCatalog(document.SourceSha256, Catalog("第一章 起点", "第二章 继续"));
        try
        {
            var acquisition = ChapterAutoRepair.ReadSavedCatalog(document);
            Assert.NotNull(acquisition.Catalog); // 确实读得到

            // 但**只有**显式走 ReferenceRepairRequest 才会用到它。
            var withoutCatalog = await ChapterAutoRepair.RepairAsync(path, new CurrentTreeHeuristicRequest(),
                currentDocument: document);
            Assert.Null(withoutCatalog.Catalog);

            var withCatalog = await ChapterAutoRepair.RepairAsync(path, new ReferenceRepairRequest(acquisition.Catalog!),
                currentDocument: document);
            Assert.NotNull(withCatalog.Catalog);
        }
        finally { ReferenceCatalogInput.Forget(document.SourceSha256); }
    }
}
