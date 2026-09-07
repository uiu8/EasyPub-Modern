using System.IO.Compression;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class QualitySafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Atomic_output_preserves_previous_file_after_partial_write_failure(bool cancel)
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-atomic-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var output = Path.Combine(root, "book.mobi");
        await File.WriteAllTextAsync(output, "existing artifact");
        using var cancellation = new CancellationTokenSource();
        try
        {
            var error = await Record.ExceptionAsync(() => AtomicOutputWriter.WriteAsync(output, async (stream, token) =>
            {
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, token);
                if (cancel) cancellation.Cancel();
                else throw new IOException("injected write failure");
            }, cancellation.Token));
            Assert.NotNull(error);
            if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.IsType<IOException>(error);
            Assert.Equal("existing artifact", await File.ReadAllTextAsync(output));
            Assert.Single(Directory.GetFiles(root));
            await AtomicOutputWriter.WriteAsync(output, (stream, token) => stream.WriteAsync(new byte[] { 4, 5 }, token).AsTask(), default);
            Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsafe_reflow_is_rejected_without_affecting_basic_epub_inspection(bool collapsedToc)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-reflow-{Guid.NewGuid():N}.txt");
        var output = Path.ChangeExtension(path, ".epub");
        await File.WriteAllTextAsync(path, "第一章 开始\n甲段\n第二章 后续\n乙段独有末尾");
        try
        {
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(path, output));
            EpubInspectionService.ValidateCompatibleReflow(output);
            using (var archive = ZipFile.Open(output, ZipArchiveMode.Update))
            {
                var name = collapsedToc ? "OEBPS/toc.ncx" : "OEBPS/chapter1.html";
                var entry = archive.GetEntry(name)!;
                string content;
                using (var reader = new StreamReader(entry.Open())) content = reader.ReadToEnd();
                if (collapsedToc) Assert.Contains("chapter2.html", content);
                content = collapsedToc ? content.Replace("chapter2.html", "chapter1.html#second") : content.Replace("<p ", "<div ").Replace("</p>", "</div>");
                entry.Delete();
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(content);
            }
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => EpubCompatibilityImporter.ImportAsync(output, default));
            Assert.Contains("保留原 EPUB 版式", error.Message);
            Assert.True(EpubInspectionService.Inspect(output).SpineDocumentCount > 0);
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Fact]
    public void Cleanup_preview_merge_keeps_existing_rules_and_exclusions()
    {
        var custom = new TextCleanupCustomRule { Pattern = "保留配置" };
        var current = new TextCleanupOptions { NormalizePunctuation = true, ChineseVariant = ChineseVariantConversion.ToTraditional,
            CustomRules = [custom], ExcludedChangeKeys = ["keep"] };
        var result = AutomaticCheckOptions.MergeCleanupForPreview(current, new() { RemoveSiteNotices = true,
            ChineseVariant = ChineseVariantConversion.ToSimplified, CustomRules = [custom with { Replacement = "不应覆盖" }] });
        Assert.True(result.NormalizePunctuation);
        Assert.True(result.RemoveSiteNotices);
        Assert.Equal(ChineseVariantConversion.ToTraditional, result.ChineseVariant);
        Assert.Equal(custom, Assert.Single(result.CustomRules));
        Assert.Equal("keep", Assert.Single(result.ExcludedChangeKeys));
        Assert.False(current.RemoveSiteNotices);
    }

    [Fact]
    public void Structurally_invalid_artifact_is_not_counted_as_success()
    {
        var request = new ConversionRequest("in.txt", "out.epub");
        var validation = new ArtifactValidationReport("out.epub", "epub",
            [new(ArtifactValidationSeverity.Error, "broken", "损坏")], DateTimeOffset.UtcNow, false);
        Assert.False(new BatchJobOutcome(request, new("in.txt", "out.epub", 1, 10, TimeSpan.Zero), null, Validation: validation).Succeeded);
    }

    [Theory]
    [InlineData("甲段", "甲段\n新增一行")]
    [InlineData("甲段\n第二章 后续", "甲段第二章 后续")]
    [InlineData("甲段\n第二章 后续", "\n甲段第二章 后续")]
    public async Task Applied_multiline_cleanup_with_saved_tree_must_not_silently_drop_text(string pattern, string replacement)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-quality-{Guid.NewGuid():N}.txt");
        var output = Path.ChangeExtension(path, ".epub");
        await File.WriteAllTextAsync(path, "第一章 开始\n甲段\n第二章 后续\n乙段独有末尾");
        await File.WriteAllTextAsync(output, "existing artifact");
        try
        {
            var tree = await ChapterTreeDocument.LoadAsync(path);
            var request = new ConversionRequest(path, output, Options: new ConversionOptions
            {
                TextCleanup = new() { CustomRules = [new() { Pattern = pattern, Replacement = replacement, Multiline = true }] }
            }) { ChapterTree = tree.CreatePlan(tree.Entries) };
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => new EasyPubConverter().ConvertAsync(request));
            Assert.Contains("跨行", error.Message);
            Assert.Equal("existing artifact", await File.ReadAllTextAsync(output));
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Fact]
    public async Task Empty_automatic_checks_do_not_claim_conversion_readiness()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-quality-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n正文");
        try
        {
            var request = new ConversionRequest(path, Path.ChangeExtension(path, ".mobi"))
            { AutomaticChecks = new() { Targets = [], Cleanup = new() } };
            var result = await new BookAnalysisCoordinator().AnalyzeAsync([request]);
            Assert.Equal("未选择检查项", result.Books[0].Readiness.Label);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Nonmatching_multiline_rule_does_not_block_saved_tree()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-quality-{Guid.NewGuid():N}.txt");
        var output = Path.ChangeExtension(path, ".epub");
        await File.WriteAllTextAsync(path, "第一章 开始\n乙段独有末尾");
        try
        {
            var tree = await ChapterTreeDocument.LoadAsync(path);
            await new EasyPubConverter().ConvertAsync(new ConversionRequest(path, output,
                Options: new() { TextCleanup = new() { CustomRules = [new() { Pattern = "不匹配", Replacement = "\n", Multiline = true }] } })
                { ChapterTree = tree.CreatePlan(tree.Entries) });
            using var archive = ZipFile.OpenRead(output);
            using var reader = new StreamReader(archive.GetEntry("OEBPS/chapter1.html")!.Open());
            Assert.Contains("乙段独有末尾", await reader.ReadToEndAsync());
        }
        finally { File.Delete(path); File.Delete(output); }
    }

    [Fact]
    public async Task Invalid_check_regex_is_classified_as_rule_error()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-quality-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n正文");
        try
        {
            var result = await new BookAnalysisCoordinator().AnalyzeAsync([new ConversionRequest(path, Path.ChangeExtension(path, ".epub"))
                { AutomaticChecks = new() { Cleanup = new() { CustomRules = [new() { Pattern = "(", IsRegex = true }] } } }]);
            Assert.Contains(result.Report.Issues, issue => issue.Code == "cleanup_rule_invalid");
            Assert.DoesNotContain(result.Report.Issues, issue => issue.Code == "input_unreadable");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Already_planned_cleanup_is_not_reported_as_unhandled()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-quality-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n更多精彩小说，请访问：www.example.org\n正文");
        try
        {
            var cleanup = new TextCleanupOptions { RemoveSiteNotices = true };
            var request = new ConversionRequest(path, Path.ChangeExtension(path, ".epub"), Options: new() { TextCleanup = cleanup })
            { AutomaticChecks = new() { Cleanup = cleanup } };
            var result = await new BookAnalysisCoordinator().AnalyzeAsync([request]);
            Assert.DoesNotContain(result.Report.Issues, issue => issue.Code == "cleanup_check" && issue.Severity == PreflightSeverity.Warning);
            Assert.Contains(result.Report.Issues, issue => issue.Message.Contains("已计划"));
        }
        finally { File.Delete(path); }
    }
}
