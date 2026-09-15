namespace EasyPub.Core;

public enum AlignmentKind { Matched, LocalOnly, ReferenceOnly }

/// <summary>
/// One step of the global alignment. LocalIndex indexes <see cref="ReferenceAlignment.Entries"/>;
/// ReferenceIndex indexes <see cref="ReferenceAlignment.Reference"/>. A step is never a text edit.
/// </summary>
public sealed record AlignmentStep(AlignmentKind Kind, int LocalIndex, int ReferenceIndex);

/// <summary>A maximal run of consecutive steps sharing one kind.</summary>
public sealed record AlignmentRun(AlignmentKind Kind, int Start, int Count);

/// <summary>
/// A chapter title split into its two independent signals: the chapter number and the title words.
/// They must stay separate because sources disagree on both — the local TXT often writes
/// "第六篇 第十章" (number only) where the reference writes "第十章 在路上" (number and words).
/// </summary>
public sealed record TitleKey(string Number, string Words)
{
    public bool IsEmpty => Number.Length == 0 && Words.Length == 0;

    /// <summary>Comparable form used for exact-sequence lookups.</summary>
    public string Canonical => Number + "|" + Words;
}

/// <summary>
/// Global sequence alignment between the local chapter tree and a reference directory.
/// The reference is a structural baseline: it states which titles exist and in what order,
/// never what the text must say. No step authorises a text change by itself.
/// </summary>
public sealed record ReferenceAlignment(
    IReadOnlyList<ChapterTreeEntry> Entries,
    IReadOnlyList<ReferenceNode> Reference,
    ReferenceCatalog Catalog,
    IReadOnlyList<AlignmentStep> Steps)
{
    public int MatchedCount => Steps.Count(s => s.Kind == AlignmentKind.Matched);
    public int LocalOnlyCount => Steps.Count(s => s.Kind == AlignmentKind.LocalOnly);
    public int ReferenceOnlyCount => Steps.Count(s => s.Kind == AlignmentKind.ReferenceOnly);
    public double Coverage => Reference.Count == 0 ? 0 : (double)MatchedCount / Reference.Count;

    public IEnumerable<AlignmentRun> Runs()
    {
        var index = 0;
        while (index < Steps.Count)
        {
            var kind = Steps[index].Kind;
            var start = index;
            while (index < Steps.Count && Steps[index].Kind == kind) index++;
            yield return new(kind, start, index - start);
        }
    }

    public IReadOnlyList<ChapterTreeEntry> LocalEntries(AlignmentRun run) =>
        Steps.Skip(run.Start).Take(run.Count).Where(s => s.LocalIndex >= 0).Select(s => Entries[s.LocalIndex]).ToArray();

    public IReadOnlyList<ReferenceNode> ReferenceNodes(AlignmentRun run) =>
        Steps.Skip(run.Start).Take(run.Count).Where(s => s.ReferenceIndex >= 0).Select(s => Reference[s.ReferenceIndex]).ToArray();
}

public sealed class ReferenceDiagnosis(string Code, PreflightSeverity Severity, string Message, int Line, IReadOnlyList<int> Lines)
{
    public string Code { get; } = Code;
    public PreflightSeverity Severity { get; } = Severity;
    public string Message { get; } = Message;
    public int Line { get; } = Line;
    public IReadOnlyList<int> Lines { get; } = Lines;
}

public static class ReferenceOutline
{
    private const int GapScore = -2;
    private const int ExactScore = 5;
    private const int NumberOnlyScore = 3;
    private const int WordsScore = 4;
    private const int PartialScore = 2;
    private const int OrderedScore = 1;

    /// <summary>
    /// Lowest score that may consume a source line during location. Below this the match is only a
    /// title resemblance, which is not evidence strong enough to anchor a chapter.
    /// </summary>
    public const int ConfidentScore = 3;
    private const double PartialThreshold = 0.55;

    private static readonly System.Text.RegularExpressions.Regex Numbering = HeadingSyntax.Numbering;
    private static readonly System.Text.RegularExpressions.Regex ChapterNumber = HeadingSyntax.ChapterNumber;

    /// <summary>
    /// Weighted global alignment (Needleman-Wunsch). Plain longest-common-subsequence is not enough:
    /// local titles carry volume prefixes, half of them lack title words entirely, and sources disagree
    /// on numbering. Matches are therefore graded, and gap steps stay cheaper than a bad match.
    /// </summary>
    public static ReferenceAlignment Align(IReadOnlyList<ChapterTreeEntry> entries, ReferenceCatalog catalog)
    {
        var local = entries.Where(e => !e.IsFrontMatter).ToArray();
        var reference = catalog.Nodes.Where(n => n.Kind == ReferenceNodeKind.Chapter).ToArray();
        var volumePrefixes = catalog.VolumeTitles
            .Select(title => ParseWords(title))
            .Where(words => words.Length >= 2)
            .Distinct()
            .OrderByDescending(words => words.Length)
            .ToArray();
        var localKeys = local.Select(e => ParseKey(e.Title, volumePrefixes)).ToArray();
        var referenceKeys = reference.Select(r => ParseKey(r.Title, volumePrefixes)).ToArray();
        var rows = local.Length;
        var columns = reference.Length;
        var score = new int[rows + 1, columns + 1];
        for (var i = rows - 1; i >= 0; i--) score[i, columns] = score[i + 1, columns] + GapScore;
        for (var j = columns - 1; j >= 0; j--) score[rows, j] = score[rows, j + 1] + GapScore;
        for (var i = rows - 1; i >= 0; i--)
            for (var j = columns - 1; j >= 0; j--)
            {
                var best = Math.Max(score[i + 1, j], score[i, j + 1]) + GapScore;
                var pair = PairScore(localKeys[i], referenceKeys[j]);
                if (pair > 0) best = Math.Max(best, score[i + 1, j + 1] + pair);
                score[i, j] = best;
            }
        var steps = new List<AlignmentStep>(rows + columns);
        int a = 0, b = 0;
        while (a < rows && b < columns)
        {
            var pair = PairScore(localKeys[a], referenceKeys[b]);
            if (pair > 0 && score[a, b] == score[a + 1, b + 1] + pair) { steps.Add(new(AlignmentKind.Matched, a, b)); a++; b++; continue; }
            if (score[a, b] == score[a + 1, b] + GapScore) { steps.Add(new(AlignmentKind.LocalOnly, a, -1)); a++; continue; }
            steps.Add(new(AlignmentKind.ReferenceOnly, -1, b)); b++;
        }
        while (a < rows) { steps.Add(new(AlignmentKind.LocalOnly, a, -1)); a++; }
        while (b < columns) { steps.Add(new(AlignmentKind.ReferenceOnly, -1, b)); b++; }
        return new(local, reference, catalog, steps);
    }

    /// <summary>
    /// Splits a title into its number and its words. Numbering prefixes, known volume prefixes,
    /// punctuation, whitespace and Chinese/Arabic numeral variants are all normalised away so that
    /// "追忆杀戮时光（1）" and "追忆杀戮时光（一）" agree, and "第五篇 回归 序 王都（中）"
    /// reduces to the same remainder as "序章 王都（中）".
    /// </summary>
    public static TitleKey ParseKey(string title, IReadOnlyList<string>? volumePrefixes = null)
    {
        var text = title.Normalize(System.Text.NormalizationForm.FormKC);
        var number = "";
        if (ChapterNumber.Match(text) is { Success: true } match && ParseNumeral(match.Groups["n"].Value) is { } value && value > 0)
            number = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var words = ParseWords(Numbering.Replace(text, ""));
        if (volumePrefixes is not null && words.Length > 0)
            foreach (var prefix in volumePrefixes)
                if (words.Length > prefix.Length && words.StartsWith(prefix, StringComparison.Ordinal)) { words = words[prefix.Length..]; break; }
        return new(number, words);
    }

    private static string ParseWords(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (IsNumeral(text[index]))
            {
                var start = index;
                while (index < text.Length && IsNumeral(text[index])) index++;
                var run = text[start..index];
                builder.Append(ParseNumeral(run)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? run);
                continue;
            }
            var ch = text[index++];
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }

    private static bool IsNumeral(char ch) => HeadingSyntax.IsNumeral(ch);
    private static int? ParseNumeral(string text) => HeadingSyntax.ParseNumber(text);

    /// <summary>Public view of the graded match, so reference-driven location can reuse it.</summary>
    public static int MatchScore(TitleKey left, TitleKey right) => PairScore(left, right);

    /// <summary>
    /// Graded match. Highest confidence is same number plus same words. Same number with one side
    /// lacking words is still a real match, because untitled chapters are common in the source TXT.
    /// Zero means the pair must never be matched, so a gap is preferred.
    /// </summary>
    private static int PairScore(TitleKey left, TitleKey right)
    {
        var sameNumber = left.Number.Length > 0 && left.Number == right.Number;
        if (sameNumber)
        {
            if (left.Words.Length > 0 && left.Words == right.Words) return ExactScore;
            if (left.Words.Length == 0 || right.Words.Length == 0) return NumberOnlyScore;
        }
        if (left.Words.Length >= 2 && right.Words.Length >= 2)
        {
            if (left.Words == right.Words) return WordsScore;
            if (Similarity(left.Words, right.Words) >= PartialThreshold) return PartialScore;
        }
        if (left.IsEmpty && right.IsEmpty) return OrderedScore;
        return 0;
    }

    /// <summary>
    /// Character-level similarity in [0,1], exposed so the locator's last-resort pass can compare a
    /// reference title against a line whose heading has a wrong or missing character ("赌徙" vs
    /// "赌徒"), which a plain substring test can never match.
    /// </summary>
    public static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        var table = new int[left.Length + 1, right.Length + 1];
        for (var i = 1; i <= left.Length; i++)
            for (var j = 1; j <= right.Length; j++)
                table[i, j] = left[i - 1] == right[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
        return 2.0 * table[left.Length, right.Length] / (left.Length + right.Length);
    }

    /// <summary>Start index of <paramref name="needle"/> inside <paramref name="haystack"/>, or -1.</summary>
    internal static int FindSequence(IReadOnlyList<string> haystack, IReadOnlyList<string> needle)
    {
        if (needle.Count == 0 || needle.Count > haystack.Count) return -1;
        for (var start = 0; start + needle.Count <= haystack.Count; start++)
        {
            var ok = true;
            for (var offset = 0; offset < needle.Count; offset++)
                if (haystack[start + offset] != needle[offset]) { ok = false; break; }
            if (ok) return start;
        }
        return -1;
    }
}

/// <summary>
/// Turns an alignment into findings. Every finding is evidence for a human decision; none of them
/// edits text, and all of them are reversible through the normal chapter-tree undo path.
/// </summary>
public static class ReferenceDiagnostics
{
    public static IReadOnlyList<ReferenceDiagnosis> Inspect(ReferenceAlignment alignment)
    {
        var findings = new List<ReferenceDiagnosis>();
        var referenceKeys = alignment.Reference.Select(r => ReferenceOutline.ParseKey(r.Title).Canonical).ToArray();
        foreach (var run in alignment.Runs())
        {
            var nodes = alignment.ReferenceNodes(run).ToArray();
            var entries = alignment.LocalEntries(run).ToArray();
            if (run.Kind == AlignmentKind.ReferenceOnly && nodes.Length > 0)
            {
                var before = PrecedingMatchedEntry(alignment, run);
                var listed = string.Join("；", nodes.Take(5).Select(n => n.Title));
                var rest = nodes.Length > 5 ? $" 等共 {nodes.Length} 章" : "";
                var anchor = before is null
                    ? "位于参考目录起始处"
                    : $"应位于本地“{before.Title}”（第 {before.TitleLineNumber} 行）之后";
                findings.Add(new("ref_chapter_missing", PreflightSeverity.Warning,
                    $"参考目录中有 {nodes.Length} 章在本地未找到：{listed}{rest}。{anchor}。需要核对原文后补建，不会自动插入正文。",
                    before?.TitleLineNumber ?? 0, []));
                continue;
            }
            if (run.Kind != AlignmentKind.LocalOnly || entries.Length == 0) continue;
            var keys = entries.Select(e => ReferenceOutline.ParseKey(e.Title).Canonical).ToArray();
            var lines = entries.Select(e => e.TitleLineNumber ?? 0).ToArray();
            var position = ReferenceOutline.FindSequence(referenceKeys, keys);
            if (position >= 0)
            {
                var twin = LocalTwinLines(alignment, position, keys.Length, run);
                findings.Add(new("ref_duplicate_segment", PreflightSeverity.Warning,
                    $"本地第 {lines[0]}–{lines[^1]} 行的 {entries.Length} 章（{entries[0].Title} → {entries[^1].Title}）" +
                    $"与参考目录第 {position + 1}–{position + keys.Length} 章完全对应；参考目录中该段只出现一次。" +
                    (twin is null
                        ? "需要核对前后衔接，确认这一段是否为错位重复。"
                        : $"本地第 {twin} 行附近已有同一段，应保留与参考目录位置一致的那一份。"),
                    lines[0], lines));
            }
            else
            {
                var listed = string.Join("；", entries.Take(5).Select(e => e.Title));
                var rest = entries.Length > 5 ? $" 等共 {entries.Length} 章" : "";
                findings.Add(new("ref_extra_chapter", PreflightSeverity.Information,
                    $"本地 {entries.Length} 章在参考目录中没有对应：{listed}{rest}。可能是番外、站点广告章或误识别，请核对，不会自动处理。",
                    lines[0], lines));
            }
        }
        if (alignment.Catalog.VolumeTitles.Count > 1 && alignment.Entries.All(e => e.Level <= 1))
        {
            var volumes = string.Join("；", alignment.Catalog.VolumeTitles);
            findings.Add(new("ref_volume_missing", PreflightSeverity.Information,
                $"参考目录含 {alignment.Catalog.VolumeTitles.Count} 卷，本地章节树未建立对应层级：{volumes}。" +
                "可依据参考目录的卷边界建立卷／篇层级，正文与章节身份不变。",
                0, []));
        }
        return findings;
    }

    private static ChapterTreeEntry? PrecedingMatchedEntry(ReferenceAlignment alignment, AlignmentRun run)
    {
        for (var index = run.Start - 1; index >= 0; index--)
            if (alignment.Steps[index].Kind == AlignmentKind.Matched)
                return alignment.Entries[alignment.Steps[index].LocalIndex];
        return null;
    }

    /// <summary>First line of the local block that already matched the same reference range, if any.</summary>
    private static int? LocalTwinLines(ReferenceAlignment alignment, int referenceStart, int count, AlignmentRun self)
    {
        foreach (var run in alignment.Runs())
        {
            if (run.Kind != AlignmentKind.Matched) continue;
            var steps = alignment.Steps.Skip(run.Start).Take(run.Count).ToArray();
            if (steps.Length < count) continue;
            for (var offset = 0; offset + count <= steps.Length; offset++)
            {
                if (steps[offset].ReferenceIndex != referenceStart) continue;
                var contiguous = true;
                for (var probe = 1; probe < count; probe++)
                    if (steps[offset + probe].ReferenceIndex != referenceStart + probe) { contiguous = false; break; }
                if (!contiguous) continue;
                if (run.Start == self.Start) continue;
                return alignment.Entries[steps[offset].LocalIndex].TitleLineNumber;
            }
        }
        return null;
    }
}
