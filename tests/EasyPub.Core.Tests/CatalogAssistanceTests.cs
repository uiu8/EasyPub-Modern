using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class CatalogAssistanceTests
{
    [Fact]
    public async Task Local_parts_and_reference_titles_split_without_losing_source_or_restoring_omissions()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var source = "第一章 原章\n\n普通正文。\n\n吸血惊情（上）\n\n上篇正文。\n\n吸血惊情（下）\n\n下篇正文。\n\n见了我就不要跑\n\n最后正文。\n第二章 已删除\n不应恢复。";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var doc = await ChapterTreeDocument.LoadAsync(path);
            var entries = doc.Entries.Where(e => e.Title != "第二章 已删除").ToArray();
            var proposals = UnnumberedHeadings.Find(doc, entries, 5, ["第三章 见了我就不要跑"]);
            Assert.Equal(new[] { 5, 9, 13 }, proposals.Select(p => p.Line));
            var repaired = UnnumberedHeadings.Apply(doc, entries, proposals, true);
            Assert.Contains(repaired, e => e.Title == "第三章 见了我就不要跑");
            Assert.DoesNotContain(repaired, e => e.Title == "第二章 已删除");
            static IEnumerable<int> Coverage(IEnumerable<ChapterTreeEntry> items) => items.SelectMany(e =>
                (e.TitleLineNumber is int line ? new[] { line } : Array.Empty<int>()).Concat(e.ContentRanges.SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1))));
            Assert.Equal(Coverage(entries), Coverage(repaired));
            Assert.Equal(source, await File.ReadAllTextAsync(path));
            Assert.Empty(UnnumberedHeadings.Find(doc, entries, 200));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Catalog_parser_keeps_source_order_and_does_not_download_bodies()
    {
        const string html = "<title>测试书 - 作者</title><a href='/reader/11'>第三章 后文</a><a href='/reader/12'>第一章 前文</a><a href='/reader/13'>第一章 前文（下）</a><a href='/reader/13'>重复导航</a><a href='https://evil.test/reader/14'>正文</a>";
        var catalog = ReferenceCatalogClient.Parse(new Uri("https://fanqienovel.com/page/123"), html);
        Assert.Equal(new[] { "第三章 后文", "第一章 前文", "第一章 前文（下）" }, catalog.Titles);
        Assert.Equal("测试书 - 作者", catalog.PageTitle);
        Assert.False(ReferenceCatalogClient.Supported(new Uri("https://127.0.0.1/page/1")));
        Assert.False(ReferenceCatalogClient.Supported(new Uri("https://192.168.1.10/book/1")));
        Assert.False(ReferenceCatalogClient.Supported(new Uri("https://[::1]/book/1")));
        Assert.False(ReferenceCatalogClient.Supported(new Uri("https://user:pass@example.com/book/1")));
        // Plain HTTP is accepted because many novel sites are HTTP-only; anonymity of the target is
        // what matters, not the scheme.
        Assert.True(ReferenceCatalogClient.Supported(new Uri("http://example.com/book/1")));
        Assert.True(ReferenceCatalogClient.Supported(new Uri("https://any-public-site.example/book/1")));
        Assert.Throws<InvalidOperationException>(() => ReferenceCatalogClient.Parse(new Uri("https://www.qidian.com/book/1/"), "<script>challenge</script>"));
    }
}
