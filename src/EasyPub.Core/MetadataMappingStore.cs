using System.Text.Json;

namespace EasyPub.Core;

public sealed class MetadataMappingStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public MetadataMappingStore(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        StoragePath = Path.GetFullPath(storagePath);
    }

    public string StoragePath { get; }

    public static MetadataMappingStore CreateDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("EASYPUB_METADATA_MAPPINGS_PATH");
        return new MetadataMappingStore(string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPub Modern",
                "metadata-mappings.json")
            : overridePath);
    }

    public async Task<IReadOnlyList<FolderMetadataRule>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(StoragePath)) return [];
            try
            {
                await using var stream = File.OpenRead(StoragePath);
                var rules = await JsonSerializer.DeserializeAsync<FolderMetadataRule[]>(
                    stream, JsonOptions, cancellationToken).ConfigureAwait(false);
                return Normalize(rules ?? []);
            }
            catch (JsonException)
            {
                return [];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IEnumerable<FolderMetadataRule> rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var normalized = Normalize(rules);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
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
                    await JsonSerializer.SerializeAsync(
                        stream, normalized, JsonOptions, cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporaryPath, StoragePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static IReadOnlyList<FolderMetadataRule> Normalize(IEnumerable<FolderMetadataRule> rules) =>
        rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.FolderPath) && rule.Metadata is not null)
            .Select(rule => rule with
            {
                FolderPath = MetadataMappingResolver.NormalizeFolder(rule.FolderPath),
                Metadata = NormalizeMetadata(rule.Metadata),
            })
            .GroupBy(rule => rule.FolderPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(rule => rule.FolderPath, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static BookMetadataOverrides NormalizeMetadata(BookMetadataOverrides metadata) => metadata with
    {
        Author = NormalizeOptional(metadata.Author),
        Translator = NormalizeOptional(metadata.Translator),
        Isbn = NormalizeOptional(metadata.Isbn),
        Publisher = NormalizeOptional(metadata.Publisher),
        Category = NormalizeOptional(metadata.Category),
        Language = NormalizeOptional(metadata.Language),
        Description = NormalizeOptional(metadata.Description),
        CustomMetadata = CalibreCustomMetadata.NormalizeAll(metadata.CustomMetadata),
    };

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
