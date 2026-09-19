namespace EasyPub.Core;

/// <summary>
/// **识别规则与编号解析不一致时，把"哪些功能不会工作"说出来。**
///
/// <para>这是 HANDOVER §8.2 那个架构裂缝的最小补丁。它的形状是：
/// **上半截可配、下半截写死** —— "什么算标题"由用户的
/// <see cref="ConversionOptions.ChapterPattern"/> 决定，而"标题里的编号长什么样"
/// 由 <see cref="HeadingSyntax.ChapterNumber"/> 这个**常量**决定，两者不一致时
/// **没有任何检查会拦下来**。</para>
///
/// <para>实测（2026-09-18，一本 6 个「第N话」标题的小书，见
/// <c>RecognitionConsistencyTests</c>）：用户的规则认得出标题，但
/// <c>ReferenceOutline.ParseKey</c> 读出的 <c>Number</c> **全是空字符串**，
/// 于是 <c>ChapterDiagnostics</c> 的提醒从
/// <c>跳章 · 重复 · 编号顺序 · 跳章</c> 缩成只剩 <c>重复</c> ——
/// **跳章提示与编号顺序检查静默消失，界面上没有任何提示。**</para>
///
/// <para><b>本类不改任何行为，只做一致性检查。</b>修的是"沉默"，不是"错" ——
/// 与 §8.5 的 P0-3 同一个形状。用户的自定义正则仍然说了算。</para>
/// </summary>
public static class RecognitionConsistency
{
    /// <summary>
    /// 少于这个数就不报。一两章的短文件读不出编号并不说明什么，
    /// 而且"序章 + 番外"这类书本来就没编号 —— 报了只会变成噪音。
    /// </summary>
    private const int MinimumTitles = 3;

    /// <summary>
    /// 检查这棵树的标题能不能读出编号。全都读不出时返回一条说明，否则 <c>null</c>。
    ///
    /// <para>判据是**全部**读不出，不是"大部分"：一本正常的书里
    /// 「序」「番外」本来就没有编号，用比例判会误报。全都读不出才确定是词表不一致。</para>
    /// </summary>
    public static RecognitionNumberingGap? Inspect(ChapterTreeDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var titles = document.Entries
            .Where(entry => entry.TitleLineNumber.HasValue)
            .Select(entry => entry.Title.Trim())
            .Where(title => title.Length > 0)
            .ToArray();
        if (titles.Length < MinimumTitles) return null;

        var unreadable = titles.Where(title => ReferenceOutline.ParseKey(title).Number.Length == 0).ToArray();
        if (unreadable.Length != titles.Length) return null;

        return new RecognitionNumberingGap(titles.Length, unreadable.Take(3).ToArray());
    }
}

/// <summary>
/// 识别出的标题**一个编号都读不出来**。
///
/// <para><see cref="UnavailableFeatures"/> 里是实测确认会因此不工作的功能 ——
/// 只列验证过的，不列"理论上也依赖编号"的。多列一条会让用户以为功能坏了，
/// 而那正是这一整类缺陷原本的样子。</para>
/// </summary>
public sealed record RecognitionNumberingGap(
    int TitleCount,
    IReadOnlyList<string> SampleTitles)
{
    /// <summary>实测确认会静默不工作的检查项（对应 <see cref="ChapterDiagnostics"/> 的问题码）。</summary>
    public static IReadOnlyList<string> UnavailableFeatures { get; } =
        ["跳章提示", "编号顺序检查"];

    public string Example => SampleTitles.Count == 0 ? "(无)" : SampleTitles[0];

    /// <summary>
    /// 给用户看的一句话。**必须同时说清三件事**：规则本身没问题、读不出编号是为什么、
    /// 以及因此少了哪几个检查 —— 只说"编号读不出"用户不知道关他什么事。
    /// </summary>
    public string Describe() =>
        $"当前识别规则认出了 {TitleCount} 个标题，但**一个章号都读不出来**（例如「{Example}」）。"
        + "识别本身是对的 —— 是「从标题里读编号」这一步只认「第…章 / 回 / 节」，不认其他写法。"
        + $"因此这些检查不会工作：{string.Join("、", UnavailableFeatures)}。"
        + "想让它们工作，可以把标题里的「章」改成识别规则支持的写法，或保持现状并手工核对。";
}
