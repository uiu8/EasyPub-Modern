using System.Text;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

internal sealed record LegacyChapter(string Title, IReadOnlyList<string> Paragraphs, int TocLevel = 2);

internal static partial class LegacyTextParser
{
    internal const string PositionedIllustrationPrefix = "\u001eEasyPubIllustration:";

    public static async Task<IReadOnlyList<LegacyChapter>> ParseAsync(
        string inputPath,
        ConversionOptions options,
        CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(inputPath, cancellationToken);
        var text = DetectEncoding(bytes, options.TextEncoding).GetString(RemovePreamble(bytes));
        var hierarchy = options.TocHierarchy ?? new TocHierarchyOptions();
        var chapters = new List<MutableChapter> { new("序", hierarchy.Enabled ? 1 : 2) };
        var chapterRegex = string.IsNullOrWhiteSpace(options.ChapterPattern)
            ? ChapterRegex()
            : new Regex(options.ChapterPattern, RegexOptions.Compiled);
        var hierarchyRegexes = hierarchy.Enabled
            ? CreateHierarchyRegexes(hierarchy)
            : [];

        var sourceLines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var positionedIllustrations = options.Illustrations
            .Where(illustration => illustration.InsertAfterLine.HasValue)
            .GroupBy(illustration => illustration.InsertAfterLine!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (var lineNumber in positionedIllustrations.Keys)
        {
            if (lineNumber < 1 || lineNumber > sourceLines.Length)
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    $"插图位置第 {lineNumber} 行超出 TXT 的 1–{sourceLines.Length} 行范围。");
        }

        for (var index = 0; index < sourceLines.Length; index++)
        {
            var rawLine = sourceLines[index];
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

    private static Encoding DetectEncoding(byte[] bytes, TextEncodingMode mode)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (mode == TextEncodingMode.Utf8) return new UTF8Encoding(false, true);
        if (mode == TextEncodingMode.Gbk) return Encoding.GetEncoding(936);
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) return Encoding.UTF8;
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.Preamble)) return Encoding.Unicode;
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble)) return Encoding.BigEndianUnicode;
        if (bytes.AsSpan().StartsWith(Encoding.UTF32.Preamble)) return Encoding.UTF32;

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(936);
        }
    }

    private static byte[] RemovePreamble(byte[] bytes)
    {
        foreach (var encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode, Encoding.UTF32 })
        {
            var preamble = encoding.Preamble;
            if (bytes.AsSpan().StartsWith(preamble)) return bytes[preamble.Length..];
        }

        return bytes;
    }

    [GeneratedRegex(ChapterEditingDocument.DefaultChapterPattern)]
    private static partial Regex ChapterRegex();

    private sealed record MutableChapter(string Title, int TocLevel)
    {
        public List<string> Paragraphs { get; } = [];
    }
}
