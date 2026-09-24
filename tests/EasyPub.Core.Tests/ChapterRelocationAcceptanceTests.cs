namespace EasyPub.Core.Tests;

public class ChapterRelocationAcceptanceTests
{
    [Fact]
    public Task Explicit_local_tree_relocation_preserves_all_690_bodies_after_project_reopen_and_epub() =>
        P31AcceptanceTests.Verify("S3", 1);

    [Fact]
    public Task Catalog_tree_relocation_preserves_all_690_bodies_after_project_reopen_and_epub() =>
        P31AcceptanceTests.Verify("S3", 2);

    [Fact]
    public void Invalid_targets_and_fragmented_selections_are_refused_without_mutation()
    {
        var doc = EasyPub.Core.ChapterTreeDocument.Load("relocation.txt", System.Text.Encoding.UTF8.GetBytes(
            "第一卷 起点\n第一章 甲\n正文甲\n第二章 乙\n正文乙\n第三章 丙\n正文丙\n第二卷 后续\n第一章 丁\n正文丁"),
            hierarchy: new EasyPub.Core.TocHierarchyOptions { Enabled = true });
        var chapters = doc.Entries.Where(e => !e.IsFrontMatter && e.Level == 2).ToArray();
        var volume = doc.Entries.First(e => !e.IsFrontMatter && e.Level == 1);
        var ids = doc.Entries.Select(e => e.Id).ToArray();
        Assert.Throws<InvalidOperationException>(() => EasyPub.Core.ChapterTreeRelocation.Before(doc, [chapters[0].Id], chapters[0].Id));
        Assert.Throws<InvalidOperationException>(() => EasyPub.Core.ChapterTreeRelocation.Before(doc, [chapters[0].Id, chapters[2].Id], chapters[3].Id));
        Assert.Throws<InvalidOperationException>(() => EasyPub.Core.ChapterTreeRelocation.Before(doc, [volume.Id], chapters[3].Id));
        Assert.Throws<InvalidOperationException>(() => EasyPub.Core.ChapterTreeRelocation.Before(doc, [chapters[0].Id], volume.Id));
        Assert.Throws<InvalidOperationException>(() => EasyPub.Core.ChapterTreeRelocation.Before(doc, ["stale"], chapters[3].Id));
        Assert.Equal(ids, doc.Entries.Select(e => e.Id));
    }
}
