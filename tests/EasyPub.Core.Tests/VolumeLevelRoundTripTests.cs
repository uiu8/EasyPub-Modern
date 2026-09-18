using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 卷层级必须真的建在用户拿到的那棵树上。
///
/// 这条测试是有来历的：一次探针里「报告说建立 6 卷，但 <see cref="ChapterAutoRepair.RebuildWithSelection"/>
/// 返回的树一个卷都没有」，看着像生产缺陷，实际是探针用了 <see cref="ChapterAutoRepair.Prepare"/>
/// 而 Prepare 不设置 <c>Catalog</c> —— 那个字段只有 <see cref="ChapterAutoRepair.RepairAsync"/> 才设，
/// 而 <c>RebuildWithSelection</c> 的 <c>buildVolumeLevels</c> 参数依赖它。
///
/// 也就是说：**生产路径（按钮 → RepairAsync）是对的，测试路径错了**。为了不再让下一个探针
/// 或下一个测试踩同一个坑，这里把两条路径都固定下来 —— 谁改坏了哪条都会红。
/// </summary>
public class VolumeLevelRoundTripTests
{
    private static string SampleRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "六卷缺陷样书");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 samples/六卷缺陷样书。");
    }

    /// <summary>
    /// 按钮走的那条：<c>RepairAsync</c> → <c>RebuildWithSelection</c>。
    /// 用已保存目录的形态直接把它传进去，等价于 UI 里目录已经存在的情形 —— 不联网。
    /// </summary>
    [Fact]
    public async Task The_button_path_puts_volumes_on_the_tree()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;

        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: document);
        Assert.True(outcome.CatalogFound);
        Assert.NotNull(outcome.Catalog);

        var rebuilt = ChapterAutoRepair.RebuildWithSelection(
            document, outcome, outcome.Plan!.DefaultSelection.ToArray());

        var volumes = rebuilt.Where(e => e.RecognitionSource is "reference-volume" or "inferred-volume").ToArray();
        Assert.Equal(6, volumes.Length);
        Assert.Equal(6, outcome.RebuiltVolumes);
        // 卷必须真的进了树 —— 报告里的「建立卷层级 6 卷」说的就是这件事。
        Assert.Equal(volumes.Length, rebuilt.Count(e => e.Level == 1 && e.TitleLineNumber is not null));
    }

    /// <summary>
    /// 直接 <c>Prepare</c> 得到的 outcome **不带** Catalog。这不是缺陷，是分工：
    /// Prepare 是纯计算，联网/装配由 RepairAsync 负责。把它固定下来，避免下一个人
    /// 再拿 Prepare 的结果去调用依赖 Catalog 的方法，然后得出"卷丢了"的结论。
    /// </summary>
    [Fact]
    public async Task Prepare_alone_does_not_carry_the_catalog_so_callers_must_not_rely_on_it()
    {
        var root = SampleRoot();
        var document = await ChapterTreeDocument.LoadAsync(Path.Combine(root, "缺陷样书.txt"));
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;

        var direct = ChapterAutoRepair.Prepare(document, catalog);
        Assert.Null(direct.Catalog);
        Assert.True(direct.CatalogFound);

        // 而经过 RepairAsync 之后同一个字段就有了。
        var viaEntryPoint = await ChapterAutoRepair.RepairAsync(
            document.SourcePath, new ReferenceRepairRequest(catalog), currentDocument: document);
        Assert.NotNull(viaEntryPoint.Catalog);
    }
}
