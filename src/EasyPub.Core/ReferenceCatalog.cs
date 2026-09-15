using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

public enum ReferenceNodeKind { Volume, Chapter }

/// <summary>
/// One directory node. A volume comes from a structural heading (no URL); a chapter comes from a link
/// and records the volume it belongs to. Volume ownership is what disambiguates repeated chapter numbers.
/// </summary>
public sealed record ReferenceNode(string Title, ReferenceNodeKind Kind, string? Url, string? VolumeTitle)
{
    public bool IsVolume => Kind == ReferenceNodeKind.Volume;
}

/// <summary>Public directory metadata only. No cookies, credentials or chapter-body downloads.</summary>
public sealed record ReferenceCatalog(string Source, string PageTitle, IReadOnlyList<ReferenceNode> Nodes)
{
    /// <summary>Flat chapter titles in catalog order; volumes excluded. Kept for existing matching callers.</summary>
    public IReadOnlyList<string> Titles => Nodes.Where(n => n.Kind == ReferenceNodeKind.Chapter).Select(n => n.Title).ToArray();

    /// <summary>Volume headings in catalog order.</summary>
    public IReadOnlyList<string> VolumeTitles => Nodes.Where(n => n.IsVolume).Select(n => n.Title).ToArray();

    /// <summary>Chapters belonging to one volume, in catalog order.</summary>
    public IReadOnlyList<ReferenceNode> ChaptersOf(string volumeTitle) =>
        Nodes.Where(n => n.Kind == ReferenceNodeKind.Chapter && n.VolumeTitle == volumeTitle).ToArray();
}

/// <summary>
/// Reads public book directories from any site. Two structure-agnostic strategies are tried in order:
/// a definition list (&lt;dl&gt;&lt;dt&gt;&lt;dd&gt;, the dominant layout of Chinese novel sites, which also
/// carries volumes), then the largest group of same-prefix links. No login, no cookies, no chapter
/// bodies, and never a request to a private, loopback or link-local address.
/// </summary>
public sealed class ReferenceCatalogClient
{
    private static readonly Regex Anchor = new("<a\\b[^>]*href\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>(?<title>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex Node = new("<dt\\b[^>]*>(?<volume>.*?)</dt>|<dd\\b[^>]*>(?<chapter>.*?)</dd>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex DlBlock = new("<dl\\b[^>]*>(?<body>.*?)</dl>", RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex TitleTag = new("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    /// <summary>
    /// Built-in directory search entry points. Each returns a search result page; the book link is
    /// resolved and followed to its table of contents. Sources are independent, so one site being
    /// down or rate-limited does not stop the others.
    /// </summary>
    /// <summary>Fewer chapters than this from a search hit means the page was not a real directory.</summary>
    /// <summary>
    /// Optional session cookie for sites that gate their directory behind a login (Qidian). The user
    /// supplies it in settings; the catalog client never writes it to disk.
    /// </summary>
    public static string? SessionCookie { get; set; }
    private const int MinimumChapters = 20;

    /// <summary>
    /// How many sources may be queried at once. High enough that one slow site no longer decides the
    /// total wait, low enough that a single lookup never looks like a flood to the sites being asked.
    /// </summary>
    private const int MaxConcurrentSources = 4;

    /// <summary>
    /// How long a book-name search may take before that source is left out of this lookup. Every
    /// healthy source answers a search in well under a second; measured over the built-in set, the
    /// slowest one took 6.2 s and never contributed a directory that was actually chosen. With
    /// <see cref="MaxConcurrentSources"/> slots, one source like that holds a quarter of the pool for
    /// the whole lookup, which is what makes directory discovery feel slow. A source that misses this
    /// budget is skipped silently, exactly like a source that fails outright.
    /// </summary>
    private const int SearchTimeoutSeconds = 6;

    /// <summary>Budget for reading a directory page, which is a larger response than a search page.</summary>
    private const int FetchTimeoutSeconds = 15;

    /// <summary>
    /// Wall-clock budget for one source's whole contribution to a lookup, search and every directory
    /// read combined. Measured on a real book, a single site spent 27.8 s trying to answer and returned
    /// nothing at all, while every source that did contribute finished inside 8 s. The budget bounds
    /// that tail without cutting off a site that is merely slow but working.
    /// </summary>
    private const int SourceBudgetMs = 15000;

    /// <summary>
    /// How many chapters a directory needs before that source counts as answered. Three sources each
    /// return one link per page, so the first is normally the best and the remaining rounds exist for
    /// the cases where it is not. Measured on a real book, one site returned a 431-chapter teaser
    /// beside the real 1408-chapter directory, so "answered" has to mean a directory large enough to
    /// be the book rather than merely non-empty. Chapters keep accumulating until the budget runs out,
    /// so which source wins the comparison is unaffected.
    /// </summary>
    private const int AnsweredChapters = 50;

    /// <summary>
    /// The arranged source list. Defaults to the built-in order (Qidian and Fanqie lead, because their
    /// directories carry the publisher's own volume split). The source dialog replaces this and
    /// <see cref="BookSourceStore"/> persists it, so an ordering the user chose survives a restart.
    /// </summary>
    public static IReadOnlyList<BookSource> Sources { get; set; } = BookSourceCatalog.Merge(null);
    private static readonly string[] IgnoredTitles =
    [
        "在线阅读", "开始阅读", "免费阅读", "最新章节", "首页", "上一页", "下一页", "末页", "尾页",
        "登录", "注册", "书架", "排行", "分类", "返回目录", "加入书签", "投推荐票", "章节目录", "目录",
        "电脑版", "手机版", "上一章", "下一章", "查看全部", "更多",
    ];
    private static string Text(string html) => WebUtility.HtmlDecode(Tags.Replace(html, "")).Trim();

    /// <summary>
    /// A &lt;dt&gt; is a real volume heading only when it is neither site boilerplate nor one of the
    /// "《书名》章节列表" wrappers novel sites use as a list caption rather than a volume name.
    /// </summary>
    private static bool IsVolumeHeading(string heading) =>
        heading.Length is >= 1 and <= 100
        && !IgnoredTitles.Contains(heading)
        && !heading.Contains("章节列表", StringComparison.Ordinal)
        && !heading.Contains("最新章节", StringComparison.Ordinal)
        && !heading.Contains("提示：", StringComparison.Ordinal)
        && !heading.Contains("登录", StringComparison.Ordinal);

    /// <summary>
    /// Any public HTTP or HTTPS address on the default port without embedded credentials. Many novel
    /// sites are HTTP-only, so it is accepted; private, loopback, link-local and site-local targets are
    /// always refused so a pasted URL cannot be used to probe the local network.
    /// </summary>
    public static bool Supported(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (!uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        return IsPublicHost(uri.Host);
    }

    /// <summary>True when the address is not encrypted, so the caller can say so in the UI.</summary>
    public static bool IsPlainText(Uri uri) => uri.Scheme == Uri.UriSchemeHttp;

    private static bool IsPublicHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var name = host.Trim('[', ']').TrimEnd('.');
        if (name.Equals("localhost", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IPAddress.TryParse(name, out var address)) return true;
        if (address.IsIPv4MappedToIPv6) return IsPublicHost(address.MapToIPv4().ToString());
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                >= 224 => false,
                100 when bytes[1] >= 64 && bytes[1] <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] >= 16 && bytes[1] <= 31 => false,
                192 when bytes[1] == 168 => false,
                _ => true,
            };
        return !address.Equals(IPAddress.IPv6Any) && bytes[0] != 0xFF && (bytes[0] & 0xFE) != 0xFC;   // fc00::/7 unique-local
    }

    public static ReferenceCatalog Parse(Uri source, string html)
    {
        if (!Supported(source)) throw new InvalidOperationException("仅支持公开的 HTTP/HTTPS 目录页；不接受内网、回环地址或带凭据的网址。");
        var pageTitle = Text(TitleTag.Match(html).Groups[1].Value);
        var links = new HashSet<string>(StringComparer.Ordinal);
        // Qidian renders volumes and chapters in dedicated markup; elsewhere the structure-agnostic
        // strategies apply. All paths feed the same alignment engine.
        var nodes = source.Host.Contains("qidian") ? ParseQidian(source, html, links)
            : source.Host.Contains("fanqienovel") ? ParseFanqie(source, html, links)
            : ParseDefinitionList(source, html, links);
        if (nodes.Count(n => n.Kind == ReferenceNodeKind.Chapter) < 3)
        {
            links.Clear();
            nodes = ParseGeneric(source, html, links);
        }
        if (nodes.Count(n => n.Kind == ReferenceNodeKind.Chapter) < 3)
            throw new InvalidOperationException("未取得有效目录，可能需要登录、网页验证或站点结构特殊。请使用备用导入，不会自动绕过验证。");
        return new(source.AbsoluteUri, pageTitle, nodes);
    }

    /// <summary>
    /// A &lt;dl&gt; holding &lt;dt&gt; volume headings and &lt;dd&gt; chapter links is the dominant layout across
    /// Chinese novel sites, so it is detected structurally rather than by host name. The list with the
    /// most chapter entries wins; this is what recovers volume structure a flat link scan cannot see.
    /// </summary>
private static readonly Regex QidianVolume = new(
        "<div\\b[^>]*class=\"[^\"]*catalog-volume[^\"]*\"[^>]*>(?<body>.*?)</ul>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    private static readonly Regex QidianVolumeName = new(
        "<h3\\b[^>]*class=\"[^\"]*volume-name[^\"]*\"[^>]*>(?<name>.*?)</h3>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Qidian: each catalog-volume block carries one volume heading and its chapter links. The heading
    /// text is truncated at the first nested tag, which is where the site appends "·共53章 免费".
    /// </summary>
    private static List<ReferenceNode> ParseQidian(Uri source, string html, HashSet<string> links)
    {
        var nodes = new List<ReferenceNode>();
        foreach (Match block in QidianVolume.Matches(html))
        {
            var body = block.Groups["body"].Value;
            var raw = QidianVolumeName.Match(body).Groups["name"].Value;
            var cut = raw.IndexOf("<span", StringComparison.OrdinalIgnoreCase);
            var heading = Text(cut > 0 ? raw[..cut] : raw);
            if (IsVolumeHeading(heading)) nodes.Add(new(heading, ReferenceNodeKind.Volume, null, heading));
            var volume = IsVolumeHeading(heading) ? heading : null;
            foreach (Match anchor in Anchor.Matches(body))
                AddChapter(source, anchor, volume, nodes, links);
        }
        return nodes;
    }

    private static readonly Regex FanqieVolume = new(
        "<div\\b[^>]*class=\"[^\"]*\\bvolume\\b[^\"]*\"[^>]*>(?<name>.*?)</div>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    private static readonly Regex FanqieChapter = new(
        "<a\\b[^>]*chapter-item-title[^>]*>(?<title>.*?)</a>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
    private static readonly Regex HrefAttribute = new(
        "href\\s*=\\s*[\"'](?<url>[^\"']+)[\"']",
        RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Fanqie renders the whole table of contents server-side in one container: a volume banner
    /// ("第一卷：…" followed by "共91章") and then that volume's chapter anchors. Volumes and chapters
    /// are collected separately and replayed in document order, so a chapter stays under the volume it
    /// was printed in. The banner text is cut at the first nested tag, which is where "共91章" starts.
    /// </summary>
    private static List<ReferenceNode> ParseFanqie(Uri source, string html, HashSet<string> links)
    {
        var nodes = new List<ReferenceNode>();
        var start = html.IndexOf("page-directory-content", StringComparison.OrdinalIgnoreCase);
        var body = start < 0 ? html : html[start..];
        var volumes = FanqieVolume.Matches(body).Cast<Match>()
            .Select(match => (Index: match.Index, Banner: (Match?)match, Chapter: (Match?)null));
        var chapters = FanqieChapter.Matches(body).Cast<Match>()
            .Select(match => (Index: match.Index, Banner: (Match?)null, Chapter: (Match?)match));
        var events = volumes.Concat(chapters).OrderBy(item => item.Index);
        string? volume = null;
        foreach (var (_, banner, chapter) in events)
        {
            if (banner is not null)
            {
                var raw = banner.Groups["name"].Value;
                var cut = raw.IndexOf("<span", StringComparison.OrdinalIgnoreCase);
                var heading = Text(cut > 0 ? raw[..cut] : raw);
                if (IsVolumeHeading(heading))
                {
                    volume = heading;
                    nodes.Add(new(heading, ReferenceNodeKind.Volume, null, heading));
                }
                continue;
            }
            var tag = chapter!.Value;
            if (!Uri.TryCreate(source, WebUtility.HtmlDecode(HrefAttribute.Match(tag).Groups["url"].Value), out var link)) continue;
            if (!string.Equals(link.Host, source.Host, StringComparison.OrdinalIgnoreCase) || link.AbsolutePath.Length <= 1) continue;
            var title = Text(chapter.Groups["title"].Value);
            if (title.Length is < 2 or > 100 || IgnoredTitles.Contains(title)) continue;
            if (links.Add(link.AbsoluteUri))
                nodes.Add(new(title, ReferenceNodeKind.Chapter, link.AbsoluteUri, volume));
        }
        return nodes;
    }

    private static List<ReferenceNode> ParseDefinitionList(Uri source, string html, HashSet<string> links)
    {
        Match? best = null;
        var bestCount = 0;
        foreach (Match block in DlBlock.Matches(html))
        {
            var count = Node.Matches(block.Groups["body"].Value).Count(node => node.Groups["chapter"].Success);
            if (count > bestCount) { bestCount = count; best = block; }
        }
        var nodes = new List<ReferenceNode>();
        if (best is null) return nodes;
        string? volume = null;
        foreach (Match node in Node.Matches(best.Groups["body"].Value))
        {
            if (node.Groups["volume"].Success)
            {
                var heading = Text(node.Groups["volume"].Value);
                if (IsVolumeHeading(heading))
                {
                    volume = heading;
                    nodes.Add(new(heading, ReferenceNodeKind.Volume, null, heading));
                }
                continue;
            }
            AddChapter(source, Anchor.Match(node.Groups["chapter"].Value), volume, nodes, links);
        }
        return nodes;
    }

    /// <summary>
    /// Groups the page's links by the URL prefix they share and keeps the largest group — that group is
    /// the chapter list. Prefix grouping is what separates "next chapter / home / login" navigation
    /// from the actual table of contents, and it works without knowing anything about the site.
    /// </summary>
    private static List<ReferenceNode> ParseGeneric(Uri source, string html, HashSet<string> links)
    {
        var nodes = new List<ReferenceNode>();
        var candidates = Anchor.Matches(html).Cast<Match>()
            .Select(match => (Match: match, Link: Resolve(source, match)))
            .Where(pair => pair.Link is not null)
            .Select(pair => (Link: pair.Link!, Title: Text(pair.Match.Groups["title"].Value)))
            .Where(pair => pair.Title.Length is >= 2 and <= 100 && !IgnoredTitles.Contains(pair.Title))
            .ToArray();
        if (candidates.Length == 0) return nodes;
        foreach (var (link, title) in candidates
            .GroupBy(pair => PathPrefix(pair.Link))
            .OrderByDescending(group => group.Select(pair => pair.Link.AbsoluteUri).Distinct().Count())
            .First())
            if (links.Add(link.AbsoluteUri))
                nodes.Add(new(title, ReferenceNodeKind.Chapter, link.AbsoluteUri, null));
        return nodes;
    }

    private static Uri? Resolve(Uri source, Match anchor)
    {
        if (!Uri.TryCreate(source, WebUtility.HtmlDecode(anchor.Groups["url"].Value), out var link)) return null;
        if (!string.Equals(link.Host, source.Host, StringComparison.OrdinalIgnoreCase)) return null;
        // A trailing slash must NOT disqualify a link: Qidian chapter URLs end with one
        // (/chapter/107580/20901221/). Landing pages are filtered by the chapter-count threshold.
        if (link.AbsolutePath.Length <= 1) return null;
        return link;
    }

    /// <summary>Directory part of the path, used to group chapter links apart from site navigation.</summary>
    private static string PathPrefix(Uri uri)
    {
        var path = uri.AbsolutePath;
        var index = path.LastIndexOf('/');
        return index <= 0 ? path : path[..index];
    }

    private static void AddChapter(Uri source, Match anchor, string? volume, List<ReferenceNode> nodes, HashSet<string> links)
    {
        if (!anchor.Success) return;
        if (Resolve(source, anchor) is not { } link) return;
        var title = Text(anchor.Groups["title"].Value);
        if (title.Length is < 2 or > 100 || IgnoredTitles.Contains(title)) return;
        if (links.Add(link.AbsoluteUri))
            nodes.Add(new(title, ReferenceNodeKind.Chapter, link.AbsoluteUri, volume));
    }

    internal static string? CookieFor(Uri uri, string? cookie = null, string? originHost = null)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        var host=uri.IdnHost.TrimEnd('.');
        if (originHost is not null && !host.Equals(originHost.TrimEnd('.'),StringComparison.OrdinalIgnoreCase)) return null;
        if (!string.IsNullOrWhiteSpace(cookie)) return cookie;
        var configured=Sources.FirstOrDefault(s=>
            (Uri.TryCreate(s.SearchUrl.Replace("{q}","book"),UriKind.Absolute,out var search) && search.IdnHost.Equals(host,StringComparison.OrdinalIgnoreCase))
            || (Uri.TryCreate(s.DirectUrl?.Replace("{q}","book"),UriKind.Absolute,out var direct) && direct.IdnHost.Equals(host,StringComparison.OrdinalIgnoreCase)));
        if (!string.IsNullOrWhiteSpace(configured?.Cookie)) return configured.Cookie;
        return host.Equals("qidian.com",StringComparison.OrdinalIgnoreCase) || host.EndsWith(".qidian.com",StringComparison.OrdinalIgnoreCase) ? SessionCookie : null;
    }

    private static async Task<string> Get(Uri uri, CancellationToken token, string? cookie = null, string? originHost = null,
        int timeoutSeconds = FetchTimeoutSeconds)
    {
        if (!Supported(uri)) throw new InvalidOperationException("目录网址不是允许的公开地址。");
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, cancellation) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
                if (addresses.Length == 0 || addresses.Any(address => !IsPublicHost(address.ToString())))
                    throw new HttpRequestException("目录域名解析到了非公开地址，已停止连接。");
                Exception? last = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or IOException) { socket.Dispose(); last = ex; }
                    catch { socket.Dispose(); throw; }
                }
                throw new HttpRequestException("无法连接目录站点。", last);
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds), MaxResponseContentBufferSize = 6 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
        // A per-source cookie wins over the shared one, so two sources that both need a login cannot
        // overwrite each other's session.
        var effective = CookieFor(uri,cookie,originHost);
        if (!string.IsNullOrWhiteSpace(effective)) client.DefaultRequestHeaders.Add("Cookie", effective);
        using var response = await client.GetAsync(uri, token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(token);
    }

    public async Task<ReferenceCatalog> FetchAsync(string url, CancellationToken token = default, string? cookie = null)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !Supported(uri))
            throw new InvalidOperationException("请填写公开的 HTTP/HTTPS 书籍目录页网址；内网、回环地址与带凭据的网址不被接受。");
        var html = await Get(uri, token, cookie);
        var direct = TryParse(uri, html);
        // A book page usually lists only "最新章节", which parses fine but is incomplete. When the page
        // offers a directory entry, take whichever source yields more chapters — silently returning a
        // 42-chapter "latest updates" list instead of a 307-chapter table of contents would be worse
        // than failing.
        var best = direct;
        foreach (var candidate in DirectoryCandidates(uri, html).Take(2))
        {
            try
            {
                var parsed = Parse(candidate, await Get(candidate, token, cookie, uri.IdnHost));
                if (best is null || parsed.Titles.Count > best.Titles.Count) best = parsed;
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !token.IsCancellationRequested) { }
        }
        if (best is not null) return best;
        // Qidian hides its volume structure behind a login. Reporting "structure not recognised"
        // there sends the user hunting for a parsing bug instead of filling in the cookie they
        // already have, so say what is actually missing.
        if (uri.Host.Contains("qidian", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(cookie)
            && string.IsNullOrWhiteSpace(SessionCookie))
            throw new InvalidOperationException("起点需要登录才能显示完整分卷目录。请打开「书源…」，把起点 Cookie 填到「起点中文网」这一行，之后这里就能直接读出分卷。");
        throw new InvalidOperationException("未取得有效目录，可能需要登录、网页验证或站点结构特殊。请使用备用导入，不会自动绕过验证。");
    }

    private static ReferenceCatalog? TryParse(Uri uri, string html)
    {
        try { return Parse(uri, html); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Candidate table-of-contents URLs for a book overview page, best first: an explicit "目录" link,
    /// then the sibling index.html of the first chapter link. Every candidate is tried and the richest
    /// result wins, because a site's "目录" navigation link is sometimes a sort page, not the list.
    /// </summary>
    private static IEnumerable<Uri> DirectoryCandidates(Uri source, string html)
    {
        var links = Anchor.Matches(html).Cast<Match>()
            .Select(match => (Match: match, Link: Resolve(source, match)))
            .Where(pair => pair.Link is not null)
            .Select(pair => (Link: pair.Link!, Title: Text(pair.Match.Groups["title"].Value)))
            .Where(pair => !string.Equals(pair.Link.AbsolutePath, source.AbsolutePath, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The prefix shared by the most chapter-like links is the book's own directory. This is the
        // reliable signal: navigation links each sit at their own prefix, so they never form a group.
        foreach (var guess in links
            .GroupBy(pair => PathPrefix(pair.Link))
            .Where(group => group.Key.Length > 0 && group.Count() >= 3)
            .OrderByDescending(group => group.Count())
            .Select(group => new Uri(source, group.Key + "/index.html"))
            .Where(uri => !string.Equals(uri.AbsolutePath, source.AbsolutePath, StringComparison.OrdinalIgnoreCase)))
            if (seen.Add(guess.AbsoluteUri)) yield return guess;
        foreach (var (link, title) in links)
            if ((title.Contains("目录", StringComparison.Ordinal) || title.Contains("章节列表", StringComparison.Ordinal))
                && seen.Add(link.AbsoluteUri))
                yield return link;
        foreach (var (link, _) in links.Take(80))
        {
            if (!link.AbsolutePath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) continue;
            if (link.AbsolutePath.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase)) continue;
            var guess = new Uri(source, PathPrefix(link) + "/index.html");
            if (string.Equals(guess.AbsolutePath, source.AbsolutePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (seen.Add(guess.AbsoluteUri)) yield return guess;
        }
    }

    /// <summary>One source's contribution to a lookup, so a slow total can be attributed to a site.</summary>
    public sealed record SourceTiming(string Name, long Milliseconds, int Catalogs, int SkippedCandidates = 0)
    {
        public string State => Catalogs > 0 ? $"{Catalogs} 个目录"
            : Catalogs == 0 ? "无可用目录"
            : Catalogs == -2 ? "超时未完成"
            : "查询失败";
    }

    /// <summary>
    /// Per-source cost of the most recent <see cref="DiscoverAsync"/>. Directory discovery is the one
    /// part of a repair that waits on the network, and "it is slow" is not actionable without knowing
    /// which site is responsible.
    /// </summary>
    public IReadOnlyList<SourceTiming> LastTimings { get; private set; } = [];

    /// <summary>
    /// Book-name lookup across the user's enabled sources, in the arranged order. Each source is asked
    /// independently, so one site being down or rate-limited never stops the others.
    /// </summary>
    /// <param name="preferred">
    /// Source ids to consult first, in order. A folder metadata rule supplies this, so a folder that
    /// only ever holds books from one site says so once instead of on every book.
    /// </param>
    public async Task<IReadOnlyList<ReferenceCatalog>> DiscoverAsync(
        string book,
        CancellationToken token = default,
        IReadOnlyList<string>? preferred = null,
        bool refresh = false)
    {
        if (book.Trim().Length < 2) throw new InvalidOperationException("请填写书名，可加作者避免同名书。");
        // A pasted book address is the strongest statement of intent there is, so follow it at once.
        if (Uri.TryCreate(book.Trim(), UriKind.Absolute, out var pasted) && Supported(pasted))
            return [await FetchAsync(pasted.AbsoluteUri, token)];
        var name = Regex.Replace(book.Trim(), @"\s*[（(].*?[）)]", "").Trim();
        // Repairs are iterative: the same book gets fixed, checked, adjusted and fixed again, and each
        // pass used to ask every site from scratch — measured at 98% of the wall clock. A recent answer
        // is reused instead. Clearing the cache or waiting out its lifetime asks the sources again.
        if (!refresh && CatalogCache.TryRead(name, preferred, out var remembered))
        {
            LastTimings = [];
            return remembered;
        }
        var ordered = Arrange(Sources.Where(source => source.Enabled), preferred).ToArray();
        var searchable = ordered.Count(source => source.Searchable || (source.Direct && LooksLikeIdentifier(name)));

        // Every source is asked at once. One after another they cost the sum of their round trips —
        // six sources measured 4.6 s on a real book — while concurrently the cost is the slowest
        // single reply. A gate keeps the burst from looking like a flood, and each source keeps its
        // own slot so the results still come back in the user's configured order.
        using var gate = new SemaphoreSlim(MaxConcurrentSources);
        var slots = new List<ReferenceCatalog>?[ordered.Length];
        var timings = new SourceTiming[ordered.Length];
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Task.WhenAll(ordered.Select(async (source, index) =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            try
            {
                var found = new List<ReferenceCatalog>();
                var urls = await LocateAsync(source, name, token).ConfigureAwait(false);
                var attempted = 0;
                var budgetSpent = false;
                foreach (var url in urls)
                {
                    token.ThrowIfCancellationRequested();
                    // Stop spending on this source once its budget is gone: the call already in flight
                    // is allowed to finish, later candidates are not started. What was found so far is
                    // still reported, so a source is never silently reduced to nothing.
                    if (watch.ElapsedMilliseconds > SourceBudgetMs) { budgetSpent = true; break; }
                    attempted++;
                    try
                    {
                        var parsed = await FetchAsync(url, token, source.Cookie).ConfigureAwait(false);
                        // A search hit that yields only a handful of links is a landing page or a
                        // teaser, not a table of contents. Accepting it would rewrite the book from
                        // four chapters, which is far worse than reporting no directory at all.
                        if (parsed.Titles.Count >= MinimumChapters) found.Add(parsed);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !requestCancelled(token)) { }
                    // Enough to stop looking: a later search hit on the same site is a smaller or
                    // repeated view of the same book.
                    if (found.Count > 0 && found.Max(catalog => catalog.Titles.Count) >= AnsweredChapters)
                        break;
                }
                slots[index] = found;
                timings[index] = new(source.Name, watch.ElapsedMilliseconds, found.Count,
                    budgetSpent ? urls.Count - attempted : 0);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException && !requestCancelled(token))
            {
                failures.Add($"{source.Name}: {ex.Message}");
                timings[index] = new(source.Name, watch.ElapsedMilliseconds, -1);
            }
            finally { gate.Release(); watch.Stop(); }
        })).ConfigureAwait(false);
        LastTimings = timings.Where(timing => timing is not null)
            .OrderByDescending(timing => timing.Milliseconds).ToArray();

        var result = slots.Where(slot => slot is not null).SelectMany(slot => slot!).ToList();
        // Only a total failure is reported as an error; otherwise return whatever was found.
        if (result.Count == 0 && searchable > 0 && failures.Count == searchable)
            throw new InvalidOperationException($"全部目录源查询失败（{string.Join("；", failures)}），可能被限流或网络不通，请稍后重试");
        CatalogCache.Write(name, preferred, result);
        return result;
    }

    /// <summary>
    /// Candidate book pages for one source: the search hits, or the direct address when the source has
    /// no keyword search but does accept an id (Fanqie).
    /// </summary>
    private async Task<List<string>> LocateAsync(BookSource source, string name, CancellationToken token)
    {
        if (source.Searchable)
        {
            var origin = BuildSearchUri(source, name);
            // Search pages are small and quick; a source that cannot answer one within the budget is
            // skipped rather than allowed to hold a concurrency slot for the whole lookup.
            var html = await Get(origin, token, source.Cookie, timeoutSeconds: SearchTimeoutSeconds);
            return Anchor.Matches(html).Cast<Match>()
                .Select(match => (Title: Text(match.Groups["title"].Value), Link: SearchResult(origin, match)))
                .Where(pair => pair.Link is not null && pair.Title.Length >= 2
                    && pair.Title.Contains(name, StringComparison.Ordinal))
                .Select(pair => pair.Link!.AbsoluteUri)
                .Distinct().Take(3).ToList();
        }
        if (source.Direct && LooksLikeIdentifier(name))
            return [new Uri(source.DirectUrl!.Replace("{q}", Uri.EscapeDataString(name), StringComparison.Ordinal)).AbsoluteUri];
        return [];
    }

    /// <summary>
    /// Address of a source's own search page. The template is the only site-specific part of a
    /// user-added source, so supporting a new site never needs a code change.
    /// </summary>
    public static Uri BuildSearchUri(BookSource source, string query)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Searchable) throw new InvalidOperationException($"{source.Name} 不支持按书名搜索。");
        var address = source.SearchUrl.Replace("{q}", Uri.EscapeDataString(query), StringComparison.Ordinal);
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !Supported(uri))
            throw new InvalidOperationException($"{source.Name} 的搜索地址无效：{source.SearchUrl}");
        return uri;
    }

    /// <summary>Preferred ids first in the order given, then everything else by its configured priority.</summary>
    public static IEnumerable<BookSource> Arrange(IEnumerable<BookSource> sources, IReadOnlyList<string>? preferred)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.ToArray();
        if (preferred is null || preferred.Count == 0) return list.OrderBy(source => source.Priority);
        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < preferred.Count; index++) rank.TryAdd(preferred[index], index);
        return list
            .OrderBy(source => rank.TryGetValue(source.Id, out var index) ? index : int.MaxValue)
            .ThenBy(source => source.Priority);
    }

    /// <summary>A bare id (a Fanqie book number, a Qidian book id) rather than a book name.</summary>
    public static bool LooksLikeIdentifier(string value) =>
        value.Length >= 5 && value.All(char.IsAsciiDigit);

    /// <summary>
    /// Checks one source the way the user would: runs a real search for a book name they care about and
    /// reports what came back. A source only counts as usable when it returned a link that looks like
    /// that book, so "reachable but answers nothing" is never displayed as healthy.
    /// </summary>
    public async Task<BookSourceProbe> ProbeAsync(BookSource source, string sampleBook, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var name = Regex.Replace(sampleBook.Trim(), @"\s*[（(].*?[）)]", "").Trim();
        if (name.Length < 2) name = "斗破苍穹";
        var watch = Stopwatch.StartNew();
        try
        {
            if (source.Searchable)
            {
                var origin = BuildSearchUri(source, name);
                // Same budget the real lookup uses, so "available" in the dialog cannot mean a source
                // that would actually be skipped the moment a repair ran.
                var html = await Get(origin, token, source.Cookie, timeoutSeconds: SearchTimeoutSeconds);
                watch.Stop();
                var hit = Anchor.Matches(html).Cast<Match>()
                    .Select(match => (Title: Text(match.Groups["title"].Value), Link: SearchResult(origin, match)))
                    .FirstOrDefault(pair => pair.Link is not null && pair.Title.Length >= 2
                        && pair.Title.Contains(name, StringComparison.Ordinal));
                return new BookSourceProbe(
                    source.Id, source.Name, true, hit.Link is not null, (int)watch.ElapsedMilliseconds,
                    hit.Link is not null
                        ? $"搜索「{name}」已命中，可继续读取它的目录。"
                        // Qidian answers an anonymous search with an empty 202 page: the site is up and
                        // the parser is fine, so "no matching book" would send the user hunting for a
                        // bug instead of pasting the cookie that actually fixes it.
                        : string.Equals(source.Id, "qidian", StringComparison.OrdinalIgnoreCase)
                          && string.IsNullOrWhiteSpace(source.Cookie) && string.IsNullOrWhiteSpace(SessionCookie)
                            ? "起点对未登录的搜索返回空页面（反爬拦截）。在「书源…」里把浏览器 Cookie 填到「起点中文网」这一行后即可搜索。"
                            : $"站点可访问，但搜索「{name}」没有返回匹配书籍；可能是书名不同、站点改版，或该站确实没有这本书。",
                    hit.Title ?? "");
            }
            if (source.Direct)
            {
                var home = new Uri(new Uri(source.DirectUrl!.Replace("{q}", "0", StringComparison.Ordinal)), "/");
                await Get(home, token, source.Cookie);
                watch.Stop();
                return new BookSourceProbe(source.Id, source.Name, true, true, (int)watch.ElapsedMilliseconds,
                    "该源不提供书名搜索，但可以直接读取书籍网址或编号：把网址粘贴到「书籍网址」即可。");
            }
            watch.Stop();
            return new BookSourceProbe(source.Id, source.Name, false, false, 0, "该源还没有填写搜索地址。");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or UriFormatException)
        {
            watch.Stop();
            return new BookSourceProbe(source.Id, source.Name, false, false, (int)watch.ElapsedMilliseconds,
                "连接失败：" + ex.Message);
        }
    }

    /// <summary>A same-host, public HTTPS/HTTP link taken from a search result page.</summary>
    private static Uri? SearchResult(Uri origin, Match anchor)
    {
        if (!Uri.TryCreate(origin, WebUtility.HtmlDecode(anchor.Groups["url"].Value), out var link)) return null;
        if (!string.Equals(link.Host, origin.Host, StringComparison.OrdinalIgnoreCase)) return null;
        return Supported(link) ? link : null;
    }

    private static bool requestCancelled(CancellationToken token) => token.IsCancellationRequested;
}
