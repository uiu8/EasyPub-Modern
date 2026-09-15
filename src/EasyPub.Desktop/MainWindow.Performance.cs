using System.Globalization;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;

namespace EasyPub.Desktop;

public partial class MainWindow
{
    private void ApplyMotionSettings()
    {
        Resources["MotionEnabled"] = !_reduceMotion;
        if (Application.Current is { } application) application.Resources["MotionEnabled"] = !_reduceMotion;
        if (_reduceMotion)
            foreach (var panel in WorkspacePanels()) MotionEffects.ShowPage(panel, false);
    }

    private FrameworkElement[] WorkspacePanels() =>
        [InkLibraryPage, WorkflowReviewPage, InkCoverPage, InkLayoutPage, InkConvertPage, TaskCenterLanding];

    private void AnimateWorkspacePage(WorkspacePage page)
    {
        foreach (var panel in WorkspacePanels()) MotionEffects.ShowPage(panel, false);
        var target = page switch
        {
            WorkspacePage.Library => (FrameworkElement)InkLibraryPage,
            WorkspacePage.Review => WorkflowReviewPage,
            WorkspacePage.Cover => InkCoverPage,
            WorkspacePage.Layout => InkLayoutPage,
            WorkspacePage.Convert => InkConvertPage,
            _ => TaskCenterLanding,
        };
        MotionEffects.ShowPage(target, IsLoaded && !_reduceMotion);
    }

    private double? _preferredLayoutSettingsWidth;

    private void InitializePreviewSplitter()
    {
        LayoutSettingsColumn.MinWidth = 280;
        LayoutSettingsColumn.MaxWidth = 480;
        var splitter = new GridSplitter
        {
            Name = "LayoutPreviewSplitter", Width = 10,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Columns,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ShowsPreview = true, Focusable = true,
            ToolTip = "拖动调整设置与预览宽度；方向键微调，双击恢复默认",
        };
        AutomationProperties.SetName(splitter, "调整排版设置与预览宽度");
        splitter.SetResourceReference(StyleProperty, "WorkspaceSplitter");
        Grid.SetColumn(splitter, 3);
        splitter.DragCompleted += (_, _) => _preferredLayoutSettingsWidth = LayoutSettingsColumn.ActualWidth;
        splitter.KeyUp += (_, args) =>
        {
            if (args.Key is Key.Left or Key.Right) _preferredLayoutSettingsWidth = LayoutSettingsColumn.ActualWidth;
        };
        splitter.MouseDoubleClick += (_, args) =>
        {
            _preferredLayoutSettingsWidth = null;
            LayoutSettingsColumn.Width = new GridLength(_compactLayout ? 310 : 350);
            args.Handled = true;
        };
        RegisterName(splitter.Name, splitter);
        InkLayoutPage.Children.Add(splitter);
    }

    private readonly DispatcherTimer _layoutPreviewTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(200),
    };
    private DispatcherOperation? _analysisUiRefresh;
    private LayoutPreviewPaginationKey? _layoutPreviewPaginationKey;

    private sealed record LayoutPreviewPaginationKey(string Title, IReadOnlyList<string> Paragraphs,
        double Width, double Height, double FontSize, double LineHeight, int Indent);

    private void RequestLayoutPreviewRefresh()
    {
        _layoutPreviewTimer.Stop();
        _layoutPreviewTimer.Start();
    }

    private bool HasValidLayoutPreviewInput()
    {
        foreach (var field in new[] { FontSizeText, LineHeightText, ParagraphSpacingText,
                     IndentText, PageMarginTopText, PageMarginBottomText, PageMarginLeftText, PageMarginRightText })
        {
            if (field is null) continue;
            if (!double.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || !double.IsFinite(value)) return false;
            if ((field == FontSizeText || field == LineHeightText) && value <= 0) return false;
        }
        return true;
    }

    private void RequestAnalysisUiRefresh()
    {
        if (_analysisUiRefresh?.Status == DispatcherOperationStatus.Pending) return;
        _analysisUiRefresh = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsLoaded) return;
            // Preserve selection/metadata edits: analysis updates only its own portion of the inspector.
            if (CurrentLibraryInspectorBook() is { } book) UpdateSelectedBookAnalysis(book);
            _bookWorklistView?.Refresh();
            UpdateConversionSummary();
        }));
    }

    private void UpdateSelectedBookAnalysis(InputBookItem book)
    {
        UpdateSelectedBookInspectorSummaryOnly(book);
        SelectedBookReadinessText.Text = book.ReadinessLabel;
        SelectedBookReadinessText.Foreground = book.ReadinessForeground;
        SelectedBookAnalysisText.Text = book.ReadinessDetail;
        ViewSelectedBookIssuesButton.IsEnabled = book.HasBeenChecked;
        ViewSelectedBookIssuesButton.Content = book.HasPreflightIssues ? "查看并处理" : "查看结果";
    }
}
