using System.IO.Compression;
using System.Text;
using EasyPub.Core;
using SkiaSharp;

namespace EasyPub.Core.Tests;

public sealed class IllustrationTests
{
    [Fact]
    public async Task Epub_embeds_webp_illustration_and_replaces_its_standalone_marker()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-illustration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "book.txt");
        var imagePath = Path.Combine(directory, "night.webp");
        var outputPath = Path.Combine(directory, "book.epub");
        await File.WriteAllTextAsync(
            inputPath,
            "第一章 雨夜\r\n第一段。\r\n[[插图:夜雨]]\r\n第二段。\r\n");
        using (var bitmap = new SKBitmap(7, 5))
        {
            bitmap.Erase(new SKColor(20, 40, 90));
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Webp, 100);
            await File.WriteAllBytesAsync(imagePath, encoded.ToArray());
        }

        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                inputPath,
                outputPath,
                Options: new ConversionOptions
                {
                    Illustrations = [new BookIllustration("夜雨", imagePath, "雨夜插图")],
                }));

            using var archive = ZipFile.OpenRead(outputPath);
            var imageEntry = Assert.Single(
                archive.Entries,
                candidate => candidate.FullName == "OEBPS/illustrations/illustration-001.jpg");
            byte[] imageBytes;
            using (var stream = imageEntry.Open())
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory);
                imageBytes = memory.ToArray();
            }
            Assert.Equal([0xFF, 0xD8, 0xFF], imageBytes[..3]);
            using var jpeg = SKBitmap.Decode(imageBytes);
            Assert.Equal(7, jpeg.Width);
            Assert.Equal(5, jpeg.Height);

            var chapter = await ReadTextEntryAsync(archive, "OEBPS/chapter1.html");
            Assert.Contains(
                "<div class=\"illustration\"><img class=\"body-illustration\" src=\"illustrations/illustration-001.jpg\" alt=\"雨夜插图\"/></div>",
                chapter);
            Assert.DoesNotContain("[[插图:夜雨]]", chapter);

            var opf = await ReadTextEntryAsync(archive, "OEBPS/content.opf");
            Assert.Contains(
                "<item id=\"illustration-001\" href=\"illustrations/illustration-001.jpg\" media-type=\"image/jpeg\"/>",
                opf);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Epub_can_insert_illustration_after_a_selected_source_line_without_modifying_txt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-positioned-illustration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "book.txt");
        var imagePath = Path.Combine(directory, "scene.jpg");
        var outputPath = Path.Combine(directory, "book.epub");
        const string originalText = "第一章 开始\r\n第一段。\r\n第二段。\r\n";
        await File.WriteAllTextAsync(inputPath, originalText);
        using (var bitmap = new SKBitmap(5, 3))
        {
            bitmap.Erase(new SKColor(30, 70, 110));
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
            await File.WriteAllBytesAsync(imagePath, encoded.ToArray());
        }

        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                inputPath,
                outputPath,
                Options: new ConversionOptions
                {
                    AddFullWidthIndent = false,
                    Illustrations = [new BookIllustration("场景", imagePath, "场景图", InsertAfterLine: 2)],
                }));

            using var archive = ZipFile.OpenRead(outputPath);
            var chapter = await ReadTextEntryAsync(archive, "OEBPS/chapter1.html");
            var firstParagraph = chapter.IndexOf("<p class=\"a\">第一段。</p>", StringComparison.Ordinal);
            var illustration = chapter.IndexOf("class=\"body-illustration\"", StringComparison.Ordinal);
            var secondParagraph = chapter.IndexOf("<p class=\"a\">第二段。</p>", StringComparison.Ordinal);
            Assert.True(firstParagraph < illustration && illustration < secondParagraph);
            Assert.Equal(originalText, await File.ReadAllTextAsync(inputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Batch_requests_keep_each_novels_illustrations_independent()
    {
        var first = new[] { new BookIllustration("图1", @"C:\images\one.webp") };
        var second = new[] { new BookIllustration("图2", @"C:\images\two.png") };

        var requests = BatchConversionRequestFactory.Create(
            [
                new BookConversionSource(@"C:\books\one.txt", Illustrations: first),
                new BookConversionSource(@"C:\books\two.txt", Illustrations: second),
            ],
            Path.GetTempPath(),
            "mobi",
            null,
            ConversionOptions.LegacyDefault);

        Assert.Same(first, requests[0].Options!.Illustrations);
        Assert.Same(second, requests[1].Options!.Illustrations);
    }

    [Fact]
    public async Task Mobi_embeds_body_illustration_without_losing_joint_kindle_structure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-mobi-illustration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "book.txt");
        var imagePath = Path.Combine(directory, "scene.png");
        var outputPath = Path.Combine(directory, "book.mobi");
        await File.WriteAllTextAsync(inputPath, "第一章 开始\r\n正文\r\n");
        using (var bitmap = new SKBitmap(6, 4))
        {
            bitmap.Erase(new SKColor(120, 80, 40));
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            await File.WriteAllBytesAsync(imagePath, encoded.ToArray());
        }

        try
        {
            var root = FindWorkspaceRoot();
            var config = LegacyEasyPubConfig.Load(
                Path.Combine(root, "work", "easypub-compat", "legacy-capture", "config.xml"));
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                inputPath,
                outputPath,
                "MOBI 插图测试",
                Options: config.Options with
                {
                    Illustrations = [new BookIllustration("场景", imagePath, "场景插图", InsertAfterLine: 1)],
                }));

            var mobi = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(mobi, 60, 8));
            Assert.True(mobi.AsSpan().IndexOf("BOUNDARY"u8) >= 0);
            Assert.True(mobi.AsSpan().IndexOf(new byte[] { 0xFF, 0xD8, 0xFF }) >= 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<string> ReadTextEntryAsync(ZipArchive archive, string path)
    {
        var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == path);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync();
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EasyPub.Modern.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find workspace root.");
    }
}
