using System.Text.RegularExpressions;

namespace EasyPub.Core;

internal sealed record LegacyChapter(
    string Title,
    IReadOnlyList<string> Paragraphs,
    int TocLevel = 2,
    bool IncludeInToc = true,
    int? HeadingLevel = null);

internal static partial class LegacyTextParser
{
    internal const string PositionedIllustrationPrefix = "\u001eEasyPubIllustration:";

    public static async Task<IReadOnlyList<LegacyChapter>> ParseAsync(
        string inputPath,
        ConversionOptions options,
        ChapterTreePlan? chapterTree,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        var text = TextFileDecoder.Decode(bytes, options.TextEncoding).Text;
        var cleanup = TextCleanupPipeline.Apply(text, options.TextCleanup);
        var sourceLines = cleanup.Lines;
        if (chapterTree is not null)
            return ParseUsingChapterTree(bytes, sourceLines, options, chapterTree);

        var hierarchy = options.TocHierarchy ?? new TocHierarchyOptions();
        var chapters = new List<MutableChapter> { new("序", hierarchy.Enabled ? 1 : 2) };
        var chapterRegex = string.IsNullOrWhiteSpace(options.ChapterPattern)
            ? ChapterRegex()
            : new Regex(options.ChapterPattern, RegexOptions.Compiled);
        var hierarchyRegexes = hierarchy.Enabled
            ? CreateHierarchyRegexes(hierarchy)
            : [];

        var positionedIllustrations = options.Illustrations
            .Where(illustration => illustration.InsertAfterLine.HasValue)
            .GroupBy(illustration => illustration.InsertAfterLine!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var lineNumber in positionedIllustrations.Keys)
        {
            if (lineNumber < 1 || lineNumber > sourceLines.Count)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"插图位置第 {lineNumber} 行超出 TXT 的 1–{sourceLines.Count} 行范围。");
        }

        for (var index = 0; index < sourceLines.Count; index++)
        {
            var rawLine = sourceLines[index];
            if (rawLine == TextCleanupPipeline.RemovedLine) continue;
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (!options.RemoveBlankLines) chapters[^1].Paragraphs.Add(string.Empty);
            }
            else if (TryGetTocLevel(rawLine, hierarchyRegexes, out var tocLevel) || chapterRegex.IsMatch(rawLine))
                chapters.Add(new MutableChapter(line, tocLevel == 0 ? 2 : tocLevel));
            else
                chapters[^1].Paragraphs.Add(line);

            if (positionedIllustrations.TryGetValue(index + 1, out var illustrations))
            {
                foreach (var illustration in illustrations)
                    chapters[^1].Paragraphs.Add(PositionedIllustrationPrefix + illustration.Marker.Trim());
            }
        }

        return chapters
            .Select(chapter => new LegacyChapter(chapter.Title, chapter.Paragraphs.ToArray(), chapter.TocLevel))
            .ToArray();
    }

    private static IReadOnlyList<LegacyChapter> ParseUsingChapterTree(
        byte[] sourceBytes,
        IReadOnlyList<string> sourceLines,
        ConversionOptions options,
        ChapterTreePlan chapterTree)
    {
        var sourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceBytes));
        if (!string.Equals(sourceHash, chapterTree.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("TXT 内容已发生变化，章节树已失效，请重新打开章节树并保存。");
        ChapterTreeDocument.ValidatePlan(chapterTree, sourceLines.Count);

        var positionedIllustrations = options.Illustrations
            .Where(illustration => illustration.InsertAfterLine.HasValue)
            .GroupBy(illustration => illustration.InsertAfterLine!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var result = new List<LegacyChapter>(chapterTree.Entries.Count);
        foreach (var entry in chapterTree.Entries)
        {
            var paragraphs = new List<string>();
            if (entry.TitleLineNumber is int titleLine)
                AddPositionedIllustrations(paragraphs, positionedIllustrations, titleLine);
            foreach (var range in entry.ContentRanges ?? [])
            {
                for (var lineNumber = range.StartLine; lineNumber <= range.EndLine; lineNumber++)
                {
                    var line = sourceLines[lineNumber - 1].Trim();
                    if (line == TextCleanupPipeline.RemovedLine) continue;
                    if (line.Length > 0 || !options.RemoveBlankLines) paragraphs.Add(line);
                    AddPositionedIllustrations(paragraphs, positionedIllustrations, lineNumber);
                }
            }
            result.Add(new LegacyChapter(
                entry.Title.Trim(),
                paragraphs,
                entry.IsFrontMatter ? 2 : Math.Clamp(entry.Level, 1, 4),
                entry.IncludeInToc,
                entry.HeadingLevel is >= 1 and <= 4 ? entry.HeadingLevel : null));
        }
        return result;
    }

    private static void AddPositionedIllustrations(
        ICollection<string> paragraphs,
        IReadOnlyDictionary<int, BookIllustration[]> positionedIllustrations,
        int lineNumber)
    {
        if (!positionedIllustrations.TryGetValue(lineNumber, out var illustrations)) return;
        foreach (var illustration in illustrations)
            paragraphs.Add(PositionedIllustrationPrefix + illustration.Marker.Trim());
    }

    private static Regex[] CreateHierarchyRegexes(TocHierarchyOptions hierarchy) =>
    [
        new Regex(NormalizePattern(hierarchy.Level1Pattern, TocHierarchyOptions.DefaultLevel1Pattern), RegexOptions.Compiled),
        new Regex(NormalizePattern(hierarchy.Level2Pattern, TocHierarchyOptions.DefaultLevel2Pattern), RegexOptions.Compiled),
        new Regex(NormalizePattern(hierarchy.Level3Pattern, TocHierarchyOptions.DefaultLevel3Pattern), RegexOptions.Compiled),
    ];

    private static bool TryGetTocLevel(string line, IReadOnlyList<Regex> regexes, out int level)
    {
        for (var index = 0; index < regexes.Count; index++)
        {
            if (!regexes[index].IsMatch(line)) continue;
            level = index + 1;
            return true;
        }
        level = 0;
        return false;
    }

    private static string NormalizePattern(string? pattern, string fallback) =>
        string.IsNullOrWhiteSpace(pattern) ? fallback : pattern;

    [GeneratedRegex(ChapterEditingDocument.DefaultChapterPattern)]
    private static partial Regex ChapterRegex();

    private sealed record MutableChapter(string Title, int TocLevel)
    {
        public List<string> Paragraphs { get; } = [];
    }
}
