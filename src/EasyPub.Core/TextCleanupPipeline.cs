using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record TextCleanupChange(
    int LineNumber,
    string Rule,
    string Before,
    string After)
{
    public string Key { get; init; } = TextCleanupPipeline.CreateChangeKey(LineNumber, Rule, Before);
    public bool IsApplied { get; init; } = true;
}

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

    public static TextCleanupPreview Apply(
        string text,
        TextCleanupOptions? options,
        CancellationToken cancellationToken = default)
    {
        options ??= new TextCleanupOptions();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var result = lines.ToArray();
        var changes = new List<TextCleanupChange>();
        var exclusions = new HashSet<string>(options.ExcludedChangeKeys ?? [], StringComparer.Ordinal);

        for (var index = 0; index < result.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            var original = result[index];
            var current = original;

            if (options.RemoveInvisibleCharacters)
                current = InvisibleCharacterPattern().Replace(current, string.Empty);
            if (options.NormalizeFullWidthSpaces)
                current = NormalizeSpaces(current);
            if (options.NormalizeChapterNumbers && ChapterTitleNormalizer.TryNormalizeNumericTitle(current, out var title))
                current = title;
            if (options.ChineseVariant != ChineseVariantConversion.None)
                current = ChineseVariantMapper.Convert(current, options.ChineseVariant);
            if (options.NormalizePunctuation)
                current = NormalizeChinesePunctuation(current);
            if (options.ApplyOcrCorrections)
                current = ApplySafeOcrCorrections(current);
            if (options.RepairParagraphBoundaries)
                current = ParagraphBoundaryPattern().Replace(current, "$1" + Environment.NewLine + "　　");

            if (!string.Equals(original, current, StringComparison.Ordinal))
            {
                var change = CreateChange(index + 1, DescribeInlineRules(original, current, options), original, current, exclusions);
                changes.Add(change);
                result[index] = change.IsApplied ? current : original;
            }
            else result[index] = current;
        }

        if (options.RemoveSiteNotices)
        {
            for (var index = 0; index < result.Length; index++)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (result[index] == RemovedLine || !SiteNoticePattern().IsMatch(result[index].Trim())) continue;
                var change = CreateChange(index + 1, "清理网站广告/下载说明", result[index], string.Empty, exclusions, lines[index]);
                changes.Add(change);
                if (change.IsApplied) result[index] = RemovedLine;
            }
        }

        if (options.RemoveRepeatedHeaders)
        {
            for (var index = 0; index < result.Length; index++)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (result[index] == RemovedLine || !RepeatedHeaderPattern().IsMatch(result[index].Trim())) continue;
                var change = CreateChange(index + 1, "删除重复页眉/页码", result[index], string.Empty, exclusions, lines[index]);
                changes.Add(change);
                if (change.IsApplied) result[index] = RemovedLine;
            }
        }

        if (options.RemoveDuplicateChapterTitles)
        {
            string? previousTitle = null;
            for (var index = 0; index < result.Length; index++)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (result[index] == RemovedLine || string.IsNullOrWhiteSpace(result[index])) continue;
                var currentTitle = result[index].Trim();
                if (!ChapterLinePattern().IsMatch(currentTitle)) { previousTitle = null; continue; }
                if (string.Equals(previousTitle, currentTitle, StringComparison.Ordinal))
                {
                    var change = CreateChange(index + 1, "删除连续重复章节标题", result[index], string.Empty, exclusions, lines[index]);
                    changes.Add(change);
                    if (change.IsApplied) result[index] = RemovedLine;
                }
                else previousTitle = currentTitle;
            }
        }

        if (options.RepairHardWraps)
        {
            for (var index = 0; index < result.Length - 1; index++)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var nextIndex = index + 1;
                while (nextIndex < result.Length && ShouldJoin(result[index], result[nextIndex]))
                {
                    var before = result[index] + " ↵ " + result[nextIndex];
                    var after = result[index].TrimEnd() + result[nextIndex].TrimStart();
                    var sourcePair = lines[index] + " ↵ " + lines[nextIndex];
                    var change = CreateChange(index + 1, "修复正文硬换行", before, after, exclusions, sourcePair);
                    changes.Add(change);
                    if (!change.IsApplied) break;
                    result[index] = after;
                    result[nextIndex] = RemovedLine;
                    nextIndex++;
                }
            }
        }

        if (options.CollapseBlankLines)
        {
            var seenBlank = false;
            for (var index = 0; index < result.Length; index++)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
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
                var change = CreateChange(index + 1, "合并连续空行", result[index], string.Empty, exclusions);
                changes.Add(change);
                if (change.IsApplied) result[index] = RemovedLine;
            }
        }

        result = ApplyCustomRules(result, changes, exclusions, options.CustomRules, cancellationToken);

        return new TextCleanupPreview(result, changes.OrderBy(change => change.LineNumber).ToArray());
    }

    public static async Task<string> ReadFileAsync(
        string path,
        TextEncodingMode encodingMode,
        CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return TextFileDecoder.Decode(bytes, encodingMode).Text;
    }

    public static string CreateChangeKey(int lineNumber, string rule, string before)
    {
        var payload = Encoding.UTF8.GetBytes($"{lineNumber}\n{rule}\n{before}");
        return $"{lineNumber}:{Convert.ToHexString(SHA256.HashData(payload))[..16]}";
    }

    private static TextCleanupChange CreateChange(
        int lineNumber,
        string rule,
        string before,
        string after,
        HashSet<string> exclusions,
        string? keySource = null)
    {
        var change = new TextCleanupChange(lineNumber, rule, before, after)
        {
            Key = CreateChangeKey(lineNumber, rule, keySource ?? before),
        };
        return change with { IsApplied = !exclusions.Contains(change.Key) };
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
            .Replace("--", "——", StringComparison.Ordinal)
            .Replace("— —", "——", StringComparison.Ordinal)
            .Replace("‘‘", "“", StringComparison.Ordinal)
            .Replace("’’", "”", StringComparison.Ordinal)
            .Replace("““", "“", StringComparison.Ordinal)
            .Replace("””", "”", StringComparison.Ordinal);
    }

    private static string ApplySafeOcrCorrections(string value) => OcrChineseGapPattern().Replace(value, string.Empty)
        .Replace("．．．．．．", "……", StringComparison.Ordinal)
        .Replace("。。。。。。", "……", StringComparison.Ordinal);

    private static string[] ApplyCustomRules(
        string[] result,
        List<TextCleanupChange> changes,
        HashSet<string> exclusions,
        IReadOnlyList<TextCleanupCustomRule> rules,
        CancellationToken cancellationToken)
    {
        foreach (var rule in rules.Where(rule => rule.Enabled && !string.IsNullOrEmpty(rule.Pattern)).OrderBy(rule => rule.Order).ThenBy(rule => rule.Name, StringComparer.CurrentCulture))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Regex? regex = null;
            if (rule.IsRegex)
            {
                var regexOptions = RegexOptions.CultureInvariant;
                if (rule.IgnoreCase) regexOptions |= RegexOptions.IgnoreCase;
                if (rule.Multiline) regexOptions |= RegexOptions.Multiline | RegexOptions.Singleline;
                regex = new Regex(rule.Pattern, regexOptions, TimeSpan.FromMilliseconds(250));
            }

            if (rule.Multiline)
            {
                var beforeDocument = string.Join('\n', result.Select(line => line == RemovedLine ? string.Empty : line));
                var afterDocument = regex is null
                    ? beforeDocument.Replace(rule.Pattern, rule.Replacement, rule.IgnoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.Ordinal)
                    : regex.Replace(beforeDocument, rule.Replacement);
                if (!string.Equals(beforeDocument, afterDocument, StringComparison.Ordinal))
                {
                    var firstDifference = 0;
                    while (firstDifference < beforeDocument.Length && firstDifference < afterDocument.Length && beforeDocument[firstDifference] == afterDocument[firstDifference]) firstDifference++;
                    var lineNumber = 1 + beforeDocument.AsSpan(0, Math.Min(firstDifference, beforeDocument.Length)).Count('\n');
                    var change = CreateChange(lineNumber, $"自定义：{rule.Name}", Abbreviate(beforeDocument), Abbreviate(afterDocument), exclusions, $"{rule.Id}\n{beforeDocument}");
                    changes.Add(change);
                    if (change.IsApplied) result = afterDocument.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                }
                continue;
            }

            for (var index = 0; index < result.Length; index++)
            {
                if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (result[index] == RemovedLine) continue;
                var chapter = ChapterLinePattern().IsMatch(result[index].Trim());
                if (rule.Scope == TextCleanupRuleScope.BodyOnly && chapter || rule.Scope == TextCleanupRuleScope.ChapterTitlesOnly && !chapter) continue;
                var before = result[index];
                var after = regex is null
                    ? before.Replace(rule.Pattern, rule.Replacement, rule.IgnoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.Ordinal)
                    : regex.Replace(before, rule.Replacement);
                if (string.Equals(before, after, StringComparison.Ordinal)) continue;
                var change = CreateChange(index + 1, $"自定义：{rule.Name}", before, after, exclusions, $"{rule.Id}\n{before}");
                changes.Add(change);
                if (change.IsApplied) result[index] = after;
            }
        }
        return result;
    }

    private static string Abbreviate(string value) => value.Length <= 240 ? value : value[..237] + "…";

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

    [GeneratedRegex("[\\u0000-\\u0008\\u000B\\u000C\\u000E-\\u001F\\u007F\\u200B-\\u200D\\u2060\\uFEFF]")]
    private static partial Regex InvisibleCharacterPattern();

    [GeneratedRegex("([。！？!?])　{2,}")]
    private static partial Regex ParagraphBoundaryPattern();

    [GeneratedRegex(@"^(?:[-—_=]{2,}\s*)?(?:第\s*\d+\s*页|页码\s*[:：]?\s*\d+|www\.[^\s]+)(?:\s*[-—_=]{2,})?$", RegexOptions.IgnoreCase)]
    private static partial Regex RepeatedHeaderPattern();

    [GeneratedRegex(@"(?<=[\p{IsCJKUnifiedIdeographs}])[ \t]+(?=[\p{IsCJKUnifiedIdeographs}])")]
    private static partial Regex OcrChineseGapPattern();

    [GeneratedRegex(@"^\s*(?:第[0-9一二三四五六七八九十百千零〇两]+[章回卷部篇集节]|\d{1,6}(?:\s+|[.．、:_：\-—])).*")]
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
