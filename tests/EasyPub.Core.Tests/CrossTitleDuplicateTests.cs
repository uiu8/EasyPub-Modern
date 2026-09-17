using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// A repeated block whose headings were renumbered. The same-title comparison cannot see it: both
/// copies look like ordinary, differently-named chapters, and so does every other pair in the book.
/// The finding matters because the pair is just as removable as a same-title duplicate — and just as
/// damaging if it is not noticed, since the reader gets the same prose twice under two names.
/// </summary>
public class CrossTitleDuplicateTests
{
    private static async Task<ChapterTreeDocument> DocumentAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-cross-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text);
        try { return await ChapterTreeDocument.LoadAsync(path); }
        finally { File.Delete(path); }
    }

    private static string Paragraph(string marker) =>
        string.Join('\n', Enumerable.Range(1, 6).Select(index => $"{marker} 的第 {index} 段文字，用来把正文写到足够长以便比较。"));

    [Fact]
    public async Task The_same_body_under_a_different_heading_is_reported()
    {
        var shared = Paragraph("重复段落");
        var document = await DocumentAsync(string.Join('\n',
        [
            "第一章 甲", "", Paragraph("甲自己的"), "",
            "第二章 乙", "", shared, "",
            "第三章 丙", "", Paragraph("丙自己的"), "",
            "第四章 丁", "", shared, "",
        ]));

        var found = ChapterContentDuplicates.FindCrossTitle(document, document.Entries);

        var pair = Assert.Single(found);
        Assert.Equal("第二章 乙", pair.FirstTitle);
        Assert.Equal("第四章 丁", pair.SecondTitle);
        Assert.True(pair.Exact);
        Assert.Equal(1.0, pair.Similarity, 3);
    }

    [Fact]
    public async Task Chapters_that_share_a_title_are_left_to_the_same_title_comparison()
    {
        // Same title twice: that is the shape ChapterContentDuplicates.Find already owns, and reporting
        // it in both places would put one finding on screen twice.
        var shared = Paragraph("重复段落");
        var document = await DocumentAsync(string.Join('\n',
        [
            "第一章 甲", "", Paragraph("甲自己的"), "",
            "第二章 乙", "", shared, "",
            "第二章 乙", "", shared, "",
        ]));

        Assert.Empty(ChapterContentDuplicates.FindCrossTitle(document, document.Entries));
    }

    [Fact]
    public async Task Unrelated_chapters_are_not_paired_by_the_bucketing()
    {
        // Every chapter has the same shape and the same length, which is what a length-and-sample
        // signature buckets together. The full comparison still has to decide, and decide "no".
        var document = await DocumentAsync(string.Join('\n',
        [
            "第一章 甲", "", Paragraph("甲"), "",
            "第二章 乙", "", Paragraph("乙"), "",
            "第三章 丙", "", Paragraph("丙"), "",
            "第四章 丁", "", Paragraph("丁"), "",
        ]));

        Assert.Empty(ChapterContentDuplicates.FindCrossTitle(document, document.Entries));
    }
}

