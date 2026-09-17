using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 没有参考目录时的修复方案。
///
/// 边界是实测出来的：无目录时统一入口返回 <c>Plan = null</c>，两种落地模式都报"没有可用的修复方案"。
/// 但树自身能看出的问题并不少 —— 六卷样书上有 32 组同名标题（64 个条目）、5 处章号重启、
/// 49 个缺号。这些都不依赖外部目录，所以"无目录就什么都做不了"不是能力边界，只是**少了判定来源**。
///
/// 这一层提供那个来源：只发**独立于外部参考就能判定**的动作，而且必须是编译器已经支撑的少数几种
/// （<c>Retitle</c>、<c>RemoveDuplicate</c>）—— 不新增协议，因此整条应用链原样复用。
/// </summary>
public class SelfRepairPlannerTests
{
    private static async Task<ChapterTreeDocument> BookAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-self-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        return await ChapterTreeDocument.LoadAsync(path);
    }

    [Fact]
    public async Task A_book_with_a_repeated_title_offers_a_duplicate_action()
    {
        // 同一个标题连续出现两次，就是这个样书的核心缺陷形状。
        var document = await BookAsync(
            "第一章 起点\n正文一\n第一章 起点\n正文二\n第二章 继续\n正文三\n");

        var plan = SelfRepairPlanner.Build(document);

        var duplicate = Assert.Single(plan.Actions, action => action.Kind == ReferenceActionKind.RemoveDuplicate);
        // 锚点落在**保留**的那一份上（第 1 行），而不是被删的那一份 —— 效果编译器是用"定位到的所有副本
        // 减去动作自己那一行"来算哪些行离开这本书的，锚在被删行上会算反，删掉留下的那一份。
        Assert.Equal(1, duplicate.Line);
        Assert.Equal("第一章 起点", duplicate.Title);
        Assert.True(duplicate.Recommended);
    }

    [Fact]
    public async Task A_book_without_repeats_offers_nothing()
    {
        var document = await BookAsync("第一章 起点\n正文一\n第二章 继续\n正文二\n");

        var plan = SelfRepairPlanner.Build(document);

        Assert.Empty(plan.Actions);
    }

    [Fact]
    public async Task A_bare_number_title_is_not_guessed_at_when_the_tree_did_not_recognise_it()
    {
        // 「1 起点」这类纯数字标题只有在数字识别打开时才会成为树上的标题行。关着的时候树里没有它，
        // 于是自修复**什么也不发** —— 而不是凭"这一行看起来像标题"去改原文。
        // 这一条钉住的正是那条规则：不依赖外部目录就能判定的才做，判不了的不猜。
        var document = await BookAsync("1 起点\n正文一\n2 继续\n正文二\n");

        var plan = SelfRepairPlanner.Build(document);

        Assert.DoesNotContain(plan.Actions, action => action.Kind == ReferenceActionKind.Retitle);
    }

    [Fact]
    public async Task A_canonical_book_gets_no_retitles()
    {
        var document = await BookAsync("第一章 起点\n正文一\n第二章 继续\n正文二\n");

        var plan = SelfRepairPlanner.Build(document);

        Assert.DoesNotContain(plan.Actions, action => action.Kind == ReferenceActionKind.Retitle);
    }

    [Fact]
    public async Task The_self_catalog_names_itself_so_a_reader_knows_where_it_came_from()
    {
        var document = await BookAsync("第一章 起点\n正文一\n第二章 继续\n正文二\n");

        var plan = SelfRepairPlanner.Build(document);

        Assert.Equal(SelfRepairPlanner.SourceName, plan.Catalog.Source);
    }

    [Fact]
    public async Task Every_action_carries_a_reference_the_compiler_can_work_with()
    {
        // 编译器按动作种类取值，而 RemoveDuplicate 的正文归属要靠 Reference 找回位置。
        // 自修复没有外部目录，所以它必须自己造一个能被认出来的节点 —— 否则这一维会变成"不确定"，
        // 动作在 EditSource 下会被拒绝。
        var document = await BookAsync(
            "第一章 起点\n正文一\n第一章 起点\n正文二\n第二章 继续\n正文三\n");

        var plan = SelfRepairPlanner.Build(document);
        foreach (var action in plan.Actions)
        {
            Assert.NotNull(action.Reference);
            Assert.False(string.IsNullOrWhiteSpace(action.Reference!.Title));
        }
    }

    [Fact]
    public async Task The_sample_book_yields_the_repeated_titles_it_actually_has()
    {
        var root = SampleRoot();
        var document = await ChapterTreeDocument.LoadAsync(Path.Combine(root, "缺陷样书.txt"));

        var plan = SelfRepairPlanner.Build(document);

        // 实测：32 组同名标题，涉及 64 个条目 —— 每组保留一份，就是 32 个动作。
        var duplicates = plan.Actions.Count(action => action.Kind == ReferenceActionKind.RemoveDuplicate);
        Assert.Equal(32, duplicates);
    }

    [Fact]
    public async Task A_self_plan_is_accepted_by_the_compiler_in_both_modes()
    {
        // 这是这一层的全部意义：自修复方案必须能走完整的应用链，而不是只能看。
        var root = SampleRoot();
        var document = await ChapterTreeDocument.LoadAsync(Path.Combine(root, "缺陷样书.txt"));
        var plan = SelfRepairPlanner.Build(document);
        var version = RepairBaseVersionFactory.Capture(document.SourceSha256, document.Entries,
            document.RecognitionOptions, null);
        var proposal = RepairProposalFactory.Create(SelfRepairPlanner.BasisName, version, plan);

        foreach (var mode in new[] { RepairLandingMode.TreeOnly, RepairLandingMode.EditSource })
        {
            var decisions = proposal.DefaultDecisions(mode);
            var compilation = RepairPlanCompiler.Compile(new RepairCompileInput(proposal, document, decisions, mode));
            Assert.True(compilation.CanApply, $"{mode}：{compilation.Describe()}");
        }
    }

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
}
