using EasyPub.Core;
using SkiaSharp;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace EasyPub.Core.Tests;

public sealed class CoverImageTests
{
    [Fact]
    public void Batch_requests_keep_each_novels_independent_cover_assignment()
    {
        var outputDirectory = Path.Combine(Path.GetTempPath(), "easypub-independent-covers");
        var sources = new[]
        {
            new BookConversionSource(@"C:\books\第一本.txt", @"C:\covers\第一本.webp"),
            new BookConversionSource(@"C:\books\第二本.txt", @"C:\covers\第二本.jpg"),
        };

        var requests = BatchConversionRequestFactory.Create(
            sources,
            outputDirectory,
            "mobi",
            "作者",
            ConversionOptions.LegacyDefault);

        Assert.Equal(2, requests.Count);
        Assert.Equal(@"C:\covers\第一本.webp", requests[0].Options!.CoverImagePath);
        Assert.Equal(@"C:\covers\第二本.jpg", requests[1].Options!.CoverImagePath);
        Assert.EndsWith("第一本.mobi", requests[0].OutputPath, StringComparison.Ordinal);
        Assert.EndsWith("第二本.mobi", requests[1].OutputPath, StringComparison.Ordinal);
    }

    [Fact]
    public void Batch_requests_use_each_books_title_and_author_overrides()
    {
        var requests = BatchConversionRequestFactory.Create(
            [
                new BookConversionSource(@"C:\books\第一本.txt", Title: "自定义书名", Author: "甲"),
                new BookConversionSource(@"C:\books\第二本.txt", Author: "乙"),
                new BookConversionSource(@"C:\books\第三本.txt"),
            ],
            Path.Combine(Path.GetTempPath(), "easypub-metadata"),
            "epub",
            "统一作者",
            ConversionOptions.LegacyDefault);

        Assert.Equal("自定义书名", requests[0].Title);
        Assert.Equal("甲", requests[0].Author);
        Assert.Null(requests[1].Title);
        Assert.Equal("乙", requests[1].Author);
        Assert.Equal("统一作者", requests[2].Author);
    }

    [Fact]
    public async Task Batch_conversion_does_not_mix_covers_between_novels()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-two-covers-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(directory, "output");
        Directory.CreateDirectory(directory);
        var firstText = Path.Combine(directory, "第一本.txt");
        var secondText = Path.Combine(directory, "第二本.txt");
        var firstCover = Path.Combine(directory, "第一本.jpg");
        var secondCover = Path.Combine(directory, "第二本.jpg");
        await File.WriteAllTextAsync(firstText, "第一章 开始\r\n正文\r\n");
        await File.WriteAllTextAsync(secondText, "第一章 开始\r\n正文\r\n");
        await WriteJpegAsync(firstCover, 8, 12, new SKColor(200, 30, 30));
        await WriteJpegAsync(secondCover, 9, 13, new SKColor(30, 30, 200));

        try
        {
            var requests = BatchConversionRequestFactory.Create(
                [new BookConversionSource(firstText, firstCover), new BookConversionSource(secondText, secondCover)],
                outputDirectory,
                "epub",
                null,
                ConversionOptions.LegacyDefault);
            await new BatchConverter(new EasyPubConverter()).ConvertAsync(requests, maxParallelism: 2);

            Assert.Equal((8, 12), ReadEpubCoverSize(Path.Combine(outputDirectory, "第一本.epub")));
            Assert.Equal((9, 13), ReadEpubCoverSize(Path.Combine(outputDirectory, "第二本.epub")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mobi_with_image_cover_keeps_a_valid_joint_kindle_structure()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-mobi-cover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "book.txt");
        var coverPath = Path.Combine(directory, "cover.jpg");
        var outputPath = Path.Combine(directory, "book.mobi");
        var extractedCoverPath = Path.Combine(directory, "extracted-cover.jpg");
        await File.WriteAllTextAsync(inputPath, "第一章 开始\r\n正文\r\n");
        using (var source = new SKBitmap(8, 12))
        {
            source.Erase(new SKColor(180, 60, 40));
            using var encoded = source.Encode(SKEncodedImageFormat.Jpeg, 100);
            await File.WriteAllBytesAsync(coverPath, encoded.ToArray());
        }

        try
        {
            var root = FindWorkspaceRoot();
            var config = LegacyEasyPubConfig.Load(
                Path.Combine(root, "work", "easypub-compat", "legacy-capture", "config.xml"));
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                inputPath,
                outputPath,
                "MOBI 封面测试",
                Options: config.Options with { CoverImagePath = coverPath }));

            var mobi = await File.ReadAllBytesAsync(outputPath);
            Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(mobi, 60, 8));
            Assert.True(mobi.AsSpan().IndexOf(new byte[] { 0xFF, 0xD8, 0xFF }) >= 0);
            AssertValidJointMobi(mobi);

            var calibre = @"C:\Program Files\Calibre2\ebook-meta.exe";
            if (File.Exists(calibre))
            {
                var startInfo = new ProcessStartInfo(calibre)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                startInfo.ArgumentList.Add(outputPath);
                startInfo.ArgumentList.Add("--get-cover");
                startInfo.ArgumentList.Add(extractedCoverPath);
                using var process = Process.Start(startInfo)!;
                await process.WaitForExitAsync();
                Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
                using var extracted = SKBitmap.Decode(extractedCoverPath);
                Assert.Equal(8, extracted.Width);
                Assert.Equal(12, extracted.Height);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Webp_cover_is_embedded_as_jpeg_in_epub_metadata_and_cover_page()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-epub-cover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var inputPath = Path.Combine(directory, "book.txt");
        var coverPath = Path.Combine(directory, "cover.webp");
        var outputPath = Path.Combine(directory, "book.epub");
        await File.WriteAllTextAsync(inputPath, "第一章 开始\r\n正文\r\n");
        using (var source = new SKBitmap(4, 6))
        {
            source.Erase(new SKColor(30, 90, 180));
            using var encoded = source.Encode(SKEncodedImageFormat.Webp, 100);
            await File.WriteAllBytesAsync(coverPath, encoded.ToArray());
        }

        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                inputPath,
                outputPath,
                "封面测试",
                Options: new ConversionOptions { CoverImagePath = coverPath }));

            using var archive = ZipFile.OpenRead(outputPath);
            var coverEntry = Assert.Single(archive.Entries, entry => entry.FullName == "OEBPS/cover.jpg");
            byte[] coverBytes;
            using (var stream = coverEntry.Open())
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory);
                coverBytes = memory.ToArray();
            }
            Assert.Equal([0xFF, 0xD8, 0xFF], coverBytes[..3]);
            using var jpeg = SKBitmap.Decode(coverBytes);
            Assert.Equal(4, jpeg.Width);
            Assert.Equal(6, jpeg.Height);

            Assert.Contains(
                "<meta name=\"cover\" content=\"cover-image\"/>",
                await ReadTextEntryAsync(archive, "OEBPS/content.opf"));
            Assert.Contains(
                "<item id=\"cover-image\" href=\"cover.jpg\" media-type=\"image/jpeg\"/>",
                await ReadTextEntryAsync(archive, "OEBPS/content.opf"));
            Assert.Contains(
                "<img class=\"attpic\" src=\"cover.jpg\" alt=\"封面测试\"/>",
                await ReadTextEntryAsync(archive, "OEBPS/cover.html"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Lossless_webp_is_converted_to_full_size_high_quality_jpeg()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-cover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var webpPath = Path.Combine(directory, "cover.webp");
        using (var source = new SKBitmap(3, 2))
        {
            source.Erase(new SKColor(210, 40, 30));
            using var encoded = source.Encode(SKEncodedImageFormat.Webp, 100);
            await File.WriteAllBytesAsync(webpPath, encoded.ToArray());
        }

        try
        {
            var cover = await CoverImageConverter.PrepareJpegAsync(webpPath);

            Assert.True(cover.WasConverted);
            Assert.Equal("WEBP", cover.SourceFormat);
            Assert.Equal(3, cover.PixelWidth);
            Assert.Equal(2, cover.PixelHeight);
            Assert.Equal([0xFF, 0xD8, 0xFF], cover.JpegBytes[..3]);
            using var jpeg = SKBitmap.Decode(cover.JpegBytes);
            Assert.Equal(3, jpeg.Width);
            Assert.Equal(2, jpeg.Height);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cover_preparation_reuses_the_same_result_while_the_file_is_unchanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-cover-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var coverPath = Path.Combine(directory, "cover.jpg");
        await WriteJpegAsync(coverPath, 7, 11, new SKColor(40, 80, 120));

        try
        {
            var first = await CoverImageConverter.PrepareJpegAsync(coverPath);
            var second = await CoverImageConverter.PrepareJpegAsync(coverPath);

            Assert.Same(first, second);
            Assert.Same(first.JpegBytes, second.JpegBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cover_preparation_invalidates_the_cache_after_the_file_changes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-cover-cache-change-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var coverPath = Path.Combine(directory, "cover.jpg");
        await WriteJpegAsync(coverPath, 5, 9, new SKColor(20, 60, 100));

        try
        {
            var first = await CoverImageConverter.PrepareJpegAsync(coverPath);
            await WriteJpegAsync(coverPath, 13, 17, new SKColor(100, 60, 20));
            File.SetLastWriteTimeUtc(coverPath, DateTime.UtcNow.AddSeconds(2));

            var second = await CoverImageConverter.PrepareJpegAsync(coverPath);

            Assert.NotSame(first, second);
            Assert.Equal((13, 17), (second.PixelWidth, second.PixelHeight));
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

    private static async Task WriteJpegAsync(string path, int width, int height, SKColor color)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 100);
        await File.WriteAllBytesAsync(path, encoded.ToArray());
    }

    private static (int Width, int Height) ReadEpubCoverSize(string epubPath)
    {
        using var archive = ZipFile.OpenRead(epubPath);
        var entry = Assert.Single(archive.Entries, candidate => candidate.FullName == "OEBPS/cover.jpg");
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        using var bitmap = SKBitmap.Decode(memory.ToArray());
        return (bitmap.Width, bitmap.Height);
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

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EasyPub.Modern.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find workspace root.");
    }
}
