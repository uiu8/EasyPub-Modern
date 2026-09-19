using System.IO;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// **章节数契约**：同一个文件、同一份识别配置，三条路径必须数出同样的章节。
///
/// <para>它锁的是一个真实缺陷族。样书在"现代默认"下，只看分层开关就能得到两个不同的数：
/// 分层关 = 467 章，分层开 = 567 章 —— 多出来的 100 章是第六卷重复标题行被拆成的空章。
/// 而"没保存章节树"的转换路径无论分层开关都给 567 章，也就是**跟分层开一致**。</para>
///
/// <para>所以真正的缺口是：**重复标题行的合并只存在于章节树路径**。
/// <c>LegacyTextParser</c> 与分层路径都不合并。样书第六卷每章标题印了两遍，
/// 于是同一本书：章节树说 467，转换产出 567。</para>
///
/// <para><b>三条路径</b>：</para>
/// <list type="number">
/// <item><b>章节树</b> —— 工作台/保存树走的那条（<see cref="ChapterTreeDocument.LoadAsync"/>）</item>
/// <item><b>预检</b> —— 导入后自动检查走的那条（<c>ConversionPreflightInspector</c>）</item>
/// <item><b>无树转换</b> —— 没保存过章节树时转换走的那条（<c>LegacyTextParser</c>）</item>
/// </list>
///
/// <para><b>一个已经排除的假象</b>：走查时预检报 560、章节树是 561，
/// 曾据此判断"两个数不可能来自同一个 document"。口径摆平后发现
/// 560 与 561 是同一份 document 的同一个数（561 含合成的「序」，560 不含），
/// 两个数是自洽的。当时的误判源于把"旧正则 + 分层开"下的 561
/// 跟"现代默认 + 分层关"下的 468 当成同一份配置在比 —— 组合效应。</para>
///
/// <para><b>这个测试当前应当是红的。</b>它是修复计划 P0-1 的产物：先把它写成契约，
/// 让三条路径的差距变成基线，再动 P0-2（收敛 <c>?? LegacyDefault</c>）与 P0-4（分层路径补合并）。
/// 修好之前它红着是**正确**的状态 —— 不要为了让测试过而放宽断言。</para>
/// </summary>
public class ChapterCountContractTests
{
    private static string SamplePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "六卷缺陷样书", "缺陷样书.txt");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 samples/六卷缺陷样书");
    }

    private const string OldLevel1 = @"^\s*第[0123456789一二三四五六七八九十零〇百千两]+[卷部篇集].*";
    private const string OldLevel2 = @"^\s*第[0123456789一二三四五六七八九十零〇百千两]+[章回].*";

    private static ConversionOptions Options(bool layered, bool oldPatterns) => new()
    {
        ChapterPattern = oldPatterns ? HeadingSyntax.LegacyPattern : null,
        TocHierarchy = new TocHierarchyOptions
        {
            Enabled = layered,
            Level1Pattern = oldPatterns ? OldLevel1 : TocHierarchyOptions.DefaultLevel1Pattern,
            Level2Pattern = oldPatterns ? OldLevel2 : TocHierarchyOptions.DefaultLevel2Pattern,
        },
    };

    // **三条路径必须用同一个口径**，否则测出来的差异是记账方式，不是缺陷。
    //
    // 三条路径各有各的"前置条目"：
    //   · 章节树的 Entries 含一个合成的前置「序」（TitleLineNumber 为 null）；
    //   · 预检的 ChapterCandidateCount **只数有标题行号的条目**，天然不含前置；
    //   · LegacyTextParser 无条件在开头放一个「序」（源码第 33 行），也不是识别出来的。
    //
    // 所以统一口径 = **由标题行识别出来的章节数**。样书在此口径下前置恰好 1 条，
    // 于是三方各减 1 —— 这不是"差一个"的缺陷，是先把口径摆平，再看剩下的差。
    // 摆平之后如果还有差，那才是真的差。

    /// <summary>路径一：章节树 —— <c>ChapterTreeDocument.LoadAsync</c> 中由标题行识别出的条目数。</summary>
    private static async Task<int> ByTreeAsync(string path, ConversionOptions options) =>
        (await ChapterTreeDocument.LoadAsync(path, options.ChapterPattern, options.TocHierarchy,
            options.TextEncoding)).Entries.Count(entry => entry.TitleLineNumber.HasValue);

    /// <summary>路径二：预检 —— 导入后自动检查报出的章节候选数（本来就是"有标题行号"口径）。</summary>
    private static async Task<int> ByPreflightAsync(string path, ConversionOptions options)
    {
        var inspector = new ConversionPreflightInspector();
        var request = new ConversionRequest(path, path + ".epub", Options: options);
        var report = await inspector.InspectAsync([request], CancellationToken.None);
        return report.Books.Single().ChapterCandidateCount;
    }

    /// <summary>路径三：无树转换 —— <c>LegacyTextParser</c> 逐行解析出的章数，去掉开头合成的「序」。</summary>
    private static async Task<int> ByLegacyParseAsync(string path, ConversionOptions options) =>
        (await LegacyTextParser.ParseAsync(path, options, null, CancellationToken.None)).Count - 1;

    [Theory]
    [InlineData(false, false, "现代默认 + 分层关")]
    [InlineData(true, false, "现代默认 + 分层开")]
    [InlineData(false, true, "旧正则 + 分层关")]
    [InlineData(true, true, "旧正则 + 分层开")]
    public async Task All_three_paths_count_the_same_chapters(bool layered, bool oldPatterns, string label)
    {
        var path = SamplePath();
        var options = Options(layered, oldPatterns);

        var byTree = await ByTreeAsync(path, options);
        var byPreflight = await ByPreflightAsync(path, options);
        var byLegacy = await ByLegacyParseAsync(path, options);

        Assert.True(byTree == byPreflight && byPreflight == byLegacy,
            $"[{label}] 三条路径数出了不同的章节数 —— "
            + $"章节树={byTree} 预检={byPreflight} 无树转换={byLegacy}。"
            + "用户看到哪个数，取决于他有没有保存过章节树。");
    }

    /// <summary>
    /// 同一份配置下，**有无层级目录不该改变"这本书有多少章"这个事实** ——
    /// 它只该改变章与卷的从属关系。样书第六卷的重复标题行被分层路径拆成了 100 个空章，
    /// 所以这条现在也是红的。
    /// </summary>
    [Fact]
    public async Task Turning_on_layering_does_not_change_how_many_chapters_the_book_has()
    {
        var path = SamplePath();
        var flat = await ByTreeAsync(path, Options(layered: false, oldPatterns: false));
        var layered = await ByTreeAsync(path, Options(layered: true, oldPatterns: false));

        Assert.True(flat == layered,
            $"分层开关改变了章节数：关={flat} 开={layered}。"
            + "分层只该决定卷与章的从属，不该多出章节 —— 多出来的应当是重复标题行被拆开的空章。");
    }
}
