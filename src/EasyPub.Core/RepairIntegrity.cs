namespace EasyPub.Core;

/// <summary>Source-line accounting shared by previews, automatic repair and removal receipts.</summary>
public static class RepairIntegrity
{
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
        var folder = Path.Combine(SourceBackupStore.CreateDefault().DirectoryPath, "Removed");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Path.GetFileNameWithoutExtension(document.SourcePath) + "." +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "." + Guid.NewGuid().ToString("N")[..8] + ".removed.txt");
        var text = "源文件：" + document.SourcePath + "\nSHA-256：" + document.SourceSha256 + "\n原文行号\t原文内容\n" +
            string.Join(Environment.NewLine, lines.Distinct().Order().Select(line => $"{line}\t{document.SourceLine(line)?.Text}"));
        await File.WriteAllTextAsync(path, text, token);
        return path;
    }
}
