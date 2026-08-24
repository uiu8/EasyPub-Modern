using System.Text.Json.Serialization;

namespace EasyPub.Core;

public sealed record BookMetadataOverrides
{
    public string? Author { get; init; }
    public string? Translator { get; init; }
    public string? Isbn { get; init; }
    public DateOnly? PublicationDate { get; init; }
    public string? Publisher { get; init; }
    public string? Category { get; init; }
    public string? Language { get; init; }
    public string? Description { get; init; }

    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Author) &&
        string.IsNullOrWhiteSpace(Translator) &&
        string.IsNullOrWhiteSpace(Isbn) &&
        PublicationDate is null &&
        string.IsNullOrWhiteSpace(Publisher) &&
        string.IsNullOrWhiteSpace(Category) &&
        string.IsNullOrWhiteSpace(Language) &&
        string.IsNullOrWhiteSpace(Description);
}

public sealed record FolderMetadataRule(string FolderPath, BookMetadataOverrides Metadata);

public static class MetadataMappingResolver
{
    public static FolderMetadataRule? Match(
        string inputPath,
        IEnumerable<FolderMetadataRule> rules)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentNullException.ThrowIfNull(rules);
        var fullInputPath = Path.GetFullPath(inputPath);

        return rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.FolderPath))
            .Select(rule => rule with { FolderPath = NormalizeFolder(rule.FolderPath) })
            .Where(rule => ContainsFile(rule.FolderPath, fullInputPath))
            .OrderByDescending(rule => rule.FolderPath.Length)
            .FirstOrDefault();
    }

    public static PublicationMetadata Apply(
        PublicationMetadata baseMetadata,
        BookMetadataOverrides? overrides)
    {
        ArgumentNullException.ThrowIfNull(baseMetadata);
        overrides ??= new BookMetadataOverrides();
        return baseMetadata with
        {
            Translator = Prefer(overrides.Translator, baseMetadata.Translator),
            Isbn = Prefer(overrides.Isbn, baseMetadata.Isbn),
            PublicationDate = overrides.PublicationDate ?? baseMetadata.PublicationDate,
            Publisher = Prefer(overrides.Publisher, baseMetadata.Publisher),
            Category = Prefer(overrides.Category, baseMetadata.Category),
            Language = Prefer(overrides.Language, baseMetadata.Language) ?? "zh-CN",
            Description = Prefer(overrides.Description, baseMetadata.Description),
        };
    }

    public static string NormalizeFolder(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath.Trim()));
    }

    private static bool ContainsFile(string folderPath, string inputPath)
    {
        var relative = Path.GetRelativePath(folderPath, inputPath);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string? Prefer(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
}
