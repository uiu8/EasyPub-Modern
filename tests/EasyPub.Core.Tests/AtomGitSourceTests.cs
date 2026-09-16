using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// AtomGit 镜像源。样本取自
/// https://atomgit.com/api/v5/repos/Wohl/EasyPub-Modern/releases/latest
/// 2026-09-16 的真实响应（v1.57.2），只保留与解析相关的字段，值一律未改。
/// </summary>
public class AtomGitSourceTests
{
    private const string LatestRelease = """
    {
      "tag_name": "v1.57.2",
      "target_commitish": "ca3decbb8bb53990665b42e0965d3c801627ae56",
      "prerelease": false,
      "name": "EasyPub Modern v1.57.2",
      "body": "# EasyPub Modern v1.57.2：修正备份目录的命名\n\n2026-09-16，基于 v1.57.1（5732148）。",
      "author": { "id": "6aaa38700daaa416078e5afc", "login": "Wohl", "name": "Wohl", "type": "User" },
      "created_at": "09/16/2026 15:25:30",
      "assets": [
        {
          "browser_download_url": "https://raw.gitcode.com/Wohl/EasyPub-Modern/archive/refs/heads/v1.57.2.zip",
          "name": "v1.57.2.zip",
          "type": "source"
        },
        {
          "browser_download_url": "https://raw.gitcode.com/Wohl/EasyPub-Modern/archive/refs/heads/v1.57.2.tar.gz",
          "name": "v1.57.2.tar.gz",
          "type": "source"
        },
        {
          "browser_download_url": "https://raw.gitcode.com/Wohl/EasyPub-Modern/archive/refs/heads/v1.57.2.tar.bz2",
          "name": "v1.57.2.tar.bz2",
          "type": "source"
        },
        {
          "browser_download_url": "https://raw.gitcode.com/Wohl/EasyPub-Modern/archive/refs/heads/v1.57.2.tar",
          "name": "v1.57.2.tar",
          "type": "source"
        },
        {
          "browser_download_url": "https://gitcode.com/Wohl/EasyPub-Modern/releases/download/v1.57.2/EasyPubModern-Setup-v1.57.2-x64.exe",
          "name": "EasyPubModern-Setup-v1.57.2-x64.exe",
          "type": "attach",
          "id": 205263
        },
        {
          "browser_download_url": "https://gitcode.com/Wohl/EasyPub-Modern/releases/download/v1.57.2/EasyPubModern-v1.57.2-backup-name-win-x64.zip",
          "name": "EasyPubModern-v1.57.2-backup-name-win-x64.zip",
          "type": "attach",
          "id": 205264
        }
      ],
      "release_status": "latest"
    }
    """;

    [Fact]
    public void Parses_the_real_atomgit_payload()
    {
        Assert.True(UpdateChecker.TryParseLatest(LatestRelease, out var release));
        Assert.Equal("v1.57.2", release.Tag);
        Assert.Equal(new Version(1, 57, 2), release.Version);
        Assert.Equal("EasyPub Modern v1.57.2", release.Title);
        Assert.Contains("修正备份目录的命名", release.Notes);
    }

    [Fact]
    public void Platform_generated_source_archives_are_filtered_out()
    {
        // AtomGit（后端是 GitCode）会往 assets 里塞四个源码包。它们不是更新包，
        // 混进来会让"哪个才是要下载的文件"变得不确定。
        Assert.True(UpdateChecker.TryParseLatest(LatestRelease, out var release));
        Assert.Equal(2, release.Assets.Count);
        Assert.DoesNotContain(release.Assets, asset => asset.Name.EndsWith(".tar.gz"));
        Assert.DoesNotContain(release.Assets, asset => asset.Name == "v1.57.2.zip");
        Assert.All(release.Assets, asset => Assert.Contains("gitcode.com", asset.DownloadUrl));
    }

    [Fact]
    public void Self_update_picks_the_portable_zip_from_the_mirror()
    {
        Assert.True(UpdateChecker.TryParseLatest(LatestRelease, out var release));
        var package = release.PortablePackage;
        Assert.NotNull(package);
        Assert.Equal("EasyPubModern-v1.57.2-backup-name-win-x64.zip", package!.Name);
        // AtomGit 不提供 size；为 0 表示"未知"，下载时只靠 Content-Length 算进度。
        Assert.Equal(0, package.Size);
    }

    [Fact]
    public void Falls_back_to_created_at_because_atomgit_has_no_published_at()
    {
        Assert.True(UpdateChecker.TryParseLatest(LatestRelease, out var release));
        Assert.NotNull(release.PublishedAt);
        Assert.Equal(2026, release.PublishedAt!.Value.Year);
        Assert.Equal(9, release.PublishedAt.Value.Month);
        Assert.Equal(16, release.PublishedAt.Value.Day);
    }

    [Fact]
    public void A_version_equal_to_the_mirror_is_reported_as_up_to_date()
    {
        var result = UpdateChecker.Evaluate(LatestRelease, new Version(1, 57, 2), UpdateSources.AtomGit);
        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Equal("AtomGit", result.Source!.Name);
    }

    [Fact]
    public void An_older_local_version_sees_the_mirror_as_an_update()
    {
        var result = UpdateChecker.Evaluate(LatestRelease, new Version(1, 57, 1), UpdateSources.AtomGit);
        Assert.Equal(UpdateCheckStatus.Available, result.Status);
        Assert.Contains("1.57.2", result.Message);
        Assert.Equal("AtomGit", result.Source!.Name);
    }

    [Fact]
    public void Sources_are_tried_in_order_with_github_first()
    {
        Assert.Equal(["GitHub", "AtomGit"], UpdateSources.All.Select(source => source.Name));
        // 备用源必须指向镜像仓库，两边的 owner 不一样（uiu8 vs Wohl）。
        Assert.Contains("uiu8/EasyPub-Modern", UpdateSources.GitHub.LatestReleaseApi);
        Assert.Contains("Wohl/EasyPub-Modern", UpdateSources.AtomGit.LatestReleaseApi);
    }

    [Fact]
    public async Task An_empty_source_list_fails_instead_of_hanging()
    {
        var result = await UpdateChecker.CheckAsync(new Version(1, 57, 2), default, []);
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("没有配置任何更新源", result.Message);
    }
}
