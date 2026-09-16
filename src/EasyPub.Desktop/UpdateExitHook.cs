using System.ComponentModel;
using System.IO;
using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// 更新落地的唯一出口：把已下载的更新交给独立脚本，由脚本在本进程退出后覆盖程序文件并重启。
/// 用户点「立即重启更新」和「关闭软件时自动更新」都走这里，行为保持一致。
/// </summary>
internal static class UpdateExitHook
{
    /// <summary>
    /// 生成并启动落地脚本，成功后清除待应用记录。
    /// 脚本里的进程号必须是<b>当前</b>进程：记录可能是上一次运行留下的，
    /// 沿用记录里的进程号会去等一个早就不存在的进程，覆盖随即失败。
    /// </summary>
    public static void Apply(PendingUpdate pending, PendingUpdateStore store)
    {
        // 备份目录用「即将被替换掉的版本」命名——它记录的是更新前的状态，回滚时要靠它辨认。
        // 早先这里传的是 pending.Version（目标版本），于是备份目录变成 .backup-1.57.1，
        // 里面装的却是 1.57.0，名字和内容对不上。
        var replaced = AppVersion.Display(typeof(UpdateExitHook).Assembly.GetName().Version);
        var plan = UpdateInstaller.CreatePlan(
            pending.StagingDirectory,
            pending.TargetDirectory,
            Environment.ProcessId,
            replaced);
        UpdateInstaller.WriteScript(plan);
        UpdateInstaller.Launch(plan.ScriptPath);
        store.Clear();
    }

    /// <summary>退出时尝试应用。没有待更新、或脚本起不来，都只是安静地放弃，绝不阻断退出。</summary>
    public static bool TryApplyOnExit(PendingUpdateStore store)
    {
        try
        {
            var pending = store.LoadReady();
            if (pending is null) return false;
            Apply(pending, store);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
}
