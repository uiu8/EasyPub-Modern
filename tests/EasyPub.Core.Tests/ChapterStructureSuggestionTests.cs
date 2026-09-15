using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class ChapterStructureSuggestionTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Repeated_resets_require_missing_group_boundaries(bool volumes, bool expected)
    {
        var source = string.Join("\n", Enumerable.Range(1, 3).SelectMany(v =>
            (volumes ? new[] { $"第{v}卷 标题" } : Array.Empty<string>()).Concat(new[] { "第1章 开始", "正文", "第2章 继续", "正文", "第3章 后续", "正文", "第20章 结束", "正文" })));
        await WithBook(source, doc =>
        {
            Assert.Equal(expected, ChapterStructureSuggestion.Find(doc, doc.Entries) is not null);
            if (expected)
            {
                var recovered = ChapterStructureRecovery.Analyze(doc);
                Assert.Null(ChapterStructureSuggestion.Find(doc, recovered.Entries));
            }
        });
    }

    [Fact]
    public async Task Missing_heading_repair_remains_alongside_structure_advice()
    {
        var source = "第657章 开始\n正文\n\n第六百五十八张 回归\n\n正文\n\n第660章 结束\n正文\n第一章 新段\n正文\n第二章 后续\n正文\n第三章 后续\n正文\n第二卷实际上这是感言。\n正文";
        await WithBook(source, doc =>
        {
            var groups = ChapterReviewAnalyzer.Analyze(doc).Groups;
            Assert.Contains(groups, g => g.Issue.Code == "chapter_heading_typo");
            Assert.Contains(groups, g => g.Issue.Code == "chapter_structure_suggested");
            Assert.NotEmpty(MissingChapterHeadings.Find(doc));
        });
    }

    [Fact]
    public async Task Isolated_gap_does_not_recommend_rebuilding()
        => await WithBook("第86章 甲\n正文\n第89章 乙\n正文\n第88章 丙\n正文", doc =>
        {
            Assert.Null(ChapterStructureSuggestion.Find(doc, doc.Entries));
            Assert.Contains(ChapterReviewAnalyzer.Analyze(doc).Groups, g => g.Issue.Code == "chapter_number_gap");
        });

    private static async Task WithBook(string source, Action<ChapterTreeDocument> test)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, source);
            var doc = await ChapterTreeDocument.LoadAsync(path, hierarchy: new() { Enabled = true });
            test(doc);
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }
}
