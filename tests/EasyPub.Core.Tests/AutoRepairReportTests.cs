using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// The repair confirmation window has to answer "what actually changed" in one screen, which needs
/// the outcome to carry the numbers separately instead of burying them in one verdict sentence. The
/// case that forced this: an aggregator directory collects the author's leave notices ("请假一天")
/// alongside real chapters, so a book missing twelve chapters reported 264, and the twelve worth
/// reading were pushed hundreds of lines down a flat list.
/// </summary>
public class AutoRepairReportTests
{
    private static async Task<AutoRepairOutcome> RepairAsync(string source, string catalogText)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-report-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, source);
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            return ChapterAutoRepair.Prepare(document, ReferenceCatalogInput.ParseText(catalogText));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Unmatched_entries_without_a_chapter_number_are_announcements_not_missing_chapters()
    {
        var outcome = await RepairAsync(
            "第1章 起点\n正文\n第2章 继续\n正文",
            "第1章 起点\n第2章 继续\n第3章 断线\n请假一天\n今天的更新也会晚一些");

        Assert.Equal(2, outcome.AlignedCount);
        Assert.Equal(1, outcome.UnmatchedWithNumber);
        Assert.Equal(2, outcome.UnmatchedNoNumber);
        // The total stays intact: "264 not located" is still true, and the split is what makes it readable.
        Assert.Equal(3, outcome.Missing);

        var announcements = Assert.Single(outcome.Report, group => group.Label == "疑似公告");
        Assert.Equal(2, announcements.Count);
        Assert.Equal(["请假一天", "今天的更新也会晚一些"], announcements.Items.Select(item => item.Title));
        Assert.All(announcements.Items, item => Assert.Null(item.Kind));

        var unmatched = Assert.Single(outcome.Report, group => group.Label == "参考目录未匹配");
        Assert.Equal(["第3章 断线"], unmatched.Items.Select(item => item.Title));
        Assert.All(unmatched.Items, item => Assert.Null(item.Kind));
    }

    [Fact]
    public async Task Every_checkbox_the_window_draws_maps_back_to_exactly_one_planned_action()
    {
        var outcome = await RepairAsync(
            "第1章 起点\n正文\n第2章 继续\n正文\n他忽然想起那年的雨\n随笔正文\n番外 人物卡\n卡片正文",
            "第1章 起点\n第2章 继续\n第3章 断线\n请假一天");

        var plan = outcome.Plan;
        Assert.NotNull(plan);

        // Deselecting a row must be able to reach the action behind it. When nothing matches, the
        // checkbox looks interactive and silently does nothing — the failure this window exists to fix.
        foreach (var item in outcome.Report.SelectMany(group => group.Items).Where(item => item.Kind is not null))
        {
            var matches = plan!.Actions.Where(action => action.Kind == item.Kind && action.Line == item.Line).ToArray();
            Assert.True(matches.Length == 1,
                $"「{item.Title}」映射到 {matches.Length} 个动作，勾选框会失去意义");
            // The node's preselection is the plan's, not a second opinion computed for display.
            Assert.Equal(matches[0].Recommended, item.Recommended);
            Assert.Equal(matches[0].Recommended, plan.DefaultSelection.Contains(matches[0]));
        }

        // A row nobody can select — an entry the directory never located — must not claim it can.
        foreach (var item in outcome.Report.SelectMany(group => group.Items).Where(item => item.Kind is null))
            Assert.DoesNotContain(plan!.Actions, action => action.Line == item.Line && action.Recommended);
    }

    [Fact]
    public async Task A_directory_whose_chapters_all_carry_a_number_reports_no_announcements()
    {
        var outcome = await RepairAsync(
            "第1章 起点\n正文",
            "第1章 起点\n第2章 续章\n第3章 断线");

        Assert.Equal(2, outcome.UnmatchedWithNumber);
        Assert.Equal(0, outcome.UnmatchedNoNumber);
        Assert.DoesNotContain(outcome.Report, group => group.Label == "疑似公告");
    }

    [Fact]
    public async Task A_directory_of_only_announcements_leaves_nothing_to_check_by_hand()
    {
        var outcome = await RepairAsync(
            "第1章 起点\n正文",
            "第1章 起点\n请假一天\n休更一天");

        Assert.Equal(0, outcome.UnmatchedWithNumber);
        Assert.Equal(2, outcome.UnmatchedNoNumber);
        Assert.DoesNotContain(outcome.Report, group => group.Label == "参考目录未匹配");
        Assert.Single(outcome.Report, group => group.Label == "疑似公告");
    }

    [Fact]
    public async Task The_verdict_sentence_and_the_split_numbers_never_disagree()
    {
        var outcome = await RepairAsync(
            "第1章 起点\n正文\n第2章 继续\n正文",
            "第1章 起点\n第2章 继续\n第3章 断线\n请假一天");

        // The window shows the sentence and the four figures side by side; a drift between them would
        // be worse than either alone, so the sentence is composed from the same fields.
        Assert.Contains($"已对齐 {outcome.AlignedCount}/{outcome.ReferenceChapters} 章", outcome.Verdict);
        Assert.Contains($"未定位 {outcome.UnmatchedWithNumber + outcome.UnmatchedNoNumber} 章", outcome.Verdict);
    }
}
