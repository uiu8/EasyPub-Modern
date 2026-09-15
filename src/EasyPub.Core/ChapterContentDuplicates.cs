using System.Text;

namespace EasyPub.Core;

public sealed record ChapterContentDuplicate(string FirstId, string SecondId, double Similarity,
    bool Exact, int FirstLines, int SecondLines, int FirstCharacters, int SecondCharacters);

/// <summary>Conservative same-title body comparison. Evidence only; never edits a source.</summary>
public static class ChapterContentDuplicates
{
    public const double Threshold = .90;
    private static string TitleKey(string text) => string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)));
    private sealed record Body(string Text, int Lines)
    {
        private Dictionary<string, int>? _grams;
        public Dictionary<string, int> Grams => _grams ??= BuildGrams(Text);
    }

    private static Body Read(ChapterTreeDocument document, ChapterTreeEntry entry)
    {
        var lines = document.GetSourceLines(entry);
        return new(string.Concat(lines.SelectMany(l => l.Text).Where(c => !char.IsWhiteSpace(c))), lines.Count);
    }

    private static Dictionary<string, int> BuildGrams(string text)
    {
        var grams = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i + 5 <= text.Length; i++)
        {
            var gram = text.Substring(i, 5);
            grams[gram] = grams.GetValueOrDefault(gram) + 1;
        }
        return grams;
    }

    private static ChapterContentDuplicate? Compare(ChapterTreeEntry first, ChapterTreeEntry second, Body a, Body b)
    {
        if (TitleKey(first.Title) != TitleKey(second.Title) || Math.Min(a.Text.Length, b.Text.Length) < 20) return null;
        var exact = a.Text == b.Text;
        double score = exact ? 1 : 0;
        // Short matching boilerplate is not enough evidence for a near-duplicate chapter.
        if (!exact && Math.Min(a.Text.Length, b.Text.Length) >= 100
            && 2d * Math.Min(a.Text.Length - 4, b.Text.Length - 4) / (a.Text.Length + b.Text.Length - 8) >= Threshold)
        {
            var overlap = a.Grams.Sum(pair => Math.Min(pair.Value, b.Grams.GetValueOrDefault(pair.Key)));
            score = 2d * overlap / (a.Text.Length + b.Text.Length - 8);
        }
        return score >= Threshold ? new(first.Id, second.Id, score, exact, a.Lines, b.Lines, a.Text.Length, b.Text.Length) : null;
    }

    public static ChapterContentDuplicate? Compare(ChapterTreeDocument document, ChapterTreeEntry first, ChapterTreeEntry second)
        => Compare(first, second, Read(document, first), Read(document, second));

    public static IReadOnlyList<ChapterContentDuplicate> Find(ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry> entries, CancellationToken cancellationToken = default)
    {
        var result = new List<ChapterContentDuplicate>();
        // Only repeated titles need body reads; unique chapters cost no body comparisons.
        foreach (var group in entries.Where(e => !e.IsFrontMatter).GroupBy(e => TitleKey(e.Title)).Where(g => g.Count() > 1))
        {
            var prior = new List<(ChapterTreeEntry Entry, Body Body)>();
            foreach (var entry in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = Read(document, entry);
                foreach (var candidate in prior)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Compare(candidate.Entry, entry, candidate.Body, body) is not { } match) continue;
                    result.Add(match);
                    break; // One actionable pair per later copy; refresh discovers remaining copies.
                }
                prior.Add((entry, body));
            }
        }
        return result;
    }

    public static IReadOnlyList<ChapterTreeEntry> RemoveCopy(ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry> entries, string removeId, string keepId)
    {
        var remove = entries.Single(e => e.Id == removeId);
        var keep = entries.Single(e => e.Id == keepId);
        if (removeId == keepId || remove.IsFrontMatter || keep.IsFrontMatter || Compare(document, remove, keep) is null)
            throw new InvalidOperationException("当前章节不再满足重复正文条件，请重新检查。");
        var index = entries.ToList().FindIndex(e => e.Id == removeId);
        if (index + 1 < entries.Count && !entries[index + 1].IsFrontMatter && entries[index + 1].Level > remove.Level)
            throw new InvalidOperationException("此章节含有子章节，请先整理层级，不能直接删除整组。");
        var remaining = entries.Where(e => e.Id != removeId).ToArray();
        ChapterTreeDocument.ValidatePlan(document.CreatePlan(remaining), document.LineCount);
        return remaining;
    }
}
