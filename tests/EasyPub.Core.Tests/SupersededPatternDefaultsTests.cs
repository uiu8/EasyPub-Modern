using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 设置里那份**旧默认正则**必须被升到当前默认值，而用户自己写的不许动。
///
/// <para>这条测试来自一次真实走查：同一本样书在一台旧设置的机器上识别出 <b>560</b> 个条目、
/// 在默认设置下 <b>468</b> 个。根因是设置文件里存着当年写入的默认值，而它不为空，
/// 所以在"空则用默认"里永远轮不到新默认值 —— <b>识别正则改进了却传不到老用户身上，
/// 界面上还没有任何地方提示这件事</b>。用户以为自己没改过设置（他确实没改）。</para>
/// </summary>
public class SupersededPatternDefaultsTests
{
    /// <summary>当年那份写进设置文件的旧默认值，一字不差。</summary>
    private const string OldLevel2 = @"^\s*第[0123456789一二三四五六七八九十零〇百千两]+[章回].*";

    [Fact]
    public void The_old_default_is_upgraded_to_the_current_one()
    {
        var settings = new TocHierarchyOptions { Level2Pattern = OldLevel2 };
        var upgraded = settings.UpgradeSupersededPatterns(out var changed);

        Assert.True(changed);
        Assert.Equal(TocHierarchyOptions.DefaultLevel2Pattern, upgraded.Level2Pattern);
        // 原来的对象不动 —— 它是 record，升级产生新的一份。
        Assert.Equal(OldLevel2, settings.Level2Pattern);
    }

    [Fact]
    public void A_pattern_the_user_actually_edited_is_left_alone()
    {
        // 自己写过一个正则的人，不会恰好等于旧默认值 —— 这就是判据的全部依据。
        const string Mine = @"^第\d+话\s";
        var settings = new TocHierarchyOptions { Level2Pattern = Mine };
        var kept = settings.UpgradeSupersededPatterns(out var changed);

        Assert.False(changed);
        Assert.Equal(Mine, kept.Level2Pattern);
    }

    [Fact]
    public void Already_current_defaults_are_not_touched()
    {
        var settings = new TocHierarchyOptions();
        var kept = settings.UpgradeSupersededPatterns(out var changed);
        Assert.False(changed);
        Assert.Equal(TocHierarchyOptions.DefaultLevel1Pattern, kept.Level1Pattern);
        Assert.Equal(TocHierarchyOptions.DefaultLevel2Pattern, kept.Level2Pattern);
    }
}
