using EasyPub.Desktop;

namespace EasyPub.Desktop.Tests;

public sealed class LayoutPreviewPaginatorTests
{
    [Fact]
    public void Leading_full_width_indent_is_preserved()
    {
        var pages = LayoutPreviewPaginator.Paginate("第一章 测试", ["　　正文第一段。"], 280, 400, 16, 28);

        Assert.StartsWith("　　正文", pages[0].Body);
    }

    [Fact]
    public void Long_chapter_is_split_into_ordered_turnable_pages()
    {
        var paragraphs = Enumerable.Range(1, 40).Select(index => $"段落{index:D2}：" + new string('文', 48)).ToArray();

        var pages = LayoutPreviewPaginator.Paginate("第一章 测试", paragraphs, 280, 400, 16, 28);

        Assert.True(pages.Count > 2);
        Assert.Equal("第一章 测试", pages[0].Title);
        Assert.All(pages.Skip(1), page => Assert.Null(page.Title));
        var combined = string.Concat(pages.Select(page => page.Body.Replace("\n", string.Empty)));
        Assert.Contains("段落01", combined);
        Assert.Contains("段落40", combined);
    }
}
