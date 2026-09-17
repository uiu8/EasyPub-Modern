using System.Globalization;

namespace EasyPub.Core;

/// <summary>
/// Where every line of the old version went in the new one.
///
/// <para>A repair replaces the source file, and a line number only means something relative to one version
/// of that file. Everything held in memory that points at a line — the chapter tree's heading lines, the
/// review confirmed on line 812, the breakpoint before chapter 40 — points at the <b>old</b> file the
/// moment the replacement lands. This map is the only bridge between the two numberings.</para>
///
/// <para>It is built by re-parsing the text the renderer produced and walking the same single pass the
/// renderer walks, and it is <b>verifiable</b>: <see cref="Verify"/> re-reads the new text and checks that a
/// kept line is textually identical, that a replaced line carries exactly the replacement text, and that an
/// inserted line has no original. A map that silently drifts by one line is worse than no map, because it
/// rebinds every later line to the wrong place and does so without failing.</para>
/// </summary>
public sealed record SourceCoordinateMap(
    int OriginalLineCount,
    int NewLineCount,
    IReadOnlyDictionary<int, int?> NewLineOfOriginal,
    IReadOnlyDictionary<int, int> OriginalLineOfNew,
    IReadOnlyDictionary<string, int> NewLineOfOperation,
    IReadOnlyDictionary<string, int> OriginalLineOfOperation,
    IReadOnlySet<int> DeletedLines,
    IReadOnlySet<int> ReplacedLines,
    IReadOnlySet<int> InsertedLines)
{
    /// <summary>
    /// The number of lines the placing pass actually placed. It has to equal
    /// <c>OriginalLineCount − deleted + inserted</c>, and it is deliberately a separate number from
    /// <see cref="NewLineCount"/>: the line count comes from the file, this comes from the arithmetic, and a
    /// pass that stopped one line short shows up as the two disagreeing rather than as nothing at all.
    /// </summary>
    public int PlacedLineCount { get; init; }
    /// <summary>Where original line <paramref name="line"/> ended up, or <c>null</c> when it was deleted.</summary>
    public int? TryMap(int line) => NewLineOfOriginal.TryGetValue(line, out var mapped) ? mapped : null;

    public bool Exists(int originalLine) => NewLineOfOriginal.TryGetValue(originalLine, out var mapped) && mapped is not null;

    /// <summary>Which original line a line of the new file came from; <c>null</c> for an inserted line.</summary>
    public int? SourceOf(int newLine) => OriginalLineOfNew.TryGetValue(newLine, out var original) ? original : null;

    /// <summary>The line an operation produced. A delete has none; a replace is itself one line.</summary>
    public int? LineOfOperation(string operationId) =>
        NewLineOfOperation.TryGetValue(operationId, out var line) ? line : null;

    public bool IsDeleted(int originalLine) => DeletedLines.Contains(originalLine);
    public bool IsReplaced(int originalLine) => ReplacedLines.Contains(originalLine);

    /// <summary>
    /// True when every surviving line moved by the same amount, and hands back that amount.
    ///
    /// <para>This is a diagnostic, not a check: it answers "did this change behave like a block shift", which
    /// is the expensive case to reason about by hand. A deletion in the middle makes it false even though the
    /// map is perfectly correct — the lines above the deletion did not move and the lines below did.</para>
    /// </summary>
    public bool TryGetUniformOffset(out int offset)
    {
        offset = 0;
        var first = true;
        foreach (var pair in NewLineOfOriginal.OrderBy(pair => pair.Key))
        {
            if (pair.Value is not { } line) continue;
            var delta = line - pair.Key;
            if (first)
            {
                offset = delta;
                first = false;
            }
            else if (delta != offset)
            {
                offset = 0;
                return false;
            }
        }
        return !first;
    }

    /// <summary>
    /// Rebinds a line-keyed identity (<c>"L812"</c>) to the new numbering. Returns <c>null</c> when that line
    /// is gone <b>or was replaced</b>: a replaced line still exists, but it is different text, so an identity
    /// defined by the old text does not survive. Rebinding it would silently fuse two different chapters.
    /// </summary>
    public string? Rebind(string stableKey)
    {
        if (stableKey.Length < 2 || stableKey[0] != 'L') return stableKey;
        if (!int.TryParse(stableKey.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var line))
            return stableKey;
        if (IsReplaced(line)) return null;
        return TryMap(line) is { } mapped ? "L" + mapped.ToString(CultureInfo.InvariantCulture) : null;
    }

    public static readonly SourceCoordinateMap Empty = new(0, 0,
        new Dictionary<int, int?>(), new Dictionary<int, int>(), new Dictionary<string, int>(),
        new Dictionary<string, int>(), new HashSet<int>(), new HashSet<int>(), new HashSet<int>());

    /// <summary>
    /// Builds the map by replaying, line for line, the single pass <see cref="SourcePatchRenderer"/> makes.
    ///
    /// <para>The new text is parsed back with the original's encoding, because that is what the transaction
    /// writes and what the next read will see. Building the map from the renderer's in-memory intent instead
    /// would describe a file nobody ever read.</para>
    /// </summary>
    public static SourceCoordinateMap Build(SourceTextDocument original, SourcePatch patch, string newText)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(patch);

        var patched = SourceTextDocument.Parse(newText, original.Encoding, original.Preamble);

        var deletes = new Dictionary<int, DeleteOriginalLine>();
        var replaces = new Dictionary<int, ReplaceOriginalLine>();
        var before = new Dictionary<int, List<InsertLineAtAnchor>>();
        var after = new Dictionary<int, List<InsertLineAtAnchor>>();
        var atEnd = new List<InsertLineAtAnchor>();

        foreach (var operation in patch.Operations)
            switch (operation)
            {
                case DeleteOriginalLine delete: deletes[delete.Line] = delete; break;
                case ReplaceOriginalLine replace: replaces[replace.Line] = replace; break;
                case InsertLineAtAnchor insert:
                    switch (insert.Anchor)
                    {
                        case BeforeOriginalLine line: Bucket(before, line.Line).Add(insert); break;
                        case AfterOriginalLine line: Bucket(after, line.Line).Add(insert); break;
                        default: atEnd.Add(insert); break;
                    }
                    break;
            }

        foreach (var bucket in before.Values) bucket.Sort(CompareInserts);
        foreach (var bucket in after.Values) bucket.Sort(CompareInserts);
        atEnd.Sort(CompareInserts);

        var forward = new Dictionary<int, int?>();
        var backward = new Dictionary<int, int>();
        var newLineOfOperation = new Dictionary<string, int>(StringComparer.Ordinal);
        var originalLineOfOperation = new Dictionary<string, int>(StringComparer.Ordinal);
        var cursor = 0;

        for (var line = 1; line <= original.Lines.Count; line++)
        {
            if (before.TryGetValue(line, out var leading))
                foreach (var insert in leading)
                {
                    cursor++;
                    newLineOfOperation[insert.OperationId] = cursor;
                }

            if (deletes.TryGetValue(line, out var delete))
            {
                forward[line] = null;
                originalLineOfOperation[delete.OperationId] = line;
                continue;
            }

            cursor++;
            forward[line] = cursor;
            backward[cursor] = line;

            if (replaces.TryGetValue(line, out var replace))
            {
                newLineOfOperation[replace.OperationId] = cursor;
                originalLineOfOperation[replace.OperationId] = line;
            }

            if (after.TryGetValue(line, out var trailing))
                foreach (var insert in trailing)
                {
                    cursor++;
                    newLineOfOperation[insert.OperationId] = cursor;
                }
        }

        foreach (var insert in atEnd)
        {
            cursor++;
            newLineOfOperation[insert.OperationId] = cursor;
        }

        // The renderer's count and the re-parsed count can legitimately differ by the end-of-file newline it
        // has to add before an end-of-file insert. That is a difference in how many lines the file *has*, not
        // in where any of them are, so the position count comes from the pass and the line count from the file.
        var inserted = Enumerable.Range(1, patched.Lines.Count).Where(line => !backward.ContainsKey(line)).ToHashSet();

        return new SourceCoordinateMap(
            original.Lines.Count,
            patched.Lines.Count,
            forward,
            backward,
            newLineOfOperation,
            originalLineOfOperation,
            deletes.Keys.ToHashSet(),
            replaces.Keys.ToHashSet(),
            inserted)
        {
            PlacedLineCount = cursor,
        };
    }

    /// <summary>
    /// The check that makes the map trustworthy: the new text is read back and every claim the map makes is
    /// compared against it. This is deliberately independent of the renderer — it re-parses the file the way
    /// the next reader will.
    /// </summary>
    public SourceCoordinateVerification Verify(SourceTextDocument original, SourcePatch patch, string newText)
    {
        var patched = SourceTextDocument.Parse(newText, original.Encoding, original.Preamble);

        // 0. The map has to be a map *of this patch*. Every count here is derived from the patch, so a map
        // handed over beside a different patch — or an empty one — would otherwise sail through checks 1 to 3,
        // each of which only compares the map against itself. This is the cheapest check and the one that
        // catches "the patch says 69 replacements, the map says 69, and the patch that actually ran was
        // empty".
        var declaredDeletes = patch.Operations.OfType<DeleteOriginalLine>().Select(operation => operation.Line)
            .ToHashSet();
        var declaredReplaces = patch.Operations.OfType<ReplaceOriginalLine>().Select(operation => operation.Line)
            .ToHashSet();
        var declaredInserts = patch.Operations.OfType<InsertLineAtAnchor>().Select(operation => operation.OperationId)
            .ToHashSet();
        if (!DeletedLines.SetEquals(declaredDeletes))
            return SourceCoordinateVerification.Failed(
                $"坐标表记着删除 {DeletedLines.Count} 行，补丁里却声明了 {declaredDeletes.Count} 行"
                + $"（{Describe(DeletedLines, declaredDeletes)}）。");
        if (!ReplacedLines.SetEquals(declaredReplaces))
            return SourceCoordinateVerification.Failed(
                $"坐标表记着替换 {ReplacedLines.Count} 行，补丁里却声明了 {declaredReplaces.Count} 行"
                + $"（{Describe(ReplacedLines, declaredReplaces)}）。");
        if (!declaredInserts.IsSubsetOf(NewLineOfOperation.Keys))
            return SourceCoordinateVerification.Failed(
                $"补丁声明了 {declaredInserts.Count} 个插入，坐标表只给出 "
                + $"{declaredInserts.Intersect(NewLineOfOperation.Keys).Count()} 个落点。");

        // The count of lines the pass places is the renderer's own arithmetic, and it has to agree with both
        // the patch and the file. This is the check a forgotten trailing line fails: a replace on the last
        // line still emits that line, so `newLineCount` must be `before − deleted + inserted`, and comparing it
        // against the re-parsed file catches a pass that stopped one line short.
        var expectedNewLines = original.Lines.Count - declaredDeletes.Count + declaredInserts.Count;
        if (PlacedLineCount != expectedNewLines)
            return SourceCoordinateVerification.Failed(
                $"补丁声明 原文 {original.Lines.Count} 行 − 删除 {declaredDeletes.Count} + 插入 {declaredInserts.Count} "
                + $"= {expectedNewLines} 行，行号对照却只排了 {PlacedLineCount} 行。");
        if (NewLineCount != expectedNewLines)
            return SourceCoordinateVerification.Failed(
                $"补丁声明应得 {expectedNewLines} 行，新文本却有 {NewLineCount} 行。");

        if (patched.Lines.Count != NewLineCount)
            return SourceCoordinateVerification.Failed(
                $"新版本实际有 {patched.Lines.Count} 行，坐标表却记了 {NewLineCount} 行。");

        // 1. Every original line is accounted for exactly once.
        if (NewLineOfOriginal.Count != OriginalLineCount)
            return SourceCoordinateVerification.Failed(
                $"原文有 {OriginalLineCount} 行，坐标表只覆盖了 {NewLineOfOriginal.Count} 行。");

        foreach (var (line, mapped) in NewLineOfOriginal)
        {
            if (mapped is not { } target)
            {
                if (!DeletedLines.Contains(line))
                    return SourceCoordinateVerification.Failed($"第 {line} 行没有落点，却不在删除清单里。");
                continue;
            }

            if (target < 1 || target > NewLineCount)
                return SourceCoordinateVerification.Failed($"第 {line} 行被映射到第 {target} 行，超出新文件范围。");

            var before = original.Lines[line - 1].Text;
            var after = patched.Lines[target - 1].Text;
            if (ReplacedLines.Contains(line))
            {
                if (string.Equals(before, after, StringComparison.Ordinal))
                    return SourceCoordinateVerification.Failed(
                        $"第 {line} 行被记为替换，但新旧文本完全相同（\"{Truncate(before)}\"），替换没有发生。");
            }
            else if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                return SourceCoordinateVerification.Failed(
                    $"第 {line} 行被记为原样保留，但内容变了：\"{Truncate(before)}\" → \"{Truncate(after)}\"。");
            }
        }

        // 2. Every line of the new file either came from an original line or is one the patch inserted.
        foreach (var newLine in Enumerable.Range(1, NewLineCount))
        {
            var source = SourceOf(newLine);
            if (source is null)
            {
                if (!InsertedLines.Contains(newLine))
                    return SourceCoordinateVerification.Failed($"第 {newLine} 行没有来源，却不在插入清单里。");
                continue;
            }

            if (source < 1 || source > OriginalLineCount)
                return SourceCoordinateVerification.Failed($"第 {newLine} 行声称来自第 {source} 行，超出原文范围。");

            if (NewLineOfOriginal.TryGetValue(source.Value, out var roundTrip) && roundTrip != newLine)
                return SourceCoordinateVerification.Failed($"第 {source} 行与第 {newLine} 行的映射不自洽。");
        }

        // 3. Every line of the new file that claims to come from an original line has to *be* that line,
        //    unless the map says an operation replaced it.
        //
        //    This is the check that catches "the map is a map of a different patch": hand over an empty patch
        //    beside a rewritten file and every count above still agrees — zero deletions, zero replacements,
        //    zero insertions — while 69 lines of the new file are not the lines they claim to be. Comparing
        //    the text is the only thing that notices.
        foreach (var (newLine, originalLine) in OriginalLineOfNew)
        {
            if (ReplacedLines.Contains(originalLine)) continue;
            if (string.Equals(patched.Lines[newLine - 1].Text, original.Lines[originalLine - 1].Text,
                    StringComparison.Ordinal)) continue;
            return SourceCoordinateVerification.Failed(
                $"第 {newLine} 行声称来自原文第 {originalLine} 行，内容却不同："
                + $"\"{Truncate(original.Lines[originalLine - 1].Text)}\" → \"{Truncate(patched.Lines[newLine - 1].Text)}\"，"
                + "而坐标表里没有任何操作改过它。");
        }

        // 4. Each operation landed where the patch says it should.
        foreach (var operation in patch.Operations)
        {
            if (!NewLineOfOperation.TryGetValue(operation.OperationId, out var line)) continue;
            var text = line >= 1 && line <= NewLineCount ? patched.Lines[line - 1].Text : null;
            if (text is null)
                return SourceCoordinateVerification.Failed($"操作 {operation.OperationId} 的落点第 {line} 行不存在。");
            var expected = operation switch
            {
                ReplaceOriginalLine replace => replace.NewText,
                InsertLineAtAnchor insert => insert.Text,
                _ => null,
            };
            if (expected is not null && !string.Equals(expected, text, StringComparison.Ordinal))
                return SourceCoordinateVerification.Failed(
                    $"操作 {operation.OperationId} 应产生 \"{Truncate(expected)}\"，第 {line} 行实际是 \"{Truncate(text)}\"。");
        }

        // 5. Every line the patch never mentioned has to be the line that was there before.
        //
        //    This walks the *original* lines and follows the map, rather than comparing the two files by
        //    position — the positions differ by exactly the deletions, so a positional comparison would report
        //    every line after the first deletion as "changed" and be useless. What it catches is an untracked
        //    edit: text that changed although no operation asked for it.
        for (var line = 1; line <= original.Lines.Count; line++)
        {
            if (DeletedLines.Contains(line) || ReplacedLines.Contains(line)) continue;
            if (TryMap(line) is not { } target) continue;
            if (!string.Equals(original.Lines[line - 1].Text, patched.Lines[target - 1].Text, StringComparison.Ordinal))
                return SourceCoordinateVerification.Failed(
                    $"第 {line} 行没有任何操作提到它，内容却变了："
                    + $"\"{Truncate(original.Lines[line - 1].Text)}\" → \"{Truncate(patched.Lines[target - 1].Text)}\"。");
        }

        return new SourceCoordinateVerification(true,
            $"坐标表已与新文本逐行核对：原文 {OriginalLineCount} 行 → 新文 {NewLineCount} 行，"
            + $"删除 {DeletedLines.Count}、替换 {ReplacedLines.Count}、插入 {InsertedLines.Count}。");
    }

    private static string Truncate(string value) => value.Length <= 40 ? value : value[..40] + "…";

    /// <summary>The lines the two sets disagree about, so the message names them instead of only counting.</summary>
    private static string Describe(IReadOnlySet<int> mapped, IReadOnlySet<int> declared)
    {
        var onlyMapped = mapped.Except(declared).Order().Take(3).ToArray();
        var onlyDeclared = declared.Except(mapped).Order().Take(3).ToArray();
        var parts = new List<string>();
        if (onlyMapped.Length > 0) parts.Add("只在坐标表里：" + string.Join("、", onlyMapped));
        if (onlyDeclared.Length > 0) parts.Add("只在补丁里：" + string.Join("、", onlyDeclared));
        return parts.Count == 0 ? "行号不同" : string.Join("；", parts);
    }

    private static List<InsertLineAtAnchor> Bucket(Dictionary<int, List<InsertLineAtAnchor>> map, int line)
    {
        if (!map.TryGetValue(line, out var list)) map[line] = list = [];
        return list;
    }

    private static int CompareInserts(InsertLineAtAnchor left, InsertLineAtAnchor right)
    {
        var byOrdinal = SourceAnchor.OrdinalOf(left.Anchor).CompareTo(SourceAnchor.OrdinalOf(right.Anchor));
        return byOrdinal != 0 ? byOrdinal : string.CompareOrdinal(left.OperationId, right.OperationId);
    }
}

public sealed record SourceCoordinateVerification(bool IsSound, string Message)
{
    public static SourceCoordinateVerification Failed(string message) => new(false, message);
}
