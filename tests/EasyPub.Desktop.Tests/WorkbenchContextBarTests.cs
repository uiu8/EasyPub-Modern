using System.IO;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 工作台顶部那**三行事实** —— 文档 §3.2。
///
/// <para>它们替换掉的是原来那个单一的「修复依据」状态条。那个模型把所有情况压成一个开关，
/// 于是总有一半是假的；这里钉住的是三行**各说各的、不能互相推导**：</para>
///
/// <list type="bullet">
/// <item>参考目录 —— 磁盘上 / 本次有没有一份可用的目录；</item>
/// <item>当前章节树 —— 它是怎么来的（这一行**不能**从上一行推出来）；</item>
/// <item>原文版本 —— 文件是否在程序之外被改过。</item>
/// </list>
///
/// <para>设 <c>EASYPUB_SCREENSHOT_DIR</c> 会把这个窗口写成 PNG，供人工核对外观。</para>
/// </summary>
public class WorkbenchContextBarTests
{
    [Fact]
    public void The_workbench_states_three_facts_as_three_separate_lines()
    {
        var book = Path.Combine(WorkbenchHarness.SampleRoot(), "缺陷样书.txt");
        var previous = WorkbenchHarness.IsolateCatalogStore();
        string? catalogLine = null;
        string? treeLine = null;
        string? sourceLine = null;

        var failure = WorkbenchHarness.OnStaThread(() =>
        {
            var document = ChapterTreeDocument.LoadAsync(book).GetAwaiter().GetResult();
            var editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(document));

            catalogLine = WorkbenchHarness.Line(editor, "ContextCatalogText");
            treeLine = WorkbenchHarness.Line(editor, "ContextTreeText");
            sourceLine = WorkbenchHarness.Line(editor, "ContextSourceText");

            WorkbenchHarness.Save(editor, "20-工作台-三行事实");
        });
        WorkbenchHarness.RestoreCatalogStore(previous);

        Assert.Null(failure);

        // 干净状态下的样书：没有保存过目录、没有 provenance（旧项目语义）、原文没被外部改过。
        // **三行各说各的** —— 这正是单一开关做不到的。
        Assert.Equal("未加载", catalogLine);
        Assert.Contains("来源未知", treeLine);
        Assert.Equal("正常", sourceLine);
    }

    [Fact]
    public void A_verified_tree_is_named_as_such_even_when_no_catalog_is_loaded()
    {
        // 「清除目录记录」不等于「树回到本地识别」：树绑的是那次修复，不是那条记录。
        // 所以第一行可以是"未加载"而第二行是"目录校验后" —— 三行必须能同时成立。
        var book = Path.Combine(WorkbenchHarness.SampleRoot(), "缺陷样书.txt");
        var previous = WorkbenchHarness.IsolateCatalogStore();
        string? catalogLine = null;
        string? treeLine = null;

        var failure = WorkbenchHarness.OnStaThread(() =>
        {
            var document = ChapterTreeDocument.LoadAsync(book).GetAwaiter().GetResult();
            var plan = document.CreatePlan(document.Entries) with
            {
                Provenance = PersistedTreeProvenance.FromCatalog("样书目录"),
            };
            var editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(
                document, document.RecognitionOptions, null, TextEncodingMode.Auto, savedPlan: plan));

            catalogLine = WorkbenchHarness.Line(editor, "ContextCatalogText");
            treeLine = WorkbenchHarness.Line(editor, "ContextTreeText");

            WorkbenchHarness.Save(editor, "21-工作台-目录树但无目录记录");
        });
        WorkbenchHarness.RestoreCatalogStore(previous);

        Assert.Null(failure);
        Assert.Equal("未加载", catalogLine);
        Assert.Contains("目录校验后", treeLine);
    }
}
