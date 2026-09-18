using System.IO;
using System.Reflection;
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

    /// <summary>
    /// 设计文档 §7.2 走查表的第 1 步与第 4 步，逐句核对状态条。
    ///
    /// <para>这两步的**数字**早被 <c>SampleBookRegressionTests</c> 锁住，但"用户看到什么"没有 ——
    /// 走查表里那两格一直属于"读过代码、没看过画面"。</para>
    ///
    /// <para>第 4 步是这份设计里最要紧的中间态：<b>目录已就位，而树还没被它校验过</b>。
    /// 原来的单一「修复依据」开关说不出这件事，所以必须由两行同时说出来。</para>
    /// </summary>
    [Fact]
    public void The_walkthrough_can_tell_a_chosen_catalog_from_an_unchosen_one()
    {
        var book = Path.Combine(WorkbenchHarness.SampleRoot(), "缺陷样书.txt");
        var previous = WorkbenchHarness.IsolateCatalogStore();
        string? step1Catalog = null;
        string? step1Tree = null;
        string? step4Catalog = null;
        string? step4Tree = null;
        string? step4Button = null;

        var failure = WorkbenchHarness.OnStaThread(() =>
        {
            var document = ChapterTreeDocument.LoadAsync(book).GetAwaiter().GetResult();
            // 第 1 步：刚导入，树是**本地识别**的（"来源未知"是旧项目才有的第三种说法）。
            var local = document.CreatePlan(document.Entries) with
            {
                Provenance = PersistedTreeProvenance.Local,
            };
            var editor = WorkbenchHarness.ShowOffscreen(new ChapterEditorWindow(
                document, document.RecognitionOptions, null, TextEncodingMode.Auto, savedPlan: local));
            step1Catalog = WorkbenchHarness.Line(editor, "ContextCatalogText");
            step1Tree = WorkbenchHarness.Line(editor, "ContextTreeText");
            WorkbenchHarness.Save(editor, "24-走查第1步-刚导入");

            // 第 4 步：用户已经在目录面板里按过「记住这份目录」。
            // 直接给出那个结果（字段是 private，反射设它等价于用户按过按钮）—— 要断言的是
            // **状态条读不读得懂这个状态**，而不是按钮接线，后者由面板自己那条路径保证。
            SaveCatalogFor(document);
            typeof(ChapterEditorWindow)
                .GetField("_catalogChosenThisSession", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(editor, true);
            // 次序不能反：窗口构造时已经读过一次磁盘并把"没有目录"缓存下来了，
            // 所以必须先让那份缓存失效（产品路径里 Save() 自己就带这一步），
            // 否则状态条会拿旧值说话。
            typeof(ChapterEditorWindow)
                .GetMethod("InvalidateBreakpoints", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, null);
            typeof(ChapterEditorWindow)
                .GetMethod("RefreshContextBar", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(editor, null);
            editor.UpdateLayout();

            step4Catalog = WorkbenchHarness.Line(editor, "ContextCatalogText");
            step4Tree = WorkbenchHarness.Line(editor, "ContextTreeText");
            step4Button = Text(editor, "AutoRepairButtonText");
            WorkbenchHarness.Save(editor, "25-走查第4步-目录已就位树还没变");
        });
        WorkbenchHarness.RestoreCatalogStore(previous);

        Assert.Null(failure);
        // 第 1 步：什么都没有。
        Assert.Equal("未加载", step1Catalog);
        Assert.Equal("本地识别", step1Tree);
        // 第 4 步：目录进来了，**树没动**。§7.2 称这一步是"新设计的关键一步"——
        // 而它只有在「已选用」与「已保存」分得开的时候才说得出来。
        Assert.Contains("已选用", step4Catalog);
        Assert.Contains("480 章", step4Catalog);
        Assert.Equal("本地识别", step4Tree);
        Assert.Contains("参考目录检查", step4Button);
    }
}
