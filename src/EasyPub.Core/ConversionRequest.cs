namespace EasyPub.Core;

public sealed record ConversionRequest(
    string InputPath,
    string OutputPath,
    string? Title = null,
    string? Author = null,
    ConversionOptions? Options = null);

public sealed record ConversionOptions
{
    public static ConversionOptions LegacyDefault { get; } = new();

    public string? ChapterPattern { get; init; }
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
