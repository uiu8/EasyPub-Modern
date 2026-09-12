using System.Diagnostics;
using System.Text;
using EasyPub.Core;
using Xunit.Abstractions;

namespace EasyPub.Core.Tests;

public sealed class LongTextPerformanceTests
{
    private readonly ITestOutputHelper _output;

    public LongTextPerformanceTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Long_text_opening_pipeline_reports_baseline()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-long-open-{Guid.NewGuid():N}.txt");
        var builder = new StringBuilder(capacity: 32 * 1024 * 1024);
        for (var chapter = 1; chapter <= 8_000; chapter++)
        {
            builder.Append("第").Append(chapter).Append("章 长篇小说测试标题").AppendLine();
            for (var paragraph = 0; paragraph < 28; paragraph++)
                builder.Append("这是一段用于模拟长篇中文小说的正文内容，包含足够的字符以覆盖打开、识别和预览路径。").AppendLine();
        }

        await File.WriteAllTextAsync(path, builder.ToString(), new UTF8Encoding(false));
        try
        {
            var request = new ConversionRequest(
                path,
                Path.Combine(Path.GetTempPath(), $"easypub-long-open-{Guid.NewGuid():N}.epub"),
                Options: new ConversionOptions
                {
                    TocHierarchy = new TocHierarchyOptions(),
                    TextEncoding = TextEncodingMode.Utf8,
                })
            {
                AutomaticChecks = new AutomaticCheckOptions
                {
                    Targets = [PreflightTargetKind.Chapters],
                    Cleanup = new TextCleanupOptions { RemoveSiteNotices = true },
                },
            };

            var documentCache = new ChapterTreeDocumentCache();
            var loadWatch = Stopwatch.StartNew();
            var document = await documentCache.GetOrLoadAsync(
                path,
                chapterPattern: null,
                hierarchy: request.Options!.TocHierarchy,
                encoding: TextEncodingMode.Utf8,
                existingPlan: null);
            loadWatch.Stop();

            // Warm the test process so the budget reflects the long-text work rather
            // than one-time JIT/setup noise from the first regex invocation.
            await new ConversionPreflightInspector(documentCache).InspectAsync([request]);
            var analysisWatch = Stopwatch.StartNew();
            var report = (await new BookAnalysisCoordinator(documentCache: documentCache).AnalyzeAsync([request])).Report;
            analysisWatch.Stop();

            _output.WriteLine($"LONG_TEXT_BYTES={new FileInfo(path).Length}");
            _output.WriteLine($"LONG_TEXT_CHAPTERS={document.Entries.Count}");
            _output.WriteLine($"LONG_TEXT_LOAD_MS={loadWatch.Elapsed.TotalMilliseconds:F0}");
            _output.WriteLine($"LONG_TEXT_PREFLIGHT_MS={analysisWatch.Elapsed.TotalMilliseconds:F0}");
            Assert.True(document.Entries.Count > 8_000);
            Assert.False(report.HasErrors);
            Assert.True(
                analysisWatch.Elapsed < TimeSpan.FromMilliseconds(400),
                $"长文本预检查超过目标预算：{analysisWatch.Elapsed.TotalMilliseconds:F0} ms");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
