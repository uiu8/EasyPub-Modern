using System.Text.Json;

namespace EasyPub.Core;

public sealed record ConversionHistoryEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    string InputPath,
    string OutputPath,
    bool Succeeded,
    int? ChapterCount,
    long? OutputBytes,
    long? ElapsedMilliseconds,
    string? ErrorMessage);

public sealed class ConversionHistoryStore
{
    private const int MaximumEntries = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ConversionHistoryStore(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        StoragePath = Path.GetFullPath(storagePath);
    }

    public string StoragePath { get; }

    public static ConversionHistoryStore CreateDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("EASYPUB_HISTORY_PATH");
        return new ConversionHistoryStore(string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPub Modern",
                "conversion-history.json")
            : overridePath);
    }

    public async Task<IReadOnlyList<ConversionHistoryEntry>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ConversionHistoryEntry>> AppendAsync(
        IEnumerable<ConversionHistoryEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var combined = (await LoadCoreAsync(cancellationToken))
                .Concat(entries)
                .GroupBy(entry => entry.Id)
                .Select(group => group.Last())
                .OrderByDescending(entry => entry.Timestamp)
                .Take(MaximumEntries)
                .ToArray();
            await SaveCoreAsync(combined, cancellationToken);
            return combined;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<ConversionHistoryEntry>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StoragePath)) return [];
        try
        {
            await using var stream = File.OpenRead(StoragePath);
            var entries = await JsonSerializer.DeserializeAsync<ConversionHistoryEntry[]>(
                stream, JsonOptions, cancellationToken);
            return (entries ?? []).OrderByDescending(entry => entry.Timestamp).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveCoreAsync(
        IReadOnlyList<ConversionHistoryEntry> entries,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(StoragePath)!;
        Directory.CreateDirectory(parent);
        var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(StoragePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, StoragePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
