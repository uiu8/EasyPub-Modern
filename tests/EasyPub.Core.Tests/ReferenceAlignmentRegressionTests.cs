using System.IO;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// Regression locks for the alignment faults that made a repaired tree look scrambled:
/// a chapter located thousands of lines away because a later chapter shared its title words, and
/// author promo blocks promoted into the table of contents as if they were chapters.
/// </summary>
public class ReferenceAlignmentRegressionTests
{
    /// <summary>
    /// "第十六章 预谋" must land on the line whose number is 16, not on "第二百五十七章 预谋" several
    /// hundred chapters later. Word equality alone used to outrank an agreeing chapter number, which
    /// dragged the chapter across the whole book and inverted the rebuilt order.
    /// </summary>
    [Fact]
    public void A_later_chapter_with_the_same_title_words_does_not_capture_an_earlier_chapter()
    {
        var lines = new List<string>
        {
            "第一章 起点", "", "正文一", "",
            "第十六章 预谋（新）", "", "正文二", "",
            "第二百五十七章 预谋", "", "正文三", "",
        };
        var catalog = new ReferenceCatalog("test", "测试", [
            new ReferenceNode("第十六章 预谋", ReferenceNodeKind.Chapter, "https://example.com/16", null),
        ]);

        var location = ReferenceLocator.Locate(lines, catalog);

        Assert.Equal(5, location.Chapters[0].Line);
    }

    /// <summary>
    /// The source TXT ends with the author's promo blocks. They are isolated short lines, so the
    /// recogniser offers them as headings; the official directory never lists them, and they carry no
    /// chapter number. They must fold into the preceding chapter instead of becoming entries — and
    /// above all instead of becoming phantom roots above the volumes.
    /// </summary>
    [Fact]
    public async Task Promo_blocks_without_a_chapter_number_never_enter_the_rebuilt_tree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-promo-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, string.Join("\n", new[]
        {
            "第一章 开始", "", "正文一", "",
            "第二章 继续", "", "正文二", "",
            "新书《我怎么还活着》已经十万+", "", "推广文字", "",
        }));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var catalog = new ReferenceCatalog("test", "测试", [
                new ReferenceNode("第一章 开始", ReferenceNodeKind.Chapter, "https://example.com/1", null),
                new ReferenceNode("第二章 继续", ReferenceNodeKind.Chapter, "https://example.com/2", null),
            ]);

            var plan = ReferencePlanner.Build(document, document.Entries, catalog);
            var rebuilt = ReferencePlanner.Apply(
                document, document.Entries, plan, plan.DefaultSelection.ToArray(), true, false);

            Assert.DoesNotContain(rebuilt, entry => entry.Title.Contains("我怎么还活着"));
            Assert.Contains(rebuilt, entry => entry.Title.Contains("第二章 继续"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// A numbered extra the directory does not list (an unlisted side chapter) is still a chapter and
    /// must survive the same filter that drops the promo blocks.
    /// </summary>
    [Fact]
    public async Task An_unlisted_chapter_that_has_a_number_is_kept()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-extra-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, string.Join("\n", new[]
        {
            "第一章 开始", "", "正文一", "",
            "第九十九章 番外篇", "", "正文二", "",
        }));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var catalog = new ReferenceCatalog("test", "测试", [
                new ReferenceNode("第一章 开始", ReferenceNodeKind.Chapter, "https://example.com/1", null),
            ]);

            var plan = ReferencePlanner.Build(document, document.Entries, catalog);
            var rebuilt = ReferencePlanner.Apply(
                document, document.Entries, plan, plan.DefaultSelection.ToArray(), true, false);

            Assert.Contains(rebuilt, entry => entry.Title.Contains("第九十九章"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Once a tree has been reconciled against the official directory, a gap between chapter numbers
    /// is no longer a heuristic guess — the directory already said which chapters exist, so the gap
    /// means the source really is missing them. Suppressing it there hid the single most useful thing
    /// the user could be told.
    /// </summary>
    [Fact]
    public async Task A_gap_between_numbers_is_still_reported_after_reference_alignment()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-gap-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, string.Join("\n", new[]
        {
            "第一章 开始", "", "正文一", "",
            "第八百三十六章 结束", "", "正文二", "",
        }));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var aligned = document.Entries
                .Where(entry => !entry.IsFrontMatter)
                .Select(entry => entry with { RecognitionSource = "reference" })
                .ToArray();

            var review = ChapterReviewAnalyzer.Analyze(document, aligned);

            Assert.Contains(review.Groups, group => group.Issue.Code == "chapter_number_gap");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// Releases carry mis-typed headings. "番外 永无止境的大冒险" printed locally as "大冒除" has no
    /// chapter number to lean on, and a substring test can never match it, so without a character-level
    /// comparison the chapter is simply reported missing even though it is right there in the text.
    /// </summary>
    [Fact]
    public void A_heading_with_one_wrong_character_is_found_by_similarity()
    {
        var lines = new List<string>
        {
            "第一章 开始", "", "正文一", "",
            "正文二", "番外 永无止境的大冒除", "正文三", "",
        };
        var catalog = new ReferenceCatalog("test", "测试", [
            new ReferenceNode("第一章 开始", ReferenceNodeKind.Chapter, "https://example.com/1", null),
            new ReferenceNode("番外 永无止境的大冒险", ReferenceNodeKind.Chapter, "https://example.com/x", null),
        ]);

        var location = ReferenceLocator.Locate(lines, catalog);

        Assert.Equal(6, location.Chapters[1].Line);
        Assert.Equal(0, location.Missing);
    }
}
