using System.Globalization;
using EasyPub.Core;

if (args.Length < 2)
{
    PrintUsage();
    return 2;
}

try
{
    var outputDirectory = Path.GetFullPath(args[0]);
    var values = ParseArguments(args[1..]);
    Directory.CreateDirectory(outputDirectory);
    var css = values.CssFile is null ? null : await File.ReadAllTextAsync(values.CssFile);
    var options = new ConversionOptions
    {
        ChapterPattern = values.ChapterPattern,
        TextEncoding = values.Encoding,
        RemoveBlankLines = !values.KeepBlankLines,
        AddFullWidthIndent = !values.NoFullWidthIndent,
        ParagraphIndentEm = values.Indent,
        FontSizePercent = values.FontSize,
        LineHeightPercent = values.LineHeight,
        ParagraphSpacingEm = values.ParagraphSpacing,
        PageMarginTopPx = values.MarginTop,
        PageMarginBottomPx = values.MarginBottom,
        PageMarginLeftPx = values.MarginLeft,
        PageMarginRightPx = values.MarginRight,
        TextAlignment = values.Alignment,
        AdditionalCss = css,
        CoverImagePath = values.CoverImagePath,
        Metadata = new PublicationMetadata
        {
            Translator = values.Translator,
            Isbn = values.Isbn,
            PublicationDate = values.PublicationDate,
            Publisher = values.Publisher,
            Category = values.Category,
            Language = values.Language,
            Description = values.Description,
        },
        Font = new EmbeddedFontOptions
        {
            Enabled = values.FontPath is not null,
            FontPath = values.FontPath,
            FamilyName = values.FontFamily,
            Subset = !values.NoFontSubset,
        },
        Mobi = new MobiOptions
        {
            KindleGenPath = values.KindleGenPath,
            Compression = values.Compression,
            StripSourceArchive = !values.KeepSourceArchive,
            EnableReadingProgressSync = values.MobiSync,
            Asin = values.MobiAsin,
            ExtraArguments = values.KindleGenArguments,
        },
    };
    var extension = values.Format == "mobi" ? ".mobi" : ".epub";
    var requests = values.Inputs.Select(input => new ConversionRequest(
        input,
        Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(input) + extension),
        values.Title,
        values.Author,
        options));
    var results = await new BatchConverter(new EasyPubConverter()).ConvertAsync(requests, values.Parallelism);
    foreach (var result in results)
        Console.WriteLine($"OK\t{result.InputPath}\t{result.OutputPath}\t{result.ChapterCount}\t{result.OutputBytes}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

static CliValues ParseArguments(string[] arguments)
{
    var values = new CliValues();
    for (var index = 0; index < arguments.Length; index++)
    {
        var argument = arguments[index];
        string Next() => index + 1 < arguments.Length ? arguments[++index] : throw new ArgumentException($"选项 {argument} 缺少参数。");
        switch (argument)
        {
            case "--format": values.Format = Next().ToLowerInvariant(); break;
            case "--parallel": values.Parallelism = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--title": values.Title = Next(); break;
            case "--author": values.Author = Next(); break;
            case "--translator": values.Translator = Next(); break;
            case "--isbn": values.Isbn = Next(); break;
            case "--publication-date": values.PublicationDate = DateOnly.ParseExact(Next(), "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
            case "--publisher": values.Publisher = Next(); break;
            case "--category": values.Category = Next(); break;
            case "--language": values.Language = Next(); break;
            case "--description": values.Description = Next(); break;
            case "--chapter-regex": values.ChapterPattern = Next(); break;
            case "--encoding": values.Encoding = Enum.Parse<TextEncodingMode>(Next(), true); break;
            case "--font-size": values.FontSize = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--line-height": values.LineHeight = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--paragraph-spacing": values.ParagraphSpacing = double.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--indent": values.Indent = double.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--margin-top": values.MarginTop = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--margin-bottom": values.MarginBottom = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--margin-left": values.MarginLeft = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--margin-right": values.MarginRight = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--align": values.Alignment = Enum.Parse<TextAlignment>(Next(), true); break;
            case "--css-file": values.CssFile = Path.GetFullPath(Next()); break;
            case "--cover": values.CoverImagePath = Path.GetFullPath(Next()); break;
            case "--font": values.FontPath = Path.GetFullPath(Next()); break;
            case "--font-family": values.FontFamily = Next(); break;
            case "--no-font-subset": values.NoFontSubset = true; break;
            case "--kindlegen": values.KindleGenPath = Path.GetFullPath(Next()); break;
            case "--mobi-compression": values.Compression = (MobiCompression)int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "--no-mobi-sync": values.MobiSync = false; break;
            case "--mobi-asin": values.MobiAsin = Next(); break;
            case "--kindlegen-args": values.KindleGenArguments = Next(); break;
            case "--keep-blank-lines": values.KeepBlankLines = true; break;
            case "--no-fullwidth-indent": values.NoFullWidthIndent = true; break;
            case "--keep-source-archive": values.KeepSourceArchive = true; break;
            default:
                if (argument.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"未知选项：{argument}");
                values.Inputs.Add(Path.GetFullPath(argument));
                break;
        }
    }
    if (values.Format is not ("epub" or "mobi")) throw new ArgumentException("--format 只能是 epub 或 mobi。");
    if (values.Inputs.Count == 0) throw new ArgumentException("至少需要一个 TXT 输入文件。");
    return values;
}

static void PrintUsage()
{
    Console.Error.WriteLine("用法: EasyPub.Cli <输出目录> <input.txt> [input2.txt ...] [选项]");
    Console.Error.WriteLine("主要选项: --format epub|mobi --parallel N --title 标题 --author 作者");
    Console.Error.WriteLine("书籍信息: --translator 译者 --isbn ISBN --publication-date yyyy-MM-dd --publisher 出版社 --category 类别 --language zh-CN --description 简介");
    Console.Error.WriteLine("排版选项: --chapter-regex 正则 --encoding Auto|Utf8|Gbk --font-size N --line-height N --cover 图片路径");
    Console.Error.WriteLine("字体选项: --font 字体.ttf --font-family 字体名 --no-font-subset");
    Console.Error.WriteLine("MOBI选项: --kindlegen 路径 --mobi-compression 0|1|2 --mobi-asin B00XXXXXXX --no-mobi-sync --kindlegen-args 参数 --keep-source-archive");
}

sealed class CliValues
{
    public string Format { get; set; } = "epub";
    public int Parallelism { get; set; } = 1;
    public string? Title { get; set; }
    public string? Author { get; set; }
    public string? Translator { get; set; }
    public string? Isbn { get; set; }
    public DateOnly? PublicationDate { get; set; }
    public string? Publisher { get; set; }
    public string? Category { get; set; }
    public string Language { get; set; } = "zh-CN";
    public string? Description { get; set; }
    public string? ChapterPattern { get; set; }
    public TextEncodingMode Encoding { get; set; } = TextEncodingMode.Auto;
    public int FontSize { get; set; } = 110;
    public int LineHeight { get; set; } = 120;
    public double ParagraphSpacing { get; set; } = 0.6;
    public double Indent { get; set; }
    public int MarginTop { get; set; }
    public int MarginBottom { get; set; }
    public int MarginLeft { get; set; } = 3;
    public int MarginRight { get; set; } = 3;
    public TextAlignment Alignment { get; set; } = TextAlignment.Default;
    public bool KeepBlankLines { get; set; }
    public bool NoFullWidthIndent { get; set; }
    public string? CssFile { get; set; }
    public string? CoverImagePath { get; set; }
    public string? FontPath { get; set; }
    public string? FontFamily { get; set; }
    public bool NoFontSubset { get; set; }
    public string? KindleGenPath { get; set; }
    public MobiCompression Compression { get; set; } = MobiCompression.Standard;
    public bool MobiSync { get; set; } = true;
    public string? MobiAsin { get; set; }
    public bool KeepSourceArchive { get; set; }
    public string? KindleGenArguments { get; set; }
    public List<string> Inputs { get; } = [];
}
