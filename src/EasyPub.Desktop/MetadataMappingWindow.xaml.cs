using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class MetadataMappingWindow : Window
{
    public MetadataMappingWindow(IReadOnlyList<FolderMetadataRule> rules)
    {
        InitializeComponent();
        Rules = new ObservableCollection<FolderMetadataRule>(rules);
        DataContext = this;
    }

    public ObservableCollection<FolderMetadataRule> Rules { get; }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var editor = new MetadataMappingRuleWindow(null) { Owner = this };
        if (editor.ShowDialog() != true || editor.Rule is null) return;
        ReplaceSameFolder(editor.Rule);
        RulesGrid.SelectedItem = editor.Rule;
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
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is FolderMetadataRule selected) Rules.Remove(selected);
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

    private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
