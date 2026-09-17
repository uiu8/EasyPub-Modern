using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 备份窗口在真实样书上的样子，以及这一阶段最要紧的一条：**它不碰书稿**。
///
/// 用环境变量把应用设置指到临时目录，于是 <c>SourceBackupStore.CreateDefault()</c> 把备份写进
/// 测试自己的目录 —— 既不污染用户真实的备份夹，又走的是产品那条完全一样的路径。
///
/// <para>设 <c>EASYPUB_SCREENSHOT_DIR</c> 写出 PNG。</para>
/// </summary>
public class SampleBookBackupWindowTests
{
    private static string ScreenshotDirectory =>
        Environment.GetEnvironmentVariable("EASYPUB_SCREENSHOT_DIR") ?? "";

    private static string SampleRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "六卷缺陷样书");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 samples/六卷缺陷样书。");
    }

    private static void Save(Window window, string name)
    {
        window.UpdateLayout();
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        Assert.True(width > 0 && height > 0, $"{name} 没有布局尺寸（{width}×{height}）");
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        Assert.True(stream.Length > 4096, $"{name} 渲染出的 PNG 只有 {stream.Length} 字节");
        if (string.IsNullOrWhiteSpace(ScreenshotDirectory)) return;
        Directory.CreateDirectory(ScreenshotDirectory);
        File.WriteAllBytes(Path.Combine(ScreenshotDirectory, name + ".png"), stream.ToArray());
    }

    /// <summary>
    /// 走一遍真实的备份路径：首次原文 + 修复前快照，然后打开窗口。
    ///
    /// 关键断言不是"窗口画出来了"，而是"书稿一个字节都没变" —— Phase 2 不允许有任何写回能力，
    /// 恢复备份本身也是一次源文件改动，需要 Phase 5 的事务层。
    /// </summary>
    [Fact]
    public async Task The_backup_window_lists_a_real_books_backups_and_never_touches_it()
    {
        var sample = SampleRoot();
        var bookPath = Path.Combine(sample, "缺陷样书.txt");

        // 用副本，不动仓库里的样书本体 —— 但副本的内容与样书完全一致。
        var workspace = Path.Combine(Path.GetTempPath(), $"easypub-phase2-{Guid.NewGuid():N}");
        var backupRoot = Path.Combine(workspace, "backups");
        Directory.CreateDirectory(workspace);
        var book = Path.Combine(workspace, "缺陷样书.txt");
        File.Copy(bookPath, book);

        var settingsPath = Path.Combine(workspace, "app-settings.json");
        var previousSettings = Environment.GetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH");
        var previousBackups = Environment.GetEnvironmentVariable("EASYPUB_SOURCE_BACKUPS_PATH");
        Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH", settingsPath);
        // 这两个变量在进程启动时被读过一次；显式清掉，让本次测试只依赖下面写出的设置文件。
        Environment.SetEnvironmentVariable("EASYPUB_SOURCE_BACKUPS_PATH", null);
        try
        {
            var store = AppSettingsStore.CreateDefault();
            await store.SaveAsync(EasyPubAppSettings.Default with
            {
                SourceBackupRoot = backupRoot,
                SourceBackupRetentionLimit = 5,
            });

            var shaBefore = Sha(book);
            var writeBefore = File.GetLastWriteTimeUtc(book);

            var document = await ChapterTreeDocument.LoadAsync(book);
            SourceBackupStore.CreateDefault().EnsureBackup(book);
            SourceBackupStore.CreateDefault().EnsureSnapshot(document);

            var resolved = SourceBackupStore.CreateDefault().DirectoryPath;
            Assert.Equal(Path.GetFullPath(backupRoot), resolved);

            Exception? failure = null;
            // 自建 STA 线程，且**不**创建 Application。
            //
            // 这套测试里 Application 是进程级单例，谁先建谁独占，线程一结束它就属于一条死线程 ——
            // 之后所有靠 Application.Current 解析资源或找窗口的测试会一起倒。曾经在这里加过
            // `if (Application.Current is null) _ = new Application();`，整份套件从偶发 1 个失败
            // 变成 90 个。备份窗口不依赖 Application 的资源，所以这里什么都不建。
            var thread = new Thread(() =>
            {
                try
                {
                    var window = new SourceBackupWindow([book])
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = -4000,
                        Top = -4000,
                        ShowInTaskbar = false,
                    };
                    window.Show();
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    Save(window, "20-六卷样书-原文备份");

                    Assert.Equal("原文备份", window.Title);
                    window.Close();
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "备份窗口线程没有在 60 秒内结束");
            if (failure is not null) throw failure;

            // 书稿必须原封不动。
            Assert.Equal(shaBefore, Sha(book));
            Assert.Equal(writeBefore, File.GetLastWriteTimeUtc(book));

            // 备份确实落在配置的位置，且首次原文与快照各一份。
            var entries = SourceBackupStore.CreateDefault().Inventory().ListEntries(book);
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, entry => entry.Kind == SourceBackupKind.Baseline);
            Assert.Contains(entries, entry => entry.Kind == SourceBackupKind.Snapshot);

            // 导出必须给出与书稿一致的字节。
            var exportPath = Path.Combine(workspace, "导出.txt");
            await SourceBackupStore.CreateDefault().Inventory()
                .ExportAsync(entries[^1].Path, exportPath);
            Assert.Equal(shaBefore, Sha(exportPath));
            Assert.Equal(shaBefore, Sha(book));
        }
        finally
        {
            Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH", previousSettings);
            Environment.SetEnvironmentVariable("EASYPUB_SOURCE_BACKUPS_PATH", previousBackups);
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    private static string Sha(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
}
