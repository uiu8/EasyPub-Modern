namespace EasyPub.Core;

/// <summary>Consecutive pairs in both source sequences, not just similar chapter numbers.</summary>
public static class ChapterRepeatedSequences
{
    public static IReadOnlyList<ChapterReviewGroup> Find(ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry> entries, IReadOnlyList<ChapterContentDuplicate>? matches = null)
    {
        var chapters = entries.Where(e => !e.IsFrontMatter && e.TitleLineNumber.HasValue).ToArray();
        var indexes = chapters.Select((e, i) => (e.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var pairs = (matches ?? ChapterContentDuplicates.Find(document, entries))
            .Where(p => indexes.ContainsKey(p.FirstId) && indexes.ContainsKey(p.SecondId))
            .Select(p => (A: indexes[p.FirstId], B: indexes[p.SecondId])).OrderBy(p => p.B).ToArray();
        var result = new List<ChapterReviewGroup>();
        for (var i = 0; i < pairs.Length; i++)
        {
            var start = i;
            while (i + 1 < pairs.Length && pairs[i + 1].A == pairs[i].A + 1 && pairs[i + 1].B == pairs[i].B + 1) i++;
            var count = i - start + 1;
            if (count < 3 || pairs[i].A >= pairs[start].B) continue;
            var first = chapters[pairs[start].A]; var last = chapters[pairs[i].A];
            var second = chapters[pairs[start].B]; var end = chapters[pairs[i].B];
            var members = pairs.Skip(start).Take(count).SelectMany(p => new[] { chapters[p.A], chapters[p.B] }).ToArray();
            var message = $"连续重复区段：至少 {count} 章以相同顺序再次出现，各对正文相似度 ≥90%。"
                + $"\n前段：原文第 {first.TitleLineNumber}—{last.TitleLineNumber} 行章头（{first.Title} → {last.Title}）。"
                + $"\n后段：原文第 {second.TitleLineNumber}—{end.TitleLineNumber} 行章头（{second.Title} → {end.Title}）。"
                + "\n疑似源文件重复拼接，不是正常分卷的证据。先定位两段前后章节、核对完整性，再逐对选择保留；不自动移动、删除或补写内容。";
            var issue = new ConversionPreflightIssue(document.SourcePath, PreflightSeverity.Warning, "chapter_repeated_sequence", message,
                PreflightTargetKind.Chapters, second.TitleLineNumber);
            result.Add(new(issue, ReviewCategories.Duplicate, members.Select(e => e.Id).ToArray(), members.Select(e => e.TitleLineNumber!.Value).Distinct().Order().ToArray(), [issue]));
        }
        return result;
    }
}
