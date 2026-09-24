using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class ReferenceEmptyHeadingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Repeated_empty_headings_keep_the_body_bearing_entry(bool manual)
    {
        const string text = "第一章 起点\n \n第一章 起点\n\n第一章 起点\n这是第一章独有的正文，必须完整保留。\n第二章 继续\n这是第二章的正文。\n";
        var document = ChapterTreeDocument.Load("duplicate-contract.txt", Encoding.UTF8.GetBytes(text));
        document = document.WithEntries([
            new("a", "第一章 起点", 1, true, 1, [new(2, 2)]) { RecognitionSource = manual ? "manual" : null },
            new("b", "第一章 起点", 1, true, 3, [new(4, 4)]),
            new("c", "第一章 起点", 1, true, 5, [new(6, 6)]),
            new("d", "第二章 继续", 1, true, 7, [new(8, document.LineCount)])]);
        var catalog = ReferenceCatalogInput.ParseText("第一章 起点\n第二章 继续")!;
        var plan = ReferencePlanner.Build(document, document.Entries, catalog);
        var action = Assert.Single(plan.OfKind(ReferenceActionKind.RemoveDuplicate));
        if (manual)
        {
            Assert.False(action.Recommended);
            Assert.Equal(1, action.Line);
            return;
        }
        Assert.True(action.Recommended);
        Assert.Equal(5, action.Line);
        Assert.Contains("空标题", action.Detail);
        var dropped = new List<int>();
        var tree = ReferencePlanner.Apply(document, document.Entries, plan, [action], true, false, dropped);
        Assert.Equal(new[] { 1, 2, 3, 4 }, dropped);
        Assert.Equal(new[] { "c", "d" }, tree.Select(e => e.Id));
        Assert.Contains(tree.SelectMany(document.GetSourceLines), line => line.Text == "这是第一章独有的正文，必须完整保留。");
        Assert.Contains(tree.SelectMany(document.GetSourceLines), line => line.Text == "这是第二章的正文。");
    }

    [Fact]
    public void Matching_catalog_occurrences_are_not_treated_as_empty_duplicates_of_each_other()
    {
        var document = ChapterTreeDocument.Load("two-volumes.txt", Encoding.UTF8.GetBytes("第一章 起点\n\n第一章 起点\n正文。\n"));
        document = document.WithEntries([
            new("a", "第一章 起点", 1, true, 1, [new(2, 2)]),
            new("b", "第一章 起点", 1, true, 3, [new(4, document.LineCount)])]);
        var catalog = ReferenceCatalogInput.ParseText("第一卷\n第一章 起点\n第二卷\n第一章 起点")!;
        var plan = ReferencePlanner.Build(document, document.Entries, catalog);
        Assert.Equal(new int?[] { 1, 3 }, plan.Location.Chapters.Select(c => c.Line));
        Assert.Empty(plan.OfKind(ReferenceActionKind.RemoveDuplicate));
    }
}
