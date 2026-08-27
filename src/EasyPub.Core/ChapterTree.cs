using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record ChapterSourceRange(int StartLine, int EndLine);

public sealed record ChapterTreeEntry(
    string Id,
    string Title,
    int Level,
    bool IncludeInToc,
    int? TitleLineNumber,
    IReadOnlyList<ChapterSourceRange> ContentRanges)
{
    public bool IsFrontMatter { get; init; }
    public int HeadingLevel { get; init; }
}

public sealed record ChapterTreePlan(
    string SourceSha256,
    IReadOnlyList<ChapterTreeEntry> Entries);

public sealed record ChapterTreeSourceLine(int LineNumber, string Text);

public sealed class ChapterTreeDocument
{
    private readonly IReadOnlyList<ChapterTreeSourceLine> _sourceLines;

    private ChapterTreeDocument(
        string sourcePath,
        string sourceSha256,
        IReadOnlyList<ChapterTreeSourceLine> sourceLines,
        IReadOnlyList<ChapterTreeEntry> entries)
    {
        SourcePath = sourcePath;
        SourceSha256 = sourceSha256;
        _sourceLines = sourceLines;
        Entries = entries;
    }

    public string SourcePath { get; }
    public string SourceSha256 { get; }
    public int LineCount => _sourceLines.Count;
    public IReadOnlyList<ChapterTreeEntry> Entries { get; }

    public static async Task<ChapterTreeDocument> LoadAsync(
        string sourcePath,
        string? chapterPattern = null,
        TocHierarchyOptions? hierarchy = null,
        TextEncodingMode encodingMode = TextEncodingMode.Auto,
        ChapterTreePlan? existingPlan = null,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        if (!string.Equals(Path.GetExtension(fullPath), ".txt", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("章节树目前用于 TXT 书稿；EPUB 的目录会在导入时自动读取。");

        var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        var sourceHash = Convert.ToHexString(SHA256.HashData(bytes));
        var editingDocument = ChapterEditingDocument.FromBytes(
            fullPath, bytes, chapterPattern, encodingMode, cancellationToken);
        var sourceLines = editingDocument.GetLines()
            .Select(line => new ChapterTreeSourceLine(line.LineNumber, line.Text))
            .ToArray();

        if (existingPlan is not null)
        {
            if (!string.Equals(existingPlan.SourceSha256, sourceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("TXT 内容已发生变化，已保存的章节树不能继续套用，请重新识别。");
            existingPlan = NormalizePersistedPlan(existingPlan);
            ValidatePlan(existingPlan, sourceLines.Length);
            return new ChapterTreeDocument(fullPath, sourceHash, sourceLines, existingPlan.Entries);
        }

        var hierarchyOptions = hierarchy ?? new TocHierarchyOptions();
        var levelPatterns = hierarchyOptions.Enabled
            ? new[]
            {
                CompilePattern(hierarchyOptions.Level1Pattern, TocHierarchyOptions.DefaultLevel1Pattern),
                CompilePattern(hierarchyOptions.Level2Pattern, TocHierarchyOptions.DefaultLevel2Pattern),
                CompilePattern(hierarchyOptions.Level3Pattern, TocHierarchyOptions.DefaultLevel3Pattern),
            }
            : [];
        var candidates = editingDocument.Candidates.ToDictionary(candidate => candidate.LineNumber);
        var headings = new List<(int LineNumber, string Title, int Level)>();
        foreach (var line in sourceLines)
        {
            var level = MatchLevel(line.Text, levelPatterns);
            if (level == 0 && !candidates.TryGetValue(line.LineNumber, out var candidate)) continue;
            var suggested = candidates.TryGetValue(line.LineNumber, out candidate)
                ? candidate.OriginalTitle
                : line.Text.Trim();
            headings.Add((line.LineNumber, suggested, level == 0 ? 2 : level));
        }

        var entries = new List<ChapterTreeEntry>();
        var firstHeadingLine = headings.Count == 0 ? sourceLines.Length + 1 : headings[0].LineNumber;
        entries.Add(new ChapterTreeEntry(
            Guid.NewGuid().ToString("N"),
            "序",
            2,
            true,
            null,
            CreateRange(1, firstHeadingLine - 1))
        {
            IsFrontMatter = true,
            HeadingLevel = 2,
        });

        for (var index = 0; index < headings.Count; index++)
        {
            var heading = headings[index];
            var endLine = index + 1 < headings.Count ? headings[index + 1].LineNumber - 1 : sourceLines.Length;
            entries.Add(new ChapterTreeEntry(
                Guid.NewGuid().ToString("N"),
                heading.Title,
                heading.Level,
                true,
                heading.LineNumber,
                CreateRange(heading.LineNumber + 1, endLine))
            {
                HeadingLevel = heading.Level,
            });
        }

        entries = NormalizeHierarchyLevels(entries).ToList();
        var plan = new ChapterTreePlan(sourceHash, entries);
        ValidatePlan(plan, sourceLines.Length);
        return new ChapterTreeDocument(fullPath, sourceHash, sourceLines, entries);
    }

    public IReadOnlyList<ChapterTreeSourceLine> GetSourceLines(ChapterTreeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var result = new List<ChapterTreeSourceLine>();
        foreach (var range in entry.ContentRanges ?? [])
        {
            for (var line = range.StartLine; line <= range.EndLine; line++)
                result.Add(_sourceLines[line - 1]);
        }
        return result;
    }

    public ChapterTreePlan CreatePlan(IEnumerable<ChapterTreeEntry> entries)
    {
        var plan = new ChapterTreePlan(SourceSha256, entries.ToArray());
        ValidatePlan(plan, LineCount);
        return plan;
    }

    public static void ValidatePlan(ChapterTreePlan plan, int lineCount)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (string.IsNullOrWhiteSpace(plan.SourceSha256))
            throw new InvalidDataException("章节树缺少源文件校验值。");
        if (plan.Entries is null || plan.Entries.Count == 0)
            throw new InvalidDataException("章节树至少需要一个章节。");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var occupiedLines = new HashSet<int>();
        var previousTreeLevel = 0;
        foreach (var entry in plan.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !ids.Add(entry.Id))
                throw new InvalidDataException("章节树含有无效或重复的章节标识。");
            if (string.IsNullOrWhiteSpace(entry.Title) || entry.Title.Contains('\r') || entry.Title.Contains('\n'))
                throw new InvalidDataException("章节标题不能为空或包含换行。");
            if (entry.Level is < 1 or > 4)
                throw new InvalidDataException($"章节“{entry.Title}”的层级必须在 1–4 之间。");
            if (entry.IsFrontMatter && entry.Level != 2)
                throw new InvalidDataException($"前置章节“{entry.Title}”不参与层级，内部目录级别必须保持为 2。");
            if (entry.HeadingLevel is < 0 or > 4)
                throw new InvalidDataException($"章节“{entry.Title}”的正文标题样式级别无效。");
            if (entry.IsFrontMatter)
            {
                previousTreeLevel = 0;
            }
            else
            {
                if (previousTreeLevel == 0 && entry.Level != 1)
                    throw new InvalidDataException($"根章节“{entry.Title}”必须是 L1。");
                if (previousTreeLevel > 0 && entry.Level > previousTreeLevel + 1)
                    throw new InvalidDataException($"章节“{entry.Title}”跳过了中间目录层级。");
                previousTreeLevel = entry.Level;
            }
            if (entry.TitleLineNumber is < 1 || entry.TitleLineNumber > lineCount)
                throw new InvalidDataException($"章节“{entry.Title}”的标题行超出 TXT 范围。");
            foreach (var range in entry.ContentRanges ?? [])
            {
                if (range.StartLine < 1 || range.EndLine < range.StartLine || range.EndLine > lineCount)
                    throw new InvalidDataException($"章节“{entry.Title}”的正文范围超出 TXT 范围。");
                for (var line = range.StartLine; line <= range.EndLine; line++)
                {
                    if (!occupiedLines.Add(line))
                        throw new InvalidDataException($"TXT 第 {line} 行被分配给了多个章节。");
                }
            }
        }
    }

    private static IReadOnlyList<ChapterSourceRange> CreateRange(int startLine, int endLine) =>
        startLine <= endLine ? [new ChapterSourceRange(startLine, endLine)] : [];

    private static Regex CompilePattern(string? pattern, string fallback) =>
        new(string.IsNullOrWhiteSpace(pattern) ? fallback : pattern, RegexOptions.Compiled);

    private static int MatchLevel(string line, IReadOnlyList<Regex> patterns)
    {
        for (var index = 0; index < patterns.Count; index++)
        {
            if (patterns[index].IsMatch(line)) return index + 1;
        }
        return 0;
    }

    private static ChapterTreePlan NormalizeLegacyFrontMatter(ChapterTreePlan plan)
    {
        if (plan.Entries.Count == 0) return plan;
        var first = plan.Entries[0];
        if (first.IsFrontMatter || first.TitleLineNumber.HasValue) return plan;
        var startsAtBeginning = first.ContentRanges.Count == 0 || first.ContentRanges.Min(range => range.StartLine) == 1;
        if (!startsAtBeginning) return plan;
        var entries = plan.Entries.ToArray();
        entries[0] = first with
        {
            Level = 2,
            IsFrontMatter = true,
            HeadingLevel = first.HeadingLevel is >= 1 and <= 4 ? first.HeadingLevel : 2,
        };
        return plan with { Entries = entries };
    }

    private static ChapterTreePlan NormalizePersistedPlan(ChapterTreePlan plan)
    {
        plan = NormalizeLegacyFrontMatter(plan);
        return plan with { Entries = NormalizeHierarchyLevels(plan.Entries) };
    }

    internal static IReadOnlyList<ChapterTreeEntry> NormalizeHierarchyLevels(
        IReadOnlyList<ChapterTreeEntry> entries)
    {
        var normalized = new List<ChapterTreeEntry>(entries.Count);
        var stack = new Stack<(int SourceLevel, int TreeLevel)>();
        foreach (var entry in entries)
        {
            var sourceLevel = Math.Clamp(entry.Level, 1, 4);
            var headingLevel = entry.HeadingLevel is >= 1 and <= 4 ? entry.HeadingLevel : sourceLevel;
            if (entry.IsFrontMatter)
            {
                normalized.Add(entry with { Level = 2, HeadingLevel = headingLevel });
                stack.Clear();
                continue;
            }

            while (stack.Count > 0 && stack.Peek().SourceLevel >= sourceLevel) stack.Pop();
            var treeLevel = stack.Count == 0 ? 1 : stack.Peek().TreeLevel + 1;
            normalized.Add(entry with { Level = treeLevel, HeadingLevel = headingLevel });
            stack.Push((sourceLevel, treeLevel));
        }
        return normalized;
    }
}
