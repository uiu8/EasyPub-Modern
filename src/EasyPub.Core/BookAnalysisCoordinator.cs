using System.Diagnostics;

namespace EasyPub.Core;

public enum BookReadiness
{
    Ready,
    NeedsReview,
    Blocked,
    Unchecked,
}

public sealed record ReadinessAssessment(
    BookReadiness State,
    int ErrorCount,
    int WarningCount,
    string Label,
    string Summary);

public static class ReadinessEvaluator
{
    public static ReadinessAssessment Evaluate(IEnumerable<ConversionPreflightIssue> issues, AutomaticCheckOptions? checks = null)
    {
        ArgumentNullException.ThrowIfNull(issues);
        if (checks is not null && (!checks.Enabled || !checks.ActiveLabels().Any()))
            return new ReadinessAssessment(BookReadiness.Unchecked, 0, 0,
                checks.Enabled ? "未选择检查项" : "自动检查已关闭", "未执行所选检查；转换时仍验证必要条件");
        var items = issues.ToArray();
        var errors = items.Count(issue => issue.Severity == PreflightSeverity.Error);
        var warnings = items.Count(issue => issue.Severity == PreflightSeverity.Warning);
        if (errors > 0)
            return new ReadinessAssessment(BookReadiness.Blocked, errors, warnings, "必须处理", $"{errors} 个错误阻止转换");
        if (warnings > 0)
            return new ReadinessAssessment(BookReadiness.NeedsReview, 0, warnings, "建议处理", $"{warnings} 条建议，可继续转换");
        return new ReadinessAssessment(BookReadiness.Ready, 0, 0,
            checks is null ? "可直接转换" : "所选检查通过",
            checks is null ? "未发现阻止转换的问题" : "仅代表已选择的检查项通过；不等于成品内容或真机验收通过");
    }

    public static string ActionLabel(PreflightTargetKind target) => target switch
    {
        PreflightTargetKind.Chapters => "处理章节",
        PreflightTargetKind.Output => "调整输出",
        PreflightTargetKind.Cover => "处理封面",
        PreflightTargetKind.Illustrations => "处理插图",
        PreflightTargetKind.BookInformation => "编辑书籍信息",
        PreflightTargetKind.Mobi => "设置 KindleGen",
        PreflightTargetKind.Font => "调整字体",
        PreflightTargetKind.TextCleanup => "查看广告并清理",
        PreflightTargetKind.InputBook => "查看书稿",
        _ => "查看处理",
    };
}

public sealed record BookAnalysisSnapshot(
    string InputPath,
    string Format,
    long FileSizeBytes,
    DateTime LastWriteTimeUtc,
    int ChapterCandidateCount,
    ReadinessAssessment Readiness,
    IReadOnlyList<ConversionPreflightIssue> Issues,
    DateTimeOffset AnalyzedAtUtc);

public sealed record BookAnalysisResult(
    ConversionPreflightReport Report,
    IReadOnlyList<BookAnalysisSnapshot> Books,
    bool Reused,
    TimeSpan Elapsed);

/// <summary>
/// Provides one stable analysis result for automatic inspection, manual review and conversion.
/// The underlying cache is keyed per book so changing the current selection does not force
/// unchanged long novels to be parsed again.
/// </summary>
public sealed class BookAnalysisCoordinator(ConversionPreflightCache? cache = null)
{
    private readonly ConversionPreflightCache _cache = cache ?? new ConversionPreflightCache();

    public async Task<BookAnalysisResult> AnalyzeAsync(
        IEnumerable<ConversionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var jobs = requests.ToArray();
        var stopwatch = Stopwatch.StartNew();
        var (report, reused) = await _cache.InspectAsync(jobs, cancellationToken).ConfigureAwait(false);
        if (jobs.Any(job => job.AutomaticChecks is not null))
        {
            var settings = jobs.ToDictionary(job => Path.GetFullPath(job.InputPath), job => job.AutomaticChecks, StringComparer.OrdinalIgnoreCase);
            report = report with { Issues = report.Issues.Where(issue =>
            {
                var checks = issue.InputPath is null ? jobs[0].AutomaticChecks : settings.GetValueOrDefault(Path.GetFullPath(issue.InputPath));
                return checks is null || checks.Enabled && (issue.Target == PreflightTargetKind.TextCleanup || checks.Targets.Contains(issue.Target));
            }).ToArray() };
        }
        stopwatch.Stop();

        var booksByPath = report.Books
            .GroupBy(book => Path.GetFullPath(book.InputPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var issuesByPath = report.Issues
            .Where(issue => !string.IsNullOrWhiteSpace(issue.InputPath))
            .GroupBy(issue => Path.GetFullPath(issue.InputPath!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var globalIssues = report.Issues.Where(issue => string.IsNullOrWhiteSpace(issue.InputPath)).ToArray();

        var snapshots = new List<BookAnalysisSnapshot>(jobs.Length);
        foreach (var request in jobs)
        {
            var fullPath = Path.GetFullPath(request.InputPath);
            var info = new FileInfo(fullPath);
            issuesByPath.TryGetValue(fullPath, out var bookIssues);
            var issues = globalIssues.Length == 0
                ? bookIssues ?? []
                : [.. bookIssues ?? [], .. globalIssues];
            booksByPath.TryGetValue(fullPath, out var book);
            snapshots.Add(new BookAnalysisSnapshot(
                fullPath,
                Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant(),
                info.Exists ? info.Length : 0,
                info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
                book?.ChapterCandidateCount ?? 0,
                ReadinessEvaluator.Evaluate(issues, request.AutomaticChecks),
                issues,
                DateTimeOffset.UtcNow));
        }

        return new BookAnalysisResult(report, snapshots, reused, stopwatch.Elapsed);
    }

    public void Clear() => _cache.Clear();
}
