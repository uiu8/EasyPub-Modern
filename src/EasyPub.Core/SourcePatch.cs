namespace EasyPub.Core;

/// <summary>
/// One physical change to the TXT.
///
/// <para>Every operation carries the text it expects to find, so a line that has moved or changed since
/// the plan was built is a refusal rather than an overwrite. Every operation also names the action that
/// demanded it, which is what lets a receipt say why a line was deleted and lets a downgrade be traced
/// back to the action it belonged to.</para>
/// </summary>
public abstract record SourceOperation
{
    public required string OperationId { get; init; }
    public required string ActionId { get; init; }
}

public sealed record DeleteOriginalLine(int Line, string ExpectedOldText) : SourceOperation;

public sealed record ReplaceOriginalLine(int Line, string ExpectedOldText, string NewText) : SourceOperation;

public sealed record InsertLineAtAnchor(SourceAnchor Anchor, string Text) : SourceOperation;

/// <summary>
/// Everything one repair wants to do to the text, bound to the exact bytes it was computed against.
///
/// <para>Operations are keyed by <b>original</b> line numbers throughout — never by "the line after the
/// previous change". Rendering is a single pass, so the order operations appear in cannot affect the
/// result; the order is kept readable rather than load-bearing.</para>
/// </summary>
public sealed record SourcePatch(
    string ActionSetId,
    string BaseSourceSha256,
    IReadOnlyList<SourceOperation> Operations)
{
    public bool IsEmpty => Operations.Count == 0;

    public static SourcePatch Empty(string baseSourceSha256) => new("", baseSourceSha256, []);
}

/// <summary>Why a patch cannot be applied. Each case names the rule it broke.</summary>
public sealed record SourcePatchProblem(string Rule, string Detail);

/// <summary>The outcome of normalising a patch: either a usable one, or the reasons it is not.</summary>
public sealed record SourcePatchValidation(SourcePatch? Patch, IReadOnlyList<SourcePatchProblem> Problems)
{
    public bool IsValid => Problems.Count == 0 && Patch is not null;

    public static SourcePatchValidation Rejected(IEnumerable<SourcePatchProblem> problems) =>
        new(null, problems.ToArray());

    public string Describe() => Problems.Count == 0
        ? "补丁有效。"
        : string.Join("；", Problems.Select(problem => $"{problem.Rule}：{problem.Detail}"));
}

/// <summary>
/// Turns a bag of requested operations into one patch that is known to be executable, or says why not.
///
/// <para>All eight rules are checked here, before anything is written, and a violation is a refusal
/// rather than something the executor works around. Ordering was the old way of resolving conflicts —
/// "delete first, then insert" — and it hid the mistakes instead of reporting them: two actions that
/// disagreed about the same line produced a file that matched neither of them, and nothing said so.</para>
/// </summary>
public static class SourcePatchNormalizer
{
    /// <summary>How many lines the whole file has, so out-of-range lines are caught here too.</summary>
    public static SourcePatchValidation Normalize(
        string actionSetId,
        string baseSourceSha256,
        IEnumerable<SourceOperation> operations,
        int lineCount,
        Func<int, string?> lineText)
    {
        var problems = new List<SourcePatchProblem>();
        var all = operations.ToArray();

        // Rule 5 & 6: every line exists, and every precondition matches the text as it is now.
        // 判断"这个操作针对哪一行"必须按**类型**来，不能用 0 当哨兵 —— 0 是非法行号，
        // 用它表示"不是行操作"会让 Delete(0) 这种越界请求被静默跳过。
        foreach (var operation in all)
        {
            string? expected;
            int line;
            switch (operation)
            {
                case DeleteOriginalLine delete:
                    line = delete.Line;
                    expected = delete.ExpectedOldText;
                    break;
                case ReplaceOriginalLine replace:
                    line = replace.Line;
                    expected = replace.ExpectedOldText;
                    break;
                default:
                    continue;
            }
            if (line < 1 || line > lineCount)
            {
                problems.Add(new SourcePatchProblem("行号越界", $"第 {line} 行不在 1–{lineCount} 之内"));
                continue;
            }
            var actual = lineText(line) ?? "";
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
                problems.Add(new SourcePatchProblem("前置条件不符",
                    $"第 {line} 行不是预期的内容（预期「{Trim(expected)}」，实际「{Trim(actual)}」）"));
        }

        // Rule 7: an insert may not sit on a line another action is deleting. "Delete the copy, then
        // insert a heading where it was" reads as coherent and is not: the anchor would not exist.
        var deleted = all.OfType<DeleteOriginalLine>().Select(delete => delete.Line).ToHashSet();
        foreach (var insert in all.OfType<InsertLineAtAnchor>())
        {
            var anchorLine = insert.Anchor switch
            {
                BeforeOriginalLine before => before.Line,
                AfterOriginalLine after => after.Line,
                _ => 0,
            };
            if (anchorLine != 0 && deleted.Contains(anchorLine))
                problems.Add(new SourcePatchProblem("锚点落在被删除的行上",
                    $"第 {anchorLine} 行被删除，插入点不能选在它上面"));
        }

        // Rule 8: two different actions must not produce contradictory physical operations.
        var byLine = all.Where(operation => operation is DeleteOriginalLine or ReplaceOriginalLine)
            .GroupBy(operation => operation switch
            {
                DeleteOriginalLine delete => delete.Line,
                ReplaceOriginalLine replace => replace.Line,
                _ => 0,
            });

        // Rule 1, 2, 3: at most one delete and at most one replace per line, and never both.
        var kept = new List<SourceOperation>();
        foreach (var group in byLine)
        {
            var deletes = group.OfType<DeleteOriginalLine>().ToArray();
            var replaces = group.OfType<ReplaceOriginalLine>().ToArray();
            if (deletes.Length > 1 || replaces.Length > 1 || (deletes.Length > 0 && replaces.Length > 0))
            {
                var owners = group.Select(operation => operation.ActionId).Distinct().Count();
                problems.Add(new SourcePatchProblem("同一行被多个操作占用",
                    owners > 1
                        ? $"第 {group.Key} 行同时被 {owners} 个动作要求改动"
                        : $"第 {group.Key} 行被同一个动作要求改动多次"));
                continue;
            }
            kept.AddRange(deletes);
            kept.AddRange(replaces);
        }

        // An insert with no text would add a blank line that nothing accounts for.
        foreach (var insert in all.OfType<InsertLineAtAnchor>().Where(insert => insert.Text.Length == 0))
            problems.Add(new SourcePatchProblem("插入内容为空", $"动作 {Short(insert.ActionId)} 要求插入空行"));

        if (problems.Count > 0) return SourcePatchValidation.Rejected(problems);

        // Rule 4: a stable order for inserts that share an anchor.
        var inserts = all.OfType<InsertLineAtAnchor>()
            .OrderBy(insert => AnchorSortKey(insert.Anchor))
            .ThenBy(insert => insert.OperationId, StringComparer.Ordinal);
        var ordered = kept
            .OrderBy(operation => operation switch
            {
                DeleteOriginalLine delete => delete.Line,
                ReplaceOriginalLine replace => replace.Line,
                _ => int.MaxValue,
            })
            .Concat<SourceOperation>(inserts)
            .ToArray();

        return new SourcePatchValidation(new SourcePatch(actionSetId, baseSourceSha256, ordered), []);
    }

    /// <summary>
    /// Inserts are ordered by where they land, then by ordinal within that anchor, then by operation id
    /// so that two inserts with the same anchor and ordinal still come out in a fixed order. Without the
    /// last term the rendered bytes would depend on the order the caller happened to build the list in.
    /// </summary>
    private static (int Kind, int Line, int Ordinal) AnchorSortKey(SourceAnchor anchor) => anchor switch
    {
        BeforeOriginalLine before => (0, before.Line, before.Ordinal),
        AfterOriginalLine after => (1, after.Line, after.Ordinal),
        EndOfFileAnchor end => (2, int.MaxValue, end.Ordinal),
        _ => (3, int.MaxValue, SourceAnchor.OrdinalOf(anchor)),
    };

    private static string Trim(string text) => text.Length <= 24 ? text : text[..24] + "…";

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];
}
