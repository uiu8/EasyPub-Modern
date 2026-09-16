namespace EasyPub.Core;

/// <summary>What kind of change one entry of the report describes.</summary>
public enum RepairChangeKind
{
    /// <summary>A volume level was built or rebuilt above the chapters.</summary>
    RebuiltVolume,
    /// <summary>A chapter moved under a volume, or back out of one.</summary>
    ChangedLevel,
    /// <summary>A chapter the directory lists was located in the text and recorded.</summary>
    AddedChapter,
    /// <summary>An existing chapter took the directory's wording for its title.</summary>
    RetitledChapter,
    /// <summary>A heading was folded back into the body; every line of it is kept.</summary>
    FoldedIntoBody,
    /// <summary>One of two identical occurrences was dropped from the finished book.</summary>
    RemovedDuplicate,
    /// <summary>An entry that is in the tree before but not after, and is not any of the above.</summary>
    RemovedEntry,
    /// <summary>Text coverage moved without the entry itself changing.</summary>
    ReassignedBody,
    /// <summary>Where the entry sits in the tree moved.</summary>
    Reordered,
    /// <summary>Source lines left the finished book; the receipt lists them.</summary>
    RemovedLines,
}

/// <summary>
/// One change to the chapter tree, kept as data rather than a sentence so the confirmation window can
/// group it. <paramref name="Line"/> is the source line the change is anchored to, and <paramref name="Title"/>
/// is the entry it names.
/// </summary>
public sealed record RepairChange(RepairChangeKind Kind, int Line, string Title, string Detail = "");

/// <summary>Source-line accounting shared by previews, automatic repair and removal receipts.</summary>
public static class RepairIntegrity
{
    /// <summary>
    /// Reads the difference between two trees as structured changes.
    ///
    /// Arguments are <b>(after, before)</b> — the new tree first, matching "what changed, against what".
    /// Getting this the wrong way round reports an addition as a removal, which is exactly the
    /// confusion this method exists to remove, so the order is stated here on purpose.
    ///
    /// Entries are matched by their <c>Id</c> first — the tree keeps an entry's id when an action only
    /// retitles or moves it. Only when that fails is a structural identity used: a volume by its title,
    /// anything else by the source line it sits on. That second pass is what the old string-based
    /// version lacked. Rebuilding a volume level mints a fresh <c>Guid</c> for every volume, so matching
    /// by id alone reported one volume twice — once as removed, once as added — and on a real
    /// 385-chapter book that filled most of a 286-line list with six volumes counted twice, under the
    /// words "移除章节项", which reads as "your text was deleted".
    /// </summary>
    public static IReadOnlyList<RepairChange> Describe(
        IReadOnlyList<ChapterTreeEntry> after, IReadOnlyList<ChapterTreeEntry> before)
    {
        var changes = new List<RepairChange>();
        var old = new Dictionary<string, (ChapterTreeEntry Entry, int Index)>(StringComparer.Ordinal);
        for (var index = 0; index < before.Count; index++) old[before[index].Id] = (before[index], index);
        var remaining = new HashSet<string>(after.Select(entry => entry.Id), StringComparer.Ordinal);

        // Structural identity for the entries whose id did not survive, so a rebuilt volume is
        // recognised as the same volume instead of as a brand new one.
        var byVolumeTitle = new Dictionary<string, ChapterTreeEntry>(StringComparer.Ordinal);
        var byLine = new Dictionary<int, ChapterTreeEntry>();
        foreach (var entry in before)
        {
            if (IsVolumeLike(entry.Title)) byVolumeTitle.TryAdd(entry.Title, entry);
            if (entry.TitleLineNumber is int line && !byLine.ContainsKey(line)) byLine[line] = entry;
        }
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        // Front matter is rebuilt with a fresh id on every pass too (ReferencePlan.cs:207), and it is
        // not something the user chose to remove — its body is simply reassigned. Reporting it as a
        // removed entry would read as "your preface was deleted", so it is claimed up front.
        var frontBefore = before.FirstOrDefault(entry => entry.IsFrontMatter);
        var frontAfter = after.FirstOrDefault(entry => entry.IsFrontMatter);
        if (frontBefore is not null && frontAfter is not null)
        {
            consumed.Add(frontBefore.Id);
            remaining.Remove(frontAfter.Id);
        }

        for (var index = 0; index < after.Count; index++)
        {
            var entry = after[index];
            if (entry.IsFrontMatter) continue;
            if (old.TryGetValue(entry.Id, out var prior))
            {
                consumed.Add(prior.Entry.Id);
                var line = entry.TitleLineNumber ?? prior.Entry.TitleLineNumber ?? 0;
                if (entry.Title != prior.Entry.Title)
                    changes.Add(new(RepairChangeKind.RetitledChapter, line, entry.Title, $"原标题「{prior.Entry.Title}」"));
                else if (entry.Level != prior.Entry.Level)
                    changes.Add(new(RepairChangeKind.ChangedLevel, line, entry.Title, $"层级 {prior.Entry.Level}→{entry.Level}"));
                else
                {
                    if (prior.Index != index)
                        changes.Add(new(RepairChangeKind.Reordered, line, entry.Title, $"位置 {prior.Index + 1}→{index + 1}"));
                    if (!entry.ContentRanges.SequenceEqual(prior.Entry.ContentRanges))
                        changes.Add(new(RepairChangeKind.ReassignedBody, line, entry.Title,
                            "正文范围 " + string.Join(",", prior.Entry.ContentRanges.Select(range => $"{range.StartLine}-{range.EndLine}"))
                            + " → " + string.Join(",", entry.ContentRanges.Select(range => $"{range.StartLine}-{range.EndLine}"))));
                }
                continue;
            }
            // The id is gone. A volume keeps its identity by title; anything else by the source line
            // it sits on, which is what a rebuild preserves even when it renames the entry.
            if (IsVolumeLike(entry.Title) && byVolumeTitle.TryGetValue(entry.Title, out var rebuilt))
            {
                consumed.Add(rebuilt.Id);
                // A volume whose level moved is a re-level, not a new volume: keeping the two apart is
                // what lets the summary say "建立卷层级 2 个" instead of counting every chapter that
                // merely moved under one.
                changes.Add(rebuilt.Level == entry.Level
                    ? new(RepairChangeKind.RebuiltVolume, rebuilt.TitleLineNumber ?? 0, entry.Title, "建立卷层级")
                    : new(RepairChangeKind.ChangedLevel, rebuilt.TitleLineNumber ?? 0, entry.Title, $"层级 {rebuilt.Level}→{entry.Level}"));
                continue;
            }
            if (entry.TitleLineNumber is int sourceLine && byLine.TryGetValue(sourceLine, out var sameLine)
                && consumed.Add(sameLine.Id))
            {
                changes.Add(sameLine.Title == entry.Title
                    ? new(RepairChangeKind.ReassignedBody, sourceLine, entry.Title, "同一处标题，章节项已重建")
                    : new(RepairChangeKind.RetitledChapter, sourceLine, entry.Title, $"原标题「{sameLine.Title}」"));
                continue;
            }
            changes.Add(IsVolumeLike(entry.Title)
                ? new(RepairChangeKind.RebuiltVolume, entry.TitleLineNumber ?? 0, entry.Title, "建立卷层级")
                : new(RepairChangeKind.AddedChapter, entry.TitleLineNumber ?? 0, entry.Title, "收录为章节"));
        }

        foreach (var entry in before.Where(entry => !entry.IsFrontMatter
                     && !remaining.Contains(entry.Id) && !consumed.Contains(entry.Id)))
            changes.Add(new(RepairChangeKind.RemovedEntry, entry.TitleLineNumber ?? 0, entry.Title, "从章节树移除"));

        var removed = Coverage(before); removed.ExceptWith(Coverage(after));
        if (removed.Count > 0)
            changes.Add(new(RepairChangeKind.RemovedLines, 0, $"{removed.Count} 行",
                string.Join(",", Ranges(removed).Select(range => $"{range.StartLine}-{range.EndLine}"))));
        return changes;
    }

    /// <summary>
    /// A heading that names a volume or a part rather than a chapter. Volume entries are the ones a
    /// rebuild recreates with new ids, so they need a title-based identity.
    /// </summary>
    private static bool IsVolumeLike(string title) =>
        title.Contains('卷') || title.Contains('部') || title.Contains('篇') || title.Contains('册');

    /// <summary>Legacy sentence-per-change view, kept for the callers that still print a plain list.</summary>
    public static IReadOnlyList<string> DescribeChanges(
        IReadOnlyList<ChapterTreeEntry> before, IReadOnlyList<ChapterTreeEntry> after) =>
        Describe(after, before).Select(change => change.Kind switch
        {
            RepairChangeKind.RebuiltVolume when change.Detail.StartsWith("层级", StringComparison.Ordinal) =>
                $"{change.Title}：{change.Detail}",
            RepairChangeKind.RebuiltVolume => $"建立卷层级：{change.Title}（原文行 {change.Line}；正文未改动）",
            RepairChangeKind.ChangedLevel => $"{change.Title}：{change.Detail}（原文行 {change.Line}）",
            RepairChangeKind.AddedChapter => $"新增：{change.Title}（原文行 {change.Line}）",
            RepairChangeKind.RetitledChapter => $"{change.Title}：{change.Detail}（原文行 {change.Line}）",
            RepairChangeKind.FoldedIntoBody => $"{change.Title}：并回正文（原文行 {change.Line}；文字全部保留）",
            RepairChangeKind.RemovedDuplicate => $"移除重复正文：{change.Title}（原文行 {change.Line}；逐行清单见下）",
            RepairChangeKind.RemovedEntry => $"移除章节项：{change.Title}（原文行 {change.Line}；正文是否移除见下方逐行清单）",
            RepairChangeKind.ReassignedBody => $"{change.Title}：{change.Detail}",
            RepairChangeKind.Reordered => $"{change.Title}：{change.Detail}",
            RepairChangeKind.RemovedLines => $"从成品移除原文 {change.Title}：{change.Detail}",
            _ => change.Title,
        }).ToArray();

    public static HashSet<int> Coverage(IEnumerable<ChapterTreeEntry> entries)
    {
        var lines = new HashSet<int>();
        foreach (var entry in entries)
        {
            if (entry.TitleLineNumber is int heading) lines.Add(heading);
            foreach (var range in entry.ContentRanges)
                for (var line = range.StartLine; line <= range.EndLine; line++) lines.Add(line);
        }
        return lines;
    }

    public static IReadOnlyList<ChapterSourceRange> Ranges(IEnumerable<int> lines)
    {
        var result = new List<ChapterSourceRange>();
        foreach (var line in lines.Distinct().Order())
        {
            if (result.Count > 0 && result[^1].EndLine + 1 == line)
                result[^1] = result[^1] with { EndLine = line };
            else result.Add(new(line, line));
        }
        return result;
    }

    public static void Verify(IEnumerable<ChapterTreeEntry> before, IEnumerable<ChapterTreeEntry> after, IEnumerable<int> removed)
    {
        var expected = Coverage(before);
        var dropped = removed.ToHashSet();
        if (!dropped.IsSubsetOf(expected)) throw new InvalidDataException("移除清单包含当前书稿之外的行。");
        expected.ExceptWith(dropped);
        if (!expected.SetEquals(Coverage(after)))
            throw new InvalidDataException("修复前后原文行不守恒，已停止应用。请保留当前章节树并核对目录。");
    }

    public static async Task<string?> SaveRemovedAsync(ChapterTreeDocument document, IReadOnlyList<int> lines, CancellationToken token = default)
    {
        if (lines.Count == 0) return null;
        if (lines.Any(line => line < 1 || line > document.LineCount))
            throw new InvalidDataException("移除清单包含无效原文行号。");
        SourceBackupStore.CreateDefault().EnsureSnapshot(document);
        var folder = Path.Combine(SourceBackupStore.CreateDefault().DirectoryPath, "Removed");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileNameWithoutExtension(document.SourcePath) + "." +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "." + Guid.NewGuid().ToString("N")[..8] + ".removed.txt");
        var text = "源文件：" + document.SourcePath + "\nSHA-256：" + document.SourceSha256 + "\n原文行号\t原文内容\n" +
            string.Join(Environment.NewLine, lines.Distinct().Order().Select(line => $"{line}\t{document.SourceLine(line)?.Text}"));
        await File.WriteAllTextAsync(path, text, token).ConfigureAwait(false);
        return path;
    }
}
