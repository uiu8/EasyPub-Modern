using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EasyPub.Core;

/// <summary>
/// What a repair plan was computed against.
///
/// <para>Three fingerprints, because three independent things decide what a repair would do, and any of
/// them changing makes an old plan describe a book that is no longer there:</para>
///
/// <list type="bullet">
/// <item><b>Source</b> — the bytes of the TXT.</item>
/// <item><b>Tree</b> — the chapter structure the user has built on top of those bytes. The same TXT,
/// byte for byte, can carry a completely different tree after a manual edit; binding to the source
/// alone would let a plan computed for one tree be applied to another.</item>
/// <item><b>Recognition</b> — the rules that decide what counts as a heading. Changing them re-derives
/// the whole tree from the same bytes, so a plan built under the old rules no longer matches.</item>
/// </list>
///
/// <para>All three are checked before a plan is applied. None of them is ever recomputed during recovery
/// — see the transaction boundary in the design doc.</para>
/// </summary>
public sealed record RepairBaseVersion(
    string BaseSourceSha256,
    string BaseTreeFingerprint,
    string RecognitionFingerprint);

/// <summary>
/// One fingerprint is a SHA-256 over a canonical byte encoding, so that two runs that see the same
/// book produce the same value and any difference at all produces a different one.
/// </summary>
internal static class RepairFingerprint
{
    /// <summary>
    /// Length-prefixes every part before hashing it.
    ///
    /// Without the prefix, fields that are concatenated can be made to collide by moving a separator
    /// across a boundary — "a|b" + "|c" and "a" + "|b|c" encode identically. Prefixing each part with
    /// its length makes the encoding injective, which is the whole point of a fingerprint.
    /// </summary>
    internal static string Compute(params string?[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            var text = part ?? "";
            builder.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append('\u001f');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    internal static string ComputeLines(IEnumerable<string> lines) =>
        Compute(lines.Select(line => line ?? "").ToArray().Aggregate(new StringBuilder(), (builder, line) =>
        {
            builder.Append(line.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(line).Append('\u001e');
            return builder;
        }).ToString());

    /// <summary>Numbers are formatted invariantly so a machine with a different locale agrees.</summary>
    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    internal static string Flag(bool value) => value ? "1" : "0";
}

/// <summary>
/// The chapter tree, reduced to what would change a repair's decision.
///
/// <para><c>Entry.Id</c> is deliberately excluded. Ids are regenerated whenever a plan is rebuilt from
/// the same input, so including them would invalidate a plan for a book that had not changed at all —
/// every fingerprint check would fail and the user would be told to regenerate a plan that is still
/// perfectly valid.</para>
/// </summary>
public static class ChapterTreeFingerprint
{
    public static string Compute(IEnumerable<ChapterTreeEntry> entries) =>
        RepairFingerprint.ComputeLines(entries.Select(Describe));

    private static string Describe(ChapterTreeEntry entry)
    {
        var ranges = string.Join(',', entry.ContentRanges.Select(range =>
            RepairFingerprint.Number(range.StartLine) + "-" + RepairFingerprint.Number(range.EndLine)));
        return string.Join('|',
            entry.Title ?? "",
            RepairFingerprint.Number(entry.Level),
            RepairFingerprint.Number(entry.HeadingLevel),
            RepairFingerprint.Flag(entry.IncludeInToc),
            RepairFingerprint.Flag(entry.IsFrontMatter),
            entry.TitleLineNumber is { } line ? RepairFingerprint.Number(line) : "none",
            ranges,
            entry.RecognitionSource ?? "");
    }
}

/// <summary>
/// The rules that decide what counts as a heading.
///
/// <para>Every switch that can re-derive a different tree from the same bytes belongs here. Adding one
/// to <see cref="TocHierarchyOptions"/> without adding it here would make a rule change invisible to the
/// version check, which is exactly the failure this fingerprint exists to prevent — so the fields are
/// listed exhaustively rather than by reflection, and a test asserts the list is complete.</para>
/// </summary>
public static class RecognitionFingerprint
{
    public static string Compute(TocHierarchyOptions options, string? chapterPattern) =>
        RepairFingerprint.Compute(
            chapterPattern ?? ChapterEditingDocument.DefaultChapterPattern,
            RepairFingerprint.Flag(options.Enabled),
            RepairFingerprint.Flag(options.RecognizeNumericHeadings),
            RepairFingerprint.Number(options.NumericHeadingMinimumBodyLines),
            options.NumericHeadingPattern ?? "",
            options.HeadingNumberCorrections ?? "",
            options.Level1Pattern ?? "",
            options.Level2Pattern ?? "",
            options.Level3Pattern ?? "",
            // The two output switches below do not change recognition, but they do change what a reader
            // ends up with, and a plan whose preview claimed one thing while the build did another is
            // the kind of drift this binding is for.
            RepairFingerprint.Flag(options.IncludeHtmlTocPage),
            RepairFingerprint.Flag(options.IncludeChapterTopNavigation));

    /// <summary>The property names this fingerprint covers. A test asserts nothing else exists.</summary>
    public static IReadOnlyList<string> CoveredOptionNames { get; } =
    [
        nameof(TocHierarchyOptions.Enabled),
        nameof(TocHierarchyOptions.RecognizeNumericHeadings),
        nameof(TocHierarchyOptions.NumericHeadingMinimumBodyLines),
        nameof(TocHierarchyOptions.NumericHeadingPattern),
        nameof(TocHierarchyOptions.HeadingNumberCorrections),
        nameof(TocHierarchyOptions.Level1Pattern),
        nameof(TocHierarchyOptions.Level2Pattern),
        nameof(TocHierarchyOptions.Level3Pattern),
        nameof(TocHierarchyOptions.IncludeHtmlTocPage),
        nameof(TocHierarchyOptions.IncludeChapterTopNavigation),
    ];
}

/// <summary>
/// The reference directory, reduced to what would change a plan.
///
/// <para>Only the titles and their volume grouping matter: the source address and the page title are
/// provenance, and two directories fetched from different sites that print the same chapters produce
/// the same plan.</para>
/// </summary>
public static class ReferenceCatalogFingerprint
{
    public static string Compute(ReferenceCatalog? catalog) =>
        catalog is null
            ? RepairFingerprint.Compute("no-catalog")
            : RepairFingerprint.ComputeLines(catalog.Nodes.Select(node =>
                (node.IsVolume ? "V|" : "C|") + (node.VolumeTitle ?? "") + "|" + node.Title));
}

/// <summary>Builds the three-fingerprint binding for a plan.</summary>
public static class RepairBaseVersionFactory
{
    public static RepairBaseVersion Capture(
        string sourceSha256,
        IEnumerable<ChapterTreeEntry> entries,
        TocHierarchyOptions recognition,
        string? chapterPattern) =>
        new(sourceSha256,
            ChapterTreeFingerprint.Compute(entries),
            RecognitionFingerprint.Compute(recognition, chapterPattern));
}
