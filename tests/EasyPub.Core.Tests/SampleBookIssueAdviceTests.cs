using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 策略在六卷缺陷样书上的实际行为。
///
/// <para>它同时是探针和回归夹具。样书**只产生四类提醒**，而这四类恰好覆盖四种不同的性质 ——
/// 内容重复（目录证据推翻不了）、跳章（本地候选与参考目录两条路）、重复拼接（只能人工核对）、
/// 结构建议（必须先在预览里看清）—— 策略对它们的取舍就是这份设计的核心主张。</para>
///
/// <para>数字来自 2026-09-18 的实测，与 <see cref="SampleBookRegressionTests"/> 的基线同源。</para>
/// </summary>
public class SampleBookIssueAdviceTests
{
    private static string SampleRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "六卷缺陷样书");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 samples/六卷缺陷样书，测试必须能在仓库内定位它。");
    }

    private static async Task<(ChapterTreeDocument Document, ReferenceCatalog Catalog)> LoadAsync()
    {
        var root = SampleRoot();
        var document = await ChapterTreeDocument.LoadAsync(Path.Combine(root, "缺陷样书.txt"));
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8));
        Assert.NotNull(catalog);
        return (document, catalog!);
    }

    [Fact]
    public async Task The_sample_book_produces_four_kinds_of_reminders()
    {
        var (document, _) = await LoadAsync();
        var analysis = ChapterReviewAnalyzer.Analyze(document);
        Assert.Equal(44, analysis.Groups.Count);
        Assert.Equal(
            ["chapter_content_duplicate", "chapter_number_gap", "chapter_repeated_sequence", "chapter_structure_suggested"],
            analysis.Groups.Select(group => group.Issue.Code).Distinct().Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(32, analysis.Groups.Count(group => group.Issue.Code == "chapter_content_duplicate"));
        Assert.Equal(9, analysis.Groups.Count(group => group.Issue.Code == "chapter_number_gap"));
        Assert.Equal(2, analysis.Groups.Count(group => group.Issue.Code == "chapter_repeated_sequence"));
    }

    /// <summary>
    /// 归类：一个码属于哪一类，只有 <see cref="IssueResolutionPolicy.All"/> 一处定义。
    ///
    /// <para><c>chapter_number_gap</c> 这一格是<b>行为变更</b>：工作台原来把它归成「编号与层级」
    /// 或「误识别」（取决于该节点是不是数字章节），而它一直是「漏识别」。报告窗口那一份则漏掉了
    /// 三个新码，让它们全部落进「其他提醒」。</para>
    /// </summary>
    [Fact]
    public async Task Every_reminder_on_the_sample_book_gets_one_category()
    {
        var (document, _) = await LoadAsync();
        var analysis = ChapterReviewAnalyzer.Analyze(document);
        var expected = new Dictionary<string, string>
        {
            ["chapter_content_duplicate"] = ReviewCategories.Duplicate,
            ["chapter_number_gap"] = ReviewCategories.Missing,
            ["chapter_repeated_sequence"] = ReviewCategories.Duplicate,
            ["chapter_structure_suggested"] = ReviewCategories.Structure,
        };
        foreach (var group in analysis.Groups)
        {
            Assert.Equal(expected[group.Issue.Code], group.Category);
            // 报告窗口那一份必须给出同一个答案。
            Assert.Equal(group.Category, IssueCategory.For(group.Issue));
        }
    }

    /// <summary>
    /// 推荐：调用方**不知道**本地有没有可补建的候选时（报告窗口、任务中心），
    /// 跳章推荐的是「到原文核对」，而不是替工作台承诺"去获取参考目录"。
    /// </summary>
    [Fact]
    public async Task Without_local_evidence_the_sample_book_never_promises_a_catalog_will_help()
    {
        var (document, _) = await LoadAsync();
        var analysis = ChapterReviewAnalyzer.Analyze(document);
        var expected = new Dictionary<string, ResolutionIntent>
        {
            ["chapter_content_duplicate"] = ResolutionIntent.CompareDuplicateBodies,
            ["chapter_number_gap"] = ResolutionIntent.NavigateToSource,
            ["chapter_repeated_sequence"] = ResolutionIntent.NavigateToSource,
            ["chapter_structure_suggested"] = ResolutionIntent.RecoverStructure,
        };
        foreach (var group in analysis.Groups)
        {
            var advice = IssueResolutionPolicy.Evaluate(group, ChapterRepairContextSnapshot.Unknown);
            Assert.Equal(expected[group.Issue.Code], advice.RecommendedIntent);
            // 每条提醒都要有一句人能读懂的解释，否则卡片上会出现一个没有理由的按钮。
            Assert.False(string.IsNullOrWhiteSpace(advice.Summary));
        }
    }

    /// <summary>
    /// 工作台手里有文档 —— 它**查过**本地候选，所以那九条跳章会按查到的结果分成两种建议，
    /// 而不是笼统地推给目录。
    /// </summary>
    [Fact]
    public async Task With_local_evidence_a_gap_offers_both_exits_and_says_which_one_it_checked()
    {
        var (document, catalog) = await LoadAsync();
        var analysis = ChapterReviewAnalyzer.Analyze(document);
        var gap = analysis.Groups.First(group => group.Issue.Code == "chapter_number_gap");

        var hasCandidate = IssueResolutionPolicy.Evaluate(gap, ChapterRepairContextSnapshot.Unknown,
            new IssueResolutionFacts(HasLocalHeadingCandidate: true));
        Assert.Equal(ResolutionIntent.RepairMissingHeadings, hasCandidate.RecommendedIntent);
        // 本地核对不等于确认缺章 —— 参考目录那条路必须仍然摆在那里。
        Assert.Contains(hasCandidate.AvailableActions, a => a.Intent == ResolutionIntent.AcquireCatalog);

        var noCandidate = IssueResolutionPolicy.Evaluate(gap, ChapterRepairContextSnapshot.Unknown,
            new IssueResolutionFacts(HasLocalHeadingCandidate: false));
        Assert.Equal(ResolutionIntent.AcquireCatalog, noCandidate.RecommendedIntent);
        Assert.Equal(CatalogRole.RequiredForConclusion,
            noCandidate.Actions().First(a => a.Intent == ResolutionIntent.AcquireCatalog).CatalogRole);

        // 已经选用目录时，"去获取目录"这件事本身就不该再出现。
        var selected = IssueResolutionPolicy.Evaluate(gap, ChapterRepairContextSnapshot.Unknown with
        {
            CatalogAvailability = CatalogAvailability.Selected,
            CatalogSource = "local",
            CatalogChapterCount = catalog.Titles.Count,
        }, new IssueResolutionFacts(HasLocalHeadingCandidate: false));
        Assert.Equal(ResolutionIntent.AdoptCatalogChapters, selected.RecommendedIntent);
        Assert.DoesNotContain(selected.AvailableActions, a => a.Intent == ResolutionIntent.AcquireCatalog);
    }
}

file static class AdviceExtensions
{
    internal static IReadOnlyList<ResolutionAction> Actions(this ResolutionAdvice advice) => advice.AvailableActions;
}
