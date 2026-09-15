using System.Text.Json;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record CatalogPreferences(string Query, string Url, string Text, int MinimumLines);

public static class ReferenceCatalogInput
{
    public static string SettingsPath(string hash) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EasyPub Modern", "Catalogs", hash + ".json");

    public static CatalogPreferences? Load(string hash)
    {
        try { return JsonSerializer.Deserialize<CatalogPreferences>(File.ReadAllText(SettingsPath(hash))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
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
