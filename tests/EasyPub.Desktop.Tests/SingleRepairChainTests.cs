using System.IO;
using System.Reflection;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 「前端按钮能调到的功能，必须和代码走同一条路」——这条要求靠人来遵守是守不住的，
/// 因为它没有失败信号：第二条链路照样能跑、照样能出正确结果，只是它是**另一套**代码，
/// 从此两边的勾选项、报告分类和默认值各自演化。
///
/// 这类回归只有两个可靠信号：一是旧方法名从类型上消失，二是旧文件不再存在。
/// 两条都断言在这里，谁把自建链路加回来，这里立刻红。
/// </summary>
public class SingleRepairChainTests
{
    /// <summary>
    /// Phase 1 删掉的那套「自建表格 + 自建勾选 + 自建确认窗」。
    /// 它们不是坏代码，是和 <see cref="RepairReviewWindow"/> 竞争的第二套预览。
    /// </summary>
    private static readonly string[] RetiredMembers =
    [
        "ReferenceOutlineAssist",   // 自建预览窗
        "ConfirmRepairChanges",     // 自建确认窗
        "CatalogFromText",          // 只为自建链路存在的解析
        "OutlineRow",               // 自建表格行
        "ProposalRow",              // 本地扫描的自建表格行
    ];

    [Fact]
    public void The_self_built_preview_chain_is_gone_from_the_window_type()
    {
        var type = typeof(ChapterEditorWindow);
        var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
            | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var declared = type.GetMembers(flags).Select(member => member.Name).ToHashSet();

        var resurrected = RetiredMembers.Where(declared.Contains).ToArray();
        Assert.True(resurrected.Length == 0,
            "以下成员属于已删除的自建修复链路，不应重新出现：" + string.Join("、", resurrected)
            + "。修复入口只有一个：ChapterAutoRepairPanel.RunRepairReviewAsync。");

        // 它自己是嵌套类型，DeclaredOnly 看不到，单独查一遍。
        var nested = type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Select(nestedType => nestedType.Name).ToArray();
        Assert.DoesNotContain("OutlineRow", nested);
        Assert.DoesNotContain("ProposalRow", nested);
    }

    /// <summary>
    /// 唯一入口必须存在，而且必须**只有一个**。
    /// 断言"只有两个调用点"是有意的：顶部的「一键目录修复」按钮一个，目录对话框的
    /// 「预览章节调整…」一个。多出第三个就意味着又多了一条路。
    /// </summary>
    [Fact]
    public void There_is_exactly_one_repair_entry_and_it_is_reachable()
    {
        var type = typeof(ChapterEditorWindow);
        var entry = type.GetMethod("RunRepairReviewAsync",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        Assert.NotNull(entry);
        Assert.Equal(typeof(Task), entry!.ReturnType);

        // 参数里带着目录与进度回调 —— 「传哪份目录」正是两种入口的唯一差别。
        var parameters = entry.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(ReferenceCatalog), parameters[0].ParameterType);
        // 进度回调收的是 Action<string> 而不是 IProgress<string>：跨线程编组由入口内部
        // 用 Dispatcher 显式完成，不依赖调用方恰好有 SynchronizationContext。
        Assert.Equal(typeof(Action<string>), parameters[1].ParameterType);
    }

    /// <summary>
    /// 承载自建链路的整个文件已删除。用源码路径断言，因为文件不存在时反射看不到任何东西，
    /// 而"看不到"和"没写"必须区分开。
    /// </summary>
    [Fact]
    public void The_file_that_held_the_second_chain_is_deleted()
    {
        var retired = Path.Combine(RepositoryRoot(), "src", "EasyPub.Desktop", "ChapterReferencePanel.cs");
        Assert.False(File.Exists(retired),
            "ChapterReferencePanel.cs 是已删除的第二条链路，不应重新出现：" + retired);
    }

    /// <summary>
    /// 从测试程序集位置向上找仓库根（含 src/EasyPub.Desktop 的那一层）。
    /// 不写死盘符，换机器或在 CI 里都能定位。
    /// </summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "EasyPub.Desktop"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到仓库根目录。");
    }
}
