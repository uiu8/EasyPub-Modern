using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 从两版文本推出补丁 —— 恢复备份走的就是这条路。
///
/// 这一层只有一个真正重要的问题：**照补丁渲染出来，必须逐字节等于目标版本**。其余都是围绕它的边界：
/// 相同的文本应当推出空补丁（否则恢复会被当成"改动"），只动一行时不该报告整本都变了
/// （否则每一个章节都会被判成"标题行被改写"而失去身份）。
/// </summary>
public class SourceDiffTests
{
    private static SourceTextDocument Text(string text) => SourceTextDocument.Parse(text);

    private static void AssertRoundTrips(string from, string to)
    {
        var source = Text(from);
        var target = Text(to);
        var patch = SourceDiff.Patch(source, target);
        var rendered = SourcePatchRenderer.Render(source, patch);
        Assert.Equal(target.Render(), rendered.Text);
    }

    [Fact]
    public void The_same_text_produces_no_operations()
    {
        // 这一条很要紧：恢复成"和现在一样"的版本不该产生任何操作，否则事务会去替换一个字节都没变的文件。
        var text = "第一章 起点\n正文一。\n第二章 中途\n正文二。\n";
        var patch = SourceDiff.Patch(Text(text), Text(text));

        Assert.Empty(patch.Operations);
        Assert.True(patch.IsEmpty);
    }

    [Fact]
    public void One_changed_line_produces_the_cheapest_script()
    {
        // 只改一行时，最长公共子序列必须把其余每一行都原样留下。
        // 若这里退化成"整本替换"，每一个章节的标题行都会被判成"被改写"，身份全部失效。
        var from = "第一章 起点\n正文一。\n第二章 中途\n正文二。\n第三章 终点\n正文三。\n";
        var to = "第一章 起点\n正文一。\n第二章 改过的\n正文二。\n第三章 终点\n正文三。\n";

        var source = Text(from);
        var patch = SourceDiff.Patch(source, Text(to));

        // 一个操作，命中那一行 —— 不是六行替换，也不是一删一插。
        var replacement = Assert.IsType<ReplaceOriginalLine>(Assert.Single(patch.Operations));
        Assert.Equal(3, replacement.Line);
        Assert.Equal("第二章 中途", replacement.ExpectedOldText);
        AssertRoundTrips(from, to);
    }

    [Fact]
    public void A_changed_line_is_one_replacement_not_a_removal_plus_an_addition()
    {
        // 这条检查的不是"脚本够不够短"，而是坐标表的语义。一删一插会让那一行在新版本里没有对应行，
        // 于是正文里出现一个洞 —— 实测中，正文被改过一句的那一章因此被判成"没有正文"，身份被丢掉。
        var from = "第一章 起点\n正文一。\n第二章 中途\n正文二。\n";
        var to = "第一章 起点\n正文一。\n第二章 中途\n正文二，改过一句。\n";

        var source = Text(from);
        var patch = SourceDiff.Patch(source, Text(to));

        Assert.Empty(patch.Operations.OfType<DeleteOriginalLine>());
        Assert.Empty(patch.Operations.OfType<InsertLineAtAnchor>());
        var replacement = Assert.IsType<ReplaceOriginalLine>(Assert.Single(patch.Operations));
        Assert.Equal(4, replacement.Line);

        // 坐标表必须能回答"原来第 4 行去哪了"：答案是新文本的第 4 行，而且它确实被改写这一版认领了。
        var rendered = SourcePatchRenderer.Render(source, patch);
        var map = SourceCoordinateMap.Build(source, patch, rendered.Text);
        Assert.Equal(4, map.TryMap(4));
        Assert.True(map.IsReplaced(4));
        AssertRoundTrips(from, to);
    }

    [Fact]
    public void A_run_of_changed_lines_is_paired_line_by_line()
    {
        var from = "第一章 起点\n甲\n乙\n丙\n尾。\n";
        var to = "第一章 起点\n甲改\n乙改\n丙改\n尾。\n";

        var source = Text(from);
        var patch = SourceDiff.Patch(source, Text(to));

        // 三行各改一次，就是三次改写 —— 而不是"删三行、插三行"。
        Assert.Equal([2, 3, 4], patch.Operations.OfType<ReplaceOriginalLine>().Select(r => r.Line).Order());
        Assert.Empty(patch.Operations.OfType<DeleteOriginalLine>());
        Assert.Empty(patch.Operations.OfType<InsertLineAtAnchor>());
        AssertRoundTrips(from, to);
    }

    [Fact]
    public void More_new_lines_than_dropped_ones_land_after_the_replacements()
    {
        // 一批新行比它换掉的旧行多：多出来的那几行必须排在改写之后。
        // 锚在原来那个锚点上会让它们排到改写行**前面**，整段顺序就乱了 —— 这是配对最容易错的地方。
        var from = "第一章 起点\n甲\n乙\n尾。\n";
        var to = "第一章 起点\n甲改\n乙改\n丙新\n丁新\n尾。\n";

        var source = Text(from);
        var patch = SourceDiff.Patch(source, Text(to));

        Assert.Equal([2, 3], patch.Operations.OfType<ReplaceOriginalLine>().Select(r => r.Line).Order());
        Assert.Equal(2, patch.Operations.OfType<InsertLineAtAnchor>().Count());
        AssertRoundTrips(from, to);
    }

    [Fact]
    public void A_batch_of_ten_insertions_keeps_the_text_order()
    {
        // 同一个锚点上的多个插入，渲染顺序靠 OperationId 的序数比较决定，而 "ins-10" 在字典序里
        // 排在 "ins-9" 前面 —— 一批十个是普通情况，不是边界。
        var from = "标题\n尾行。\n";
        var to = "标题\n" + string.Concat(Enumerable.Range(1, 12).Select(n => $"新{n}\n")) + "尾行。\n";

        AssertRoundTrips(from, to);
    }

    [Fact]
    public void A_deleted_line_is_removed_and_nothing_else_moves()
    {
        var from = "第一章 起点\n正文一。\n第二章 中途\n正文二。\n";
        var to = "第一章 起点\n第二章 中途\n正文二。\n";

        var patch = SourceDiff.Patch(Text(from), Text(to));
        Assert.Single(patch.Operations);
        Assert.Equal(2, patch.Operations.OfType<DeleteOriginalLine>().Single().Line);
        AssertRoundTrips(from, to);
    }

    [Fact]
    public void An_added_line_is_anchored_where_it_belongs()
    {
        var from = "第一章 起点\n正文一。\n";
        var to = "第一章 起点\n正文一。\n补写的一段。\n";

        var patch = SourceDiff.Patch(Text(from), Text(to));
        var insert = Assert.Single(patch.Operations.OfType<InsertLineAtAnchor>());
        Assert.Equal(2, Assert.IsType<AfterOriginalLine>(insert.Anchor).Line);
        AssertRoundTrips(from, to);
    }

    [Fact]
    public void An_added_line_at_the_top_goes_in_front_of_the_first_line()
    {
        var from = "第一章 起点\n正文一。\n";
        var to = "卷首语\n第一章 起点\n正文一。\n";

        var patch = SourceDiff.Patch(Text(from), Text(to));
        var insert = Assert.Single(patch.Operations.OfType<InsertLineAtAnchor>());
        Assert.Equal(1, Assert.IsType<BeforeOriginalLine>(insert.Anchor).Line);
        AssertRoundTrips(from, to);
    }

    [Fact]
    public void Everything_different_still_round_trips()
    {
        AssertRoundTrips("甲\n乙\n丙\n", "一\n二\n三\n四\n");
    }

    [Fact]
    public void An_empty_source_still_round_trips()
    {
        // 空文件是一个合法版本：全部内容都是插入。行尾仍然跟着当前文件（空文件没有行尾，取默认的 CRLF），
        // 所以这里断言的是"逐行文字与行数"，不是字节 —— 见 An_inserted_line_ends_the_way_... 那条。
        var source = SourceTextDocument.Parse("");
        var target = Text("第一章 起点\n正文一。\n");
        var patch = SourceDiff.Patch(source, target);
        var rendered = SourceTextDocument.Parse(SourcePatchRenderer.Render(source, patch).Text);

        Assert.Equal(target.Lines.Select(line => line.Text), rendered.Lines.Select(line => line.Text));
        Assert.Equal(2, rendered.Lines.Count);
    }

    [Fact]
    public void Repeated_lines_do_not_confuse_the_alignment()
    {
        // 同一行文字出现多次时，对齐必须仍然给出可渲染的脚本 —— 锚点靠"它跟着哪一行"来确定位置。
        var from = "标题\n正文\n标题\n正文\n标题\n正文\n";
        var to = "标题\n正文\n标题\n正文\n";
        AssertRoundTrips(from, to);
        AssertRoundTrips(to, from);
    }

    [Fact]
    public void Both_versions_using_the_same_line_ending_round_trip_byte_for_byte()
    {
        // 全 LF 与全 CRLF 两个场景：恢复要能把版本还原成原来的字节。
        AssertRoundTrips("第一章 起点\n正文一。\n第二章 中途\n", "第一章 起点\n第二章 中途\n正文二。\n");
        AssertRoundTrips("第一章 起点\r\n正文一。\r\n第二章 中途\r\n",
            "第一章 起点\r\n第二章 中途\r\n正文二。\r\n");
    }

    [Fact]
    public void An_inserted_line_ends_the_way_the_file_it_is_being_written_into_ends()
    {
        // 已知边界，不是缺陷，但必须写下来：插入行的行尾用的是**当前文件**的约定
        // （SourceTextDocument.DefaultNewLine，取文件里第一个行尾），而不是目标版本的约定。
        //
        // 后果是：要恢复的版本用了和当前版本不同的行尾约定时，插入的那些行会带回当前约定的行尾，
        // 于是结果不等于目标版本的原始字节。补丁模型里只有"行号 + 行首文字"，行尾是每一行自己的、
        // 而插入的行没有"自己"。
        var current = SourceTextDocument.Parse("第一章 起点\r\n正文一。\r\n");   // CRLF 文件
        var target = SourceTextDocument.Parse("第一章 起点\n新加的一段。\n正文一。\n"); // LF 版本

        var patch = SourceDiff.Patch(current, target);
        var rendered = SourcePatchRenderer.Render(current, patch).Text;

        // 文字与行数都对，行尾跟着当前文件走。
        Assert.Equal(["第一章 起点", "新加的一段。", "正文一。"], 
            SourceTextDocument.Parse(rendered).Lines.Select(line => line.Text));
        Assert.Equal("第一章 起点\r\n新加的一段。\r\n正文一。\r\n", rendered);
        Assert.NotEqual(target.Render(), rendered);
    }

    [Fact]
    public async Task The_sample_book_aligns_with_its_repaired_version()
    {
        // 真实规模：2502 行的样书与它自己被修复后的版本。这是 LCS 表的上界场景（约 626 万格），
        // 也是恢复真正的用途 —— 把当前版本搬回某个保存过的版本。
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
        var repaired = SourcePatchRenderer.Render(original, compiled.SourcePatch!);

        // 修复前后：脚本应当只认那 69 行，而不是整本替换。
        var forward = SourceDiff.Patch(original, SourceTextDocument.Parse(repaired.Text));
        var touched = forward.Operations
            .Select(operation => operation switch
            {
                DeleteOriginalLine delete => delete.Line,
                ReplaceOriginalLine replace => replace.Line,
                _ => 0,
            })
            .Where(line => line > 0)
            .ToHashSet();
        Assert.Equal(repaired.Text, SourcePatchRenderer.Render(original, forward).Text);
        Assert.True(touched.Count <= 140,
            $"改动 69 行的两个版本，脚本却提到了 {touched.Count} 行 —— 对齐退化成了整本替换。");

        // 反过来也成立：从修复后的版本回到原版。
        var backward = SourceDiff.Patch(SourceTextDocument.Parse(repaired.Text), original);
        Assert.Equal(original.Render(), SourcePatchRenderer.Render(SourceTextDocument.Parse(repaired.Text), backward).Text);

        // 自己对自己：空补丁。
        Assert.Empty(SourceDiff.Patch(original, original).Operations);
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
