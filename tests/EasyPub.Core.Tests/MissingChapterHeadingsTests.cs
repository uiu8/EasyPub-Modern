using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class MissingChapterHeadingsTests
{
    // Exercise recovery of trees recognized with the older restrictive expression.

    [Theory]
    [InlineData("久=九", "第六百八十久章 奥秘", 1)]
    [InlineData("", "第六百八十久章 奥秘", 0)]
    [InlineData("玖=九", "第六百八十玖章 奥秘", 1)]
    [InlineData("久=九", "第六百八十八章 久别重逢", 0)]
    [InlineData("久=九", "第六百久十久章 奥秘", 0)]
    public async Task Number_typos_require_mapping_and_missing_number(string rules, string title, int count)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, $"第六百八十八章开始\n正文\n\n{title}\n\n正文\n\n第六百九十章结束\n正文");
            var document = await ChapterTreeDocument.LoadAsync(path, HeadingSyntax.LegacyPattern, new() { HeadingNumberCorrections = rules });
            var found = MissingChapterHeadings.Find(document);
            Assert.Equal(count, found.Count);
            if (count > 0) { Assert.Equal("第六百八十九章 奥秘", found[0].Title); Assert.Equal(title, found[0].Original); }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Correction_settings_roundtrip_and_allow_empty_book_override()
    {
        var global = new TocHierarchyOptions { HeadingNumberCorrections = "玖=九" };
        var plan = new ChapterTreePlan("hash", []) { HeadingNumberCorrections = "" };
        var saved = System.Text.Json.JsonSerializer.Deserialize<ChapterTreePlan>(System.Text.Json.JsonSerializer.Serialize(plan));
        Assert.Equal("", global.ForBook(saved).HeadingNumberCorrections);
        Assert.Equal("玖=九", global.ForBook(new("hash", [])).HeadingNumberCorrections);
        Assert.Throws<ArgumentException>(() => HeadingTypoRules.Parse("八=九"));
        Assert.Throws<ArgumentException>(() => HeadingTypoRules.Parse("久=九\n久=八"));
        var preset = new NamedNumericHeadingPreset("自定义", NumericHeadingRule.DefaultPattern, 5, false) { HeadingNumberCorrections = "玖=九" };
        Assert.Equal(preset, System.Text.Json.JsonSerializer.Deserialize<NamedNumericHeadingPreset>(System.Text.Json.JsonSerializer.Serialize(preset)));
    }

    [Theory]
    [InlineData("六百五十八章 回归", true)]
    [InlineData("第六百五十八张 回归", true)]
    [InlineData("第六百五十八 回归", true)]
    [InlineData("六百五十八 回归", false)]
    [InlineData("第六百五十八章中提到 回归", false)]
    [InlineData("第六百六十八张 回归", false)]
    public async Task Only_supported_mistakes_inside_gap_are_candidates(string title, bool expected)
    {
        await WithDocument($"第六百五十七章 开始\n正文甲\n\n{title}\n\n正文乙\n\n第六百六十章 结束\n正文丙", document =>
        {
            var found = MissingChapterHeadings.Find(document);
            Assert.Equal(expected ? 1 : 0, found.Count);
            if (!expected) return;
            var entries = MissingChapterHeadings.Repair(document, document.Entries, found.Select(c => c.Line).ToArray());
            Assert.Contains(entries, e => e.Title == "第六百五十八章 回归" && e.TitleLineNumber == 4);
            var lines = entries.SelectMany(e => e.ContentRanges.SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1))
                .Concat(e.TitleLineNumber.HasValue ? new[] { e.TitleLineNumber.Value } : Array.Empty<int>())).Order().ToArray();
            Assert.Equal(Enumerable.Range(1, document.SourceLines.Count), lines);
            Assert.Empty(MissingChapterHeadings.Find(document, entries));
            Assert.Contains(ChapterDiagnostics.Inspect(document, currentEntries: entries), i => i.Code == "chapter_number_gap");
        });
    }

    [Theory]
    [InlineData("六百五十八章 回归", "第六百五十九张 后续", 2)]
    [InlineData("六百五十八章 回归", "第六百五十八张 重复", 0)]
    [InlineData("第六百五十九张 后续", "六百五十八章 回归", 0)]
    public async Task Batch_candidates_must_be_unique_and_ordered(string first, string second, int expected)
    {
        await WithDocument($"第六百五十七章 开始\n正文甲\n\n{first}\n\n正文乙\n\n{second}\n\n正文丙\n\n第六百六十章 结束\n正文丁", document =>
        {
            var found = MissingChapterHeadings.Find(document);
            Assert.Equal(expected, found.Count);
            if (expected == 0) return;
            var repaired = MissingChapterHeadings.Repair(document, document.Entries, found.Select(c => c.Line).ToArray());
            Assert.Equal(document.Entries.Count + 2, repaired.Count);
            Assert.DoesNotContain(ChapterDiagnostics.Inspect(document, currentEntries: repaired), i => i.Code == "chapter_number_gap");
            Assert.Contains(ChapterReviewAnalyzer.Analyze(document).Groups, g => g.Issue.Code == "chapter_heading_typo");
            var partial = MissingChapterHeadings.Repair(document, document.Entries, [found[0].Line]);
            Assert.Single(MissingChapterHeadings.Find(document, partial));
        });
    }

    private static async Task WithDocument(string text, Action<ChapterTreeDocument> check)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try { await File.WriteAllTextAsync(path, text); check(await ChapterTreeDocument.LoadAsync(path, HeadingSyntax.LegacyPattern)); Assert.Equal(text, await File.ReadAllTextAsync(path)); }
        finally { File.Delete(path); }
    }
}
