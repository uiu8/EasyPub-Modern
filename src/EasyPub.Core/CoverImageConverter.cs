using System.Collections.Concurrent;
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
    private const int CacheCapacity = 8;
    private static readonly ConcurrentDictionary<CoverCacheKey, Lazy<Task<PreparedCoverImage>>> Cache = new();
    private static readonly ConcurrentQueue<CoverCacheKey> CacheOrder = new();
    private static readonly SemaphoreSlim DecodeGate = new(3, 3);

    public static async Task<PreparedCoverImage> PrepareJpegAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到封面图片。", fullPath);

        var info = new FileInfo(fullPath);
        var key = new CoverCacheKey(fullPath, info.Length, info.LastWriteTimeUtc.Ticks);
        var candidate = new Lazy<Task<PreparedCoverImage>>(
            () => PrepareCoreAsync(fullPath),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var cached = Cache.GetOrAdd(key, candidate);
        if (ReferenceEquals(candidate, cached))
        {
            CacheOrder.Enqueue(key);
            TrimCache();
        }

        try
        {
            return await cached.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelling one preview must not abort a shared conversion used by a writer or validator.
            throw;
        }
        catch
        {
            Cache.TryRemove(new KeyValuePair<CoverCacheKey, Lazy<Task<PreparedCoverImage>>>(key, cached));
            throw;
        }
    }

    private static async Task<PreparedCoverImage> PrepareCoreAsync(string fullPath)
    {
        await DecodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var sourceBytes = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
            using var sourceData = SKData.CreateCopy(sourceBytes);
            using var codec = SKCodec.Create(sourceData)
                ?? throw new InvalidDataException("无法识别封面图片格式。支持 JPG、JPEG、PNG 和 WebP。");
            var format = codec.EncodedFormat;
            if (format is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
                throw new NotSupportedException("封面图片只支持 JPG、JPEG、PNG 和 WebP。");

            if (format == SKEncodedImageFormat.Jpeg)
                return new PreparedCoverImage(sourceBytes, codec.Info.Width, codec.Info.Height, "JPEG", false);

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
        finally
        {
            DecodeGate.Release();
        }
    }

    private static void TrimCache()
    {
        while (Cache.Count > CacheCapacity && CacheOrder.TryDequeue(out var oldest))
            Cache.TryRemove(oldest, out _);
    }

    private sealed record CoverCacheKey(string FullPath, long Length, long LastWriteTicks);
}
