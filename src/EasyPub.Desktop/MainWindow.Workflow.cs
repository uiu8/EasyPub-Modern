using System.Windows;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class MainWindow
{
    private bool _workflowChecking;
    private static readonly string[] WorkflowCategories =
    [
        "全部问题", .. ReviewCategories.All, "广告 / 文本清理", "文件 / 制作设置",
    ];

    private void NavigateReview_Click(object sender, RoutedEventArgs e)
    {
        ShowWorkspacePage(WorkspacePage.Review);
        if (InputBooks.Count > 0) _ = CheckWorkflowAsync();
    }

    private void WorkflowFilter_Changed(object sender, RoutedEventArgs e) => RefreshWorkflowRows();
    private async void WorkflowRefresh_Click(object sender, RoutedEventArgs e) => await CheckWorkflowAsync();

    private async Task CheckWorkflowAsync()
    {
        if (_workflowChecking || InputBooks.Count == 0) return;
        _workflowChecking = true;
        WorkflowRefreshButton.IsEnabled = false;
        WorkflowSummaryText.Text = "正在检查全部书稿…";
        var generation = Interlocked.Increment(ref _automaticAnalysisGeneration);
        _automaticAnalysisCancellation?.Cancel();
        _automaticAnalysisTimer.Stop();
        try
        {
            var requests = await BuildConversionRequestsForBooksAsync(InputBooks.ToArray(), resolveCollisions: false, enforceCompatibility: false);
            var result = await Task.Run(() => _analysisCoordinator.AnalyzeAsync(requests));
            if (generation != Interlocked.Read(ref _automaticAnalysisGeneration))
            {
                WorkflowSummaryText.Text = "书稿或设置已变化，旧检查结果未应用；请重新检查。";
                return;
            }
            ApplyAnalysisToWorklist(result);
            _lastPreflightReport = result.Report;
            _taskCenterWindow?.UpdatePreflight(result.Report);
        }
        catch (Exception ex) { WorkflowSummaryText.Text = "检查未完成：" + ex.Message; }
        finally
        {
            _workflowChecking = false;
            WorkflowRefreshButton.IsEnabled = true;
            if (generation != Interlocked.Read(ref _automaticAnalysisGeneration) && _workspacePage == WorkspacePage.Review)
                _automaticAnalysisTimer.Start();
        }
    }

    private void RefreshWorkflowRows()
    {
        if (WorkflowCategoryCombo is null || WorkflowIssuesGrid is null || WorkflowSummaryText is null) return;
        if (WorkflowCategoryCombo.ItemsSource is null)
        {
            WorkflowCategoryCombo.ItemsSource = WorkflowCategories;
            WorkflowCategoryCombo.SelectedIndex = 0;
        }
        var issues = InputBooks.SelectMany(book => book.PreflightIssues).Distinct().ToArray();
        var category = WorkflowCategoryCombo.SelectedItem as string;
        var rows = issues
            .Where(issue => WorkflowIncludeInformation.IsChecked == true || issue.Severity != PreflightSeverity.Information)
            .Where(issue => category is null or "全部问题" || IssueCategory.For(issue) == category)
            .OrderByDescending(issue => issue.Severity)
            .ThenBy(issue => issue.InputPath).ThenBy(issue => issue.LineNumber)
            .Select(PreflightIssueRow.From).ToArray();
        // Same folding the preflight report uses. Without it the overview listed every reminder of
        // every book one per line, so a single book with forty near-identical chapter-gap warnings
        // filled the table and buried the other books' problems entirely. Choosing one category is
        // already a drill-down, so those rows stay individual and actionable.
        WorkflowIssuesGrid.ItemsSource = category is null or "全部问题"
            ? PreflightWindow.Collapse(rows)
            : rows;
        var pending = InputBooks.Count(book => !book.HasBeenChecked || book.AnalysisStatus is BookAnalysisStatus.Pending or BookAnalysisStatus.Running);
        WorkflowSummaryText.Text = InputBooks.Count == 0 ? "请先导入书稿。"
            : $"共 {InputBooks.Count} 本 · 必须修正 {issues.Count(i => i.Severity == PreflightSeverity.Error)} · 待核对 {issues.Count(i => i.Severity == PreflightSeverity.Warning)} · 信息 / 已安排处理 {issues.Count(i => i.Severity == PreflightSeverity.Information)}"
              + (pending > 0 ? $" · {pending} 本待更新，请重新检查" : " · 当前筛选无条目不代表全书无问题");
    }

    private void WorkflowIssue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: PreflightIssueRow { Issue: { } issue } })
        {
            var book = InputBooks.FirstOrDefault(b => string.Equals(b.InputPath, issue.InputPath, StringComparison.OrdinalIgnoreCase));
            if (_workflowChecking || book?.AnalysisStatus is BookAnalysisStatus.Pending or BookAnalysisStatus.Running)
            {
                WorkflowSummaryText.Text = "书稿正在重新检查，请等待新结果后再处理。";
                return;
            }
            NavigateToPreflightIssue(issue);
        }
    }

    private void ManageSourceBackups_Click(object sender, RoutedEventArgs e) =>
        new SourceBackupWindow(InputBooks.Where(b => !b.IsEpub).Select(b => b.InputPath).ToArray()) { Owner = this }.ShowDialog();
}
