using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ChapterReviewAnalyzerTests
{
    [Theory]
    [InlineData("001：开端\n甲\n乙\n丙\n002：后续\n丁\n戊\n己", true)]
    [InlineData("1：要求\n2：注意\n3：说明", false)]
    [InlineData("44.4度的嘴角弧度\n甲\n乙\n丙\n48.48度的变化\n丁\n戊\n己", false)]
    [InlineData("第一章 开端\n甲\n乙\n丙\n002：后续\n丁\n戊\n己", false)]
    public async Task Empty_tree_suggests_numeric_chapters_but_not_short_lists_or_decimals(string source, bool expected)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, source);
            var document = await ChapterTreeDocument.LoadAsync(path, hierarchy: new() { RecognizeNumericHeadings = false, NumericHeadingMinimumBodyLines = 0 });
            var groups = ChapterReviewAnalyzer.Analyze(document).Groups;
            Assert.Equal(expected, groups.Any(g => g.Issue.Code == "numeric_chapters_suspected"));
            if (expected)
            {
                var suggestion = ChapterReviewAnalyzer.FindNumericChapters(document)!;
                var recognized = await ChapterTreeDocument.LoadAsync(path, hierarchy: document.RecognitionOptions with
                { RecognizeNumericHeadings = true, NumericHeadingPattern = suggestion.Pattern, NumericHeadingMinimumBodyLines = suggestion.MinimumBodyLines });
                Assert.Equal(suggestion.Lines, recognized.Entries.Where(e => !e.IsFrontMatter).Select(e => e.TitleLineNumber!.Value));
                var report = await new ConversionPreflightInspector().InspectAsync([new ConversionRequest(path, Path.ChangeExtension(path, ".epub"))
                { Options = new() { TocHierarchy = document.RecognitionOptions } }]);
                Assert.Contains(report.Issues, i => i.Code == "numeric_chapters_suspected" && i.LineNumber == 1);
                Assert.DoesNotContain(report.Issues, i => i.Code == "chapter_not_found");
            }
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Numeric_list_is_one_review_group_without_changing_source()
    {
        const string source = "第一章 正章\n正文\n1：第一条\n说明\n2：第二条\n说明\n第二章 正章\n正文";
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, source);
            var document = await ChapterTreeDocument.LoadAsync(path, hierarchy: new() { RecognizeNumericHeadings = true, NumericHeadingMinimumBodyLines = 0 });
            var analysis = ChapterReviewAnalyzer.Analyze(document);
            var group = Assert.Single(analysis.Groups, g => g.Issue.Code == "numeric_body_group");
            Assert.Equal(new[] { 3, 5 }, group.Lines);
            Assert.Equal(2, group.NodeIds.Count);
            var manual = document.Entries.Select(e => e with { RecognitionSource = "manual" }).ToArray();
            Assert.DoesNotContain(ChapterReviewAnalyzer.Analyze(document, manual).Groups, g => g.Issue.Code == "numeric_body_group");
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Limited_result_keeps_total_for_load_more()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, "第1章 A\n正文\n第3章 B\n正文\n第6章 C\n正文\n第9章 D\n正文");
            var document = await ChapterTreeDocument.LoadAsync(path);
            var full = ChapterReviewAnalyzer.Analyze(document);
            var limited = ChapterReviewAnalyzer.Analyze(document, maximumGroups: 1);
            Assert.True(full.TotalGroups > 1);
            Assert.Single(limited.Groups);
            Assert.Equal(full.TotalGroups, limited.TotalGroups);
        }
        finally { File.Delete(path); }
    }
}
