using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class TextCleanupPipelineTests
{
    [Fact]
    public void Site_notice_cleanup_detects_promotional_sentence_with_bare_domain()
    {
        const string notice = "　　更新不易，请书友分享，速读谷 www.sudugu.org，无错最新章节";

        var preview = TextCleanupPipeline.Apply(notice, new TextCleanupOptions
        {
            RemoveSiteNotices = true,
        });

        var change = Assert.Single(preview.Changes);
        Assert.Equal("清理网站广告/下载说明", change.Rule);
        Assert.True(change.IsApplied);
        Assert.Equal(string.Empty, preview.Text);
    }

    [Fact]
    public void Site_notice_cleanup_keeps_ordinary_prose_that_mentions_a_shared_web_address()
    {
        const string prose = "他把研究网址 www.example.org 分享给同事，随后继续讨论。";

        var preview = TextCleanupPipeline.Apply(prose, new TextCleanupOptions
        {
            RemoveSiteNotices = true,
        });

        Assert.Empty(preview.Changes);
        Assert.Equal(prose, preview.Text);
    }

    [Fact]
    public void Site_notice_cleanup_keeps_reader_prose_with_multiple_generic_signals()
    {
        const string prose = "读者分享了最新的研究网址 www.example.org，并讨论其中的数据。";

        var preview = TextCleanupPipeline.Apply(prose, new TextCleanupOptions
        {
            RemoveSiteNotices = true,
        });

        Assert.Empty(preview.Changes);
        Assert.Equal(prose, preview.Text);
    }

    [Fact]
    public void Site_notice_cleanup_detects_known_promotional_variants_from_real_novel()
    {
        var notices = new[]
        {
            "写到这里，请各位书友使用 必应 搜索：速 读 谷 www.sudugu.org看最新无错章节！",
            "更新不易，请书友分享，速读谷 www.sudugu.org，无错最新章节",
            "更新不易，看完请读者分享，速读谷，www.sudugu.org 更新快，不出错！",
            "更新不易，请分享，速读谷，www.sudugu.org看最新无错章节！",
            "更新不易，请分享，速读谷，www.sudugu.org 看最新章节！",
            "更新不易，请记得分享，速读谷，www.sudugu.org,看最新章节！",
            "写到这里，请记得我们的域名，速读谷，www.sudugu.org,看无错最新章节！",
            "更多精彩小说，请访问：速读谷 http://www.sudugu.org",
        };

        var preview = TextCleanupPipeline.Apply(string.Join('\n', notices), new TextCleanupOptions
        {
            RemoveSiteNotices = true,
        });

        Assert.Equal(notices.Length, preview.Changes.Count);
        Assert.Equal(string.Empty, preview.Text);
    }

    [Fact]
    public void Multiline_custom_regex_replaces_across_line_boundaries()
    {
        var preview = TextCleanupPipeline.Apply("广告开始\n请访问 example.com\n广告结束\n正文。", new TextCleanupOptions
        {
            CustomRules = [new TextCleanupCustomRule
            {
                Name = "跨行广告",
                Pattern = "广告开始.*?广告结束\\n?",
                Replacement = string.Empty,
                IsRegex = true,
                Multiline = true,
            }],
        });

        Assert.Equal("正文。", preview.Text);
        Assert.Contains(preview.Changes, change => change.Rule == "自定义：跨行广告");
    }

    [Fact]
    public void Invisible_characters_duplicate_titles_and_repeated_headers_can_be_cleaned()
    {
        var preview = TextCleanupPipeline.Apply("第一章 开始\n第一章 开始\n正\u200B文\n第 2 页", new TextCleanupOptions
        {
            RemoveInvisibleCharacters = true,
            RemoveDuplicateChapterTitles = true,
            RemoveRepeatedHeaders = true,
        });

        Assert.Equal(1, preview.Text.Split("第一章 开始").Length - 1);
        Assert.Contains("正文", preview.Text);
        Assert.DoesNotContain("第 2 页", preview.Text);
    }

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
    public void Individual_cleanup_change_can_be_excluded_and_restored_without_touching_other_changes()
    {
        const string text = "001 雨夜\n正文第一行没有句号\n接在上一行。\n\n\n第二段。";
        var options = new TextCleanupOptions
        {
            CollapseBlankLines = true,
            RepairHardWraps = true,
            NormalizeChapterNumbers = true,
        };
        var initial = TextCleanupPipeline.Apply(text, options);
        var chapterChange = Assert.Single(initial.Changes, change => change.Rule == "标准化章节编号");

        var excluded = TextCleanupPipeline.Apply(text, options with { ExcludedChangeKeys = [chapterChange.Key] });

        Assert.Equal("001 雨夜", excluded.Lines[0]);
        Assert.Contains(excluded.Changes, change => change.Key == chapterChange.Key && !change.IsApplied);
        Assert.Contains(excluded.Changes, change => change.Rule == "修复正文硬换行" && change.IsApplied);
        Assert.Contains(excluded.Changes, change => change.Rule == "合并连续空行" && change.IsApplied);

        var restored = TextCleanupPipeline.Apply(text, options);
        Assert.Equal("第一章 雨夜", restored.Lines[0]);
    }

    [Fact]
    public void Excluded_hard_wrap_remains_excluded_when_an_unrelated_inline_rule_is_toggled()
    {
        const string text = "第一章 雨夜\n这一行有一个英文逗号,而且没有结束标点\n下一行仍是正文。";
        var initialOptions = new TextCleanupOptions { RepairHardWraps = true, NormalizePunctuation = true };
        var initial = TextCleanupPipeline.Apply(text, initialOptions);
        var hardWrap = Assert.Single(initial.Changes, change => change.Rule == "修复正文硬换行");

        var withoutPunctuation = TextCleanupPipeline.Apply(text, new TextCleanupOptions
        {
            RepairHardWraps = true,
            NormalizePunctuation = false,
            ExcludedChangeKeys = [hardWrap.Key],
        });

        Assert.Contains(withoutPunctuation.Changes, change => change.Rule == "修复正文硬换行" && change.Key == hardWrap.Key && !change.IsApplied);
        Assert.Contains(Environment.NewLine, withoutPunctuation.Text);
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
                    TocHierarchy = new TocHierarchyOptions { IncludeHtmlTocPage = true },
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
