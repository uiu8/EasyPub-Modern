namespace EasyPub.Core;

public static class HeadingTypoRules
{
    public const string Default = "久=九";
    public static IReadOnlyDictionary<char, char> Parse(string text)
    {
        var result = new Dictionary<char, char>();
        foreach (var line in text.Replace("\r", "").Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            var pair = line.Trim().Split('=');
            if (pair.Length != 2 || pair[0].Trim().Length != 1 || pair[1].Trim().Length != 1)
                throw new ArgumentException("编号纠错表每行填写一个单字映射，例如：久=九。留空表示关闭编号纠错。");
            var from = pair[0].Trim()[0]; var to = pair[1].Trim()[0];
            const string numbers = "0123456789０１２３４５６７８９零〇一二两三四五六七八九十百千万";
            if (numbers.Contains(from) || !char.IsLetter(from) || !numbers.Contains(to) || !result.TryAdd(from, to))
                throw new ArgumentException("原字必须是非数字文字且不能重复，目标必须是数字；不允许改写合法章号。");
        }
        return result;
    }
}
