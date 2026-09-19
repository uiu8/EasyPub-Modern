using System.IO;
using EasyPub.Core;

namespace EasyPub.Desktop;

public sealed record ConversionSettingsDraft(
    string OutputDirectory,
    ConversionProfile Profile,
    OutputCollisionPolicy CollisionPolicy,
    bool AutoOpenOutputDirectory,
    bool AutoOpenTaskCenter,
    string? LegacyConfigPath)
{
    public static ConversionSettingsDraft CreateDefault(string outputDirectory, string? kindleGenPath = null)
    {
        var profile = ConversionProfile.Default with
        {
            OutputFormat = "mobi",
            Parallelism = ConversionConcurrencyPolicy.Auto,
            Options = ConversionProfile.Default.Options with
            {
                Mobi = ConversionProfile.Default.Options.Mobi with
                {
                    KindleGenPath = NormalizeOptional(kindleGenPath),
                },
            },
        };
        return new ConversionSettingsDraft(
            outputDirectory,
            profile,
            OutputCollisionPolicy.AutoRename,
            AutoOpenOutputDirectory: true,
            AutoOpenTaskCenter: false,
            LegacyConfigPath: null);
    }

    public ConversionSettingsDraft ApplyLegacyConfig(LegacyConfigImport import, string? machineKindleGenPath)
    {
        ArgumentNullException.ThrowIfNull(import);
        var imported = import.Options;
        var current = Profile.Options;
        var merged = current with
        {
            ChapterPattern = imported.ChapterPattern,
            RemoveBlankLines = imported.RemoveBlankLines,
            AddFullWidthIndent = imported.AddFullWidthIndent,
            FullWidthIndentCount = imported.FullWidthIndentCount,
            ParagraphIndentEm = imported.ParagraphIndentEm,
            FontSizePercent = imported.FontSizePercent,
            LineHeightPercent = imported.LineHeightPercent,
            ParagraphSpacingEm = imported.ParagraphSpacingEm,
            PageMarginTopPx = imported.PageMarginTopPx,
            PageMarginBottomPx = imported.PageMarginBottomPx,
            PageMarginLeftPx = imported.PageMarginLeftPx,
            PageMarginRightPx = imported.PageMarginRightPx,
            TextAlignment = imported.TextAlignment,
            Mobi = current.Mobi with
            {
                KindleGenPath = NormalizeOptional(machineKindleGenPath) ?? current.Mobi.KindleGenPath,
                Compression = imported.Mobi.Compression,
                StripSourceArchive = imported.Mobi.StripSourceArchive,
                EnableReadingProgressSync = imported.Mobi.EnableReadingProgressSync,
                Asin = imported.Mobi.Asin,
                ExtraArguments = imported.Mobi.ExtraArguments,
            },
        };
        return this with
        {
            // **输出目录是"这台机器上的路径"，不该跟着别人的 config.xml 过来。**
            //
            // 随包那份 config.xml 是**原版文件原样**（`LegacyCompatibilityTests` 逐值比对锁着这一点），
            // 里面存的是原作者的桌面路径。谁导入它，输出目录就会变成别人的桌面 —— 而原版文件
            // 不该为了这个被手改，所以判断放在**采纳这一侧**。
            //
            // 判据是"这个目录在本机存不存在"：存在就采纳（用户导入自己那份配置的正常场景），
            // 不存在或为空就保留原来的输出目录。上面对 MOBI 的处理已经是同样的思路 ——
            // KindleGen 路径也是按机器解析的。
            OutputDirectory = Directory.Exists(import.OutputDirectory)
                ? import.OutputDirectory
                : OutputDirectory,
            Profile = Profile with
            {
                OutputFormat = import.OutputFormat == LegacyOutputFormat.Epub ? "epub" : "mobi",
                Mode = ConversionMode.OriginalCompatible,
                Options = merged,
            },
            LegacyConfigPath = import.SourcePath,
        };
    }

    public ConversionSettingsDraft Normalize(string? machineKindleGenPath)
    {
        var normalizedFormat = string.Equals(Profile.OutputFormat, "epub", StringComparison.OrdinalIgnoreCase)
            ? "epub"
            : "mobi";
        var normalizedMobi = Profile.Options.Mobi with
        {
            KindleGenPath = NormalizeOptional(machineKindleGenPath) ?? Profile.Options.Mobi.KindleGenPath,
            Asin = NormalizeOptional(Profile.Options.Mobi.Asin),
            ExtraArguments = NormalizeOptional(Profile.Options.Mobi.ExtraArguments),
        };
        return this with
        {
            OutputDirectory = Path.GetFullPath(OutputDirectory),
            Profile = Profile with
            {
                OutputFormat = normalizedFormat,
                Parallelism = Math.Clamp(Profile.Parallelism, 0, ConversionConcurrencyPolicy.Maximum),
                Options = Profile.Options with { Mobi = normalizedMobi },
            },
            LegacyConfigPath = NormalizeOptional(LegacyConfigPath),
        };
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record ConversionSettingsContext(
    string KindleGenPath,
    int SelectedBookCount,
    int SelectedEpubCount,
    IReadOnlyList<NamedConversionPreset> Presets,
    string DefaultOutputDirectory);
