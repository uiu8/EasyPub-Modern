using System.IO.Compression;
using System.Text;
using EasyPub.Core;
using SkiaSharp;

namespace EasyPub.Core.Tests;

public sealed class PreviewAndFontTests
{
    [Fact]
    public async Task Full_book_preview_uses_generated_epub_and_cleans_temporary_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-preview-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "小说.txt");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文\n第二章 继续\n正文");
        BookPreviewPackage? package = null;
        try
        {
            package = await new BookPreviewService().BuildAsync(new ConversionRequest(
                input,
                Path.Combine(directory, "ignored.mobi"),
                Options: ConversionOptions.LegacyDefault with { AdditionalCss = "p { color: #123456; }" }));
            Assert.Equal(5, package.Items.Count);
            Assert.All(package.Items, item => Assert.True(File.Exists(item.HtmlPath)));
            var working = package.WorkingDirectory;
            package.Dispose();
            package = null;
            Assert.False(Directory.Exists(working));
        }
        finally
        {
            package?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TrueType_font_is_subset_and_embedded_in_epub()
    {
        var fontPath = FindTestFont();
        if (!File.Exists(fontPath)) return;
        var info = FontEmbeddingService.Inspect(fontPath);
        if (!info.CanEmbed || !info.CanSubset) return;

        var directory = Path.Combine(Path.GetTempPath(), $"easypub-font-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "book.txt");
        var output = Path.Combine(directory, "book.epub");
        await File.WriteAllTextAsync(input, "第一章 Start\nOnly a small ASCII glyph set.");
        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                input,
                output,
                Options: ConversionOptions.LegacyDefault with
                {
                    Font = new EmbeddedFontOptions
                    {
                        Enabled = true,
                        FontPath = fontPath,
                        FamilyName = "Embedded Test",
                        Subset = true,
                    },
                }));

            using var archive = ZipFile.OpenRead(output);
            var fontEntry = Assert.Single(archive.Entries, entry => entry.FullName == "OEBPS/fonts/book.ttf");
            Assert.True(fontEntry.Length < new FileInfo(fontPath).Length);
            await using (var fontStream = fontEntry.Open())
            using (var memory = new MemoryStream())
            {
                await fontStream.CopyToAsync(memory);
                using var data = SKData.CreateCopy(memory.ToArray());
                using var typeface = SKTypeface.FromData(data);
                Assert.NotNull(typeface);
            }
            var css = await ReadEntryAsync(archive, "OEBPS/style.css");
            Assert.Contains("@font-face", css);
            Assert.Contains("Embedded Test", css);
            var opf = await ReadEntryAsync(archive, "OEBPS/content.opf");
            Assert.Contains("application/vnd.ms-opentype", opf);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancelled_epub_does_not_replace_existing_output()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-cancel-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "book.txt");
        var output = Path.Combine(directory, "book.epub");
        await File.WriteAllTextAsync(input, string.Join('\n', Enumerable.Repeat("第一章 正文", 1000)));
        await File.WriteAllTextAsync(output, "old-output");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new EasyPubConverter().ConvertAsync(new ConversionRequest(input, output), cancellation.Token));
            Assert.Equal("old-output", await File.ReadAllTextAsync(output));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mobi_with_subset_font_keeps_joint_kindle_structure()
    {
        var fontPath = FindTestFont();
        if (!File.Exists(fontPath)) return;
        var root = FindWorkspaceRoot();
        var configPath = Path.Combine(root, "work", "easypub-compat", "legacy-capture", "config.xml");
        if (!File.Exists(configPath)) return;

        var directory = Path.Combine(Path.GetTempPath(), $"easypub-mobi-font-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "font.txt");
        var output = Path.Combine(directory, "font.mobi");
        await File.WriteAllTextAsync(input, "第一章 Start\nA small embedded font test.");
        try
        {
            var config = LegacyEasyPubConfig.Load(configPath);
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(
                input,
                output,
                Options: config.Options with
                {
                    Font = new EmbeddedFontOptions
                    {
                        Enabled = true,
                        FontPath = fontPath,
                        FamilyName = "Embedded Test",
                        Subset = true,
                    },
                }));
            var mobi = await File.ReadAllBytesAsync(output);
            Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(mobi, 60, 8));
            Assert.True(mobi.AsSpan().IndexOf("BOUNDARY"u8) >= 0);
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

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EasyPub.Modern.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find workspace root.");
    }

    private static string FindTestFont()
    {
        var candidates = new[]
        {
            @"C:\Windows\Fonts\simhei.ttf",
            @"C:\Windows\Fonts\Deng.ttf",
            @"C:\Windows\Fonts\arial.ttf",
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[^1];
    }
}
