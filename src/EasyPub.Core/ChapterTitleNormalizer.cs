using System.Text.RegularExpressions;
using System.Globalization;
using System.Text;

namespace EasyPub.Core;

public static partial class ChapterTitleNormalizer
{
    public static bool TryNormalizeNumericTitle(string input, out string normalized)
    {
        var match = NumericChapterPattern().Match(input);
        if (!match.Success
            || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number is < 1 or > 9999)
        {
            normalized = input;
            return false;
        }

        var title = match.Groups["title"].Value.Trim();
        if (!title.Any(char.IsLetter))
        {
            normalized = input;
            return false;
        }

        normalized = $"第{ToChineseNumber(number)}章 {title}";
        return true;
    }

    private static string ToChineseNumber(int number)
    {
        const string digits = "零一二三四五六七八九";
        var units = new[] { "", "十", "百", "千" };
        var result = new StringBuilder();
        var pendingZero = false;

        for (var power = 3; power >= 0; power--)
        {
            var divisor = (int)Math.Pow(10, power);
            var digit = number / divisor % 10;
            if (digit == 0)
            {
                if (result.Length > 0 && number % divisor != 0) pendingZero = true;
                continue;
            }

            if (pendingZero)
            {
                result.Append('零');
                pendingZero = false;
            }
            if (!(power == 1 && digit == 1 && result.Length == 0)) result.Append(digits[digit]);
            result.Append(units[power]);
        }

        return result.ToString();
    }

    [GeneratedRegex(@"^\s*(?<number>\d{1,6})(?:\s+|[.\uff0e、:_：\-—]\s*)(?<title>\S.*?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumericChapterPattern();
}
