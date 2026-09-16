using System.Globalization;
using System.Net;
using System.Text.Json;

namespace EasyPub.Core;

/// <summary>发布页上的一个下载资产。</summary>
public sealed record UpdateAsset(string Name, long Size, string DownloadUrl);

/// <summary>一次 GitHub Release。</summary>
public sealed record UpdateRelease(
    string Tag,
    Version Version,
    string Title,
    string Notes,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<UpdateAsset> Assets)
{
    /// <summary>便携包的文件名后缀。自更新只认这一种资产。</summary>
    public const string PortablePackageSuffix = "-win-x64.zip";

    /// <summary>用于覆盖式更新的便携包；为 null 时只能引导用户去发布页手动下载。</summary>
    public UpdateAsset? PortablePackage =>
        Assets.FirstOrDefault(asset => asset.Name.EndsWith(PortablePackageSuffix, StringComparison.OrdinalIgnoreCase));
}

public enum UpdateCheckStatus
{
    /// <summary>已经是最新版本。</summary>
    UpToDate,

    /// <summary>存在更新的版本，<see cref="UpdateCheckResult.Release"/> 一定有值。</summary>
    Available,

    /// <summary>网络、限流或响应无法识别；<see cref="UpdateCheckResult.Message"/> 说明原因。</summary>
    Failed,
}

/// <summary>一个可供检查与下载的发布源。</summary>
/// <param name="Name">界面上显示的名字。</param>
/// <param name="LatestReleaseApi">返回最新发布信息的接口地址。</param>
/// <param name="ReleasesPage">让用户手动下载的发布页地址。</param>
public sealed record UpdateSource(string Name, string LatestReleaseApi, string ReleasesPage);

/// <summary>
/// 可用的发布源，按顺序尝试。
/// GitHub 是主源；国内直连 GitHub 不稳定时，AtomGit 上的镜像仓库可以顶上——
/// 它的接口路径与 GitHub 同构，附件也允许匿名下载（实测）。
/// </summary>
public static class UpdateSources
{
    public static readonly UpdateSource GitHub = new(
        "GitHub",
        "https://api.github.com/repos/uiu8/EasyPub-Modern/releases/latest",
        "https://github.com/uiu8/EasyPub-Modern/releases");

    public static readonly UpdateSource AtomGit = new(
        "AtomGit",
        "https://atomgit.com/api/v5/repos/Wohl/EasyPub-Modern/releases/latest",
        "https://atomgit.com/Wohl/EasyPub-Modern/releases");

    /// <summary>
    /// 依次尝试，取第一个应答正常的源。
    /// 刻意不做"跨源比版本取最高"：主源能通时它就是权威，而合并两个源会掩盖
    /// "忘了往备用源同步"这件事，还会让每次检查都多花一倍时间和配额。
    /// </summary>
    public static readonly IReadOnlyList<UpdateSource> All = [GitHub, AtomGit];
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateRelease? Release, string Message, UpdateSource? Source = null)
{
    public bool HasUpdate => Status == UpdateCheckStatus.Available;
}

/// <summary>
/// 检查 GitHub 上的最新发布版本。
/// 解析刻意不依赖反射（发布是自包含单文件 + 裁剪），全部走 <see cref="JsonDocument"/>。
/// </summary>
public static class UpdateChecker
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>解析 /releases/latest 的响应体。缺 tag_name 或 tag 不是版本号都算失败。</summary>
    public static bool TryParseLatest(string? json, out UpdateRelease release)
    {
        release = null!;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("tag_name", out var tagElement)) return false;
            var tag = tagElement.GetString();
            if (!AppVersion.TryParse(tag, out var version)) return false;

            var title = ReadString(root, "name") ?? tag!;
            var notes = ReadString(root, "body") ?? string.Empty;
            // GitHub 用 published_at；AtomGit 只有 created_at，且是 "09/16/2026 15:25:30"
            // 这种本地时间格式、不带时区。它只用于展示，解析不出来就留空。
            var published = ReadTimestamp(root, "published_at") ?? ReadTimestamp(root, "created_at");
            release = new UpdateRelease(tag!, version, title, notes, published, ReadAssets(root));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>把一次响应判定成结果。纯函数，测试直接打这里。</summary>
    public static UpdateCheckResult Evaluate(string? json, Version? current, UpdateSource? source = null)
    {
        if (!TryParseLatest(json, out var release))
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "更新服务返回了无法识别的内容。", source);

        var currentText = AppVersion.Display(current);
        if (release.Version <= AppVersion.Normalize(current))
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, release, $"已是最新版本 {currentText}。", source);

        var available = AppVersion.Display(release.Version);
        return release.PortablePackage is null
            ? new UpdateCheckResult(UpdateCheckStatus.Available, release,
                $"发现新版本 {available}，但该版本没有可自动安装的便携包，请到发布页手动下载。", source)
            : new UpdateCheckResult(UpdateCheckStatus.Available, release, $"发现新版本 {available}。", source);
    }

    /// <summary>
    /// 联网检查。按 <paramref name="sources"/> 的顺序逐个尝试，第一个应答正常的源胜出；
    /// 全部失败才返回 Failed，并把最后一个源的原因说给用户听。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(
        Version? current,
        CancellationToken token = default,
        IReadOnlyList<UpdateSource>? sources = null)
    {
        var candidates = sources ?? UpdateSources.All;
        UpdateCheckResult? lastFailure = null;
        foreach (var source in candidates)
        {
            var result = await CheckOneAsync(source, current, token).ConfigureAwait(false);
            if (result.Status != UpdateCheckStatus.Failed) return result;
            lastFailure = result;
        }

        return lastFailure ?? new UpdateCheckResult(UpdateCheckStatus.Failed, null, "没有配置任何更新源。");
    }

    /// <summary>查单个源。任何网络异常都收敛成 Failed，不向上抛。</summary>
    public static async Task<UpdateCheckResult> CheckOneAsync(UpdateSource source, Version? current, CancellationToken token = default)
    {
        try
        {
            using var client = CreateClient(AppVersion.Display(current));
            using var response = await client.GetAsync(source.LatestReleaseApi, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden or (HttpStatusCode)429 => $"{source.Name} 暂时限制了请求频率，请稍后再试。",
                    HttpStatusCode.NotFound => $"{source.Name} 上还没有任何正式发布。",
                    _ => $"{source.Name} 返回 {(int)response.StatusCode}，请稍后再试。",
                };
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, hint, source);
            }

            var json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return Evaluate(json, current, source);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, $"连接 {source.Name} 超时，请检查网络后重试。", source);
        }
        catch (HttpRequestException exception)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, $"无法连接 {source.Name}：{exception.Message}", source);
        }
        catch (InvalidOperationException exception)
        {
            // 例如系统代理配置异常导致 HttpClient 无法构造。
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "无法发起更新请求：" + exception.Message, source);
        }
    }

    /// <summary>
    /// 走系统代理（<see cref="SocketsHttpHandler"/> 默认 UseProxy=true）。
    /// 目标地址是硬编码的 GitHub，不受用户输入影响，因此不需要目录抓取那套 SSRF 限制。
    /// </summary>
    internal static HttpClient CreateClient(string userAgent, TimeSpan? timeout = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = RequestTimeout,
        };
        var client = new HttpClient(handler) { Timeout = timeout ?? RequestTimeout };
        // GitHub API 要求带 User-Agent，缺少会直接 403；AtomGit 接受同样的头。
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"EasyPub-Modern/{userAgent}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string property)
    {
        var text = ReadString(element, property);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static List<UpdateAsset> ReadAssets(JsonElement root)
    {
        var assets = new List<UpdateAsset>();
        if (!root.TryGetProperty("assets", out var array) || array.ValueKind != JsonValueKind.Array) return assets;
        foreach (var element in array.EnumerateArray())
        {
            var name = ReadString(element, "name");
            var url = ReadString(element, "browser_download_url");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url)) continue;
            // AtomGit / GitCode 会往 assets 里塞四个平台自动生成的源码包（type=source），
            // 它们不是我们发布的更新包；而更新包自己的名字以 -win-x64.zip 结尾，两者本就不该混。
            if (string.Equals(ReadString(element, "type"), "source", StringComparison.OrdinalIgnoreCase)) continue;
            var size = element.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed) ? parsed : 0L;
            assets.Add(new UpdateAsset(name, size, url));
        }

        return assets;
    }
}
