using System.IO;
using System.Windows;
using System.Windows.Media;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class PreflightWindow : Window
{
    private readonly Action<ConversionPreflightIssue>? _navigate;
    private PreflightIssueRow[] _rows = [];

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
        ChapterSummaryText.Text = $"共识别 {report.Books.Sum(book => book.ChapterCandidateCount)} 个章节候选 · 信息 / 已安排处理 {report.Issues.Count(i => i.Severity == PreflightSeverity.Information)} 项（不计入待修复）";
        ResultBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            report.HasErrors ? "#FEECEE" : warnings > 0 ? "#FFF7E6" : "#ECFDF3"));
        ResultBadgeText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            report.HasErrors ? "#B42318" : warnings > 0 ? "#B54708" : "#067647"));
        ResultBadgeText.Text = report.HasErrors ? "需要修正" : warnings > 0 ? "可以继续" : "检查通过";
        ContinueButton.Visibility = allowContinue && !report.HasErrors ? Visibility.Visible : Visibility.Collapsed;

        _rows = report.Issues.Count == 0
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
        CategoryCombo.ItemsSource = new[] { "全部问题" }.Concat(_rows.Select(r => r.Category).Distinct());
        CategoryCombo.SelectedIndex = 0;
        RefreshRows();
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => RefreshRows();
    private void RefreshRows()
    {
        if (IssuesGrid is null || IncludeInformation is null) return;
        var category = CategoryCombo.SelectedItem as string;
        var filtered = _rows
            .Where(row => row.Issue is null || IncludeInformation.IsChecked == true || row.Issue.Severity != PreflightSeverity.Information)
            .Where(row => category is null or "全部问题" || row.Category == category)
            .ToArray();
        // Collapsing applies to the overview only: choosing one category is already a drill-down,
        // so those rows stay individually actionable.
        IssuesGrid.ItemsSource = category is null or "全部问题" ? Collapse(filtered) : filtered;
    }

    /// <summary>
    /// Folds same-category reminders of one book into a single summary row so a long report stays
    /// readable. The summary keeps the group's first issue, so locating it still works.
    ///
    /// Grouping is on the file name alone, without extension or directory: the same book reached the
    /// report through paths that differed only in those, and every issue then formed its own group —
    /// which is how one book's reminders ended up listed one per line with nothing folded.
    /// </summary>
    internal static PreflightIssueRow[] Collapse(PreflightIssueRow[] rows)
    {
        var collapsed = new List<PreflightIssueRow>();
        foreach (var group in rows.GroupBy(row => (row.Category, BookKey(row.BookName))))
        {
            var items = group.ToArray();
            if (items.Length == 1) { collapsed.Add(items[0]); continue; }
            collapsed.Add(items[0] with { Count = items.Length });
        }
        return collapsed.ToArray();
    }

    internal static string BookKey(string bookName) =>
        string.IsNullOrWhiteSpace(bookName) ? bookName : Path.GetFileNameWithoutExtension(bookName);

    private void IssuesGrid_SelectionChanged(object sender, RoutedEventArgs e) =>
        LocateButton.IsEnabled = _navigate is not null &&
            IssuesGrid.SelectedItem is PreflightIssueRow { Issue: not null };

    private void IssuesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => LocateSelected();
    private void Locate_Click(object sender, RoutedEventArgs e) => LocateSelected();
    private void HandleIssue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: PreflightIssueRow { Issue: { } issue } } || _navigate is null) return;
        _navigate(issue);
        Close();
    }

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
    ConversionPreflightIssue? Issue)
{
    /// <summary>
    /// How many reminders this row stands for. It gets its own column so the number is scannable —
    /// prefixed onto the message it pushed the actual evidence out of view.
    /// </summary>
    public int Count { get; init; } = 1;

    public string CountLabel => Count > 1 ? $"{Count} 条" : "";

    public bool CanNavigate => Issue is not null;
    public string Category => Issue is null ? "全部" : IssueCategory.For(Issue);

    /// <summary>
    /// 按钮说什么，由 <see cref="IssueResolutionPolicy"/> 算出的动作决定 —— 不再是一张
    /// 与工作台各自维护的 code 表。两个表面现在推荐**同一个动作**，只是措辞不同：
    /// 这里只能跳转，所以说「在工作台…」。
    /// </summary>
    public string ActionLabel => Issue is null ? "—" : PreflightCta.For(Issue);

    public static PreflightIssueRow From(ConversionPreflightIssue issue) => new(
        issue.Severity == PreflightSeverity.Error ? "需修正" : issue.Severity == PreflightSeverity.Warning ? "待核对" : "信息",
        string.IsNullOrWhiteSpace(issue.InputPath) ? "批量任务" : Path.GetFileName(issue.InputPath),
        issue.Message, issue);
}
