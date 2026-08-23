using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ConversionPreflightTests
{
    [Fact]
    public async Task Missing_input_is_reported_before_conversion_starts()
    {
        var request = new ConversionRequest(
            Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.txt"),
            Path.Combine(Path.GetTempPath(), "missing.epub"));

        var report = await new ConversionPreflightInspector().InspectAsync([request]);

        Assert.True(report.HasErrors);
        var issue = Assert.Single(report.Issues);
        Assert.Equal("input_missing", issue.Code);
        Assert.Equal(PreflightSeverity.Error, issue.Severity);
    }

    [Fact]
    public async Task Readable_txt_reports_chapter_candidates_and_existing_output_warning()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "小说.txt");
        var output = Path.Combine(directory, "小说.epub");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文\n第二章 继续\n正文");
        await File.WriteAllTextAsync(output, "old");

        try
        {
            var report = await new ConversionPreflightInspector().InspectAsync([
                new ConversionRequest(input, output),
            ]);

            var book = Assert.Single(report.Books);
            Assert.Equal(2, book.ChapterCandidateCount);
            Assert.False(report.HasErrors);
            Assert.Contains(report.Issues, issue =>
                issue.Code == "output_exists" && issue.Severity == PreflightSeverity.Warning);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mobi_preflight_reports_duplicate_output_missing_kindlegen_and_cover()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "第一本.txt");
        var second = Path.Combine(directory, "第二本.txt");
        var output = Path.Combine(directory, "重复.mobi");
        await File.WriteAllTextAsync(first, "第一章 开始\n正文");
        await File.WriteAllTextAsync(second, "第一章 开始\n正文");
        var options = ConversionOptions.LegacyDefault with
        {
            CoverImagePath = Path.Combine(directory, "missing.webp"),
            Mobi = new MobiOptions { KindleGenPath = Path.Combine(directory, "missing-kindlegen.exe") },
        };

        try
        {
            var report = await new ConversionPreflightInspector().InspectAsync([
                new ConversionRequest(first, output, Options: options),
                new ConversionRequest(second, output, Options: options),
            ]);

            Assert.True(report.HasErrors);
            Assert.Contains(report.Issues, issue => issue.Code == "duplicate_output");
            Assert.Contains(report.Issues, issue => issue.Code == "kindlegen_missing");
            Assert.Contains(report.Issues, issue => issue.Code == "cover_missing");
            Assert.Contains(report.Issues, issue =>
                issue.Code == "cover_missing" && issue.Target == PreflightTargetKind.Cover);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Corrupt_cover_is_reported_before_conversion()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "小说.txt");
        var cover = Path.Combine(directory, "损坏封面.jpg");
        await File.WriteAllTextAsync(input, "第一章 开始\n正文");
        await File.WriteAllTextAsync(cover, "not an image");

        try
        {
            var report = await new ConversionPreflightInspector().InspectAsync([
                new ConversionRequest(
                    input,
                    Path.Combine(directory, "小说.epub"),
                    Options: ConversionOptions.LegacyDefault with { CoverImagePath = cover }),
            ]);

            Assert.Contains(report.Issues, issue =>
                issue.Code == "cover_unreadable" && issue.Severity == PreflightSeverity.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_and_corrupt_illustrations_are_reported_before_conversion()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-preflight-illustrations-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "小说.txt");
        var corrupt = Path.Combine(directory, "损坏插图.webp");
        await File.WriteAllTextAsync(input, "第一章 开始\n[[插图:图一]]\n[[插图:图二]]");
        await File.WriteAllTextAsync(corrupt, "not an image");

        try
        {
            var report = await new ConversionPreflightInspector().InspectAsync([
                new ConversionRequest(
                    input,
                    Path.Combine(directory, "小说.epub"),
                    Options: ConversionOptions.LegacyDefault with
                    {
                        Illustrations =
                        [
                            new BookIllustration("图一", Path.Combine(directory, "不存在.jpg")),
                            new BookIllustration("图二", corrupt, InsertAfterLine: 99),
                        ],
                    }),
            ]);

            Assert.Contains(report.Issues, issue => issue.Code == "illustration_missing");
            Assert.Contains(report.Issues, issue => issue.Code == "illustration_unreadable");
            Assert.Contains(report.Issues, issue => issue.Code == "illustration_position_out_of_range");
            Assert.True(report.HasErrors);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_font_points_to_font_settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"easypub-preflight-font-{Guid.NewGuid():N}");
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
                        Font = new EmbeddedFontOptions
                        {
                            Enabled = true,
                            FontPath = Path.Combine(directory, "missing.ttf"),
                        },
                    }),
            ]);

            var issue = Assert.Single(report.Issues, item => item.Code == "font_missing");
            Assert.Equal(PreflightTargetKind.Font, issue.Target);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
