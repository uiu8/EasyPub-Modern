using System.Diagnostics;

namespace EasyPub.Core;

public sealed class EasyPubConverter
{
    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        CancellationToken cancellationToken = default)
        => await ConvertAsync(request, progress: null, cancellationToken).ConfigureAwait(false);

    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        IProgress<ConversionProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        var extension = Path.GetExtension(request.OutputPath);
        var result = string.Equals(extension, ".epub", StringComparison.OrdinalIgnoreCase)
            ? await LegacyEpubWriter.WriteAsync(request, cancellationToken, progress)
            : string.Equals(extension, ".mobi", StringComparison.OrdinalIgnoreCase)
                ? await LegacyMobiWriter.WriteAsync(request, cancellationToken, progress)
                : throw new NotSupportedException("Output extension must be .epub or .mobi.");
        stopwatch.Stop();

        return new ConversionResult(
            Path.GetFullPath(request.InputPath),
            Path.GetFullPath(request.OutputPath),
            result.ChapterCount,
            result.OutputBytes,
            stopwatch.Elapsed);
    }
}
