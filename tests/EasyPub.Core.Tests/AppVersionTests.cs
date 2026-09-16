using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v1.56.3", 1, 56, 3)]
    [InlineData("1.56.3", 1, 56, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("v1.18", 1, 18, 0)]      // 仓库里真实存在的两段 tag
    [InlineData("1.15.1", 1, 15, 1)]
    [InlineData("v1.1.4", 1, 1, 4)]
    [InlineData("1.57.0-beta.1", 1, 57, 0)]   // 预发布后缀不参与比较
    [InlineData("1.57.0+abc123", 1, 57, 0)]   // 构建元数据不参与比较
    [InlineData("  v2.0.0  ", 2, 0, 0)]
    [InlineData("1.56.3.0", 1, 56, 3)]        // 四段取前三段
    public void Parses_real_release_tags(string tag, int major, int minor, int build)
    {
        Assert.True(AppVersion.TryParse(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("abc")]
    [InlineData("v1.x.0")]
    [InlineData("1.2.3.4.5")]
    [InlineData("nightly")]
    public void Rejects_things_that_are_not_versions(string? tag)
    {
        Assert.False(AppVersion.TryParse(tag, out var version));
        Assert.Equal(new Version(0, 0, 0), version);
    }

    [Fact]
    public void Two_part_tag_never_counts_as_newer_than_its_own_three_part_form()
    {
        // 这是最容易出错的地方：Version("1.18").Build 是 -1，直接比较会得到 1.18 > 1.18.0，
        // 于是每次检查都提示"有新版本"。补齐三段后两者必须相等。
        Assert.False(AppVersion.IsNewer("v1.18", new Version(1, 18, 0)));
        Assert.False(AppVersion.IsNewer("v1.18.0", new Version(1, 18, 0)));
        Assert.False(AppVersion.IsNewer("v1.18", new Version(1, 18)));
        Assert.Equal(new Version(1, 18, 0), AppVersion.Normalize(new Version(1, 18)));
    }

    [Theory]
    [InlineData("v1.56.4", "1.56.3", true)]
    [InlineData("v1.57.0", "1.56.3", true)]
    [InlineData("v2.0.0", "1.56.3", true)]
    [InlineData("v1.56.3", "1.56.3", false)]
    [InlineData("v1.56.2", "1.56.3", false)]
    [InlineData("v1.9.0", "1.10.0", false)]   // 必须按数字比，不能按字符串比
    [InlineData("v1.10.0", "1.9.0", true)]
    public void Compares_numerically_not_lexically(string candidate, string current, bool expected) =>
        Assert.Equal(expected, AppVersion.IsNewer(candidate, Version.Parse(current)));

    [Fact]
    public void Unparsable_candidate_never_reports_an_update() =>
        Assert.False(AppVersion.IsNewer("nightly", new Version(1, 56, 3)));

    [Fact]
    public void Display_always_shows_three_parts()
    {
        Assert.Equal("1.56.3", AppVersion.Display(new Version(1, 56, 3)));
        Assert.Equal("1.18.0", AppVersion.Display(new Version(1, 18)));
        Assert.Equal("0.0.0", AppVersion.Display(null));
    }
}
