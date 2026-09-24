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
    public void Catalog_origin_is_undoable_even_when_the_chapter_entries_are_unchanged()
    {
        var previous = WorkbenchHarness.IsolateCatalogStore();
        try
        {
            var failure = WorkbenchHarness.OnStaThread(() =>
            {
                var document = ChapterTreeDocument.LoadAsync(Path.Combine(WorkbenchHarness.SampleRoot(), "缺陷样书.txt")).GetAwaiter().GetResult();
                var editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(document));
                try
                {
                    editor.ApplyRepairEntries(document, "verified-catalog", usedReferenceCatalog: true);
                    Assert.Contains("目录校验后", WorkbenchHarness.Line(editor, "ContextTreeText"));
                    typeof(ChapterEditorWindow).GetMethod("Undo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, null);
                    Assert.Equal("本地识别", WorkbenchHarness.Line(editor, "ContextTreeText"));
                    typeof(ChapterEditorWindow).GetMethod("Redo", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(editor, null);
                    Assert.Contains("目录校验后", WorkbenchHarness.Line(editor, "ContextTreeText"));
                }
                finally { editor.DiscardChangesAndClose(); }
            });
            Assert.Null(failure);
        }
        finally { WorkbenchHarness.RestoreCatalogStore(previous); }
    }

    [Fact]
    public void Local_repair_preserves_the_existing_tree_origin_instead_of_claiming_a_catalog_was_used()
    {
        var book = Path.Combine(WorkbenchHarness.SampleRoot(), "缺陷样书.txt");
        var previous = WorkbenchHarness.IsolateCatalogStore();
        try
        {
            var failure = WorkbenchHarness.OnStaThread(() =>
            {
                var document = ChapterTreeDocument.LoadAsync(book).GetAwaiter().GetResult();
                var editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(document));
                try
                {
                    editor.ApplyRepairEntries(document);
                    Assert.Equal("本地识别", WorkbenchHarness.Line(editor, "ContextTreeText"));
                    editor.ApplyRepairEntries(document, "catalog-fingerprint", usedReferenceCatalog: true);
                    Assert.Contains("目录校验后", WorkbenchHarness.Line(editor, "ContextTreeText"));
                    editor.ApplyRepairEntries(document);
                    Assert.Contains("目录校验后", WorkbenchHarness.Line(editor, "ContextTreeText"));
                }
                finally { editor.DiscardChangesAndClose(); }
            });
            Assert.Null(failure);
        }
        finally { WorkbenchHarness.RestoreCatalogStore(previous); }
    }

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

        // 干净状态下的样书：没有保存过目录、原文没被外部改过。
        // 树是"本地识别"而**不是**"来源未知（旧项目）" —— 后者是"旧项目文件里没有 provenance 字段"
        // 的意思，与"这份树刚按本地规则识别出来"完全是两回事。原来单参数构造时 _provenance
        // 保持初值 Unknown，于是新导入的书被标成旧项目（走查时一眼看出来的错标）。
        Assert.Equal("未加载", catalogLine);
        Assert.Equal("本地识别", treeLine);
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

    /// <summary>
    /// 顶部那句「下一步」说的是**该做什么**，不是"现在是什么状态"。
    ///
    /// <para>这一条来自真实走查的反馈：用户走进工作台，看到的是一串问题码，
    /// 而原来的提示只说了句泛泛的「点主按钮生成建议」—— 既没说有几件事，
    /// 也没说哪一件非要有参考目录不可。他原话是"我确实不知道应该点哪里"。</para>
    ///
    /// <para>所以这里钉住三件事：句子里有**数量**、数量按"要不要目录"分堆、
    /// 并且在他还没有目录时**直接点名该点哪个按钮**。</para>
    /// </summary>
    [Fact]
    public void The_next_step_line_says_what_to_do_and_not_only_what_is_wrong()
    {
        var book = Path.Combine(WorkbenchHarness.SampleRoot(), "缺陷样书.txt");
        var previous = WorkbenchHarness.IsolateCatalogStore();
        string? headline = null;
        string? detail = null;

        var failure = WorkbenchHarness.OnStaThread(() =>
        {
            var document = ChapterTreeDocument.LoadAsync(book).GetAwaiter().GetResult();
            var editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(document));
            headline = WorkbenchHarness.Line(editor, "NextStepHeadline");
            detail = WorkbenchHarness.Line(editor, "NextStepDetail");
            WorkbenchHarness.Save(editor, "26-工作台-下一步做什么");
            editor.Width = 1080;
            editor.Height = 680;
            editor.UpdateLayout();
            WorkbenchHarness.Save(editor, "27-工作台-小屏布局");
        });
        WorkbenchHarness.RestoreCatalogStore(previous);

        Assert.Null(failure);
        // 样书实测：本地漏识别候选为 0（§7.2 第 2 步），所以那 9 处跳章只能靠目录。
        // 当前样书的分类结果是 2 处需要人工核对；数量来自同一份工作台策略。
        Assert.Contains("9 处需要参考目录", headline);
        Assert.Contains("1 处本地就能修", headline);
        Assert.Contains("2 处需要你核对", headline);
        // 没有目录时**点名**该按哪个按钮，而不是让用户自己从六个控件里猜。
        Assert.Contains("选择参考目录", detail);
    }
}
