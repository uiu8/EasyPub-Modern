using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class BookAnalysisCoordinatorTests
{
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
