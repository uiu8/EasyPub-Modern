using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 新旧行号对照表：它是把内存里所有指向"第几行"的东西从旧版本搬到新版本的唯一桥梁。
///
/// 这一层最危险的失效方式是**静默错位**——表错一行，后面每一个行号都指到别处，而程序不会报错。
/// 所以这里的测试分两类：
///
///   1. 结构正确：保留行的落点、删除行的缺席、替换行的落点、插入行的插入点。
///   2. **拒绝错误**：表与补丁不符、文本与补丁不符、镜像不自洽，都必须被验证器挡住，而不是被用下去。
/// </summary>
public class SourceCoordinateMapTests
{
    /// <summary>造一份小书稿：四行标题 + 正文，行号好数，断言好写。</summary>
    private static SourceTextDocument Original() => SourceTextDocument.Parse(
        "第一章 起点\n" +
        "正文一\n" +
        "第二章 中途\n" +
        "正文二\n" +
        "第三章 终点\n" +
        "正文三\n");

    private static SourcePatch Patch(params SourceOperation[] operations) =>
        new("test", "sha", operations);

    private static ReplaceOriginalLine Replace(int line, string newText, string oldText) =>
        new(line, oldText, newText) { OperationId = $"rep-{line}", ActionId = "a" };

    private static DeleteOriginalLine Delete(int line, string oldText) =>
        new(line, oldText) { OperationId = $"del-{line}", ActionId = "a" };

    private static InsertLineAtAnchor Insert(SourceAnchor anchor, string text, string id) =>
        new(anchor, text) { OperationId = id, ActionId = "a" };

    [Fact]
    public void Keeping_every_line_maps_each_line_to_itself()
    {
        var original = Original();
        var map = SourceCoordinateMap.Build(original, Patch(), original.Render());

        Assert.Equal(6, map.OriginalLineCount);
        Assert.Equal(6, map.NewLineCount);
        Assert.Equal(6, map.PlacedLineCount);
        Assert.True(map.TryGetUniformOffset(out var shift));
        Assert.Equal(0, shift);
        for (var line = 1; line <= 6; line++)
        {
            Assert.Equal(line, map.TryMap(line));
            Assert.Equal(line, map.SourceOf(line));
        }
        Assert.Empty(map.DeletedLines);
        Assert.Empty(map.ReplacedLines);
        Assert.Empty(map.InsertedLines);
    }

    [Fact]
    public void A_deleted_line_has_no_new_line_and_everything_below_shifts_up()
    {
        var original = Original();
        var patch = Patch(Delete(2, "正文一"));
        var result = SourcePatchRenderer.Render(original, patch);
        var map = SourceCoordinateMap.Build(original, patch, result.Text);

        Assert.Null(map.TryMap(2));
        Assert.True(map.IsDeleted(2));
        // 删掉一行之后，它下面的每一行都上移一位。
        Assert.Equal(2, map.TryMap(3));
        Assert.Equal(5, map.TryMap(6));
        Assert.Equal(5, map.NewLineCount);
        Assert.Single(map.DeletedLines);
        Assert.True(map.Verify(original, patch, result.Text).IsSound, map.Verify(original, patch, result.Text).Message);
    }

    [Fact]
    public void A_uniform_offset_is_reported_only_when_every_surviving_line_moved_the_same_way()
    {
        // 末尾删一行：上面没动、下面没有，所以确实是一次整体平移。
        var original = Original();
        var tail = Patch(Delete(6, "正文三"));
        var tailMap = SourceCoordinateMap.Build(original, tail, SourcePatchRenderer.Render(original, tail).Text);
        Assert.True(tailMap.TryGetUniformOffset(out var tailShift));
        Assert.Equal(0, tailShift);

        // 中间删一行：上面没动、下面上移，位移不统一 —— 表是对的，但它不是一次整体平移。
        var middle = Patch(Delete(2, "正文一"));
        var middleMap = SourceCoordinateMap.Build(original, middle, SourcePatchRenderer.Render(original, middle).Text);
        Assert.False(middleMap.TryGetUniformOffset(out _));
    }

    [Fact]
    public void A_replaced_line_keeps_its_position_and_points_at_the_new_text()
    {
        var original = Original();
        var patch = Patch(Replace(3, "第二章 改过的标题", "第二章 中途"));
        var result = SourcePatchRenderer.Render(original, patch);
        var map = SourceCoordinateMap.Build(original, patch, result.Text);

        Assert.Equal(3, map.TryMap(3));
        Assert.True(map.IsReplaced(3));
        Assert.Equal(3, map.LineOfOperation("rep-3"));
        Assert.Equal(3, map.OriginalLineOfOperation["rep-3"]);
        Assert.True(map.Verify(original, patch, result.Text).IsSound);
    }

    [Fact]
    public void An_inserted_line_is_placed_where_its_anchor_says()
    {
        var original = Original();
        var patch = Patch(Insert(new BeforeOriginalLine(3), "标题之前", "ins-a"));
        var result = SourcePatchRenderer.Render(original, patch);
        var map = SourceCoordinateMap.Build(original, patch, result.Text);

        Assert.Equal(3, map.LineOfOperation("ins-a"));
        Assert.Contains(3, map.InsertedLines);
        Assert.Null(map.SourceOf(3));
        // 被插入行挤下去的那一行还在，只是往后挪了一位。
        Assert.Equal(4, map.TryMap(3));
        Assert.Equal(7, map.NewLineCount);
        Assert.Equal(7, map.PlacedLineCount);
        Assert.True(map.Verify(original, patch, result.Text).IsSound);
    }

    [Fact]
    public void Rearranging_the_input_still_places_a_replacement_on_its_own_line()
    {
        // 单遍渲染，所以操作的存放顺序不改变结果；对照表也必须跟着无关。
        var original = Original();
        var forward = Patch(Replace(1, "第一章 甲", "第一章 起点"), Replace(5, "第三章 丙", "第三章 终点"));
        var backward = Patch(Replace(5, "第三章 丙", "第三章 终点"), Replace(1, "第一章 甲", "第一章 起点"));

        var a = SourceCoordinateMap.Build(original, forward, SourcePatchRenderer.Render(original, forward).Text);
        var b = SourceCoordinateMap.Build(original, backward, SourcePatchRenderer.Render(original, backward).Text);

        Assert.Equal(a.TryMap(1), b.TryMap(1));
        Assert.Equal(a.TryMap(5), b.TryMap(5));
        Assert.Equal(a.LineOfOperation("rep-1"), b.LineOfOperation("rep-1"));
    }

    [Fact]
    public void Rebind_refuses_a_line_whose_text_was_rewritten()
    {
        var original = Original();
        var patch = Patch(Replace(3, "第二章 改过的标题", "第二章 中途"));
        var map = SourceCoordinateMap.Build(original, patch, SourcePatchRenderer.Render(original, patch).Text);

        // 标题行还在，但已经不是原来那一行；身份不能悄悄跟过去。
        Assert.Null(map.Rebind("L3"));
        Assert.Equal("L1", map.Rebind("L1"));
        // 标题键不是行号，原样返回。
        Assert.Equal("T某个还没插入的章节", map.Rebind("T某个还没插入的章节"));
    }

    [Fact]
    public void Rebind_moves_an_identity_that_survived_onto_the_new_numbering()
    {
        var original = Original();
        var patch = Patch(Delete(2, "正文一"));
        var map = SourceCoordinateMap.Build(original, patch, SourcePatchRenderer.Render(original, patch).Text);

        Assert.Null(map.Rebind("L2"));
        Assert.Equal("L2", map.Rebind("L3"));
        Assert.Equal("L5", map.Rebind("L6"));
    }

    // ---- 下面全部是"必须拒绝"。验证器的价值在于它能挡住什么，不在于它能放过什么。 ----

    [Fact]
    public void Verification_refuses_a_map_that_describes_a_different_patch()
    {
        var original = Original();
        var withReplace = Patch(Replace(3, "第二章 改过的标题", "第二章 中途"));
        var text = SourcePatchRenderer.Render(original, withReplace).Text;

        // 表是照"有替换"的补丁造的，却拿去配一个空补丁 —— 这正是"补丁与文本不符"的形状。
        var map = SourceCoordinateMap.Build(original, withReplace, text);
        var verdict = map.Verify(original, Patch(), text);

        Assert.False(verdict.IsSound);
        Assert.Contains("替换", verdict.Message);
    }

    [Fact]
    public void Verification_refuses_a_text_that_does_not_match_the_patch()
    {
        var original = Original();
        var patch = Patch(Replace(3, "第二章 改过的标题", "第二章 中途"));
        var text = SourcePatchRenderer.Render(original, patch).Text;
        var map = SourceCoordinateMap.Build(original, patch, text);

        // 替换没有真的发生：新文本还是老样子。
        var verdict = map.Verify(original, patch, original.Render());
        Assert.False(verdict.IsSound);
    }

    [Fact]
    public void Verification_refuses_a_text_with_an_extra_line()
    {
        var original = Original();
        var patch = Patch(Replace(3, "第二章 改过的标题", "第二章 中途"));
        var text = SourcePatchRenderer.Render(original, patch).Text;
        var map = SourceCoordinateMap.Build(original, patch, text);

        var verdict = map.Verify(original, patch, text + "\n多出来的一行\n");
        Assert.False(verdict.IsSound);
        Assert.Contains("行", verdict.Message);
    }

    [Fact]
    public void Verification_refuses_a_line_that_changed_without_any_operation_asking_for_it()
    {
        var original = Original();
        var patch = Patch(Delete(6, "正文三"));
        var text = SourcePatchRenderer.Render(original, patch).Text;
        var map = SourceCoordinateMap.Build(original, patch, text);

        // 偷偷改了第一行，补丁里没有任何操作提到它。
        var tampered = text.Replace("第一章 起点", "第一章 偷偷改过", StringComparison.Ordinal);
        var verdict = map.Verify(original, patch, tampered);

        Assert.False(verdict.IsSound);
        Assert.Contains("偷偷改过", verdict.Message);
    }

    [Fact]
    public void A_map_built_from_a_patch_always_verifies_against_that_patch()
    {
        // 反向确认：验证器不是"什么都说不行"。三个维度各来一次。
        var original = Original();
        foreach (var patch in new[]
                 {
                     Patch(Delete(2, "正文一")),
                     Patch(Replace(3, "第二章 改过的标题", "第二章 中途")),
                     Patch(Insert(new AfterOriginalLine(4), "插在中间", "ins-b")),
                     Patch(Delete(2, "正文一"), Replace(3, "第二章 改过的标题", "第二章 中途"),
                         Insert(new BeforeOriginalLine(1), "最前面", "ins-c")),
                 })
        {
            var text = SourcePatchRenderer.Render(original, patch).Text;
            var map = SourceCoordinateMap.Build(original, patch, text);
            var verdict = map.Verify(original, patch, text);
            Assert.True(verdict.IsSound, verdict.Message);
            Assert.Equal(map.NewLineCount, map.PlacedLineCount);
        }
    }

    [Fact]
    public async Task The_sample_book_map_holds_for_all_7_replacements()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var original = SourceTextDocument.Load(bookPath);
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: document);
        var version = RepairBaseVersionFactory.Capture(document.SourceSha256, document.Entries,
            document.RecognitionOptions, null);
        var proposal = RepairProposalFactory.Create("ReferenceCatalog", version, outcome.Plan!, catalog);
        var decisions = proposal.Actions.Select(action => new RepairDecision(RepairActionIdentity.Compute(action),
            proposal.Plan.DefaultSelection.Contains(action),
            SourceEffectPolicies.Decide(action.Kind, RepairLandingMode.EditSource))).ToArray();
        var compiled = RepairPlanCompiler.Compile(new RepairCompileInput(proposal, document, decisions,
            RepairLandingMode.EditSource, BuildVolumeLevels: catalog.VolumeTitles.Count > 0));

        var rendered = SourcePatchRenderer.Render(original, compiled.SourcePatch!);
        var map = SourceCoordinateMap.Build(original, compiled.SourcePatch!, rendered.Text);
        var verdict = map.Verify(original, compiled.SourcePatch!, rendered.Text);

        Assert.True(verdict.IsSound, verdict.Message);
        Assert.Equal(7, map.ReplacedLines.Count);
        Assert.Empty(map.DeletedLines);
        Assert.Equal(original.Lines.Count, map.NewLineCount);
        // 每一行都还有落点，因为这次修复只改行内的文字。
        Assert.All(Enumerable.Range(1, original.Lines.Count), line => Assert.NotNull(map.TryMap(line)));
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
