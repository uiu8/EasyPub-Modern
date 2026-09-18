using System.Text.RegularExpressions;

namespace EasyPub.Core;

/// <summary>Read-only guidance based on the current tree, never a replacement for gap repair.</summary>
public static class ChapterStructureSuggestion
{
    private static readonly Regex Heading = new(@"^第\s*([0-9０-９一二三四五六七八九十百千零〇两]+)\s*章(.*)$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Volume = new(@"^第[0-9一二三四五六七八九十百千零〇两]+[卷部篇集]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static ChapterReviewGroup? Find(ChapterTreeDocument doc, IReadOnlyList<ChapterTreeEntry> entries)
    {
        var stack = new List<ChapterTreeEntry>();
        var numbered = new List<(ChapterTreeEntry Entry, int Number, string Body, string Scope)>();
        var evidence = new List<(ChapterTreeEntry Entry, string Reason)>();
        var falseVolumes = 0;
        foreach (var e in entries)
        {
            if (e.IsFrontMatter) { stack.Clear(); continue; }
            while (stack.Count > 0 && stack[^1].Level >= e.Level) stack.RemoveAt(stack.Count - 1);
            var title = e.Title.Trim();
            var suspect = Volume.IsMatch(title) && (title.Length > 35 || title.Contains("结束之后") || title.Contains("实际上")
                || title.Contains("开始之后") || title.Contains("完结感言") || title.Contains("就这样结束") || title.Contains("内容为"));
            if (suspect) { falseVolumes++; evidence.Add((e, $"第 {e.TitleLineNumber} 行：疑似正文或感言被作为卷边界「{title}」")); }
            var m = Heading.Match(title);
            if (m.Success && ChapterDiagnostics.ParseNumber(m.Groups[1].Value) is int number)
                numbered.Add((e, number, m.Groups[2].Value.Trim(), string.Join("/", stack.Select(n => n.Id))));
            stack.Add(e);
        }
        var resets = 0; var extras = 0;
        for (var i = 1; i < numbered.Count; i++)
        {
            var a = numbered[i - 1]; var b = numbered[i];
            if (b.Number == 1 && a.Number >= 10 && a.Scope == b.Scope && i + 2 < numbered.Count
                && numbered[i + 1].Number == 2 && numbered[i + 2].Number == 3
                && numbered[i + 1].Scope == b.Scope && numbered[i + 2].Scope == b.Scope)
            { resets++; evidence.Add((b.Entry, $"第 {b.Entry.TitleLineNumber} 行：同一分组内 {a.Number} → 1 → 2 → 3，疑似缺少分卷边界")); }
            if (a.Scope == b.Scope && b.Number > a.Number + 100 && b.Body.Length > 0 && a.Body.StartsWith(b.Body, StringComparison.Ordinal)
                && a.Entry.TitleLineNumber is int start && b.Entry.TitleLineNumber is int end && end > start && end - start <= 4
                && Enumerable.Range(start + 1, end - start - 1).All(line => string.IsNullOrWhiteSpace(doc.SourceLine(line)?.Text)))
            { extras++; evidence.Add((b.Entry, $"第 {end} 行：相邻章头使用不同编号 {a.Number} / {b.Number}，疑似额外章头")); }
        }
        var kinds = (resets > 0 ? 1 : 0) + (falseVolumes > 0 ? 1 : 0) + (extras > 0 ? 1 : 0);
        if (resets < 2 && kinds < 2) return null;
        var lines = evidence.Select(e => e.Entry.TitleLineNumber).OfType<int>().Distinct().Order().ToArray();
        var issue = new ConversionPreflightIssue(doc.SourcePath, PreflightSeverity.Warning, "chapter_structure_suggested",
            $"建议预览结构整理：{resets} 处同组编号重置、{falseVolumes} 处疑似卷边界、{extras} 处额外章头。可先逐处修复漏识别标题；整理方案需预览后应用。\n"
                + string.Join("\n", evidence.Select(e => e.Reason)), PreflightTargetKind.Chapters, lines.FirstOrDefault());
        return new(issue, IssueCategory.ForCode(issue.Code) ?? ReviewCategories.Structure, evidence.Select(e => e.Entry.Id).Distinct().ToArray(), lines, [issue]);
    }
}
