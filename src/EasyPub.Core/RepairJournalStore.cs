using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyPub.Core;

/// <summary>
/// One transaction's progress.
///
/// <para>Deliberately small and deliberately mutable: it records how far the work got, and nothing about
/// what the work was. What the transaction is supposed to do is frozen in
/// <see cref="RepairExecutionManifest"/> before anything is written, and that file is never edited again
/// — so a recovery reads the intent from a file that cannot have drifted, and the progress from one whose
/// whole job is to drift.</para>
///
/// <para><see cref="BaseSha256"/> and <see cref="ExpectedNewSha256"/> are both here because recovery
/// needs them to tell the two possible file states apart, and this file is the one that is guaranteed to
/// exist as soon as a transaction starts.</para>
/// </summary>
public sealed record RepairJournal(
    int SchemaVersion,
    string TransactionId,
    string SourcePath,
    string BaseSha256,
    string ExpectedNewSha256,
    RepairJournalState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// States past which the transaction has claimed to have replaced the file.
    ///
    /// <para>Read by recovery to decide whether a journal that disagrees with the file is merely behind or
    /// is claiming something that did not happen.</para>
    /// </summary>
    public bool HasClaimedReplacement => State is RepairJournalState.SourceReplaced
        or RepairJournalState.DocumentReloaded or RepairJournalState.StateMigrated
        or RepairJournalState.Committed;

    public RepairJournal WithState(RepairJournalState state) =>
        this with { State = state, UpdatedAt = DateTimeOffset.Now };
}

/// <summary>
/// Reads and writes the journal.
///
/// <para>Written with a temporary file and a rename, because the journal is updated at each step and a
/// half-written journal is worse than one that is behind: the recovery would read a truncated file and
/// have to guess. The rename is the only step that decides which of the two versions exists.</para>
/// </summary>
public sealed class RepairJournalStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public string PathFor(string transactionId) =>
        Path.Combine(DirectoryPath, transactionId + ".json");

    public void Save(RepairJournal journal)
    {
        Directory.CreateDirectory(DirectoryPath);
        var target = PathFor(journal.TransactionId);
        var temporary = target + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, journal, Json);
                // The point of the rename is that only a complete file ever carries the real name, so the
                // bytes have to reach the disk before it happens.
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public RepairJournal? Load(string transactionId)
    {
        var path = PathFor(transactionId);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<RepairJournal>(stream, Json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // An unreadable journal is not "no journal": recovery must treat it as something to look at
            // rather than as a transaction that never happened.
            return null;
        }
    }

    /// <summary>
    /// Every journal this directory holds, oldest first.
    ///
    /// <para>Recovery runs over all of them, not just the newest: two interrupted transactions for the same
    /// book is unusual but possible, and silently ignoring the older one would leave a record that forever
    /// blocks backup pruning while never being resolved.</para>
    /// </summary>
    public IReadOnlyList<RepairJournal> LoadAll()
    {
        if (!Directory.Exists(DirectoryPath)) return [];
        var journals = new List<RepairJournal>();
        foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                if (JsonSerializer.Deserialize<RepairJournal>(stream, Json) is { } journal)
                    journals.Add(journal);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return journals.OrderBy(journal => journal.CreatedAt).ToArray();
    }

    /// <summary>Removes a finished transaction's record. The receipt and the backups stay.</summary>
    public void Delete(string transactionId)
    {
        var path = PathFor(transactionId);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>True when anything is still in flight for this book, which blocks backup pruning.</summary>
    public bool HasPending() => LoadAll().Any(journal =>
        journal.State is not (RepairJournalState.Committed or RepairJournalState.RolledBack
            or RepairJournalState.Conflict));
}
