using System.IO.Compression;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class LegacyCompatibilityTests
{
    [Fact]
    public async Task Default_profile_matches_the_first_legacy_golden_sample()
    {
        var root = FindWorkspaceRoot();
        var input = Path.Combine(root, "work", "easypub-compat", "fixtures", "basic-utf8.txt");
        var golden = Path.Combine(root, "work", "easypub-compat", "golden", "basic-utf8.epub");
        var output = Path.Combine(Path.GetTempPath(), $"easypub-modern-{Guid.NewGuid():N}.epub");

        try
        {
            var result = await new EasyPubConverter().ConvertAsync(
                new ConversionRequest(input, output, "EasyPub 兼容基准", "Codex"));

            Assert.Equal(4, result.ChapterCount);
            var expected = ReadEntries(golden);
            var actual = ReadEntries(output);
            Assert.Equal(expected.Keys, actual.Keys);
            foreach (var path in expected.Keys)
                Assert.Equal(expected[path], actual[path]);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Batch_conversion_preserves_input_order()
    {
        var root = FindWorkspaceRoot();
        var input = Path.Combine(root, "work", "easypub-compat", "fixtures", "basic-utf8.txt");
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var requests = Enumerable.Range(1, 3)
                .Select(index => new ConversionRequest(input, Path.Combine(directory, $"{index}.epub"), $"Book {index}"))
                .ToArray();
            var results = await new BatchConverter(new EasyPubConverter()).ConvertAsync(requests, maxParallelism: 2);
            Assert.Equal(
                requests.Select(request => Path.GetFullPath(request.OutputPath)),
                results.Select(result => result.OutputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Batch_report_keeps_successes_and_failures_for_retry()
    {
        var root = FindWorkspaceRoot();
        var input = Path.Combine(root, "work", "easypub-compat", "fixtures", "basic-utf8.txt");
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-batch-report-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var missing = Path.Combine(directory, "missing.txt");
        var requests = new[]
        {
            new ConversionRequest(input, Path.Combine(directory, "ok.epub")),
            new ConversionRequest(missing, Path.Combine(directory, "failed.epub")),
        };

        try
        {
            var outcomes = await new BatchConverter(new EasyPubConverter())
                .ConvertWithReportAsync(requests, maxParallelism: 2);

            Assert.Equal(2, outcomes.Count);
            Assert.True(outcomes[0].Succeeded);
            Assert.Equal(requests[0], outcomes[0].Request);
            Assert.False(outcomes[1].Succeeded);
            Assert.Equal(requests[1], outcomes[1].Request);
            Assert.NotEmpty(outcomes[1].ErrorMessage!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Default_mobi_profile_matches_the_legacy_golden_sample()
    {
        var root = FindWorkspaceRoot();
        var input = Path.Combine(root, "work", "easypub-compat", "fixtures", "basic-utf8.txt");
        var golden = Path.Combine(root, "work", "easypub-compat", "golden", "basic-utf8-v150-complete.mobi");
        var output = Path.Combine(Path.GetTempPath(), $"easypub-modern-{Guid.NewGuid():N}.mobi");

        try
        {
            var result = await new EasyPubConverter().ConvertAsync(
                new ConversionRequest(input, output, "EasyPub 兼容基准", "Codex"));

            Assert.Equal(4, result.ChapterCount);
            var actual = File.ReadAllBytes(output);
            AssertMobiEquivalent(File.ReadAllBytes(golden), actual);
            AssertValidJointMobi(actual);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public async Task Custom_layout_and_chapter_pattern_are_applied()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "custom.txt");
        var output = Path.Combine(directory, "custom.epub");
        await File.WriteAllTextAsync(input, "Chapter 1\n正文\n\nChapter 2\n结尾");

        try
        {
            var options = new ConversionOptions
            {
                ChapterPattern = @"^Chapter\s+\d+",
                RemoveBlankLines = false,
                AddFullWidthIndent = false,
                FontSizePercent = 125,
                LineHeightPercent = 150,
                ParagraphIndentEm = 2,
                TextAlignment = TextAlignment.Left,
                PageMarginTopPx = 11,
                PageMarginBottomPx = 12,
                PageMarginLeftPx = 13,
                PageMarginRightPx = 14,
                AdditionalCss = ".custom { color: red; }",
            };
            var result = await new EasyPubConverter().ConvertAsync(
                new ConversionRequest(input, output, Options: options));

            Assert.Equal(3, result.ChapterCount);
            var entries = ReadEntries(output);
            var css = System.Text.Encoding.UTF8.GetString(entries["OEBPS/style.css"]);
            var chapter = System.Text.Encoding.UTF8.GetString(entries["OEBPS/chapter1.html"]);
            Assert.Contains("font-size: 125%;", css);
            Assert.Contains("line-height: 150%;", css);
            Assert.Contains("text-indent: 2em;", css);
            Assert.Contains("margin-top: 11px;", css);
            Assert.Contains("margin-bottom: 12px;", css);
            Assert.Contains("margin-left: 13px;", css);
            Assert.Contains("margin-right: 14px;", css);
            Assert.Contains(".custom { color: red; }", css);
            Assert.Contains("<p class=\"a\">正文</p>", chapter);
            Assert.Contains("<p class=\"a\"><br /></p>", chapter);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Original_config_is_imported_and_drives_mobi_conversion()
    {
        var root = FindWorkspaceRoot();
        var config = Path.Combine(root, "work", "easypub-compat", "legacy-capture", "config.xml");
        var bundledConfig = Path.Combine(root, "src", "EasyPub.Desktop", "config.xml");
        var input = Path.Combine(root, "work", "easypub-compat", "fixtures", "basic-utf8.txt");
        var output = Path.Combine(Path.GetTempPath(), $"easypub-config-{Guid.NewGuid():N}.mobi");

        var imported = LegacyEasyPubConfig.Load(config);

        static string[] Values(string path) => System.Xml.Linq.XDocument.Load(path)
            .Descendants()
            .Where(element => !element.HasElements)
            .Select(element => $"{element.Name.LocalName}={element.Value.Trim()}")
            .ToArray();
        Assert.Equal(Values(config), Values(bundledConfig));

        Assert.Equal(LegacyOutputFormat.Mobi, imported.OutputFormat);
        Assert.Equal(@"C:\Users\13168\Desktop", imported.OutputDirectory);
        Assert.Equal(@"^\s*[第卷][0123456789一二三四五六七八九十零〇百千两]*[章回部节集卷].*", imported.Options.ChapterPattern);
        Assert.True(imported.Options.RemoveBlankLines);
        Assert.True(imported.Options.AddFullWidthIndent);
        Assert.Equal(110, imported.Options.FontSizePercent);
        Assert.Equal(120, imported.Options.LineHeightPercent);
        Assert.Equal(0.6, imported.Options.ParagraphSpacingEm);
        Assert.Equal(0, imported.Options.ParagraphIndentEm);
        Assert.Equal(3, imported.Options.PageMarginLeftPx);
        Assert.Equal(3, imported.Options.PageMarginRightPx);
        Assert.Equal(TextAlignment.Default, imported.Options.TextAlignment);
        Assert.Equal(MobiCompression.Standard, imported.Options.Mobi.Compression);
        Assert.True(imported.Options.Mobi.StripSourceArchive);
        Assert.True(imported.Options.Mobi.EnableReadingProgressSync);
        Assert.Null(imported.Options.Mobi.Asin);
        Assert.EndsWith("kindlegen_v2.9.exe", imported.Options.Mobi.KindleGenPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(imported.AlwaysOnTop);
        Assert.NotEmpty(imported.AppliedSettings);
        Assert.NotEmpty(imported.UnsupportedSettings);

        try
        {
            var result = await new EasyPubConverter().ConvertAsync(
                new ConversionRequest(input, output, "EasyPub config 实测", "Codex", imported.Options));
            var mobi = await File.ReadAllBytesAsync(output);
            Assert.Equal(4, result.ChapterCount);
            Assert.Equal("BOOKMOBI", System.Text.Encoding.ASCII.GetString(mobi, 60, 8));
            Assert.False(Contains(mobi, System.Text.Encoding.ASCII.GetBytes("SRCS")));
            AssertValidJointMobi(mobi);
            Assert.Matches(@"^B00[A-Z0-9]{7}$", ReadExthText(mobi, 113));
            Assert.Equal("EBOK", ReadExthText(mobi, 501));
        }
        finally
        {
            File.Delete(output);
        }
    }

    private static Dictionary<string, byte[]> ReadEntries(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.ToDictionary(
            entry => entry.FullName,
            entry =>
            {
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            },
            StringComparer.Ordinal);
    }

    private static void AssertMobiEquivalent(byte[] expected, byte[] actual)
    {
        Assert.Equal("BOOKMOBI", System.Text.Encoding.ASCII.GetString(actual, 60, 8));
        Assert.Equal(ReadBigEndianUInt16(expected, 76), ReadBigEndianUInt16(actual, 76));
        Assert.InRange(actual.Length, expected.Length - 1024, expected.Length + 1024);
        Assert.False(Contains(actual, System.Text.Encoding.ASCII.GetBytes("SRCS")));
        Assert.True(Contains(actual, System.Text.Encoding.UTF8.GetBytes("EasyPub 兼容基准")));
        Assert.True(Contains(actual, System.Text.Encoding.UTF8.GetBytes("Codex")));

        var recordCount = ReadBigEndianUInt16(actual, 76);
        var previousOffset = 0u;
        for (var index = 0; index < recordCount; index++)
        {
            var offset = ReadBigEndianUInt32(actual, 78 + index * 8);
            Assert.InRange(offset, previousOffset, (uint)actual.Length);
            previousOffset = offset;
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
            if (end - start == 8 && System.Text.Encoding.ASCII.GetString(mobi, start, 8) == "BOUNDARY")
            {
                boundaryRecord = index;
                break;
            }
        }
        Assert.True(boundaryRecord >= 0, "联合 MOBI 缺少 BOUNDARY 记录。");

        var record0 = checked((int)ReadBigEndianUInt32(mobi, 78));
        var mobiHeaderLength = checked((int)ReadBigEndianUInt32(mobi, record0 + 20));
        var exth = record0 + 16 + mobiHeaderLength;
        Assert.Equal("EXTH", System.Text.Encoding.ASCII.GetString(mobi, exth, 4));
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

        var kf8Record = boundaryRecord + 1;
        var kf8Offset = checked((int)ReadBigEndianUInt32(mobi, 78 + kf8Record * 8));
        Assert.Equal("MOBI", System.Text.Encoding.ASCII.GetString(mobi, kf8Offset + 16, 4));
    }

    private static string ReadExthText(byte[] mobi, uint requestedType)
    {
        var record0 = checked((int)ReadBigEndianUInt32(mobi, 78));
        var mobiHeaderLength = checked((int)ReadBigEndianUInt32(mobi, record0 + 20));
        var exth = record0 + 16 + mobiHeaderLength;
        var count = checked((int)ReadBigEndianUInt32(mobi, exth + 8));
        var cursor = exth + 12;
        for (var index = 0; index < count; index++)
        {
            var type = ReadBigEndianUInt32(mobi, cursor);
            var length = checked((int)ReadBigEndianUInt32(mobi, cursor + 4));
            if (type == requestedType)
                return System.Text.Encoding.UTF8.GetString(mobi, cursor + 8, length - 8);
            cursor += length;
        }
        throw new Xunit.Sdk.XunitException($"EXTH 中缺少类型 {requestedType}。");
    }

    private static ushort ReadBigEndianUInt16(byte[] bytes, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static uint ReadBigEndianUInt32(byte[] bytes, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    private static bool Contains(byte[] source, byte[] value) => source.AsSpan().IndexOf(value) >= 0;

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EasyPub.Modern.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find workspace root.");
    }
}
