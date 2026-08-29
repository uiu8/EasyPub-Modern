using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class MobiContentPackagerTests
{
    [Fact]
    public async Task Optimized_package_keeps_every_logical_toc_entry_and_reduces_physical_documents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-mobi-pack-{Guid.NewGuid():N}");
        var input = Path.Combine(root, "长篇测试.txt");
        var epub = Path.Combine(root, "长篇测试.epub");
        var expanded = Path.Combine(root, "expanded");
        Directory.CreateDirectory(root);
        var text = new StringBuilder("序言正文\n");
        for (var chapter = 1; chapter <= 25; chapter++)
            text.Append($"第{chapter}章 测试{chapter}\n这一章的唯一正文标记为 TEST-{chapter:000}。\n");
        await File.WriteAllTextAsync(input, text.ToString(), Encoding.UTF8);

        try
        {
            var result = await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                input,
                epub,
                Options: new ConversionOptions
                {
                    TocHierarchy = new TocHierarchyOptions { IncludeHtmlTocPage = true },
                }));
            ZipFile.ExtractToDirectory(epub, expanded);
            var oebps = Path.Combine(expanded, "OEBPS");

            var packing = MobiContentPackager.Optimize(oebps);

            Assert.Equal(result.ChapterCount - 1, packing.LogicalChapterCount);
            Assert.Equal(3, packing.PhysicalDocumentCount);
            Assert.Equal(10, packing.MaximumChaptersPerDocument);
            Assert.True(File.Exists(Path.Combine(oebps, "chapter0.html")));
            Assert.Empty(Directory.EnumerateFiles(oebps, "chapter1.html"));
            Assert.Equal(3, Directory.EnumerateFiles(oebps, "chapter-pack-*.html").Count());

            var ncx = XDocument.Load(Path.Combine(oebps, "toc.ncx"));
            XNamespace ncxNamespace = "http://www.daisy.org/z3986/2005/ncx/";
            var logicalTargets = ncx.Descendants(ncxNamespace + "content")
                .Select(element => (string?)element.Attribute("src"))
                .Where(value => value?.Contains("#chapter", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Equal(25, logicalTargets.Length);
            Assert.Equal(25, logicalTargets.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("chapter-pack-0001.html#chapter1", logicalTargets);
            Assert.Contains("chapter-pack-0003.html#chapter25", logicalTargets);

            var htmlToc = XDocument.Load(Path.Combine(oebps, "book-toc.html"));
            XNamespace xhtml = "http://www.w3.org/1999/xhtml";
            var visualTargets = htmlToc.Descendants(xhtml + "a")
                .Select(element => (string?)element.Attribute("href"))
                .Where(value => value?.Contains("#chapter", StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Equal(25, visualTargets.Length);

            var firstPack = XDocument.Load(Path.Combine(oebps, "chapter-pack-0001.html"));
            var identifiers = firstPack.Descendants()
                .Select(element => (string?)element.Attribute("id"))
                .Where(value => value is not null)
                .Cast<string>()
                .ToArray();
            Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("chapter1", identifiers);
            Assert.Contains("chapter10", identifiers);
            Assert.Contains(firstPack.Descendants(xhtml + "div"), element =>
                (string?)element.Attribute("id") == "chapter2" &&
                (string?)element.Attribute("style") == "page-break-before: always;");

            var opf = XDocument.Load(Path.Combine(oebps, "content.opf"));
            XNamespace opfNamespace = "http://www.idpf.org/2007/opf";
            Assert.Equal(3, opf.Descendants(opfNamespace + "item")
                .Count(element => ((string?)element.Attribute("id"))?.StartsWith("chapter-pack-", StringComparison.Ordinal) == true));
            Assert.Equal(3, opf.Descendants(opfNamespace + "itemref")
                .Count(element => ((string?)element.Attribute("idref"))?.StartsWith("chapter-pack-", StringComparison.Ordinal) == true));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Optimized_text_conversion_produces_a_valid_joint_mobi()
    {
        var workspace = FindWorkspaceRoot();
        var root = Path.Combine(Path.GetTempPath(), $"easypub-mobi-pack-output-{Guid.NewGuid():N}");
        var input = Path.Combine(root, "真机目录测试.txt");
        var output = Path.Combine(root, "真机目录测试.mobi");
        Directory.CreateDirectory(root);
        var text = new StringBuilder("序言正文\n");
        for (var chapter = 1; chapter <= 25; chapter++)
            text.Append($"第{chapter}章 测试{chapter}\n本章唯一标记：VERIFY-{chapter:000}\n");
        await File.WriteAllTextAsync(input, text.ToString(), Encoding.UTF8);

        try
        {
            var kindleGen = Path.Combine(workspace, "work", "easypub-compat", "legacy-capture", "bin", "kindlegen_v2.9.exe");
            var options = ConversionOptions.LegacyDefault with
            {
                Mobi = new MobiOptions
                {
                    KindleGenPath = kindleGen,
                    OptimizeContentPackaging = true,
                },
            };

            var result = await new EasyPubConverter().ConvertAsync(
                new ConversionRequest(input, output, "真机目录测试", "Codex", options));
            var bytes = await File.ReadAllBytesAsync(output);

            Assert.Equal(26, result.ChapterCount);
            Assert.True(LegacyMobiPostProcessor.HasValidJointStructure(bytes));
            Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(bytes, 60, 8));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string FindWorkspaceRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "EasyPub.Modern.slnx"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("找不到 EasyPub 工作区。");
    }
}
