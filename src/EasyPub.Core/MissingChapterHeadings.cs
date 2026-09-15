using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record MissingChapterHeading(string OwnerId, int NextLine, int Line, int Number, string Original, string Title);

public static class MissingChapterHeadings
{
    private const string Digits = "[0-9０-９零〇一二两三四五六七八九十百千万]+";
    private static readonly Regex Normal = new("^第\\s*(" + Digits + ")\\s*章", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Candidate = new("^(?<prefix>第)?\\s*(?<number>" + Digits + ")\\s*(?<unit>[章张])?\\s+(?<title>[^\\r\\n]+)$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static IReadOnlyList<MissingChapterHeading> Find(ChapterTreeDocument document, IReadOnlyList<ChapterTreeEntry>? entries = null,
        CancellationToken cancellationToken = default)
    {
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
            if (!a.Success || !b.Success || ChapterDiagnostics.ParseNumber(a.Groups[1].Value) is not int low
                || ChapterDiagnostics.ParseNumber(b.Groups[1].Value) is not int high || high <= low + 1) continue;
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
                    if (bad == 1)
                    {
                        var fixedNumber = new string(token.Value.Select(c => corrections.TryGetValue(c, out var replacement) ? replacement : c).ToArray());
                        corrected = text[..token.Index] + fixedNumber + text[(token.Index + token.Length)..];
                        m = Candidate.Match(corrected);
                        numberTypo = true;
                    }
                }
                if (!m.Success || ChapterDiagnostics.ParseNumber(m.Groups["number"].Value) is not int number || number <= low || number >= high) continue;
                var prefix = m.Groups["prefix"].Success; var unit = m.Groups["unit"].Value;
                // Exactly one of the supported formatting mistakes, never guess a number.
                if (!(numberTypo || prefix && unit is "张" or "" || !prefix && unit == "章")) continue;
                var title = m.Groups["title"].Value.Trim();
                if (title.Contains('。') || title.Contains('；') || title.Contains('“') || title.StartsWith("中提到")) continue;
                if (!string.IsNullOrWhiteSpace(document.SourceLine(line - 1)?.Text)
                    || !string.IsNullOrWhiteSpace(document.SourceLine(line + 1)?.Text)) continue;
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
