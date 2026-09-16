using System.Globalization;

namespace EasyPub.Core;

/// <summary>
/// 应用版本号的解析与比较。
/// 发布 tag 不总是三段（仓库里同时存在 v1.18、v1.15.1、v1.56.3），
/// 一律补齐成三段再比较，否则 "1.18" 会被当成 Build=-1 而小于 "1.18.0"。
/// </summary>
public static class AppVersion
{
    private static readonly Version Zero = new(0, 0, 0);

    /// <summary>把 "v1.56.3"、"1.18"、"V1.2.3-beta.1" 解析成补齐三段的版本号。</summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var span = text.Trim();
        if (span[0] is 'v' or 'V') span = span[1..];
        // 预发布后缀（1.57.0-beta.1）与构建元数据（+sha）不参与比较。
        var cut = span.IndexOfAny(['-', '+', ' ', '\t']);
        if (cut >= 0) span = span[..cut];
        if (span.Length == 0) return false;

        var parts = span.Split('.');
        if (parts.Length is 0 or > 4) return false;
        var numbers = new int[3];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            if (index < 3) numbers[index] = value;
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>补齐三段。Build 或 Revision 为 -1 时直接比较会得到错误结果（1.57 &gt; 1.57.0）。</summary>
    public static Version Normalize(Version? version) =>
        version is null ? Zero : new Version(version.Major, version.Minor, Math.Max(0, version.Build));

    /// <summary>candidate 是否比 current 更新。任一无法解析都返回 false（宁可漏报，不可误报）。</summary>
    public static bool IsNewer(string? candidate, Version? current) =>
        TryParse(candidate, out var parsed) && parsed > Normalize(current);

    /// <summary>界面展示用的 "1.56.3"。</summary>
    public static string Display(Version? version)
    {
        var normalized = Normalize(version);
        return $"{normalized.Major}.{normalized.Minor}.{normalized.Build}";
    }
}
