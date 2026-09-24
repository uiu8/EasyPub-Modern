using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

/// <summary>
/// 章节工作台中间的“待核对”摘要。它只改变导航呈现，不建立第二套诊断逻辑：
/// 数字来自 ChapterReviewAnalyzer 已生成的同一批提醒，点击卡片仍然回到原有筛选和处理链。
/// </summary>
public partial class ChapterEditorWindow
{
    private void UpdateIssueOverview()
    {
        if (IssueOverviewSummaryText is null) return;

        var structure = _allReviewIssues.Count(issue => ReviewCategory(issue) == ReviewCategories.Structure);
        var duplicate = _allReviewIssues.Count(issue => ReviewCategory(issue) == ReviewCategories.Duplicate);
        var missing = _allReviewIssues.Count(issue => ReviewCategory(issue) == ReviewCategories.Missing);
        var bodyMissing = _allReviewIssues.Count(issue =>
            issue.Code.Contains("content", StringComparison.OrdinalIgnoreCase)
            && issue.Message.Contains("未找到", StringComparison.Ordinal));

        IssueRelocationCountText.Text = structure.ToString();
        IssueDuplicateCountText.Text = duplicate.ToString();
        IssueMissingCountText.Text = missing.ToString();
        IssueBodyMissingCountText.Text = bodyMissing.ToString();
        IssueOverviewSummaryText.Text = _allReviewIssues.Length == 0 ? "暂无提醒" : $"{_allReviewIssues.Length} 组";
    }

    private void IssueOverviewCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || !int.TryParse(button.Tag as string, out var category)) return;

        // 中间卡片是现有筛选器的快捷入口，不另造一套“卡片选中”状态。
        ReviewOnlyCheck.IsChecked = true;
        AllChaptersRadio.IsChecked = false;
        if (IssueCategoryCombo.SelectedIndex != category)
            IssueCategoryCombo.SelectedIndex = category;
        else
        {
            ClearReviewResult();
            RefreshSuggestions(Flatten().Select(node => node.ToEntry()).ToArray());
            UpdateReviewVisibility(userChange: true);
            UpdateActionButtons();
        }
    }

    private void IssueOverviewAll_Click(object sender, RoutedEventArgs e)
    {
        ReviewOnlyCheck.IsChecked = false;
        AllChaptersRadio.IsChecked = true;
        ClearReviewResult();
        UpdateReviewVisibility(userChange: true);
        UpdateActionButtons();
    }

    private void IssueOverviewActionable_Click(object sender, RoutedEventArgs e)
    {
        ReviewOnlyCheck.IsChecked = true;
        AllChaptersRadio.IsChecked = false;
        ClearReviewResult();
        UpdateReviewVisibility(userChange: true);
        UpdateActionButtons();
    }

    private void IssueOverviewConfirm_Click(object sender, RoutedEventArgs e)
    {
        // “需确认”与“可处理”目前共享待核对树筛选；每条详情仍由既有策略给出证据状态。
        ReviewOnlyCheck.IsChecked = true;
        AllChaptersRadio.IsChecked = false;
        ClearReviewResult();
        UpdateReviewVisibility(userChange: true);
        UpdateActionButtons();
    }
}
