using System.Text.Json;

namespace EasyPub.Core;

public sealed record ConversionProfile(
    string OutputFormat,
    string? Author,
    int Parallelism,
    string? AdditionalCssFilePath,
    ConversionOptions Options)
{
    public static ConversionProfile Default { get; } = new(
        "epub", null, 1, null, ConversionOptions.LegacyDefault);

    public int FontSizePercent => Options.FontSizePercent;
    public MobiCompression MobiCompression => Options.Mobi.Compression;
    public ConversionMode Mode { get; init; } = ConversionMode.OriginalCompatible;
}

public enum ConversionMode
{
    OriginalCompatible,
    ModernLayout,
    Custom,
}

public sealed record NamedConversionPreset(string Name, ConversionProfile Profile);

public sealed record EasyPubAppSettings(
    string? OutputDirectory,
    ConversionProfile LastProfile,
    IReadOnlyList<NamedConversionPreset> Presets)
{
    public bool UseLegacyConfig { get; init; } = true;
    public string? LegacyConfigPath { get; init; }

    public static EasyPubAppSettings Default { get; } = new(null, ConversionProfile.Default, []);
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettingsStore(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        StoragePath = Path.GetFullPath(storagePath);
    }

    public string StoragePath { get; }

    public static AppSettingsStore CreateDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH");
        return new AppSettingsStore(string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPub Modern",
                "app-settings.json")
            : overridePath);
    }

    public async Task<EasyPubAppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(StoragePath)) return EasyPubAppSettings.Default;
            try
            {
                await using var stream = File.OpenRead(StoragePath);
                return await JsonSerializer.DeserializeAsync<EasyPubAppSettings>(stream, JsonOptions, cancellationToken)
                    ?? EasyPubAppSettings.Default;
            }
            catch (JsonException)
            {
                return EasyPubAppSettings.Default;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(EasyPubAppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var parent = Path.GetDirectoryName(StoragePath)!;
            Directory.CreateDirectory(parent);
            var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(StoragePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4096,
                           FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
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
}
