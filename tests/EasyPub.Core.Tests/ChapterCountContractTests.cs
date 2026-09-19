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

    private static ConversionOptions Options(bool layered, bool oldChapterPattern, bool oldLevelPatterns) => new()
    {
        ChapterPattern = oldChapterPattern ? HeadingSyntax.LegacyPattern : null,
        TocHierarchy = new TocHierarchyOptions
        {
            Enabled = layered,
            Level1Pattern = oldLevelPatterns ? OldLevel1 : TocHierarchyOptions.DefaultLevel1Pattern,
            Level2Pattern = oldLevelPatterns ? OldLevel2 : TocHierarchyOptions.DefaultLevel2Pattern,
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
    [InlineData(false, false, false, "现代默认 + 分层关")]
    [InlineData(true, false, false, "现代默认 + 分层开")]
    [InlineData(false, true, true, "旧正则 + 分层关")]
    [InlineData(true, true, true, "旧正则 + 分层开")]
    [InlineData(true, false, true, "现代默认 + 分层开 + 旧 Level")]
    [InlineData(true, true, false, "旧正则 + 分层开 + 新 Level")]
    public async Task All_three_paths_count_the_same_chapters(
        bool layered, bool oldChapterPattern, bool oldLevelPatterns, string label)
    {
        var path = SamplePath();
        var options = Options(layered, oldChapterPattern, oldLevelPatterns);

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
    /// 它只该改变章与卷的从属关系。
    ///
    /// <para>样书第六卷每章标题印了两遍（100 章）。分层路径曾经把第二遍拆成
    /// 一个正文为空的独立条目，于是开分层 = 关分层 + 100 个空章。
    /// 这条现在是绿的：<c>SkipRepeatedHeadingRun</c> 也作用于条目列表了。</para>
    /// </summary>
    [Theory]
    [InlineData(false, "现代默认")]
    [InlineData(true, "旧正则")]
    public async Task Turning_on_layering_does_not_change_how_many_chapters_the_book_has(
        bool oldChapterPattern, string label)
    {
        var path = SamplePath();
        var flat = await ByTreeAsync(path, Options(layered: false, oldChapterPattern, oldLevelPatterns: oldChapterPattern));
        var layered = await ByTreeAsync(path, Options(layered: true, oldChapterPattern, oldLevelPatterns: oldChapterPattern));

        Assert.True(flat == layered,
            $"[{label}] 分层开关改变了章节数：关={flat} 开={layered}。"
            + "分层只该决定卷与章的从属，不该多出章节 —— 多出来的应当是重复标题行被拆开的空章。");
    }

    /// <summary>
    /// **Level 正则是第二条独立的识别通道** —— 它认出的标题，章节正则未必认得出。
    ///
    /// <para>样书第一百零一章起有 7 章印成裸数字标题「一百零一章 戊101 断后险路」，
    /// 没有「第」字。旧 `LegacyPattern` 要求首字符必须是 第 或 卷，**结构上就匹不上**；
    /// 旧 Level2 要求 `第…章回`，也匹不上。只有现代 `HeadingSyntax.ChapterPattern`
    /// 能认——它的 `NumberPrefix` 里「第」是可选的（见 HeadingSyntax 第 20–23 行的注释：
    /// "plenty of releases print 「八百三十章 赌徒」"）。</para>
    ///
    /// <para>所以"旧正则 + 分层开"下旧 Level 给 460、新 Level 给 467 —— **那 7 个差
    /// 是能力，不是缺陷**。这条测试把"这 7 章存在、且只有现代 Level 正则看得见"锁下来，
    /// 免得将来有人把它当成计数 bug 去"修平"。</para>
    ///
    /// <para>⚠️ 曾经这里写的是一条错误的契约：「Level 正则不该改变章节数」。
    /// 那是把 Level 正则当成了"只决定层级"的装饰，而它实际上承担识别职责。
    /// 契约写错时不要改实现去迁就它 —— 先查清差在哪。</para>
    /// </summary>
    [Fact]
    public async Task The_modern_level_patterns_see_bare_numbered_chapters_the_legacy_pattern_cannot()
    {
        var path = SamplePath();
        var oldLevels = await ChapterTreeDocument.LoadAsync(path, HeadingSyntax.LegacyPattern,
            new TocHierarchyOptions { Enabled = true, Level1Pattern = OldLevel1, Level2Pattern = OldLevel2 });
        var newLevels = await ChapterTreeDocument.LoadAsync(path, HeadingSyntax.LegacyPattern,
            new TocHierarchyOptions
            {
                Enabled = true,
                Level1Pattern = TocHierarchyOptions.DefaultLevel1Pattern,
                Level2Pattern = TocHierarchyOptions.DefaultLevel2Pattern,
            });

        var oldLines = oldLevels.Entries.Where(e => e.TitleLineNumber.HasValue)
            .Select(e => e.TitleLineNumber!.Value).ToHashSet();
        var newOnly = newLevels.Entries.Where(e => e.TitleLineNumber.HasValue)
            .Select(e => (Line: e.TitleLineNumber!.Value, e.Title))
            .Where(e => !oldLines.Contains(e.Line)).ToArray();

        Assert.Equal(7, newOnly.Length);
        Assert.All(newOnly, e => Assert.DoesNotContain("第", e.Title));
        Assert.All(newOnly, e => Assert.Matches(@"^[0-9０-９零〇一二两兩三四五六七八九十百千万萬]+章", e.Title.Trim()));
    }
}
