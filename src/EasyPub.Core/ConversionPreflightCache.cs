using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EasyPub.Core;

/// <summary>
/// Keeps the most recent preflight result while every input, referenced asset and
/// conversion option remains unchanged.
/// </summary>
public sealed class ConversionPreflightCache
{
    private const int MaximumEntries = 256;
    private readonly object _gate = new();
    private readonly Dictionary<string, CacheEntry> _entries = [];

    public async Task<(ConversionPreflightReport Report, bool Reused)> InspectAsync(
        IEnumerable<ConversionRequest> requests,
        CancellationToken cancellationToken = default,
        ChapterTreeDocumentCache? documentCache = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var jobs = requests.ToArray();
        if (jobs.Length == 0) return (new ConversionPreflightReport([], []), true);

        var reports = new ConversionPreflightReport[jobs.Length];
        var misses = new List<(int Index, ConversionRequest Request, string Key)>();
        for (var index = 0; index < jobs.Length; index++)
        {
            var key = CreateKey([jobs[index]]);
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    entry.LastAccessUtc = DateTime.UtcNow;
                    reports[index] = entry.Report;
                    continue;
                }
            }
            misses.Add((index, jobs[index], key));
        }

        await Parallel.ForEachAsync(misses, cancellationToken, async (miss, token) =>
        {
            var report = await new ConversionPreflightInspector(documentCache).InspectAsync([miss.Request], token).ConfigureAwait(false);
            reports[miss.Index] = report;
            StoreSingle(miss.Request, miss.Key, report);
        }).ConfigureAwait(false);

        return (Combine(jobs, reports), misses.Count == 0);
    }

    public bool TryGet(IEnumerable<ConversionRequest> requests, out ConversionPreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var jobs = requests.ToArray();
        var reports = new ConversionPreflightReport[jobs.Length];
        lock (_gate)
        {
            for (var index = 0; index < jobs.Length; index++)
            {
                var key = CreateKey([jobs[index]]);
                if (!_entries.TryGetValue(key, out var entry))
                {
                    report = null!;
                    return false;
                }
                entry.LastAccessUtc = DateTime.UtcNow;
                reports[index] = entry.Report;
            }
        }
        report = Combine(jobs, reports);
        return true;
    }

    public void Store(IEnumerable<ConversionRequest> requests, ConversionPreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(report);
        var jobs = requests.ToArray();
        if (jobs.Length == 1)
        {
            StoreSingle(jobs[0], CreateKey([jobs[0]]), report);
            return;
        }
        foreach (var request in jobs)
        {
            var fullPath = Path.GetFullPath(request.InputPath);
            var single = new ConversionPreflightReport(
                report.Books.Where(book => string.Equals(Path.GetFullPath(book.InputPath), fullPath, StringComparison.OrdinalIgnoreCase)).ToArray(),
                report.Issues.Where(issue => issue.InputPath is not null && string.Equals(Path.GetFullPath(issue.InputPath), fullPath, StringComparison.OrdinalIgnoreCase)).ToArray());
            StoreSingle(request, CreateKey([request]), single);
        }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    public static string CreateKey(IEnumerable<ConversionRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var jobs = requests.ToArray();
        var builder = new StringBuilder(JsonSerializer.Serialize(jobs));
        foreach (var path in ReferencedPaths(jobs).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            builder.Append('\n').Append(Path.GetFullPath(path));
            if (!File.Exists(path))
            {
                builder.Append("|missing");
                continue;
            }

            var info = new FileInfo(path);
            builder.Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static IEnumerable<string> ReferencedPaths(IEnumerable<ConversionRequest> requests)
    {
        foreach (var request in requests)
        {
            yield return request.InputPath;
            var options = request.Options;
            if (options is null) continue;
            if (!string.IsNullOrWhiteSpace(options.CoverImagePath)) yield return options.CoverImagePath;
            if (!string.IsNullOrWhiteSpace(options.Font.FontPath)) yield return options.Font.FontPath;
            if (!string.IsNullOrWhiteSpace(options.Mobi.KindleGenPath)) yield return options.Mobi.KindleGenPath;
            foreach (var illustration in options.Illustrations)
                if (!string.IsNullOrWhiteSpace(illustration.ImagePath)) yield return illustration.ImagePath;
        }
    }

    private void StoreSingle(ConversionRequest request, string key, ConversionPreflightReport report)
    {
        var inputPath = Path.GetFullPath(request.InputPath);
        lock (_gate)
        {
            foreach (var stale in _entries.Where(pair =>
                         string.Equals(pair.Value.InputPath, inputPath, StringComparison.OrdinalIgnoreCase)
                         && !string.Equals(pair.Key, key, StringComparison.Ordinal)).Select(pair => pair.Key).ToArray())
                _entries.Remove(stale);
            _entries[key] = new CacheEntry(inputPath, report, DateTime.UtcNow);
            while (_entries.Count > MaximumEntries)
            {
                var oldest = _entries.MinBy(pair => pair.Value.LastAccessUtc).Key;
                _entries.Remove(oldest);
            }
        }
    }

    private static ConversionPreflightReport Combine(
        IReadOnlyList<ConversionRequest> requests,
        IReadOnlyList<ConversionPreflightReport> reports)
    {
        if (reports.Count == 1) return reports[0];
        var books = reports.SelectMany(item => item.Books).ToList();
        var issues = reports.SelectMany(item => item.Issues).ToList();
        foreach (var duplicate in requests
                     .GroupBy(request => Path.GetFullPath(request.OutputPath), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(new ConversionPreflightIssue(
                null,
                PreflightSeverity.Error,
                "duplicate_output",
                $"多本小说将写入同一个输出文件：{duplicate.Key}",
                PreflightTargetKind.Output));
        }
        return new ConversionPreflightReport(books, issues);
    }

    private sealed class CacheEntry(string inputPath, ConversionPreflightReport report, DateTime lastAccessUtc)
    {
        public string InputPath { get; } = inputPath;
        public ConversionPreflightReport Report { get; } = report;
        public DateTime LastAccessUtc { get; set; } = lastAccessUtc;
    }
}
