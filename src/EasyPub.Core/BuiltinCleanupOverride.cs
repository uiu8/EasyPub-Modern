using System.Text.RegularExpressions;

namespace EasyPub.Core;

public sealed record BuiltinCleanupOverride
{
    public bool UseDefaultAlgorithm { get; init; } = true;
    public IReadOnlyList<TextCleanupCustomRule> Rules { get; init; } = [];
}

public sealed record AdvertisementRuleOptions
{
    public const string DefaultPattern = @"(?:本书来自|更多精彩.*访问|请记住本站|最新网址|手机用户请浏览|下载本书|txt电子书|小说下载|加入书签|投推荐票|章节错误.*举报|广告位)";
    public string Pattern { get; init; } = DefaultPattern;
    public bool IsRegex { get; init; } = true;
    public bool UsePromotionHeuristics { get; init; } = true;
    public string PreservePattern { get; init; } = string.Empty;
    public bool PreserveIsRegex { get; init; }

    public Func<string, bool> CompileMatcher(string pattern, bool regex)
    {
        if (string.IsNullOrEmpty(pattern)) return _ => false;
        if (!regex) return text => text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        var compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        return compiled.IsMatch;
    }
}

public static class BuiltinCleanupConfiguration
{
    public static TextCleanupOptions Restore(TextCleanupOptions options, string? key = null)
    {
        var overrides = options.BuiltinOverrides.Where(pair => key is not null && pair.Key != key).ToDictionary(pair => pair.Key, pair => pair.Value);
        return options with
        {
            BuiltinOverrides = overrides,
            Advertisement = key is null or nameof(TextCleanupOptions.RemoveSiteNotices) ? new() : options.Advertisement,
            HardWrapMinimumLength = key is null or nameof(TextCleanupOptions.RepairHardWraps) ? 8 : options.HardWrapMinimumLength,
        };
    }

    public static TextCleanupOptions Prepare(TextCleanupOptions options)
    {
        var effective = options with { };
        var rules = new List<TextCleanupCustomRule>();
        foreach (var (key, customization) in options.BuiltinOverrides)
        {
            var property = typeof(TextCleanupOptions).GetProperty(key);
            if (property?.PropertyType != typeof(bool) || property.SetMethod is null || property.GetValue(options) is not true) continue;
            if (!customization.UseDefaultAlgorithm) property.SetValue(effective, false);
            rules.AddRange(customization.Rules.Select(rule => rule with { Id = key + ":" + rule.Id, Name = AutomaticCheckOptions.CleanupLabels.GetValueOrDefault(key, key) + " / " + rule.Name }));
        }
        return effective with { CustomRules = rules.Concat(options.CustomRules).ToArray() };
    }
}
