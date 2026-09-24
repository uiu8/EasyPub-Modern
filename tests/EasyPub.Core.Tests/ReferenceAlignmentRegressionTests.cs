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
    [Fact]
    public void Missing_heading_does_not_borrow_the_next_volumes_exact_chapter()
    {
        var catalog = ReferenceCatalogInput.ParseText("第一卷\n第1章 北城起点\n第2章 北城旅程\n第3章 北城终点\n第二卷\n第1章 南城起点\n第2章 南城旅程\n第3章 南城终点")!;
        string[] lines = ["第1章 北城起点", "", "正文。", "",
            "第3章 北城终点", "", "正文。", "",
            "第1章 南城起点", "", "正文。", "",
            "第2章 南城旅程", "", "正文。", "",
            "第3章 南城终点", "", "正文。"];

        var location = ReferenceLocator.Locate(lines, catalog);

        Assert.Null(location.Chapters[1].Line);
        Assert.Equal(13, location.Chapters[4].Line);
        Assert.Equal(1, location.Missing);
    }

    [Fact]
    public void Exact_heading_inside_a_missing_chapters_gap_is_not_taken_by_rescue()
    {
        var catalog = ReferenceCatalogInput.ParseText("第1章 起点\n第2章 北城旅程\n第3章 终点\n第2章 南城旅程")!;
        // No blank neighbours: the heading is considered only by the final rescue pass.
        string[] lines = ["第1章 起点", "", "正文。", "第2章 南城旅程", "正文。", "", "第3章 终点"];
        var location = ReferenceLocator.Locate(lines, catalog);
        Assert.Null(location.Chapters[1].Line);
        Assert.DoesNotContain(location.Chapters, chapter => chapter.Reference.Title == "第2章 北城旅程" && chapter.Line == 4);
    }

    [Fact]
    public void Exact_displaced_chapters_and_their_duplicate_candidates_keep_their_identity()
    {
        var catalog = ReferenceCatalogInput.ParseText("第一卷\n第1章 北城起点\n第2章 北城旅程\n第3章 北城终点\n第二卷\n第1章 南城起点\n第2章 南城旅程\n第3章 南城终点")!;
        string[] lines = ["第1章 北城起点", "", "第3章 北城终点", "",
            "第1章 南城起点", "", "第2章 北城旅程", "", "第2章 北城旅程", "",
            "第2章 南城旅程", "", "第3章 南城终点"];
        var location = ReferenceLocator.Locate(lines, catalog);
        Assert.Equal(0, location.Missing);
        Assert.Equal(7, location.Chapters[1].Line);
        Assert.Equal(new[] { 7, 9 }, location.Chapters[1].Lines);
        Assert.Equal(11, location.Chapters[4].Line);
    }

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

    /// <summary>
    /// The last-resort pass must say which evidence rescued a chapter. Without that, the only way to
    /// tell an expensive search that works from one that is merely expensive is to guess.
    /// </summary>
    [Fact]
    public void The_last_resort_pass_records_which_evidence_accepted_a_line()
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

        var stats = ReferenceLocator.Locate(lines, catalog).Stats;

        Assert.NotNull(stats);
        Assert.Equal(1, stats!.SimilarityHits);
        Assert.Equal(1, stats.ThirdHits);
        Assert.Equal(1, stats.ScannedChapters);
        // The search is bounded by the chapters that were placed, so the cost is the gap it searched
        // rather than the whole file. What matters is that the figure is recorded and finite.
        Assert.InRange(stats.ScannedLines, 1, lines.Count);
        Assert.Equal(0, stats.Unrescuable);
    }

    /// <summary>
    /// The shape behind the slow-book report: a directory far larger than the local text sends every
    /// remaining chapter through a full-file scan, and virtually none of them can succeed. The numbers
    /// recorded here are what makes that visible instead of looking like a healthy alignment with a
    /// long "missing" list.
    /// </summary>
    [Fact]
    public void A_directory_far_larger_than_the_text_reports_a_fruitless_last_resort_pass()
    {
        var lines = new List<string> { "第一章 起点", "", "正文一", "", "第二章 前行", "", "正文二", "", "第三章 终点", "", "正文三", "" };
        var nodes = new List<ReferenceNode>
        {
            new("第一章 起点", ReferenceNodeKind.Chapter, "https://example.com/1", null),
            new("第二章 前行", ReferenceNodeKind.Chapter, "https://example.com/2", null),
            new("第三章 终点", ReferenceNodeKind.Chapter, "https://example.com/3", null),
        };
        // Titles with at least two words are the ones the last-resort pass can even search for;
        // the rest are counted separately, which is the honest answer rather than "not found".
        for (var number = 4; number <= 20; number++)
            nodes.Add(new($"第{number}章 名字", ReferenceNodeKind.Chapter, $"https://example.com/{number}", null));
        nodes.Add(new("第一百章 一", ReferenceNodeKind.Chapter, "https://example.com/x", null));
        var catalog = new ReferenceCatalog("test", "测试", nodes);

        var location = ReferenceLocator.Locate(lines, catalog);
        var stats = location.Stats;

        Assert.NotNull(stats);
        // 18 chapters the cheap passes could not place. One of them ("第一百章 一") has no usable
        // title words, so it is never searched; the rest sit after the last placed chapter, where the
        // search is bounded by the file itself, and not one of them succeeds — the honest answer,
        // because the text really does not contain them.
        Assert.Equal(21, stats!.CatalogChapters);
        Assert.Equal(17, stats.ScannedChapters);
        Assert.Equal(51, stats.ScannedLines);
        Assert.Equal(0, stats.ThirdHits);
        Assert.Equal(1, stats.Unrescuable);
        Assert.Equal(3, stats.SecondPassLocated);
        Assert.Equal(18, stats.SecondPassMissing);
        Assert.Equal(18, location.Missing);
    }

    /// <summary>
    /// A chapter is only ever rescued from the gap its placed neighbours bracket. A line outside it
    /// belongs to another part of the book, so taking it is how a chapter ends up bound thousands of
    /// lines from where it belongs.
    ///
    /// This case also records the limit of what the locator can decide on its own: the text holds one
    /// "第三章 终章" and one "第四章 之后", and the directory's chapter 3 is titled differently. Both
    /// readings — "chapter 3's title is miswritten" and "chapter 3 is absent" — fit the text.
    /// The exact chapter 4 anchors the upper bound before rescue, so the ambiguous chapter 3
    /// outside the gap remains unlocated and needs human review.
    /// </summary>
    [Fact]
    public void A_chapter_is_only_rescued_from_inside_its_neighbours_gap()
    {
        var lines = new List<string>
        {
            "第一章 起点", "", "正文", "",
            "第二章 继续", "", "正文", "",
            "第四章 之后", "", "正文", "",
            "第三章 终章", "", "正文", "",
        };
        var catalog = new ReferenceCatalog("test", "测试", [
            new("第一章 起点", ReferenceNodeKind.Chapter, "https://example.com/1", null),
            new("第二章 继续", ReferenceNodeKind.Chapter, "https://example.com/2", null),
            new("第三章 第一份终章", ReferenceNodeKind.Chapter, "https://example.com/3", null),
            new("第四章 之后", ReferenceNodeKind.Chapter, "https://example.com/4", null),
        ]);

        var location = ReferenceLocator.Locate(lines, catalog);

        // The later exact chapter now anchors the gap before weaker matching starts.
        // The differently titled chapter 3 outside that gap needs human review.
        Assert.Equal(3, location.Found);
        Assert.Null(location.Chapters[2].Line);
        Assert.Equal(3, location.Chapters.Where(c => c.Line is not null).Select(c => c.Line).Distinct().Count());
        Assert.Equal(9, location.Chapters[3].Line);
    }

    /// <summary>
    /// A chapter listed by the directory whose number appears nowhere in the text stays missing, and
    /// the chapters around it keep the lines they actually own.
    /// </summary>
    [Fact]
    public void A_gap_the_text_really_has_stays_missing()
    {
        var lines = new List<string>
        {
            "第一章 起点", "", "正文", "",
            "第二章 继续", "", "正文", "",
            "第四章 之后", "", "正文", "",
            "第五章 收束", "", "正文", "",
        };
        var catalog = new ReferenceCatalog("test", "测试", [
            new("第一章 起点", ReferenceNodeKind.Chapter, "https://example.com/1", null),
            new("第二章 继续", ReferenceNodeKind.Chapter, "https://example.com/2", null),
            new("第三章 中断", ReferenceNodeKind.Chapter, "https://example.com/3", null),
            new("第四章 之后", ReferenceNodeKind.Chapter, "https://example.com/4", null),
            new("第五章 收束", ReferenceNodeKind.Chapter, "https://example.com/5", null),
        ]);

        var location = ReferenceLocator.Locate(lines, catalog);

        Assert.Null(location.Chapters[2].Line);
        Assert.Equal(9, location.Chapters[3].Line);
        Assert.Equal(13, location.Chapters[4].Line);
        Assert.Equal(1, location.Missing);
    }

    /// <summary>
    /// A chapter title written as a bare number carries its number even though no 「章」 follows it,
    /// so the number test can confirm it. Without that the line matched on resemblance alone and the
    /// chapter came out missing whenever the resemblance fell under the threshold.
    /// </summary>
    [Fact]
    public void A_bare_number_title_keeps_its_number()
    {
        var location = ReferenceLocator.Locate(
            ["001：开始！", "正文", "002：继续？", "正文"],
            new ReferenceCatalog("test", "测试", [
                new("001：开始！", ReferenceNodeKind.Chapter, "https://example.com/1", null),
                new("002：继续？", ReferenceNodeKind.Chapter, "https://example.com/2", null)]));

        Assert.Equal(0, location.Missing);
        Assert.Equal(3, location.Chapters[1].Line);
    }
}
