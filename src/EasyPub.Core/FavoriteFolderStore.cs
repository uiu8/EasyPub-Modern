using System.Text.Json;

namespace EasyPub.Core;

public sealed class FavoriteFolderStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FavoriteFolderStore(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        StoragePath = Path.GetFullPath(storagePath);
    }

    public string StoragePath { get; }

    public static FavoriteFolderStore CreateDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("EASYPUB_FAVORITES_PATH");
        return new FavoriteFolderStore(string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPub Modern",
                "favorite-folders.json")
            : overridePath);
    }

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken cancellationToken = default)
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

    public async Task<IReadOnlyList<string>> AddAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(folderPath);
        if (!Directory.Exists(normalized))
            throw new DirectoryNotFoundException($"收藏文件夹不存在：{normalized}");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var folders = (await LoadCoreAsync(cancellationToken)).ToList();
            if (!folders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(normalized);
                await SaveCoreAsync(folders, cancellationToken);
            }
            return folders;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> RemoveAsync(
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(folderPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var folders = (await LoadCoreAsync(cancellationToken)).ToList();
            var removed = folders.RemoveAll(path =>
                string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) await SaveCoreAsync(folders, cancellationToken);
            return folders;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StoragePath)) return [];

        try
        {
            await using var stream = File.OpenRead(StoragePath);
            var folders = await JsonSerializer.DeserializeAsync<string[]>(stream, JsonOptions, cancellationToken);
            return (folders ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task SaveCoreAsync(IReadOnlyList<string> folders, CancellationToken cancellationToken)
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
                await JsonSerializer.SerializeAsync(stream, folders, JsonOptions, cancellationToken);
            }
            File.Move(temporaryPath, StoragePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static string Normalize(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim()));
    }
}
