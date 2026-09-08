using System.Text.Json;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class CleanupCustomizationTests
{
    [Fact]
    public async Task Optional_real_book_preservation_and_conversion_smoke()
    {
        var path = Environment.GetEnvironmentVariable("EASYPUB_CLEANUP_REAL_BOOK");
        if (string.IsNullOrWhiteSpace(path)) return;
        var bytes = await File.ReadAllBytesAsync(path);
        var source = System.Text.Encoding.UTF8.GetString(bytes);
        var options = new TextCleanupOptions { RemoveSiteNotices = true };
        var preview = TextCleanupPipeline.Apply(source, options);
        Assert.NotEmpty(preview.Changes);
        var line = source.Split('\n')[preview.Changes[0].LineNumber - 1].Trim();
        Assert.NotEmpty(line);
        options = options with { Advertisement = options.Advertisement.AddKeyword(line, true) };
        Assert.Contains(line, TextCleanupPipeline.Apply(source, options).Text);
        var output = Path.Combine(Path.GetTempPath(), "easypub-real-" + Guid.NewGuid() + ".epub");
        try
        {
            await new EasyPubConverter().ConvertAsync(new(path, output, Options: new() { TextCleanup = options }));
            using var zip = System.IO.Compression.ZipFile.OpenRead(output);
            var content = string.Join("\n", zip.Entries.Where(e => e.FullName.Contains("chapter") && e.FullName.EndsWith(".html")).Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }));
            Assert.Contains(line, System.Net.WebUtility.HtmlDecode(content));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public void Literal_keywords_ignore_blank_lines_deduplicate_and_preserve_regex()
    {
        Assert.Equal(new[] { "abc", "广告" }, AdvertisementRuleOptions.ParseKeywords(" abc\r\n\nABC\n广告 "));
        var ad = new AdvertisementRuleOptions { Pattern = "existing.*pattern" }.AddKeyword("[推广].*", false).AddKeyword("[推广].*", false);
        Assert.Single(ad.MatchKeywords);
        Assert.Equal("existing.*pattern", ad.Pattern);
        var preview = TextCleanupPipeline.Apply("[推广].*\n普通推广文字", new() { RemoveSiteNotices = true, Advertisement = ad with { UsePromotionHeuristics = false } });
        Assert.Single(preview.Changes);
        Assert.Contains("普通推广文字", preview.Text);
        Assert.Throws<ArgumentException>(() => ad.AddKeyword("\n ", false));
        Assert.Throws<ArgumentException>(() => ad.AddKeyword("甲\n乙", false));
    }

    [Fact]
    public void Preserve_keyword_wins_over_keyword_regex_and_heuristics_and_roundtrips()
    {
        var ad = new AdvertisementRuleOptions().AddKeyword("本站", false).AddKeyword("正文", true);
        var options = new TextCleanupOptions { RemoveSiteNotices = true, Advertisement = ad };
        var loaded = JsonSerializer.Deserialize<TextCleanupOptions>(JsonSerializer.Serialize(options))!;
        var result = TextCleanupPipeline.Apply("请记住本站 正文\n更新不易 书友分享 www.example.org 正文\n本站独有广告", loaded);
        Assert.Single(result.Changes);
        Assert.Contains("正文", result.Text);
        Assert.Empty(BuiltinCleanupConfiguration.Restore(loaded).Advertisement.MatchKeywords);
        Assert.Empty(BuiltinCleanupConfiguration.Restore(loaded).Advertisement.PreserveKeywords);
    }
    [Fact]
    public async Task Actual_epub_uses_book_override_and_preserves_source()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var output = Path.ChangeExtension(path, ".epub");
        const string source = "第一章 开始\n独有推广行\n故事正文不可丢失。\n第二章 后续\n正文继续。";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var options = new TextCleanupOptions { RemoveSiteNotices = true, Advertisement = new AdvertisementRuleOptions { Pattern = "", UsePromotionHeuristics = false }.AddKeyword("独有推广行", false) };
            var request = BatchConversionRequestFactory.Create([new(path, CleanupOverride: options)], Path.GetTempPath(), "epub", null, new()).Single();
            await new EasyPubConverter().ConvertAsync(request);
            using var zip = System.IO.Compression.ZipFile.OpenRead(output);
            var content = string.Join("\n", zip.Entries.Where(entry => entry.FullName.Contains("chapter") && entry.FullName.EndsWith(".html")).Select(entry => { using var reader = new StreamReader(entry.Open()); return reader.ReadToEnd(); }));
            Assert.DoesNotContain("独有推广行", content);
            Assert.Contains("故事正文不可丢失", content);
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); File.Delete(output); }
    }
    [Fact]
    public void Advertisement_can_replace_default_detection_and_preserve_exceptions()
    {
        var options = new TextCleanupOptions { RemoveSiteNotices = true, Advertisement = new() { Pattern = "本站专属推广", IsRegex = false, UsePromotionHeuristics = false, PreservePattern = "角色说" } };
        var result = TextCleanupPipeline.Apply("本站专属推广\n角色说本站专属推广\n请记住本站\n正常正文", options);
        Assert.Single(result.Changes);
        Assert.Contains("角色说本站专属推广", result.Text);
        Assert.Contains("请记住本站", result.Text);
        Assert.Equal(1, result.Changes[0].LineNumber);
    }

    [Fact]
    public void Builtin_override_can_replace_algorithm_without_mutating_original_options()
    {
        var options = new TextCleanupOptions { NormalizePunctuation = true, BuiltinOverrides = new Dictionary<string, BuiltinCleanupOverride>
        {
            [nameof(TextCleanupOptions.NormalizePunctuation)] = new() { UseDefaultAlgorithm = false, Rules = [new() { Pattern = "foo", Replacement = "bar" }] }
        }};
        Assert.Equal("bar,", TextCleanupPipeline.Apply("foo,", options).Text);
        Assert.True(options.NormalizePunctuation);
        Assert.Equal("foo,", TextCleanupPipeline.Apply("foo,", options with { NormalizePunctuation = false }).Text);
    }

    [Fact]
    public void Restore_defaults_preserves_independent_rules_exclusions_and_enabled_flags()
    {
        var options = new TextCleanupOptions { RemoveSiteNotices = true, Advertisement = new() { Pattern = "特殊广告" }, HardWrapMinimumLength = 70, CustomRules = [new() { Pattern = "abc" }], ExcludedChangeKeys = ["keep"], BuiltinOverrides = new Dictionary<string, BuiltinCleanupOverride> { [nameof(TextCleanupOptions.RemoveSiteNotices)] = new() { UseDefaultAlgorithm = false } } };
        var one = BuiltinCleanupConfiguration.Restore(options, nameof(TextCleanupOptions.RemoveSiteNotices));
        Assert.Equal(70, one.HardWrapMinimumLength);
        Assert.Equal(AdvertisementRuleOptions.DefaultPattern, one.Advertisement.Pattern);
        var all = BuiltinCleanupConfiguration.Restore(options);
        Assert.Equal(8, all.HardWrapMinimumLength);
        Assert.Empty(all.BuiltinOverrides);
        Assert.Equal(options.CustomRules, all.CustomRules);
        Assert.Equal(options.ExcludedChangeKeys, all.ExcludedChangeKeys);
        Assert.True(all.RemoveSiteNotices);
    }

    [Fact]
    public void Per_book_rules_override_shared_and_roundtrip_through_project_json()
    {
        var shared = new TextCleanupOptions { RemoveSiteNotices = true };
        var separate = shared with { Advertisement = new() { Pattern = "special" } };
        var book = new EasyPubProjectBook(Path.GetFullPath("book.txt"), null, null, null, []) { CleanupOverride = separate };
        var restored = JsonSerializer.Deserialize<EasyPubProjectBook>(JsonSerializer.Serialize(book))!;
        var requests = BatchConversionRequestFactory.Create([new("book.txt", CleanupOverride: restored.CleanupOverride), new("other.txt")], Path.GetTempPath(), "epub", null, new() { TextCleanup = shared });
        Assert.Equal("special", requests[0].Options!.TextCleanup.Advertisement.Pattern);
        Assert.Equal(AdvertisementRuleOptions.DefaultPattern, requests[1].Options!.TextCleanup.Advertisement.Pattern);
        Assert.Null(JsonSerializer.Deserialize<EasyPubProjectBook>("{\"InputPath\":\"old.txt\",\"Illustrations\":[]}")!.CleanupOverride);
    }

    [Fact]
    public async Task Preflight_uses_same_advertisement_exception_as_conversion()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, "第一章 开始\n请记住本站，角色正在说话。");
            var cleanup = new TextCleanupOptions { RemoveSiteNotices = true, Advertisement = new AdvertisementRuleOptions().AddKeyword("角色", true) };
            var request = new ConversionRequest(path, Path.ChangeExtension(path, ".epub"), Options: new() { TextCleanup = cleanup }) { AutomaticChecks = new() { Targets = [], Cleanup = new() { RemoveSiteNotices = true } } };
            var report = await new ConversionPreflightInspector().InspectAsync([request]);
            Assert.DoesNotContain(report.Issues, issue => issue.Code == "cleanup_check");
            Assert.Empty(TextCleanupPipeline.Apply("请记住本站，角色正在说话。", cleanup).Changes);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Invalid_ad_regex_is_rejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => TextCleanupPipeline.Apply("正文", new() { RemoveSiteNotices = true, Advertisement = new() { Pattern = "[" } }));
    }
}
