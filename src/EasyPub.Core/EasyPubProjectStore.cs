using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EasyPub.Core;

public sealed record EasyPubProjectBook(
    string InputPath,
    string? Title,
    string? Author,
    string? CoverImagePath,
    IReadOnlyList<BookIllustration> Illustrations);

public sealed record EasyPubProjectDocument(
    int SchemaVersion,
    string? ProjectPathHint,
    string? OutputDirectory,
    ConversionProfile Profile,
    IReadOnlyList<EasyPubProjectBook> Books,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;
}

public sealed class EasyPubProjectStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public EasyPubProjectStore(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        StoragePath = Path.GetFullPath(storagePath);
    }

    public string StoragePath { get; }

    public static EasyPubProjectStore CreateRecoveryDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("EASYPUB_RECOVERY_PATH");
        return new EasyPubProjectStore(string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPub Modern",
                "recovery.easypubproj")
            : overridePath);
    }

    public async Task<EasyPubProjectDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var stream = File.OpenRead(StoragePath);
            var document = await JsonSerializer.DeserializeAsync<EasyPubProjectDocument>(
                stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("项目文件为空或格式无效。");
            if (document.SchemaVersion < 1 || document.SchemaVersion > EasyPubProjectDocument.CurrentSchemaVersion)
                throw new InvalidDataException($"不支持的项目文件版本：{document.SchemaVersion}。");
            return Normalize(document);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("项目文件不是有效的 EasyPub 项目。", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(EasyPubProjectDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
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
                                 16 * 1024,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                        stream,
                        Normalize(document),
                        JsonOptions,
                        cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    public void Delete()
    {
        if (File.Exists(StoragePath)) File.Delete(StoragePath);
    }

    public static string Fingerprint(EasyPubProjectDocument document)
    {
        var stable = document with { UpdatedAt = default };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stable, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static EasyPubProjectDocument Normalize(EasyPubProjectDocument document) => document with
    {
        ProjectPathHint = NormalizeOptionalPath(document.ProjectPathHint),
        OutputDirectory = NormalizeOptionalPath(document.OutputDirectory),
        Profile = document.Profile ?? ConversionProfile.Default,
        Books = (document.Books ?? [])
            .Where(book => !string.IsNullOrWhiteSpace(book.InputPath))
            .Select(book => book with
            {
                InputPath = Path.GetFullPath(book.InputPath),
                CoverImagePath = NormalizeOptionalPath(book.CoverImagePath),
                Illustrations = book.Illustrations ?? [],
            })
            .ToArray(),
    };

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
}
