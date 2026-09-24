using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using EasyPub.Core;

var roots = args.Length == 0
    ? new[]
    {
        @"C:\Users\13168\Desktop\novel\so-novel",
        @"C:\Users\13168\Desktop\novel\tomato",
    }
    : args;
var files = roots
    .SelectMany(root => File.Exists(root) ? new[] { root } : Directory.EnumerateFiles(root, "*.txt", SearchOption.TopDirectoryOnly))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
    .ToArray();
if (files.Length == 0) throw new InvalidOperationException("没有找到 TXT 语料。");

var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
var output = Path.GetFullPath(Path.Combine("work", "corpus-baseline", stamp));
Directory.CreateDirectory(output);
var beforeHashes = files.ToDictionary(path => path, HashFile, StringComparer.OrdinalIgnoreCase);
var rows = new List<Row>();
var process = Process.GetCurrentProcess();

Console.WriteLine($"语料文件: {files.Length} 个");
Console.WriteLine($"输出目录: {output}");
Console.WriteLine("阶段: 冷加载 + 本地诊断 + 断点 + 树合法性，再对同一文件做热加载；不联网、不写回原文。");

for (var index = 0; index < files.Length; index++)
{
    var path = files[index];
    Console.WriteLine($"[{index + 1}/{files.Length}] {Path.GetFileName(path)}");
    var info = new FileInfo(path);
    var cold = await MeasureAsync(path);
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
    var hot = await MeasureAsync(path);
    var afterHash = HashFile(path);
    var unchanged = string.Equals(beforeHashes[path], afterHash, StringComparison.OrdinalIgnoreCase);
    rows.Add(cold with
    {
        HotLoadMs = hot.LoadMs,
        HotAnalyzeMs = hot.AnalyzeMs,
        HotBreakpointsMs = hot.BreakpointsMs,
        HotAllocatedBytes = hot.AllocatedBytes,
        HotWorkingSetDeltaBytes = hot.WorkingSetDeltaBytes,
        SourceUnchanged = unchanged,
        Sha256 = beforeHashes[path],
        Bytes = info.Length,
        Category = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty),
    });
}

var unique = rows.GroupBy(row => row.Sha256, StringComparer.OrdinalIgnoreCase).ToArray();
var failures = rows.Where(row => row.Error is not null).ToArray();
var summary = new
{
    generatedAt = DateTimeOffset.Now,
    roots,
    physicalFiles = rows.Count,
    uniqueSha256 = unique.Length,
    duplicateGroups = unique.Where(group => group.Count() > 1).Select(group => new
    {
        sha256 = group.Key,
        count = group.Count(),
        files = group.Select(row => row.Path).ToArray(),
    }).ToArray(),
    succeeded = rows.Count(row => row.Error is null),
    failed = failures.Length,
    sourceUnchanged = rows.Count(row => row.SourceUnchanged),
    sourceChanged = rows.Count(row => !row.SourceUnchanged),
    totalBytes = rows.Sum(row => row.Bytes),
    cold = Stats(rows.Select(row => row.LoadMs)),
    hot = Stats(rows.Select(row => row.HotLoadMs)),
    coldAnalyze = Stats(rows.Select(row => row.AnalyzeMs)),
    coldBreakpoints = Stats(rows.Select(row => row.BreakpointsMs)),
    maxWorkingSetDeltaBytes = rows.Select(row => Math.Max(row.WorkingSetDeltaBytes, row.HotWorkingSetDeltaBytes)).DefaultIfEmpty().Max(),
    rows,
};
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(summary, jsonOptions));
await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), RenderMarkdown(summary, rows, failures));

Console.WriteLine($"完成: 成功 {summary.succeeded} / 失败 {summary.failed}；源字节未改变 {summary.sourceUnchanged} / 改变 {summary.sourceChanged}");
Console.WriteLine($"唯一内容 {summary.uniqueSha256}；冷加载中位数 {summary.cold.MedianMs:F1} ms；热加载中位数 {summary.hot.MedianMs:F1} ms");
Console.WriteLine($"报告: {output}");
if (failures.Length > 0) Environment.ExitCode = 2;

static async Task<Row> MeasureAsync(string path)
{
    var process = Process.GetCurrentProcess();
    var allocatedBefore = GC.GetTotalAllocatedBytes(false);
    var workingBefore = process.WorkingSet64;
    var watch = Stopwatch.StartNew();
    try
    {
        var document = await ChapterTreeDocument.LoadAsync(path);
        watch.Stop();
        var loadMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var analysis = ChapterReviewAnalyzer.Analyze(document, cancellationToken: CancellationToken.None);
        watch.Stop();
        var analyzeMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        var breakpoints = ChapterBreakpoints.Between(document, cancellationToken: CancellationToken.None);
        watch.Stop();
        var breakpointMs = watch.Elapsed.TotalMilliseconds;
        document.CreatePlan(document.Entries);
        var body = document.Entries.Where(entry => !entry.IsFrontMatter).ToArray();
        var bodyWithContent = body.Count(entry => entry.ContentRanges.Any(range => range.EndLine >= range.StartLine));
        return new Row(
            path,
            string.Empty,
            string.Empty,
            0,
            loadMs,
            analyzeMs,
            breakpointMs,
            GC.GetTotalAllocatedBytes(false) - allocatedBefore,
            process.WorkingSet64 - workingBefore,
            0,
            0,
            0,
            0,
            0,
            document.LineCount,
            body.Length,
            bodyWithContent,
            document.Entries.Count(entry => entry.IsFrontMatter),
            document.Entries.Count(entry => entry.Level == 1 && !entry.IsFrontMatter),
            analysis.TotalGroups,
            analysis.Groups.GroupBy(group => group.Issue.Code).ToDictionary(group => group.Key, group => group.Count()),
            breakpoints.Count,
            document.RepeatedHeadingLinesSkipped,
            true,
            null);
    }
    catch (Exception exception)
    {
        watch.Stop();
        return new Row(path, string.Empty, string.Empty, 0, watch.Elapsed.TotalMilliseconds, 0, 0,
            GC.GetTotalAllocatedBytes(false) - allocatedBefore, process.WorkingSet64 - workingBefore,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), 0, 0, false,
            exception.GetType().Name + ": " + exception.Message);
    }
}

static string HashFile(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexString(SHA256.HashData(stream));
}

static StatsValue Stats(IEnumerable<double> values)
{
    var numbers = values.Order().ToArray();
    if (numbers.Length == 0) return new(0, 0, 0, 0);
    return new(numbers.Min(), numbers[numbers.Length / 2], numbers.Average(), numbers.Max());
}

static string RenderMarkdown(object summaryObject, IReadOnlyList<Row> rows, IReadOnlyList<Row> failures)
{
    var summary = (dynamic)summaryObject;
    var lines = new List<string>
    {
        "# 真实语料只读基线",
        "",
        $"生成时间：`{summary.generatedAt}`",
        "",
        "> 本报告只读加载 TXT，执行当前生产章节识别、诊断和断点计算；不联网、不改写原始 TXT。转换出口的验收统一由 P5-1 的 Kindling 报告覆盖。",
        "",
        "## 规模",
        "",
        $"- 物理文件：**{summary.physicalFiles}**",
        $"- 唯一 SHA-256 内容：**{summary.uniqueSha256}**",
        $"- 总字节：**{summary.totalBytes}**",
        $"- 成功：**{summary.succeeded}**；失败：**{summary.failed}**",
        $"- 源字节未改变：**{summary.sourceUnchanged}**；改变：**{summary.sourceChanged}**",
        "",
        "## 性能（毫秒）",
        "",
        "| 阶段 | 最小 | 中位 | 平均 | 最大 |",
        "| --- | ---: | ---: | ---: | ---: |",
        $"| 冷加载 | {summary.cold.MinMs:F1} | {summary.cold.MedianMs:F1} | {summary.cold.AverageMs:F1} | {summary.cold.MaxMs:F1} |",
        $"| 热加载 | {summary.hot.MinMs:F1} | {summary.hot.MedianMs:F1} | {summary.hot.AverageMs:F1} | {summary.hot.MaxMs:F1} |",
        $"| 本地诊断 | {summary.coldAnalyze.MinMs:F1} | {summary.coldAnalyze.MedianMs:F1} | {summary.coldAnalyze.AverageMs:F1} | {summary.coldAnalyze.MaxMs:F1} |",
        $"| 断点计算 | {summary.coldBreakpoints.MinMs:F1} | {summary.coldBreakpoints.MedianMs:F1} | {summary.coldBreakpoints.AverageMs:F1} | {summary.coldBreakpoints.MaxMs:F1} |",
        "",
        "## 结果分布",
        "",
        "| 文件 | 字节 | 行 | 章节 | 有正文 | 卷 | 提示组 | 断点 | 重复标题跳过 | 冷加载 ms | 热加载 ms |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    };
    foreach (var row in rows)
    {
        var name = Path.GetFileName(row.Path).Replace("|", "\\|");
        lines.Add($"| {name} | {row.Bytes} | {row.Lines} | {row.BodyEntries} | {row.BodyWithContent} | {row.VolumeEntries} | {row.ReviewGroups} | {row.Breakpoints} | {row.RepeatedHeadingLinesSkipped} | {row.LoadMs:F1} | {row.HotLoadMs:F1} |");
    }
    if (failures.Count > 0)
    {
        lines.AddRange(["", "## 失败", ""]);
        lines.AddRange(failures.Select(row => $"- `{row.Path}`：{row.Error}"));
    }
    return string.Join(Environment.NewLine, lines) + Environment.NewLine;
}

public sealed record StatsValue(double MinMs, double MedianMs, double AverageMs, double MaxMs);

public sealed record Row(
    string Path,
    string Category,
    string Sha256,
    long Bytes,
    double LoadMs,
    double AnalyzeMs,
    double BreakpointsMs,
    long AllocatedBytes,
    long WorkingSetDeltaBytes,
    double HotLoadMs,
    double HotAnalyzeMs,
    double HotBreakpointsMs,
    long HotAllocatedBytes,
    long HotWorkingSetDeltaBytes,
    int Lines,
    int BodyEntries,
    int BodyWithContent,
    int FrontMatterEntries,
    int VolumeEntries,
    int ReviewGroups,
    IReadOnlyDictionary<string, int> IssueCounts,
    int Breakpoints,
    int RepeatedHeadingLinesSkipped,
    bool SourceUnchanged,
    string? Error);
