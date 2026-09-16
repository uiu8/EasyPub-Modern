namespace EasyPub.Core;

/// <summary>上一次检查更新的结果。</summary>
public sealed record UpdateCheckCache(DateTimeOffset CheckedAt, string LatestTag)
{
    /// <summary>远端当时没有更新的版本时留空。</summary>
    public bool HasNewerRelease => !string.IsNullOrWhiteSpace(LatestTag);
}

/// <summary>
/// 检查更新的节流记录。
/// GitHub 的匿名接口只有约 60 次/小时/IP：每次启动都去请求，在频繁重启或多人共用出口 IP 时
/// 会撞上限流（实测撞到过）。所以启动检查之间要留间隔；手动点「检查更新」不受这份记录约束。
/// </summary>
public sealed class UpdateCheckCacheStore
{
    /// <summary>启动检查的最小间隔。手动检查不受它限制。</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>时间戳比"现在"还晚超过这么多，就当作时钟被调整过，不再信任它。</summary>
    private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    public UpdateCheckCacheStore(string storagePath) => StoragePath = Path.GetFullPath(storagePath);

    public string StoragePath { get; }

    public static UpdateCheckCacheStore CreateDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("EASYPUB_UPDATE_CHECK_PATH");
        return new UpdateCheckCacheStore(string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPub Modern",
                "update-check.json")
            : overridePath);
    }

    /// <summary>是否可以跳过这次启动检查。缓存损坏或时间戳异常都返回 false，也就是照常去查。</summary>
    public static bool IsFresh(UpdateCheckCache? cache, DateTimeOffset now, TimeSpan interval) =>
        cache is not null
        && cache.CheckedAt <= now + FutureTolerance
        && now - cache.CheckedAt < interval;

    /// <summary>读取记录；文件不存在或损坏都返回 null（只意味着会多查一次，不影响启动）。</summary>
    public UpdateCheckCache? Load()
    {
        try
        {
            if (!File.Exists(StoragePath)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<UpdateCheckCache>(File.ReadAllText(StoragePath));
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(UpdateCheckCache cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        var temporary = StoragePath + ".tmp";
        File.WriteAllText(temporary, System.Text.Json.JsonSerializer.Serialize(cache));
        File.Move(temporary, StoragePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(StoragePath)) File.Delete(StoragePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 删不掉不影响使用，下次 Save 会覆盖。
        }
    }
}
