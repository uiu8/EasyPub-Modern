using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>Display-only folding. Diagnostics and repair plans retain their original evidence.</summary>
internal static class ChapterReviewPresentation
{
    internal static ChapterReviewAnalysis CollapseSequences(ChapterReviewAnalysis analysis, int limit)
    {
        var owners = new Dictionary<(string, string), ChapterReviewGroup>();
        foreach (var sequence in analysis.Groups.Where(g => g.Issue.Code == "chapter_repeated_sequence"))
            for (var i = 0; i + 1 < sequence.NodeIds.Count; i += 2)
                owners.TryAdd(Key(sequence.NodeIds[i], sequence.NodeIds[i + 1]), sequence);

        var nested = new Dictionary<ChapterReviewGroup, List<ConversionPreflightIssue>>();
        var folded = new HashSet<ChapterReviewGroup>();
        foreach (var pair in analysis.Groups)
        {
            if (pair.Issue.Code != "chapter_content_duplicate" || pair.NodeIds.Count != 2
                || !owners.TryGetValue(Key(pair.NodeIds[0], pair.NodeIds[1]), out var owner)) continue;
            if (!nested.TryGetValue(owner, out var evidence)) nested[owner] = evidence = [];
            evidence.AddRange(pair.RelatedIssues);
            folded.Add(pair);
        }
        var groups = analysis.Groups.Where(g => !folded.Contains(g)).Select(g =>
            nested.TryGetValue(g, out var evidence)
                ? g with { RelatedIssues = g.RelatedIssues.Concat(evidence).Distinct().ToArray() }
                : g).ToArray();
        return new(groups.Take(Math.Max(1, limit)).ToArray(), groups.Length);
    }

    private static (string, string) Key(string first, string second) =>
        string.CompareOrdinal(first, second) <= 0 ? (first, second) : (second, first);
}
