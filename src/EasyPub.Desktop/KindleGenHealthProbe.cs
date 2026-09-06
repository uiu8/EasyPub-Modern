using System.Diagnostics;
using System.IO;

namespace EasyPub.Desktop;

public enum KindleGenHealthStatus
{
    Missing,
    Ready,
    Failed,
    TimedOut,
}

public sealed record KindleGenHealthResult(KindleGenHealthStatus Status, string Message)
{
    public bool IsReady => Status == KindleGenHealthStatus.Ready;
}

public static class KindleGenHealthProbe
{
    public static async Task<KindleGenHealthResult> ProbeAsync(
        string? executablePath,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            return new KindleGenHealthResult(KindleGenHealthStatus.Missing, "未找到 KindleGen");

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!,
        };
        startInfo.ArgumentList.Add("-help");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new KindleGenHealthResult(KindleGenHealthStatus.Failed, "无法启动 KindleGen");

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(timeoutCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return new KindleGenHealthResult(KindleGenHealthStatus.TimedOut, "KindleGen 自检超时");
            }

            var combined = (await outputTask.ConfigureAwait(false)) + "\n" + (await errorTask.ConfigureAwait(false));
            var recognizable = combined.Contains("KindleGen", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("Usage", StringComparison.OrdinalIgnoreCase)
                || combined.Contains("Amazon", StringComparison.OrdinalIgnoreCase);
            return recognizable
                ? new KindleGenHealthResult(KindleGenHealthStatus.Ready, "KindleGen 2.9 已就绪")
                : new KindleGenHealthResult(KindleGenHealthStatus.Failed, $"KindleGen 返回异常（代码 {process.ExitCode}）");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new KindleGenHealthResult(KindleGenHealthStatus.Failed, $"KindleGen 自检失败：{exception.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort only; the caller receives a timeout result either way.
        }
    }
}
