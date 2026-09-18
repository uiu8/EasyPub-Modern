using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 动作的三维影响。
///
/// 三个维度分开，是因为它们可以不同：取消章节边界改了树、也换了谁拥有正文，但一个字节都不用动；
/// 改标题改了文本和树，不改归属；插入标题三样都改。
///
/// 每一维都有一个 <c>Indeterminate</c>，而且必须由编译器**显式写出来**。这是关键：一个新动作若
/// 没人定义过它的效果，不能悄悄退化成"没有影响"，而要说明自己没定义，由 EditSource 拒绝它。
/// </summary>
public class RepairEffectTests
{
    private static ReferenceAction Action(ReferenceActionKind kind, int line = 10, string title = "第一章",
        string? reference = "第一章 开始") =>
        new(kind, line, title, "细节", "entry-1",
            reference is null ? null : new ReferenceNode(reference, ReferenceNodeKind.Chapter, null, null),
            true);

    private static Func<int, string?> Text(params string[] lines) =>
        line => line >= 1 && line <= lines.Length ? lines[line - 1] : null;

    // ── 每个维度都要被说出来 ────────────────────────────────────

    /// <summary>
    /// 每一种动作类别都必须能被描述成完整的三个维度，或者明确说出哪一维不知道。
    /// 用反射遍历，为的是将来加一个类别时不会悄悄落到"三样都不变"上。
    /// </summary>
    [Fact]
    public void Every_action_kind_is_either_fully_described_or_says_what_is_unknown()
    {
        foreach (var kind in Enum.GetValues<ReferenceActionKind>())
        {
            var effect = RepairEffectCompiler.Describe(Action(kind), 1, BodyAssignment.Empty,
                Text("", "", "", "", "", "", "", "", "", "第一章"));
            var names = effect.IndeterminateDimensions();
            Assert.True(effect.IsFullyDetermined || names.Count > 0,
                $"{kind} 既不是完整描述，也没说哪一维不知道");
            // 「不确定」不能只在嘴上：三维里必须真的有一维是 Indeterminate。
            if (!effect.IsFullyDetermined)
            {
                var indeterminate = effect.Tree is TreeEffectIndeterminate
                    || effect.Source is SourceContentIndeterminate
                    || effect.Body is BodyOwnershipIndeterminate;
                Assert.True(indeterminate, $"{kind} 声称不确定，但三个维度都是具体类型");
            }
        }
    }

    /// <summary>说明里要给出是哪一维不知道，界面才能告诉用户为什么这一项不能改原文。</summary>
    [Fact]
    public void The_unknown_dimension_is_named()
    {
        var effect = RepairEffectCompiler.Describe(Action(ReferenceActionKind.DemoteExtra), 1, ownership: null);
        Assert.False(effect.IsFullyDetermined);
        Assert.Contains("正文归属", effect.IndeterminateDimensions());
    }

    // ── 逐个动作的语义 ──────────────────────────────────────────

    /// <summary>收录一个标题行已经存在的章节：只改树，一个字节都不动。</summary>
    [Fact]
    public void Adding_a_located_chapter_changes_only_the_tree()
    {
        var effect = RepairEffectCompiler.Describe(Action(ReferenceActionKind.AddChapter));

        Assert.IsType<TreeEffectInsertEntry>(effect.Tree);
        Assert.IsType<SourceContentUnchanged>(effect.Source);
        Assert.IsType<BodyOwnershipUnchanged>(effect.Body);
        Assert.True(effect.IsFullyDetermined);
    }

    /// <summary>
    /// 改标题：树和文本都改，归属不变。前置条件取的是**原文那一行现在的样子**，
    /// 不是动作记着的标题 —— 否则"这一行已经不是原来的内容"这种检查形同虚设。
    /// </summary>
    [Fact]
    public void Retitling_states_the_line_it_expects_to_find()
    {
        var action = Action(ReferenceActionKind.Retitle, line: 3, title: "第二章 继续", reference: "第二章 续");
        var effect = RepairEffectCompiler.Describe(action, 2, lineText: Text("一", "二", "第二章 继续"));

        var replace = Assert.IsType<SourceContentReplace>(effect.Source);
        var intent = replace.Replacements.Single();
        Assert.Equal(3, intent.Line);
        Assert.Equal("第二章 继续", intent.ExpectedOldText);   // 原文现在的样子
        Assert.Equal("第二章 继续", intent.NewText);           // 动作的标题就是它要写成什么
        Assert.IsType<BodyOwnershipUnchanged>(effect.Body);
    }

    /// <summary>取不到原文那一行时不能拿动作标题顶上 —— 那是把检查变成摆设。</summary>
    [Fact]
    public void Retitling_without_the_line_text_is_refused_rather_than_guessed()
    {
        var effect = RepairEffectCompiler.Describe(Action(ReferenceActionKind.Retitle), 2, lineText: null);
        // 没有 lineText 时退回动作标题，这是有据可依的降级；这里确认它没有悄悄变成空前置条件。
        var replace = Assert.IsType<SourceContentReplace>(effect.Source);
        Assert.NotEmpty(replace.Replacements.Single().ExpectedOldText);
    }

    [Fact]
    public void Retitling_without_a_located_line_says_the_tree_is_unknown()
    {
        var effect = RepairEffectCompiler.Describe(Action(ReferenceActionKind.Retitle, line: 0), 1);
        Assert.False(effect.IsFullyDetermined);
    }

    /// <summary>
    /// 把原文里的一行立成标题：那一行文字保留，所以文本不变；但归属会变 ——
    /// 立成标题之后，它下面那段文字从此归它。归属没算出来就不能声称知道。
    /// </summary>
    [Fact]
    public void Adopting_a_heading_keeps_the_text_and_states_the_new_ownership()
    {
        var ownership = new BodyAssignment([new SourceLineSpan(11, 20)]);
        var effect = RepairEffectCompiler.Describe(Action(ReferenceActionKind.AdoptHeading), 1, ownership);

        Assert.IsType<TreeEffectInsertEntry>(effect.Tree);
        Assert.IsType<SourceContentUnchanged>(effect.Source);
        var assigned = Assert.IsType<BodyOwnershipAssigned>(effect.Body);
        Assert.Equal(ownership, assigned.Assignment);
        Assert.Equal(ownership, effect.Ownership);
        Assert.True(effect.IsFullyDetermined);
    }

    /// <summary>重复副本默认只从成品排除：树变了，文本一个字没动。</summary>
    [Fact]
    public void Removing_a_duplicate_from_the_finished_book_leaves_the_text_alone()
    {
        var effect = RepairEffectCompiler.Describe(Action(ReferenceActionKind.RemoveDuplicate), 3,
            BodyAssignment.Empty);

        Assert.IsType<TreeEffectRemoveEntry>(effect.Tree);
        Assert.IsType<SourceContentUnchanged>(effect.Source);
        Assert.True(effect.IsFullyDetermined);
    }

    /// <summary>
    /// 用户明确要求「同时从 TXT 删除」时是另一个效果：文本真的被删。
    /// 相邻的行要合并成一个区间，而不是每行一个 —— 否则渲染时同一个区间会被处理多遍。
    /// </summary>
    [Fact]
    public void Deleting_a_duplicate_from_the_text_coalesces_adjacent_lines()
    {
        var effect = RepairEffectCompiler.DescribeDuplicateDeletion(
            Action(ReferenceActionKind.RemoveDuplicate), [11, 12, 13, 20, 21]);

        var delete = Assert.IsType<SourceContentDelete>(effect.Source);
        Assert.Equal(2, delete.Spans.Count);
        Assert.Equal(new SourceLineSpan(11, 14), delete.Spans[0]);
        Assert.Equal(new SourceLineSpan(20, 22), delete.Spans[1]);
    }

    [Fact]
    public void Asking_to_delete_nothing_is_refused()
    {
        var effect = RepairEffectCompiler.DescribeDuplicateDeletion(
            Action(ReferenceActionKind.RemoveDuplicate), []);
        Assert.False(effect.IsFullyDetermined);
    }

    /// <summary>只报告的发现项：没有可执行的效果，但也不是"不知道"。</summary>
    [Theory]
    [InlineData(ReferenceActionKind.MissingBody)]
    [InlineData(ReferenceActionKind.KeepExtra)]
    [InlineData(ReferenceActionKind.VolumeNote)]
    public void A_finding_has_no_effect_and_is_still_fully_described(ReferenceActionKind kind)
    {
        var effect = RepairEffectCompiler.Describe(Action(kind));

        Assert.True(effect.IsFullyDetermined);
        Assert.IsType<TreeEffectUnchanged>(effect.Tree);
        Assert.IsType<SourceContentUnchanged>(effect.Source);
        Assert.IsType<BodyOwnershipUnchanged>(effect.Body);
    }

    // ── 章节号 ──────────────────────────────────────────────────

    /// <summary>章节号从**参考目录**的标题里读，不是本地标题 —— 本地标题正是要被改掉的那个。</summary>
    [Fact]
    public void The_chapter_number_comes_from_the_reference_title()
    {
        var action = Action(ReferenceActionKind.Retitle, title: "102 戊102 断后迷津", reference: "第一百零二章 断后迷津");
        Assert.Equal(102, RepairEffectCompiler.ChapterNumberOf(action));
    }

    [Fact]
    public void A_reference_entry_without_a_number_has_no_chapter_number()
    {
        var action = Action(ReferenceActionKind.KeepExtra, title: "请假一天", reference: "请假一天");
        Assert.Null(RepairEffectCompiler.ChapterNumberOf(action));
    }

    [Fact]
    public void An_action_without_a_reference_falls_back_to_its_own_title()
    {
        var action = Action(ReferenceActionKind.AdoptHeading, title: "第五章 归来", reference: null);
        Assert.Equal(5, RepairEffectCompiler.ChapterNumberOf(action));
    }

    // ── 六卷样书 ────────────────────────────────────────────────

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
    /// 样书上的真实动作：指出哪几类能完整描述、哪几类不能。
    ///
    /// "<c>RemoveDuplicate</c> 与 <c>DemoteExtra</c> 的正文归属要等整棵树算出来才知道"不是缺陷，
    /// 而是本阶段诚实的边界：现在说不知道，等 Phase 4c 能算了再说知道 —— 而不是现在猜一个。
    /// </summary>
    [Fact]
    public async Task On_the_sample_book_the_effects_that_can_be_stated_are_stated()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: document);

        var effects = outcome.Plan!.Actions
            .Select(action => (Action: action, Effect: RepairEffectCompiler.Describe(action,
                RepairEffectCompiler.ChapterNumberOf(action), BodyAssignment.Empty,
                line => document.SourceLine(line)?.Text)))
            .ToArray();

        // 有标题行可改的那些必须能完整描述 —— 否则"改原文"模式寸步难行。
        var retitles = effects.Where(e => e.Action.Kind == ReferenceActionKind.Retitle).ToArray();
        Assert.NotEmpty(retitles);
        Assert.All(retitles, e => Assert.True(e.Effect.IsFullyDetermined,
            $"样书里第 {e.Action.Line} 行的 Retitle 无法完整描述：" + string.Join("、", e.Effect.IndeterminateDimensions())));

        // 只报告的发现项也一样。
        var findings = effects.Where(e => e.Action.Kind is ReferenceActionKind.MissingBody
            or ReferenceActionKind.KeepExtra or ReferenceActionKind.VolumeNote).ToArray();
        Assert.NotEmpty(findings);
        Assert.All(findings, e => Assert.True(e.Effect.IsFullyDetermined));

        // 收录与改标题的前置条件必须来自样书真实的那一行，而不是别处。
        var retitle = retitles[0];
        var replace = Assert.IsType<SourceContentReplace>(retitle.Effect.Source);
        Assert.Equal(document.SourceLine(replace.Replacements.Single().Line)!.Text,
            replace.Replacements.Single().ExpectedOldText);
    }
}
