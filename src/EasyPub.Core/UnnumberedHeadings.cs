using System.Text;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record HeadingProposal(string OwnerId, int Line, string Original, string Suggested, string Evidence, bool Recommended);

public static class UnnumberedHeadings
{
    private static readonly Regex Prefix = new(@"^(?:第[零〇一二两三四五六七八九十百千万0-9]+[卷部篇]\s*\S*?\s+)?(?:第[零〇一二两三四五六七八九十百千万0-9]+[章回]\s*|序章?\s*)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Part = new(@"^(.+?)[(（](?:续\s*[,，]?\s*)?([上中下]|[一二三四五六七八九十0-9]+)[)）]$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    public static string Key(string text) => string.Concat(Prefix.Replace(text.Normalize(NormalizationForm.FormKC).Trim(), "").Where(c => !char.IsWhiteSpace(c)));

    public static IReadOnlyList<HeadingProposal> Find(ChapterTreeDocument doc, IReadOnlyList<ChapterTreeEntry> entries,
        int minimumLines = 200, IReadOnlyList<string>? catalog = null)
    {
        var reference = (catalog ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).GroupBy(Key).ToDictionary(g => g.Key, g => g.Distinct().ToArray());
        var result = new List<HeadingProposal>();
        foreach (var entry in entries)
        {
            if (entry.IsFrontMatter || entry.ContentRanges.Count != 1 || entry.ContentRanges.Sum(r => r.EndLine - r.StartLine + 1) < minimumLines) continue;
            var range = entry.ContentRanges[0];
            var possible = new List<(int Line, string Title, Match Part, string[] Reference)>();
            for (var line = range.StartLine; line <= range.EndLine; line++)
            {
                var raw = doc.SourceLine(line)!.Text;
                var title = raw.Trim();
                if (title.Length is < 2 or > 40 || title.Any(c => "。！？!?；;“”\"：:".Contains(c))
                    || title.All(c => char.IsDigit(c) || char.IsWhiteSpace(c)) || title.Contains("http", StringComparison.OrdinalIgnoreCase)
                    || title.Contains("举报") || title.Contains("书签") || title.Contains("阅读体验")) continue;
                if (!string.IsNullOrWhiteSpace(doc.SourceLine(line - 1)?.Text) || !string.IsNullOrWhiteSpace(doc.SourceLine(line + 1)?.Text)) continue;
                var match = reference.GetValueOrDefault(Key(title), []);
                if (raw != raw.TrimStart() && match.Length == 0) continue;
                possible.Add((line, title, Part.Match(title), match));
            }
            foreach (var candidate in possible)
            {
                var family = candidate.Part.Success && possible.Any(other => other.Line != candidate.Line && other.Part.Success
                    && Key(other.Part.Groups[1].Value) == Key(candidate.Part.Groups[1].Value)
                    && other.Part.Groups[2].Value != candidate.Part.Groups[2].Value);
                var referenceMatch = candidate.Reference.Length == 1;
                if (!family && !referenceMatch) continue;
                var evidence = referenceMatch ? "与参考目录标题匹配；独立短行、上下空行" : "同名分段系列；独立顶格短行、上下空行";
                result.Add(new(entry.Id, candidate.Line, candidate.Title, referenceMatch ? candidate.Reference[0] : candidate.Title, evidence, referenceMatch || family));
            }
        }
        return result;
    }

    public static IReadOnlyList<ChapterTreeEntry> Apply(ChapterTreeDocument doc, IReadOnlyList<ChapterTreeEntry> entries,
        IReadOnlyList<HeadingProposal> proposals, bool useReferenceTitles)
    {
        var result = entries.ToList();
        foreach (var p in proposals.DistinctBy(p => p.Line).OrderByDescending(p => p.Line))
        {
            var index = result.FindIndex(e => e.Id == p.OwnerId);
            if (index < 0) throw new InvalidOperationException("章节已变化，请重新预览。");
            var owner = result[index];
            if (owner.ContentRanges.Count != 1 || (index + 1 < result.Count && result[index + 1].Level > owner.Level))
                throw new InvalidOperationException("合并范围或父章节需要先人工核对，不能自动拆分。");
            var range = owner.ContentRanges[0];
            if (p.Line < range.StartLine || p.Line > range.EndLine || doc.SourceLine(p.Line)?.Text.Trim() != p.Original)
                throw new InvalidOperationException("原文位置已变化，请重新预览。");
            result[index] = owner with { ContentRanges = p.Line > range.StartLine ? [new(range.StartLine, p.Line - 1)] : [] };
            result.Insert(index + 1, new(Guid.NewGuid().ToString("N"), useReferenceTitles ? p.Suggested : p.Original,
                owner.Level, owner.IncludeInToc, p.Line, p.Line < range.EndLine ? [new(p.Line + 1, range.EndLine)] : [])
                { RecognitionSource = "manual", HeadingLevel = owner.HeadingLevel });
        }
        doc.CreatePlan(result);
        return result;
    }
}
