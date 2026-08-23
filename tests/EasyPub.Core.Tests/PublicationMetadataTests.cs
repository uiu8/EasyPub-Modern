using System.IO.Compression;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class PublicationMetadataTests
{
    [Fact]
    public async Task Epub_writes_extended_publication_metadata_to_opf()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "小说.txt");
        var output = Path.Combine(directory, "小说.epub");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文");
        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                input,
                output,
                "雨夜",
                "原作者",
                ConversionOptions.LegacyDefault with
                {
                    Metadata = new PublicationMetadata
                    {
                        Translator = "译者甲",
                        Isbn = "978-7-121-15535-2",
                        PublicationDate = new DateOnly(2026, 8, 23),
                        Publisher = "示例出版社",
                        Category = "悬疑 & 推理",
                        Language = "zh-CN",
                        Description = "一段 <不能直接写入 XML> 的简介。",
                    },
                }));

            using var archive = ZipFile.OpenRead(output);
            var opf = await ReadEntryAsync(archive, "OEBPS/content.opf");
            Assert.Contains("<dc:contributor opf:role=\"trl\">译者甲</dc:contributor>", opf);
            Assert.Contains("<dc:identifier opf:scheme=\"ISBN\">978-7-121-15535-2</dc:identifier>", opf);
            Assert.Contains("<dc:date>2026-08-23</dc:date>", opf);
            Assert.Contains("<dc:publisher>示例出版社</dc:publisher>", opf);
            Assert.Contains("<dc:subject>悬疑 &amp; 推理</dc:subject>", opf);
            Assert.Contains("<dc:language>zh-CN</dc:language>", opf);
            Assert.Contains("<dc:description>一段 &lt;不能直接写入 XML&gt; 的简介。</dc:description>", opf);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_isbn_and_language_are_clickable_preflight_warnings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-metadata-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "小说.txt");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文");
        try
        {
            var report = await new ConversionPreflightInspector().InspectAsync([
                new ConversionRequest(
                    input,
                    Path.Combine(directory, "小说.epub"),
                    Options: ConversionOptions.LegacyDefault with
                    {
                        Metadata = new PublicationMetadata { Isbn = "123", Language = "中文" },
                    }),
            ]);

            Assert.False(report.HasErrors);
            Assert.Contains(report.Issues, issue =>
                issue.Code == "isbn_invalid" && issue.Target == PreflightTargetKind.BookInformation);
            Assert.Contains(report.Issues, issue =>
                issue.Code == "language_invalid" && issue.Target == PreflightTargetKind.BookInformation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string path)
    {
        var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == path);
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }
}
