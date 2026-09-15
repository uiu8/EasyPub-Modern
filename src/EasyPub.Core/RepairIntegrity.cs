namespace EasyPub.Core;

/// <summary>Source-line accounting shared by previews, automatic repair and removal receipts.</summary>
public static class RepairIntegrity
{
    public static IReadOnlyList<string> DescribeChanges(IReadOnlyList<ChapterTreeEntry> before, IReadOnlyList<ChapterTreeEntry> after)
    {
        var changes = new List<string>();
        var old = before.Select((entry, index) => (entry, index)).ToDictionary(p => p.entry.Id);
        var remaining = after.Select(e => e.Id).ToHashSet();
        foreach (var entry in before.Where(e => !remaining.Contains(e.Id))) changes.Add($"移除章节项：{entry.Title}（原文行 {entry.TitleLineNumber}；正文是否移除见下方逐行清单）");
        for (var index = 0; index < after.Count; index++)
        {
            var entry = after[index];
            if (!old.TryGetValue(entry.Id, out var prior)) { changes.Add($"新增：{entry.Title}（原文行 {entry.TitleLineNumber}，层级 {entry.Level}）"); continue; }
            var detail = new List<string>();
            if (entry.Title != prior.entry.Title) detail.Add($"标题「{prior.entry.Title}」→「{entry.Title}」");
            if (entry.Level != prior.entry.Level) detail.Add($"层级 {prior.entry.Level}→{entry.Level}");
            if (index != prior.index) detail.Add($"位置 {prior.index + 1}→{index + 1}");
            if (!entry.ContentRanges.SequenceEqual(prior.entry.ContentRanges))
                detail.Add("正文范围 " + string.Join(",", prior.entry.ContentRanges.Select(r => $"{r.StartLine}-{r.EndLine}"))
                    + " → " + string.Join(",", entry.ContentRanges.Select(r => $"{r.StartLine}-{r.EndLine}")));
            if (detail.Count > 0) changes.Add(entry.Title + "：" + string.Join("；", detail));
        }
        var removed = Coverage(before); removed.ExceptWith(Coverage(after));
        changes.Add($"从成品移除原文 {removed.Count} 行：" + string.Join(",", Ranges(removed).Select(r => $"{r.StartLine}-{r.EndLine}")));
        return changes;
    }
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
