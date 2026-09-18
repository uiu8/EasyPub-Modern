using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 「忘记这份目录」与「重新识别章节」是**两个独立操作**（设计文档 §3.3）。
///
/// <para>这条测试锁的是一件具体的事：忘掉目录记录之后，**章节树必须保持原样**。
/// 把两件事合成一个按钮，用户按一下就会以为记录和树一起回退了 —— 那正是 §3.1 里
/// "清除目录就等于回到书自身规则"那个错误模型。三行事实能同时说出「未加载」与「目录校验后」，
/// 就是这两件事彼此独立的证据。</para>
///
/// <para>顺带锁住主按钮<b>按依据变名</b>：它说的依据必须与
/// <c>ChapterAutoRepair.ReadSavedCatalog</c> 读的是同一处记录，否则按钮又变成一句不可信的承诺。</para>
/// </summary>
public class WorkbenchForgetCatalogTests
{
    private static string Text(Window window, string name) =>
        (window.FindName(name) as TextBlock)?.Text
        ?? throw new InvalidOperationException($"工作台里找不到 {name}");

    /// <summary>写一份"本书保存过的目录"进隔离后的存储根。目录文本取自样书素材。</summary>
    private static void SaveCatalogFor(ChapterTreeDocument document)
    {
        var text = File.ReadAllText(
            Path.Combine(WorkbenchHarness.SampleRoot(), "目录.txt"), Encoding.UTF8);
        var catalog = ReferenceCatalogInput.ParseText(text);
        Assert.NotNull(catalog);
        ReferenceCatalogInput.SaveCatalog(document.SourceSha256, catalog!, "https://example.invalid/样书");
    }

    [Fact]
    public void Forgetting_the_catalog_leaves_the_tree_exactly_as_it_was()
    {
        var book = Path.Combine(WorkbenchHarness.SampleRoot(), "缺陷样书.txt");
        var previous = WorkbenchHarness.IsolateCatalogStore();
        string? catalogBefore = null;
        string? treeBefore = null;
        string? treeAfter = null;
        string? catalogAfter = null;
        string? buttonBefore = null;
        string? buttonAfter = null;
        Visibility forgetBefore = Visibility.Visible;
        Visibility forgetAfter = Visibility.Visible;
        var entryCountBefore = 0;
        var entryCountAfter = 0;

        var failure = WorkbenchHarness.OnStaThread(() =>
        {
            var document = ChapterTreeDocument.LoadAsync(book).GetAwaiter().GetResult();
            SaveCatalogFor(document);
            var plan = document.CreatePlan(document.Entries) with
            {
                Provenance = PersistedTreeProvenance.FromCatalog("https://example.invalid/样书"),
            };
            var editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(
                document, document.RecognitionOptions, null, TextEncodingMode.Auto, savedPlan: plan));

            catalogBefore = WorkbenchHarness.Line(editor, "ContextCatalogText");
            treeBefore = WorkbenchHarness.Line(editor, "ContextTreeText");
            buttonBefore = Text(editor, "AutoRepairButtonText");
            forgetBefore = ((Button)editor.FindName("ForgetCatalogButton")!).Visibility;
            entryCountBefore = editor.Roots.Count;
            WorkbenchHarness.Save(editor, "22-工作台-有已保存目录");

            ((Button)editor.FindName("ForgetCatalogButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            catalogAfter = WorkbenchHarness.Line(editor, "ContextCatalogText");
            treeAfter = WorkbenchHarness.Line(editor, "ContextTreeText");
            buttonAfter = Text(editor, "AutoRepairButtonText");
            forgetAfter = ((Button)editor.FindName("ForgetCatalogButton")!).Visibility;
            entryCountAfter = editor.Roots.Count;
            WorkbenchHarness.Save(editor, "23-工作台-忘记目录之后");
        });
        WorkbenchHarness.RestoreCatalogStore(previous);

        Assert.Null(failure);

        // 忘掉之前：第一行说得出这份目录的规模，主按钮说的是**参考目录**那条依据，
        // 而且「忘记这份目录」这个出口就在它旁边 —— 不必先猜它藏在哪个菜单里。
        Assert.Contains("已保存", catalogBefore);
        Assert.Contains("480 章", catalogBefore);
        Assert.Equal(Visibility.Visible, forgetBefore);
        // "已保存的"这三个字是必要的：第一行说这份目录"未重新选择"，按钮必须说清它用的就是那一份，
        // 否则两句话读起来互相打架 —— 这一条是渲染出截图之后才看出来的。
        Assert.Contains("按已保存的参考目录检查", buttonBefore);

        // 忘掉之后：记录没了，所以第一行回到"未加载"，按钮也没了 —— **但树一格都没变**。
        Assert.Equal("未加载", catalogAfter);
        Assert.Equal(Visibility.Collapsed, forgetAfter);
        Assert.Equal(entryCountBefore, entryCountAfter);

        // 这两条是全部要点：第二行**仍然**是"目录校验后"，而主按钮改说了另一条依据。
        // 记录与树是两件事，忘掉前者不会撤销后者。
        Assert.Equal(treeBefore, treeAfter);
        Assert.Contains("目录校验后", treeAfter);
        Assert.Contains("基于当前章节树检查", buttonAfter);
    }
}
