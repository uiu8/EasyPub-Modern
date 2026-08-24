namespace EasyPub.Core;

public sealed record ConversionRequest(
    string InputPath,
    string OutputPath,
    string? Title = null,
    string? Author = null,
    ConversionOptions? Options = null)
{
    public ChapterTreePlan? ChapterTree { get; init; }
}

public sealed record ConversionOptions
{
    public static ConversionOptions LegacyDefault { get; } = new();

    public string? ChapterPattern { get; init; }
    public TocHierarchyOptions TocHierarchy { get; init; } = new();
    public TextEncodingMode TextEncoding { get; init; } = TextEncodingMode.Auto;
    public bool RemoveBlankLines { get; init; } = true;
    public bool AddFullWidthIndent { get; init; } = true;
    public double ParagraphIndentEm { get; init; }
    public int FontSizePercent { get; init; } = 110;
    public int LineHeightPercent { get; init; } = 120;
    public double ParagraphSpacingEm { get; init; } = 0.6;
    public int PageMarginTopPx { get; init; }
    public int PageMarginBottomPx { get; init; }
    public int PageMarginLeftPx { get; init; } = 3;
    public int PageMarginRightPx { get; init; } = 3;
    public TextAlignment TextAlignment { get; init; } = TextAlignment.Default;
    public string? AdditionalCss { get; init; }
    public string? CoverImagePath { get; init; }
    public IReadOnlyList<BookIllustration> Illustrations { get; init; } = [];
    public PublicationMetadata Metadata { get; init; } = new();
    public EmbeddedFontOptions Font { get; init; } = new();
    public MobiOptions Mobi { get; init; } = new();
    public TextCleanupOptions TextCleanup { get; init; } = new();
}

public sealed record TextCleanupOptions
{
    public bool CollapseBlankLines { get; init; }
    public bool RepairHardWraps { get; init; }
    public bool NormalizeFullWidthSpaces { get; init; }
    public bool NormalizeChapterNumbers { get; init; }
    public bool RemoveSiteNotices { get; init; }
    public ChineseVariantConversion ChineseVariant { get; init; }
    public bool NormalizePunctuation { get; init; }

    public bool Enabled => CollapseBlankLines || RepairHardWraps || NormalizeFullWidthSpaces
        || NormalizeChapterNumbers || RemoveSiteNotices
        || ChineseVariant != ChineseVariantConversion.None || NormalizePunctuation;
}

public enum ChineseVariantConversion
{
    None,
    ToSimplified,
    ToTraditional,
}

public sealed record TocHierarchyOptions
{
    public const string DefaultLevel1Pattern = @"^\s*第[0123456789一二三四五六七八九十零〇百千两]+[卷部篇集].*";
    public const string DefaultLevel2Pattern = @"^\s*第[0123456789一二三四五六七八九十零〇百千两]+[章回].*";
    public const string DefaultLevel3Pattern = @"^\s*第[0123456789一二三四五六七八九十零〇百千两]+节.*";

    public bool Enabled { get; init; }
    public string Level1Pattern { get; init; } = DefaultLevel1Pattern;
    public string Level2Pattern { get; init; } = DefaultLevel2Pattern;
    public string Level3Pattern { get; init; } = DefaultLevel3Pattern;
}

public sealed record PublicationMetadata
{
    public string? Translator { get; init; }
    public string? Isbn { get; init; }
    public DateOnly? PublicationDate { get; init; }
    public string? Publisher { get; init; }
    public string? Category { get; init; }
    public string Language { get; init; } = "zh-CN";
    public string? Description { get; init; }
}

public sealed record EmbeddedFontOptions
{
    public bool Enabled { get; init; }
    public string? FontPath { get; init; }
    public string? FamilyName { get; init; }
    public bool Subset { get; init; } = true;
}

public sealed record BookIllustration(
    string Marker,
    string ImagePath,
    string? AltText = null,
    int? InsertAfterLine = null);

public sealed record MobiOptions
{
    public string? KindleGenPath { get; init; }
    public MobiCompression Compression { get; init; } = MobiCompression.Standard;
    public bool StripSourceArchive { get; init; } = true;
    public bool EnableReadingProgressSync { get; init; } = true;
    public string? Asin { get; init; }
    public string? ExtraArguments { get; init; }
    public EpubInputMode EpubInputMode { get; init; } = EpubInputMode.PreserveOriginal;
}

public enum EpubInputMode
{
    PreserveOriginal,
    EasyPubCompatible,
}

public enum TextEncodingMode
{
    Auto,
    Utf8,
    Gbk,
}

public enum TextAlignment
{
    Default,
    Justify,
    Left,
    Center,
    Right,
}

public enum MobiCompression
{
    None = 0,
    Standard = 1,
    High = 2,
}

public sealed record ConversionResult(
    string InputPath,
    string OutputPath,
    int ChapterCount,
    long OutputBytes,
    TimeSpan Elapsed);
