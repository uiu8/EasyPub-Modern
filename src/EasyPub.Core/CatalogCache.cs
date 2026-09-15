using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EasyPub.Core;

/// <summary>
/// Remembers the directories found for a book name.
///
/// A lookup costs almost nothing but waiting: measured on a real book the network round trips are
/// 98% of a repair pass, and the same book is normally repaired several times in a row while the
/// user checks the result, adjusts, and repairs again. Asking six sites each time is pure waste.
/// Entries expire so a directory that changed upstream is picked up again on its own.
/// </summary>
public static class CatalogCache
{
    /// <summary>How long a remembered directory stays usable. Zero disables the cache entirely.</summary>
    public static TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyPub Modern",
        "CatalogCache");

    public static bool TryRead(
        string name,
        IReadOnlyList<string>? preferred,
        out IReadOnlyList<ReferenceCatalog> catalogs)
    {
        catalogs = [];
        if (Lifetime <= TimeSpan.Zero) return false;
        try
        {
            var path = PathFor(name, preferred);
            if (!File.Exists(path)) return false;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > Lifetime) return false;
            var stored = JsonSerializer.Deserialize<ReferenceCatalog[]>(File.ReadAllText(path), Json);
            if (stored is null || stored.Length == 0) return false;
            catalogs = stored;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    public static void Write(
        string name,
        IReadOnlyList<string>? preferred,
        IReadOnlyList<ReferenceCatalog> catalogs)
    {
        if (catalogs.Count == 0) return;
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathFor(name, preferred), JsonSerializer.Serialize(catalogs, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Forgets everything, so the next lookup asks the sources again.</summary>
    public static void Clear()
    {
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Identity of one lookup: the book name plus the folder rule's source preference, because that
    /// preference changes which source wins and the two answers must not be shared.
    /// </summary>
    private static string PathFor(string name, IReadOnlyList<string>? preferred)
    {
        var identity = name + "\u0000" + string.Join("\u0001", preferred ?? []);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return Path.Combine(Folder, hash + ".json");
    }
}
