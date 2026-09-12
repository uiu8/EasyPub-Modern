using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record ChapterReviewGroup(ConversionPreflightIssue Issue, string Category,
    IReadOnlyList<string> NodeIds, IReadOnlyList<int> Lines, IReadOnlyList<ConversionPreflightIssue> RelatedIssues);

public sealed record ChapterReviewAnalysis(IReadOnlyList<ChapterReviewGroup> Groups, int TotalGroups);
public sealed record NumericChapterSuggestion(string Pattern, int MinimumBodyLines, IReadOnlyList<int> Lines);

/// <summary>Shared read-only review grouping. A group is evidence, never permission to alter text.</summary>
public static class ChapterReviewAnalyzer
{
    public static NumericChapterSuggestion? FindNumericChapters(ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry>? entries = null, CancellationToken cancellationToken = default)
    {
        if ((entries ?? document.Entries).Any(e => !e.IsFrontMatter)) return null;
        var text = document.SourceLines.Select(l => l.Text).ToArray();
        // A suggestion is conservative: require two separated candidates with real body text.
        var minimum = Math.Max(3, document.RecognitionOptions.NumericHeadingMinimumBodyLines);
        foreach (var pattern in new[] { document.RecognitionOptions.NumericHeadingPattern, NumericHeadingRule.DefaultPattern }.Distinct())
        {
            var regex = NumericHeadingRule.Compile(pattern);
            var candidates = new List<int>();
            for (var i = 0; i < text.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (NumericHeadingRule.Matches(regex, text[i])) candidates.Add(i);
            }
            var accepted = NumericHeadingFilter.AcceptedLines(text, candidates, minimum).Order().Select(i => i + 1).ToArray();
            if (accepted.Length >= 2) return new(pattern, minimum, accepted);
        }
        return null;
    }

    public static ChapterReviewAnalysis Analyze(ChapterTreeDocument document, IReadOnlyList<ChapterTreeEntry>? currentEntries = null,
        bool detectUnrecognized = true, int maximumGroups = 200, CancellationToken cancellationToken = default)
    {
        var entries = currentEntries ?? document.Entries;
        var raw = ChapterDiagnostics.Inspect(document, cancellationToken, detectUnrecognized, entries, int.MaxValue).ToList();
        var parentIds = new Dictionary<string, string>();
        var stack = new Stack<ChapterTreeEntry>();
        foreach (var entry in entries)
        {
            if (entry.IsFrontMatter) { stack.Clear(); parentIds[entry.Id] = ""; continue; }
            while (stack.Count > 0 && stack.Peek().Level >= entry.Level) stack.Pop();
            parentIds[entry.Id] = stack.Count > 0 ? stack.Peek().Id : "";
            stack.Push(entry);
        }
        var groups = new List<ChapterReviewGroup>();
        if (FindNumericChapters(document, entries, cancellationToken) is { } suggestion)
        {
            var first = suggestion.Lines[0];
            var issue = new ConversionPreflightIssue(document.SourcePath, PreflightSeverity.Warning, "numeric_chapters_suspected",
                $"未识别到章节，但发现 {suggestion.Lines.Count} 处疑似数字章节（每章至少 {suggestion.MinimumBodyLines} 行非空正文）。可在章节工作台核对原文后，一键识别本书；不会修改全局设置。",
                PreflightTargetKind.Chapters, first);
            groups.Add(new(issue, "疑似漏识别", entries.Where(e => e.IsFrontMatter).Select(e => e.Id).ToArray(), suggestion.Lines, [issue]));
        }
        var consumed = new HashSet<ConversionPreflightIssue>();
        var numeric = NumericHeadingRule.Compile(document.RecognitionOptions.NumericHeadingPattern);
        bool IsNumeric(ChapterTreeEntry entry) => !entry.IsFrontMatter && entry.RecognitionSource is not ("manual" or "pattern")
            && entry.TitleLineNumber is int line && NumericHeadingRule.Matches(numeric, document.SourceLine(line)?.Text ?? "");
        void Add(ConversionPreflightIssue issue, string category, IEnumerable<ChapterTreeEntry> nodes, IEnumerable<ConversionPreflightIssue> related)
        {
            var members = nodes.ToArray();
            var originals = related.ToArray();
            foreach (var item in originals) consumed.Add(item);
            var lines = members.Select(n => n.TitleLineNumber).Concat(originals.Select(i => i.LineNumber)).Append(issue.LineNumber)
                .OfType<int>().Distinct().Order().ToArray();
            groups.Add(new(issue, category, members.Select(n => n.Id).ToArray(), lines, originals));
        }
        // Scan the complete flattened order; require same parent and leaves, not filtered neighbors.
        for (var i = 1; i < entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsNumeric(entries[i]) || IsNumeric(entries[i - 1]) || entries[i - 1].IsFrontMatter
                || parentIds[entries[i].Id] != parentIds[entries[i - 1].Id]) continue;
            var end = i;
            while (end + 1 < entries.Count && IsNumeric(entries[end + 1])
                && parentIds[entries[end + 1].Id] == parentIds[entries[i].Id]) end++;
            var members = entries.Skip(i).Take(end - i + 1).ToArray();
            if (members.Length > 1 && end + 1 < entries.Count && parentIds[entries[end + 1].Id] == parentIds[entries[i].Id]
                && members.Take(members.Length - 1).All(n => n.ContentRanges.Sum(r => r.EndLine - r.StartLine + 1) <= 3))
            {
                var lines = members.Select(n => n.TitleLineNumber).ToHashSet();
                var related = raw.Where(issue => lines.Contains(issue.LineNumber)
                    && issue.Code is "chapter_number_order" or "chapter_number_gap").ToArray();
                var issue = new ConversionPreflightIssue(document.SourcePath, PreflightSeverity.Warning, "numeric_body_group",
                    $"普通章节之间有 {members.Length} 个连续数字条目，多数正文很短，疑似正文列举（请核对原文）",
                    PreflightTargetKind.Chapters, members[0].TitleLineNumber);
                Add(issue, "疑似正文误识别", members, related);
            }
            i = end;
        }
        foreach (var issue in raw)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consumed.Contains(issue)) continue;
            var node = entries.FirstOrDefault(n => n.TitleLineNumber == issue.LineNumber)
                ?? entries.FirstOrDefault(n => issue.LineNumber is int line && n.ContentRanges.Any(r => line >= r.StartLine && line <= r.EndLine));
            if (issue.Code == "chapter_duplicate" && node is not null)
            {
                var same = entries.Where(n => !n.IsFrontMatter && parentIds[n.Id] == parentIds[node.Id]
                    && n.Level == node.Level && n.Title.Trim() == node.Title.Trim()).ToArray();
                var lines = same.Select(n => n.TitleLineNumber).ToHashSet();
                Add(issue, "重复标题", same, raw.Where(r => r.Code == "chapter_duplicate" && lines.Contains(r.LineNumber)));
                continue;
            }
            var category = issue.Code switch
            {
                "volume_number_duplicate" or "chapter_level_gap" => "卷与层级",
                "chapter_unrecognized" => "疑似漏识别",
                "chapter_number_order" or "chapter_number_gap" => node is not null && IsNumeric(node) ? "疑似正文误识别" : "编号异常",
                _ => "其他提醒"
            };
            var associated = node is null ? Array.Empty<ChapterTreeEntry>() : new[] { node };
            if (issue.Code == "volume_number_duplicate")
            {
                // The diagnostic already carries the first occurrence; include it for side-by-side navigation.
                var first = Regex.Match(issue.Message, @"首次在原文第\s*(\d+)\s*行", RegexOptions.None, TimeSpan.FromMilliseconds(200));
                if (first.Success && int.TryParse(first.Groups[1].Value, out var line)
                    && entries.FirstOrDefault(n => n.TitleLineNumber == line) is { } origin) associated = new[] { origin }.Concat(associated).ToArray();
            }
            Add(issue, category, associated, [issue]);
        }
        var ordered = groups.OrderBy(g => g.Issue.LineNumber ?? int.MaxValue).ToArray();
        return new(ordered.Take(Math.Max(1, maximumGroups)).ToArray(), ordered.Length);
    }
}
