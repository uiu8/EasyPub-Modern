using SkiaSharp;

namespace EasyPub.Core;

public sealed record PreparedCoverImage(
    byte[] JpegBytes,
    int PixelWidth,
    int PixelHeight,
    string SourceFormat,
    bool WasConverted);

public static class CoverImageConverter
{
    public static async Task<PreparedCoverImage> PrepareJpegAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到封面图片。", fullPath);

        var sourceBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken);
        using var sourceData = SKData.CreateCopy(sourceBytes);
        using var codec = SKCodec.Create(sourceData)
            ?? throw new InvalidDataException("无法识别封面图片格式。支持 JPG、JPEG、PNG 和 WebP。");
        var format = codec.EncodedFormat;
        if (format is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
            throw new NotSupportedException("封面图片只支持 JPG、JPEG、PNG 和 WebP。");

        if (format == SKEncodedImageFormat.Jpeg)
        {
            return new PreparedCoverImage(sourceBytes, codec.Info.Width, codec.Info.Height, "JPEG", false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var source = SKBitmap.Decode(sourceData)
            ?? throw new InvalidDataException("封面图片解码失败。");
        using var flattened = new SKBitmap(
            new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(flattened))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions());
            canvas.Flush();
        }

        using var image = SKImage.FromBitmap(flattened);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 100)
            ?? throw new InvalidOperationException("无法将封面图片编码为 JPG。");
        return new PreparedCoverImage(
            jpeg.ToArray(),
            source.Width,
            source.Height,
            format == SKEncodedImageFormat.Webp ? "WEBP" : "PNG",
            true);
    }
}
