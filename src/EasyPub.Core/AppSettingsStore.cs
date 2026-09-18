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
        "epub", null, 1, null, new ConversionOptions());

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
    public string? KindleGenPath { get; init; }
    public string TextEditorPath { get; init; } = "notepad.exe";
    public TocHierarchyOptions NumericHeadingDefaults { get; init; } = new();
    public IReadOnlyList<NamedNumericHeadingPreset> NumericHeadingPresets { get; init; } = [];
    public bool AutoOpenTaskCenter { get; init; }
    public AutomaticCheckOptions AutomaticChecks { get; init; } = new();
    public bool AutoOpenOutputDirectory { get; init; }

    /// <summary>启动时后台检查新版本。只提示，不自动下载，默认开启。</summary>
    public bool AutoCheckUpdate { get; init; } = true;

    /// <summary>
    /// Where原文备份 live. null means the default: a folder under local app data selected by
    /// <see cref="SourceBackupLayout.DefaultRoot"/>. A reader who points this at their own folder is
    /// telling us where they will look for backups, so the UI must show the resolved path rather than
    /// assume it.
    /// </summary>
    public string? SourceBackupRoot { get; init; }

    /// <summary>Repair-time snapshots kept per book. The first-seen original is never counted.</summary>
    public int SourceBackupRetentionLimit { get; init; } = SourceBackupRetention.DefaultSnapshotLimit;

    /// <summary>
    /// Which landing mode the repair confirmation window opens on.
    ///
    /// <para>A setting rather than a constant because it is a preference about how much the program may do
    /// on its own, and reasonable readers differ: one wants the TXT fixed in the same pass, another wants
    /// every write to the file to be asked for. The window still shows both options and still says what the
    /// chosen one will change, so this only decides where the cursor starts.</para>
    /// </summary>
    public RepairLandingMode DefaultRepairLandingMode { get; init; } = RepairLandingMode.EditSource;

    public OutputCollisionPolicy OutputCollisionPolicy { get; init; } = OutputCollisionPolicy.AutoRename;
    public string KindlePreviewDeviceId { get; init; } = "kpw6";
    public int CustomKindleWidth { get; init; } = 1264;
    public int CustomKindleHeight { get; init; } = 1680;
    public int CustomKindlePpi { get; init; } = 300;
    public IReadOnlyDictionary<string, string> ShortcutBindings { get; init; } = new Dictionary<string, string>();
    public string Theme { get; init; } = "Light";
    public string UiDensity { get; init; } = "Comfortable";
    public int UiScalePercent { get; init; } = 100;
    public bool RememberWindowPlacement { get; init; } = true;
    public bool ReduceMotion { get; init; }
    public double? WindowLeft { get; init; }
    public double? WindowTop { get; init; }
    public double? WindowWidth { get; init; }
    public double? WindowHeight { get; init; }
    public string WindowState { get; init; } = "Normal";

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

    /// <summary>
    /// Synchronous read, for the few places that need settings before an await is possible — backup
    /// paths in particular, which are resolved inside synchronous Core code. The file is small and
    /// these calls are rare, so this is a plain read rather than a cache; a reader who changes the
    /// backup folder expects the next write to use it immediately.
    /// </summary>
    public EasyPubAppSettings Load()
    {
        if (!File.Exists(StoragePath)) return EasyPubAppSettings.Default;
        try
        {
            return UpgradeSupersededPatterns(
                JsonSerializer.Deserialize<EasyPubAppSettings>(File.ReadAllText(StoragePath), JsonOptions)
                ?? EasyPubAppSettings.Default);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return EasyPubAppSettings.Default;
        }
    }

    /// <summary>
    /// 把设置里**等于旧版默认值**的识别正则升到当前默认值，并记一条日志。
    ///
    /// <para>没有这一步，改进过的正则传不到老用户身上：设置文件里存着当年写入的默认值，
    /// 它不为空，所以永远压着代码里的新默认值。实测同一本样书在旧设置下识别出 560 个条目、
    /// 默认设置下 468 个 —— 而界面上没有任何地方提示这件事。</para>
    ///
    /// <para>换掉的时候写日志，免得"我的设置怎么变了"变成另一个谜。</para>
    /// </summary>
    private static EasyPubAppSettings UpgradeSupersededPatterns(EasyPubAppSettings settings)
    {
        var upgraded = settings.NumericHeadingDefaults.UpgradeSupersededPatterns(out var changed);
        if (!changed) return settings;
        InteractionLog.Decision("升级旧识别规则", new
        {
            原Level1 = settings.NumericHeadingDefaults.Level1Pattern,
            原Level2 = settings.NumericHeadingDefaults.Level2Pattern,
            新Level1 = upgraded.Level1Pattern,
            新Level2 = upgraded.Level2Pattern,
        });
        return settings with { NumericHeadingDefaults = upgraded };
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
                return UpgradeSupersededPatterns(
                    await JsonSerializer.DeserializeAsync<EasyPubAppSettings>(stream, JsonOptions, cancellationToken)
                    ?? EasyPubAppSettings.Default);
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
