using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class AutomaticCheckTests
{
    [Fact]
    public async Task Selected_cleanup_rules_are_cached_independently_and_do_not_change_the_source()
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-check-options-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "book.txt");
            const string text = "第一章 开始\n正文\n\n\n测试水印\n更多精彩小说，请访问：www.example.org";
            await File.WriteAllTextAsync(path, text);
            var custom = new TextCleanupCustomRule { Name = "测试水印", Pattern = "测试水印", Replacement = "" };
            var checks = new AutomaticCheckOptions { Targets = [], Cleanup = new TextCleanupOptions { CollapseBlankLines = true, CustomRules = [custom] } };
            var request = new ConversionRequest(path, Path.Combine(root, "book.epub")) { AutomaticChecks = checks };
            var coordinator = new BookAnalysisCoordinator();
            var first = await coordinator.AnalyzeAsync([request]);
            Assert.Contains(first.Report.Issues, issue => issue.Message.Contains("测试水印"));
            Assert.Contains(first.Report.Issues, issue => issue.Message.Contains("空行"));
            Assert.DoesNotContain(first.Report.Issues, issue => issue.Message.Contains("广告"));
            Assert.True((await coordinator.AnalyzeAsync([request])).Reused);
            var noRules = await coordinator.AnalyzeAsync([request with { AutomaticChecks = checks with { Cleanup = new() } }]);
            Assert.False(noRules.Reused);
            Assert.Empty(noRules.Report.Issues);
            var off = await coordinator.AnalyzeAsync([request with { AutomaticChecks = checks with { Enabled = false } }]);
            Assert.Empty(off.Report.Issues);
            Assert.Equal(text, await File.ReadAllTextAsync(path));
            var store = new AppSettingsStore(Path.Combine(root, "settings.json"));
            await store.SaveAsync(EasyPubAppSettings.Default with { AutomaticChecks = checks });
            var loaded = (await store.LoadAsync()).AutomaticChecks;
            Assert.True(loaded.Cleanup.CollapseBlankLines);
            Assert.Equal(custom.Pattern, Assert.Single(loaded.Cleanup.CustomRules).Pattern);
            Assert.Contains("测试水印", loaded.Summary);
        }
        finally { Directory.Delete(root, true); }
    }
}
