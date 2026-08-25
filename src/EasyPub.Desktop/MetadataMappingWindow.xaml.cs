using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class MetadataMappingWindow : Window
{
    private readonly IReadOnlyList<string> _inputPaths;

    public MetadataMappingWindow(IReadOnlyList<FolderMetadataRule> rules, IReadOnlyList<string>? inputPaths = null)
    {
        InitializeComponent();
        _inputPaths = inputPaths ?? [];
        Rules = new ObservableCollection<FolderMetadataRule>(rules);
        PreviewRows = new ObservableCollection<MetadataMappingPreview>();
        DataContext = this;
        RefreshPreview();
    }

    public ObservableCollection<FolderMetadataRule> Rules { get; }
    public ObservableCollection<MetadataMappingPreview> PreviewRows { get; }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var editor = new MetadataMappingRuleWindow(null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Rule is null) return;
        ReplaceSameFolder(editor.Rule);
        RulesGrid.SelectedItem = editor.Rule;
        RefreshPreview();
    }

    private void EditRule_Click(object sender, RoutedEventArgs e) => EditSelectedRule();

    private void RulesGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditSelectedRule();

    private void EditSelectedRule()
    {
        if (RulesGrid.SelectedItem is not FolderMetadataRule selected)
        {
            MessageBox.Show(this, "请先选择一条映射规则。", "EasyPub Modern");
            return;
        }

        var editor = new MetadataMappingRuleWindow(selected) { Owner = this };
        if (editor.ShowDialog() != true || editor.Rule is null) return;
        Rules.Remove(selected);
        ReplaceSameFolder(editor.Rule);
        RulesGrid.SelectedItem = editor.Rule;
        RefreshPreview();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is FolderMetadataRule selected)
        {
            Rules.Remove(selected);
            RefreshPreview();
        }
    }

    private void ReplaceSameFolder(FolderMetadataRule rule)
    {
        var duplicate = Rules.FirstOrDefault(candidate => string.Equals(
            candidate.FolderPath,
            rule.FolderPath,
            StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null) Rules.Remove(duplicate);
        Rules.Add(rule);
    }

    private void RefreshPreview()
    {
        PreviewRows.Clear();
        foreach (var row in MetadataMappingResolver.Preview(_inputPaths, Rules)) PreviewRows.Add(row);
        var matched = PreviewRows.Count(row => row.MatchedRule is not null);
        var overlaps = PreviewRows.Count(row => row.HasOverlap);
        PreviewSummaryText.Text = _inputPaths.Count == 0
            ? "当前项目还没有书稿；添加书稿后会在这里显示实际命中结果。"
            : $"{PreviewRows.Count} 本 · 命中 {matched} 本 · 重叠 {overlaps} 本。重叠时自动采用路径最具体的子文件夹规则。";
    }

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
