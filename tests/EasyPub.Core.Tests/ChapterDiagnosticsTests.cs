using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ChapterDiagnosticsTests
{
    [Theory]
    [InlineData("第66章 甲", "第66章 乙", "第68章 丙", "第67章 乙")]
    [InlineData("第66章 甲", "第66章 甲", "第68章 丙", null)]
    [InlineData("第66章 甲（1）", "第66章 甲（2）", "第68章 丙", null)]
    [InlineData("第66章 甲", "第66章 乙", "第69章 丙", null)]
    public void Correction_requires_both_neighbors(string previous, string current, string next, string? expected)
        => Assert.Equal(expected, ChapterDiagnostics.SuggestCorrectedTitle(previous, current, next));

    [Theory]
    [InlineData("第25章 逃避可耻但有用（2）", "第25章 逃避可耻但有用（3）", false)]
    [InlineData("第25章 标题 (4)", "第25章 标题 (5，加更！)", false)]
    [InlineData("第25章 标题（2）", "第25章 标题（2）", true)]
    [InlineData("第25章 标题（2）", "第25章 别的标题（3）", true)]
    [InlineData("第25章 标题（2）", "第24章 标题（3）", true)]
    public async Task Consecutive_parts_do_not_report_duplicate_numbers(string first, string second, bool expectedWarning)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, first + "\n正文\n" + second + "\n正文");
            var document = await ChapterTreeDocument.LoadAsync(path);
            Assert.Equal(expectedWarning, ChapterDiagnostics.Inspect(document).Any(i => i.Code == "chapter_number_order"));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task Reports_gaps_duplicates_and_unrecognized_headings_without_mutation()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        const string source = "第一章 开始\n正文\n第三章 后续\n正文\n第三章 后续\n正文\n第四回 漏识别\n正文";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var document = await ChapterTreeDocument.LoadAsync(path, @"^第[一三]+章");
            var issues = ChapterDiagnostics.Inspect(document);
            Assert.Contains(issues, i => i.Code == "chapter_number_gap" && i.LineNumber == 3);
            Assert.Contains(issues, i => i.Code == "chapter_duplicate" && i.LineNumber == 5);
            Assert.Contains(issues, i => i.Code == "chapter_unrecognized" && i.LineNumber == 7);
            Assert.All(issues, i => Assert.Equal(PreflightSeverity.Warning, i.Severity));
            Assert.Equal(source, await File.ReadAllTextAsync(path));
            Assert.DoesNotContain(ChapterDiagnostics.Inspect(document, detectUnrecognized: false), i => i.Code == "chapter_unrecognized");
            var report = await new ConversionPreflightInspector().InspectAsync([new(path, Path.ChangeExtension(path, ".epub")) { AutomaticChecks = new() { Targets = [], Cleanup = new() } }]);
            Assert.DoesNotContain(report.Issues, i => i.Target == PreflightTargetKind.Chapters);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Explicit_volumes_allow_numbering_to_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, "第一卷 开始\n第一章 甲\n第二章 乙\n第二卷 继续\n第一章 甲\n第二章 乙\n番外\n正文里提到第九章的内容。");
            var doc = await ChapterTreeDocument.LoadAsync(path, @"^第[一二]+[卷章]");
            Assert.Empty(ChapterDiagnostics.Inspect(doc));
        }
        finally { File.Delete(path); }
    }
}
