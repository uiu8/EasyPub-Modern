using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class UpdateCheckerTests
{
    /// <summary>
    /// 取自 https://api.github.com/repos/uiu8/EasyPub-Modern/releases/latest 的真实响应结构
    /// （v1.56.3，去掉与解析无关的字段）。字段名和嵌套层级必须与 GitHub 保持一致。
    /// </summary>
    private const string LatestRelease = """
    {
      "url": "https://api.github.com/repos/uiu8/EasyPub-Modern/releases/253000001",
      "html_url": "https://github.com/uiu8/EasyPub-Modern/releases/tag/v1.56.3",
      "id": 253000001,
      "node_id": "RE_kwDOUBeeIc4Pq4AB",
      "tag_name": "v1.56.3",
      "target_commitish": "main",
      "name": "EasyPub Modern v1.56.3",
      "draft": false,
      "prerelease": false,
      "created_at": "2026-09-16T05:46:56Z",
      "published_at": "2026-09-16T05:46:57Z",
      "assets": [
        {
          "url": "https://api.github.com/repos/uiu8/EasyPub-Modern/releases/assets/4001",
          "id": 4001,
          "name": "EasyPubModern-Setup-v1.56.3-x64.exe",
          "size": 67568253,
          "download_count": 12,
          "browser_download_url": "https://github.com/uiu8/EasyPub-Modern/releases/download/v1.56.3/EasyPubModern-Setup-v1.56.3-x64.exe"
        },
        {
          "url": "https://api.github.com/repos/uiu8/EasyPub-Modern/releases/assets/4002",
          "id": 4002,
          "name": "EasyPubModern-v1.56.3-sidebar-overflow-win-x64.zip",
          "size": 68087283,
          "download_count": 30,
          "browser_download_url": "https://github.com/uiu8/EasyPub-Modern/releases/download/v1.56.3/EasyPubModern-v1.56.3-sidebar-overflow-win-x64.zip"
        }
      ],
      "body": "## 修复\n\n- 侧栏导航文字不再被遮挡。",
      "tarball_url": "https://api.github.com/repos/uiu8/EasyPub-Modern/tarball/v1.56.3",
      "zipball_url": "https://api.github.com/repos/uiu8/EasyPub-Modern/zipball/v1.56.3"
    }
    """;

    [Fact]
    public void Parses_the_real_github_payload()
    {
        Assert.True(UpdateChecker.TryParseLatest(LatestRelease, out var release));
        Assert.Equal("v1.56.3", release.Tag);
        Assert.Equal(new Version(1, 56, 3), release.Version);
        Assert.Equal("EasyPub Modern v1.56.3", release.Title);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal(new DateTimeOffset(2026, 9, 16, 5, 46, 57, TimeSpan.Zero), release.PublishedAt);
        Assert.Contains("侧栏导航文字", release.Notes);
    }

    [Fact]
    public void Self_update_uses_the_portable_zip_not_the_installer()
    {
        Assert.True(UpdateChecker.TryParseLatest(LatestRelease, out var release));
        var package = release.PortablePackage;
        Assert.NotNull(package);
        Assert.Equal("EasyPubModern-v1.56.3-sidebar-overflow-win-x64.zip", package!.Name);
        // 大小必须来自发布页声明，后面要用它校验下载是否完整。
        Assert.Equal(68087283, package.Size);
    }

    [Fact]
    public void Newer_release_reports_an_update()
    {
        var result = UpdateChecker.Evaluate(LatestRelease, new Version(1, 56, 2));
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.True(result.HasUpdate);
        Assert.Contains("1.56.3", result.Message);
    }

    [Fact]
    public void Same_version_reports_up_to_date()
    {
        var result = UpdateChecker.Evaluate(LatestRelease, new Version(1, 56, 3));
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.False(result.HasUpdate);
        Assert.Contains("已是最新版本", result.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"tag_name\":\"nightly\"}")]
    [InlineData("{\"name\":\"没有 tag\"}")]
    public void Unusable_responses_fail_instead_of_claiming_an_update(string json)
    {
        var result = UpdateChecker.Evaluate(json, new Version(1, 56, 3));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public void Release_without_a_portable_package_still_reports_but_says_to_download_manually()
    {
        const string json = """
        { "tag_name": "v1.57.0", "name": "只有安装包", "assets": [
            { "name": "EasyPubModern-Setup-v1.57.0-x64.exe", "size": 100, "browser_download_url": "https://example.com/setup.exe" } ] }
        """;
        var result = UpdateChecker.Evaluate(json, new Version(1, 56, 3));
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        // 没有便携包就不能自动更新，必须把话说清楚，而不是给一个装不上的按钮。
        Assert.Contains("手动下载", result.Message);
        Assert.Null(result.Release!.PortablePackage);
    }

    [Fact]
    public void Missing_published_at_or_assets_does_not_break_parsing()
    {
        const string json = """{ "tag_name": "v1.57.0", "name": "无资产" }""";
        Assert.True(UpdateChecker.TryParseLatest(json, out var release));
        Assert.Null(release.PublishedAt);
        Assert.Empty(release.Assets);
        Assert.Equal(string.Empty, release.Notes);
    }

    [Fact]
    public void Assets_missing_a_name_or_url_are_skipped()
    {
        const string json = """
        { "tag_name": "v1.57.0", "assets": [
            { "name": "", "browser_download_url": "https://example.com/a.zip" },
            { "name": "b.zip" },
            { "name": "c.zip", "size": 5, "browser_download_url": "https://example.com/c.zip" } ] }
        """;
        Assert.True(UpdateChecker.TryParseLatest(json, out var release));
        Assert.Single(release.Assets);
        Assert.Equal("c.zip", release.Assets[0].Name);
    }
}

public class UpdateInstallerTests
{
    private static UpdateApplyPlan Plan() => new(
        StagingDirectory: @"C:\Users\tester\AppData\Local\Temp\EasyPubModern-Update",
        TargetDirectory: @"C:\Users\tester\Apps\EasyPub Modern",
        ExecutableName: "EasyPub.Desktop.exe",
        BackupDirectory: @"C:\Users\tester\Apps\EasyPub Modern\.backup-1.56.3",
        ScriptPath: @"C:\Users\tester\AppData\Local\Temp\EasyPubModern-Update\apply-update.ps1",
        ProcessId: 4321);

    [Fact]
    public void Script_waits_for_the_running_process_to_exit()
    {
        var script = UpdateInstaller.BuildScript(Plan());
        Assert.Contains("Wait-Process -Id $targetPid", script);
        Assert.Contains("$targetPid = 4321", script);
    }

    [Fact]
    public void Script_probes_until_the_executable_handle_is_released()
    {
        // 进程消失不等于句柄释放（杀软扫描、写盘延迟），必须先探测到能独占打开再覆盖。
        var script = UpdateInstaller.BuildScript(Plan());
        Assert.Contains("[System.IO.File]::Open($exePath, 'Open', 'ReadWrite', 'None')", script);
        Assert.Contains("$probe -le 15", script);
    }

    [Fact]
    public void Script_excludes_backup_directory_from_its_own_backup()
    {
        // 备份目录就在程序目录里面；不排除自己会把备份递归复制进备份。
        var script = UpdateInstaller.BuildScript(Plan());
        Assert.Contains("$_.Name -notlike '.backup-*'", script);
        Assert.Contains("$_.Name -notlike '.update-*'", script);
    }

    [Fact]
    public void Script_rolls_back_and_restarts_even_when_copying_fails()
    {
        var script = UpdateInstaller.BuildScript(Plan());
        Assert.Contains("$attempt -le 5", script);              // 覆盖重试
        Assert.Contains("Copy-Item (Join-Path $backup '*'", script); // 回滚
        Assert.Contains("Start-Process -FilePath (Join-Path $target $exe)", script); // 无论成败都重启
    }

    [Fact]
    public void Script_quotes_paths_containing_spaces()
    {
        var script = UpdateInstaller.BuildScript(Plan());
        Assert.Contains("$target = 'C:\\Users\\tester\\Apps\\EasyPub Modern'", script);
    }

    [Fact]
    public void Script_escapes_single_quotes_in_paths()
    {
        var plan = Plan() with { TargetDirectory = @"C:\it's here\EasyPub" };
        var script = UpdateInstaller.BuildScript(plan);
        Assert.Contains(@"'C:\it''s here\EasyPub'", script);
    }

    [Fact]
    public void WriteScript_emits_a_bom_so_chinese_paths_survive_windows_powershell()
    {
        var directory = Path.Combine(Path.GetTempPath(), "easypub-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var plan = Plan() with { ScriptPath = Path.Combine(directory, "apply-update.ps1") };
            var path = UpdateInstaller.WriteScript(plan);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
            Assert.Contains("Wait-Process", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Staging_with_a_single_top_level_folder_is_flattened()
    {
        // 真实发布包（v1.56.3）就是带顶层目录的：EasyPubModern-v1.56.3-xxx/EasyPub.Desktop.exe。
        // 不摊平的话落地脚本的 $source\* 会把整个版本目录复制进程序目录，更新完程序就找不到了。
        var root = Path.Combine(Path.GetTempPath(), "easypub-flatten-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var package = Path.Combine(root, "package.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("EasyPubModern-v1.56.3-sidebar-overflow-win-x64/" + UpdateInstaller.ExecutableName);
                using (var writer = new StreamWriter(entry.Open())) writer.Write("new build");
                var nested = archive.CreateEntry("EasyPubModern-v1.56.3-sidebar-overflow-win-x64/config.xml");
                using (var writer = new StreamWriter(nested.Open())) writer.Write("<config/>");
            }

            var staging = UpdateInstaller.ExtractPackage(package, Path.Combine(root, "staging"));

            Assert.True(UpdateInstaller.StagingLooksComplete(staging));
            Assert.True(File.Exists(Path.Combine(staging, "config.xml")));
            Assert.False(Directory.Exists(Path.Combine(staging, "EasyPubModern-v1.56.3-sidebar-overflow-win-x64")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Staging_that_already_has_files_in_the_root_is_left_alone()
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-flat-noop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, UpdateInstaller.ExecutableName), "stub");
            File.WriteAllText(Path.Combine(root, "config.xml"), "<config/>");
            Assert.Equal(root, UpdateInstaller.FlattenStaging(root));
            Assert.True(File.Exists(Path.Combine(root, UpdateInstaller.ExecutableName)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Update_archive_is_kept_outside_the_staging_directory()
    {
        // 解压前会清空暂存目录。更新包要是放在里面，清理会先把它删掉，紧接着的解压必然失败
        // （这一步在真实下载验证里就是这样炸掉的）。
        var staging = UpdateInstaller.StagingDirectory;
        var package = UpdateInstaller.PackagePath(UpdateInstaller.StagingRoot);
        Assert.False(string.Equals(Path.GetDirectoryName(package), staging, StringComparison.OrdinalIgnoreCase));
        Assert.False(package.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Extracting_does_not_delete_the_archive_it_reads_from()
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-coexist-" + Guid.NewGuid().ToString("N"));
        try
        {
            var package = UpdateInstaller.PackagePath(root);
            Directory.CreateDirectory(root);
            using (var archive = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(UpdateInstaller.ExecutableName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("new build");
            }

            var staging = UpdateInstaller.ExtractPackage(package, Path.Combine(root, "staging"));

            Assert.True(File.Exists(package), "更新包必须还在——脚本之后还要靠它重试");
            Assert.True(UpdateInstaller.StagingLooksComplete(staging));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Staging_is_incomplete_until_the_executable_is_there()
    {
        var directory = Path.Combine(Path.GetTempPath(), "easypub-staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.False(UpdateInstaller.StagingLooksComplete(directory));
            File.WriteAllText(Path.Combine(directory, UpdateInstaller.ExecutableName), "stub");
            Assert.True(UpdateInstaller.StagingLooksComplete(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Extract_package_replaces_previous_staging_contents()
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var staging = Path.Combine(root, "staging");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, "leftover.txt"), "上一次更新的残留");

            var package = Path.Combine(root, "package.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry(UpdateInstaller.ExecutableName);
                using var writer = new StreamWriter(entry.Open());
                writer.Write("new build");
            }

            UpdateInstaller.ExtractPackage(package, staging);

            Assert.True(UpdateInstaller.StagingLooksComplete(staging));
            Assert.False(File.Exists(Path.Combine(staging, "leftover.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class PendingUpdateStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "easypub-pending-" + Guid.NewGuid().ToString("N"), "pending-update.json");

    [Fact]
    public void Round_trips_a_pending_update()
    {
        var path = TempPath();
        try
        {
            var store = new PendingUpdateStore(path);
            Assert.Null(store.Load());

            var pending = new PendingUpdate("1.57.0", @"C:\Temp\staging", @"C:\Apps\EasyPub", DateTimeOffset.Now);
            store.Save(pending);

            var loaded = store.Load();
            Assert.NotNull(loaded);
            Assert.Equal("1.57.0", loaded!.Version);
            Assert.Equal(@"C:\Temp\staging", loaded.StagingDirectory);

            store.Clear();
            Assert.Null(store.Load());
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Corrupt_record_reads_as_null_instead_of_crashing_startup()
    {
        var path = TempPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ this is not json");
            Assert.Null(new PendingUpdateStore(path).Load());
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadReady_ignores_records_whose_staging_directory_is_gone()
    {
        var path = TempPath();
        try
        {
            var store = new PendingUpdateStore(path);
            store.Save(new PendingUpdate("1.57.0", Path.Combine(Path.GetTempPath(), "easypub-not-here-" + Guid.NewGuid().ToString("N")), @"C:\Apps\EasyPub", DateTimeOffset.Now));
            // 记录还在，但暂存目录已经被清理：不能因此提示用户"重启即可更新"。
            Assert.NotNull(store.Load());
            Assert.Null(store.LoadReady());
        }
        finally
        {
            var directory = Path.GetDirectoryName(path)!;
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LoadReady_accepts_a_record_whose_staging_directory_has_the_executable()
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-pending-ready-" + Guid.NewGuid().ToString("N"));
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, UpdateInstaller.ExecutableName), "stub");
        try
        {
            var store = new PendingUpdateStore(Path.Combine(root, "pending-update.json"));
            store.Save(new PendingUpdate("1.57.0", staging, @"C:\Apps\EasyPub", DateTimeOffset.Now));
            Assert.NotNull(store.LoadReady());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
