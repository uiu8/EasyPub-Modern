using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 补丁渲染与无损校验。
///
/// 渲染是单遍的，按**原始**行号索引，所以操作存放的顺序不影响结果。这是刻意的：换一种做法 ——
/// 一条条应用、每条都让下面的行号移位 —— 会让正确性取决于次序是否凑巧正确，并且把错误藏进结果里
/// 而不是报出来。
///
/// 没被补丁提到的行逐字节照抄，连它自己的换行符也照抄。于是"改了一行"就显示为改了一行，
/// 而不是整篇重写。
/// </summary>
public class SourcePatchRendererTests
{
    private static SourceTextDocument Doc(string text) => SourceTextDocument.Parse(text);

    private static SourcePatch Patch(params SourceOperation[] operations) =>
        new("actions", "SHA", operations);

    private static DeleteOriginalLine Delete(int line, string text) =>
        new(line, text) { OperationId = $"del-{line}", ActionId = "A1" };

    private static ReplaceOriginalLine Replace(int line, string oldText, string newText) =>
        new(line, oldText, newText) { OperationId = $"rep-{line}", ActionId = "A1" };

    private static InsertLineAtAnchor Insert(SourceAnchor anchor, string text, string id = "ins-1") =>
        new(anchor, text) { OperationId = id, ActionId = "A1" };

    // ── 无损读写 ────────────────────────────────────────────────

    [Theory]
    [InlineData("甲\n乙\n丙\n")]
    [InlineData("甲\r\n乙\r\n丙\r\n")]
    [InlineData("甲\r乙\r丙\r")]
    [InlineData("甲\n乙\r\n丙\r")]
    [InlineData("甲\n乙\n丙")]
    [InlineData("甲")]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Any_text_round_trips_to_the_same_bytes(string text)
    {
        var document = Doc(text);
        Assert.Equal(text, document.Render());
    }

    /// <summary>末行没有换行符是真实存在的一种文件，不能顺手给它补一个。</summary>
    [Fact]
    public void A_file_whose_last_line_has_no_terminator_keeps_not_having_one()
    {
        var document = Doc("甲\n乙");
        Assert.Equal(2, document.Lines.Count);
        Assert.Null(document.Lines[^1].Terminator);
        Assert.Equal("甲\n乙", document.Render());
    }

    [Fact]
    public void A_trailing_terminator_does_not_create_an_extra_empty_line()
    {
        Assert.Equal(3, Doc("甲\n乙\n丙\n").Lines.Count);
        Assert.Equal(3, Doc("甲\n乙\n丙").Lines.Count);
    }

    /// <summary>混合换行的文件必须按行各自保留 —— 用统一换行符会把一行改动变成整篇 diff。</summary>
    [Fact]
    public void A_mixed_file_keeps_each_line_own_terminator()
    {
        var document = Doc("甲\r\n乙\n丙\r丁");

        Assert.Equal("\r\n", document.Lines[0].Terminator);
        Assert.Equal("\n", document.Lines[1].Terminator);
        Assert.Equal("\r", document.Lines[2].Terminator);
        Assert.Null(document.Lines[3].Terminator);
        Assert.Equal("甲\r\n乙\n丙\r丁", document.Render());
    }

    // ── 插入用的换行符：规则必须唯一 ────────────────────────────

    /// <summary>默认换行符取文件里**第一个**出现的，而不是最多的 —— 后者要扫全文件且两遍可能不一致。</summary>
    [Fact]
    public void The_default_newline_is_the_first_one_the_file_has()
    {
        Assert.Equal("\r\n", Doc("甲\r\n乙\n丙\n").DefaultNewLine);
        Assert.Equal("\n", Doc("甲\n乙\r\n丙\r\n").DefaultNewLine);
        // 没有任何换行符的单行文件退回 CRLF。
        Assert.Equal("\r\n", Doc("甲").DefaultNewLine);
    }

    [Fact]
    public void An_insert_before_a_line_takes_the_previous_lines_terminator()
    {
        var document = Doc("甲\r\n乙\n丙\n");
        Assert.Equal("\r\n", document.NewLineForInsertBefore(2));   // 第 1 行是 CRLF
        Assert.Equal("\n", document.NewLineForInsertBefore(3));     // 第 2 行是 LF
    }

    [Fact]
    public void An_insert_after_a_line_takes_that_lines_terminator()
    {
        var document = Doc("甲\r\n乙\n丙\n");
        Assert.Equal("\r\n", document.NewLineForInsertAfter(1));
        Assert.Equal("\n", document.NewLineForInsertAfter(2));
    }

    // ── 渲染 ────────────────────────────────────────────────────

    [Fact]
    public void An_empty_patch_changes_nothing()
    {
        var document = Doc("甲\n乙\n丙\n");
        var result = SourcePatchRenderer.Render(document, Patch());

        Assert.Equal("甲\n乙\n丙\n", result.Text);
        Assert.False(result.Changed);
    }

    [Fact]
    public void Deleting_a_line_removes_exactly_that_line()
    {
        var document = Doc("甲\n乙\n丙\n");
        var result = SourcePatchRenderer.Render(document, Patch(Delete(2, "乙")));

        Assert.Equal("甲\n丙\n", result.Text);
        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public void Replacing_a_line_keeps_its_terminator()
    {
        var document = Doc("甲\r\n乙\r\n丙\r\n");
        var result = SourcePatchRenderer.Render(document, Patch(Replace(2, "乙", "乙改")));

        Assert.Equal("甲\r\n乙改\r\n丙\r\n", result.Text);
    }

    [Fact]
    public void Inserting_after_a_line_puts_the_new_line_below_it()
    {
        var document = Doc("甲\n乙\n");
        var result = SourcePatchRenderer.Render(document, Patch(Insert(new AfterOriginalLine(1), "新")));

        Assert.Equal("甲\n新\n乙\n", result.Text);
        Assert.Equal(1, result.Inserted);
    }

    [Fact]
    public void Inserting_before_a_line_puts_the_new_line_above_it()
    {
        var document = Doc("甲\n乙\n");
        var result = SourcePatchRenderer.Render(document, Patch(Insert(new BeforeOriginalLine(2), "新")));

        Assert.Equal("甲\n新\n乙\n", result.Text);
    }

    /// <summary>渲染是单遍的：操作存放的顺序不影响结果。</summary>
    [Fact]
    public void The_stored_order_of_operations_does_not_change_the_result()
    {
        var document = Doc("甲\n乙\n丙\n丁\n");
        var forward = Patch(
            Delete(2, "乙"),
            Replace(3, "丙", "丙改"),
            Insert(new AfterOriginalLine(1), "新"));
        var backward = Patch(
            Insert(new AfterOriginalLine(1), "新"),
            Replace(3, "丙", "丙改"),
            Delete(2, "乙"));

        Assert.Equal(
            SourcePatchRenderer.Render(document, forward).Text,
            SourcePatchRenderer.Render(document, backward).Text);
    }

    /// <summary>
    /// 删除与插入落在同一处时，行号以**原文**为准，不随前面的改动移位 ——
    /// 这正是"按原始行号索引"要保证的事。
    /// </summary>
    [Fact]
    public void Line_numbers_stay_relative_to_the_original_file()
    {
        var document = Doc("甲\n乙\n丙\n丁\n");
        // 删掉第 2 行，同时替换第 4 行：第 4 行仍是原来的第 4 行，不是"删完之后的新第 3 行"。
        var result = SourcePatchRenderer.Render(document, Patch(Delete(2, "乙"), Replace(4, "丁", "丁改")));

        Assert.Equal("甲\n丙\n丁改\n", result.Text);
    }

    [Fact]
    public void Several_inserts_sharing_an_anchor_keep_their_order()
    {
        var document = Doc("甲\n乙\n");
        var result = SourcePatchRenderer.Render(document, Patch(
            Insert(new AfterOriginalLine(1), "一", "op-1"),
            Insert(new AfterOriginalLine(1), "二", "op-2")));

        Assert.Equal("甲\n一\n二\n乙\n", result.Text);
    }

    [Fact]
    public void An_end_of_file_insert_follows_the_last_line()
    {
        var document = Doc("甲\n乙\n");
        var result = SourcePatchRenderer.Render(document, Patch(Insert(new EndOfFileAnchor(), "尾")));

        Assert.Equal("甲\n乙\n尾", result.Text);
    }

    /// <summary>
    /// 原文末尾没有换行符时，先补一个再追加 —— 否则新内容会接在最后一行后面而不是另起一行。
    /// 而最后一条插入行保持"文件末尾无换行"的形态。
    /// </summary>
    [Fact]
    public void An_end_of_file_insert_terminates_the_previous_last_line_first()
    {
        var document = Doc("甲\n乙");
        var result = SourcePatchRenderer.Render(document, Patch(Insert(new EndOfFileAnchor(), "尾")));

        Assert.Equal("甲\n乙\n尾", result.Text);
    }

    [Fact]
    public void The_last_inserted_line_does_not_gain_a_trailing_newline()
    {
        var document = Doc("甲\n");
        var result = SourcePatchRenderer.Render(document, Patch(Insert(new EndOfFileAnchor(), "尾")));

        Assert.EndsWith("尾", result.Text);
        Assert.False(result.Text.EndsWith("尾\n", StringComparison.Ordinal));
    }

    /// <summary>没被提到的行逐字节照抄：改一行就该只显示一行变化。</summary>
    [Fact]
    public void Lines_the_patch_does_not_mention_are_copied_faithfully()
    {
        var document = Doc("甲\r\n乙\n丙\r丁");
        var result = SourcePatchRenderer.Render(document, Patch(Replace(2, "乙", "乙改")));

        Assert.Equal("甲\r\n乙改\n丙\r丁", result.Text);
    }

    // ── 无损校验 ────────────────────────────────────────────────

    [Fact]
    public void The_lossless_check_passes_on_a_document_parsed_from_the_same_text()
    {
        const string text = "甲\r\n乙\n丙\r丁";
        var verification = SourceRenderVerifier.Verify(Doc(text), text);

        Assert.True(verification.IsLossless, verification.Message);
    }

    [Fact]
    public void The_lossless_check_reports_where_two_texts_first_differ()
    {
        var verification = SourceRenderVerifier.Verify(Doc("甲\n乙\n"), "甲\n丙\n");

        Assert.False(verification.IsLossless);
        Assert.Contains("第 3 个字符", verification.Message);
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
    /// 真实样书必须能无损读入并还原 —— 这是允许改原文之前必须过的那道门。
    ///
    /// 注意这里的 2502 与 <c>ChapterTreeDocument.LineCount</c> 报的 2503 不同，两个都"对"，但含义
    /// 不一样：旧模型把 <c>text.Split('\n')</c> 的最后那个空元素当成一行（文件末尾本来就有换行，
    /// 于是多出一个），本模型按行终止符切分，末尾换行属于最后一行而不产生新行。两者对**任何真实
    /// 存在的行**给出的行号完全一致，所以不影响修复；差的是末尾那个并不存在的"行"。
    /// </summary>
    [Fact]
    public void The_sample_book_round_trips_without_losing_a_byte()
    {
        var bookPath = Path.Combine(SampleRoot(), "缺陷样书.txt");
        var text = File.ReadAllText(bookPath);
        var document = SourceTextDocument.Parse(text);

        Assert.Equal(text, document.Render());
        Assert.True(SourceRenderVerifier.Verify(document, text).IsLossless);
        Assert.Equal(2502, document.Lines.Count);
    }

    /// <summary>
    /// 在真实样书上做一次真实的渲染：把目录写法与原文不同的那些标题行换掉，
    /// 然后确认只有那些行变了、其余一个字节没动。
    /// </summary>
    [Fact]
    public async Task Rewriting_the_sample_books_titles_changes_only_those_lines()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var text = await File.ReadAllTextAsync(bookPath);
        var document = SourceTextDocument.Parse(text);
        var tree = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: tree);

        var operations = outcome.Plan!.Actions
            .Where(action => action.Kind == ReferenceActionKind.Retitle && action.Line > 0)
            .Select(action => (SourceOperation)Replace(action.Line,
                tree.SourceLine(action.Line)?.Text ?? "",
                action.Reference?.Title ?? action.Title))
            .ToArray();
        Assert.NotEmpty(operations);

        var result = SourcePatchRenderer.Render(document, Patch(operations));

        var before = text.Split('\n');
        var after = result.Text.Split('\n');
        // 只替换、不增删，所以行数必须一致。
        Assert.Equal(before.Length, after.Length);
        var changed = before.Zip(after).Count(pair => pair.First != pair.Second);
        Assert.Equal(operations.Length, changed);
        Assert.Equal(0, result.Deleted);
        Assert.Equal(0, result.Inserted);
    }
}
