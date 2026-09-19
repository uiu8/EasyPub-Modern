using System.IO;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// **底栏不许说假话。**
///
/// <para>工作台底栏原来无条件写「原始 TXT 不变 · 按章节树顺序输出」。但默认设置下修复是
/// **会写回 TXT** 的（<c>DefaultRepairLandingMode</c> 的默认值是 <c>EditSource</c>）——
/// 于是执行完一次改原文的修复，底栏仍然写着「不变」。这不是排版问题，是界面在说假话，
/// 而且说的正是三条约束里「可核对」那一条最要紧的话。</para>
///
/// <para>现在它说的是**这一次的落地方式**（改原文 / 只改章节树）+ 工作台改动有没有提交。
/// 这条测试钉两件事：不再出现「不变」这种绝对说法；落地方式必须被说出来。</para>
/// </summary>
public class WorkbenchFooterTruthTests
{
    [Fact]
    public void The_footer_states_the_landing_mode_instead_of_claiming_the_source_is_untouched()
    {
        var book = Path.Combine(WorkbenchHarness.SampleRoot(), "缺陷样书.txt");
        var previous = WorkbenchHarness.IsolateCatalogStore();
        string? footer = null;

        var failure = WorkbenchHarness.OnStaThread(() =>
        {
            var document = ChapterTreeDocument.LoadAsync(book).GetAwaiter().GetResult();
            var editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(document));
            footer = WorkbenchHarness.Line(editor, "SaveStateText");
        });
        WorkbenchHarness.RestoreCatalogStore(previous);

        Assert.Null(failure);
        Assert.False(string.IsNullOrWhiteSpace(footer));

        // ① 不许再出现"原文一定没被改"这种绝对说法。
        Assert.DoesNotContain("原始 TXT 不变", footer);
        // ② 必须说清这一次的落地方式 —— 两个取值之一，不能含糊。
        Assert.True(
            footer!.Contains("改原文", StringComparison.Ordinal)
            || footer.Contains("只改章节树", StringComparison.Ordinal),
            "底栏没有说明落地方式：" + footer);
        // ③ 改原文时必须提醒已经备份 —— 那是"可核对"的凭据。
        if (footer.Contains("改原文", StringComparison.Ordinal))
            Assert.Contains("备份", footer);
    }
}
