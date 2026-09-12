namespace EasyPub.Core;

/// <summary>
/// Shares an in-flight chapter parse between the workbench preview and automatic
/// preflight. A TXT is immutable for a cache entry; file stamps and recognition
/// settings are part of the key, so changing the source or rules never reuses a
/// stale document.
/// </summary>
public sealed class ChapterTreeDocumentCache
{
    private const int MaximumEntries = 4;
    private readonly object _gate = new();
    private readonly Dictionary<ChapterDocumentCacheKey, Task<ChapterTreeDocument>> _entries = [];

    public Task<ChapterTreeDocument> GetOrLoadAsync(
        string sourcePath,
        string? chapterPattern,
        TocHierarchyOptions hierarchy,
        TextEncodingMode encoding,
        ChapterTreePlan? existingPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(hierarchy);

        var fullPath = Path.GetFullPath(sourcePath);
        var source = new FileInfo(fullPath);
        var key = new ChapterDocumentCacheKey(
            fullPath.ToUpperInvariant(),
            source.Exists ? source.Length : 0,
            source.Exists ? source.LastWriteTimeUtc.Ticks : 0,
            chapterPattern ?? string.Empty,
            hierarchy.Enabled,
            hierarchy.Level1Pattern,
            hierarchy.Level2Pattern,
            hierarchy.Level3Pattern,
            hierarchy.RecognizeNumericHeadings,
            hierarchy.NumericHeadingMinimumBodyLines,
            hierarchy.NumericHeadingPattern,
            encoding,
            existingPlan?.SourceSha256 ?? string.Empty,
            existingPlan?.Entries.Count ?? 0);

        Task<ChapterTreeDocument> task;
        lock (_gate)
        {
            // A source edit invalidates every recognition variant for that path.
            foreach (var staleKey in _entries.Keys
                         .Where(candidate => candidate.SourcePath == key.SourcePath && candidate != key)
                         .ToArray())
                _entries.Remove(staleKey);

            if (!_entries.TryGetValue(key, out task!))
            {
                task = Task.Run(() => ChapterTreeDocument.LoadAsync(
                    fullPath,
                    chapterPattern,
                    hierarchy,
                    encoding,
                    existingPlan,
                    CancellationToken.None));
                _entries[key] = task;
                while (_entries.Count > MaximumEntries)
                {
                    var oldestOtherKey = _entries.Keys.First(candidate => candidate != key);
                    _entries.Remove(oldestOtherKey);
                }
            }
        }

        return AwaitCachedAsync(key, task, cancellationToken);
    }

    public void Invalidate(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath).ToUpperInvariant();
        lock (_gate)
            foreach (var key in _entries.Keys.Where(candidate => candidate.SourcePath == fullPath).ToArray())
                _entries.Remove(key);
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    private async Task<ChapterTreeDocument> AwaitCachedAsync(
        ChapterDocumentCacheKey key,
        Task<ChapterTreeDocument> task,
        CancellationToken cancellationToken)
    {
        try
        {
            // The caller may cancel its wait without cancelling a parse that another
            // view is already using.
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (task.IsCanceled || task.IsFaulted)
            {
                lock (_gate)
                    if (_entries.TryGetValue(key, out var cached) && ReferenceEquals(cached, task))
                        _entries.Remove(key);
            }
            throw;
        }
    }

    private sealed record ChapterDocumentCacheKey(
        string SourcePath,
        long SourceLength,
        long SourceLastWriteUtcTicks,
        string ChapterPattern,
        bool HierarchyEnabled,
        string Level1Pattern,
        string Level2Pattern,
        string Level3Pattern,
        bool RecognizeNumericHeadings,
        int NumericHeadingMinimumBodyLines,
        string NumericHeadingPattern,
        TextEncodingMode Encoding,
        string PlanSourceSha256,
        int PlanEntryCount);
}
