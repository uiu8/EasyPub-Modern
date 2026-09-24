using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class ChapterContentSplitTests
{
    [Theory]
    [InlineData(4, "10,11,12,2,3", "4,5,20,21")]
    [InlineData(2, "10,11,12", "2,3,4,5,20,21")]
    [InlineData(12, "10,11", "12,2,3,4,5,20,21")]
    [InlineData(21, "10,11,12,2,3,4,5,20", "21")]
    public void Split_preserves_reading_order_and_every_body_line(int line, string before, string after)
    {
        ChapterSourceRange[] ranges = [new(10, 12), new(2, 5), new(20, 21)];
        var split = Assert.IsType<ChapterContentSplit>(ChapterContentSplit.At(ranges, line));
        Assert.Equal(before, string.Join(",", Lines(split.Before)));
        Assert.Equal(after, string.Join(",", Lines(split.After)));
        Assert.Equal(Lines(ranges), Lines(split.Before).Concat(Lines(split.After)));
        Assert.Equal(line, Lines(split.After).First());
        Assert.Equal(new ChapterSourceRange[] { new(10, 12), new(2, 5), new(20, 21) }, ranges);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(22)]
    public void Rejects_first_body_line_or_line_outside_ranges(int line)
        => Assert.Null(ChapterContentSplit.At([new(10, 12), new(2, 5), new(20, 21)], line));

    [Fact]
    public void Empty_body_cannot_be_split() => Assert.Null(ChapterContentSplit.At([], 1));

    private static IEnumerable<int> Lines(IEnumerable<ChapterSourceRange> ranges)
        => ranges.SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1));
}
