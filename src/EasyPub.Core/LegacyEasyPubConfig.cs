using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace EasyPub.Core;

public enum LegacyOutputFormat
{
    Epub,
    Mobi,
    Azw3,
}

public sealed record LegacyConfigImport(
    string SourcePath,
    string OutputDirectory,
    LegacyOutputFormat OutputFormat,
    ConversionOptions Options,
    bool AlwaysOnTop,
    IReadOnlyList<string> AppliedSettings,
    IReadOnlyList<string> UnsupportedSettings);

/// <summary>
/// Imports the settings that affect output from an EasyPub v1.50 config.xml.
/// The source file is read-only and is never rewritten.
/// </summary>
public static class LegacyEasyPubConfig
{
    public static LegacyConfigImport Load(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var sourcePath = Path.GetFullPath(configPath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("找不到原版 EasyPub 配置文件。", sourcePath);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(sourcePath, settings);
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root?.Name.LocalName != "EasyPubConfig")
            throw new InvalidDataException("该文件不是 EasyPub config.xml。缺少 EasyPubConfig 根节点。");

        var recent = root.Element("RecentOptions")
            ?? throw new InvalidDataException("EasyPub config.xml 缺少 RecentOptions。\n");
        var advanced = root.Element("AdvancedOptions")
            ?? throw new InvalidDataException("EasyPub config.xml 缺少 AdvancedOptions。\n");

        var applied = new List<string>();
        var unsupported = new List<string>();

        var outputDirectory = Text(recent, "outputfolder", string.Empty);
        var outputFormatIndex = Integer(advanced, "outputformat", 0);
        var outputFormat = outputFormatIndex switch
        {
            1 => LegacyOutputFormat.Mobi,
            2 => LegacyOutputFormat.Azw3,
            _ => LegacyOutputFormat.Epub,
        };
        if (outputFormat == LegacyOutputFormat.Azw3)
            unsupported.Add("AZW3 输出：新版当前只提供 EPUB/MOBI，界面将回退到 MOBI");

        var alignmentIndex = Integer(recent, "textalign", 0);
        var alignment = alignmentIndex switch
        {
            1 => TextAlignment.Justify,
            2 => TextAlignment.Left,
            3 => TextAlignment.Right,
            4 => TextAlignment.Center,
            _ => TextAlignment.Default,
        };

        var top = PageMargin(recent, "top", "pagetopunit", unsupported);
        var bottom = PageMargin(recent, "bottom", "pagebottomunit", unsupported);
        var left = PageMargin(recent, "left", "pageleftunit", unsupported);
        var right = PageMargin(recent, "right", "pagerightunit", unsupported);
        var paragraphSpacing = Number(recent, "margintop", 0.6);
        if (Integer(recent, "margintopunit", 2) != 2)
            unsupported.Add("段间距单位不是 em：新版暂按数值作为 em 应用");

        var addSpace = Flag(recent, "addspace", true);
        var addSpaceCount = Integer(recent, "addspacecount", 2);
        if (addSpace && addSpaceCount != 2)
            unsupported.Add($"段首全角空格数量={addSpaceCount}：新版当前固定为两个");

        var kindleGenName = Text(advanced, "kindlegenexe", "kindlegen_v2.9.exe");
        var kindleGenPath = ResolveKindleGen(sourcePath, kindleGenName);
        var compressionValue = Math.Clamp(Integer(advanced, "kindlegencompress", 1), 0, 2);
        var chapterPattern = NullIfBlank(Text(recent, "full_reg", string.Empty));

        var options = new ConversionOptions
        {
            ChapterPattern = chapterPattern,
            RemoveBlankLines = Flag(recent, "removeblankline", true),
            AddFullWidthIndent = addSpace,
            ParagraphIndentEm = Number(recent, "indent", 0),
            FontSizePercent = Integer(recent, "fontsize", 110),
            LineHeightPercent = Integer(recent, "lineheight", 120),
            ParagraphSpacingEm = paragraphSpacing,
            PageMarginTopPx = top,
            PageMarginBottomPx = bottom,
            PageMarginLeftPx = left,
            PageMarginRightPx = right,
            TextAlignment = alignment,
            Mobi = new MobiOptions
            {
                KindleGenPath = kindleGenPath,
                Compression = (MobiCompression)compressionValue,
                StripSourceArchive = Flag(advanced, "mobistrip", true),
                EnableReadingProgressSync = Flag(advanced, "mobisync", true),
                Asin = Integer(advanced, "asinstyle", 0) == 1
                    ? NullIfBlank(Text(advanced, "mobiasin", string.Empty))
                    : null,
                ExtraArguments = NullIfBlank(Text(advanced, "kindlegenoption", string.Empty)),
            },
        };

        applied.AddRange([
            $"输出目录={outputDirectory}",
            $"输出格式={outputFormat.ToString().ToUpperInvariant()}",
            "章节标题正则",
            "字号、行高、段间距与首行缩进",
            "页面四边距及单位",
            "空行清理与段首全角空格",
            $"文本对齐={alignment}",
            "KindleGen 路径、压缩级别、附加参数与源档移除",
            $"阅读进度同步={options.Mobi.EnableReadingProgressSync}，ASIN={(options.Mobi.Asin ?? "随机")}",
            $"窗口置顶={Flag(advanced, "alwaysontop", false)}",
        ]);

        unsupported.AddRange([
            "按章节数量拆分文件（splitmode/splitcount）",
            "字体设备、嵌入与子集化设置",
            "封面样式、封面字体与强制文字封面",
            "外部编辑器、CSS 覆盖及单独保存 CSS",
            "简易正则组合、附加正则和正则预设列表",
            "原版窗口位置、自动标记位置",
            "静默模式、原始 HTML 标签、临时目录",
            "流大小、屏幕尺寸、目录留白与空章节样式",
            "MOBI 期刊、语言强制及格式细项",
            "输出到源文件目录（outputtosrc）",
        ]);

        return new LegacyConfigImport(
            sourcePath,
            outputDirectory,
            outputFormat,
            options,
            Flag(advanced, "alwaysontop", false),
            applied,
            unsupported);
    }

    private static int PageMargin(XElement parent, string valueName, string unitName, ICollection<string> unsupported)
    {
        var value = Number(parent, valueName, valueName is "left" or "right" ? 3 : 0);
        var unit = Integer(parent, unitName, 0);
        if (unit != 0)
            unsupported.Add($"页面边距 {valueName} 使用非 px 单位：新版暂按 px 数值应用");
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static string ResolveKindleGen(string configPath, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath)) return Path.GetFullPath(configuredPath);

        var configDirectory = Path.GetDirectoryName(configPath)!;
        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var candidates = new[]
        {
            Path.Combine(configDirectory, "bin", configuredPath),
            Path.Combine(configDirectory, configuredPath),
            Path.Combine(desktopDirectory, "easypub", "bin", configuredPath),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private static string Text(XElement parent, string name, string fallback) =>
        parent.Element(name)?.Value.Trim() ?? fallback;

    private static int Integer(XElement parent, string name, int fallback) =>
        int.TryParse(Text(parent, name, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static double Number(XElement parent, string name, double fallback) =>
        double.TryParse(Text(parent, name, string.Empty), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static bool Flag(XElement parent, string name, bool fallback) =>
        Integer(parent, name, fallback ? 1 : 0) != 0;

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
