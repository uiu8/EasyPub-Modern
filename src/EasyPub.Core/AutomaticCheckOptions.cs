namespace EasyPub.Core;

public sealed record AutomaticCheckOptions
{
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<PreflightTargetKind> Targets { get; init; } = [PreflightTargetKind.InputBook, PreflightTargetKind.Chapters, PreflightTargetKind.Output, PreflightTargetKind.Cover, PreflightTargetKind.Illustrations, PreflightTargetKind.BookInformation, PreflightTargetKind.Mobi, PreflightTargetKind.Font];
    public TextCleanupOptions Cleanup { get; init; } = new() { RemoveSiteNotices = true };

    public static IReadOnlyDictionary<string, string> CleanupLabels { get; } = new Dictionary<string, string>
    {
        [nameof(TextCleanupOptions.CollapseBlankLines)] = "合并连续空行",
        [nameof(TextCleanupOptions.RepairHardWraps)] = "修复正文硬换行",
        [nameof(TextCleanupOptions.NormalizeFullWidthSpaces)] = "统一全角空格",
        [nameof(TextCleanupOptions.NormalizeChapterNumbers)] = "标准化章节编号",
        [nameof(TextCleanupOptions.RemoveSiteNotices)] = "网站广告/下载说明",
        [nameof(TextCleanupOptions.NormalizePunctuation)] = "规范中文标点",
        [nameof(TextCleanupOptions.RemoveInvisibleCharacters)] = "删除零宽/控制字符",
        [nameof(TextCleanupOptions.RemoveDuplicateChapterTitles)] = "删除重复章节标题",
        [nameof(TextCleanupOptions.RepairParagraphBoundaries)] = "修复段落粘连",
        [nameof(TextCleanupOptions.RemoveRepeatedHeaders)] = "删除重复页眉/页码",
        [nameof(TextCleanupOptions.ApplyOcrCorrections)] = "安全 OCR 空白修复",
    };
    public static IReadOnlyDictionary<PreflightTargetKind, string> TargetLabels { get; } = new Dictionary<PreflightTargetKind, string>
    {
        [PreflightTargetKind.InputBook] = "文件可读性/EPUB 兼容性",
        [PreflightTargetKind.Chapters] = "章节识别与目录校验",
        [PreflightTargetKind.Output] = "输出路径与格式",
        [PreflightTargetKind.Cover] = "封面资源",
        [PreflightTargetKind.Illustrations] = "插图资源",
        [PreflightTargetKind.BookInformation] = "书籍元数据",
        [PreflightTargetKind.Mobi] = "KindleGen",
        [PreflightTargetKind.Font] = "字体资源",
    };
    public string Summary => !Enabled ? "自动检查已关闭" : !ActiveLabels().Any() ? "自动检查已开启 · 尚未选择规则" : "已开启：" + string.Join("、", ActiveLabels());
    public static TextCleanupOptions MergeCleanupForPreview(TextCleanupOptions current, TextCleanupOptions checks) => current with
    {
        CollapseBlankLines = current.CollapseBlankLines || checks.CollapseBlankLines,
        RepairHardWraps = current.RepairHardWraps || checks.RepairHardWraps,
        NormalizeFullWidthSpaces = current.NormalizeFullWidthSpaces || checks.NormalizeFullWidthSpaces,
        NormalizeChapterNumbers = current.NormalizeChapterNumbers || checks.NormalizeChapterNumbers,
        RemoveSiteNotices = current.RemoveSiteNotices || checks.RemoveSiteNotices,
        NormalizePunctuation = current.NormalizePunctuation || checks.NormalizePunctuation,
        RemoveInvisibleCharacters = current.RemoveInvisibleCharacters || checks.RemoveInvisibleCharacters,
        RemoveDuplicateChapterTitles = current.RemoveDuplicateChapterTitles || checks.RemoveDuplicateChapterTitles,
        RepairParagraphBoundaries = current.RepairParagraphBoundaries || checks.RepairParagraphBoundaries,
        RemoveRepeatedHeaders = current.RemoveRepeatedHeaders || checks.RemoveRepeatedHeaders,
        ApplyOcrCorrections = current.ApplyOcrCorrections || checks.ApplyOcrCorrections,
        ChineseVariant = current.ChineseVariant != ChineseVariantConversion.None ? current.ChineseVariant : checks.ChineseVariant,
        CustomRules = current.CustomRules.Concat(checks.CustomRules).DistinctBy(rule => rule.Id).ToArray(),
    };

    public IEnumerable<string> ActiveLabels()
    {
        foreach (var target in Targets) if (TargetLabels.TryGetValue(target, out var label)) yield return label;
        foreach (var item in CleanupLabels)
            if ((bool)typeof(TextCleanupOptions).GetProperty(item.Key)!.GetValue(Cleanup)!) yield return item.Value;
        if (Cleanup.ChineseVariant != ChineseVariantConversion.None) yield return Cleanup.ChineseVariant == ChineseVariantConversion.ToSimplified ? "繁转简" : "简转繁";
        foreach (var rule in Cleanup.CustomRules.Where(rule => rule.Enabled)) yield return "自定义：" + rule.Name;
    }
}
