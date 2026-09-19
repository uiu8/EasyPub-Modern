using System.IO;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// **识别选项不许静默替换。**
///
/// <para>原先 9 处调用点写的是 <c>request.Options ?? ConversionOptions.LegacyDefault</c> ——
/// 一句话就把 <c>ChapterPattern</c> 换成 2015 年的宽松正则。后果是同一本书只因为某条路径
/// 没拿到 Options，就被数出不同的章节数，而用户设置里明明选的是现代默认。
/// 那是 HANDOVER §8.1 那一族的**第一个成因**。</para>
///
/// <para>现在四条路径（分析 / 预检 / 预览 / 回执）在 null 时**直接抛**；
/// 只有两个 <c>Legacy*Writer</c> 兼容出口保留显式回退 —— 它们的 "Legacy" 指
/// **输出格式兼容 v1.50**，是既有契约。</para>
/// </summary>
public class ConversionRequestOptionsContractTests
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

    [Fact]
    public void Options_present_is_returned_untouched()
    {
        var options = new ConversionOptions { ChapterPattern = "^第.+话" };
        var request = new ConversionRequest("a.txt", "a.epub", Options: options);

        Assert.Same(options, request.RequiredOptions);
    }

    [Fact]
    public void Options_missing_throws_and_names_the_book()
    {
        var request = new ConversionRequest("/tmp/某本书.txt", "/tmp/某本书.epub");

        var error = Assert.Throws<InvalidOperationException>(() => request.RequiredOptions);

        // 报错必须能定位到是**哪一次请求**没带选项，否则在批量转换里等于没报。
        Assert.Contains("某本书.txt", error.Message);
        // 并且必须告诉人怎么改：要旧语义就显式传 LegacyDefault。
        Assert.Contains("LegacyDefault", error.Message);
    }

    /// <summary>
    /// 预检是四条路径里最先跑、也最容易被误认为"读一下文件而已"的那条。
    /// 它以前会静默换成旧正则 —— 于是工作台和转换看到的不是同一棵树。
    /// </summary>
    [Fact]
    public async Task Preflight_refuses_to_guess_the_recognition_options()
    {
        var request = new ConversionRequest(SamplePath(), SamplePath() + ".epub");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConversionPreflightInspector().InspectAsync([request]));
    }

    [Fact]
    public async Task Analysis_refuses_to_guess_the_recognition_options()
    {
        var request = new ConversionRequest(SamplePath(), SamplePath() + ".epub");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new BookAnalysisCoordinator().AnalyzeAsync([request]));
    }

    /// <summary>
    /// 反方向：显式要旧语义是**允许**的，而且必须真的还是旧语义。
    /// 这条锁住的是"我们取消的是沉默，不是 v1.50 兼容"。
    /// </summary>
    [Fact]
    public void Asking_for_legacy_semantics_explicitly_still_works()
    {
        var request = new ConversionRequest("a.txt", "a.epub", Options: ConversionOptions.LegacyDefault);

        Assert.Equal(HeadingSyntax.LegacyPattern, request.RequiredOptions.ChapterPattern);
        // 旧默认与现代默认的**唯一**差别就是章节正则；其余排版选项两者相同。
        // 所以"没传就用旧默认"才会那么难发现：它在大多数书上看起来一切正常。
        var modern = new ConversionOptions();
        Assert.Null(modern.ChapterPattern);
        Assert.NotEqual(modern.ChapterPattern, ConversionOptions.LegacyDefault.ChapterPattern);
    }
}
