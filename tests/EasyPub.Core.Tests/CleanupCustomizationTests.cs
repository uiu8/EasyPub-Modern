using System.Text.Json;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class CleanupCustomizationTests
{
    [Fact]
    public async Task Actual_epub_uses_book_override_and_preserves_source()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var output = Path.ChangeExtension(path, ".epub");
        const string source = "第一章 开始\n独有推广行\n故事正文不可丢失。\n第二章 后续\n正文继续。";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var options = new TextCleanupOptions { RemoveSiteNotices = true, Advertisement = new() { Pattern = "独有推广行", IsRegex = false, UsePromotionHeuristics = false } };
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
            var cleanup = new TextCleanupOptions { RemoveSiteNotices = true, Advertisement = new() { PreservePattern = "角色" } };
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
