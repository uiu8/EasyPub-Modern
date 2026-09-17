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
    public string? RecognitionSource { get; init; }

    /// <summary>
    /// An identity that survives a rebuild.
    ///
    /// <para><see cref="Id"/> does not: every rebuild from the same input mints new GUIDs, so anything that
    /// matches entries across two builds by id finds nothing — which is how a plan compiled twice from the
    /// same decisions first failed to compile to the same products at all.</para>
    ///
    /// <para>A chapter is identified by the line its heading sits on, and by its title only when there is
    /// no heading line to point at (a volume the directory implies, the front matter, a chapter whose
    /// heading this repair has yet to insert). Line-plus-title is what the directory, the locator and the
    /// rebuilt tree already agree on.</para>
    /// </summary>
    public string StableKey => TitleLineNumber is { } line
        ? "L" + line.ToString(System.Globalization.CultureInfo.InvariantCulture)
        : "T" + (Title ?? "");
}

public sealed record ChapterTreePlan(
    string SourceSha256,
    IReadOnlyList<ChapterTreeEntry> Entries)
{
    public bool? NumericHeadingRecognition { get; init; }
    public int? NumericHeadingMinimumBodyLines { get; init; }
    public string? NumericHeadingPattern { get; init; }
    public string? HeadingNumberCorrections { get; init; }
    public ReferenceCatalog? ReferenceCatalog { get; init; }
    public IReadOnlyDictionary<string, ChapterReviewGroup>? ConfirmedReviews { get; init; }
}

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
    internal IReadOnlyList<ChapterTreeSourceLine> SourceLines => _sourceLines;
    public IReadOnlyList<ChapterTreeEntry> Entries { get; }
    public TocHierarchyOptions RecognitionOptions { get; private init; } = new();
    public ChapterTreeSourceLine? SourceLine(int number) => number >= 1 && number <= _sourceLines.Count ? _sourceLines[number - 1] : null;

    /// <summary>Creates a new in-memory tree for the same TXT, preserving its source hash and recognition options.</summary>
    public ChapterTreeDocument WithEntries(IEnumerable<ChapterTreeEntry> entries)
        => new(SourcePath, SourceSha256, _sourceLines, entries.ToArray()) { RecognitionOptions = RecognitionOptions };

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
        return Load(fullPath, bytes, chapterPattern, hierarchy, encodingMode, existingPlan, cancellationToken);
    }

    /// <summary>
    /// Reads a chapter tree from bytes that are already in hand.
    ///
    /// <para>Added for <see cref="RestoreTransaction"/>: it recognises the text of a <b>backup</b>, which is a
    /// `.bak` file, and the path-based entry point refuses anything that is not a `.txt`. Copying the backup
    /// to a temporary `.txt` to get around that would write a book-sized file to disk for no reason, and would
    /// leave it behind whenever the process died mid-restore.</para>
    /// </summary>
    public static ChapterTreeDocument Load(
        string sourcePath,
        byte[] bytes,
        string? chapterPattern = null,
        TocHierarchyOptions? hierarchy = null,
        TextEncodingMode encodingMode = TextEncodingMode.Auto,
        ChapterTreePlan? existingPlan = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var fullPath = Path.GetFullPath(sourcePath);
        var sourceHash = Convert.ToHexString(SHA256.HashData(bytes));
        var hierarchyOptions = hierarchy ?? new TocHierarchyOptions();
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
            return new ChapterTreeDocument(fullPath, sourceHash, sourceLines, existingPlan.Entries) { RecognitionOptions = hierarchyOptions.ForBook(existingPlan) };
        }

        var levelPatterns = hierarchyOptions.Enabled
            ? new[]
            {
                CompilePattern(hierarchyOptions.Level1Pattern, TocHierarchyOptions.DefaultLevel1Pattern),
                CompilePattern(hierarchyOptions.Level2Pattern, TocHierarchyOptions.DefaultLevel2Pattern),
                CompilePattern(hierarchyOptions.Level3Pattern, TocHierarchyOptions.DefaultLevel3Pattern),
            }
            : [];
        var candidates = editingDocument.Candidates
            .Where(candidate => candidate.Kind != ChapterCandidateKind.NumericTitle)
            .ToDictionary(candidate => candidate.LineNumber);
        if (hierarchyOptions.RecognizeNumericHeadings)
        {
            var numericRegex = NumericHeadingRule.Compile(hierarchyOptions.NumericHeadingPattern);
            foreach (var line in sourceLines)
                if (!candidates.ContainsKey(line.LineNumber) && NumericHeadingRule.Matches(numericRegex, line.Text))
                    candidates.Add(line.LineNumber, new ChapterCandidate(line.LineNumber, line.Text.Trim(), line.Text.Trim(), ChapterCandidateKind.NumericTitle));
        }
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

        var numericLines = NumericHeadingFilter.AcceptedLines(
            sourceLines.Select(line => line.Text).ToArray(),
            headings.Select(heading => heading.LineNumber - 1).ToArray(),
            hierarchyOptions.NumericHeadingMinimumBodyLines);
        headings.RemoveAll(heading => candidates.TryGetValue(heading.LineNumber, out var candidate)
            && candidate.Kind == ChapterCandidateKind.NumericTitle
            && MatchLevel(sourceLines[heading.LineNumber - 1].Text, levelPatterns) == 0
            && !numericLines.Contains(heading.LineNumber - 1));

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
            // A heading printed twice in a row — "第九章 归途" then the identical line again — is one
            // chapter, and the recognition pass already treats it as one by not emitting a second
            // candidate. But the repeated line is still a line in the file, and the body range used to
            // begin right after the heading, which swallowed it: the finished book opened every such
            // chapter with the title printed a second time as its first body line. Skipping the run
            // here is what makes "one chapter" true of the ranges as well as of the entries.
            var bodyStart = SkipRepeatedHeadingRun(sourceLines, heading.LineNumber);
            entries.Add(new ChapterTreeEntry(
                Guid.NewGuid().ToString("N"),
                heading.Title,
                heading.Level,
                true,
                heading.LineNumber,
                CreateRange(bodyStart, endLine))
            {
                HeadingLevel = heading.Level,
                RecognitionSource = candidates.TryGetValue(heading.LineNumber, out var sourceCandidate)
                    && sourceCandidate.Kind == ChapterCandidateKind.NumericTitle
                    && MatchLevel(sourceLines[heading.LineNumber - 1].Text, levelPatterns) == 0 ? "numeric" : "pattern",
            });
        }

        entries = NormalizeHierarchyLevels(entries).ToList();
        var plan = new ChapterTreePlan(sourceHash, entries);
        ValidatePlan(plan, sourceLines.Length);
        return new ChapterTreeDocument(fullPath, sourceHash, sourceLines, entries) { RecognitionOptions = hierarchyOptions };
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

    /// <summary>
    /// The first line after the run of identical heading lines that begins at <paramref name="headingLine"/>.
    ///
    /// Only lines byte-identical to the heading, taken consecutively, are skipped — a body whose first
    /// paragraph happens to repeat a sentence is not a heading run, because the heading line itself is
    /// matched by exact text equality and nothing here re-tests it against the chapter pattern. A run is
    /// capped at a handful of lines so that a pathological file cannot lose a chapter's entire body to
    /// this rule.
    /// </summary>
    internal const int MaximumRepeatedHeadingRun = 4;

    private static int SkipRepeatedHeadingRun(IReadOnlyList<ChapterTreeSourceLine> sourceLines, int headingLine)
    {
        if (headingLine < 1 || headingLine > sourceLines.Count) return headingLine + 1;
        var text = sourceLines[headingLine - 1].Text.Trim();
        if (text.Length == 0) return headingLine + 1;
        var line = headingLine + 1;
        var skipped = 0;
        while (line <= sourceLines.Count && skipped < MaximumRepeatedHeadingRun
            && string.Equals(sourceLines[line - 1].Text.Trim(), text, StringComparison.Ordinal))
        {
            line++;
            skipped++;
        }
        return line;
    }

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
