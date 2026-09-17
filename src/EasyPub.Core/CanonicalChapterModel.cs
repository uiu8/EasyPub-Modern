using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EasyPub.Core;

/// <summary>
/// Where a tree change gets its source line from.
///
/// <para>After an edit the original line numbers no longer exist — lines were deleted and inserted, so
/// everything after the first change has moved. A tree change therefore cannot carry a line number and
/// mean anything; it has to say which <i>kind</i> of position it refers to, and let the coordinate map
/// resolve it. An inserted heading is the case that forces this: it has no original line at all, only the
/// operation that created it.</para>
/// </summary>
public abstract record SourceBinding;

/// <summary>A line of the original file. Valid in both landing modes.</summary>
public sealed record OriginalLineBinding(int Line) : SourceBinding;

/// <summary>A line this repair inserted. Resolvable only after the source edit, through its operation id.</summary>
public sealed record InsertedLineBinding(string OperationId) : SourceBinding;

/// <summary>Nothing in the text backs this change — a volume node the directory implies, for instance.</summary>
public sealed record NoSourceBinding : SourceBinding;

/// <summary>What kind of node a chapter is. Part of the canonical identity because it changes the output.</summary>
public enum CanonicalNodeKind
{
    Chapter,
    Volume,
    FrontMatter,
}

/// <summary>
/// One chapter reduced to what a reader would notice, with every implementation detail removed.
///
/// <para>Line numbers are gone on purpose. <c>EditSource</c> deletes and inserts lines, so after it runs
/// every physical line number differs from <c>TreeOnly</c>; comparing them would report a difference
/// between two results that are the same book. What is compared instead is the order of the chapters,
/// their titles, their levels, whether they are in the table of contents, and what text each one
/// owns.</para>
///
/// <para><see cref="HeadingOrigin"/> deliberately has no "virtual" or "physical" case. That distinction
/// is a storage detail — whether the heading is a real line yet. Including it would make the two modes
/// unequal by construction and destroy the only check that says they produce the same book.</para>
/// </summary>
public sealed record CanonicalChapter(
    string Title,
    int Level,
    bool IncludeInToc,
    CanonicalHeadingOrigin HeadingOrigin,
    CanonicalNodeKind Kind,
    IReadOnlyList<int> ParentPath,
    IReadOnlyList<string> BodyContentHashes);

/// <summary>Why a heading exists, in business terms rather than storage terms.</summary>
public enum CanonicalHeadingOrigin
{
    /// <summary>The source text already had this heading line.</summary>
    ExistingSourceHeading,
    /// <summary>This repair decided the chapter exists and gave it a heading.</summary>
    InsertedByRepair,
    /// <summary>This repair decided an existing line is a heading.</summary>
    AdoptedByRepair,
    /// <summary>A person put it there.</summary>
    Manual,
}

/// <summary>
/// The finished book, as opposed to the tree that produced it.
///
/// <para>Two landing modes are only equivalent if their canonical models are equal. That is the single
/// claim this whole layer exists to make checkable, so the model holds nothing that differs between the
/// modes by construction — no line numbers, no ids, no virtual/physical flag.</para>
/// </summary>
public sealed record CanonicalChapterModel(IReadOnlyList<CanonicalChapter> Chapters)
{
    public static CanonicalChapterModel Empty { get; } = new([]);

    /// <summary>
    /// Compares two models by content.
    ///
    /// <para>Written out rather than relying on record equality, because the collections inside are
    /// reference types: two models built from equal data would otherwise compare unequal and the check
    /// would always fail — the opposite of useful.</para>
    /// </summary>
    public bool EqualsByContent(CanonicalChapterModel? other)
    {
        if (other is null) return false;
        if (Chapters.Count != other.Chapters.Count) return false;
        for (var index = 0; index < Chapters.Count; index++)
        {
            var left = Chapters[index];
            var right = other.Chapters[index];
            if (left.Title != right.Title) return false;
            if (left.Level != right.Level) return false;
            if (left.IncludeInToc != right.IncludeInToc) return false;
            if (left.HeadingOrigin != right.HeadingOrigin) return false;
            if (left.Kind != right.Kind) return false;
            if (!left.ParentPath.SequenceEqual(right.ParentPath)) return false;
            if (!left.BodyContentHashes.SequenceEqual(right.BodyContentHashes)) return false;
        }
        return true;
    }

    /// <summary>
    /// The first place two models differ, in words. A bare "not equal" on a three-hundred-chapter book
    /// tells nobody which chapter to look at.
    /// </summary>
    public string DescribeFirstDifference(CanonicalChapterModel? other)
    {
        if (other is null) return "另一个模型不存在。";
        var limit = Math.Min(Chapters.Count, other.Chapters.Count);
        for (var index = 0; index < limit; index++)
        {
            var left = Chapters[index];
            var right = other.Chapters[index];
            if (left.Title != right.Title) return $"第 {index + 1} 项标题不同：「{left.Title}」对「{right.Title}」";
            if (left.Level != right.Level) return $"第 {index + 1} 项层级不同：{left.Level} 对 {right.Level}";
            if (left.IncludeInToc != right.IncludeInToc) return $"第 {index + 1} 项是否进目录不同：{left.Title}";
            if (left.HeadingOrigin != right.HeadingOrigin) return $"第 {index + 1} 项标题来源不同：{left.Title}（{left.HeadingOrigin} 对 {right.HeadingOrigin}）";
            if (left.Kind != right.Kind) return $"第 {index + 1} 项节点类型不同：{left.Title}";
            if (!left.ParentPath.SequenceEqual(right.ParentPath)) return $"第 {index + 1} 项父节点不同：{left.Title}";
            if (!left.BodyContentHashes.SequenceEqual(right.BodyContentHashes))
                return $"第 {index + 1} 项正文不同：{left.Title}（{left.BodyContentHashes.Count} 段 对 {right.BodyContentHashes.Count} 段）";
        }
        if (Chapters.Count != other.Chapters.Count)
            return $"章节数量不同：{Chapters.Count} 对 {other.Chapters.Count}"
                + (Chapters.Count > limit ? $"（多出「{Chapters[limit].Title}」）" : $"（多出「{other.Chapters[limit].Title}」）");
        return "没有差异。";
    }
}

/// <summary>Builds the canonical model from a chapter tree and the text it covers.</summary>
public static class CanonicalChapterModelFactory
{
    /// <summary>
    /// <paramref name="lineText"/> reads a line of the text the tree covers — 1-based, and it may return
    /// null for a line past the end.
    ///
    /// <para>The body hashes are computed from <i>what each chapter owns</i>, not from the file's bytes.
    /// That is what makes the comparison meaningful: with 260 lines deleted from the file, the two modes
    /// have different bytes but the same books, and it is the books that must match.</para>
    /// </summary>
    public static CanonicalChapterModel Build(IReadOnlyList<ChapterTreeEntry> entries,
        Func<int, string?> lineText)
    {
        var chapters = new List<CanonicalChapter>(entries.Count);
        // The output index of the most recent node seen at each level. A node's parent is the most
        // recent node one level shallower, which is exactly what this records.
        var lastAtLevel = new Dictionary<int, int>();

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var parent = new List<int>();
            if (entry.Level > 1 && lastAtLevel.TryGetValue(entry.Level - 1, out var ancestor))
                parent.Add(ancestor);

            chapters.Add(new CanonicalChapter(
                entry.Title,
                entry.Level,
                entry.IncludeInToc,
                Origin(entry),
                Classify(entry),
                parent,
                BodyHashes(entry, lineText)));

            lastAtLevel[entry.Level] = index;
            // Anything deeper than this node is no longer in scope: it cannot be the parent of what
            // follows, because a following node at that depth would have had to appear before this one.
            foreach (var deeper in lastAtLevel.Keys.Where(level => level > entry.Level).ToArray())
                lastAtLevel.Remove(deeper);
        }

        return new CanonicalChapterModel(chapters);
    }

    private static CanonicalNodeKind Classify(ChapterTreeEntry entry) =>
        entry.IsFrontMatter ? CanonicalNodeKind.FrontMatter
        : entry.RecognitionSource is "reference-volume" or "inferred-volume" ? CanonicalNodeKind.Volume
        : CanonicalNodeKind.Chapter;

    private static CanonicalHeadingOrigin Origin(ChapterTreeEntry entry)
    {
        if (entry.RecognitionSource == "manual") return CanonicalHeadingOrigin.Manual;
        // A body-level chapter with no heading line was given one by this repair; the front matter has
        // no heading by design and is not a repair.
        if (entry.TitleLineNumber is null && !entry.IsFrontMatter) return CanonicalHeadingOrigin.InsertedByRepair;
        return CanonicalHeadingOrigin.ExistingSourceHeading;
    }

    /// <summary>
    /// One hash per body line, keyed by the line's position within the chapter as well as its text.
    ///
    /// <para>Position matters because a body can legitimately contain the same sentence twice, and a
    /// model that hashed only the text would call two different bodies equal. Including the position
    /// costs nothing and removes that whole class of false "these match".</para>
    /// </summary>
    private static IReadOnlyList<string> BodyHashes(ChapterTreeEntry entry, Func<int, string?> lineText)
    {
        var hashes = new List<string>();
        var position = 0;
        foreach (var line in entry.ContentRanges
                     .OrderBy(range => range.StartLine)
                     .SelectMany(range => Enumerable.Range(range.StartLine, Math.Max(0, range.EndLine - range.StartLine + 1))))
        {
            var text = lineText(line) ?? "";
            hashes.Add(Hash(position.ToString(CultureInfo.InvariantCulture) + "\u001f" + text));
            position++;
        }
        return hashes;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
}
