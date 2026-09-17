namespace EasyPub.Core;

/// <summary>
/// What one repair action does, described in three independent dimensions.
///
/// <para>The three are separate because they can differ: cancelling a chapter boundary changes the tree
/// and moves which chapter owns the text, but need not change a single byte; retitling changes the text
/// and the tree but not ownership; inserting a heading does all three.</para>
///
/// <para>Each dimension has an <c>Indeterminate</c> case that a compiler must write out explicitly. That
/// is the point: an action whose effect nobody has defined cannot quietly default to "no effect" — it
/// has to say so, and <c>EditSource</c> refuses it. A new action kind therefore cannot slip into the
/// source-editing path by omission.</para>
/// </summary>
public sealed record RepairEffect(
    TreeEffect Tree,
    SourceContentEffect Source,
    BodyOwnershipEffect Body)
{
    /// <summary>
    /// Which lines the chapter this action concerns owns afterwards, in BaseSource coordinates.
    ///
    /// <para>Carried on the effect rather than derived later because deriving it is exactly what went
    /// wrong before: <c>InsertHeading</c> used to leave "which lines belong to the new chapter" to an
    /// implicit rule about anchors, so the two landing modes could each work it out differently and
    /// there was nothing to compare. Stating it once is what makes them agree.</para>
    ///
    /// <para>Null means the action does not move any text, which is not the same as "owns nothing".</para>
    /// </summary>
    public BodyAssignment? Ownership { get; init; }

    /// <summary>True when every dimension is defined, which is what source editing requires.</summary>
    public bool IsFullyDetermined =>
        Tree is not TreeEffectIndeterminate
        && Source is not SourceContentIndeterminate
        && Body is not BodyOwnershipIndeterminate;

    /// <summary>Which dimensions are undefined, for the message shown when an action is refused.</summary>
    public IReadOnlyList<string> IndeterminateDimensions()
    {
        var names = new List<string>();
        if (Tree is TreeEffectIndeterminate) names.Add("树形态");
        if (Source is SourceContentIndeterminate) names.Add("文本内容");
        if (Body is BodyOwnershipIndeterminate) names.Add("正文归属");
        return names;
    }
}

// ── 树形态 ──────────────────────────────────────────────────────

public abstract record TreeEffect;
public sealed record TreeEffectUnchanged : TreeEffect;
public sealed record TreeEffectInsertEntry(string Title, int Level) : TreeEffect;
public sealed record TreeEffectRetitleEntry(string NewTitle) : TreeEffect;
public sealed record TreeEffectRemoveEntry : TreeEffect;
public sealed record TreeEffectChangeLevel(int NewLevel) : TreeEffect;
public sealed record TreeEffectReassignBodyRanges : TreeEffect;
public sealed record TreeEffectBuildVolumeNode(string VolumeTitle, int Level) : TreeEffect;
/// <summary>Nobody has said what this action does to the tree. Source editing refuses it.</summary>
public sealed record TreeEffectIndeterminate : TreeEffect;

// ── 文本内容 ────────────────────────────────────────────────────

public abstract record SourceContentEffect;
public sealed record SourceContentUnchanged : SourceContentEffect;
public sealed record SourceContentReplace(IReadOnlyList<ReplaceIntent> Replacements) : SourceContentEffect;
public sealed record SourceContentDelete(IReadOnlyList<SourceLineSpan> Spans) : SourceContentEffect;
public sealed record SourceContentInsert(IReadOnlyList<InsertIntent> Inserts) : SourceContentEffect;
/// <summary>Nobody has said what this action does to the text. Source editing refuses it.</summary>
public sealed record SourceContentIndeterminate : SourceContentEffect;

/// <summary>One line's text is replaced. The old text is carried so a mismatch is detectable, not silently overwritten.</summary>
public sealed record ReplaceIntent(int Line, string ExpectedOldText, string NewText);

/// <summary>Lines are added at an anchor. The anchor is a real position, not a line number that may not exist.</summary>
public sealed record InsertIntent(SourceAnchor Anchor, string Text);

/// <summary>A half-open span of source lines: <c>[Start, End)</c>, in BaseSource coordinates.</summary>
public sealed record SourceLineSpan(int Start, int End);

// ── 正文归属 ────────────────────────────────────────────────────

public abstract record BodyOwnershipEffect;
/// <summary>The text keeps the owner it had. Distinct from "there is no text".</summary>
public sealed record BodyOwnershipUnchanged : BodyOwnershipEffect;
/// <summary>The text belongs to a different chapter now, and the new assignment is stated.</summary>
public sealed record BodyOwnershipAssigned(BodyAssignment Assignment) : BodyOwnershipEffect;
/// <summary>Nobody has said who owns the text. Source editing refuses it.</summary>
public sealed record BodyOwnershipIndeterminate : BodyOwnershipEffect;

/// <summary>
/// Which source lines a chapter owns, in <b>BaseSource</b> coordinates.
///
/// <para>BaseSource coordinates — the lines as they are in the file the plan was built against — and not
/// "wherever they end up after the edit". Stating it this way is what lets the two landing modes be
/// compared at all: <c>TreeOnly</c> uses these spans directly, and <c>EditSource</c> maps them through
/// <c>SourceCoordinateMap</c>. If each mode computed its own answer there would be nothing to check them
/// against.</para>
///
/// <para>Multiple spans, not one range, because a chapter's body already can be disjoint — a heading
/// moved out of the middle leaves the text on both sides of where it was.</para>
/// </summary>
public sealed record BodyAssignment(IReadOnlyList<SourceLineSpan> Spans)
{
    public static BodyAssignment Empty { get; } = new([]);

    /// <summary>Builds from inclusive ranges, which is how the rest of the code states them.</summary>
    public static BodyAssignment FromRanges(IEnumerable<ChapterSourceRange> ranges) =>
        new(ranges.Where(range => range.EndLine >= range.StartLine)
            .Select(range => new SourceLineSpan(range.StartLine, range.EndLine + 1))
            .ToArray());

    public IReadOnlyList<ChapterSourceRange> ToRanges() =>
        Spans.Where(span => span.End > span.Start)
            .Select(span => new ChapterSourceRange(span.Start, span.End - 1))
            .ToArray();

    /// <summary>Every line this assignment claims, ascending and distinct.</summary>
    public IEnumerable<int> Lines() =>
        Spans.SelectMany(span => Enumerable.Range(span.Start, Math.Max(0, span.End - span.Start)));
}

/// <summary>
/// Where an insert goes.
///
/// <para>A discriminated type rather than <c>(line, placement)</c>, because that pair admits states that
/// cannot happen: an end-of-file insert that also names a line, or a line number of zero. Those states
/// would have to be rejected at every use; here they cannot be built.</para>
///
/// <para><c>Ordinal</c> is declared on each case and deliberately <b>not</b> on this base type. A base
/// declaration is shadowed by the positional parameter of the same name, so the base member would stay 0
/// forever while the real value sat on the case — and every read through the base type would silently
/// sort as if all ordinals were zero.</para>
/// </summary>
public abstract record SourceAnchor
{
    /// <summary>Position among inserts sharing this anchor. Read through <see cref="OrdinalOf"/>.</summary>
    public static int OrdinalOf(SourceAnchor anchor) => anchor switch
    {
        BeforeOriginalLine before => before.Ordinal,
        AfterOriginalLine after => after.Ordinal,
        EndOfFileAnchor end => end.Ordinal,
        _ => 0,
    };
}

/// <summary>Before the given original line (1-based).</summary>
public sealed record BeforeOriginalLine(int Line, int Ordinal = 0) : SourceAnchor;

/// <summary>After the given original line (1-based).</summary>
public sealed record AfterOriginalLine(int Line, int Ordinal = 0) : SourceAnchor;

/// <summary>At the end of the file. Needs no line, which is what makes an empty file expressible.</summary>
public sealed record EndOfFileAnchor(int Ordinal = 0) : SourceAnchor;
