using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class ConversionHistoryWindow : Window
{
    public ObservableCollection<ConversionHistoryRow> Rows { get; }
    public IReadOnlyList<string> RetryInputPaths { get; private set; } = [];

    public ConversionHistoryWindow(IReadOnlyList<ConversionHistoryEntry> entries)
    {
        InitializeComponent();
        Rows = new ObservableCollection<ConversionHistoryRow>(entries.Select(entry => new ConversionHistoryRow(entry)));
        HistoryGrid.ItemsSource = Rows;
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        var selectedFailures = HistoryGrid.SelectedItems
            .Cast<ConversionHistoryRow>()
            .Where(row => !row.Entry.Succeeded)
            .ToArray();
        var candidates = selectedFailures.Length > 0
            ? selectedFailures
            : Rows.Where(row => !row.Entry.Succeeded).ToArray();
        RetryInputPaths = candidates
            .Select(row => row.Entry.InputPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (RetryInputPaths.Count == 0)
        {
            InkDialog.Show(this, "历史中没有可载入的失败项目。", "EasyPub Modern");
            return;
        }
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class ConversionHistoryRow(ConversionHistoryEntry entry)
{
    public ConversionHistoryEntry Entry { get; } = entry;
    public string TimeText => Entry.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string StatusText => Entry.Succeeded ? "成功" : "失败";
    public string FileName => Path.GetFileName(Entry.InputPath);
    public string OutputName => Path.GetFileName(Entry.OutputPath);
    public string ChapterText => Entry.ChapterCount?.ToString() ?? "—";
    public string SizeText => Entry.OutputBytes is long bytes ? $"{bytes / 1024d:F1} KB" : "—";
    public string Detail => Entry.Succeeded ? $"耗时 {Entry.ElapsedMilliseconds ?? 0} ms" : Entry.ErrorMessage ?? "未知错误";
}
