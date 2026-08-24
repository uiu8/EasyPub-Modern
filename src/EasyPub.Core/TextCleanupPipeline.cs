using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record TextCleanupChange(
    int LineNumber,
    string Rule,
    string Before,
    string After);

public sealed record TextCleanupPreview(
    IReadOnlyList<string> Lines,
    IReadOnlyList<TextCleanupChange> Changes)
{
    public string Text => string.Join(Environment.NewLine, Lines.Where(line => line != TextCleanupPipeline.RemovedLine));
}

public static partial class TextCleanupPipeline
{
    internal const string RemovedLine = "\u001fEasyPubRemovedLine";

    public static bool IsRemovedLine(string line) => line == RemovedLine;

    public static TextCleanupPreview Apply(string text, TextCleanupOptions? options)
    {
        options ??= new TextCleanupOptions();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var result = lines.ToArray();
        var changes = new List<TextCleanupChange>();

        for (var index = 0; index < result.Length; index++)
        {
            var original = result[index];
            var current = original;

            if (options.NormalizeFullWidthSpaces)
                current = NormalizeSpaces(current);
            if (options.NormalizeChapterNumbers && ChapterTitleNormalizer.TryNormalizeNumericTitle(current, out var title))
                current = title;
            if (options.ChineseVariant != ChineseVariantConversion.None)
                current = ChineseVariantMapper.Convert(current, options.ChineseVariant);
            if (options.NormalizePunctuation)
                current = NormalizeChinesePunctuation(current);

            if (!string.Equals(original, current, StringComparison.Ordinal))
                changes.Add(new TextCleanupChange(index + 1, DescribeInlineRules(original, current, options), original, current));
            result[index] = current;
        }

        if (options.RemoveSiteNotices)
        {
            for (var index = 0; index < result.Length; index++)
            {
                if (result[index] == RemovedLine || !SiteNoticePattern().IsMatch(result[index].Trim())) continue;
                changes.Add(new TextCleanupChange(index + 1, "清理网站广告/下载说明", result[index], string.Empty));
                result[index] = RemovedLine;
            }
        }

        if (options.RepairHardWraps)
        {
            for (var index = 0; index < result.Length - 1; index++)
            {
                var nextIndex = index + 1;
                while (nextIndex < result.Length && ShouldJoin(result[index], result[nextIndex]))
                {
                    var before = result[index] + " ↵ " + result[nextIndex];
                    result[index] = result[index].TrimEnd() + result[nextIndex].TrimStart();
                    result[nextIndex] = RemovedLine;
                    changes.Add(new TextCleanupChange(index + 1, "修复正文硬换行", before, result[index]));
                    nextIndex++;
                }
            }
        }

        if (options.CollapseBlankLines)
        {
            var seenBlank = false;
            for (var index = 0; index < result.Length; index++)
            {
                if (result[index] == RemovedLine) continue;
                if (!string.IsNullOrWhiteSpace(result[index]))
                {
                    seenBlank = false;
                    continue;
                }
                if (!seenBlank)
                {
                    seenBlank = true;
                    continue;
                }
                changes.Add(new TextCleanupChange(index + 1, "合并连续空行", result[index], string.Empty));
                result[index] = RemovedLine;
            }
        }

        return new TextCleanupPreview(result, changes.OrderBy(change => change.LineNumber).ToArray());
    }

    public static async Task<string> ReadFileAsync(
        string path,
        TextEncodingMode encodingMode,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding encoding;
        if (encodingMode == TextEncodingMode.Utf8) encoding = new UTF8Encoding(false, true);
        else if (encodingMode == TextEncodingMode.Gbk) encoding = Encoding.GetEncoding(936);
        else if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble)) encoding = Encoding.UTF8;
        else
        {
            try { _ = new UTF8Encoding(false, true).GetString(bytes); encoding = new UTF8Encoding(false); }
            catch (DecoderFallbackException) { encoding = Encoding.GetEncoding(936); }
        }
        var preamble = encoding.GetPreamble();
        return encoding.GetString(bytes.AsSpan().StartsWith(preamble) ? bytes[preamble.Length..] : bytes);
    }

    private static string NormalizeSpaces(string value)
    {
        var normalized = value.Replace('\u00a0', ' ');
        normalized = FullWidthSpaceRun().Replace(normalized, match => match.Index == 0 ? "　　" : " ");
        return normalized.TrimEnd();
    }

    private static string NormalizeChinesePunctuation(string value)
    {
        if (value.Contains("http://", StringComparison.OrdinalIgnoreCase)
            || value.Contains("https://", StringComparison.OrdinalIgnoreCase)) return value;
        return value
            .Replace(',', '，').Replace('!', '！').Replace('?', '？')
            .Replace(';', '；').Replace(':', '：')
            .Replace("...", "……", StringComparison.Ordinal)
            .Replace("““", "“", StringComparison.Ordinal)
            .Replace("””", "”", StringComparison.Ordinal);
    }

    private static bool ShouldJoin(string current, string next)
    {
        if (current == RemovedLine || next == RemovedLine) return false;
        current = current.Trim();
        next = next.Trim();
        if (current.Length < 8 || next.Length == 0) return false;
        if (ChapterLinePattern().IsMatch(current) || ChapterLinePattern().IsMatch(next)) return false;
        if ("。！？!?；;：:…》）)]”’\"".Contains(current[^1])) return false;
        return char.IsLetterOrDigit(current[^1]) && (char.IsLetterOrDigit(next[0]) || "“‘\"（(".Contains(next[0]));
    }

    private static string DescribeInlineRules(string before, string after, TextCleanupOptions options)
    {
        if (options.NormalizeChapterNumbers && ChapterTitleNormalizer.TryNormalizeNumericTitle(before, out var title)
            && string.Equals(title, after, StringComparison.Ordinal)) return "标准化章节编号";
        if (options.ChineseVariant != ChineseVariantConversion.None)
            return options.ChineseVariant == ChineseVariantConversion.ToSimplified ? "繁体转简体" : "简体转繁体";
        if (options.NormalizePunctuation) return "规范中文标点";
        return "统一全角空格";
    }

    [GeneratedRegex("[　\\u00a0]+")]
    private static partial Regex FullWidthSpaceRun();

    [GeneratedRegex(@"^\\s*(?:第[0-9一二三四五六七八九十百千零〇两]+[章回卷部篇集节]|\\d{1,6}(?:\\s+|[.．、:_：\\-—])).*")]
    private static partial Regex ChapterLinePattern();

    [GeneratedRegex(@"(?:本书来自|更多精彩.*访问|请记住本站|最新网址|手机用户请浏览|下载本书|txt电子书|小说下载|加入书签|投推荐票|章节错误.*举报|广告位)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SiteNoticePattern();
}

internal static class ChineseVariantMapper
{
    private const uint Simplified = 0x02000000;
    private const uint Traditional = 0x04000000;

    public static string Convert(string value, ChineseVariantConversion conversion)
    {
        if (string.IsNullOrEmpty(value) || conversion == ChineseVariantConversion.None) return value;
        if (!OperatingSystem.IsWindows()) return value;
        var destination = new StringBuilder(value.Length * 2 + 8);
        var flag = conversion == ChineseVariantConversion.ToSimplified ? Simplified : Traditional;
        var length = LCMapStringEx("zh-CN", flag, value, value.Length, destination, destination.Capacity, nint.Zero, nint.Zero, 0);
        return length == 0 ? value : destination.ToString(0, length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string localeName,
        uint mapFlags,
        string source,
        int sourceLength,
        StringBuilder destination,
        int destinationLength,
        nint versionInformation,
        nint reserved,
        nint sortHandle);
}
