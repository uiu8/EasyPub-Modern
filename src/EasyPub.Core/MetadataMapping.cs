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

public sealed record MetadataMappingPreview(
    string InputPath,
    string BookName,
    FolderMetadataRule? MatchedRule,
    int CandidateCount,
    string AppliedValues,
    string Resolution)
{
    public bool HasOverlap => CandidateCount > 1;
}

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

    public static IReadOnlyList<MetadataMappingPreview> Preview(
        IEnumerable<string> inputPaths,
        IEnumerable<FolderMetadataRule> rules)
    {
        ArgumentNullException.ThrowIfNull(inputPaths);
        ArgumentNullException.ThrowIfNull(rules);
        var normalizedRules = rules
            .Where(rule => !string.IsNullOrWhiteSpace(rule.FolderPath))
            .Select(rule => rule with { FolderPath = NormalizeFolder(rule.FolderPath) })
            .ToArray();

        return inputPaths.Select(path =>
        {
            var fullPath = Path.GetFullPath(path);
            var candidates = normalizedRules
                .Where(rule => ContainsFile(rule.FolderPath, fullPath))
                .OrderByDescending(rule => rule.FolderPath.Length)
                .ToArray();
            var winner = candidates.FirstOrDefault();
            return new MetadataMappingPreview(
                fullPath,
                Path.GetFileName(fullPath),
                winner,
                candidates.Length,
                winner is null ? "—" : Describe(winner.Metadata),
                winner is null
                    ? "未命中规则"
                    : candidates.Length == 1
                        ? "命中 1 条规则"
                        : $"命中 {candidates.Length} 条，已采用最具体的子文件夹规则");
        }).ToArray();
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

    private static string Describe(BookMetadataOverrides metadata)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(metadata.Author)) values.Add($"作者={metadata.Author}");
        if (!string.IsNullOrWhiteSpace(metadata.Publisher)) values.Add($"出版社={metadata.Publisher}");
        if (!string.IsNullOrWhiteSpace(metadata.Category)) values.Add($"类别={metadata.Category}");
        if (!string.IsNullOrWhiteSpace(metadata.Language)) values.Add($"语言={metadata.Language}");
        if (!string.IsNullOrWhiteSpace(metadata.Isbn)) values.Add($"ISBN={metadata.Isbn}");
        if (metadata.PublicationDate is not null) values.Add($"日期={metadata.PublicationDate:yyyy-MM-dd}");
        return values.Count == 0 ? "其他标准书籍信息" : string.Join(" · ", values);
    }
}
