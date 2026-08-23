using System.IO;
using System.Windows;
using System.Windows.Media;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class PreflightWindow : Window
{
    private readonly Action<ConversionPreflightIssue>? _navigate;

    public PreflightWindow(
        ConversionPreflightReport report,
        bool allowContinue,
        Action<ConversionPreflightIssue>? navigate = null)
    {
        InitializeComponent();
        _navigate = navigate;
        var errors = report.Issues.Count(issue => issue.Severity == PreflightSeverity.Error);
        var warnings = report.WarningCount;
        SummaryText.Text = $"已检查 {report.Books.Count} 本可读取书稿 · {errors} 个错误 · {warnings} 个提醒";
        ChapterSummaryText.Text = $"共识别 {report.Books.Sum(book => book.ChapterCandidateCount)} 个章节候选";
        ResultBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            report.HasErrors ? "#FEECEE" : warnings > 0 ? "#FFF7E6" : "#ECFDF3"));
        ResultBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            report.HasErrors ? "#B42318" : warnings > 0 ? "#B54708" : "#067647"));
        ResultBadgeText.Text = report.HasErrors ? "需要修正" : warnings > 0 ? "可以继续" : "检查通过";
        ContinueButton.Visibility = allowContinue && !report.HasErrors ? Visibility.Visible : Visibility.Collapsed;

        IssuesGrid.ItemsSource = report.Issues.Count == 0
            ? [new PreflightIssueRow("通过", "全部书稿", "没有发现阻止转换的问题。", null)]
            : report.Issues.Select(issue => new PreflightIssueRow(
                issue.Severity switch
                {
                    PreflightSeverity.Error => "错误",
                    PreflightSeverity.Warning => "提醒",
                    _ => "信息",
                },
                string.IsNullOrWhiteSpace(issue.InputPath) ? "批量任务" : Path.GetFileName(issue.InputPath),
                issue.Message,
                issue)).ToArray();
    }

    private void IssuesGrid_SelectionChanged(object sender, RoutedEventArgs e) =>
        LocateButton.IsEnabled = _navigate is not null &&
            IssuesGrid.SelectedItem is PreflightIssueRow { Issue: not null };

    private void IssuesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LocateSelected();
    private void Locate_Click(object sender, RoutedEventArgs e) => LocateSelected();

    private void LocateSelected()
    {
        if (_navigate is null || IssuesGrid.SelectedItem is not PreflightIssueRow { Issue: { } issue }) return;
        _navigate(issue);
        Close();
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed record PreflightIssueRow(
    string SeverityText,
    string BookName,
    string Message,
    ConversionPreflightIssue? Issue);
