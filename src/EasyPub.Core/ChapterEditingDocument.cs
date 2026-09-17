using System.Text;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

public enum ChapterCandidateKind
{
    Recognized,
    NumericTitle,
}

public sealed record ChapterCandidate(
    int LineNumber,
    string OriginalTitle,
    string SuggestedTitle,
    ChapterCandidateKind Kind);

public sealed record ChapterTitleEdit(int LineNumber, string Title);

public sealed record TextSourceLine(
    int LineNumber,
    string Text,
    bool IsChapterCandidate);

public sealed class ChapterEditingDocument
{
    public const string DefaultChapterPattern = HeadingSyntax.DefaultPattern;

    private readonly string[] _lines;
    private readonly string _newLine;
    private readonly Encoding _encoding;
    private readonly byte[] _preamble;

    private ChapterEditingDocument(
        string sourcePath,
        string[] lines,
        string newLine,
        Encoding encoding,
        byte[] preamble,
        IReadOnlyList<ChapterCandidate> candidates)
    {
        SourcePath = sourcePath;
        _lines = lines;
        _newLine = newLine;
        _encoding = encoding;
        _preamble = preamble;
        Candidates = candidates;
    }

    public string SourcePath { get; }

    public IReadOnlyList<ChapterCandidate> Candidates { get; }

    public int LineCount => _lines.Length;

    public IReadOnlyList<TextSourceLine> GetLines()
    {
        var chapterLines = Candidates.Select(candidate => candidate.LineNumber).ToHashSet();
        return _lines
            .Select((line, index) => new TextSourceLine(index + 1, line, chapterLines.Contains(index + 1)))
            .ToArray();
    }

    public IReadOnlyList<ChapterTitleEdit> CreateAllSuggestedEdits() =>
        Candidates
            .Select(candidate => new ChapterTitleEdit(candidate.LineNumber, candidate.SuggestedTitle))
            .ToArray();

    public static async Task<ChapterEditingDocument> LoadAsync(
        string sourcePath,
        string? chapterPattern = null,
        TextEncodingMode encodingMode = TextEncodingMode.Auto,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(sourcePath, cancellationToken);
        return FromBytes(sourcePath, bytes, chapterPattern, encodingMode, cancellationToken);
    }

    internal static ChapterEditingDocument FromBytes(
        string sourcePath,
        byte[] bytes,
        string? chapterPattern = null,
        TextEncodingMode encodingMode = TextEncodingMode.Auto,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var decoded = TextFileDecoder.Decode(bytes, encodingMode);
        var text = decoded.Text;
        var newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var chapterRegex = new Regex(
            string.IsNullOrWhiteSpace(chapterPattern) ? DefaultChapterPattern : chapterPattern,
            RegexOptions.Compiled);
        var candidates = new List<ChapterCandidate>();

        for (var index = 0; index < lines.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var rawLine = lines[index];
            var title = rawLine.Trim();
            if (title.Length == 0) continue;
            // A heading line printed twice in a row — "第一章 归来" then the identical line again — is
            // how some releases mark a chapter start. Taken literally it becomes two chapters with the
            // same name, the first holding no text at all, and every count downstream inherits the
            // error. Only an exact repeat of the line immediately above is dropped, so a title that
            // legitimately resembles its neighbour is untouched.
            if (index > 0 && string.Equals(lines[index - 1].Trim(), title, StringComparison.Ordinal)
                && chapterRegex.IsMatch(rawLine)) continue;

            if (chapterRegex.IsMatch(rawLine))
            {
                // A chapter heading is not a sentence. The pattern allows a heading to drop its 「第」
                // ("八百三十章 赌徒"), which is what makes prose that merely begins with a chapter
                // number look like a heading too: "第二章的内容他记不清了。" matches, and turning it into
                // a chapter cuts the chapter it sits inside in half. Ending punctuation is what tells
                // them apart, and it has to come with enough length to be prose: a bare "第三章？" is a
                // heading with a question mark in it.
                if (LooksLikeProseSentence(title)) continue;
                candidates.Add(new ChapterCandidate(index + 1, title, title, ChapterCandidateKind.Recognized));
            }
            else if (ChapterTitleNormalizer.TryNormalizeNumericTitle(rawLine, out var normalized))
            {
                candidates.Add(new ChapterCandidate(index + 1, title, normalized, ChapterCandidateKind.NumericTitle));
            }
        }

        return new ChapterEditingDocument(
            Path.GetFullPath(sourcePath), lines, newLine, decoded.Encoding, decoded.Preamble, candidates);
    }

    /// <summary>
    /// True for a line that reads as a sentence rather than a heading: it ends a clause and runs long
    /// enough that no release would print it as a title. Both conditions are required, because either
    /// one alone misfires — "第三章 谁在那里？" is a title with punctuation, and "第二章" is short but a
    /// heading, while "第二章的内容他记不清了。" is neither. Ten characters is where the two stop
    /// overlapping: real headings in this corpus run to about ten, and sentences that merely start
    /// with a chapter number run longer.
    /// </summary>
    internal static bool LooksLikeProseSentence(string title) =>
        title.Length > 10 && title.IndexOfAny(['。', '！', '？', '；']) >= 0;

    public string Render(IEnumerable<ChapterTitleEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var editableLines = Candidates.Select(candidate => candidate.LineNumber).ToHashSet();
        var replacements = new Dictionary<int, string>();
        foreach (var edit in edits)
        {
            if (!editableLines.Contains(edit.LineNumber))
                throw new ArgumentException($"第 {edit.LineNumber} 行不是章节候选。", nameof(edits));
            var title = edit.Title.Trim();
            if (title.Length == 0 || title.Contains('\r') || title.Contains('\n'))
                throw new ArgumentException($"第 {edit.LineNumber} 行的章节标题无效。", nameof(edits));
            if (!replacements.TryAdd(edit.LineNumber, title))
                throw new ArgumentException($"第 {edit.LineNumber} 行存在重复编辑。", nameof(edits));
        }

        var rendered = (string[])_lines.Clone();
        foreach (var replacement in replacements)
            rendered[replacement.Key - 1] = replacement.Value;
        return string.Join(_newLine, rendered);
    }

    public async Task SaveAsAsync(
        string outputPath,
        IEnumerable<ChapterTitleEdit> edits,
        CancellationToken cancellationToken = default)
    {
        var fullOutputPath = Path.GetFullPath(outputPath);
        if (string.Equals(fullOutputPath, SourcePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("章节编辑结果必须另存为新文件，不能覆盖原始 TXT。", nameof(outputPath));

        var content = _encoding.GetBytes(Render(edits));
        var bytes = new byte[_preamble.Length + content.Length];
        _preamble.CopyTo(bytes, 0);
        content.CopyTo(bytes, _preamble.Length);
        await File.WriteAllBytesAsync(fullOutputPath, bytes, cancellationToken);
    }

    public string GetPreview(int lineNumber, int contextLines = 4)
    {
        if (lineNumber < 1 || lineNumber > _lines.Length) throw new ArgumentOutOfRangeException(nameof(lineNumber));
        if (contextLines < 0) throw new ArgumentOutOfRangeException(nameof(contextLines));

        var first = Math.Max(1, lineNumber - contextLines);
        var last = Math.Min(_lines.Length, lineNumber + contextLines);
        var width = last.ToString().Length;
        var preview = new StringBuilder();
        for (var current = first; current <= last; current++)
        {
            preview.Append(current == lineNumber ? "> " : "  ");
            preview.Append(current.ToString().PadLeft(width + 3));
            preview.Append("  ");
            preview.AppendLine(_lines[current - 1]);
        }
        return preview.ToString();
    }

}
