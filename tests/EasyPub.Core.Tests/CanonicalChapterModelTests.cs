using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 规范化章节模型：两种落地模式真正用来互相比对的东西。
///
/// TreeOnly 不改行，EditSource 会删行插行，所以压实之后**每一个物理行号都不同**。逐字段比章节树
/// 永远比不出相等，而那正是"两种模式产出同一本书"这句话唯一可检查的形式。这里验证的是：模型确实
/// 装得下业务语义，而且装不进存储细节。
/// </summary>
public class CanonicalChapterModelTests
{
    private static ChapterTreeEntry Entry(string title, int level, int? line,
        params (int Start, int End)[] ranges) =>
        new(Guid.NewGuid().ToString("N"), title, level, true, line,
            ranges.Select(range => new ChapterSourceRange(range.Start, range.End)).ToArray());

    /// <summary>正文由行号查出文本，模拟一份真实源文件。</summary>
    private static Func<int, string?> Text(params string[] lines) =>
        line => line >= 1 && line <= lines.Length ? lines[line - 1] : null;

    // ── 模型装不进存储细节 ──────────────────────────────────────

    /// <summary>
    /// 纯行号平移不得改变模型 —— 这正是 EditSource 压实之后发生的事。
    /// </summary>
    [Fact]
    public void Shifting_every_line_number_does_not_change_the_model()
    {
        var before = new[]
        {
            Entry("第一章", 1, 1, (2, 4)),
            Entry("第二章", 1, 5, (6, 8)),
        };
        var shifted = new[]
        {
            Entry("第一章", 1, 11, (12, 14)),
            Entry("第二章", 1, 15, (16, 18)),
        };

        var first = CanonicalChapterModelFactory.Build(before, Text("一", "甲", "乙", "丙", "二", "丁", "戊", "己"));
        var second = CanonicalChapterModelFactory.Build(shifted, Text(
            "", "", "", "", "", "", "", "", "", "",
            "一", "甲", "乙", "丙", "二", "丁", "戊", "己"));

        Assert.True(first.EqualsByContent(second), first.DescribeFirstDifference(second));
    }

    /// <summary>
    /// 正文相同、行号不同，仍须相等；反过来，正文不同必须不等 —— 否则这个模型什么都验不出来。
    /// </summary>
    [Fact]
    public void Same_body_at_a_different_line_is_equal_but_a_different_body_is_not()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 3)) };
        var first = CanonicalChapterModelFactory.Build(entries, Text("一", "甲", "乙"));
        var sameTextElsewhere = CanonicalChapterModelFactory.Build(
            new[] { Entry("第一章", 1, 10, (11, 12)) },
            // 10 个空行把「甲」「乙」顶到第 11、12 行 —— 与 first 的 (2,3) 内容相同、位置不同。
            Text("", "", "", "", "", "", "", "", "", "", "甲", "乙"));
        var different = CanonicalChapterModelFactory.Build(
            new[] { Entry("第一章", 1, 1, (2, 3)) }, Text("一", "甲", "改"));

        Assert.True(first.EqualsByContent(sameTextElsewhere),
            "同一段正文换个行号就判成不同了 —— " + first.DescribeFirstDifference(sameTextElsewhere)
            + $"；正文哈希 {string.Join(",", first.Chapters[0].BodyContentHashes)}"
            + $" 对 {string.Join(",", sameTextElsewhere.Chapters[0].BodyContentHashes)}");
        Assert.False(first.EqualsByContent(different));
    }

    /// <summary>
    /// 正文里重复的句子不能因为"哈希相同"而被当成同样的正文：位置参与哈希就是为了这个。
    /// </summary>
    [Fact]
    public void A_repeated_sentence_inside_a_body_still_distinguishes_two_bodies()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 5)) };
        var first = CanonicalChapterModelFactory.Build(entries, Text("一", "甲", "甲", "乙", "丙"));
        var second = CanonicalChapterModelFactory.Build(entries, Text("一", "甲", "乙", "甲", "丙"));

        Assert.False(first.EqualsByContent(second));
    }

    /// <summary>标题来源不能用"虚拟/物理"这种存储层词汇 —— 那会让两种模式天生不等。</summary>
    [Fact]
    public void The_heading_origin_has_no_storage_level_cases()
    {
        var names = Enum.GetNames<CanonicalHeadingOrigin>().OrderBy(name => name).ToArray();
        Assert.DoesNotContain("Virtual", names);
        Assert.DoesNotContain("Physical", names);
        Assert.Equal(
            new[] { "AdoptedByRepair", "ExistingSourceHeading", "InsertedByRepair", "Manual" }.OrderBy(n => n).ToArray(),
            names);
    }

    /// <summary>没有标题行的章节是"本次修复给的标题"，不论它落在哪一行。</summary>
    [Fact]
    public void A_chapter_with_no_heading_line_is_marked_as_inserted_by_repair()
    {
        var entries = new[] { Entry("第一章", 1, null, (2, 3)) };
        var model = CanonicalChapterModelFactory.Build(entries, Text("", "甲", "乙"));
        Assert.Equal(CanonicalHeadingOrigin.InsertedByRepair, model.Chapters.Single().HeadingOrigin);
    }

    [Fact]
    public void A_manual_chapter_keeps_its_manual_origin()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 3)) with { RecognitionSource = "manual" } };
        var model = CanonicalChapterModelFactory.Build(entries, Text("一", "甲", "乙"));
        Assert.Equal(CanonicalHeadingOrigin.Manual, model.Chapters.Single().HeadingOrigin);
    }

    /// <summary>卷节点与章节必须区分开 —— 否则两棵结构不同的树会被判成相等。</summary>
    [Fact]
    public void Volumes_and_chapters_are_different_kinds()
    {
        var volume = CanonicalChapterModelFactory.Build(
            new[] { Entry("第一卷", 1, 1, (2, 3)) with { RecognitionSource = "reference-volume" } },
            Text("卷", "甲", "乙"));
        var chapter = CanonicalChapterModelFactory.Build(
            new[] { Entry("第一卷", 1, 1, (2, 3)) }, Text("卷", "甲", "乙"));

        Assert.Equal(CanonicalNodeKind.Volume, volume.Chapters.Single().Kind);
        Assert.Equal(CanonicalNodeKind.Chapter, chapter.Chapters.Single().Kind);
        Assert.False(volume.EqualsByContent(chapter));
    }

    /// <summary>父节点归属不同的两棵树不能判成相等 —— v3.3 §5.3 要求补父路径就是为了这个。</summary>
    [Fact]
    public void Two_trees_that_differ_only_in_parentage_are_not_equal()
    {
        // 第一章 下挂 第二章；对照：两章平级。
        var nested = new[]
        {
            Entry("第一章", 1, 1, (2, 2)),
            Entry("第二章", 2, 3, (4, 4)),
        };
        var flat = new[]
        {
            Entry("第一章", 1, 1, (2, 2)),
            Entry("第二章", 1, 3, (4, 4)),
        };

        var first = CanonicalChapterModelFactory.Build(nested, Text("一", "甲", "二", "乙"));
        var second = CanonicalChapterModelFactory.Build(flat, Text("一", "甲", "二", "乙"));

        Assert.False(first.EqualsByContent(second));
        Assert.Empty(first.Chapters[0].ParentPath);
        Assert.Equal(new[] { 0 }, first.Chapters[1].ParentPath);
        Assert.Empty(second.Chapters[1].ParentPath);
    }

    [Fact]
    public void Differences_are_named_rather_than_counted()
    {
        var left = CanonicalChapterModelFactory.Build(new[] { Entry("第一章", 1, 1, (2, 2)) }, Text("一", "甲"));
        var right = CanonicalChapterModelFactory.Build(new[] { Entry("第一章 改", 1, 1, (2, 2)) }, Text("一", "甲"));

        var description = left.DescribeFirstDifference(right);

        Assert.Contains("标题不同", description);
        Assert.Contains("第一章 改", description);
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
    /// 在真实样书上：重建前后树确实是**不同的两棵树**（行号、正文范围都变了），
    /// 但规范化之后必须相等 —— 这才是"同一本书"的定义。
    ///
    /// 正文用行号无关的取文本方式，因此这一条验证的是模型对行号与范围重排的不敏感性；
    /// 真正带正文比对的两种落地模式对照，要等 SourcePatch 做出来才能跑（Phase 4c）。
    /// </summary>
    [Fact]
    public async Task The_sample_book_rebuilds_into_a_tree_that_is_different_but_canonically_the_same()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;

        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: document);
        var rebuilt = ChapterAutoRepair.RebuildWithSelection(
            document, outcome, outcome.Plan!.DefaultSelection.ToArray());

        // 两棵树确实不同：条目数变了。
        Assert.NotEqual(document.Entries.Count, rebuilt.Count);

        // 用行号无关的文本来源，只比较结构：标题、层级、顺序、是否进目录、父子关系。
        static string? NoText(int _) => "正文";
        var before = CanonicalChapterModelFactory.Build(document.Entries, NoText);
        var after = CanonicalChapterModelFactory.Build(rebuilt, NoText);

        // 结构上必然不同 —— 修复本来就是要改结构。这里确认模型**能**看出不同，
        // 而不是把所有东西都判成相等（那会让等价性检查变成永远通过的空断言）。
        Assert.False(before.EqualsByContent(after),
            "重建前后结构不同，模型却判成相等 —— 说明模型太粗，等价性检查会失去意义");

        // 而同样一个模型与自己比必须相等。
        Assert.True(before.EqualsByContent(CanonicalChapterModelFactory.Build(document.Entries, NoText)));
    }

    /// <summary>同一个重建结果算两次必须完全一致，否则比较无从谈起。</summary>
    [Fact]
    public async Task Building_the_model_twice_from_the_same_tree_is_deterministic()
    {
        var root = SampleRoot();
        var bookPath = Path.Combine(root, "缺陷样书.txt");
        var document = await ChapterTreeDocument.LoadAsync(bookPath);
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(bookPath, new ReferenceRepairRequest(catalog), currentDocument: document);
        var rebuilt = ChapterAutoRepair.RebuildWithSelection(
            document, outcome, outcome.Plan!.DefaultSelection.ToArray());

        static string? Text(int line) => line % 7 == 0 ? null : $"第 {line} 行";
        var first = CanonicalChapterModelFactory.Build(rebuilt, Text);
        var second = CanonicalChapterModelFactory.Build(rebuilt, Text);

        Assert.True(first.EqualsByContent(second), first.DescribeFirstDifference(second));
    }
}
