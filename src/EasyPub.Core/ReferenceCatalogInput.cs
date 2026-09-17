using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record CatalogPreferences(string Query, string Url, string Text, int MinimumLines)
{
    public ReferenceCatalog? Catalog { get; init; }
}

public static class ReferenceCatalogInput
{
    public static ReferenceCatalog? ReadCatalog(CatalogPreferences? saved) => saved?.Catalog
        ?? (string.IsNullOrWhiteSpace(saved?.Text) ? null : ParseText(saved.Text) is { } parsed
            ? parsed with { Source = string.IsNullOrWhiteSpace(saved.Url) ? parsed.Source : saved.Url } : null);

    public static void SaveCatalog(string hash, ReferenceCatalog catalog, string query = "", int minimumLines = 3) =>
        Save(hash, new(query, catalog.Source, string.Join(Environment.NewLine, catalog.Nodes.Select(n => n.Title)), minimumLines)
        { Catalog = catalog });
    public static string SettingsPath(string hash) => Path.Combine(CatalogsRoot(), hash + ".json");

    /// <summary>
    /// The folder saved directories live in.
    ///
    /// <para>Overridable by environment for the same reason the backup root is: a run must be able to keep
    /// its books' directories out of the folder the reader actually uses, and must not read the ones already
    /// there. Without it, anything that saves a directory for a throwaway book writes into
    /// <c>%LOCALAPPDATA%</c> and has to remember to delete it again.</para>
    /// </summary>
    private static string CatalogsRoot() =>
        Environment.GetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPub Modern", "Catalogs");

    public static CatalogPreferences? Load(string hash)
    {
        try { return JsonSerializer.Deserialize<CatalogPreferences>(File.ReadAllText(SettingsPath(hash))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// Remembers the directory for one book, keyed by the source hash, so a later session — or the same
    /// session after the tree is rebuilt — can still name the chapters the release has and the file lacks.
    /// Written through a temporary file: a half-written JSON would lose the directory without saying so.
    /// </summary>
    public static void Save(string hash, CatalogPreferences preferences)
    {
        var path = SettingsPath(hash);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(preferences));
        File.Move(temp, path, true);
    }

    /// <summary>
    /// Picks the directory to trust out of several candidates. The largest wins, and only a candidate within
    /// 10% of it qualifies: sources disagree about how much of a book they list, and silently taking a much
    /// shorter directory would report hundreds of chapters as missing.
    /// </summary>
    public static ReferenceCatalog? Pick(IReadOnlyList<ReferenceCatalog> catalogs)
    {
        var most = catalogs.Count == 0 ? 0 : catalogs.Max(catalog => catalog.Titles.Count);
        return catalogs.FirstOrDefault(catalog => catalog.Titles.Count > 0 && catalog.Titles.Count >= most * 0.9);
    }

    public static ReferenceCatalog? ParseText(string text)
    {
        var nodes = new List<ReferenceNode>();
        string? volume = null;
        foreach (var title in text.Split(['\r','\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (HeadingSyntax.IsVolume(title))
            { volume = title; nodes.Add(new(title,ReferenceNodeKind.Volume,null,volume)); }
            else nodes.Add(new(title,ReferenceNodeKind.Chapter,null,volume));
        }
        return nodes.Count(n=>!n.IsVolume) >= 1 ? new("local", "本地目录文本", nodes) : null;
    }

    /// <summary>Resolve only exact book-name matches from sibling downloader records; never guess an ID.</summary>
    public static string? FindLocalBookUrl(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var name = ChapterAutoRepair.ExtractBookName(path);
        var matches = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(parent).Take(200))
            {
                var id = Path.GetFileName(directory);
                if (id.Length is < 15 or > 22 || !id.All(char.IsAsciiDigit)) continue;
                var status = Path.Combine(directory,"status.json");
                if (!File.Exists(status) || new FileInfo(status).Length > 32*1024*1024) continue;
                try
                {
                    using var file=File.OpenRead(status);
                    using var json=JsonDocument.Parse(file);
                    var root=json.RootElement;
                    if (root.TryGetProperty("book_name",out var title) && title.GetString()?.Trim() == name
                        && root.TryGetProperty("book_id",out var bookId) && bookId.ToString() == id)
                        matches.Add("https://fanqienovel.com/page/"+id);
                }
                catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return matches.Count == 1 ? matches.Single() : null;
    }
}
