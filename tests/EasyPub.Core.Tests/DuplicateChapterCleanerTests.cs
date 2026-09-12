using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class DuplicateChapterCleanerTests
{
    [Theory]
    [InlineData(false, "第121章 一击")]
    [InlineData(true, "第122章 一击")]
    public void Ignore_number_preview_matches_result_and_keeps_body(bool first, string expected)
    {
        var input = new[] { Entry("a", "第122章 一击", 2, 0), Entry("b", "第121章 一击", 3, 5) };
        Assert.Equal(2, DuplicateChapterCleaner.Clean(input).Count);
        var changes = new List<DuplicateTitleChange>();
        var result = DuplicateChapterCleaner.Clean(input, ignoreChapterNumber: true, preferFirstTitle: first, changes: changes);
        Assert.Equal(expected, Assert.Single(result).Title);
        Assert.Equal(expected, Assert.Single(changes).KeptTitle);
        Assert.Equal(input[1].ContentRanges, result[0].ContentRanges);
        var parts = new[] { Entry("c", "第1章 标题（1）", 2, 0), Entry("d", "第2章 标题（2）", 3, 5) };
        Assert.Equal(2, DuplicateChapterCleaner.Clean(parts, ignoreChapterNumber: true).Count);
    }

    private static ChapterTreeEntry Entry(string id, string title, int start, int count, int level = 1) =>
        new(id, title, level, true, start - 1, count == 0 ? [] : [new(start, start + count - 1)]);

    [Fact]
    public void Default_removes_empty_duplicates_but_not_unique_empty_titles()
    {
        var input = new[] { Entry("a", "第1章 甲", 2, 0), Entry("b", "第1章 甲", 3, 10), Entry("c", "第2章 乙", 14, 0) };
        var result = DuplicateChapterCleaner.Clean(input);
        Assert.Equal(new[] { "b", "c" }, result.Select(e => e.Id));
        Assert.Equal(3, input.Length);
        Assert.Equal(input[1].ContentRanges, result[0].ContentRanges);
    }

    [Fact]
    public void Custom_range_preserves_all_bodies_in_order_and_at_least_one_title()
    {
        var input = new[] { Entry("a", "同名", 2, 2), Entry("b", "同名", 5, 10), Entry("c", "同名", 16, 1) };
        var result = DuplicateChapterCleaner.Clean(input, 0, 10);
        Assert.Single(result);
        Assert.Equal("b", result[0].Id);
        Assert.Equal(input.SelectMany(e => e.ContentRanges), result[0].ContentRanges);
        Assert.Throws<ArgumentOutOfRangeException>(() => DuplicateChapterCleaner.Clean(input, 3, 1));
    }

    [Fact]
    public void Does_not_merge_parent_nodes_or_different_titles()
    {
        var input = new[] { Entry("a", "同名", 2, 0), Entry("b", "同名", 3, 0), Entry("child", "子章", 4, 2, 2), Entry("c", "不同", 7, 0) };
        Assert.Equal(input, DuplicateChapterCleaner.Clean(input));
    }

    [Fact]
    public async Task Repeated_volume_numbers_accept_brackets_and_chinese_or_arabic_numbers()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, "【第三卷：甲】\n正文\n第3卷 乙\n正文");
            var doc = await ChapterTreeDocument.LoadAsync(path, hierarchy: new() { Enabled = true, Level1Pattern = @"^【?第[三3]+卷" });
            var issue = Assert.Single(ChapterDiagnostics.Inspect(doc), i => i.Code == "volume_number_duplicate");
            Assert.Equal(3, issue.LineNumber);
            Assert.Equal(PreflightSeverity.Warning, issue.Severity);
        }
        finally { File.Delete(path); }
    }
}
