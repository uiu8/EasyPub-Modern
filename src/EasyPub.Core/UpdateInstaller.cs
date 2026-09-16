using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;

namespace EasyPub.Core;

public sealed record UpdateDownloadProgress(long Received, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0d, 1d) : 0d;

    public string Describe() => Total > 0
        ? $"{Received / 1024d / 1024d:F0} / {Total / 1024d / 1024d:F0} MB"
        : $"{Received / 1024d / 1024d:F0} MB";
}

/// <summary>一次覆盖式更新所需的全部位置信息。</summary>
public sealed record UpdateApplyPlan(
    string StagingDirectory,
    string TargetDirectory,
    string ExecutableName,
    string BackupDirectory,
    string ScriptPath,
    int ProcessId);

/// <summary>
/// 免重装的更新落地：下载便携包 → 解压到临时目录 → 交给一个独立脚本在主程序退出后覆盖程序文件 → 重启。
/// 用户数据（设置、恢复快照、目录缓存）都在 %LOCALAPPDATA%\EasyPub Modern，不在程序目录，
/// 因此覆盖程序文件不会碰到它们。
/// </summary>
public static class UpdateInstaller
{
    public const string ExecutableName = "EasyPub.Desktop.exe";

    /// <summary>等待主进程退出的上限。超过就直接尝试覆盖（文件可能仍被占用，由调用方提示）。</summary>
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(60);

    /// <summary>进度回调的最小间隔，避免界面数字高频跳闪。</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    public static string StagingRoot => Path.Combine(Path.GetTempPath(), "EasyPubModern-Update");

    /// <summary>
    /// 解压出来的新程序文件所在目录，它必须和 <see cref="PackagePath"/> 分开：
    /// <see cref="ExtractPackage"/> 会先清空这个目录，更新包若放在里面会先把自己删掉。
    /// </summary>
    public static string StagingDirectory => Path.Combine(StagingRoot, "staging");

    /// <summary>程序目录是否可写。安装版默认装到用户目录（PrivilegesRequired=lowest），因此通常可写。</summary>
    public static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".easypub-write-probe-{Guid.NewGuid():N}");
            using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                stream.WriteByte(0);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>下载更新包。大小与发布页声明不一致时视为失败，避免拿到半截文件。</summary>
    public static async Task DownloadAsync(
        UpdateAsset asset,
        string destinationPath,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken token = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        // 更新包有 60MB 以上，检查更新那 20 秒的超时不够用；这里只限制“连不上”的情况，
        // 传输过程交给 CancellationToken。
        using var client = UpdateChecker.CreateClient("update-download", TimeSpan.FromMinutes(30));
        using var response = await client
            .GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        // 先看状态码再落盘，否则会把 429/5xx 的错误页当成更新包写成几十字节的坏文件。
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? asset.Size;
        var buffer = new byte[81920];
        long received = 0;
        var lastReported = 0L;
        var lastReportAt = System.Diagnostics.Stopwatch.GetTimestamp();

        await using (var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;

                // 节流：进度最多每 200ms 报一次，否则界面数字会高频跳闪。
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (progress is not null && System.Diagnostics.Stopwatch.GetElapsedTime(lastReportAt, now) >= ProgressInterval)
                {
                    lastReportAt = now;
                    lastReported = received;
                    progress.Report(new UpdateDownloadProgress(received, total));
                }
            }

            // flush 只写内核缓冲，掉电或强杀进程会留下半截文件；这里强制落到磁盘。
            destination.Flush(flushToDisk: true);
        }

        if (progress is not null && received != lastReported)
            progress.Report(new UpdateDownloadProgress(received, total));

        // 发布页给了大小就必须对上；GitHub 的 asset.size 与实体一致。
        if (asset.Size > 0 && received != asset.Size)
        {
            TryDelete(destinationPath);
            throw new IOException($"更新包大小不符（收到 {received} 字节，应为 {asset.Size} 字节），已放弃这次更新。");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 删不掉就留着，下次下载会覆盖。
        }
    }

    /// <summary>把更新包解压到干净的暂存目录，返回该目录。</summary>
    public static string ExtractPackage(string packagePath, string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
        Directory.CreateDirectory(stagingDirectory);
        ZipFile.ExtractToDirectory(packagePath, stagingDirectory, overwriteFiles: true);
        return FlattenStaging(stagingDirectory);
    }

    /// <summary>
    /// 发布包有两种可能：程序文件直接在压缩包根，或者外面套一层版本目录。
    /// 统一摊平成"内容直接在根"，否则落地脚本会把整个版本目录复制进程序目录，
    /// 更新完 exe 就不在原来的位置了。
    /// </summary>
    public static string FlattenStaging(string stagingDirectory)
    {
        var entries = Directory.GetFileSystemEntries(stagingDirectory);
        // 根下只有唯一一个目录时才可能是"套了一层"；已有多个条目说明内容本来就在根。
        if (entries.Length != 1 || !Directory.Exists(entries[0])) return stagingDirectory;
        var inner = entries[0];
        if (!File.Exists(Path.Combine(inner, ExecutableName))) return stagingDirectory;

        foreach (var entry in Directory.GetFileSystemEntries(inner))
        {
            var destination = Path.Combine(stagingDirectory, Path.GetFileName(entry));
            if (Directory.Exists(entry)) Directory.Move(entry, destination);
            else File.Move(entry, destination, overwrite: true);
        }

        Directory.Delete(inner, recursive: false);
        return stagingDirectory;
    }

    /// <summary>暂存目录里是否真的有可执行文件——防止解压出一个空壳就替换掉好程序。</summary>
    public static bool StagingLooksComplete(string stagingDirectory) =>
        File.Exists(Path.Combine(stagingDirectory, ExecutableName));

    /// <param name="replacedVersion">即将被替换掉的版本（不是要更新到的版本）。备份目录用它命名。</param>
    public static UpdateApplyPlan CreatePlan(string stagingDirectory, string targetDirectory, int processId, string replacedVersion)
    {
        var backup = Path.Combine(targetDirectory, $".backup-{replacedVersion}");
        var script = Path.Combine(stagingDirectory, "apply-update.ps1");
        return new UpdateApplyPlan(stagingDirectory, targetDirectory, ExecutableName, backup, script, processId);
    }

    /// <summary>
    /// 生成落地脚本。必须存成带 BOM 的 UTF-8，否则中文路径在 Windows PowerShell 下会乱码。
    /// 返回脚本路径。
    /// </summary>
    public static string WriteScript(UpdateApplyPlan plan)
    {
        var content = BuildScript(plan);
        File.WriteAllText(plan.ScriptPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return plan.ScriptPath;
    }

    internal static string BuildScript(UpdateApplyPlan plan)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var waitSeconds = (int)ExitWait.TotalSeconds;
        var builder = new StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'Continue'");
        builder.AppendLine($"$targetPid = {plan.ProcessId}");
        builder.AppendLine($"$target = {Quote(plan.TargetDirectory)}");
        builder.AppendLine($"$source = {Quote(plan.StagingDirectory)}");
        builder.AppendLine($"$backup = {Quote(plan.BackupDirectory)}");
        builder.AppendLine($"$exe = {Quote(plan.ExecutableName)}");
        builder.AppendLine("$log = Join-Path $source 'apply-update.log'");
        builder.AppendLine("function Write-Log([string]$message) {");
        builder.AppendLine("    try { Add-Content -Path $log -Value ((Get-Date).ToString('s') + ' ' + $message) -Encoding UTF8 } catch { }");
        builder.AppendLine("}");
        builder.AppendLine("Write-Log '更新脚本启动'");
        builder.AppendLine();
        // 1) 等主程序彻底退出，否则 exe 仍被占用。
        builder.AppendLine("try { Wait-Process -Id $targetPid -Timeout " + waitSeconds + " -ErrorAction SilentlyContinue } catch { }");
        builder.AppendLine("Start-Sleep -Milliseconds 1200");
        // 进程列表消失不代表句柄已释放（杀软扫描、写盘延迟），再探到目标 exe 能以独占方式打开为止。
        builder.AppendLine("$exePath = Join-Path $target $exe");
        builder.AppendLine("for ($probe = 1; $probe -le 15; $probe++) {");
        builder.AppendLine("    try { $handle = [System.IO.File]::Open($exePath, 'Open', 'ReadWrite', 'None'); $handle.Close(); break }");
        builder.AppendLine("    catch { Start-Sleep -Milliseconds 400 }");
        builder.AppendLine("}");
        builder.AppendLine("Write-Log '主程序已退出'");
        builder.AppendLine();
        // 2) 备份现有程序文件（用户数据不在这里，不会被碰到）。
        builder.AppendLine("try {");
        builder.AppendLine("    if (Test-Path $backup) { Remove-Item $backup -Recurse -Force -ErrorAction SilentlyContinue }");
        builder.AppendLine("    New-Item -ItemType Directory -Path $backup -Force | Out-Null");
        // 备份目录就在程序目录里面，必须把自己和历史备份排除掉，否则会递归复制。
        builder.AppendLine("    Get-ChildItem -Path $target -Force | Where-Object { $_.Name -notlike '.backup-*' -and $_.Name -notlike '.update-*' } | ForEach-Object { Copy-Item $_.FullName $backup -Recurse -Force -ErrorAction SilentlyContinue }");
        builder.AppendLine("    Write-Log ('已备份到 ' + $backup)");
        builder.AppendLine("} catch { Write-Log ('备份失败：' + $_.Exception.Message) }");
        builder.AppendLine();
        // 3) 覆盖。逐个重试，避开杀软或句柄释放的延迟。
        builder.AppendLine("$ok = $false");
        builder.AppendLine("for ($attempt = 1; $attempt -le 5; $attempt++) {");
        builder.AppendLine("    try {");
        builder.AppendLine("        Copy-Item (Join-Path $source '*') $target -Recurse -Force -Exclude 'apply-update.ps1','apply-update.log' -ErrorAction Stop");
        builder.AppendLine("        $ok = $true; break");
        builder.AppendLine("    } catch {");
        builder.AppendLine("        Write-Log ('第 ' + $attempt + ' 次覆盖失败：' + $_.Exception.Message)");
        builder.AppendLine("        Start-Sleep -Seconds 2");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine();
        // 4) 失败则回滚，保证至少还有一个能启动的程序。
        builder.AppendLine("if (-not $ok) {");
        builder.AppendLine("    Write-Log '覆盖失败，开始回滚'");
        builder.AppendLine("    try { Copy-Item (Join-Path $backup '*') $target -Recurse -Force -ErrorAction SilentlyContinue } catch { }");
        builder.AppendLine("}");
        builder.AppendLine();
        // 5) 无论成败都重新启动，并把结果写进日志供下次启动读取。
        builder.AppendLine("Write-Log ($(if ($ok) { '更新完成' } else { '更新失败并已回滚' }))");
        builder.AppendLine("try { Start-Process -FilePath (Join-Path $target $exe) } catch { Write-Log ('重启失败：' + $_.Exception.Message) }");
        builder.AppendLine("Start-Sleep -Seconds 3");
        builder.AppendLine("try { Remove-Item (Join-Path $source 'package.zip') -Force -ErrorAction SilentlyContinue } catch { }");
        builder.AppendLine("Write-Log '脚本结束'");
        return builder.ToString();
    }

    /// <summary>
    /// 用独立进程启动脚本。主程序随后应立即退出，脚本会在确认进程消失后再替换文件。
    /// </summary>
    public static void Launch(string scriptPath)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        // 用户机器上的执行策略通常是 Restricted，脚本自身无法放宽，只能由启动方指定。
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(scriptPath);
        Process.Start(start);
    }

    /// <summary>读取上次落地脚本留下的日志；没有则返回 null。用于启动时说明上次更新结果。</summary>
    public static string? ReadLastApplyLog(string stagingDirectory)
    {
        try
        {
            var log = Path.Combine(stagingDirectory, "apply-update.log");
            return File.Exists(log) ? File.ReadAllText(log) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>清理暂存目录与备份目录，释放磁盘。</summary>
    public static void CleanUp(string stagingRoot, string? backupDirectory = null)
    {
        foreach (var directory in new[] { stagingRoot, backupDirectory })
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // 清理失败不影响使用，留给下次启动再试。
            }
        }
    }

    /// <summary>更新包下载到哪里。刻意放在暂存目录之外，见 <see cref="StagingDirectory"/>。</summary>
    public static string PackagePath(string stagingRoot) => Path.Combine(stagingRoot, "package.zip");
}
