using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 两种落地模式产出的是不是同一本书。
///
///   <c>TreeOnly</c>    章节树变了，TXT 一个字节不动；"被移除的行"只是不再出现在成品里
///   <c>EditSource</c>  章节树变了，TXT 也真的删掉了那些行
///
/// 后者的物理行号必然不同（压实了），所以要比的是规范化模型：章节顺序、标题、层级、是否进目录、
/// 以及**每章拥有的正文内容**。这一条通过，"两种模式产出同一本书"才算被验证过 ——
/// 在此之前它只是一句设计意图。
/// </summary>
public class LandingModeEquivalenceTests
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

    /// <summary>
    /// 六卷样书：默认选中要移除 160 行（第一卷整章重复 55 + 第二卷错位副本 105）。
    /// 两种模式对这本书必须给出同一个成品。
    /// </summary>
    [Fact]
    public async Task Both_landing_modes_produce_the_same_book_for_the_sample()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var rawText = await File.ReadAllTextAsync(bookPath);
        var textDocument = SourceTextDocument.Parse(rawText);
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: document);
        var plan = outcome.Plan!;

        string? BaseLine(int line) => line >= 1 && line <= textDocument.Lines.Count
            ? textDocument.Lines[line - 1].Text
            : null;

        // TreeOnly：树变了，文本不动。
        var dropped = new List<int>();
        var treeOnly = ReferencePlanner.Apply(document, document.Entries, plan,
            plan.DefaultSelection.ToArray(), true, catalog.VolumeTitles.Count > 0, dropped);
        Assert.Equal(160, dropped.Count);

        // EditSource：把那些行真的删掉。
        var deletions = dropped
            .Select(line => (SourceOperation)new DeleteOriginalLine(line, BaseLine(line) ?? "")
            { OperationId = "del-" + line, ActionId = "remove-duplicate" })
            .ToArray();
        var patch = SourcePatchNormalizer.Normalize("sample", document.SourceSha256, deletions,
            textDocument.Lines.Count, BaseLine);
        Assert.True(patch.IsValid, patch.Describe());

        var rendered = SourcePatchRenderer.Render(textDocument, patch.Patch!);
        Assert.Equal(160, rendered.Deleted);
        Assert.Equal(textDocument.Lines.Count - 160, rendered.LinesAfter);

        // 把新行号映射回原文行号，好让两份模型用同一种坐标取文本。
        var deleted = dropped.ToHashSet();
        var newLineOfOriginal = new Dictionary<int, int>();
        var running = 0;
        for (var line = 1; line <= textDocument.Lines.Count; line++)
        {
            if (deleted.Contains(line)) continue;
            newLineOfOriginal[line] = ++running;
        }
        var renderedLines = SourceTextDocument.Parse(rendered.Text).Lines;

        var treeOnlyModel = CanonicalChapterModelFactory.Build(treeOnly, BaseLine);
        var editSourceModel = CanonicalChapterModelFactory.Build(treeOnly,
            line => newLineOfOriginal.TryGetValue(line, out var mapped) && mapped <= renderedLines.Count
                ? renderedLines[mapped - 1].Text
                : null);

        Assert.True(treeOnlyModel.EqualsByContent(editSourceModel),
            "两种落地模式产出了不同的书：" + treeOnlyModel.DescribeFirstDifference(editSourceModel));
        Assert.Equal(444, treeOnlyModel.Chapters.Count);
    }

    /// <summary>
    /// 反证：等价性检查必须真的看得见正文差异，否则"两者相等"毫无意义。
    ///
    /// 第一版反证问的是"把被移除的行留着不删，模型会不会不同"，实测**不会** —— 因为那 160 行在
    /// 重建后的树里本来就不归任何章节（实测仍被认领的为 0）。对以章节归属为准的模型来说，
    /// 它们存不存在根本不影响，所以那个问题问错了。
    ///
    /// 真正该问的是：**同一棵树配两份不同的正文，模型分不分得出**。分不出，则等价性检查是空的。
    /// </summary>
    [Fact]
    public async Task The_equivalence_check_actually_sees_a_difference_in_the_text()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var rawText = await File.ReadAllTextAsync(bookPath);
        var textDocument = SourceTextDocument.Parse(rawText);
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: document);

        var dropped = new List<int>();
        var rebuilt = ReferencePlanner.Apply(document, document.Entries, outcome.Plan!,
            outcome.Plan!.DefaultSelection.ToArray(), true, catalog.VolumeTitles.Count > 0, dropped);

        string? BaseLine(int line) => line >= 1 && line <= textDocument.Lines.Count
            ? textDocument.Lines[line - 1].Text
            : null;

        var real = CanonicalChapterModelFactory.Build(rebuilt, BaseLine);
        var invented = CanonicalChapterModelFactory.Build(rebuilt, _ => "完全不同的正文");
        Assert.False(real.EqualsByContent(invented),
            "同一棵树配两份不同的正文却判成相等 —— 正文哈希没起作用，等价性检查是空的");

        // 只改**一行正文**也必须看得出来，而不是只有整篇换掉才看得出。
        var firstOwnedLine = rebuilt
            .SelectMany(entry => entry.ContentRanges.SelectMany(range =>
                Enumerable.Range(range.StartLine, Math.Max(0, range.EndLine - range.StartLine + 1))))
            .First();
        var oneLineChanged = CanonicalChapterModelFactory.Build(rebuilt,
            line => line == firstOwnedLine ? "这一行被改过了" : BaseLine(line));
        Assert.False(real.EqualsByContent(oneLineChanged),
            $"第 {firstOwnedLine} 行正文改了却看不出差异");

        // 而被移除的行确实不在任何章节的正文里 —— 这正是第一版反证问错了问题的原因。
        var covered = RepairIntegrity.Coverage(rebuilt);
        Assert.Equal(0, dropped.Count(line => covered.Contains(line)));
    }

    // ── 正文归属的计算 ──────────────────────────────────────────

    private static readonly HashSet<int> All = [.. Enumerable.Range(1, 20)];

    [Fact]
    public void A_chapter_owns_the_lines_between_it_and_the_next_boundary()
    {
        var body = BodyAssignmentCalculator.ForChapter(5, 10, 20, All, new HashSet<int>());
        Assert.Equal(new[] { 6, 7, 8, 9 }, body.Lines());
    }

    [Fact]
    public void The_last_chapter_runs_to_the_end_of_the_file()
    {
        var body = BodyAssignmentCalculator.ForChapter(5, 0, 20, All, new HashSet<int>());
        Assert.Equal(Enumerable.Range(6, 15), body.Lines());
    }

    /// <summary>
    /// 被移除的行不属于任何人 —— 这正是"删掉了"和"还在文件里但不在成品里"在这里是同一个答案的原因。
    /// </summary>
    [Fact]
    public void A_removed_line_belongs_to_nobody()
    {
        var body = BodyAssignmentCalculator.ForChapter(5, 12, 20, All, new HashSet<int> { 8, 9 });
        Assert.Equal(new[] { 6, 7, 10, 11 }, body.Lines());
    }

    /// <summary>移除中间一段会把正文切成两个区间，而不是一个跨度。</summary>
    [Fact]
    public void A_hole_in_the_middle_produces_two_spans()
    {
        var body = BodyAssignmentCalculator.ForChapter(5, 12, 20, All, new HashSet<int> { 8 });

        Assert.Equal(2, body.Spans.Count);
        Assert.Equal(new SourceLineSpan(6, 8), body.Spans[0]);
        Assert.Equal(new SourceLineSpan(9, 12), body.Spans[1]);
    }

    [Fact]
    public void A_chapter_with_no_text_below_it_owns_nothing()
    {
        var body = BodyAssignmentCalculator.ForChapter(5, 6, 20, All, new HashSet<int>());
        Assert.Empty(body.Spans);
        Assert.Empty(body.Lines());
    }

    [Fact]
    public void Front_matter_owns_everything_before_the_first_boundary()
    {
        var body = BodyAssignmentCalculator.ForFrontMatter(5, 20, All, new HashSet<int>());
        Assert.Equal(new[] { 1, 2, 3, 4 }, body.Lines());
    }

    /// <summary>整体重算之后，行守恒仍然成立 —— 与 RepairIntegrity 用的是同一套行集合。</summary>
    [Fact]
    public async Task Recomputing_bodies_keeps_every_line_accounted_for()
    {
        var bookPath = Path.Combine(SampleRoot(), "缺陷样书.txt");
        var document = await ChapterTreeDocument.LoadAsync(bookPath);

        var reassigned = BodyAssignmentCalculator.ReassignBodies(document.Entries, document.LineCount, []);

        var before = RepairIntegrity.Coverage(document.Entries);
        var after = RepairIntegrity.Coverage(reassigned);
        Assert.True(before.SetEquals(after),
            $"重算正文之后行集合变了：少了 {before.Except(after).Count()} 行，多了 {after.Except(before).Count()} 行");
    }

    [Fact]
    public void Ranges_convert_to_an_assignment_and_back()
    {
        var ranges = new[] { new ChapterSourceRange(2, 4), new ChapterSourceRange(7, 9) };
        var assignment = BodyAssignment.FromRanges(ranges);

        Assert.Equal(2, assignment.Spans.Count);
        Assert.Equal(new SourceLineSpan(2, 5), assignment.Spans[0]);
        Assert.Equal(ranges.Select(r => (r.StartLine, r.EndLine)), assignment.ToRanges().Select(r => (r.StartLine, r.EndLine)));
    }
}
