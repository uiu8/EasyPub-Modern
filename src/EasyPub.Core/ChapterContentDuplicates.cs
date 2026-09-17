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
        if (TitleKey(first.Title) != TitleKey(second.Title)) return null;
        if (Math.Min(a.Text.Length, b.Text.Length) < 20) return null;
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

    /// <summary>
    /// The same body under a different heading. <see cref="Find"/> only compares chapters that share a
    /// title, which is the shape a duplicated block usually takes — but a release that repeats a
    /// stretch and renumbers it produces identical prose under different headings, and that pair is
    /// invisible to a same-title comparison while being just as removable.
    ///
    /// <para>
    /// This runs over every chapter rather than over repeated titles, so it is bounded on purpose. An
    /// earlier version bucketed chapters by "same length plus a few sampled characters" and compared
    /// every pair inside a bucket; chapters of similar length land in the same bucket, so a book whose
    /// chapters are all about the same size produced a quadratic number of gram comparisons, each
    /// building a dictionary five times the size of the text. That is a memory problem first and a
    /// speed problem second, and it is why the buckets below are exact-content groups with a hard size
    /// ceiling instead of "looks similar" groups.
    /// </para>
    /// </summary>
    /// <param name="maximumGroupSize">
    /// A group larger than this is skipped rather than compared. Groups are formed by identical body
    /// text, so a huge one means hundreds of chapters share one body verbatim — a fact worth reporting
    /// once, not a reason to perform hundreds of thousands of comparisons.
    /// </param>
    /// <param name="maximumChapters">
    /// Above this many chapters the check is skipped entirely. It reads every body once, which measures
    /// 241 ms on an 8000-chapter file against 5 ms for the same-title comparison — and this runs inside
    /// the analysis path that the pre-conversion check times against a 400 ms budget. The comparison is
    /// quadratic in the worst case and linear-with-a-large-constant in the common one, so it is bounded
    /// rather than left to grow with the book. Books past the limit still get every other check; only
    /// this one — the same prose under a *different* title — goes unreported.
    /// </param>
    public static IReadOnlyList<CrossTitleDuplicate> FindCrossTitle(ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry> entries, int maximumGroupSize = 32, int maximumChapters = 3_000,
        CancellationToken cancellationToken = default)
    {
        if (maximumGroupSize < 2) return [];
        var chapters = entries.Where(entry => !entry.IsFrontMatter && entry.TitleLineNumber is not null).ToArray();
        if (chapters.Length > maximumChapters) return [];
        // One pass over the bodies: group by the text itself, so the pair that matters — identical prose
        // — is grouped exactly, and no bucket ever holds chapters that merely resemble each other.
        var bodies = new Dictionary<int, (ChapterTreeEntry Entry, Body Body)>();
        var groups = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var entry in chapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = Read(document, entry);
            if (body.Text.Length < 20) continue;
            bodies[entry.TitleLineNumber!.Value] = (entry, body);
            if (!groups.TryGetValue(body.Text, out var group)) groups[body.Text] = group = [];
            group.Add(entry.TitleLineNumber!.Value);
        }

        var result = new List<CrossTitleDuplicate>();
        var reported = new HashSet<(int First, int Second)>();
        foreach (var group in groups.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Count < 2 || group.Count > maximumGroupSize) continue;
            var lines = group.Order().ToArray();
            for (var i = 0; i < lines.Length; i++)
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var first = bodies[lines[i]];
                    var second = bodies[lines[j]];
                    if (TitleKey(first.Entry.Title) == TitleKey(second.Entry.Title)) continue;
                    if (!reported.Add((lines[i], lines[j]))) continue;
                    result.Add(new CrossTitleDuplicate(first.Entry.Title, second.Entry.Title,
                        lines[i], lines[j], 1.0, true));
                }
        }
        return result;
    }

    public sealed record CrossTitleDuplicate(string FirstTitle, string SecondTitle, int? FirstLine, int? SecondLine,
        double Similarity, bool Exact);
}
