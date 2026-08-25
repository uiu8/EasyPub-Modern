using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class MetadataMappingWindow : Window
{
    private readonly IReadOnlyList<CalibreCustomMetadata> _customMetadataDefinitions;

    public MetadataMappingWindow(
        IReadOnlyList<FolderMetadataRule> rules,
        IReadOnlyList<CalibreCustomMetadata>? customMetadataDefinitions = null)
    {
        InitializeComponent();
        _customMetadataDefinitions = CustomMetadataColumnFactory.CollectDefinitions(
            customMetadataDefinitions,
            rules.SelectMany(rule => rule.Metadata.CustomMetadata));
        RuleRows = new ObservableCollection<MetadataMappingRow>(
            rules.Select(rule => new MetadataMappingRow(rule, _customMetadataDefinitions)));
        CustomMetadataColumnFactory.AddEditableColumns(
            RulesGrid,
            _customMetadataDefinitions,
            insertIndex: 4,
            nameof(MetadataMappingRow.CustomValues));
        DataContext = this;
    }

    public ObservableCollection<MetadataMappingRow> RuleRows { get; }
    public IReadOnlyList<FolderMetadataRule> Rules => RuleRows.Select(row => row.ToRule()).ToArray();

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var editor = new MetadataMappingRuleWindow(null, _customMetadataDefinitions) { Owner = this };
        if (editor.ShowDialog() != true || editor.Rule is null) return;
        ReplaceSameFolder(editor.Rule);
        RulesGrid.SelectedItem = RuleRows.LastOrDefault();
    }

    private void EditRule_Click(object sender, RoutedEventArgs e) => EditSelectedRule();

    private void EditSelectedRule()
    {
        if (RulesGrid.SelectedItem is not MetadataMappingRow selected)
        {
            MessageBox.Show(this, "请先选择一条映射规则。", "EasyPub Modern");
            return;
        }

        var editor = new MetadataMappingRuleWindow(selected.ToRule(), _customMetadataDefinitions) { Owner = this };
        if (editor.ShowDialog() != true || editor.Rule is null) return;
        RuleRows.Remove(selected);
        ReplaceSameFolder(editor.Rule);
        RulesGrid.SelectedItem = RuleRows.LastOrDefault();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is MetadataMappingRow selected) RuleRows.Remove(selected);
    }

    private void ReplaceSameFolder(FolderMetadataRule rule)
    {
        var duplicate = RuleRows.FirstOrDefault(candidate => string.Equals(
            candidate.FolderPath,
            rule.FolderPath,
            StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null) RuleRows.Remove(duplicate);
        RuleRows.Add(new MetadataMappingRow(rule, _customMetadataDefinitions));
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        RulesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class MetadataMappingRow(
    FolderMetadataRule rule,
    IReadOnlyList<CalibreCustomMetadata> customMetadataDefinitions)
{
    public string FolderPath { get; } = rule.FolderPath;
    public BookMetadataOverrides Metadata { get; } = rule.Metadata;
    public CustomMetadataValueBag CustomValues { get; } = new(customMetadataDefinitions, rule.Metadata.CustomMetadata);

    public FolderMetadataRule ToRule() => new(FolderPath, Metadata with
    {
        CustomMetadata = CustomValues.ToMetadata(),
    });
}
