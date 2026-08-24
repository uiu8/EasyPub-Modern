using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class KindlePreviewerLauncherTests
{
    [Fact]
    public void Discovery_prefers_the_official_command_line_launcher()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-kp-discovery-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        var gui = Path.Combine(root, "Kindle Previewer 3.exe");
        var cli = Path.Combine(root, "bin", "kindlepreviewer.bat");
        File.WriteAllText(gui, "gui");
        File.WriteAllText(cli, "cli");
        try
        {
            var result = new KindlePreviewerLauncher().Discover([root], pathEnvironment: string.Empty);

            Assert.NotNull(result);
            Assert.Equal(Path.GetFullPath(cli), result.ExecutablePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Preview_copy_and_official_arguments_are_safe_for_chinese_book_names()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-kp-copy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.epub");
        var executable = Path.Combine(root, "kindlepreviewer.exe");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        File.WriteAllText(executable, "previewer");
        try
        {
            var launcher = new KindlePreviewerLauncher();
            var copy = launcher.PreparePreviewCopy(source, "测试：书名", Path.Combine(root, "previews"));
            var startInfo = launcher.CreateStartInfo(copy, new KindlePreviewerInstallation(executable));

            Assert.True(File.Exists(copy));
            Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(copy));
            Assert.Equal(Path.GetFullPath(executable), startInfo.FileName);
            Assert.Equal(
                [
                    Path.GetFullPath(copy),
                    "-showpreview",
                    "-output",
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(copy))!, "kindle-output"),
                    "-locale",
                    "zh",
                ],
                startInfo.ArgumentList);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Official_batch_alias_is_started_through_cmd_with_one_controlled_command()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-kp-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "测试 & 预览.epub");
        var alias = Path.Combine(root, "kindlepreviewer.bat");
        File.WriteAllBytes(source, [1, 2, 3]);
        File.WriteAllText(alias, "@echo off");
        try
        {
            var startInfo = new KindlePreviewerLauncher().CreateStartInfo(
                source,
                new KindlePreviewerInstallation(alias));

            Assert.Equal("cmd.exe", Path.GetFileName(startInfo.FileName), ignoreCase: true);
            Assert.Equal(["/d", "/s", "/c"], startInfo.ArgumentList.Take(3));
            var command = Assert.Single(startInfo.ArgumentList.Skip(3));
            Assert.Contains($"\"{Path.GetFullPath(alias)}\"", command);
            Assert.Contains($"\"{Path.GetFullPath(source)}\"", command);
            Assert.Contains("-showpreview", command);
            Assert.Contains("-locale zh", command);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Preparing_a_preview_removes_only_expired_preview_sessions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"easypub-kp-cleanup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "source.epub");
        File.WriteAllBytes(source, [1, 2, 3]);
        var cache = Path.Combine(root, "cache");
        var expired = Path.Combine(cache, "preview-expired");
        var recent = Path.Combine(cache, "preview-recent");
        var unrelated = Path.Combine(cache, "unrelated");
        Directory.CreateDirectory(expired);
        Directory.CreateDirectory(recent);
        Directory.CreateDirectory(unrelated);
        Directory.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-8));
        try
        {
            new KindlePreviewerLauncher().PreparePreviewCopy(source, "清理测试", cache);

            Assert.False(Directory.Exists(expired));
            Assert.True(Directory.Exists(recent));
            Assert.True(Directory.Exists(unrelated));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
