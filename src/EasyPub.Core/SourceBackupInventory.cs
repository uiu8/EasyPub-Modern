using System.Security.Cryptography;
using System.Text.Json;

namespace EasyPub.Core;

/// <summary>
/// Browses and maintains one backup root: which books it holds, what each one has, and what retention
/// may remove.
///
/// Two rules shape everything here.
///
/// **The files are the truth; the manifest is an index.** A manifest that disagrees with the folder is
/// rebuilt from the folder rather than trusted — a backup the reader can see must never be hidden
/// because a JSON file was lost, and a JSON file must never claim a backup that is not there.
///
/// **Nothing here writes to a book.** Restoring a backup is a real change to a source file and needs
/// the transaction layer (Phase 5); until that exists the strongest thing this class offers is
/// <see cref="ExportAsync"/>, which writes a *copy* somewhere the caller chooses.
/// </summary>
public sealed class SourceBackupInventory(string root, string? easyPubVersion = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>
    /// 清理时使用的保留数。<see cref="SourceBackupStore"/> 会把它设成 store 上的值 ——
    /// 少了这一步，自动清理会一直用默认的 20，而设置里写的数字根本没生效。
    /// </summary>
    public int RetentionLimit { get; init; } = SourceBackupRetention.DefaultSnapshotLimit;

    public static SourceBackupInventory CreateDefault(string? easyPubVersion = null) =>
        new(SourceBackupLayout.DefaultRoot(), easyPubVersion);

    /// <summary>
    /// Every book this root holds a backup for. Discovery is by folder, not by manifest: a folder whose
    /// manifest was lost still has backups in it, and a reader looking at this list must see them.
    /// </summary>
    public IReadOnlyList<SourceBackupManifest> ListBooks(Func<string, bool>? sourceExists = null)
    {
        if (!Directory.Exists(Root)) return [];
        var books = new List<SourceBackupManifest>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            // No manifest means no recorded path: the folder name is a hash, so the path cannot be
            // recovered from it. The backups are still counted and prunable, because silently hiding
            // them is exactly how backups appear to vanish.
            var known = ReadManifestFile(folder)?.SourcePath ?? "";
            var snapshots = CountSnapshots(folder);
            if (!File.Exists(SourceBackupLayout.BaselinePath(folder)) && snapshots == 0) continue;
            _ = sourceExists?.Invoke(known);
            books.Add((ReadManifestFile(folder) ?? Adopted(folder, known)) with { SnapshotCount = snapshots });
        }
        return books.OrderByDescending(LastTouched).ToArray();
    }

    /// <summary>A folder with no readable index: describe it from what is on disk alone.</summary>
    private static SourceBackupManifest Adopted(string folder, string sourcePath) => new(
        sourcePath,
        sourcePath.Length > 0 ? Path.GetFileName(sourcePath) : null,
        new DirectoryInfo(folder).CreationTimeUtc is var created && created.Year > 2000
            ? new DateTimeOffset(created, TimeSpan.Zero)
            : DateTimeOffset.MinValue,
        DateTimeOffset.MinValue,
        null,
        0);

    /// <summary>Everything one book has, newest first, with the baseline always listed last.</summary>
    public IReadOnlyList<SourceBackupEntry> ListEntries(string sourcePath)
    {
        var folder = SourceBackupLayout.FolderFor(Root, sourcePath);
        if (!Directory.Exists(folder)) return [];
        var entries = new List<SourceBackupEntry>();

        var baseline = SourceBackupLayout.BaselinePath(folder);
        if (File.Exists(baseline)) entries.Add(Describe(sourcePath, baseline, SourceBackupKind.Baseline));

        var snapshots = Path.Combine(folder, SourceBackupLayout.SnapshotsFolder);
        if (Directory.Exists(snapshots))
            entries.AddRange(Directory.EnumerateFiles(snapshots, "*.bak")
                .Select(path => Describe(sourcePath, path, SourceBackupKind.Snapshot)));

        // Newest first, but the baseline is pinned to the end: it is the one entry a reader may never
        // treat as "just another snapshot" to be pruned.
        return entries
            .OrderBy(entry => entry.Kind == SourceBackupKind.Baseline ? 1 : 0)
            .ThenByDescending(entry => entry.SavedAt)
            .ToArray();
    }

    /// <summary>Copies one backup out to a path the caller chose. Never writes to the book.</summary>
    public async Task<long> ExportAsync(string backupPath, string destinationPath, CancellationToken token = default)
    {
        if (!File.Exists(backupPath)) throw new FileNotFoundException("备份文件不存在。", backupPath);
        var destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var input = File.OpenRead(backupPath))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            await input.CopyToAsync(output, token).ConfigureAwait(false);
        return new FileInfo(destination).Length;
    }

    /// <summary>
    /// Applies the retention limit to one book. The baseline is never a candidate; the newest
    /// <paramref name="limit"/> snapshots are kept.
    ///
    /// Refuses outright while a journal exists for this book: a transaction that has not finished may
    /// still name a snapshot it needs to roll back to, and deleting that would turn a recoverable
    /// crash into an unrecoverable one. Phase 5 creates those journals; the check is here now so the
    /// rule cannot be forgotten when it starts to matter.
    /// </summary>
    public SourceBackupPruneResult Prune(string sourcePath, int limit)
    {
        var folder = SourceBackupLayout.FolderFor(Root, sourcePath);
        if (Directory.Exists(Path.Combine(folder, JournalFolderName)))
            return new SourceBackupPruneResult([], [], "本书有未完成的事务记录，已跳过清理以免删掉恢复所需的快照。");

        limit = SourceBackupRetention.Clamp(limit);
        var snapshots = ListEntries(sourcePath)
            // Ties are broken by file name so the order is total: with timestamps that differ by less
            // than the file system's resolution, "the newest 20" would otherwise be a different set on
            // each call, and a retention pass could delete a snapshot it kept a moment ago.
            .Where(entry => entry.Kind == SourceBackupKind.Snapshot)
            .OrderByDescending(entry => entry.SavedAt)
            .ThenBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var removed = new List<string>();
        var kept = new List<string>();
        for (var index = 0; index < snapshots.Length; index++)
        {
            if (index < limit) { kept.Add(snapshots[index].Path); continue; }
            try
            {
                File.Delete(snapshots[index].Path);
                // The tree file belongs to the bytes beside it. Leaving it behind would strand a snapshot
                // that a later reader would count as restorable while its bytes are gone.
                var tree = SourceBackupLayout.SnapshotTreePath(
                    BookFolderOf(snapshots[index].Path), snapshots[index].Sha256);
                if (File.Exists(tree)) File.Delete(tree);
                removed.Add(snapshots[index].Path);
            }
            catch (IOException) { kept.Add(snapshots[index].Path); }
            catch (UnauthorizedAccessException) { kept.Add(snapshots[index].Path); }
        }
        if (removed.Count > 0) WriteManifest(folder, sourcePath);
        return new SourceBackupPruneResult(removed, kept, null);
    }

    /// <summary>Transaction journals live beside the backups, named the same way Phase 5 will name them.</summary>
    internal const string JournalFolderName = "journal";

    /// <summary>Applies this root's configured limit to every book it holds.</summary>
    public SourceBackupPruneResult PruneAll() => PruneAll(RetentionLimit);

    /// <summary>Applies the limit to every book this root holds. One failure never stops the rest.</summary>
    public SourceBackupPruneResult PruneAll(int limit)
    {
        var removed = new List<string>();
        var kept = new List<string>();
        string? refused = null;
        foreach (var book in ListBooks())
        {
            if (string.IsNullOrWhiteSpace(book.SourcePath)) continue;
            var result = Prune(book.SourcePath, limit);
            removed.AddRange(result.Removed);
            kept.AddRange(result.Kept);
            refused ??= result.RefusedBecause;
        }
        return new SourceBackupPruneResult(removed, kept, refused);
    }

    /// <summary>The recorded state of one book, or null when there is no readable index for it.</summary>
    public SourceBackupManifest? ReadBookManifest(string sourcePath) =>
        ReadManifestFile(SourceBackupLayout.FolderFor(Root, sourcePath));

    internal SourceBackupManifest? ReadManifestFile(string folder)
    {
        var path = SourceBackupLayout.ManifestPath(folder);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<SourceBackupManifest>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt index must not hide the backups it indexes; treat it as absent.
            return null;
        }
    }

    /// <summary>Writes the manifest from what is actually on disk. Idempotent, so it is safe to re-run.</summary>
    internal void WriteManifest(string folder, string sourcePath)
    {
        var baseline = SourceBackupLayout.BaselinePath(folder);
        var existing = ReadManifestFile(folder);
        var snapshotCount = CountSnapshots(folder);
        var manifest = new SourceBackupManifest(
            Path.GetFullPath(sourcePath),
            Path.GetFileName(sourcePath),
            existing?.CreatedAt is { Year: > 2000 } created ? created : DateTimeOffset.Now,
            DateTimeOffset.Now,
            File.Exists(baseline) ? Hash(baseline) : null,
            snapshotCount,
            easyPubVersion ?? existing?.EasyPubVersion)
        {
            RetentionLimit = existing?.RetentionLimit ?? SourceBackupRetention.DefaultSnapshotLimit,
            FullStateSnapshotCount = CountFullStateSnapshots(folder),
        };
        try
        {
            Directory.CreateDirectory(folder);
            var temporary = SourceBackupLayout.ManifestPath(folder) + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(manifest, Json));
            File.Move(temporary, SourceBackupLayout.ManifestPath(folder), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index is a convenience; failing to write it must not fail the backup itself.
        }
    }

    private SourceBackupEntry Describe(string sourcePath, string path, SourceBackupKind kind)
    {
        var info = new FileInfo(path);
        var sha = kind == SourceBackupKind.Snapshot
            // Snapshots are content-addressed: the file name already is the hash, so re-reading the
            // bytes to compute it again would be pure cost on a folder that can hold hundreds of them.
            ? Path.GetFileNameWithoutExtension(path).ToUpperInvariant()
            : SafeHash(path);
        return new SourceBackupEntry(sourcePath, path, kind, sha, info.Length,
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero))
        {
            HasSavedTree = SourceBackupLayout.HasSnapshotTree(BookFolderOf(path), sha),
        };
    }

    /// <summary>The book's backup folder, given a file inside <c>snapshots/</c> or <c>baseline/</c>.</summary>
    private static string BookFolderOf(string filePath) =>
        Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? "") ?? "";

    private static string SafeHash(string path)
    {
        try { return Hash(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private int CountSnapshots(string folder)
    {
        var snapshots = Path.Combine(folder, SourceBackupLayout.SnapshotsFolder);
        return Directory.Exists(snapshots) ? Directory.EnumerateFiles(snapshots, "*.bak").Count() : 0;
    }

    /// <summary>How many snapshots carry a chapter tree, so the window knows a full-state restore is on offer.</summary>
    private static int CountFullStateSnapshots(string folder)
    {
        var snapshots = Path.Combine(folder, SourceBackupLayout.SnapshotsFolder);
        return Directory.Exists(snapshots)
            ? Directory.EnumerateFiles(snapshots, "*.tree.json").Count()
            : 0;
    }

    private static DateTimeOffset LastTouched(SourceBackupManifest book) =>
        book.UpdatedAt != default ? book.UpdatedAt : book.CreatedAt;
}
