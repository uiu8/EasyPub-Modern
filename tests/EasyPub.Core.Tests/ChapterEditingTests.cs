using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ChapterEditingTests
{
    [Fact]
    public async Task Editing_document_finds_recognized_and_numeric_chapter_candidates()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-chapters-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "楔子正文\r\n第一章 已识别\r\n内容一\r\n001 雨夜\r\n内容二\r\n");

        try
        {
            var document = await ChapterEditingDocument.LoadAsync(path);

            Assert.Collection(
                document.Candidates,
                candidate =>
                {
                    Assert.Equal(2, candidate.LineNumber);
                    Assert.Equal(ChapterCandidateKind.Recognized, candidate.Kind);
                    Assert.Equal("第一章 已识别", candidate.OriginalTitle);
                    Assert.Equal("第一章 已识别", candidate.SuggestedTitle);
                },
                candidate =>
                {
                    Assert.Equal(4, candidate.LineNumber);
                    Assert.Equal(ChapterCandidateKind.NumericTitle, candidate.Kind);
                    Assert.Equal("001 雨夜", candidate.OriginalTitle);
                    Assert.Equal("第一章 雨夜", candidate.SuggestedTitle);
                });
            Assert.Contains("2  第一章 已识别", document.GetPreview(2, 1));
            Assert.Contains(">    4  001 雨夜", document.GetPreview(4, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Select_all_suggested_edits_includes_every_chapter_candidate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-select-all-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 已识别\r\n正文\r\n002 清晨\r\n正文\r\n");

        try
        {
            var document = await ChapterEditingDocument.LoadAsync(path);

            Assert.Equal(
                [new ChapterTitleEdit(1, "第一章 已识别"), new ChapterTitleEdit(3, "第二章 清晨")],
                document.CreateAllSuggestedEdits());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Render_changes_only_explicitly_edited_candidate_lines_and_preserves_crlf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-render-{Guid.NewGuid():N}.txt");
        const string source = "第一章 原标题\r\n正文 001 个例子\r\n002 次日\r\n结尾\r\n";
        await File.WriteAllTextAsync(path, source);

        try
        {
            var document = await ChapterEditingDocument.LoadAsync(path);
            var rendered = document.Render(
            [
                new ChapterTitleEdit(3, "第二章 次日"),
            ]);

            Assert.Equal("第一章 原标题\r\n正文 001 个例子\r\n第二章 次日\r\n结尾\r\n", rendered);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Save_as_preserves_gbk_encoding_without_overwriting_the_source()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        var encoding = System.Text.Encoding.GetEncoding(936);
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-save-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.txt");
        var outputPath = Path.Combine(directory, "edited.txt");
        const string source = "001 雨夜\r\n正文\r\n";
        await File.WriteAllBytesAsync(sourcePath, encoding.GetBytes(source));

        try
        {
            var document = await ChapterEditingDocument.LoadAsync(sourcePath, encodingMode: TextEncodingMode.Gbk);
            await document.SaveAsAsync(outputPath, [new ChapterTitleEdit(1, "第一章 雨夜")]);

            Assert.Equal(source, encoding.GetString(await File.ReadAllBytesAsync(sourcePath)));
            Assert.Equal("第一章 雨夜\r\n正文\r\n", encoding.GetString(await File.ReadAllBytesAsync(outputPath)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Auto_detection_reads_and_preserves_utf32_little_endian_bom()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-utf32-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.txt");
        var outputPath = Path.Combine(directory, "edited.txt");
        var encoding = System.Text.Encoding.UTF32;
        var content = encoding.GetBytes("001 雨夜\r\n正文\r\n");
        await File.WriteAllBytesAsync(sourcePath, [.. encoding.GetPreamble(), .. content]);

        try
        {
            var document = await ChapterEditingDocument.LoadAsync(sourcePath);
            await document.SaveAsAsync(outputPath, [new ChapterTitleEdit(1, "第一章 雨夜")]);
            var output = await File.ReadAllBytesAsync(outputPath);

            Assert.True(output.AsSpan().StartsWith(encoding.Preamble));
            Assert.Equal("第一章 雨夜\r\n正文\r\n", encoding.GetString(output, encoding.Preamble.Length, output.Length - encoding.Preamble.Length));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task One_click_numeric_title_edits_are_recognized_by_the_converter()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-normalized-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var sourcePath = Path.Combine(directory, "source.txt");
        var editedPath = Path.Combine(directory, "edited.txt");
        var mobiPath = Path.Combine(directory, "edited.mobi");
        await File.WriteAllTextAsync(sourcePath, "001 雨夜\r\n正文一\r\n002 清晨\r\n正文二\r\n");

        try
        {
            var document = await ChapterEditingDocument.LoadAsync(sourcePath);
            var edits = document.Candidates
                .Where(candidate => candidate.Kind == ChapterCandidateKind.NumericTitle)
                .Select(candidate => new ChapterTitleEdit(candidate.LineNumber, candidate.SuggestedTitle));
            await document.SaveAsAsync(editedPath, edits);

            var root = FindWorkspaceRoot();
            var config = LegacyEasyPubConfig.Load(
                Path.Combine(root, "work", "easypub-compat", "legacy-capture", "config.xml"));
            var result = await new EasyPubConverter().ConvertAsync(
                new ConversionRequest(editedPath, mobiPath, Options: config.Options));

            Assert.Equal(3, result.ChapterCount);
            var mobi = await File.ReadAllBytesAsync(mobiPath);
            Assert.Equal("BOOKMOBI", System.Text.Encoding.ASCII.GetString(mobi, 60, 8));
            Assert.True(mobi.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes("第一章 雨夜")) >= 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EasyPub.Modern.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find workspace root.");
    }

    [Fact]
    public void Numeric_title_with_leading_zero_is_normalized()
    {
        var matched = ChapterTitleNormalizer.TryNormalizeNumericTitle("001 雨夜", out var normalized);

        Assert.True(matched);
        Assert.Equal("第一章 雨夜", normalized);
    }

    [Theory]
    [InlineData("002 清晨", "第二章 清晨")]
    [InlineData("010 重逢", "第十章 重逢")]
    [InlineData("011 来客", "第十一章 来客")]
    [InlineData("020 决定", "第二十章 决定")]
    [InlineData("101 归途", "第一百零一章 归途")]
    [InlineData("1001 新世界", "第一千零一章 新世界")]
    public void Common_chapter_numbers_use_standard_chinese_numerals(string input, string expected)
    {
        Assert.True(ChapterTitleNormalizer.TryNormalizeNumericTitle(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("003. 风声", "第三章 风声")]
    [InlineData("004、秘密", "第四章 秘密")]
    [InlineData("005-归来", "第五章 归来")]
    [InlineData("006：尾声", "第六章 尾声")]
    public void Common_number_separators_are_supported(string input, string expected)
    {
        Assert.True(ChapterTitleNormalizer.TryNormalizeNumericTitle(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("2026-08-23")]
    [InlineData("001 12345")]
    [InlineData("0 序章")]
    [InlineData("10000 超出范围")]
    [InlineData("今天正文里有 001 个例子")]
    public void Non_title_lines_are_not_normalized(string input)
    {
        Assert.False(ChapterTitleNormalizer.TryNormalizeNumericTitle(input, out var normalized));
        Assert.Equal(input, normalized);
    }
}
