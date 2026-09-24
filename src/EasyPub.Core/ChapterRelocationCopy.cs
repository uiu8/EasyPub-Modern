using System.Text.Json;

namespace EasyPub.Core;

public sealed record RelocatedSourceLine(int OriginalLine, int NewLine, string Text);

/// <summary>One explicit physical block move, exported to a new TXT and its mapped project.</summary>
public sealed class ChapterRelocationCopy
{
    private readonly PreparedTextCopy _export;
    public IReadOnlyList<RelocatedSourceLine> Lines { get; }
    public int StartLine { get; }
    public int EndLine { get; }
    public int BeforeLine { get; }
    internal Action<RepairJournalState>? AfterStep { get; set; }

    private ChapterRelocationCopy(PreparedTextCopy export, IReadOnlyList<RelocatedSourceLine> lines, int start, int end, int before)
        => (_export, Lines, StartLine, EndLine, BeforeLine) = (export, lines, start, end, before);

    public static ChapterRelocationCopy Prepare(ChapterTreeDocument document, IReadOnlyCollection<string> selectedIds,
        string targetId, TextEncodingMode encoding = TextEncodingMode.Auto)
    {
        var preview = ChapterTreeRelocation.Before(document, selectedIds, targetId);
        var bytes = File.ReadAllBytes(document.SourcePath);
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != document.SourceSha256)
            throw new InvalidDataException("原稿已变化，请重新预览移动。");
        var source = SourceTextDocument.Decode(bytes, encoding);
        var selected = selectedIds.ToHashSet(StringComparer.Ordinal);
        var moved = document.Entries.Where(e => selected.Contains(e.Id)).ToArray();
        if (moved.Any(e => e.TitleLineNumber is null))
            throw new InvalidDataException("选区含没有原文标题行的章节，当前只能调整成品顺序。");
        var target = document.Entries.Single(e => e.Id == targetId);
        if (target.TitleLineNumber is not int before)
            throw new InvalidDataException("目标没有原文标题行，当前只能调整成品顺序。");
        var owned = RepairIntegrity.Coverage(moved).Where(n => n <= source.Lines.Count).ToHashSet();
        foreach (var heading in moved.Select(e => e.TitleLineNumber).OfType<int>()) owned.Add(heading);
        var start = moved[0].TitleLineNumber!.Value;
        var end = owned.Max();
        if (owned.Count != end - start + 1 || owned.Min() != start
            || !moved.Select(e => e.TitleLineNumber!.Value).SequenceEqual(moved.Select(e => e.TitleLineNumber!.Value).Order()))
            throw new InvalidDataException("选区不是原文中的完整连续区段，请分次移动或仅调整成品顺序。");
        if (before >= start && before <= end + 1)
            throw new InvalidDataException("目标在原文选区内或紧邻其后，无需移动。");
        if (RepairIntegrity.Coverage(document.Entries.Where(e => !selected.Contains(e.Id))).Any(owned.Contains))
            throw new InvalidDataException("选区与其他章节共用原文行，不能直接移动文本。");
        var order = new List<int>(source.Lines.Count);
        for (var n = 1; n <= source.Lines.Count; n++)
        {
            if (n == before) order.AddRange(Enumerable.Range(start, end - start + 1));
            if (!owned.Contains(n)) order.Add(n);
        }
        if (order.Count != source.Lines.Count || order.Distinct().Count() != source.Lines.Count)
            throw new InvalidDataException("移动行清单不完整，未生成副本。");
        var mapping = order.Select((old, index) => new RelocatedSourceLine(old, index + 1, source.Lines[old - 1].Text)).ToArray();
        var map = mapping.ToDictionary(l => l.OriginalLine, l => l.NewLine);
        // Keep terminators at their physical slots, including the original EOF newline policy.
        // Only line text is permuted; encoding, BOM and newline sequence remain unchanged.
        const string action = "move-chapter-block";
        var operations = mapping.Where(l => source.Lines[l.NewLine - 1].Text != l.Text)
            .Select(l => (SourceOperation)new ReplaceOriginalLine(l.NewLine, source.Lines[l.NewLine - 1].Text, l.Text)
                { OperationId = $"move-line-{l.NewLine}", ActionId = action }).ToArray();
        var patch = new SourcePatch(action, document.SourceSha256, operations);
        var rendered = SourcePatchRenderer.Render(source, patch).Text;
        var recognized = ChapterTreeDocument.Load(document.SourcePath, SourceFileHasher.BytesOf(source, rendered),
            document.ChapterPattern, document.RecognitionOptions, encoding);
        int Map(int n) => n == source.Lines.Count + 1 && document.SourceLine(n)?.Text == ""
            ? recognized.LineCount : map[n];
        IReadOnlyList<ChapterSourceRange> Ranges(IReadOnlyList<ChapterSourceRange> ranges)
        {
            var result = new List<ChapterSourceRange>();
            foreach (var range in ranges)
                for (var n = range.StartLine; n <= range.EndLine; n++)
                {
                    var mapped = Map(n);
                    if (result.Count > 0 && result[^1].EndLine + 1 == mapped)
                        result[^1] = result[^1] with { EndLine = mapped };
                    else result.Add(new(mapped, mapped));
                }
            return result;
        }
        var entries = preview.Entries.Select(e => e with
        {
            TitleLineNumber = e.TitleLineNumber is int n ? Map(n) : null,
            ContentRanges = Ranges(e.ContentRanges),
        }).ToArray();
        var expected = CanonicalChapterModelFactory.Build(preview.Entries, n => document.SourceLine(n)?.Text);
        var actual = CanonicalChapterModelFactory.Build(entries, n => recognized.SourceLine(n)?.Text);
        if (!expected.EqualsByContent(actual)) throw new InvalidDataException("移动后正文归属变化，已停止。");
        var plan = document.CreatePlan(preview.Entries) with { SourceSha256 = recognized.SourceSha256, Entries = entries };
        ChapterTreeDocument.ValidatePlan(plan, recognized.LineCount);
        var export = new PreparedTextCopy(document, source, rendered, patch, plan, new TreePatch(action, [], expected), "move")
        {
            PrepareArtifacts = (copy, token) => File.WriteAllTextAsync(copy + ".movement.json", JsonSerializer.Serialize(new
            {
                originalPath = document.SourcePath, originalSha256 = document.SourceSha256, newSha256 = plan.SourceSha256,
                start, end, before, movedEntryIds = preview.MovedEntryIds, targetId,
                newlinePolicy = "Preserve physical slot terminators and original EOF; permute line text only", lines = mapping,
            }, new JsonSerializerOptions { WriteIndented = true }), token),
        };
        return new(export, mapping, start, end, before);
    }

    public Task<ManualSplitCopyResult> ExportAsync(SourceBackupStore store, CancellationToken token = default)
    {
        _export.AfterStep = AfterStep;
        return _export.ExportAsync(store, token);
    }
}
