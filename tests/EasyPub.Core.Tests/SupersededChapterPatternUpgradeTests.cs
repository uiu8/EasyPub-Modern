using System.IO;
using System.Text.Json;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// **旧设置机器与新装必须识别出同样的章节。**（P0-5）
///
/// <para>设置文件里存着"当年写入的默认值"，它不为空，所以永远压着代码里的新默认值 ——
/// 光改代码传不到老用户身上。这是 §8.3 那个升级机制存在的理由；
/// 它原先只覆盖 Level1/Level2，**漏了章节正则**。</para>
///
/// <para>漏掉它的代价是实测的：章节正则为旧值、分层又是默认关时，Level 正则根本不被读取
/// （<c>TocHierarchyOptions.Enabled</c> 默认 false），于是那 7 个裸数字标题
/// （「一百零一章」这类没有「第」字的）永远认不出来，会被并进上一章的正文。</para>
/// </summary>
public class SupersededChapterPatternUpgradeTests
{
    private static string SamplePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "六卷缺陷样书", "缺陷样书.txt");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 samples/六卷缺陷样书");
    }

    [Fact]
    public void The_old_default_is_upgraded_to_the_built_in_rule()
    {
        var options = new ConversionOptions { ChapterPattern = HeadingSyntax.LegacyPattern };

        var upgraded = options.UpgradeSupersededChapterPattern(out var changed);

        Assert.True(changed);
        Assert.Null(upgraded.ChapterPattern);
    }

    /// <summary>
    /// **用户真改过的正则一个字符都不许动。** 判据是字符串完全相等 ——
    /// 自己写过正则的人不会恰好等于那一长串。
    /// </summary>
    [Theory]
    [InlineData(@"^第\d+话.*")]          // 日式「第1话」
    [InlineData(@"^\s*第[一二三]+章.*")]  // 旧默认的"差不多"版本，但不是它
    [InlineData("")]
    public void A_pattern_the_user_actually_wrote_is_left_alone(string pattern)
    {
        var options = new ConversionOptions { ChapterPattern = pattern };

        var upgraded = options.UpgradeSupersededChapterPattern(out var changed);

        Assert.False(changed);
        Assert.Equal(pattern, upgraded.ChapterPattern);
    }

    /// <summary>
    /// 章节正则不在 <c>EasyPubAppSettings</c> 上，而在每个 <c>ConversionProfile</c> 里
    /// （LastProfile + 每个预设各一份）。只升全局那份等于没升 —— 这条锁住"逐个都走到"。
    /// </summary>
    [Fact]
    public void Every_profile_is_upgraded_not_only_the_last_one()
    {
        var old = new ConversionOptions { ChapterPattern = HeadingSyntax.LegacyPattern };
        var settings = EasyPubAppSettings.Default with
        {
            LastProfile = new ConversionProfile("epub", null, 1, null, old),
            Presets =
            [
                new NamedConversionPreset("甲", new ConversionProfile("epub", null, 1, null, old)),
                new NamedConversionPreset("乙", new ConversionProfile("epub", null, 1, null,
                    new ConversionOptions { ChapterPattern = @"^第\d+话" })),
            ],
        };

        var upgraded = AppSettingsStore.UpgradeSupersededPatterns(settings);

        Assert.Null(upgraded.LastProfile.Options.ChapterPattern);
        Assert.Null(upgraded.Presets[0].Profile.Options.ChapterPattern);
        // 预设乙是用户自己写的，不许动。
        Assert.Equal(@"^第\d+话", upgraded.Presets[1].Profile.Options.ChapterPattern);
    }

    /// <summary>
    /// **这一条是 P0-5 真正要买到的东西。**
    ///
    /// <para>一台老机器：章节正则是旧默认、分层是默认关（也就是新装的状态）。
    /// 升级之前它数出 461 章，升级之后必须和新装的 468 章**一模一样** ——
    /// 差的 7 章正是那些没有「第」字的裸数字标题。</para>
    /// </summary>
    [Fact]
    public async Task An_old_settings_machine_ends_up_counting_what_a_fresh_install_counts()
    {
        var path = SamplePath();
        var oldMachine = new ConversionOptions { ChapterPattern = HeadingSyntax.LegacyPattern };

        var before = await ChapterTreeDocument.LoadAsync(path, oldMachine.ChapterPattern, oldMachine.TocHierarchy);
        var upgradedOptions = oldMachine.UpgradeSupersededChapterPattern(out var changed);
        var after = await ChapterTreeDocument.LoadAsync(path, upgradedOptions.ChapterPattern, upgradedOptions.TocHierarchy);
        var freshInstall = await ChapterTreeDocument.LoadAsync(path, new ConversionOptions().ChapterPattern,
            new ConversionOptions().TocHierarchy);

        Assert.True(changed);
        Assert.Equal(461, before.Entries.Count);
        Assert.Equal(freshInstall.Entries.Count, after.Entries.Count);
        Assert.Equal(468, after.Entries.Count);
        // 差的那 7 章是裸数字标题，不是凭空多出来的：旧规则漏掉它们，
        // 它们会被并进上一章的正文 —— 那是正文被改写，不只是目录少一行。
        Assert.Equal(7, after.Entries.Count(entry => entry.TitleLineNumber.HasValue)
            - before.Entries.Count(entry => entry.TitleLineNumber.HasValue));
    }

    /// <summary>
    /// 升级走的是 <c>Load</c>，所以直接写一份带旧值的设置文件、再读回来验证端到端。
    /// </summary>
    [Fact]
    public void The_upgrade_happens_on_load_from_the_real_settings_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-settings-{Guid.NewGuid():N}.json");
        try
        {
            var old = new ConversionOptions { ChapterPattern = HeadingSyntax.LegacyPattern };
            var onDisk = EasyPubAppSettings.Default with
            {
                LastProfile = new ConversionProfile("epub", null, 1, null, old),
            };
            File.WriteAllText(path, JsonSerializer.Serialize(onDisk,
                new JsonSerializerOptions { WriteIndented = false }));
            var beforeBytes = File.ReadAllBytes(path);

            var loaded = new AppSettingsStore(path).Load();

            Assert.Null(loaded.LastProfile.Options.ChapterPattern);
            // 只升内存，**不写回文件** —— 有副作用，而且可能在只读环境失败。
            // 比对字节而不是找子串：JSON 会把正则里的反斜杠转义，子串匹配必然落空。
            Assert.Equal(beforeBytes, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }
}
