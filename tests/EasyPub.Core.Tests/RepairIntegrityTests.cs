using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class RepairIntegrityTests
{
    [Fact]
    public void Numeric_colon_headings_and_punctuation_are_valid_titles()
    {
        var location=ReferenceLocator.Locate(["001：开始！","正文","002：继续？","正文"],Catalog("001：开始！","002：继续？"));
        Assert.Equal(0,location.Missing);
        Assert.Equal(3,location.Chapters[1].Line);
    }
    [Fact]
    public void A_chapter_number_mentioned_in_prose_is_not_a_heading()
    {
        var location=ReferenceLocator.Locate(["第一章 开始","正文一","他提到第三章 消失的秘密，然后继续聊天。","正文二"],
            Catalog("第一章 开始","第三章 消失的秘密"));
        Assert.Null(location.Chapters[1].Line);
    }
    [Fact]
    public void Similar_titles_with_different_numbers_are_not_duplicate_occurrences()
    {
        var catalog=Catalog("第一章 回家","第二章 回家");
        var location=ReferenceLocator.Locate(["第一章 回家","","正文一","","第二章 回家","","正文二"],catalog);
        Assert.DoesNotContain(location.Chapters,c=>c.IsDuplicate);
    }
    [Fact]
    public async Task A_title_in_another_volume_cannot_hide_a_missing_chapter()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".txt");
        try
        {
            await File.WriteAllTextAsync(path,"第一章 相同标题\n\n正文\n");
            var d=await ChapterTreeDocument.LoadAsync(path);
            var catalog=new ReferenceCatalog("test","测试",[
                new("第一卷",ReferenceNodeKind.Volume,null,"第一卷"),
                new("第一章 相同标题",ReferenceNodeKind.Chapter,null,"第一卷"),
                new("第二卷",ReferenceNodeKind.Volume,null,"第二卷"),
                new("第一章 相同标题",ReferenceNodeKind.Chapter,null,"第二卷")]);
            var outcome=ChapterAutoRepair.Prepare(d,catalog);
            Assert.Equal(1,outcome.Missing);
            Assert.Single(outcome.MissingTitles);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task No_catalog_keeps_current_tree_and_front_matter_unchanged()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".txt");
        try
        {
            await File.WriteAllTextAsync(path,"前言正文\n第一章 开始\n正文\n第十章 继续\n更多正文\n第一章 重启\n尾声");
            var d=await ChapterTreeDocument.LoadAsync(path);
            var outcome=ChapterAutoRepair.Prepare(d,null);
            Assert.Equal(d.Entries,outcome.Entries);
            Assert.False(outcome.VolumesInferred);
            Assert.Null(outcome.BackupPath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Pasted_catalog_preserves_volumes_and_chapter_membership()
    {
        var catalog=ReferenceCatalogInput.ParseText("第一卷 开始\n第一章 起点\n第二卷 归来\n第一章 再会")!;
        Assert.Equal(2,catalog.VolumeTitles.Count);
        Assert.Equal(2,catalog.Titles.Count);
        Assert.Equal("第一章 再会",Assert.Single(catalog.ChaptersOf("第二卷 归来")).Title);
    }

    [Fact]
    public void Downloader_record_requires_exact_book_identity()
    {
        var folder=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());
        var id="7143038691944959011";
        Directory.CreateDirectory(Path.Combine(folder,id));
        try
        {
            File.WriteAllText(Path.Combine(folder,id,"status.json"),"{\"book_id\":\""+id+"\",\"book_name\":\"十日终焉\"}");
            Assert.Equal("https://fanqienovel.com/page/"+id,ReferenceCatalogInput.FindLocalBookUrl(Path.Combine(folder,"十日终焉.txt")));
            Assert.Null(ReferenceCatalogInput.FindLocalBookUrl(Path.Combine(folder,"十日终焉续篇.txt")));
        }
        finally { Directory.Delete(folder,true); }
    }
    private static ReferenceCatalog Catalog(params string[] titles) => new("test", "测试书",
        titles.Select(t => new ReferenceNode(t, ReferenceNodeKind.Chapter, null, null)).ToArray());

    private static HashSet<int> Coverage(IEnumerable<ChapterTreeEntry> entries) => entries
        .SelectMany(e => e.ContentRanges.SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine-r.StartLine+1))
            .Concat(e.TitleLineNumber is int line ? [line] : Array.Empty<int>())).ToHashSet();

    [Fact]
    public async Task Demoting_an_unlisted_heading_preserves_every_source_line()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".txt");
        try
        {
            await File.WriteAllTextAsync(path, "第一章 开始\n正文\n作者的话\n不能丢失的说明\n第二章 继续\n正文二");
            var d = await ChapterTreeDocument.LoadAsync(path);
            ChapterTreeEntry[] entries = [new("a","第一章 开始",1,true,1,[new(2,2)]),
                new("b","作者的话",1,true,3,[new(4,4)]), new("c","第二章 继续",1,true,5,[new(6,6)])];
            var p = ReferencePlanner.Build(d, entries, Catalog("第一章 开始","第二章 继续"));
            var result = ReferencePlanner.Apply(d, entries, p, p.DefaultSelection.ToArray(),true);
            Assert.Equal(Coverage(entries).Order(), Coverage(result).Order());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Previously_deleted_content_is_not_resurrected()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".txt");
        try
        {
            await File.WriteAllTextAsync(path, "第一章 开始\n正文\n第二章 已删除\n不要恢复\n第三章 继续\n正文三");
            var d = await ChapterTreeDocument.LoadAsync(path);
            ChapterTreeEntry[] entries = [new("a","第一章 开始",1,true,1,[new(2,2)]),
                new("c","第三章 继续",1,true,5,[new(6,6)])];
            var p = ReferencePlanner.Build(d, entries, Catalog("第一章 开始","第二章 已删除","第三章 继续"));
            var result = ReferencePlanner.Apply(d, entries, p, p.DefaultSelection.ToArray(),true);
            Assert.Equal(Coverage(entries).Order(), Coverage(result).Order());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Removed_line_manifest_includes_duplicate_body()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".txt");
        try
        {
            await File.WriteAllTextAsync(path,"第一章 开始\n正文\n\n第二章 继续\n同一正文\n\n第二章 继续\n同一正文\n\n第三章 结束\n尾声");
            var d=await ChapterTreeDocument.LoadAsync(path);
            var p=ReferencePlanner.Build(d,d.Entries,Catalog("第一章 开始","第二章 继续","第三章 结束"));
            var removed=new List<int>();
            var result=ReferencePlanner.Apply(d,d.Entries,p,p.DefaultSelection.ToArray(),true,false,removed);
            Assert.Equal(Coverage(d.Entries).Except(Coverage(result)).Order(),removed.Order());
            Assert.Contains(8,removed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Different_bodies_with_the_same_heading_are_not_automatically_deleted()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".txt");
        try
        {
            await File.WriteAllTextAsync(path,"第一章 开始\n正文\n\n第二章 继续\n前半段剧情\n\n第二章 继续\n后半段不同剧情\n\n第三章 结束\n尾声");
            var d=await ChapterTreeDocument.LoadAsync(path);
            var p=ReferencePlanner.Build(d,d.Entries,Catalog("第一章 开始","第二章 继续","第三章 结束"));
            Assert.DoesNotContain(p.DefaultSelection,a=>a.Kind==ReferenceActionKind.RemoveDuplicate);
        }
        finally { File.Delete(path); }
    }
}
