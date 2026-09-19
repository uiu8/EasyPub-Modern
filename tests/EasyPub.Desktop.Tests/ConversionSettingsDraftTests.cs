using System.IO;
using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

public sealed class ConversionSettingsDraftTests
{
    [Fact]
    public void Default_draft_matches_the_unified_conversion_settings_contract()
    {
        var draft = ConversionSettingsDraft.CreateDefault(@"C:\Books\Output", @"C:\Tools\kindlegen_v2.9.exe");

        Assert.Equal("mobi", draft.Profile.OutputFormat);
        Assert.Equal(ConversionConcurrencyPolicy.Auto, draft.Profile.Parallelism);
        Assert.Equal(OutputCollisionPolicy.AutoRename, draft.CollisionPolicy);
        Assert.True(draft.AutoOpenOutputDirectory);
        Assert.False(draft.AutoOpenTaskCenter);
        Assert.Equal(@"C:\Tools\kindlegen_v2.9.exe", draft.Profile.Options.Mobi.KindleGenPath);
    }

    [Fact]
    public void Legacy_import_updates_supported_conversion_values_but_keeps_machine_kindlegen_path()
    {
        var original = ConversionSettingsDraft.CreateDefault(@"C:\Old", @"C:\Machine\kindlegen_v2.9.exe") with
        {
            CollisionPolicy = OutputCollisionPolicy.Skip,
        };
        var import = new LegacyConfigImport(
            @"C:\Legacy\config.xml",
            @"C:\Imported",
            LegacyOutputFormat.Epub,
            new ConversionOptions
            {
                FontSizePercent = 125,
                Mobi = new MobiOptions
                {
                    KindleGenPath = @"D:\Legacy\kindlegen.exe",
                    Compression = MobiCompression.High,
                    StripSourceArchive = false,
                },
            },
            false,
            ["字号"],
            []);

        var result = original.ApplyLegacyConfig(import, @"C:\Machine\kindlegen_v2.9.exe");

        Assert.Equal("epub", result.Profile.OutputFormat);
        Assert.Equal(125, result.Profile.Options.FontSizePercent);
        Assert.Equal(MobiCompression.High, result.Profile.Options.Mobi.Compression);
        Assert.Equal(@"C:\Machine\kindlegen_v2.9.exe", result.Profile.Options.Mobi.KindleGenPath);
        Assert.Equal(OutputCollisionPolicy.Skip, result.CollisionPolicy);
        Assert.Equal(@"C:\Legacy\config.xml", result.LegacyConfigPath);
    }

    /// <summary>
    /// **别人机器上的输出目录不许被采纳。**
    ///
    /// <para>随包那份 `config.xml` 是原版文件原样（`LegacyCompatibilityTests` 逐值比对锁着这一点），
    /// 里面存的是原作者的桌面路径。原版文件不该为了这个被手改，所以判断放在采纳这一侧：
    /// **目录在本机不存在就忽略**，否则用户导入一份示例配置，输出目录就变成了别人的桌面。</para>
    ///
    /// <para>⚠️ 路径必须**保证**不存在，不能拿"原作者的桌面"当反例 ——
    /// 在作者本机上那个目录是存在的，测试会随机器而变（第一版就是这么写错的，实测红）。
    /// 这里用临时目录下的一个随机名。</para>
    /// </summary>
    [Fact]
    public void Legacy_import_does_not_adopt_an_output_directory_that_is_not_on_this_machine()
    {
        var original = ConversionSettingsDraft.CreateDefault(@"C:\Old", @"C:\Machine\kindlegen_v2.9.exe");
        var notOnThisMachine = Path.Combine(Path.GetTempPath(), $"easypub-absent-{Guid.NewGuid():N}", "Desktop");
        Assert.False(Directory.Exists(notOnThisMachine));

        var result = original.ApplyLegacyConfig(Import(notOnThisMachine), @"C:\Machine\kindlegen_v2.9.exe");

        Assert.Equal(@"C:\Old", result.OutputDirectory);
    }

    /// <summary>
    /// 反方向：目录真的存在就采纳 —— 用户导入自己那份配置是正常用法，不能一起挡掉。
    /// </summary>
    [Fact]
    public void Legacy_import_adopts_an_output_directory_that_really_exists()
    {
        var existing = Path.Combine(Path.GetTempPath(), $"easypub-out-{Guid.NewGuid():N}");
        Directory.CreateDirectory(existing);
        try
        {
            var original = ConversionSettingsDraft.CreateDefault(@"C:\Old", @"C:\Machine\kindlegen_v2.9.exe");

            var result = original.ApplyLegacyConfig(Import(existing), @"C:\Machine\kindlegen_v2.9.exe");

            Assert.Equal(existing, result.OutputDirectory);
        }
        finally { Directory.Delete(existing, recursive: true); }
    }

    private static LegacyConfigImport Import(string outputDirectory) => new(
        @"C:\Legacy\config.xml",
        outputDirectory,
        LegacyOutputFormat.Epub,
        new ConversionOptions(),
        false,
        [],
        []);

    [Fact]
    public async Task Missing_kindlegen_returns_a_real_missing_health_result()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-kindlegen-{Guid.NewGuid():N}.exe");

        var result = await KindleGenHealthProbe.ProbeAsync(path);

        Assert.Equal(KindleGenHealthStatus.Missing, result.Status);
        Assert.False(result.IsReady);
    }
}
