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
        bool detectUnrecognized = true, int maximumGroups = 200, CancellationToken cancellationToken = default,
        ReferenceCatalog? reference = null)
    {
        var entries = currentEntries ?? document.Entries;
        // A tree reconciled against an official directory already answers "which chapters exist".
        // These heuristics exist to guess exactly that, so once the reference has been applied their
        // findings describe the source text rather than a problem still left to fix.
        var verification = ReferenceVerificationIndex.Build(entries);
        // **逐 finding 判断，不再用全局 bool，也不再在被调用处各自 Where 过滤。**
        // 原来的 referenceAligned 要求"所有非卷条目都已验证"，于是用户手工改过一个节点，
        // 六类本地提醒就全部重新打开 —— 而他不知道那是自己那次编辑造成的。
        SuppressionDecision Decide(string code, FindingScope scope) =>
            FindingSuppressionPolicy.Decide(code, scope, verification);
        // 一个码属于哪一类，全软件只有 IssueResolutionPolicy 一处定义。这里不再自己算 ——
        // 原来这里的归因与报告窗口那份并不一致（chapter_number_gap 在这里被归成
        // 「编号与层级」/「误识别」，在表里它是「漏识别」）。
        static string CategoryOf(string code) =>
            IssueCategory.ForCode(code) ?? ReviewCategories.Other;
        // **这里不再过滤。** 六类以前在这里被逐行滤掉，另外几类在各自的调用点被全局滤掉 ——
        // 四处对"哪些 finding 能被动用目录证据推翻"的判断并不一致（内容类被误伤，而
        // chapter_number_gap 本该照常报告）。现在抑制只在 Add(...) 里发生一次。
        var raw = ChapterDiagnostics.Inspect(document, cancellationToken, detectUnrecognized, entries, int.MaxValue)
            .ToList();
        // A duplicate-heavy book asks for these same title lines repeatedly. Build the
        // index once; preserve every diagnostic (including multiple codes at one line).
        var rawByLine = raw.ToLookup(issue => issue.LineNumber);
        var entryByLine = entries.ToLookup(entry => entry.TitleLineNumber);
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
        var titleGroups = entries.Where(entry => !entry.IsFrontMatter)
            .ToLookup(entry => (Parent: parentIds[entry.Id], entry.Level, Title: entry.Title.Trim()));
        // consumed 必须在这里声明：Add(...) 是局部函数，而它从下面第一个循环就被调用。
        var consumed = new HashSet<ConversionPreflightIssue>();
        // 抑制统一在 Add(...) 里做 —— 这里不再各自 Where 过滤"已由目录验证的 owner"。
        foreach (var candidates in UnnumberedHeadings.Find(document, entries).GroupBy(p => p.OwnerId))
        {
            var first = candidates.First();
            var lines = candidates.Select(c => c.Line).ToArray();
            var issue = new ConversionPreflightIssue(document.SourcePath, PreflightSeverity.Warning, "chapter_unnumbered",
                $"异常长章中发现 {candidates.Count()} 处疑似无编号标题。可在目录辅助修复中调整行数阈值、获取参考目录并预览补建；不会自动拆分正文。",
                PreflightTargetKind.Chapters, first.Line);
            AddScope(issue, [first.OwnerId], lines, [issue]);
        }
        foreach (var gap in MissingChapterHeadings.Find(document, entries, cancellationToken).GroupBy(c => c.NextLine))
        {
            var candidates = gap.ToArray();
            var owners = candidates.Select(c => c.OwnerId).Distinct().ToArray();
            var lines = candidates.Select(c => c.Line).ToArray();
            var issue = new ConversionPreflightIssue(document.SourcePath, PreflightSeverity.Warning, "chapter_heading_typo",
                $"跳章区间发现 {candidates.Length} 个疑似漏识别标题：" + string.Join("；", candidates.Select(c => $"第 {c.Line} 行：{c.Original} → {c.Title}")),
                PreflightTargetKind.Chapters, candidates[0].Line);
            AddScope(issue, owners, lines, [issue]);
        }
        if (FindNumericChapters(document, entries, cancellationToken) is { } suggestion)
        {
            var first = suggestion.Lines[0];
            var issue = new ConversionPreflightIssue(document.SourcePath, PreflightSeverity.Warning, "numeric_chapters_suspected",
                $"未识别到章节，但发现 {suggestion.Lines.Count} 处疑似数字章节（每章至少 {suggestion.MinimumBodyLines} 行非空正文）。可在章节工作台核对原文后，一键识别本书；不会修改全局设置。",
                PreflightTargetKind.Chapters, first);
            groups.Add(new(issue, CategoryOf(issue.Code), entries.Where(e => e.IsFrontMatter).Select(e => e.Id).ToArray(), suggestion.Lines, [issue]));
        }
        if (ChapterStructureSuggestion.Find(document, entries) is { } structure) groups.Add(structure);
        // **不再按"是否已验证"预过滤**：正文重复属于"目录证据推翻不了"的一类 ——
        // 两边都被目录验证过，也不能说明正文没有被错误复制。显不显示交给 Add(...) 的策略。
        var duplicateBodies = ChapterContentDuplicates.Find(document, entries, cancellationToken).ToArray();
        // 正文重复这一类**不可**被目录证据 supersede —— 目录验证的是章节身份，不是正文内容。
        // 逐条过策略而不是无条件放行，是为了让"哪一类可以被 supersede"只有一个定义处。
        foreach (var repeated in ChapterRepeatedSequences.Find(document, entries, duplicateBodies))
        {
            if (Decide(repeated.Issue.Code, FindingScope.Of(repeated)).IsSuppressed) continue;
            groups.Add(repeated);
        }
        var byId = entries.ToDictionary(e => e.Id);
        foreach (var pair in duplicateBodies)
        {
            var first = byId[pair.FirstId];
            var second = byId[pair.SecondId];
            var description = pair.Exact ? "正文完全相同（忽略空白）" : $"正文相似度 {pair.Similarity:P1}（疑似重复，请对比）";
            var issue = new ConversionPreflightIssue(document.SourcePath, PreflightSeverity.Warning, "chapter_content_duplicate",
                $"同名章节“{second.Title}”：{description}；正文 {pair.FirstLines} / {pair.SecondLines} 行，{pair.FirstCharacters} / {pair.SecondCharacters} 字符。可对比后选择删除一份，原始 TXT 不变。",
                PreflightTargetKind.Chapters, second.TitleLineNumber);
            groups.Add(new(issue, CategoryOf(issue.Code), [first.Id, second.Id], new[] { first.TitleLineNumber, second.TitleLineNumber }.OfType<int>().ToArray(), [issue]));
            // The content comparison supersedes duplicate-title / same-number noise at this copy,
            // but never consumes a gap or a missing-heading diagnosis.
            foreach (var old in rawByLine[second.TitleLineNumber].Where(i => i.Code is "chapter_duplicate" or "chapter_number_order")) consumed.Add(old);
        }
        // The same prose under two different headings. A same-title comparison cannot see this pair,
        // and a release that repeats a stretch and renumbers it produces exactly that — so the finding
        // belongs next to the duplicate it is, not in a category of its own.
        //
        // 逐对判断在下面（"两边都验证过"才跳过），所以这里不再需要全局开关。
        foreach (var pair in ChapterContentDuplicates.FindCrossTitle(document, entries, cancellationToken: cancellationToken))
        {
            var first = entryByLine[pair.FirstLine].FirstOrDefault();
            var second = entryByLine[pair.SecondLine].FirstOrDefault();
            if (first is null || second is null) continue;
            var description = pair.Exact ? "正文完全相同（忽略空白）" : $"正文相似度 {pair.Similarity:P1}";
            var issue = new ConversionPreflightIssue(document.SourcePath, PreflightSeverity.Warning, "chapter_cross_title_duplicate",
                $"“{first.Title}”（第 {pair.FirstLine} 行）与“{second.Title}”（第 {pair.SecondLine} 行）标题不同、{description}。"
                + "多半是源文件重复拼接后改了标题；请核对哪一份该留，软件不会自动删除、也不会按内容替换标题。",
                PreflightTargetKind.Chapters, pair.SecondLine);
            groups.Add(new(issue, CategoryOf(issue.Code), [first.Id, second.Id],
                new[] { pair.FirstLine, pair.SecondLine }.OfType<int>().ToArray(), [issue]));
        }
        var numeric = NumericHeadingRule.Compile(document.RecognitionOptions.NumericHeadingPattern);
        bool IsNumeric(ChapterTreeEntry entry) => !entry.IsFrontMatter && entry.RecognitionSource is not ("manual" or "pattern")
            && entry.TitleLineNumber is int line && NumericHeadingRule.Matches(numeric, document.SourceLine(line)?.Text ?? "");
        // **全流程唯一一处抑制判断。** 两个入口都汇到这里，所以"哪一类可以被目录证据推翻"
        // 只有 FindingSuppressionPolicy 一个定义处。
        //
        // 注意局部函数**不能重载**（它们是局部变量，同名即冲突），所以下面两个入口取不同的名字。
        void AddScope(ConversionPreflightIssue issue, IReadOnlyList<string> ids,
            IReadOnlyList<int> lines, IEnumerable<ConversionPreflightIssue> related)
        {
            var originals = related.ToArray();
            // 先消费 related，再决定显不显示 —— 否则被抑制的那一组会把它的关联条目漏成碎片。
            foreach (var item in originals) consumed.Add(item);
            if (Decide(issue.Code, new FindingScope(ids, lines)).IsSuppressed) return;
            groups.Add(new(issue, CategoryOf(issue.Code), ids, lines, originals));
        }
        void Add(ConversionPreflightIssue issue, IEnumerable<ChapterTreeEntry> nodes, IEnumerable<ConversionPreflightIssue> related)
        {
            var members = nodes.ToArray();
            var originals = related.ToArray();
            var lines = members.Select(n => n.TitleLineNumber).Concat(originals.Select(i => i.LineNumber)).Append(issue.LineNumber)
                .OfType<int>().Distinct().Order().ToArray();
            AddScope(issue, members.Select(n => n.Id).ToArray(), lines, originals);
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
                Add(issue, members, related);
            }
            i = end;
        }
        foreach (var issue in raw)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (consumed.Contains(issue)) continue;
            var node = entryByLine[issue.LineNumber].FirstOrDefault()
                ?? entries.FirstOrDefault(n => issue.LineNumber is int line && n.ContentRanges.Any(r => line >= r.StartLine && line <= r.EndLine));
            if (issue.Code == "chapter_duplicate" && node is not null)
            {
                var same = titleGroups[(parentIds[node.Id], node.Level, node.Title.Trim())].ToArray();
                var lines = same.Select(n => n.TitleLineNumber).ToHashSet();
                Add(issue, same, lines.SelectMany(line => rawByLine[line]).Where(r => r.Code == "chapter_duplicate"));
                continue;
            }
            var associated = node is null ? Array.Empty<ChapterTreeEntry>() : new[] { node };
            if (issue.Code == "volume_number_duplicate")
            {
                // The diagnostic already carries the first occurrence; include it for side-by-side navigation.
                var first = Regex.Match(issue.Message, @"首次在原文第\s*(\d+)\s*行", RegexOptions.None, TimeSpan.FromMilliseconds(200));
                if (first.Success && int.TryParse(first.Groups[1].Value, out var line)
                    && entryByLine[line].FirstOrDefault() is { } origin) associated = new[] { origin }.Concat(associated).ToArray();
            }
            Add(issue, associated, [issue]);
        }
        // Group repeated order warnings for review, without changing recognition or dropping
        // any original evidence. Scope remains the same parent and level.
        var orderGroups = groups.Where(g => g.Issue.Code == "chapter_number_order" && g.NodeIds.Count == 1)
            .GroupBy(g => (Parent: parentIds.GetValueOrDefault(g.NodeIds[0], ""), Level: byId[g.NodeIds[0]].Level))
            .Where(g => g.Count() > 1).Select(g => g.ToArray()).ToArray();
        var mergedOrderGroups = orderGroups.SelectMany(batch => batch).ToHashSet();
        groups.RemoveAll(mergedOrderGroups.Contains);
        foreach (var batch in orderGroups)
        {
            var first = batch.OrderBy(g => g.Issue.LineNumber).First();
            var issue = first.Issue with { Code = "chapter_numbering_variants",
                Message = $"同一层级有 {batch.Length} 处编号顺序差异，已合并展示。可能涉及编号重启、体系混用或顺序调整；不据此认定缺章，也不自动改号。请选择原文位置逐项核对。" };
            groups.Add(new(issue, CategoryOf(issue.Code), batch.SelectMany(g => g.NodeIds).Distinct().ToArray(),
                batch.SelectMany(g => g.Lines).Distinct().Order().ToArray(), batch.SelectMany(g => g.RelatedIssues).ToArray()));
        }
        // What the directory knows and the tree does not. Everything above reads the text on its own:
        // it can see that numbering jumps, but not which chapters are supposed to be there. Once a
        // directory has been applied the difference matters more, not less — the repair already left
        // chapters unplaced, and without this the panel a reader opens afterwards says "two gaps" while
        // forty chapters are missing. Reported as findings, never as actions.
        if (reference is not null)
        {
            var known = entries.Where(e => !e.IsFrontMatter && e.TitleLineNumber is not null)
                .Select(e => ReferenceOutline.ParseKey(e.Title).Canonical).ToHashSet(StringComparer.Ordinal);
            var absent = reference.Nodes.Where(node => node.Kind == ReferenceNodeKind.Chapter)
                .Where(node => !known.Contains(ReferenceOutline.ParseKey(node.Title).Canonical))
                .ToArray();
            if (absent.Length > 0)
            {
                var listed = string.Join("；", absent.Take(6).Select(node => node.Title));
                var rest = absent.Length > 6 ? $" 等共 {absent.Length} 章" : "";
                var issue = new ConversionPreflightIssue(document.SourcePath, PreflightSeverity.Warning,
                    "reference_chapter_absent",
                    $"参考目录里有 {absent.Length} 章在章节树中没有：{listed}{rest}。"
                    + "这些章要么原文里确实没有（软件不会补造正文），要么没被识别到；请对照原文核对，"
                    + "需要补建的入口在「目录辅助修复」。本项只提示，不会修改章节树。",
                    PreflightTargetKind.Chapters, 0);
                groups.Add(new(issue, CategoryOf(issue.Code), [], [], [issue]));
            }
        }
        var ordered = groups.OrderBy(g => g.Issue.LineNumber ?? int.MaxValue).ToArray();
        return new(ordered.Take(Math.Max(1, maximumGroups)).ToArray(), ordered.Length);
    }
}
