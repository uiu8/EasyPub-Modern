using System.Security.Cryptography;
using System.Text;

namespace EasyPub.Core;

/// <summary>
/// Where one book's backups live, and how to find them again.
///
/// The directory name is the full source path hashed, not the file name: two books called 书稿.txt in
/// different folders must not share a backup folder, and renaming a book must not silently orphan its
/// backups. A short readable name sits beside the hash for a human browsing the folder.
/// </summary>
public static class SourceBackupLayout
{
    public const string ManifestFileName = "manifest.json";
    public const string BaselineFolder = "baseline";
    public const string SnapshotsFolder = "snapshots";
    public const string ReceiptsFolder = "receipts";

    /// <summary>Hashed folder name for one source path. Upper-cased so Windows path case never splits it.</summary>
    public static string FolderNameFor(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())));
        return hash[..32];
    }

    public static string FolderFor(string root, string sourcePath) =>
        Path.Combine(Path.GetFullPath(root), FolderNameFor(sourcePath));

    public static string BaselinePath(string folder) => Path.Combine(folder, BaselineFolder, "original.bak");

    public static string SnapshotPath(string folder, string sha256) =>
        Path.Combine(folder, SnapshotsFolder, sha256.ToUpperInvariant() + ".bak");

    /// <summary>
    /// One snapshot's saved chapter tree, for <see cref="RestoreTargetPolicy.FullBookState"/>.
    ///
    /// <para>Reserved here in Phase 2 and written in Phase 5. The layout is fixed now rather than later
    /// because a backup folder written by an older version has to keep being readable: adding a sibling file
    /// is a change an old reader ignores, while moving the folder around is one it cannot survive.</para>
    /// </summary>
    public static string SnapshotTreePath(string folder, string sha256) =>
        Path.Combine(folder, SnapshotsFolder, sha256.ToUpperInvariant() + ".tree.json");

    /// <summary>One snapshot's saved recognition settings. Optional even for a full-state snapshot.</summary>
    public static string SnapshotRecognitionPath(string folder, string sha256) =>
        Path.Combine(folder, SnapshotsFolder, sha256.ToUpperInvariant() + ".recognition.json");

    /// <summary>True when this snapshot carries a tree, i.e. it can be restored as a full book state.</summary>
    public static bool HasSnapshotTree(string folder, string sha256) =>
        File.Exists(SnapshotTreePath(folder, sha256));

    public static string ManifestPath(string folder) => Path.Combine(folder, ManifestFileName);

    public static string ReceiptsDirectory(string folder) => Path.Combine(folder, ReceiptsFolder);

    /// <summary>The default root: a folder beside the book when that folder can hold it, else local app data.</summary>
    public static string DefaultRoot() =>
        Environment.GetEnvironmentVariable("EASYPUB_SOURCE_BACKUPS_PATH")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyPub Modern", "SourceBackups");
}

/// <summary>
/// What one book's backup folder holds. Written once and updated in place; it is the record a reader
/// browses, so it carries the source path in clear text even though the folder name is a hash.
/// </summary>
public sealed record SourceBackupManifest(
    string SourcePath,
    string? FileName,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? BaselineSha256,
    int SnapshotCount,
    string? EasyPubVersion = null)
{
    /// <summary>Longest retained snapshots; the baseline is never counted or removed.</summary>
    public int RetentionLimit { get; init; } = SourceBackupRetention.DefaultSnapshotLimit;

    /// <summary>
    /// How many of <see cref="SnapshotCount"/> also carry a chapter tree.
    ///
    /// <para>Recorded rather than discovered by listing the folder, because the window has to be able to say
    /// whether "恢复到当时的完整书稿状态" is on offer before the reader clicks it. A folder that predates this
    /// field reports 0 and offers source-only restores, which is what it can actually do.</para>
    /// </summary>
    public int FullStateSnapshotCount { get; init; }

    /// <summary>A folder with no manifest is one an older version made; it is adopted, not rejected.</summary>
    public bool IsAdopted => CreatedAt == default;
}

/// <summary>One file a reader can browse or export: the first-seen original, or a repair-time snapshot.</summary>
public sealed record SourceBackupEntry(
    string SourcePath,
    string Path,
    SourceBackupKind Kind,
    string Sha256,
    long SizeBytes,
    DateTimeOffset SavedAt)
{
    /// <summary>
    /// True when this snapshot also carries the chapter tree that went with its bytes, i.e. it can be
    /// restored as a full book state rather than only as text.
    /// </summary>
    public bool HasSavedTree { get; init; }

    /// <summary>What the window shows beside the size when a full-state restore is possible.</summary>
    public string? TreeLabel => HasSavedTree ? "含章节树" : null;

    public string KindLabel => Kind switch
    {
        SourceBackupKind.Baseline => "首次原文",
        SourceBackupKind.Snapshot => "修复前快照",
        _ => "其他",
    };

    public string SavedAtLabel => SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeLabel => SizeBytes >= 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024.0:F1} MB"
        : $"{Math.Max(1, SizeBytes / 1024)} KB";

    /// <summary>First 8 hex digits are what a receipt names; the full value stays available.</summary>
    public string ShortHash => Sha256.Length >= 8 ? Sha256[..8] : Sha256;
}

public enum SourceBackupKind
{
    Baseline,
    Snapshot,
    Other,
}

/// <summary>What a retention pass removed, and what it refused to touch.</summary>
public sealed record SourceBackupPruneResult(
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Kept,
    string? RefusedBecause)
{
    public bool Changed => Removed.Count > 0;
}

/// <summary>
/// Retention policy. The numbers live here rather than at each call site so the window, the settings
/// page and the prune pass cannot disagree about what "keep the last 20" means.
/// </summary>
public static class SourceBackupRetention
{
    public const int DefaultSnapshotLimit = 20;
    public const int MinimumSnapshotLimit = 1;
    public const int MaximumSnapshotLimit = 500;

    public static int Clamp(int requested) =>
        Math.Clamp(requested, MinimumSnapshotLimit, MaximumSnapshotLimit);
}
