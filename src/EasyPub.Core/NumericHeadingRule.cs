using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record NamedNumericHeadingPreset(string Name, string Pattern, int MinimumBodyLines, bool Enabled);

public static class NumericHeadingRule
{
    public const string DefaultPattern = @"^\s*(?<number>\d{1,6})(?:\s+|[.．](?!\d)\s*|[、:_：\-—]\s*)(?<title>\S.*?)\s*$";

    public static Regex Compile(string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        if (!regex.GetGroupNames().Contains("number") || !regex.GetGroupNames().Contains("title"))
            throw new ArgumentException("表达式必须包含 (?<number>...) 章号组和 (?<title>...) 标题组。");
        return regex;
    }

    public static bool Matches(Regex regex, string line)
    {
        var match = regex.Match(line);
        return match.Success && int.TryParse(match.Groups["number"].Value, out var number)
            && number is >= 1 and <= 9999 && match.Groups["title"].Value.Any(char.IsLetter);
    }
}
