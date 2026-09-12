using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class NumericHeadingSafetyTests
{
    [Fact]
    public async Task Custom_pattern_is_used_by_tree_and_conversion()
    {
        const string pattern = @"^\s*(?<number>\d{3,4})[：:]\s*(?<title>\S.*?)\s*$";
        var path = Path.Combine(Path.GetTempPath(), $"numeric-custom-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n1：正文列表\n正文\n333：真章节\n正文");
        try
        {
            var options = new TocHierarchyOptions { RecognizeNumericHeadings = true, NumericHeadingPattern = pattern, NumericHeadingMinimumBodyLines = 0 };
            var tree = await ChapterTreeDocument.LoadAsync(path, hierarchy: options);
            Assert.DoesNotContain(tree.Entries, entry => entry.Title == "1：正文列表");
            Assert.Contains(tree.Entries, entry => entry.Title == "333：真章节");
            var chapters = await LegacyTextParser.ParseAsync(path, new ConversionOptions { TocHierarchy = options }, null, default);
            Assert.DoesNotContain(chapters, chapter => chapter.Title == "1：正文列表");
            Assert.Contains(chapters, chapter => chapter.Title == "333：真章节");
            Assert.Contains(chapters.SelectMany(chapter => chapter.Paragraphs), text => text == "1：正文列表");
            Assert.Throws<ArgumentException>(() => NumericHeadingRule.Compile("^\\d+"));
            Assert.ThrowsAny<ArgumentException>(() => NumericHeadingRule.Compile("["));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Global_defaults_and_multiple_presets_persist_without_overwriting_book_override()
    {
        var path = Path.Combine(Path.GetTempPath(), $"numeric-settings-{Guid.NewGuid():N}.json");
        try
        {
            var global = new TocHierarchyOptions { RecognizeNumericHeadings = true, NumericHeadingMinimumBodyLines = 8 };
            var settings = EasyPubAppSettings.Default with
            {
                NumericHeadingDefaults = global,
                NumericHeadingPresets = [new("默认", NumericHeadingRule.DefaultPattern, 5, true), new("长章节", NumericHeadingRule.DefaultPattern, 20, false)],
            };
            var store = new AppSettingsStore(path);
            await store.SaveAsync(settings);
            var loaded = await store.LoadAsync();
            Assert.Equal(2, loaded.NumericHeadingPresets.Count);
            Assert.Equal(global, loaded.NumericHeadingDefaults);
            Assert.True(global.ForBook(null).RecognizeNumericHeadings);
            var local = new ChapterTreePlan("hash", []) { NumericHeadingRecognition = false, NumericHeadingMinimumBodyLines = 3, NumericHeadingPattern = NumericHeadingRule.DefaultPattern };
            Assert.False(global.ForBook(local).RecognizeNumericHeadings);
            Assert.Equal(3, global.ForBook(local).NumericHeadingMinimumBodyLines);
            var changedPattern = global with { NumericHeadingPattern = @"^(?<number>\d{3})：(?<title>.*)$" };
            Assert.Equal(NumericHeadingRule.DefaultPattern, changedPattern.ForBook(local with { NumericHeadingPattern = null }).NumericHeadingPattern);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public async Task Minimum_counts_nonblank_body_and_preserves_rejected_text(int minimum, bool accepted)
    {
        var path = Path.Combine(Path.GetTempPath(), $"numeric-boundary-{Guid.NewGuid():N}.txt");
        var original = "第一章 前文\n001：真正标题\n甲\n乙\n\n丙\n丁\n戊\n第二章 结束\n正文";
        await File.WriteAllTextAsync(path, original);
        try
        {
            var hierarchy = new TocHierarchyOptions { RecognizeNumericHeadings = true, NumericHeadingMinimumBodyLines = minimum };
            var tree = await ChapterTreeDocument.LoadAsync(path, hierarchy: hierarchy);
            Assert.Equal(accepted, tree.Entries.Any(entry => entry.Title == "001：真正标题"));
            foreach (var plan in new ChapterTreePlan?[] { null, tree.CreatePlan(tree.Entries) })
            {
                var chapters = await LegacyTextParser.ParseAsync(path, new ConversionOptions { TocHierarchy = hierarchy }, plan, default);
                Assert.Equal(accepted, chapters.Any(chapter => chapter.Title == "001：真正标题"));
                if (!accepted) Assert.Contains(chapters.SelectMany(chapter => chapter.Paragraphs), text => text == "001：真正标题");
            }
            Assert.Equal(original, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Book_numeric_options_round_trip_and_do_not_leak_to_other_books()
    {
        var global = new TocHierarchyOptions { RecognizeNumericHeadings = false, NumericHeadingMinimumBodyLines = 99 };
        var plan = new ChapterTreePlan("hash", []) { NumericHeadingRecognition = true, NumericHeadingMinimumBodyLines = 7 };
        var restored = System.Text.Json.JsonSerializer.Deserialize<ChapterTreePlan>(System.Text.Json.JsonSerializer.Serialize(plan))!;
        Assert.True(global.ForBook(restored).RecognizeNumericHeadings);
        Assert.Equal(7, global.ForBook(restored).NumericHeadingMinimumBodyLines);
        Assert.False(global.ForBook(null).RecognizeNumericHeadings);
        Assert.Equal(99, global.ForBook(null).NumericHeadingMinimumBodyLines);
        var requests = BatchConversionRequestFactory.Create(
            [new BookConversionSource("a.txt", ChapterTree: restored), new BookConversionSource("b.txt")],
            Path.GetTempPath(), "epub", null, new ConversionOptions { TocHierarchy = global.ForBook(null) });
        Assert.True(requests[0].Options!.TocHierarchy.RecognizeNumericHeadings);
        Assert.Equal(7, requests[0].Options!.TocHierarchy.NumericHeadingMinimumBodyLines);
        Assert.False(requests[1].Options!.TocHierarchy.RecognizeNumericHeadings);
        Assert.Equal(99, global.NumericHeadingMinimumBodyLines);
    }

    [Fact]
    public async Task Short_numbered_rules_stay_in_body_by_default()
    {
        var path = Path.Combine(Path.GetTempPath(), $"numeric-rules-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n001：规则\n说明\n\n002：规则\n说明\n第二章 继续\n正文");
        try
        {
            var hierarchy = new TocHierarchyOptions { RecognizeNumericHeadings = true };
            var tree = await ChapterTreeDocument.LoadAsync(path, hierarchy: hierarchy);
            Assert.DoesNotContain(tree.Entries, entry => entry.Title.StartsWith("001") || entry.Title.StartsWith("002"));
            var chapters = await LegacyTextParser.ParseAsync(path, new ConversionOptions { TocHierarchy = hierarchy }, null, default);
            Assert.DoesNotContain(chapters, chapter => chapter.Title.StartsWith("001") || chapter.Title.StartsWith("002"));
            Assert.Contains(chapters.SelectMany(chapter => chapter.Paragraphs), text => text == "001：规则");
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Numeric_heading_option_controls_tree_and_conversion(bool enabled)
    {
        var path = Path.Combine(Path.GetTempPath(), $"numeric-safety-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n44.4度的嘴角弧度，突然间就变成了48.48度了。\n44. 标题\n正文\n44、标题二\n正文二");
        try
        {
            var hierarchy = new TocHierarchyOptions { RecognizeNumericHeadings = enabled, NumericHeadingMinimumBodyLines = 0 };
            var tree = await ChapterTreeDocument.LoadAsync(path, hierarchy: hierarchy);
            Assert.DoesNotContain(tree.Entries, entry => entry.TitleLineNumber == 2);
            Assert.Equal(enabled, tree.Entries.Any(entry => entry.TitleLineNumber == 3));
            Assert.Equal(enabled, tree.Entries.Any(entry => entry.TitleLineNumber == 5));
            var chapters = await LegacyTextParser.ParseAsync(path, new ConversionOptions { TocHierarchy = hierarchy }, null, default);
            Assert.DoesNotContain(chapters, chapter => chapter.Title.Contains("44.4度"));
            Assert.Equal(enabled, chapters.Any(chapter => chapter.Title == "44. 标题"));
            Assert.Contains(chapters.SelectMany(chapter => chapter.Paragraphs), text => text.Contains("44.4度"));
            Assert.False(new TocHierarchyOptions().RecognizeNumericHeadings);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("44.4度的嘴角弧度，突然间就变成了48.48度了。", false)]
    [InlineData("44．4度", false)]
    [InlineData("44. 标题", true)]
    [InlineData("44、标题", true)]
    public void Numeric_headings_do_not_accept_decimals(string text, bool expected)
        => Assert.Equal(expected, ChapterTitleNormalizer.TryNormalizeNumericTitle(text, out _));
}
