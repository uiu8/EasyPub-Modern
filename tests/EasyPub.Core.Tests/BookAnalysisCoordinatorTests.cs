using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class BookAnalysisCoordinatorTests
{
    [Fact]
    public async Task Automatic_analysis_detects_site_advertising_without_enabling_cleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-ad-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "book.txt");
        try
        {
            await File.WriteAllTextAsync(path, "第一章 开始\n正文\n更新不易，请书友分享，速读谷 www.sudugu.org，无错最新章节\n正文");
            var result = await new BookAnalysisCoordinator().AnalyzeAsync([new ConversionRequest(path, Path.Combine(root, "book.epub"))]);
            Assert.Contains(result.Report.Issues, issue => issue.Code == "site_notices" && issue.LineNumber == 3);
            Assert.Equal(BookReadiness.NeedsReview, result.Books[0].Readiness.State);
            var enabled = await new BookAnalysisCoordinator().AnalyzeAsync([new ConversionRequest(path, Path.Combine(root, "book.epub"))
            {
                Options = ConversionOptions.LegacyDefault with { TextCleanup = new TextCleanupOptions { RemoveSiteNotices = true } }
            }]);
            Assert.Contains(enabled.Report.Issues, issue => issue.Code == "site_notices" && issue.Severity == PreflightSeverity.Information);
            Assert.Equal(BookReadiness.Ready, enabled.Books[0].Readiness.State);

            var realBook = Environment.GetEnvironmentVariable("EASYPUB_AD_REGRESSION_BOOK");
            if (!string.IsNullOrWhiteSpace(realBook))
            {
                var actual = await new BookAnalysisCoordinator().AnalyzeAsync([new ConversionRequest(realBook, Path.Combine(root, "actual.epub"))]);
                Assert.Contains(actual.Report.Issues, issue => issue.Code == "site_notices");
                var preview = TextCleanupPipeline.Apply(await TextCleanupPipeline.ReadFileAsync(realBook, TextEncodingMode.Auto), new TextCleanupOptions { RemoveSiteNotices = true });
                Assert.Contains(preview.Changes, change => change.LineNumber == 27073);
                Console.WriteLine($"REAL_BOOK_ADS={preview.Changes.Count}; LINE_27073=DETECTED");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Readiness_maps_errors_warnings_and_clean_reports_to_three_user_states()
    {
        var ready = ReadinessEvaluator.Evaluate([]);
        var review = ReadinessEvaluator.Evaluate([
            new ConversionPreflightIssue("book.txt", PreflightSeverity.Warning, "warning", "建议检查。")]);
        var blocked = ReadinessEvaluator.Evaluate([
            new ConversionPreflightIssue("book.txt", PreflightSeverity.Error, "error", "必须修复。")]);

        Assert.Equal(BookReadiness.Ready, ready.State);
        Assert.Equal("可直接转换", ready.Label);
        Assert.Equal(BookReadiness.NeedsReview, review.State);
        Assert.Equal("建议处理", review.Label);
        Assert.Equal(BookReadiness.Blocked, blocked.State);
        Assert.Equal("必须处理", blocked.Label);
    }

    [Fact]
    public async Task Analysis_returns_chapter_snapshot_and_reuses_it_for_a_later_subset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-book-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var firstPath = Path.Combine(root, "first.txt");
        var secondPath = Path.Combine(root, "second.txt");
        await File.WriteAllTextAsync(firstPath, "第一章 雨夜\n正文\n第二章 天明\n正文");
        await File.WriteAllTextAsync(secondPath, "第一章 开始\n正文");
        var first = new ConversionRequest(firstPath, Path.Combine(root, "first.epub"));
        var second = new ConversionRequest(secondPath, Path.Combine(root, "second.epub"));
        var coordinator = new BookAnalysisCoordinator();

        try
        {
            var batch = await coordinator.AnalyzeAsync([first, second]);
            var subset = await coordinator.AnalyzeAsync([first]);

            Assert.False(batch.Reused);
            Assert.True(subset.Reused);
            Assert.Equal(2, batch.Books[0].ChapterCandidateCount);
            Assert.Equal(BookReadiness.Ready, batch.Books[0].Readiness.State);
            Assert.Equal(Path.GetFullPath(firstPath), subset.Books[0].InputPath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Issue_actions_are_expressed_as_user_tasks()
    {
        Assert.Equal("处理章节", ReadinessEvaluator.ActionLabel(PreflightTargetKind.Chapters));
        Assert.Equal("调整输出", ReadinessEvaluator.ActionLabel(PreflightTargetKind.Output));
        Assert.Equal("设置 KindleGen", ReadinessEvaluator.ActionLabel(PreflightTargetKind.Mobi));
    }
}
