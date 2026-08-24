using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class TaskCenterWindow : Window
{
    private readonly ObservableCollection<BookTaskViewModel> _tasks;

    public TaskCenterWindow(ObservableCollection<BookTaskViewModel> tasks)
    {
        InitializeComponent();
        _tasks = tasks;
        DataContext = tasks;
        if (tasks.Count > 0) TasksGrid.SelectedIndex = 0;
        UpdateSummary();
    }

    public event Action<string>? RetryRequested;

    public void RefreshSummary()
    {
        UpdateSummary();
        UpdateSelectedDetails();
    }

    private BookTaskViewModel? Selected => TasksGrid.SelectedItem as BookTaskViewModel;

    private void TasksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectedDetails();

    private void UpdateSelectedDetails()
    {
        var item = Selected;
        IssueTitleText.Text = item is null ? "选择一本书查看检查结果" : item.DisplayName;
        IssuesList.ItemsSource = item?.Issues;
        HardwareText.Text = item?.RequiresHardwareConfirmation == true
            ? "此处只确认文件结构；Kindle 真机显示仍需用户实际打开确认。"
            : string.Empty;
        ReportPathText.Text = item?.ReportPath is null ? string.Empty : $"报告：{item.ReportPath}";
        ReportPathText.ToolTip = item?.ReportPath;
    }

    private void OpenReport_Click(object sender, RoutedEventArgs e)
    {
        var path = Selected?.ReportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "所选任务还没有可打开的成品报告。", "EasyPub Modern");
            return;
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        var output = Selected?.OutputPath;
        if (string.IsNullOrWhiteSpace(output)) return;
        var directory = Path.GetDirectoryName(output);
        if (directory is not null && Directory.Exists(directory))
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{output}\"") { UseShellExecute = true });
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { CanRetry: true } item) return;
        RetryRequested?.Invoke(item.InputPath);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateSummary()
    {
        SummaryText.Text = _tasks.Count == 0
            ? "等待任务"
            : $"{_tasks.Count} 本 · 完成 {_tasks.Count(item => item.Stage is BookTaskStage.Completed or BookTaskStage.Warning)} · 失败 {_tasks.Count(item => item.Stage == BookTaskStage.Failed)}";
    }
}

public sealed class BookTaskViewModel : INotifyPropertyChanged
{
    private BookTaskStage _stage;
    private double _progress;
    private string _statusText = "等待";
    private string _validationText = "尚未验收";
    private string? _reportPath;
    private bool _requiresHardwareConfirmation;

    public BookTaskViewModel(string inputPath, string outputPath)
    {
        InputPath = inputPath;
        OutputPath = outputPath;
    }

    public string InputPath { get; }
    public string OutputPath { get; }
    public string DisplayName => Path.GetFileNameWithoutExtension(InputPath);
    public string OutputName => Path.GetFileName(OutputPath);
    public ObservableCollection<string> Issues { get; } = [];
    public BookTaskStage Stage { get => _stage; private set => SetField(ref _stage, value); }
    public double Progress { get => _progress; private set => SetField(ref _progress, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }
    public string ValidationText { get => _validationText; private set => SetField(ref _validationText, value); }
    public string? ReportPath { get => _reportPath; private set => SetField(ref _reportPath, value); }
    public bool RequiresHardwareConfirmation { get => _requiresHardwareConfirmation; private set => SetField(ref _requiresHardwareConfirmation, value); }
    public bool CanRetry => Stage is BookTaskStage.Failed or BookTaskStage.Warning or BookTaskStage.Cancelled;

    public void Update(BookTaskStage stage, double progress, string status, ArtifactValidationReport? validation = null)
    {
        Stage = stage;
        Progress = stage is BookTaskStage.Completed or BookTaskStage.Warning or BookTaskStage.Failed or BookTaskStage.Cancelled
            ? 1 : Math.Clamp(progress, 0, 1);
        StatusText = StageLabel(stage, status);
        if (validation is not null)
        {
            ValidationText = validation.ResultLabel;
            ReportPath = validation.ReportPath;
            RequiresHardwareConfirmation = validation.RequiresKindleHardwareConfirmation;
            Issues.Clear();
            foreach (var issue in validation.Issues)
                Issues.Add($"{SeverityLabel(issue.Severity)}  {issue.Message}");
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanRetry)));
    }

    public void SetFailure(string message, bool cancelled = false)
    {
        Update(cancelled ? BookTaskStage.Cancelled : BookTaskStage.Failed, 1, cancelled ? "已取消" : "转换失败");
        Issues.Clear();
        Issues.Add(message);
        ValidationText = cancelled ? "未验收" : "未生成可验收成品";
    }

    private static string StageLabel(BookTaskStage stage, string detail) => stage switch
    {
        BookTaskStage.Waiting => "等待",
        BookTaskStage.Checking => "检查",
        BookTaskStage.GeneratingEpub => "生成 EPUB",
        BookTaskStage.GeneratingMobi => "生成 MOBI",
        BookTaskStage.Validating => "验收",
        BookTaskStage.Completed => "完成",
        BookTaskStage.Warning => "完成（有提醒）",
        BookTaskStage.Failed => "失败",
        BookTaskStage.Cancelled => "已取消",
        _ => detail,
    };

    private static string SeverityLabel(ArtifactValidationSeverity severity) => severity switch
    {
        ArtifactValidationSeverity.Error => "错误",
        ArtifactValidationSeverity.Warning => "提醒",
        _ => "通过",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
