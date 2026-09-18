using System.IO;
using System.Text;

namespace EasyPub.Core.Tests;

/// <summary>
/// 工作台三行事实的快照 —— 文档 §3.2。
///
/// 它替换掉的是原来那个**单一「修复依据」开关**。那个开关表达不了真实状态：
/// "有目录"和"树是按目录改的"是两件事，而一个开关只能说出其中一件，
/// 于是总有一半是假的。
/// </summary>
public class ChapterRepairContextTests
{
    private static async Task<ChapterTreeDocument> BookAsync(string text)
    {
        // 临时文件 → SourceSha256 是随机的 → 读不到任何已保存的目录记录，天然隔离。
        var path = Path.Combine(Path.GetTempPath(), $"easypub-ctx-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        return await ChapterTreeDocument.LoadAsync(path);
    }

    [Fact]
    public async Task The_tree_line_cannot_be_derived_from_the_catalog_line()
    {
        // **没有目录**不代表**树是本地识别的**：用户可以按目录修完树之后清掉目录记录，
        // 树仍然是目录树（它绑的是那次修复，不是那条记录）。
        // 这是单一开关无法表达的状态之一，也正是「清除目录 ≠ 回到本地树」。
        var document = await BookAsync("第一章 起点\n正文\n第二章 继续\n正文\n");

        var snapshot = ChapterRepairContextFactory.Capture(
            document, PersistedTreeProvenance.FromCatalog("https://example.invalid/目录"), sourceChanged: false);

        Assert.Equal(CatalogAvailability.None, snapshot.CatalogAvailability);
        Assert.Equal("未加载", snapshot.CatalogLine);
        Assert.Contains("目录校验后", snapshot.TreeLine);
    }

    [Fact]
    public async Task An_old_project_says_unknown_instead_of_guessing()
    {
        // 旧项目没有 provenance 字段。**不猜** —— 不能拿"保存过一份目录"推成"按目录改过"。
        var document = await BookAsync("第一章 起点\n正文\n");

        var snapshot = ChapterRepairContextFactory.Capture(
            document, PersistedTreeProvenance.Unknown, sourceChanged: false);

        Assert.Contains("来源未知", snapshot.TreeLine);
    }

    [Fact]
    public async Task Manual_edits_are_named_alongside_the_origin()
    {
        // 正交事实：目录校验过**并且**被手工改过 —— 两半都要说出来。
        var document = await BookAsync("第一章 起点\n正文\n");

        var snapshot = ChapterRepairContextFactory.Capture(
            document, PersistedTreeProvenance.FromCatalog("x") with { ManualRevision = 3 }, sourceChanged: false);

        Assert.Contains("目录校验后", snapshot.TreeLine);
        Assert.Contains("手工修改 3 项", snapshot.TreeLine);
    }

    [Fact]
    public async Task A_selected_catalog_is_reported_as_selected_not_merely_saved()
    {
        // "内存里选了一份"与"磁盘上存着一份"不是一回事（§8.2.2c）。
        var document = await BookAsync("第一章 起点\n正文\n");
        var catalog = ReferenceCatalogInput.ParseText("第一章 起点\n第二章 继续\n");

        var snapshot = ChapterRepairContextFactory.Capture(
            document, PersistedTreeProvenance.Local, sourceChanged: true, selectedCatalog: catalog);

        Assert.Equal(CatalogAvailability.Selected, snapshot.CatalogAvailability);
        Assert.Contains("已选用", snapshot.CatalogLine);
        Assert.Contains("2 章", snapshot.CatalogLine);
        Assert.True(snapshot.SourceChanged);
    }

    [Fact]
    public async Task A_missing_provenance_does_not_throw()
    {
        // 调用方传 null 是合法的（旧项目、或者某个还没接上 provenance 的路径）。
        var document = await BookAsync("第一章 起点\n正文\n");

        var snapshot = ChapterRepairContextFactory.Capture(document, provenance: null, sourceChanged: false);

        Assert.Equal(TreeBaseOrigin.UnknownLegacy, snapshot.Tree.BaseOrigin);
    }
}
