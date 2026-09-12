using System.Text.RegularExpressions;
namespace EasyPub.Core;

public sealed record DuplicateTitleChange(string RemovedTitle, int? RemovedLine, string KeptTitle);

public static class DuplicateChapterCleaner
{
    // Only adjacent leaf siblings with exactly equal titles are safe candidates.
    public static IReadOnlyList<ChapterTreeEntry> Clean(IReadOnlyList<ChapterTreeEntry> entries, int minimumLines = 0, int maximumLines = 0,
        bool ignoreChapterNumber = false, bool preferFirstTitle = false, IList<DuplicateTitleChange>? changes = null, IReadOnlySet<string>? scope = null)
    {
        if (minimumLines < 0 || maximumLines < minimumLines) throw new ArgumentOutOfRangeException(nameof(maximumLines));
        var result = new List<ChapterTreeEntry>();
        string Key(ChapterTreeEntry entry)
        {
            var title = entry.Title.Trim();
            if (!ignoreChapterNumber) return title;
            var match = Regex.Match(title, @"^第\s*[0-9０-９零〇一二两三四五六七八九十百千万]+\s*([章回])\s*(.+)$", RegexOptions.None, TimeSpan.FromSeconds(1));
            return match.Success && !string.IsNullOrWhiteSpace(match.Groups[2].Value)
                ? match.Groups[1].Value + "|" + match.Groups[2].Value.Trim() : title;
        }
        long Lines(ChapterTreeEntry e) => e.ContentRanges.Sum(r => (long)r.EndLine - r.StartLine + 1);
        bool Leaf(int index) => (scope is null || scope.Contains(entries[index].Id)) && !entries[index].IsFrontMatter && (index + 1 == entries.Count || entries[index + 1].Level <= entries[index].Level);
        for (var i = 0; i < entries.Count;)
        {
            var end = i + 1;
            if (Leaf(i))
                while (end < entries.Count && Leaf(end) && entries[end].Level == entries[i].Level
                    && Key(entries[end]) == Key(entries[i])) end++;
            if (end == i + 1) { result.Add(entries[i++]); continue; }
            var group = entries.Skip(i).Take(end - i).ToArray();
            var anchor = group.OrderByDescending(Lines).First();
            var remove = group.Where(e => e.Id != anchor.Id && Lines(e) >= minimumLines && Lines(e) <= maximumLines).Select(e => e.Id).ToHashSet();
            // Merge removed bodies into their nearest surviving neighbor, in source order.
            var pending = new List<ChapterSourceRange>();
            var pendingEntries = new List<ChapterTreeEntry>();
            var start = result.Count;
            foreach (var entry in group)
            {
                if (remove.Contains(entry.Id)) { pending.AddRange(entry.ContentRanges); pendingEntries.Add(entry); continue; }
                var keptTitle = preferFirstTitle && pendingEntries.Count > 0 ? pendingEntries[0].Title : entry.Title;
                result.Add(entry with { Title = keptTitle, ContentRanges = pending.Concat(entry.ContentRanges).ToArray() });
                foreach (var removed in pendingEntries) changes?.Add(new(removed.Title, removed.TitleLineNumber, keptTitle));
                pendingEntries.Clear();
                pending.Clear();
            }
            if (pending.Count > 0 && result.Count > start)
                result[^1] = result[^1] with { ContentRanges = result[^1].ContentRanges.Concat(pending).ToArray() };
            foreach (var removed in pendingEntries) changes?.Add(new(removed.Title, removed.TitleLineNumber, result[^1].Title));
            i = end;
        }
        return result;
    }
}
