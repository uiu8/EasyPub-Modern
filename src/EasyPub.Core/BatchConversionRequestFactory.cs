namespace EasyPub.Core;

public sealed record BookConversionSource(
    string InputPath,
    string? CoverImagePath = null,
    string? Title = null,
    string? Author = null,
    IReadOnlyList<BookIllustration>? Illustrations = null);

public static class BatchConversionRequestFactory
{
    public static IReadOnlyList<ConversionRequest> Create(
        IEnumerable<BookConversionSource> sources,
        string outputDirectory,
        string outputFormat,
        string? author,
        ConversionOptions baseOptions)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(baseOptions);
        var extension = outputFormat.ToLowerInvariant() switch
        {
            "epub" => ".epub",
            "mobi" => ".mobi",
            _ => throw new ArgumentException("输出格式只能是 epub 或 mobi。", nameof(outputFormat)),
        };

        return sources.Select(source => new ConversionRequest(
            source.InputPath,
            Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(source.InputPath) + extension),
            Title: NormalizeOptional(source.Title),
            Author: NormalizeOptional(source.Author) ?? NormalizeOptional(author),
            Options: baseOptions with
            {
                CoverImagePath = source.CoverImagePath,
                Illustrations = source.Illustrations ?? [],
            }))
            .ToArray();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
