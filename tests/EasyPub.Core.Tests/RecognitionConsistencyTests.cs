using System.IO;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// **识别规则与写死的编号解析不一致时，不许沉默。**（§8.2 方案 A / P1-1）
///
/// <para>这个项目的架构裂缝是"上半截可配、下半截写死"：什么算标题由用户的
/// <see cref="ConversionOptions.ChapterPattern"/> 决定，而标题里的编号长什么样由
/// <see cref="HeadingSyntax.ChapterNumber"/> 这个常量决定。两者不一致时
/// **没有任何检查会拦下来** —— 检查项只是安静地少报几项。</para>
///
/// <para>下面这组测试把 §8.2 那张"从代码读出来、没跑过"的影响表变成了实测值。</para>
/// </summary>
public class RecognitionConsistencyTests
{
    private static async Task<string> WriteAsync(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-consistency-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, source);
        return path;
    }

    // 用「话」而不是「章」：用户的正则认它，写死的编号解析不认。
    private const string HuaBook =
        "第1话 起点\n正文一\n第2话 相遇\n正文二\n第4话 缺口\n正文四\n第4话 缺口\n正文四之二\n第7话 断线\n正文七";

    // 对照组：完全相同的形状，但是「章」——所有检查都该正常。
    private const string ZhangBook =
        "第1章 起点\n正文一\n第2章 相遇\n正文二\n第4章 缺口\n正文四\n第4章 缺口\n正文四之二\n第7章 断线\n正文七";

    /// <summary>
    /// 实测：`ParseKey` 对「第1话 起点」读出的 <c>Number</c> 是**空字符串**，
    /// 而 <c>Words</c> 里混着编号（`第1话起点`）—— 因为剥编号用的 `Numbering`
    /// 常量也不认「话」。这一条是整族问题的源头。
    /// </summary>
    [Fact]
    public void Numbers_are_unreadable_when_the_rule_uses_a_word_the_parser_does_not_know()
    {
        var hua = ReferenceOutline.ParseKey("第1话 起点");
        var zhang = ReferenceOutline.ParseKey("第1章 起点");

        Assert.Equal(string.Empty, hua.Number);
        Assert.Equal("第1话起点", hua.Words);
        Assert.Equal("1", zhang.Number);
        Assert.Equal("起点", zhang.Words);
    }

    [Fact]
    public async Task A_book_whose_numbers_cannot_be_read_is_reported()
    {
        var path = await WriteAsync(HuaBook);
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path, @"^第\d+话");

            var gap = RecognitionConsistency.Inspect(document);

            Assert.NotNull(gap);
            Assert.Equal(5, gap!.TitleCount);
            Assert.Equal("第1话 起点", gap.Example);
            Assert.Contains("跳章提示", gap.Describe());
            Assert.Contains("编号顺序检查", gap.Describe());
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// **反方向，也是最重要的一条**：同一形状的「第N章」不许报。
    /// 一条永远出现的提醒等于没有提醒 —— 这正是走查里"155 项"被无视的原因。
    /// </summary>
    [Fact]
    public async Task A_book_the_parser_understands_is_not_reported()
    {
        var path = await WriteAsync(ZhangBook);
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path, @"^第\d+章");

            Assert.Null(RecognitionConsistency.Inspect(document));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// 一两章的短文件不报：证据不足，而且"序章 + 番外"这类书本来就没编号。
    /// </summary>
    [Fact]
    public async Task A_very_short_book_is_left_alone()
    {
        var path = await WriteAsync("第1话 起点\n正文\n第2话 相遇\n正文");
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path, @"^第\d+话");

            Assert.Null(RecognitionConsistency.Inspect(document));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// **这一条是 §8.2 影响表的实测版。** 表格原来写的那些"会失效"是从代码读出来的，
    /// 现在有了对照：同一本书，只是把「章」换成「话」，检查项从 4 组缩到 1 组。
    /// </summary>
    [Fact]
    public async Task Swapping_the_heading_word_silently_removes_two_of_the_four_checks()
    {
        var zhangPath = await WriteAsync(ZhangBook);
        var huaPath = await WriteAsync(HuaBook);
        try
        {
            var zhang = await ChapterTreeDocument.LoadAsync(zhangPath, @"^第\d+章");
            var hua = await ChapterTreeDocument.LoadAsync(huaPath, @"^第\d+话");

            var zhangCodes = ChapterDiagnostics.Inspect(zhang).Select(issue => issue.Code).ToArray();
            var huaCodes = ChapterDiagnostics.Inspect(hua).Select(issue => issue.Code).ToArray();

            // 两本书的**形状完全一样**（跳号、重复标题、编号倒序都在），条目数也一样。
            Assert.Equal(zhang.Entries.Count, hua.Entries.Count);
            // 对照：跳章 ×2、重复、编号顺序。
            Assert.Contains("chapter_number_gap", zhangCodes);
            Assert.Contains("chapter_number_order", zhangCodes);
            Assert.Contains("chapter_duplicate", zhangCodes);
            // 实测：「话」那本只剩重复 —— 跳章提示与编号顺序检查**静默消失**。
            Assert.Equal(["chapter_duplicate"], huaCodes);
        }
        finally { File.Delete(zhangPath); File.Delete(huaPath); }
    }

    /// <summary>
    /// 预检必须把这件事报出来，而不是让用户在"没有问题"的报告里点转换。
    /// </summary>
    [Fact]
    public async Task The_pre_conversion_check_reports_it()
    {
        var path = await WriteAsync(HuaBook);
        try
        {
            var request = new ConversionRequest(path, path + ".epub",
                Options: new ConversionOptions { ChapterPattern = @"^第\d+话" });

            var report = await new ConversionPreflightInspector().InspectAsync([request]);

            var issue = Assert.Single(report.Issues, i => i.Code == PreflightIssueCodes.NumberingUnreadable);
            Assert.Equal(PreflightTargetKind.Chapters, issue.Target);
            Assert.Contains("跳章提示", issue.Message);
        }
        finally { File.Delete(path); }
    }
}
