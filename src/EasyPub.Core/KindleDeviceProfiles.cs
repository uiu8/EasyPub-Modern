namespace EasyPub.Core;

public sealed record KindleDeviceProfile(
    string Id,
    string DisplayName,
    int PixelWidth,
    int PixelHeight,
    int Ppi,
    int ViewportWidth,
    int ViewportHeight);

public static class KindleDeviceProfiles
{
    public static IReadOnlyList<KindleDeviceProfile> BuiltIn { get; } =
    [
        new("kpw3", "KPW3 / Paperwhite 第 7 代（6 英寸）", 1072, 1448, 300, 360, 486),
        new("kpw4", "KPW4 / Paperwhite 第 10 代（6 英寸）", 1072, 1448, 300, 360, 486),
        new("kpw5", "KPW5 / Paperwhite 第 11 代（6.8 英寸）", 1236, 1648, 300, 390, 520),
        new("kpw6", "KPW6 / Paperwhite 第 12 代（7 英寸）", 1264, 1680, 300, 400, 532),
        new("basic10", "Kindle 基础款第 10 代（6 英寸）", 600, 800, 167, 360, 480),
        new("basic11", "Kindle 基础款第 11 代（6 英寸）", 1072, 1448, 300, 360, 486),
        new("voyage", "Kindle Voyage（6 英寸）", 1080, 1440, 300, 360, 480),
        new("oasis1", "Kindle Oasis 1（6 英寸）", 1080, 1440, 300, 360, 480),
        new("oasis2", "Kindle Oasis 2（7 英寸）", 1264, 1680, 300, 400, 532),
        new("oasis3", "Kindle Oasis 3（7 英寸）", 1264, 1680, 300, 400, 532),
        new("scribe", "Kindle Scribe（10.2 英寸）", 1860, 2480, 300, 465, 620),
        new("colorsoft", "Kindle Colorsoft（7 英寸）", 1264, 1680, 300, 400, 532),
    ];

    public static KindleDeviceProfile Custom(int width, int height, int ppi)
    {
        if (width is < 320 or > 5000 || height is < 320 or > 7000 || ppi is < 72 or > 600)
            throw new ArgumentOutOfRangeException(nameof(width), "自定义设备宽高必须为 320–7000 像素，PPI 必须为 72–600。");
        const int previewLongEdge = 540;
        var scale = (double)previewLongEdge / Math.Max(width, height);
        return new KindleDeviceProfile("custom", $"自定义 · {width} × {height} · {ppi} PPI", width, height, ppi,
            Math.Max(240, (int)Math.Round(width * scale)), Math.Max(320, (int)Math.Round(height * scale)));
    }
}
