using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyPub.Core;

/// <summary>
/// One directory source the软件 can search. A source only ever describes a public search entry point;
/// nothing here carries credentials except the optional session cookie the user pastes for a site that
/// gates its目录 behind a login.
/// </summary>
public sealed record BookSource
{
    /// <summary>Stable key used by folder rules and by the built-in list. Never localized.</summary>
    public string Id { get; init; } = "";

    /// <summary>Display name shown in the source picker.</summary>
    public string Name { get; init; } = "";

    /// <summary>Search address template; <c>{q}</c> is replaced with the URL-escaped book name.</summary>
    public string SearchUrl { get; init; } = "";

    /// <summary>
    /// Address template for sites that have no keyword search but do accept a direct identifier, e.g.
    /// Fanqie's <c>https://fanqienovel.com/page/{q}</c>. Used when the user pastes an id or a book URL.
    /// </summary>
    public string? DirectUrl { get; init; }

    /// <summary>Optional session cookie for sites that require a login to show volumes (Qidian).</summary>
    public string? Cookie { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Lower runs first. The user reorders with the up/down buttons.</summary>
    public int Priority { get; init; } = 100;

    /// <summary>True for sources shipped with the app; the code owns their addresses.</summary>
    public bool BuiltIn { get; init; }

    /// <summary>True when the source can be queried by book name.</summary>
    [JsonIgnore]
    public bool Searchable => !string.IsNullOrWhiteSpace(SearchUrl);

    /// <summary>True when the source accepts a pasted id or book address.</summary>
    [JsonIgnore]
    public bool Direct => !string.IsNullOrWhiteSpace(DirectUrl);

    /// <summary>What the user is told when the source cannot answer a keyword query.</summary>
    [JsonIgnore]
    public string Capability => (Searchable, Direct) switch
    {
        (true, true) => "搜索 + 网址",
        (true, false) => "搜索",
        (false, true) => "仅网址/编号",
        _ => "未配置",
    };
}

/// <summary>
/// Built-in sources and the persistence of the user's arrangement. Built-in sources are always merged
/// back in on load, so a new release can introduce one without the user losing their own list.
/// </summary>
public static class BookSourceCatalog
{
    /// <summary>
    /// Ordered default list. Qidian and Fanqie lead because their directories carry the publisher's own
    /// volume split, which is the most faithful evidence available for rebuilding a chapter tree.
    /// </summary>
    public static IReadOnlyList<BookSource> BuiltIn { get; } =
    [
        new()
        {
            Id = "qidian", Name = "起点中文网", BuiltIn = true, Priority = 10,
            SearchUrl = "https://www.qidian.com/so/{q}.html",
            DirectUrl = "https://www.qidian.com/book/{q}/",
        },
        new()
        {
            Id = "fanqie", Name = "番茄小说", BuiltIn = true, Priority = 20,
            SearchUrl = "",
            DirectUrl = "https://fanqienovel.com/page/{q}",
        },
        new() { Id = "hetushu", Name = "和图书", BuiltIn = true, Priority = 30, SearchUrl = "https://www.hetushu.com/tag/{q}.html" },
        new() { Id = "shudugu", Name = "书读谷", BuiltIn = true, Priority = 40, SearchUrl = "https://www.shudugu.org/i/sor.aspx?key={q}" },
        new() { Id = "ixdzs", Name = "爱下电子书", BuiltIn = true, Priority = 50, SearchUrl = "https://ixdzs8.com/bsearch?q={q}" },
        new() { Id = "owlook", Name = "偶书网", BuiltIn = true, Priority = 60, SearchUrl = "https://www.owlook.com.cn/search?wd={q}" },
    ];

    public static BookSource? Find(string id) =>
        BuiltIn.FirstOrDefault(source => string.Equals(source.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Restores the built-in entries and lets the code own their addresses, so a fix to a search URL
    /// reaches existing installs. Everything else the user configured (order, enabled, cookie, own
    /// sources) is preserved.
    /// </summary>
    public static IReadOnlyList<BookSource> Merge(IEnumerable<BookSource>? saved)
    {
        var result = (saved ?? []).Where(source => !string.IsNullOrWhiteSpace(source.Id)).ToList();
        foreach (var builtIn in BuiltIn)
        {
            var index = result.FindIndex(source => string.Equals(source.Id, builtIn.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) result.Add(builtIn);
            else result[index] = result[index] with
            {
                Name = builtIn.Name,
                SearchUrl = builtIn.SearchUrl,
                DirectUrl = builtIn.DirectUrl,
                BuiltIn = true,
            };
        }
        return Normalize(result);
    }

    public static IReadOnlyList<BookSource> Normalize(IEnumerable<BookSource> sources) =>
        sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Id) && !string.IsNullOrWhiteSpace(source.Name))
            .Select(source => source with { Id = source.Id.Trim(), Name = source.Name.Trim() })
            .GroupBy(source => source.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(source => source.Priority)
            .ThenBy(source => source.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    /// <summary>Renumbers priorities to 10, 20, 30 … so the up/down buttons always have room.</summary>
    public static IReadOnlyList<BookSource> Renumber(IEnumerable<BookSource> sources) =>
        Normalize(sources).Select((source, index) => source with { Priority = (index + 1) * 10 }).ToArray();

    /// <summary>The next free id for a user-added source, derived from the host so it stays readable.</summary>
    public static string CustomId(string searchUrl)
    {
        var host = Uri.TryCreate(searchUrl.Replace("{q}", "x", StringComparison.Ordinal), UriKind.Absolute, out var uri)
            ? uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase)
            : "custom";
        return "custom:" + host;
    }
}

/// <summary>
/// Persists the user's source list next to the other EasyPub settings. Writes are atomic, so a crash
/// mid-save cannot leave a half-written list behind.
/// </summary>
public sealed class BookSourceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public BookSourceStore(string storagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        StoragePath = Path.GetFullPath(storagePath);
    }

    public string StoragePath { get; }

    public static BookSourceStore CreateDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("EASYPUB_BOOK_SOURCES_PATH");
        return new BookSourceStore(string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPub Modern",
                "book-sources.json")
            : overridePath);
    }

    public async Task<IReadOnlyList<BookSource>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(StoragePath)) return AdoptLegacyCookie(BookSourceCatalog.Merge(null));
            try
            {
                await using var stream = File.OpenRead(StoragePath);
                var saved = await JsonSerializer.DeserializeAsync<BookSource[]>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                return AdoptLegacyCookie(BookSourceCatalog.Merge(saved));
            }
            catch (JsonException)
            {
                return AdoptLegacyCookie(BookSourceCatalog.Merge(null));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The Qidian cookie used to live in a file of its own. Fold it into the source list once, so a
    /// session the user already pasted keeps working instead of being asked for again.
    /// </summary>
    private static IReadOnlyList<BookSource> AdoptLegacyCookie(IReadOnlyList<BookSource> sources)
    {
        if (sources.Any(source => !string.IsNullOrWhiteSpace(source.Cookie))) return sources;
        if (LegacyQidianCookie() is not { Length: > 0 } legacy) return sources;
        var list = sources.ToList();
        var index = list.FindIndex(source => string.Equals(source.Id, "qidian", StringComparison.OrdinalIgnoreCase));
        if (index < 0) return sources;
        list[index] = list[index] with { Cookie = legacy };
        return list;
    }

    /// <summary>Reads the pre-source-list cookie file, if the user ever created one.</summary>
    public static string? LegacyQidianCookie()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EasyPub Modern",
            "qidian.cookie");
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task SaveAsync(IEnumerable<BookSource> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var normalized = BookSourceCatalog.Normalize(sources);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var parent = Path.GetDirectoryName(StoragePath)!;
            Directory.CreateDirectory(parent);
            var temporaryPath = Path.Combine(parent, $".{Path.GetFileName(StoragePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                                 FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken)
                        .ConfigureAwait(false);
                }
                File.Move(temporaryPath, StoragePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>Outcome of one availability check, phrased so the UI can explain it without jargon.</summary>
public sealed record BookSourceProbe(
    string Id,
    string Name,
    bool Reachable,
    bool Usable,
    int Milliseconds,
    string Detail,
    string SampleTitle = "")
{
    /// <summary>Short verdict for the status column.</summary>
    public string Status => (Reachable, Usable) switch
    {
        (false, _) => "不可用",
        (true, true) => "可用",
        _ => "受限",
    };
}
