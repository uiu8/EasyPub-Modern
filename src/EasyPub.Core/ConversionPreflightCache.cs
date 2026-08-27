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
    private string? _key;
    private ConversionPreflightReport? _report;

    public async Task<(ConversionPreflightReport Report, bool Reused)> InspectAsync(
        IEnumerable<ConversionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var jobs = requests.ToArray();
        var key = CreateKey(jobs);
        if (_report is not null && string.Equals(_key, key, StringComparison.Ordinal))
            return (_report, true);

        var report = await Task.Run(
            () => new ConversionPreflightInspector().InspectAsync(jobs, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        _key = key;
        _report = report;
        return (report, false);
    }

    public bool TryGet(IEnumerable<ConversionRequest> requests, out ConversionPreflightReport report)
    {
        var key = CreateKey(requests);
        if (_report is not null && string.Equals(_key, key, StringComparison.Ordinal))
        {
            report = _report;
            return true;
        }

        report = null!;
        return false;
    }

    public void Store(IEnumerable<ConversionRequest> requests, ConversionPreflightReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        _key = CreateKey(requests);
        _report = report;
    }

    public void Clear()
    {
        _key = null;
        _report = null;
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
}
