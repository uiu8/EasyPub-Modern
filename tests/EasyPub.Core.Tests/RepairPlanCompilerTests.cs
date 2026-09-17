using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 把决定编译成产物。
///
/// 两件事要成立，这一层才算数：
///
///   1. **纯** —— 同样输入给同样产物，不写盘、不改任何东西。确认窗能显示"按下按钮会做什么"、
///      以及同一个调用可以重复执行来证明它给出同样的答案，都靠这一条。
///   2. **落地模式只决定用哪套产物**，从不决定一个动作是什么意思。<c>TreeOnly</c> 编译树补丁、
///      不建源补丁；<c>EditSource</c> 两样都编译。同一个动作在两种模式下的语义相同 ——
///      这正是这次重构要消灭的那个缺陷。
/// </summary>
public class RepairPlanCompilerTests
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

    private sealed record Subject(
        ChapterTreeDocument Document,
        RepairProposal Proposal,
        ReferenceCatalog Catalog,
        string RawText);

    private static async Task<Subject> LoadAsync()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, currentDocument: document, reference: catalog);
        var version = RepairBaseVersionFactory.Capture(document.SourceSha256, document.Entries,
            document.RecognitionOptions, null);
        var proposal = RepairProposalFactory.Create("ReferenceCatalog", version, outcome.Plan!, catalog);
        return new Subject(document, proposal, catalog, await File.ReadAllTextAsync(bookPath));
    }

    private static RepairCompileInput Input(Subject subject, RepairLandingMode mode, bool all = true)
    {
        var decisions = subject.Proposal.Actions
            .Select(action =>
            {
                var id = RepairActionIdentity.Compute(action);
                return new RepairDecision(id, all || subject.Proposal.Plan.DefaultSelection.Contains(action),
                    SourceEffectPolicies.Decide(action.Kind, mode));
            })
            .ToArray();
        return new RepairCompileInput(subject.Proposal, subject.Document, decisions, mode,
            BuildVolumeLevels: subject.Catalog.VolumeTitles.Count > 0);
    }

    // ── 纯函数性质 ──────────────────────────────────────────────

    [Fact]
    public async Task Compiling_twice_from_the_same_input_gives_the_same_products()
    {
        var subject = await LoadAsync();
        var input = Input(subject, RepairLandingMode.EditSource);

        var first = RepairPlanCompiler.Compile(input);
        var second = RepairPlanCompiler.Compile(input);

        Assert.True(first.CanApply, first.Describe());
        Assert.Equal(first.TreePatch!.Mutations.Count, second.TreePatch!.Mutations.Count);
        Assert.Equal(
            first.TreePatch.Ordered().Select(m => (m.GetType().Name, m.ActionId, m.Binding.ToString())),
            second.TreePatch.Ordered().Select(m => (m.GetType().Name, m.ActionId, m.Binding.ToString())));
        Assert.Equal(
            first.SourcePatch!.Operations.Select(o => o.OperationId),
            second.SourcePatch!.Operations.Select(o => o.OperationId));
    }

    /// <summary>编译不得改动书稿，也不得改动传进去的任何东西。</summary>
    [Fact]
    public async Task Compiling_does_not_touch_the_book()
    {
        var subject = await LoadAsync();
        var bookPath = subject.Document.SourcePath;
        var before = await File.ReadAllTextAsync(bookPath);
        var beforeEntries = subject.Document.Entries.Count;

        var result = RepairPlanCompiler.Compile(Input(subject, RepairLandingMode.EditSource));

        Assert.True(result.CanApply, result.Describe());
        Assert.Equal(before, await File.ReadAllTextAsync(bookPath));
        Assert.Equal(beforeEntries, subject.Document.Entries.Count);
    }

    // ── 落地模式只决定用哪套产物 ────────────────────────────────

    /// <summary>TreeOnly 下不建源补丁 —— 那个模式的意思就是不动 TXT。</summary>
    [Fact]
    public async Task Tree_only_compiles_the_tree_and_no_source_patch()
    {
        var subject = await LoadAsync();
        var result = RepairPlanCompiler.Compile(Input(subject, RepairLandingMode.TreeOnly));

        Assert.True(result.CanApply, result.Describe());
        Assert.NotNull(result.TreePatch);
        Assert.Null(result.SourcePatch);
        Assert.False(result.SourceChanged);
        // 树该改的仍然要改：模式不改变动作的语义，只决定用哪套产物。
        Assert.NotEmpty(result.TreePatch!.Mutations);
    }

    [Fact]
    public async Task Edit_source_compiles_both_products()
    {
        var subject = await LoadAsync();
        var result = RepairPlanCompiler.Compile(Input(subject, RepairLandingMode.EditSource));

        Assert.True(result.CanApply, result.Describe());
        Assert.NotNull(result.TreePatch);
        Assert.NotNull(result.SourcePatch);
        Assert.NotEmpty(result.TreePatch!.Mutations);
    }

    /// <summary>
    /// 两种模式下的**树补丁必须一致** —— 落地模式不得改变树要变成什么样。
    /// 这条是"模式只决定用哪套产物"这句话最直接的检查。
    /// </summary>
    [Fact]
    public async Task The_tree_patch_is_the_same_in_both_modes()
    {
        var subject = await LoadAsync();
        var treeOnly = RepairPlanCompiler.Compile(Input(subject, RepairLandingMode.TreeOnly));
        var editSource = RepairPlanCompiler.Compile(Input(subject, RepairLandingMode.EditSource));

        Assert.True(treeOnly.CanApply && editSource.CanApply,
            treeOnly.Describe() + " / " + editSource.Describe());

        static IEnumerable<string> Fingerprint(TreePatch patch) => patch.Ordered()
            .Select(m => m.GetType().Name + "|" + m.ActionId + "|" + m.Binding);

        Assert.Equal(Fingerprint(treeOnly.TreePatch!), Fingerprint(editSource.TreePatch!));
        Assert.True(treeOnly.ExpectedResult!.EqualsByContent(editSource.ExpectedResult!));
    }

    /// <summary>
    /// 默认选中在 TreeOnly 下不产生任何文本操作 —— 那些"移除的行"只是不再出现在成品里。
    /// </summary>
    [Fact]
    public async Task Nothing_is_written_when_the_reader_did_not_ask_for_it()
    {
        var subject = await LoadAsync();
        var input = Input(subject, RepairLandingMode.TreeOnly, all: false);
        var result = RepairPlanCompiler.Compile(input);

        Assert.True(result.CanApply, result.Describe());
        Assert.Null(result.SourcePatch);
        // 而被移除的行仍然被记录着：成品会少掉它们，收据要说得出是哪几行。
        Assert.Equal(160, result.RemovedLines.Count);
    }

    // ── 承诺与兑现 ──────────────────────────────────────────────

    /// <summary>
    /// 在改原文模式下全选：树补丁与源补丁都要有，而且预期结果必须与源补丁自洽。
    /// </summary>
    [Fact]
    public async Task Editing_the_source_produces_operations_for_the_actions_that_need_them()
    {
        var subject = await LoadAsync();
        var result = RepairPlanCompiler.Compile(Input(subject, RepairLandingMode.EditSource));

        Assert.True(result.CanApply, result.Describe());
        // 样书里标题与目录写法不同的有 69 章，每一个都该产生一次替换。
        var replaces = result.SourcePatch!.Operations.OfType<ReplaceOriginalLine>().Count();
        Assert.Equal(69, replaces);
        // 重复副本默认只从成品排除，所以不该有删除 —— 用户没有要求删。
        Assert.Empty(result.SourcePatch.Operations.OfType<DeleteOriginalLine>());
    }

    /// <summary>没勾选的动作不得产生任何产物。</summary>
    [Fact]
    public async Task Nothing_selected_compiles_to_nothing()
    {
        var subject = await LoadAsync();
        var decisions = subject.Proposal.Actions
            .Select(action => new RepairDecision(RepairActionIdentity.Compute(action), false,
                SourceEffectDecision.None))
            .ToArray();
        var result = RepairPlanCompiler.Compile(new RepairCompileInput(subject.Proposal, subject.Document,
            decisions, RepairLandingMode.EditSource));

        Assert.True(result.CanApply, result.Describe());
        Assert.Empty(result.TreePatch!.Mutations);
        Assert.True(result.SourcePatch!.IsEmpty);
    }

    /// <summary>项目身份带来的确定性：把决定换一个顺序，产物不变。</summary>
    [Fact]
    public async Task The_order_the_decisions_arrive_in_does_not_change_the_products()
    {
        var subject = await LoadAsync();
        var forward = Input(subject, RepairLandingMode.EditSource);
        var reversed = forward with { Decisions = forward.Decisions.Reverse().ToArray() };

        var first = RepairPlanCompiler.Compile(forward);
        var second = RepairPlanCompiler.Compile(reversed);

        Assert.Equal(
            first.SourcePatch!.Operations.Select(o => o.OperationId),
            second.SourcePatch!.Operations.Select(o => o.OperationId));
        Assert.Equal(first.TreePatch!.Mutations.Count, second.TreePatch!.Mutations.Count);
    }
}
