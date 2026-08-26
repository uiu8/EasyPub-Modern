using System.IO;
using System.Windows.Media.Imaging;

namespace EasyPub.Desktop;

public sealed record ImageDiagnosticResult(int Width, int Height, string Format, long Bytes, bool KindleReady, string Message)
{
    public string Summary => $"{Width} × {Height} · {Format} · {Bytes / 1024d:F0} KB · {(KindleReady ? "适合 Kindle" : Message)}";
}

public static class ImageDiagnostics
{
    public static ImageDiagnosticResult Inspect(string path, bool cover)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var format = decoder.CodecInfo?.FriendlyName?.Replace("File Format", string.Empty, StringComparison.OrdinalIgnoreCase).Trim() ?? Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        var minimumWidth = cover ? 625 : 320;
        var ratio = frame.PixelHeight == 0 ? 0 : (double)frame.PixelWidth / frame.PixelHeight;
        var ready = frame.PixelWidth >= minimumWidth && frame.PixelHeight >= 480 && (!cover || ratio is >= 0.55 and <= 0.8);
        var message = frame.PixelWidth < minimumWidth ? "分辨率偏低" : cover && ratio is not (>= 0.55 and <= 0.8) ? "封面长宽比偏离常见电子书比例" : "尺寸偏小";
        return new ImageDiagnosticResult(frame.PixelWidth, frame.PixelHeight, format, stream.Length, ready, message);
    }
}
