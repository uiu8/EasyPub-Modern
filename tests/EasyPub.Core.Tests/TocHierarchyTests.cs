using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class TocHierarchyTests
{
    [Fact]
    public async Task Epub_writes_visual_and_ncx_three_level_toc()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-toc-{Guid.NewGuid():N}");
        var input = Path.Combine(root, "层级目录.txt");
        var output = Path.Combine(root, "层级目录.epub");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(input,
            "第一卷 风起\n卷序正文\n第一章 雨夜\n章正文\n第一节 来客\n节正文\n第二章 清晨\n清晨正文\n第二卷 余波\n余波正文",
            Encoding.UTF8);
        try
        {
            var options = ConversionOptions.LegacyDefault with
            {
                TocHierarchy = new TocHierarchyOptions { Enabled = true },
            };

            var result = await new EasyPubConverter().ConvertAsync(
                new ConversionRequest(input, output, Options: options));

            Assert.Equal(6, result.ChapterCount);
            using var archive = ZipFile.OpenRead(output);
            var htmlToc = await ReadEntryAsync(archive, "OEBPS/book-toc.html");
            Assert.Contains("class=\"tocl1\"><a href=\"chapter1.html\">第一卷 风起</a>", htmlToc);
            Assert.Contains("class=\"tocl2\"><a href=\"chapter2.html\">第一章 雨夜</a>", htmlToc);
            Assert.Contains("class=\"tocl3\"><a href=\"chapter3.html\">第一节 来客</a>", htmlToc);

            var volumeHtml = await ReadEntryAsync(archive, "OEBPS/chapter1.html");
            var chapterHtml = await ReadEntryAsync(archive, "OEBPS/chapter2.html");
            var sectionHtml = await ReadEntryAsync(archive, "OEBPS/chapter3.html");
            Assert.Contains("<h1 id=\"title\" class=\"titlel1std\">第一卷 风起</h1>", volumeHtml);
            Assert.Contains("<h2 id=\"title\" class=\"titlel2std\">第一章 雨夜</h2>", chapterHtml);
            Assert.Contains("<h3 id=\"title\" class=\"titlel3std\">第一节 来客</h3>", sectionHtml);

            var ncx = XDocument.Parse(await ReadEntryAsync(archive, "OEBPS/toc.ncx"));
            XNamespace ns = "http://www.daisy.org/z3986/2005/ncx/";
            var nodes = ncx.Descendants(ns + "navPoint")
                .ToDictionary(node => (string)node.Attribute("id")!, StringComparer.Ordinal);
            Assert.Equal("chapter1", (string?)nodes["chapter2"].Parent?.Attribute("id"));
            Assert.Equal("chapter2", (string?)nodes["chapter3"].Parent?.Attribute("id"));
            Assert.Equal("chapter1", (string?)nodes["chapter4"].Parent?.Attribute("id"));
            Assert.Equal("navMap", nodes["chapter5"].Parent?.Name.LocalName);
            Assert.Equal("3", ncx.Descendants(ns + "meta").Single(element =>
                (string?)element.Attribute("name") == "dtb:depth").Attribute("content")?.Value);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Custom_hierarchy_patterns_can_create_chapters_not_matched_by_the_main_pattern()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-custom-toc-{Guid.NewGuid():N}");
        var input = Path.Combine(root, "custom.txt");
        var output = Path.Combine(root, "custom.epub");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(input, "PART A\n正文\nCHAPTER 1\n正文\nSECTION 1\n正文", Encoding.UTF8);
        try
        {
            var result = await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                input,
                output,
                Options: ConversionOptions.LegacyDefault with
                {
                    ChapterPattern = @"^不会匹配$",
                    TocHierarchy = new TocHierarchyOptions
                    {
                        Enabled = true,
                        Level1Pattern = @"^PART\s+",
                        Level2Pattern = @"^CHAPTER\s+",
                        Level3Pattern = @"^SECTION\s+",
                    },
                }));

            Assert.Equal(4, result.ChapterCount);
            using var archive = ZipFile.OpenRead(output);
            var toc = await ReadEntryAsync(archive, "OEBPS/book-toc.html");
            Assert.Contains("class=\"tocl1\"><a href=\"chapter1.html\">PART A</a>", toc);
            Assert.Contains("class=\"tocl2\"><a href=\"chapter2.html\">CHAPTER 1</a>", toc);
            Assert.Contains("class=\"tocl3\"><a href=\"chapter3.html\">SECTION 1</a>", toc);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Hierarchical_toc_keeps_a_valid_joint_kindle_mobi()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-toc-mobi-{Guid.NewGuid():N}");
        var input = Path.Combine(root, "层级目录.txt");
        var output = Path.Combine(root, "层级目录.mobi");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(input,
            "第一卷 风起\n卷序正文\n第一章 雨夜\n正文\n第一节 来客\n正文",
            Encoding.UTF8);
        try
        {
            var result = await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                input,
                output,
                "层级目录验证",
                "Codex",
                ConversionOptions.LegacyDefault with
                {
                    TocHierarchy = new TocHierarchyOptions { Enabled = true },
                }));

            Assert.Equal(4, result.ChapterCount);
            var mobi = await File.ReadAllBytesAsync(output);
            AssertValidJointMobi(mobi);
            Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(mobi, 60, 8));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertValidJointMobi(byte[] mobi)
    {
        var recordCount = ReadBigEndianUInt16(mobi, 76);
        var boundaryRecord = -1;
        for (var index = 0; index < recordCount - 1; index++)
        {
            var start = checked((int)ReadBigEndianUInt32(mobi, 78 + index * 8));
            var end = checked((int)ReadBigEndianUInt32(mobi, 86 + index * 8));
            if (end - start == 8 && Encoding.ASCII.GetString(mobi, start, 8) == "BOUNDARY")
            {
                boundaryRecord = index;
                break;
            }
        }
        Assert.True(boundaryRecord >= 0, "联合 MOBI 缺少 BOUNDARY 记录。");

        var record0 = checked((int)ReadBigEndianUInt32(mobi, 78));
        var mobiHeaderLength = checked((int)ReadBigEndianUInt32(mobi, record0 + 20));
        var exth = record0 + 16 + mobiHeaderLength;
        Assert.Equal("EXTH", Encoding.ASCII.GetString(mobi, exth, 4));
        var exthCount = checked((int)ReadBigEndianUInt32(mobi, exth + 8));
        var cursor = exth + 12;
        int? declaredKf8Record = null;
        for (var index = 0; index < exthCount; index++)
        {
            var type = ReadBigEndianUInt32(mobi, cursor);
            var length = checked((int)ReadBigEndianUInt32(mobi, cursor + 4));
            if (type == 121 && length >= 12)
                declaredKf8Record = checked((int)ReadBigEndianUInt32(mobi, cursor + 8));
            cursor += length;
        }
        Assert.Equal(boundaryRecord + 1, declaredKf8Record);
    }

    private static ushort ReadBigEndianUInt16(byte[] bytes, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string path)
    {
        var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == path);
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }
}
