namespace EasyPub.Core;

/// <summary>A reviewed tree move. Source text and source coordinates remain unchanged.</summary>
public sealed record ChapterTreeRelocationPreview(
    IReadOnlyList<ChapterTreeEntry> Entries,
    IReadOnlyList<string> MovedEntryIds,
    string BeforeEntryId,
    string TargetTitle);

/// <summary>Moves an explicitly selected contiguous block before an explicit destination chapter.</summary>
public static class ChapterTreeRelocation
{
    public static ChapterTreeRelocationPreview Before(ChapterTreeDocument document,
        IReadOnlyCollection<string> selectedIds, string targetId)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(selectedIds);
        var selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        if (selected.Count == 0) throw new InvalidOperationException("请先选择要移动的章节。");
        var entries = document.Entries;
        var positions = entries.Select((entry, index) => (entry, index)).ToDictionary(p => p.entry.Id, p => p.index);
        if (selected.Any(id => !positions.ContainsKey(id)) || !positions.TryGetValue(targetId, out var targetIndex))
            throw new InvalidOperationException("章节或目标已变化，请重新选择。");
        if (selected.Contains(targetId)) throw new InvalidOperationException("目标不能在待移动章节之内。");
        var indexes = selected.Select(id => positions[id]).Order().ToArray();
        var first = indexes[0];
        var last = indexes[^1];
        if (last - first + 1 != indexes.Length)
            throw new InvalidOperationException("请选中一个连续章节区段，分散的章节请分次移动。");
        var level = entries[first].Level;
        var target = entries[targetIndex];
        // Leaves only: carrying a volume's descendants is a different operation and must be explicit.
        if (target.IsFrontMatter || IsParent(targetIndex) || indexes.Any(i => entries[i].IsFrontMatter
                || entries[i].Level != level || IsParent(i)))
            throw new InvalidOperationException("本次只能移动同层级的连续正文章节，目标也必须是正文章节。");
        var moved = indexes.Select(i => entries[i] with { Level = target.Level, RecognitionSource = "manual" }).ToArray();
        var result = entries.Where(e => !selected.Contains(e.Id)).ToList();
        result.InsertRange(result.FindIndex(e => e.Id == targetId), moved);
        ChapterTreeDocument.ValidatePlan(document.CreatePlan(result), document.LineCount);
        RepairIntegrity.Verify(entries, result, []);
        return new(result, moved.Select(e => e.Id).ToArray(), targetId, target.Title);

        bool IsParent(int i) => i + 1 < entries.Count && !entries[i + 1].IsFrontMatter && entries[i + 1].Level > entries[i].Level;
    }
}
