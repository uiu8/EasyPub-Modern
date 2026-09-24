using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

public class ChapterReviewPresentationTests
{
    private static ChapterReviewGroup Group(string code, params string[] ids)
    {
        var issue = new ConversionPreflightIssue("test.txt", PreflightSeverity.Warning, code, code,
            PreflightTargetKind.Chapters, 10);
        return new(issue, ReviewCategories.Duplicate, ids, [10, 20], [issue]);
    }

    [Fact]
    public void Fold_only_actual_pairs_keep_gaps_and_other_pair_assignments()
    {
        var summary = Group("chapter_repeated_sequence", "a", "b", "c", "d", "e", "f");
        var pair = Group("chapter_content_duplicate", "b", "a");
        var unrelated = Group("chapter_content_duplicate", "a", "d");
        var gap = Group("chapter_number_gap", "b");
        var result = ChapterReviewPresentation.CollapseSequences(new([summary, pair, unrelated, gap], 4), 200);
        Assert.Equal(3, result.TotalGroups);
        Assert.Contains(unrelated, result.Groups);
        Assert.Contains(gap, result.Groups);
        Assert.Contains(pair.Issue, result.Groups[0].RelatedIssues);
        Assert.Equal(summary.NodeIds, result.Groups[0].NodeIds);
        Assert.Single(summary.RelatedIssues); // Input evidence remains immutable.
    }

    [Fact]
    public void Folding_precedes_paging_and_loading_more_preserves_summary()
    {
        var fillers = Enumerable.Range(0, 200).Select(i => Group("chapter_number_gap", "node" + i)).ToArray();
        var pair = Group("chapter_content_duplicate", "a", "b");
        var summary = Group("chapter_repeated_sequence", "a", "b", "c", "d", "e", "f");
        var source = new ChapterReviewAnalysis([pair, .. fillers, summary], 202);
        var first = ChapterReviewPresentation.CollapseSequences(source, 200);
        var more = ChapterReviewPresentation.CollapseSequences(source, 400);
        Assert.Equal(201, first.TotalGroups);
        Assert.Equal(200, first.Groups.Count);
        Assert.Equal(201, more.Groups.Count);
        Assert.Contains(more.Groups, g => g.Issue.Code == "chapter_repeated_sequence");
        Assert.DoesNotContain(more.Groups, g => g.Issue.Code == "chapter_content_duplicate");
    }

    [Fact]
    public void Remaining_pairs_reappear_when_a_shortened_sequence_no_longer_has_a_summary()
    {
        var pair = Group("chapter_content_duplicate", "a", "b");
        var result = ChapterReviewPresentation.CollapseSequences(new([pair], 1), 200);
        Assert.Same(pair, Assert.Single(result.Groups));
    }
}
