using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyPub.Core;

/// <summary>
/// The chapter tree that went with one version of a source file.
///
/// <para>Written beside the snapshot's bytes so that a restore can offer
/// <see cref="RestoreTargetPolicy.FullBookState"/>: source bytes alone bring back the text, but every chapter
/// the reader had arranged — the ones the recognition pass cannot produce, because they were decisions rather
/// than discoveries — would be gone.</para>
///
/// <para>Keyed by the source hash it belongs to, and carrying the line count of the text it was built from.
/// A tree restored against different bytes is not a "close enough" tree; every line number in it means
/// something else. Both are checked on read, and a mismatch is refused rather than repaired.</para>
/// </summary>
public sealed record SourceFullStateSnapshot(string SourceSha256, int LineCount, ChapterTreePlan Tree);

/// <summary>
/// Reads and writes <see cref="SourceFullStateSnapshot"/>.
///
/// <para>The format is written out by hand for the same reason the execution manifest's is: the tree holds
/// records whose meaning would otherwise be decided by type names, and those names would become part of the
/// on-disk format by accident.</para>
/// </summary>
public static class SourceFullStateStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes the tree beside its snapshot. Returns false when it could not be written.</summary>
    public static bool Write(string path, SourceFullStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(ToDto(snapshot), Json));
            // Flushed before the rename for the same reason the manifest is: the file that recovery trusts
            // must not be one that only looks written.
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.Read))
                stream.Flush(flushToDisk: true);
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    /// <summary>Reads the tree beside a snapshot, or null when there is none or it cannot be understood.</summary>
    public static SourceFullStateSnapshot? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<SnapshotDto>(File.ReadAllText(path));
            if (dto is null || dto.SchemaVersion != CurrentSchemaVersion) return null;
            if (string.IsNullOrWhiteSpace(dto.SourceSha256) || dto.Entries is null || dto.Entries.Count == 0) return null;

            var entries = dto.Entries.Select(FromDto).ToArray();
            var plan = new ChapterTreePlan(dto.SourceSha256, entries)
            {
                NumericHeadingRecognition = dto.NumericHeadingRecognition,
                NumericHeadingMinimumBodyLines = dto.NumericHeadingMinimumBodyLines,
                NumericHeadingPattern = dto.NumericHeadingPattern,
                HeadingNumberCorrections = dto.HeadingNumberCorrections,
            };
            // A tree that cannot be validated is reported as absent rather than carried into a restore:
            // "possibly fine" line numbers are worse than no tree at all. The line count has to be the one
            // the tree was built against — validating against the entry count would pass everything.
            ChapterTreeDocument.ValidatePlan(plan, dto.LineCount);
            return new SourceFullStateSnapshot(dto.SourceSha256, dto.LineCount, plan);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static SnapshotDto ToDto(SourceFullStateSnapshot snapshot) => new(
        CurrentSchemaVersion,
        snapshot.SourceSha256,
        snapshot.LineCount,
        snapshot.Tree.NumericHeadingRecognition,
        snapshot.Tree.NumericHeadingMinimumBodyLines,
        snapshot.Tree.NumericHeadingPattern,
        snapshot.Tree.HeadingNumberCorrections,
        snapshot.Tree.Entries.Select(EntryToDto).ToArray());

    private static EntryDto EntryToDto(ChapterTreeEntry entry) => new(
        entry.Id,
        entry.Title,
        entry.Level,
        entry.IncludeInToc,
        entry.TitleLineNumber,
        entry.IsFrontMatter,
        entry.HeadingLevel,
        entry.RecognitionSource,
        (entry.ContentRanges ?? []).Select(range => new RangeDto(range.StartLine, range.EndLine)).ToArray());

    private static ChapterTreeEntry FromDto(EntryDto dto) => new(
        dto.Id,
        dto.Title,
        dto.Level,
        dto.IncludeInToc,
        dto.TitleLineNumber,
        (dto.ContentRanges ?? []).Select(range => new ChapterSourceRange(range.StartLine, range.EndLine)).ToArray())
    {
        IsFrontMatter = dto.IsFrontMatter,
        HeadingLevel = dto.HeadingLevel,
        RecognitionSource = dto.RecognitionSource,
    };

    private sealed record SnapshotDto(
        int SchemaVersion,
        string SourceSha256,
        int LineCount,
        bool? NumericHeadingRecognition,
        int? NumericHeadingMinimumBodyLines,
        string? NumericHeadingPattern,
        string? HeadingNumberCorrections,
        IReadOnlyList<EntryDto>? Entries);

    private sealed record EntryDto(
        string Id,
        string Title,
        int Level,
        bool IncludeInToc,
        int? TitleLineNumber,
        bool IsFrontMatter,
        int HeadingLevel,
        string? RecognitionSource,
        IReadOnlyList<RangeDto>? ContentRanges);

    private sealed record RangeDto(int StartLine, int EndLine);
}
