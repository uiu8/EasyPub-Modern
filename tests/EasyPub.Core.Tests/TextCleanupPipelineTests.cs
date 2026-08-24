using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class TextCleanupPipelineTests
{
    [Fact]
    public void Cleanup_keeps_source_line_numbers_and_previews_each_rule()
    {
        var text = "001 雨夜\n這是一段沒有句号\n接在上一行的正文。\n\n\n本书来自某某下载站";
        var preview = TextCleanupPipeline.Apply(text, new TextCleanupOptions
        {
            CollapseBlankLines = true,
            RepairHardWraps = true,
            NormalizeChapterNumbers = true,
            RemoveSiteNotices = true,
            ChineseVariant = ChineseVariantConversion.ToSimplified,
        });

        Assert.Equal(6, preview.Lines.Count);
        Assert.Equal("第一章 雨夜", preview.Lines[0]);
        Assert.Contains("这是一段", preview.Text);
        Assert.DoesNotContain("下载站", preview.Text);
        Assert.Contains(preview.Changes, change => change.Rule == "标准化章节编号");
        Assert.Contains(preview.Changes, change => change.Rule == "修复正文硬换行");
        Assert.Contains(preview.Changes, change => change.Rule == "合并连续空行");
        Assert.Contains(preview.Changes, change => change.Rule == "清理网站广告/下载说明");
    }

    [Fact]
    public async Task Conversion_uses_cleaned_text_without_modifying_source_txt()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "book.txt");
        var output = Path.Combine(directory, "book.epub");
        const string original = "001 雨夜\n正文内容。";
        await File.WriteAllTextAsync(input, original);
        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(input, output)
            {
                Options = new ConversionOptions
                {
                    TextCleanup = new TextCleanupOptions { NormalizeChapterNumbers = true },
                },
            });

            Assert.Equal(original, await File.ReadAllTextAsync(input));
            using var archive = System.IO.Compression.ZipFile.OpenRead(output);
            var toc = archive.GetEntry("OEBPS/book-toc.html")!;
            using var reader = new StreamReader(toc.Open());
            Assert.Contains("第一章 雨夜", await reader.ReadToEndAsync());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
