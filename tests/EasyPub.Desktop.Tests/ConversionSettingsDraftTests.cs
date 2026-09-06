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

    [Fact]
    public async Task Missing_kindlegen_returns_a_real_missing_health_result()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-kindlegen-{Guid.NewGuid():N}.exe");

        var result = await KindleGenHealthProbe.ProbeAsync(path);

        Assert.Equal(KindleGenHealthStatus.Missing, result.Status);
        Assert.False(result.IsReady);
    }
}
