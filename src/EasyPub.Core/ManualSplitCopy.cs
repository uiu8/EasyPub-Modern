namespace EasyPub.Core;

public sealed record ManualSplitCopyResult(bool Succeeded, string CopyPath, string ProjectPath,
    SourceEditResult Transaction);

/// <summary>A confirmed manual split exported to an isolated TXT and its saved reading order.</summary>
public sealed class ManualSplitCopy
{
    internal Action<RepairJournalState>? AfterStep { get; set; }
    private readonly ChapterTreeDocument _document;
    private readonly SourceTextDocument _source;
    private readonly string _rendered;
    private readonly SourcePatch _patch;
    private readonly ChapterTreePlan _plan;
    private readonly TreePatch _treePatch;

    private ManualSplitCopy(ChapterTreeDocument document, SourceTextDocument source, string rendered,
        SourcePatch patch, ChapterTreePlan plan, TreePatch treePatch)
        => (_document, _source, _rendered, _patch, _plan, _treePatch) = (document, source, rendered, patch, plan, treePatch);

    public static ManualSplitCopy Prepare(ChapterTreeDocument document, string entryId, int line, string title,
        TextEncodingMode encoding = TextEncodingMode.Auto)
    {
        title = title.Trim();
        if (title.Length == 0 || title.Any(char.IsControl)) throw new InvalidDataException("请填写单行新章标题。");
        var entries = document.Entries.ToList();
        var index = entries.FindIndex(e => e.Id == entryId);
        if (index < 0) throw new InvalidDataException("选中的章节已不存在。");
        var current = entries[index];
        var split = ChapterContentSplit.At(current.ContentRanges, line)
            ?? throw new InvalidDataException("请选择章节正文中的切分位置。");
        // A new sibling belongs after the subtree. Splitting container nodes needs a separate child policy.
        if (index + 1 < entries.Count && entries[index + 1].Level > current.Level)
            throw new InvalidDataException("请先选择不含子章节的正文节点，再另存切分副本。");
        var added = current with { Id = Guid.NewGuid().ToString("N"), Title = title,
            TitleLineNumber = null, ContentRanges = split.After, RecognitionSource = "manual", IncludeInToc = true };
        entries[index] = current with { ContentRanges = split.Before };
        entries.Insert(index + 1, added);
        RepairIntegrity.Verify(document.Entries, entries, []);
        var bytes = File.ReadAllBytes(document.SourcePath);
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) != document.SourceSha256)
            throw new InvalidDataException("原稿已变化，请重新打开后再预览。");
        var source = SourceTextDocument.Decode(bytes, encoding);
        const string operationId = "manual-split-heading";
        var patch = new SourcePatch(operationId, document.SourceSha256,
            [new InsertLineAtAnchor(new BeforeOriginalLine(line), title) { OperationId = operationId, ActionId = operationId }]);
        var rendered = SourcePatchRenderer.Render(source, patch);
        var map = SourceCoordinateMap.Build(source, patch, rendered.Text);
        var newBytes = SourceFileHasher.BytesOf(source, rendered.Text);
        var recognized = ChapterTreeDocument.Load(document.SourcePath, newBytes, document.ChapterPattern,
            document.RecognitionOptions, encoding);
        var headingLine = map.LineOfOperation(operationId)!.Value;
        if (!recognized.Entries.Any(e => e.TitleLineNumber == headingLine))
            throw new InvalidDataException("当前识别规则无法将新标题识别为独立章节。请修改标题或识别设置；也可只改成品章节树。");
        // The editor retains an empty tail after a final newline; the lossless writer
        // models that newline as a terminator, not an additional physical line.
        int Map(int n) => n == map.OriginalLineCount + 1 && document.LineCount == n
            && document.SourceLine(n)?.Text == "" && recognized.LineCount == map.NewLineCount + 1
                ? recognized.LineCount
                : map.TryMap(n) ?? throw new InvalidDataException("正文行映射失败。");
        IReadOnlyList<ChapterSourceRange> Ranges(IReadOnlyList<ChapterSourceRange> ranges)
        {
            var result = new List<ChapterSourceRange>();
            foreach (var range in ranges)
            {
                // Never include the inserted title in a range crossing its physical position.
                if (range.StartLine < line && range.EndLine >= line)
                {
                    result.Add(new(Map(range.StartLine), Map(line - 1)));
                    result.Add(new(Map(line), Map(range.EndLine)));
                }
                else result.Add(new(Map(range.StartLine), Map(range.EndLine)));
            }
            return result;
        }
        var mapped = entries.Select(e => e with
        {
            TitleLineNumber = e.Id == added.Id ? headingLine : e.TitleLineNumber is int n ? Map(n) : null,
            ContentRanges = Ranges(e.ContentRanges),
        }).ToArray();
        var expected = CanonicalChapterModelFactory.Build(entries, n => document.SourceLine(n)?.Text);
        var actual = CanonicalChapterModelFactory.Build(mapped, n => recognized.SourceLine(n)?.Text);
        if (!expected.EqualsByContent(actual)) throw new InvalidDataException("插入标题后章节正文发生变化，已停止。");
        var plan = document.CreatePlan(entries) with { SourceSha256 = recognized.SourceSha256, Entries = mapped };
        ChapterTreeDocument.ValidatePlan(plan, recognized.LineCount);
        var treePatch = new TreePatch(operationId,
        [
            new ReassignEntryBody(current.StableKey, BodyAssignment.FromRanges(split.Before))
                { ActionId = operationId, Binding = current.TitleLineNumber is int oldLine ? new OriginalLineBinding(oldLine) : new NoSourceBinding() },
            new InsertEntry(added.StableKey, title, added.Level, true, BodyAssignment.FromRanges(split.After))
                { ActionId = operationId, Binding = new InsertedLineBinding(operationId) },
        ], expected);
        return new(document, source, rendered.Text, patch, plan, treePatch);
    }

    public Task<ManualSplitCopyResult> ExportAsync(SourceBackupStore store, CancellationToken token = default) =>
        new PreparedTextCopy(_document, _source, _rendered, _patch, _plan, _treePatch, "split")
        { AfterStep = AfterStep }.ExportAsync(store, token);
}
