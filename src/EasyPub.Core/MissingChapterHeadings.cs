using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record MissingChapterHeading(string OwnerId, int NextLine, int Line, int Number, string Original, string Title);

/// <summary>
/// How far the one-click repair is allowed to reach when looking for a heading the recogniser missed.
/// Every switch here trades recall for safety, and the defaults are the cautious end of each: a
/// candidate that is wrong splits a chapter in the wrong place, which is worse than one that is
/// missing, because a missing candidate is visible in the list and a wrong one is not.
/// </summary>
public sealed record MissingChapterHeadingOptions
{
    /// <summary>A heading sits on its own line. Turning this off finds headings that lost their blank
    /// neighbours, at the cost of considering ordinary lines; the other tests then do the work.</summary>
    public bool RequireIsolatedLine { get; init; } = true;

    /// <summary>Widest run of missing numbers one pair of chapters may bracket. A book that restarts its
    /// numbering per volume shows huge apparent gaps between volumes, and "40 chapters are missing" is
    /// almost always a numbering convention rather than missing text.</summary>
    public int MaximumGap { get; init; } = 30;

    /// <summary>Accept a heading whose 「第」 was dropped ("一百零二章 归途"). Releases print these, and
    /// the recogniser already accepts them — the repair simply refused to.</summary>
    public bool AllowMissingPrefix { get; init; } = true;

    /// <summary>Accept a heading whose number contains a character from the book's 编号纠错表
    /// ("第一百零久章" as 第一百零九章). The table itself stays under the book's or the user's control.</summary>
    public bool AllowNumberTypos { get; init; } = true;
}

public static class MissingChapterHeadings
{
    private const string Digits = "[0-9０-９零〇一二两三四五六七八九十百千万]+";
    private static readonly Regex Normal = new("^第\\s*(" + Digits + ")\\s*章", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    // Both the 「第」 and the space before the title are optional, because that is how a release writes
    // a heading with something missing: "三章 归途", "三章归途" and "第三章 归途" are the same heading
    // as far as the reader is concerned. Requiring the space accepted "三章归途" by accident while
    // refusing "三章 归途", which is the shape that actually shows up. The title may not start with a
    // particle: "三章的路上" is prose that happens to begin with a number, and 「的」 is what gives it
    // away.
    private static readonly Regex Candidate = new("^(?<prefix>第)?\\s*(?<number>" + Digits + ")\\s*(?<unit>[章张])?\\s*(?<title>[^\\s的].*)$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static IReadOnlyList<MissingChapterHeading> Find(ChapterTreeDocument document, IReadOnlyList<ChapterTreeEntry>? entries = null,
        CancellationToken cancellationToken = default) =>
        Find(document, entries, new MissingChapterHeadingOptions(), cancellationToken);

    public static IReadOnlyList<MissingChapterHeading> Find(ChapterTreeDocument document, IReadOnlyList<ChapterTreeEntry>? entries,
        MissingChapterHeadingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        entries ??= document.Entries;
        var corrections = HeadingTypoRules.Parse(document.RecognitionOptions.HeadingNumberCorrections);
        var numberCandidate = new Regex("^第\\s*(?<number>[0-9０-９零〇一二两三四五六七八九十百千万" + Regex.Escape(new string(corrections.Keys.ToArray())) + "]+)\\s*章\\s+(?<title>.+)$", RegexOptions.None, TimeSpan.FromMilliseconds(200));
        var result = new List<MissingChapterHeading>();
        var recognized = entries.Select(e => e.TitleLineNumber).OfType<int>().ToHashSet();
        for (var i = 1; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = entries[i - 1]; var right = entries[i];
            if (left.IsFrontMatter || right.IsFrontMatter || left.Level != right.Level || left.ContentRanges.Count != 1
                || left.TitleLineNumber is not int start || right.TitleLineNumber is not int end || end <= start) continue;
            var a = Normal.Match(left.Title.Trim()); var b = Normal.Match(right.Title.Trim());
            // The gap between the two headings is what the repair fills, so its width is the first
            // filter. "high <= low + 1" left nothing to fill at all, which quietly excluded every pair
            // whose numbers were adjacent in the reading sense but far apart in the file.
            if (!a.Success || !b.Success || ChapterDiagnostics.ParseNumber(a.Groups[1].Value) is not int low
                || ChapterDiagnostics.ParseNumber(b.Groups[1].Value) is not int high || high <= low + 1) continue;
            if ((long)high - low - 1 > options.MaximumGap) continue;
            var candidates = new List<MissingChapterHeading>();
            for (var line = start + 1; line < end; line++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (recognized.Contains(line) || !left.ContentRanges.Any(r => line >= r.StartLine && line <= r.EndLine)) continue;
                var text = document.SourceLine(line)?.Text.Trim() ?? "";
                if (text.Length is < 3 or > 60) continue;
                var m = Candidate.Match(text);
                var numberMatch = numberCandidate.Match(text);
                var corrected = text;
                var numberTypo = false;
                if (numberMatch.Success)
                {
                    var token = numberMatch.Groups["number"];
                    var bad = token.Value.Count(c => corrections.ContainsKey(c));
                    if (bad == 1 && options.AllowNumberTypos)
                    {
                        var fixedNumber = new string(token.Value.Select(c => corrections.TryGetValue(c, out var replacement) ? replacement : c).ToArray());
                        corrected = text[..token.Index] + fixedNumber + text[(token.Index + token.Length)..];
                        m = Candidate.Match(corrected);
                        numberTypo = true;
                    }
                }
                if (!m.Success || ChapterDiagnostics.ParseNumber(m.Groups["number"].Value) is not int number || number <= low || number >= high) continue;
                var prefix = m.Groups["prefix"].Success; var unit = m.Groups["unit"].Value;
                // Exactly one of the supported formatting mistakes, never guess a number. The three
                // shapes are: the number was miswritten, 「第」 was dropped, or the unit was miswritten
                // ("张" for "章"). Nothing here invents a number that is not in the text.
                var missingPrefix = !prefix && unit == "章" && options.AllowMissingPrefix;
                if (!(numberTypo || prefix && unit is "张" or "" || missingPrefix)) continue;
                var title = m.Groups["title"].Value.Trim();
                if (title.Contains('。') || title.Contains('；') || title.Contains('“') || title.StartsWith("中提到")) continue;
                // A one-character number typo is stronger evidence than the surrounding blank-line
                // shape: real releases often run the malformed heading directly after the previous
                // body paragraph. Keep the isolation gate for missing-prefix/unit candidates, where
                // prose can otherwise look like a heading; typo candidates still pass the length,
                // punctuation and neighbouring-number checks above.
                if (options.RequireIsolatedLine && !numberTypo
                    && (!string.IsNullOrWhiteSpace(document.SourceLine(line - 1)?.Text)
                        || !string.IsNullOrWhiteSpace(document.SourceLine(line + 1)?.Text))) continue;
                // Without the blank neighbours, the shape of the line is the only thing left to judge it
                // by: a heading is short, and what follows the number is a title rather than a
                // sentence. The title itself is what the candidate regex captured — the whole line
                // includes whatever prose ran into it.
                if (!options.RequireIsolatedLine
                    && (text.Length > 40 || !title.Any(char.IsLetter) || title.Length > 30)) continue;
                candidates.Add(new(left.Id, end, line, number, text, "第" + m.Groups["number"].Value + "章 " + title));
            }
            // Ambiguous repeated numbers or reversed order must not enter a one-click repair.
            if (candidates.GroupBy(c => c.Number).Any(g => g.Count() > 1)
                || candidates.Zip(candidates.Skip(1)).Any(p => p.First.Number >= p.Second.Number)) continue;
            result.AddRange(candidates);
        }
        return result;
    }

    public static IReadOnlyList<ChapterTreeEntry> Repair(ChapterTreeDocument document, IReadOnlyList<ChapterTreeEntry> entries,
        IReadOnlyCollection<int> selectedLines)
    {
        var candidates = Find(document, entries).Where(c => selectedLines.Contains(c.Line)).ToArray();
        var result = entries.ToList();
        foreach (var c in candidates.OrderByDescending(c => c.Line))
        {
            var index = result.FindIndex(e => e.Id == c.OwnerId);
            var owner = result[index];
            // Restrict repair to ordinary contiguous source ranges; merged/reordered ranges need manual review.
            if (owner.ContentRanges.Count != 1) continue;
            var range = owner.ContentRanges[0];
            if (c.Line < range.StartLine || c.Line > range.EndLine) continue;
            result[index] = owner with { ContentRanges = c.Line > range.StartLine ? [new(range.StartLine, c.Line - 1)] : [] };
            result.Insert(index + 1, new ChapterTreeEntry(Guid.NewGuid().ToString("N"), c.Title, owner.Level, owner.IncludeInToc, c.Line,
                c.Line < range.EndLine ? [new(c.Line + 1, range.EndLine)] : []) { RecognitionSource = "manual" });
        }
        return result;
    }
}
