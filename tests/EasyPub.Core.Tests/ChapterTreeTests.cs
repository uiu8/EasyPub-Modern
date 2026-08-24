using System.IO.Compression;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ChapterTreeTests
{
    [Fact]
    public async Task Chapters_without_a_volume_are_root_l1_nodes_not_orphan_l2_nodes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-tree-depth-{Guid.NewGuid():N}.txt");
        var output = Path.ChangeExtension(path, ".epub");
        await File.WriteAllTextAsync(path, "书籍信息\n第一章 开始\n正文\n第二章 继续\n正文");
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(
                path,
                hierarchy: new TocHierarchyOptions { Enabled = true });
            var chapters = document.Entries.Where(entry => !entry.IsFrontMatter).ToArray();

            Assert.Equal(2, chapters.Length);
            Assert.All(chapters, chapter => Assert.Equal(1, chapter.Level));
            Assert.All(chapters, chapter => Assert.Equal(2, chapter.HeadingLevel));

            await new EasyPubConverter().ConvertAsync(new ConversionRequest(path, output)
            {
                ChapterTree = document.CreatePlan(document.Entries),
            });
            using var archive = ZipFile.OpenRead(output);
            Assert.Contains("class=\"tocl1\"", ReadText(archive, "OEBPS/book-toc.html"));
            Assert.Contains("<h2", ReadText(archive, "OEBPS/chapter1.html"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Chapter_tree_reorders_levels_and_excludes_only_the_toc_entry()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "book.txt");
        var output = Path.Combine(directory, "book.epub");
        await File.WriteAllTextAsync(input, "开场\n第一卷 上部\n卷正文\n第一章 雨夜\n章节正文\n第二章 清晨\n结尾");
        try
        {
            var hierarchy = new TocHierarchyOptions { Enabled = true };
            var document = await ChapterTreeDocument.LoadAsync(input, hierarchy: hierarchy);
            Assert.Equal(4, document.Entries.Count);
            Assert.True(document.Entries[0].IsFrontMatter);
            Assert.Equal(2, document.Entries[0].Level);
            Assert.Equal(1, document.Entries[1].Level);
            Assert.Equal(2, document.Entries[2].Level);
            var edited = new[]
            {
                document.Entries[0],
                document.Entries[1],
                document.Entries[3] with { Title = "第二章 新标题", Level = 2 },
                document.Entries[2] with { IncludeInToc = false },
            };
            var request = new ConversionRequest(
                input,
                output,
                Options: new ConversionOptions { TocHierarchy = hierarchy })
            {
                ChapterTree = document.CreatePlan(edited),
            };
            var result = await new EasyPubConverter().ConvertAsync(request);

            Assert.Equal(4, result.ChapterCount);
            using var archive = ZipFile.OpenRead(output);
            var htmlToc = ReadText(archive, "OEBPS/book-toc.html");
            var ncx = ReadText(archive, "OEBPS/toc.ncx");
            var lastChapter = ReadText(archive, "OEBPS/chapter3.html");
            Assert.Contains("第二章 新标题", htmlToc);
            Assert.DoesNotContain("第一章 雨夜", htmlToc);
            Assert.DoesNotContain("第一章 雨夜", ncx);
            Assert.Contains("第一章 雨夜", lastChapter);
            var ncxDocument = System.Xml.Linq.XDocument.Parse(ncx);
            var rootLabels = ncxDocument.Descendants()
                .Single(element => element.Name.LocalName == "navMap")
                .Elements()
                .Where(element => element.Name.LocalName == "navPoint")
                .Select(element => element.Descendants().First(node => node.Name.LocalName == "text").Value)
                .ToArray();
            Assert.Contains("序", rootLabels);
            Assert.Contains("第一卷 上部", rootLabels);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Chapter_tree_is_rejected_after_source_changes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-tree-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n正文");
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var plan = document.CreatePlan(document.Entries);
            await File.AppendAllTextAsync(path, "\n变化");
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                ChapterTreeDocument.LoadAsync(path, existingPlan: plan));
            Assert.Contains("发生变化", exception.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Version_016_front_matter_is_migrated_from_l1_to_a_front_root()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-tree-migrate-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "书籍信息\n第一章 开始\n正文");
        try
        {
            var current = await ChapterTreeDocument.LoadAsync(path);
            var legacyEntries = current.Entries.ToArray();
            legacyEntries[0] = legacyEntries[0] with { Level = 1, IsFrontMatter = false };
            legacyEntries[1] = legacyEntries[1] with { Level = 2, HeadingLevel = 0 };
            var legacyPlan = current.CreatePlan(current.Entries) with { Entries = legacyEntries };

            var migrated = await ChapterTreeDocument.LoadAsync(path, existingPlan: legacyPlan);

            Assert.True(migrated.Entries[0].IsFrontMatter);
            Assert.Equal(2, migrated.Entries[0].Level);
            Assert.Equal(1, migrated.Entries[1].Level);
            Assert.Equal(2, migrated.Entries[1].HeadingLevel);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string ReadText(ZipArchive archive, string path)
    {
        using var reader = new StreamReader(archive.GetEntry(path)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
