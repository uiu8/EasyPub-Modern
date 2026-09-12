using System.Text.RegularExpressions;

namespace EasyPub.Core;

/// <summary>Read-only, conservative diagnostics; never rewrites the chapter plan.</summary>
public static class ChapterDiagnostics
{
    private static readonly Regex Heading = new(@"^第\s*([0-9０-９零〇一二两三四五六七八九十百千万]+)\s*([章回卷部篇])(?:\s|[：:、.．]|$)", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly Regex Part = new(@"^(.*?)\s*[（(]\s*([0-9]+)\s*(?:[,，][^()（）]*)?[）)]\s*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static IReadOnlyList<ConversionPreflightIssue> Inspect(ChapterTreeDocument document, CancellationToken cancellationToken = default, bool detectUnrecognized = true,
        IReadOnlyList<ChapterTreeEntry>? currentEntries = null, int maximumIssues = 200)
    {
        var issues = new List<ConversionPreflightIssue>();
        var previous = new Dictionary<(string Parent, int Level, string Unit), (int Number, string Body)>();
        var titles = new HashSet<(string Parent, int Level, string Title)>();
        var volumes = new Dictionary<int, int?>();
        var parents = new List<ChapterTreeEntry>();
        var entries = currentEntries ?? document.Entries;
        var recognized = entries.Where(e => e.TitleLineNumber.HasValue).Select(e => e.TitleLineNumber!.Value).ToHashSet();
        var lastLevel = 0;
        var omitted = 0;
        void Add(string code, string message, int? line)
        {
            if (issues.Count >= maximumIssues) { omitted++; return; }
            issues.Add(new(document.SourcePath, PreflightSeverity.Warning, code, message + "（仅提示，请核对原文）", PreflightTargetKind.Chapters, line));
        }
        foreach (var entry in entries.Where(e => !e.IsFrontMatter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Level > lastLevel + 1)
                Add("chapter_level_gap", $"目录层级由 {lastLevel} 跳到 {entry.Level}：{entry.Title}", entry.TitleLineNumber);
            lastLevel = entry.Level;
            while (parents.Count > 0 && parents[^1].Level >= entry.Level) parents.RemoveAt(parents.Count - 1);
            var parent = parents.Count > 0 ? parents[^1].Id : "";
            var title = entry.Title.Trim();
            var volume = Regex.Match(title, @"^【?\s*第\s*([0-9０-９零〇一二两三四五六七八九十百千万]+)\s*卷", RegexOptions.None, TimeSpan.FromSeconds(1));
            if (volume.Success && entry.Level == 1 && ParseNumber(volume.Groups[1].Value) is int volumeNumber)
            {
                if (volumes.TryGetValue(volumeNumber, out var firstLine))
                    Add("volume_number_duplicate", $"卷号重复：第{volumeNumber}卷；首次在原文第 {firstLine} 行，本处为“{title}”，请核对并自行调整层级", entry.TitleLineNumber);
                else volumes.Add(volumeNumber, entry.TitleLineNumber);
            }
            if (!titles.Add((parent, entry.Level, title)))
                Add("chapter_duplicate", $"同层级出现重复标题：{title}", entry.TitleLineNumber);
            var match = Heading.Match(title);
            if (match.Success && ParseNumber(match.Groups[1].Value) is int number)
            {
                var unit = match.Groups[2].Value;
                // Explicit volume boundaries also reset flat chapter numbering.
                if (unit is "卷" or "部" or "篇")
                {
                    previous.Clear();
                    titles.Clear();
                }
                else
                {
                    var key = (parent, entry.Level, unit);
                    var body = title[match.Length..].Trim();
                    if (previous.TryGetValue(key, out var before))
                    {
                        if (number > before.Number + 1) Add("chapter_number_gap", $"章节编号从 {before.Number} 跳到 {number}，可能缺章或漏识别：{title}", entry.TitleLineNumber);
                        else if (number <= before.Number && !(number == before.Number && IsNextPart(before.Body, body)))
                            Add("chapter_number_order", $"章节编号由 {before.Number} 变为 {number}，疑似重复或顺序异常：{title}", entry.TitleLineNumber);
                    }
                    previous[key] = (number, body);
                }
            }
            parents.Add(entry);
        }
        // A persisted manual plan is deliberate: do not flag merged/omitted headings here.
        foreach (var line in detectUnrecognized ? document.SourceLines : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = line.Text.Trim();
            if (text.Length > 80 || recognized.Contains(line.LineNumber)) continue;
            var match = Heading.Match(text);
            if (match.Success && match.Groups[2].Value is "章" or "回")
                Add("chapter_unrecognized", $"疑似章标题未进入章节树：{text}", line.LineNumber);
        }
        if (omitted > 0) issues.Add(new(document.SourcePath, PreflightSeverity.Warning, "chapter_diagnostics_limit",
            $"另有 {omitted} 条章节提示未展开；请先核对章节识别规则，再重新检查。", PreflightTargetKind.Chapters));
        return issues;
    }

    private static bool IsNextPart(string before, string after)
    {
        var left = Part.Match(before);
        var right = Part.Match(after);
        return left.Success && right.Success && left.Groups[1].Value.Trim().Length > 0
            && left.Groups[1].Value.Trim() == right.Groups[1].Value.Trim()
            && int.TryParse(left.Groups[2].Value, out var first)
            && int.TryParse(right.Groups[2].Value, out var next)
            && first > 0 && (long)next == (long)first + 1;
    }

    public static string? SuggestCorrectedTitle(string previous, string current, string next)
    {
        var left = Heading.Match(previous.Trim());
        var middle = Heading.Match(current.Trim());
        var right = Heading.Match(next.Trim());
        if (!left.Success || !middle.Success || !right.Success) return null;
        if (middle.Groups[2].Value is not ("章" or "回") || left.Groups[2].Value != middle.Groups[2].Value || right.Groups[2].Value != middle.Groups[2].Value) return null;
        var a = ParseNumber(left.Groups[1].Value);
        var b = ParseNumber(middle.Groups[1].Value);
        var c = ParseNumber(right.Groups[1].Value);
        if (a is null || b != a || c != (long)a + 2 || previous.Trim() == current.Trim()) return null;
        if (IsNextPart(previous.Trim()[left.Length..].Trim(), current.Trim()[middle.Length..].Trim())) return null;
        var title = current.Trim();
        var number = middle.Groups[1];
        return title[..number.Index] + (a.Value + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + title[(number.Index + number.Length)..];
    }

    private static int? ParseNumber(string text)
    {
        text = string.Concat(text.Where(c => !char.IsWhiteSpace(c)).Select(c => c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c));
        if (int.TryParse(text, out var arabic)) return arabic;
        if (text.Length > 12 || text.Any(char.IsAsciiDigit)) return null;
        const string digits = "零一二三四五六七八九";
        long total = 0, section = 0, digit = 0;
        foreach (var c in text)
        {
            var value = c == '〇' ? 0 : c == '两' ? 2 : digits.IndexOf(c);
            if (value >= 0) { digit = digit * 10 + value; continue; }
            var unit = c switch { '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, _ => 0 };
            if (unit == 0) return null;
            if (unit == 10000) { total += (section + digit) * unit; section = 0; }
            else section += (digit == 0 ? 1 : digit) * unit;
            digit = 0;
        }
        var result = total + section + digit;
        return result <= int.MaxValue ? (int)result : null;
    }
}
