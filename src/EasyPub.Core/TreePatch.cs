namespace EasyPub.Core;

/// <summary>
/// One change to the chapter tree, with what it needs in order to be carried out later.
///
/// <para>Every mutation carries a <see cref="SourceBinding"/> so it can be re-applied on a new source
/// version without the plan being rebuilt. A mutation that named a physical line would be meaningless
/// after an edit: lines have been deleted and inserted, so everything below the first change has moved.
/// The binding says which kind of position it refers to and lets the coordinate map resolve it.</para>
///
/// <para>Every mutation also names the action that demanded it. Without that the receipt could list what
/// changed but not why, and a row the user unticked could not be traced to the change it should remove.</para>
/// </summary>
public abstract record ChapterTreeMutation
{
    public required string ActionId { get; init; }
    public required SourceBinding Binding { get; init; }
}

/// <summary>A chapter or volume that now exists. Its body is stated separately, in BaseSource coordinates.</summary>
public sealed record InsertEntry(
    string SemanticId,
    string Title,
    int Level,
    bool IncludeInToc,
    BodyAssignment Body) : ChapterTreeMutation;

/// <summary>An existing entry's title changes.</summary>
public sealed record RetitleEntry(string SemanticId, string NewTitle) : ChapterTreeMutation;

/// <summary>An existing entry leaves the tree — folded back into the text, or a duplicate copy dropped.</summary>
public sealed record RemoveEntry(string SemanticId, bool TextStaysInBody) : ChapterTreeMutation;

/// <summary>An entry's level changes; it stays in the tree.</summary>
public sealed record ChangeEntryLevel(string SemanticId, int NewLevel) : ChapterTreeMutation;

/// <summary>An entry keeps its place but not its old body: text moved to or away from it.</summary>
public sealed record ReassignEntryBody(string SemanticId, BodyAssignment Body) : ChapterTreeMutation;

/// <summary>
/// Everything one repair would do to the chapter tree.
///
/// <para>Kept apart from <see cref="SourcePatch"/> because the two are not one-to-one. Dropping a
/// duplicate copy changes the tree and, by default, not a single byte; retitling changes both; building a
/// volume level changes only the tree. Expressing both with one list of line operations would mean one of
/// them lying about what it does.</para>
///
/// <para><see cref="ExpectedResult"/> travels with the patch so that whoever carries it out can prove the
/// tree they built is the tree that was agreed. Without it a recovery can only re-run the work and hope;
/// with it, a materialised tree that does not match is caught rather than saved.</para>
/// </summary>
public sealed record TreePatch(
    string ActionSetId,
    IReadOnlyList<ChapterTreeMutation> Mutations,
    CanonicalChapterModel ExpectedResult)
{
    public bool IsEmpty => Mutations.Count == 0;

    /// <summary>
    /// Mutations in a stable order, so two runs over the same decisions produce the same patch.
    ///
    /// <para>Ordered by target then by kind then by action id. The exact order is not meaningful — the
    /// mutations are applied as a set — but a stable one makes the patch diffable, hashable and
    /// comparable in tests, which an order that depended on iteration order would not.</para>
    /// </summary>
    public IReadOnlyList<ChapterTreeMutation> Ordered() => Mutations
        .OrderBy(mutation => mutation switch
        {
            InsertEntry insert => insert.SemanticId,
            RetitleEntry retitle => retitle.SemanticId,
            RemoveEntry remove => remove.SemanticId,
            ChangeEntryLevel level => level.SemanticId,
            ReassignEntryBody body => body.SemanticId,
            _ => "",
        }, StringComparer.Ordinal)
        .ThenBy(mutation => mutation.GetType().Name, StringComparer.Ordinal)
        .ThenBy(mutation => mutation.ActionId, StringComparer.Ordinal)
        .ToArray();
}

/// <summary>
/// Computes which source lines a chapter owns, given the boundaries the repair decided on.
///
/// <para>This is the piece that used to be left implicit — "the text between this heading and the next" —
/// and it is exactly where the two landing modes could quietly disagree. Stating it once, in BaseSource
/// coordinates, is what makes them comparable: neither mode gets to work it out for itself.</para>
///
/// <para>A boundary line belongs to its own chapter as the heading, so the body starts after it. A line
/// the repair removed belongs to nobody, which is what makes "deleted" and "still in the text but not in
/// the finished book" the same answer here rather than two different ones.</para>
/// </summary>
public static class BodyAssignmentCalculator
{
    /// <summary>
    /// The body of one chapter: the lines after its heading and before the next boundary, minus the ones
    /// the repair removed.
    /// </summary>
    public static BodyAssignment ForChapter(int headingLine, int nextBoundaryLine, int lineCount,
        IReadOnlySet<int> allowed, IReadOnlySet<int> removed)
    {
        var end = nextBoundaryLine > headingLine ? nextBoundaryLine - 1 : lineCount;
        var spans = new List<SourceLineSpan>();
        var start = 0;
        for (var line = headingLine + 1; line <= end; line++)
        {
            var owned = allowed.Contains(line) && !removed.Contains(line);
            if (owned && start == 0) start = line;
            else if (!owned && start != 0)
            {
                spans.Add(new SourceLineSpan(start, line));
                start = 0;
            }
        }
        if (start != 0) spans.Add(new SourceLineSpan(start, end + 1));
        return new BodyAssignment(spans);
    }

    /// <summary>
    /// The body owned by the front matter: everything before the first boundary, minus what was removed.
    /// </summary>
    public static BodyAssignment ForFrontMatter(int firstBoundaryLine, int lineCount,
        IReadOnlySet<int> allowed, IReadOnlySet<int> removed) =>
        ForChapter(0, firstBoundaryLine > 0 ? firstBoundaryLine : lineCount + 1, lineCount, allowed, removed);

    /// <summary>
    /// Recomputes every entry's body from the boundaries, so the whole tree is stated in one consistent
    /// way. Entries without a heading line — a volume the directory implies, the front matter — keep the
    /// text they had, because nothing in the file marks where they start.
    /// </summary>
    public static IReadOnlyList<ChapterTreeEntry> ReassignBodies(
        IReadOnlyList<ChapterTreeEntry> entries, int lineCount, IReadOnlyCollection<int> removed)
    {
        var allowed = RepairIntegrity.Coverage(entries);
        var dropped = removed.ToHashSet();
        var boundaries = entries
            .Where(entry => !entry.IsFrontMatter && entry.TitleLineNumber is not null)
            .Select(entry => entry.TitleLineNumber!.Value)
            .Order()
            .ToArray();

        var result = new List<ChapterTreeEntry>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry.IsFrontMatter || entry.TitleLineNumber is null)
            {
                result.Add(entry);
                continue;
            }
            var heading = entry.TitleLineNumber.Value;
            var next = boundaries.FirstOrDefault(line => line > heading);
            if (next == 0) next = lineCount + 1;
            var body = ForChapter(heading, next, lineCount, allowed, dropped);
            result.Add(entry with { ContentRanges = body.ToRanges() });
        }
        return result;
    }

    /// <summary>Converts the ranges the rest of the code uses into an assignment in the same coordinates.</summary>
    public static BodyAssignment FromRanges(IReadOnlyList<ChapterSourceRange> ranges) =>
        BodyAssignment.FromRanges(ranges);

    /// <summary>The lines an assignment claims, for comparing two assignments by content.</summary>
    public static bool SameLines(BodyAssignment left, BodyAssignment right) =>
        left.Lines().Order().SequenceEqual(right.Lines().Order());
}
