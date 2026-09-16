using System.Text.Json;

namespace EasyPub.Core;

/// <summary>已经下载好、等待生效的一次更新。</summary>
public sealed record PendingUpdate(
    string Version,
    string StagingDirectory,
    string TargetDirectory,
    DateTimeOffset PreparedAt);

/// <summary>
/// 待应用更新的落盘记录。
/// 放在用户数据目录（%LOCALAPPDATA%\EasyPub Modern），与程序目录分离——
/// 程序文件被覆盖后它依然在，这正是"这次没装成、下次启动还能接着装"的依据。
/// </summary>
public sealed class PendingUpdateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public PendingUpdateStore(string storagePath) => StoragePath = Path.GetFullPath(storagePath);

    public string StoragePath { get; }

    public static PendingUpdateStore CreateDefault()
    {
        var overridePath = Environment.GetEnvironmentVariable("EASYPUB_PENDING_UPDATE_PATH");
        return new PendingUpdateStore(string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EasyPub Modern",
                "pending-update.json")
            : overridePath);
    }

    /// <summary>读取记录；文件不存在或内容损坏都返回 null（损坏只意味着丢失一次提示，不该影响启动）。</summary>
    public PendingUpdate? Load()
    {
        try
        {
            if (!File.Exists(StoragePath)) return null;
            var pending = JsonSerializer.Deserialize<PendingUpdate>(File.ReadAllText(StoragePath), Options);
            return pending is null || string.IsNullOrWhiteSpace(pending.StagingDirectory) ? null : pending;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>读取记录，并且确认暂存目录里真的有可执行文件——否则等于没有可装的更新。</summary>
    public PendingUpdate? LoadReady()
    {
        var pending = Load();
        return pending is not null && UpdateInstaller.StagingLooksComplete(pending.StagingDirectory) ? pending : null;
    }

    public void Save(PendingUpdate pending)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        var temporary = StoragePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(pending, Options));
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
            // 删不掉不影响本次运行，下次 Save 会覆盖。
        }
    }
}
