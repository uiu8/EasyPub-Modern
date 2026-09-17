using System.Security.Cryptography;
using System.Text;

namespace EasyPub.Core;

/// <summary>One first-before-edit copy per source path; never a chapter-tree file.</summary>
public sealed class SourceBackupStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    /// <summary>
    /// How many repair-time snapshots to keep per book. The baseline is never counted or removed.
    /// Set from settings so the retention pass and the settings page cannot disagree.
    /// </summary>
    public int SnapshotRetentionLimit { get; init; } = SourceBackupRetention.DefaultSnapshotLimit;

    /// <summary>Browse and prune this root, carrying this store's retention limit.</summary>
    public SourceBackupInventory Inventory() => new(DirectoryPath) { RetentionLimit = SnapshotRetentionLimit };

    public string CreateWorkingCopy(string sourcePath)
    {
        EnsureBackup(sourcePath);
        var folder = Path.Combine(DirectoryPath, "WorkingCopies", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, copy, false);
        return copy;
    }

    /// <summary>
    /// The store the app uses: the configured root when the reader has set one, else the default.
    /// Retention comes from the same settings object, so the prune pass and the settings page read the
    /// same number. A settings file that cannot be read falls back to the defaults rather than failing
    /// — being unable to read a preference must never stop a backup from being taken.
    /// </summary>
    public static SourceBackupStore CreateDefault()
    {
        var settings = AppSettingsStore.CreateDefault().Load();
        var root = string.IsNullOrWhiteSpace(settings.SourceBackupRoot)
            ? SourceBackupLayout.DefaultRoot()
            : settings.SourceBackupRoot!;
        return new SourceBackupStore(root)
        {
            SnapshotRetentionLimit = SourceBackupRetention.Clamp(settings.SourceBackupRetentionLimit),
        };
    }

    /// <summary>
    /// The legacy single-file location. Kept because receipts already written name it, and because
    /// backups made by earlier versions live there and must stay findable.
    /// </summary>
    public string BackupPath(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        return Path.Combine(DirectoryPath, SourceBackupLayout.FolderNameFor(full),
            Path.GetFileName(full) + ".bak");
    }

    /// <summary>The current location of a book's first-seen original.</summary>
    public string BaselinePath(string sourcePath) =>
        SourceBackupLayout.BaselinePath(Folder(sourcePath));

    /// <summary>This book's backup folder. One folder per source path, named by a hash of it.</summary>
    public string Folder(string sourcePath) => SourceBackupLayout.FolderFor(DirectoryPath, sourcePath);

    public string EnsureBackup(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        var baseline = BaselinePath(full);
        var legacy = BackupPath(full);
        // An original captured by an earlier version is adopted rather than copied again: copying would
        // either duplicate it or, worse, overwrite a true first-seen original with a later state.
        if (File.Exists(baseline)) { EnsureManifest(full); return baseline; }
        if (File.Exists(legacy)) { EnsureManifest(full); return legacy; }

        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        var temporary = baseline + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(full, temporary, overwrite: false);
            try { File.Move(temporary, baseline, overwrite: false); }
            catch (IOException) when (File.Exists(baseline)) { /* Another editor already saved the original. */ }
            EnsureManifest(full);
            return File.Exists(baseline) ? baseline : legacy;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public IReadOnlyList<string> FindBackups(string sourcePath)
    {
        var full = Path.GetFullPath(sourcePath);
        var folder = Folder(full);
        var files = new List<string>();

        var baseline = SourceBackupLayout.BaselinePath(folder);
        if (File.Exists(baseline)) files.Add(baseline);
        if (File.Exists(BackupPath(full))) files.Add(BackupPath(full));

        var snapshots = Path.Combine(folder, SourceBackupLayout.SnapshotsFolder);
        if (Directory.Exists(snapshots)) files.AddRange(Directory.EnumerateFiles(snapshots, "*.bak"));

        // Copies left beside the book by earlier versions, matched by their exact generated name so an
        // unrelated .bak a reader keeps there is never claimed as ours.
        var parent = Path.GetDirectoryName(full)!;
        if (Directory.Exists(parent))
        {
            var prefix = Path.GetFileName(full) + ".";
            foreach (var path in Directory.EnumerateFiles(parent, "*.bak"))
            {
                var name = Path.GetFileName(path);
                if (name.Length == prefix.Length + 32 + 4 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParseExact(name[prefix.Length..^4], "N", out _)) files.Add(path);
            }
        }
        return files.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
    }

    public string EnsureSnapshot(ChapterTreeDocument document) => EnsureSnapshot(document, null);

    /// <summary>
    /// Records the source as it is now, optionally together with the chapter tree that goes with it.
    ///
    /// <para>Passing the tree — the one the reader arranged, not the one recognition produced — is what makes
    /// <see cref="RestoreTargetPolicy.FullBookState"/> possible for this version. The `.tree.json` file is
    /// written <b>after</b> the bytes are safely in place and its failure is not fatal: a snapshot without a
    /// tree still restores the text, which is strictly better than no snapshot.</para>
    /// </summary>
    public string EnsureSnapshot(ChapterTreeDocument document, ChapterTreePlan? tree)
    {
        var bytes = File.ReadAllBytes(document.SourcePath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(hash, document.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("原始 TXT 已在外部修改，请关闭章节工作台并重新打开后再修复。");
        EnsureBackup(document.SourcePath);
        var folder = Folder(document.SourcePath);
        var directory = Path.Combine(folder, SourceBackupLayout.SnapshotsFolder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, hash + ".bak");
        try
        {
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            output.Write(bytes);
        }
        catch (IOException) when (File.Exists(path))
        {
            if (!SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(bytes))) throw;
        }

        if (tree is not null)
            SourceFullStateStore.Write(SourceBackupLayout.SnapshotTreePath(folder, hash),
                new SourceFullStateSnapshot(hash, document.LineCount, tree));

        EnsureManifest(document.SourcePath);
        // Retention runs after the new snapshot is safely in place, so a failure here can only ever
        // leave too many backups — never too few. It carries this store's limit, not the default:
        // reading the default here silently ignored the configured number.
        Inventory().Prune(document.SourcePath, SnapshotRetentionLimit);
        return path;
    }

    /// <summary>
    /// The chapter tree saved beside one version, or null when that version has none.
    ///
    /// <para>Null is the honest answer for a snapshot made before full-state snapshots existed, or by a
    /// source-only pass. The caller then offers a source-only restore instead of pretending.</para>
    /// </summary>
    public SourceFullStateSnapshot? ReadSnapshotTree(string sourcePath, string sha256)
    {
        var folder = Folder(sourcePath);
        var snapshot = SourceFullStateStore.Read(SourceBackupLayout.SnapshotTreePath(folder, sha256));
        // A tree whose recorded hash is not the one asked for belongs to another version. Re-binding it would
        // move every line number in it by however much the two versions differ.
        return snapshot is not null && string.Equals(snapshot.SourceSha256, sha256, StringComparison.OrdinalIgnoreCase)
            ? snapshot
            : null;
    }

    /// <summary>Which of a book's snapshots can be restored as a full book state.</summary>
    public bool HasSnapshotTree(string sourcePath, string sha256) =>
        SourceBackupLayout.HasSnapshotTree(Folder(sourcePath), sha256);

    /// <summary>Whether this backup root can offer a full-state restore for a book at all.</summary>
    public bool CanRestoreFullState(string sourcePath) =>
        ListSnapshotHashes(sourcePath).Any(hash => HasSnapshotTree(sourcePath, hash));

    private IEnumerable<string> ListSnapshotHashes(string sourcePath)
    {
        var directory = Path.Combine(Folder(sourcePath), SourceBackupLayout.SnapshotsFolder);
        if (!Directory.Exists(directory)) return [];
        return Directory.EnumerateFiles(directory, "*.bak")
            .Select(path => Path.GetFileNameWithoutExtension(path).ToUpperInvariant());
    }

    /// <summary>Writes the folder's index from what is on disk. Never throws into a backup path.</summary>
    private void EnsureManifest(string sourcePath)
    {
        try { Inventory().WriteManifest(Folder(sourcePath), sourcePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
