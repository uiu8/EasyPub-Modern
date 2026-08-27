using System.Text;

namespace EasyPub.Core;

internal sealed record DecodedText(string Text, Encoding Encoding, byte[] Preamble);

internal static class TextFileDecoder
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Gbk;
    private static readonly Encoding[] BomEncodings =
    [
        Encoding.UTF32,
        new UTF32Encoding(bigEndian: true, byteOrderMark: true),
        Encoding.UTF8,
        Encoding.Unicode,
        Encoding.BigEndianUnicode,
    ];

    static TextFileDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gbk = Encoding.GetEncoding(936);
    }

    public static DecodedText Decode(byte[] bytes, TextEncodingMode mode)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (mode == TextEncodingMode.Utf8) return DecodeWithEncoding(bytes, StrictUtf8);
        if (mode == TextEncodingMode.Gbk) return new DecodedText(Gbk.GetString(bytes), Gbk, []);

        foreach (var encoding in BomEncodings)
        {
            if (bytes.AsSpan().StartsWith(encoding.Preamble))
                return DecodeWithEncoding(bytes, encoding);
        }

        try
        {
            return new DecodedText(StrictUtf8.GetString(bytes), new UTF8Encoding(false), []);
        }
        catch (DecoderFallbackException)
        {
            return new DecodedText(Gbk.GetString(bytes), Gbk, []);
        }
    }

    private static DecodedText DecodeWithEncoding(byte[] bytes, Encoding encoding)
    {
        var expectedPreamble = encoding.GetPreamble();
        var preamble = bytes.AsSpan().StartsWith(expectedPreamble) ? expectedPreamble : [];
        return new DecodedText(
            encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length),
            encoding,
            preamble);
    }
}
