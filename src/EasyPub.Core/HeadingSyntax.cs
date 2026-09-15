using System.Text;
using System.Text.RegularExpressions;

namespace EasyPub.Core;

/// <summary>Shared built-in heading boundaries. Custom expressions remain explicit overrides.</summary>
public static class HeadingSyntax
{
    public const string LegacyPattern = @"^\s*[第卷][0123456789一二三四五六七八九十零〇百千两]*[章回部节集卷].*";
    public const string Numerals = "0-9０-９零〇一二两兩三四五六七八九十百千万萬壹贰貳叁參肆伍陆陸柒捌玖拾佰仟";
    public const string Start = @"^\s*[【\[（(]?\s*";
    public const string NumberPrefix = @"(?:第\s*)?[" + Numerals + @"]+\s*";
    // A short standalone special heading needs an end or a visible separator before its title.
    public const string Special = @"(?:序章|序言|前言|后记|後記|尾声|尾聲|终章|終章|番外(?:篇)?)(?:$|[\s:：、（(【\[]).*";
    public const string ChapterMarker = @"(?:章|回(?!头|頭|到|来|來|去|事|答|忆|憶))";
    public const string VolumePattern = Start + NumberPrefix + @"[卷部篇集].*";
    public const string ChapterPattern = Start + "(?:" + NumberPrefix + ChapterMarker + ".*|" + Special + ")";
    public const string SectionPattern = Start + NumberPrefix + @"节.*";
    /// <summary>
    /// "章" and "回" may drop the 「第」 — plenty of releases print "八百三十章 赌徒" and the sections
    /// must still be found. The rarer markers may not: a bare "节" turns ordinary prose into a heading
    /// ("九节鞭、三节棍，随意变换的冰武…" reads as 第9节), and the same goes for 卷/部/篇/集. Requiring
    /// 「第」 for those keeps real sections while leaving sentences alone.
    /// </summary>
    public const string DefaultPattern = Start + "(?:" + NumberPrefix + ChapterMarker + @".*|第\s*[" + Numerals + @"]+\s*[卷部篇集节].*|" + Special + ")";
    private static readonly Regex Builtin = new(DefaultPattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex Volume = new(VolumePattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    // Left as it was. Narrowing this to match DefaultPattern's 「第」 rule looked consistent but broke
    // volume naming ("第十卷" resolved to the wrong number) — and it is not needed: DefaultPattern
    // already keeps prose like "九节鞭、三节棍…" out of the tree, so no bogus entry reaches this.
    internal static readonly Regex ChapterNumber = new(@"(?:第\s*)?(?<n>[" + Numerals + @"]+)\s*[章回节]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    // Stripping a numbering prefix stays permissive on purpose: it only removes text from a title.
    internal static readonly Regex Numbering = new(NumberPrefix + @"[章回节卷部篇集]", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    private static readonly Regex ProsePrefix = new(Start + @"(?:[" + Numerals + @"]+回(?:头|頭|到|来|來|去|事|答|忆|憶)|前言不搭后语|正文开始了)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
    public static bool IsHeading(string text) => Builtin.IsMatch(text);
    public static bool IsProsePrefix(string text) => ProsePrefix.IsMatch(text);
    public static bool IsVolume(string text) => Volume.IsMatch(text) && !ChapterNumber.IsMatch(text);

    public static bool IsNumeral(char ch) => char.IsAsciiDigit(ch) || (Numerals.Contains(ch) && ch != '-');

    public static int? ParseNumber(string input)
    {
        var text = input.Normalize(NormalizationForm.FormKC);
        long total = 0, section = 0, number = 0;
        var hasNumber = false;
        foreach (var ch in text)
        {
            var digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                '零' or '〇' => 0, '一' or '壹' => 1, '二' or '两' or '兩' or '贰' or '貳' => 2,
                '三' or '叁' or '參' => 3, '四' or '肆' => 4, '五' or '伍' => 5,
                '六' or '陆' or '陸' => 6, '七' or '柒' => 7, '八' or '捌' => 8, '九' or '玖' => 9, _ => -1
            };
            if (digit >= 0) { number = number * 10 + digit; hasNumber = true; }
            else if (ch is '万' or '萬')
            { total += (section + number == 0 ? 1 : section + number) * 10000; section = number = 0; hasNumber = false; }
            else
            {
                var unit = ch switch { '十' or '拾' => 10, '百' or '佰' => 100, '千' or '仟' => 1000, _ => 0 };
                if (unit == 0) return null;
                // A 零 directly in front of a unit means "one" of it, not "zero" of it: releases write
                // "第四百零十章" for 第四百一十章 and "第一千零十章" for 第一千零一十章. Carrying the 0
                // through as the coefficient silently turns 410 into 400 and 1010 into 1000 — which is how
                // a chapter sitting in plain sight gets reported as missing. A 零 in front of a digit keeps
                // its literal value, so "一千零八" still reads 1008.
                section += (hasNumber && number > 0 ? number : 1) * unit; number = 0; hasNumber = false;
            }
            if (total + section + number > int.MaxValue) return null;
        }
        return text.Length == 0 ? null : (int)(total + section + number);
    }
}
