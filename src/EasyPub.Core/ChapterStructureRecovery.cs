using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record ChapterStructureRecoveryResult(IReadOnlyList<ChapterTreeEntry> Entries, IReadOnlyList<string> Notes, int Groups);

/// <summary>Explicit, source-order recovery for books without usable volume headings.</summary>
public static class ChapterStructureRecovery
{
    private static readonly Regex Numbered = new(@"^第\s*(?<n>[0-9０-９一二三四五六七八九十百千零〇两]+)\s*章(?<t>.*)$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Supplement = new(@"^(序章(?:\s.*)?|.{0,20}(?:完结感言|卷末感言)|角色卡.{0,60})$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private sealed record Heading(int Line, string Title, int? Number, string Body);

    public static ChapterStructureRecoveryResult Analyze(ChapterTreeDocument document)
    {
        var headings = new List<Heading>();
        var notes = new List<string>();
        for (var line = 1; line <= document.LineCount; line++)
        {
            var raw = document.SourceLine(line)!.Text;
            var text = raw.Trim();
            var match = Numbered.Match(text);
            if (match.Success && text.Length <= 80 && ChapterDiagnostics.ParseNumber(match.Groups["n"].Value) is int n)
            {
                var body = match.Groups["t"].Value.Trim();
                if (body.StartsWith("的后半部分") || body.Contains("免费阅读") || body.Contains("已经补足"))
                { notes.Add($"第 {line} 行：排除通知/网站尾注「{text}」，保留在正文中。"); continue; }
                if (headings.LastOrDefault() is { Number: int prior } before && n > prior + 100
                    && Enumerable.Range(before.Line + 1, line - before.Line - 1).All(i => string.IsNullOrWhiteSpace(document.SourceLine(i)!.Text))
                    && body.Length > 0 && before.Body.StartsWith(body, StringComparison.Ordinal))
                { notes.Add($"第 {line} 行：额外章头「{text}」还原为正文，采用前面的「{before.Title}」。"); continue; }
                headings.Add(new(line, text, n, body));
            }
            else if (raw.Length == raw.TrimStart().Length && Supplement.IsMatch(text))
                headings.Add(new(line, text, null, ""));
        }
        if (headings.Count(h => h.Number.HasValue) < 3)
            throw new InvalidOperationException("可靠的编号章节不足，暂不适合按编号段重建。");

        // Only correct an isolated wrong number when both neighbors agree on its value.
        var numberedIndexes = Enumerable.Range(0, headings.Count).Where(i => headings[i].Number.HasValue).ToArray();
        for (var i = 1; i + 1 < numberedIndexes.Length; i++)
        {
            var index = numberedIndexes[i]; var h = headings[index];
            var left = headings[numberedIndexes[i - 1]]; var right = headings[numberedIndexes[i + 1]];
            var expected = left.Number!.Value + 1;
            if (h.Number > 1 && right.Number == expected + 1 && h.Number != expected
                && h.Title != left.Title && h.Title != right.Title && h.Body != left.Body && h.Body != right.Body)
            {
                var m = Numbered.Match(h.Title).Groups["n"];
                var title = h.Title[..m.Index] + expected + h.Title[(m.Index + m.Length)..];
                notes.Add($"第 {h.Line} 行：两侧编号支持「{h.Title}」→「{title}」（仅成品标题）。");
                headings[index] = h with { Number = expected, Title = title };
            }
        }

        var starts = new HashSet<int> { 0 };
        int? previous = null;
        var previousIndex = -1;
        for (var i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            if (h.Number is not int n) continue;
            // A reset needs a following 2/3 run, not merely a stray “第一章”.
            var next = headings.Skip(i + 1).Where(x => x.Number.HasValue).Take(2).Select(x => x.Number!.Value).ToArray();
            if (n == 1 && previous >= 10 && next.SequenceEqual(new[] { 2, 3 }))
            {
                var start = i;
                for (var j = previousIndex + 1; j < i; j++)
                    if (headings[j].Title.StartsWith("序章")) { start = j; break; }
                starts.Add(start);
            }
            previous = n; previousIndex = i;
        }
        var entries = new List<ChapterTreeEntry>();
        if (headings[0].Line > 1)
            entries.Add(new(Guid.NewGuid().ToString("N"), "前置内容", 2, true, null, [new(1, headings[0].Line - 1)]) { IsFrontMatter = true });
        var group = 0;
        for (var i = 0; i < headings.Count; i++)
        {
            var h = headings[i];
            if (starts.Contains(i))
            {
                group++;
                entries.Add(new(Guid.NewGuid().ToString("N"), $"编号段 {group}（卷名待核对）", 1, true, null, []) { RecognitionSource = "manual", HeadingLevel = 1 });
                notes.Add($"编号段 {group} 从第 {h.Line} 行「{h.Title}」开始；这是推测分组，可编辑名称和层级。");
            }
            var end = i + 1 < headings.Count ? headings[i + 1].Line - 1 : document.LineCount;
            entries.Add(new(Guid.NewGuid().ToString("N"), h.Title, 2, true, h.Line,
                end > h.Line ? [new(h.Line + 1, end)] : []) { RecognitionSource = "manual", HeadingLevel = 2 });
        }
        foreach (var duplicate in headings.Where(h => !h.Title.StartsWith("序章")).GroupBy(h => h.Title).Where(g => g.Count() > 1))
        {
            var bodies = duplicate.Select(h => entries.First(e => e.TitleLineNumber == h.Line))
                .Select(e => string.Join("\n", e.ContentRanges.SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1))
                    .Select(line => document.SourceLine(line)!.Text.Trim()).Where(t => t.Length > 0))).ToArray();
            var same = bodies.Distinct(StringComparer.Ordinal).Count() == 1;
            notes.Add($"{(same ? "同名且正文一致" : "同名但正文不同")}，保留待核对：「{duplicate.Key}」，原文行 {string.Join("、", duplicate.Select(h => h.Line))}。不会按标题删除正文。");
        }
        ChapterTreeDocument.ValidatePlan(document.CreatePlan(entries), document.LineCount);
        // Rebuilding levels cannot cure duplicated source blocks. Put this warning first,
        // before synthetic-volume suggestions, and preserve every source line.
        notes.InsertRange(0, ChapterRepeatedSequences.Find(document, entries).Select(g => g.Issue.Message));
        return new(entries, notes, group);
    }
}
