using System.Collections.Concurrent;
using System.IO;
using EasyPub.Core;

namespace EasyPub.Desktop;

internal sealed record TextPreviewSnapshot(
    string PreviewText,
    string PreviewStats,
    int ChapterCandidateCount,
    string[] MeaningfulLines);

/// <summary>
/// Deduplicates TXT parsing across rapid navigation while invalidating entries
/// whenever the source file or chapter recognition rule changes.
/// </summary>
internal sealed class TextPreviewCache(int capacity = 24)
{
    private readonly ConcurrentDictionary<CacheKey, Lazy<Task<TextPreviewSnapshot>>> _entries = new();
    private readonly ConcurrentQueue<CacheKey> _insertionOrder = new();
    private readonly int _capacity = Math.Max(4, capacity);

    public async Task<TextPreviewSnapshot> GetAsync(
        string inputPath,
        string? chapterRegex,
        CancellationToken cancellationToken = default)
    {
        var key = CacheKey.Create(inputPath, chapterRegex);
        var lazy = _entries.GetOrAdd(key, candidate =>
        {
            _insertionOrder.Enqueue(candidate);
            Trim();
            return new Lazy<Task<TextPreviewSnapshot>>(
                () => LoadAsync(candidate),
                LazyThreadSafetyMode.ExecutionAndPublication);
        });

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _entries.TryRemove(new KeyValuePair<CacheKey, Lazy<Task<TextPreviewSnapshot>>>(key, lazy));
            throw;
        }
    }

    private static async Task<TextPreviewSnapshot> LoadAsync(CacheKey key)
    {
        var document = await ChapterEditingDocument.LoadAsync(key.InputPath, key.ChapterRegex).ConfigureAwait(false);
        var lines = document.GetLines();
        return new TextPreviewSnapshot(
            string.Join(Environment.NewLine, lines.Take(32).Select(line => $"{line.LineNumber,4}    {line.Text}")),
            $"{document.LineCount:N0} 行 / {lines.Sum(line => line.Text.Length):N0} 字",
            document.Candidates.Count,
            lines.Select(line => line.Text.Trim())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Take(4096)
                .ToArray());
    }

    private void Trim()
    {
        while (_entries.Count > _capacity && _insertionOrder.TryDequeue(out var oldest))
            _entries.TryRemove(oldest, out _);
    }

    private sealed record CacheKey(
        string InputPath,
        long Length,
        long LastWriteUtcTicks,
        string? ChapterRegex)
    {
        public static CacheKey Create(string inputPath, string? chapterRegex)
        {
            var fullPath = Path.GetFullPath(inputPath);
            var info = new FileInfo(fullPath);
            info.Refresh();
            if (!info.Exists) throw new FileNotFoundException("找不到正文文件。", fullPath);
            return new CacheKey(
                fullPath,
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                string.IsNullOrWhiteSpace(chapterRegex) ? null : chapterRegex);
        }
    }
}
