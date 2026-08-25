using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class CustomMetadataWindow : Window
{
    public CustomMetadataWindow(IReadOnlyList<CalibreCustomMetadata>? metadata)
    {
        InitializeComponent();
        Rows = new ObservableCollection<CustomMetadataEditRow>(
            (metadata ?? []).Select(item => new CustomMetadataEditRow
            {
                LookupName = item.LookupName,
                ColumnHeading = item.ColumnHeading ?? string.Empty,
                Type = item.Type,
                Value = item.Value,
            }));
        DataContext = this;
        TypeColumn.ItemsSource = new[]
        {
            new CustomMetadataTypeOption(CalibreCustomMetadataType.Text, "单值文本"),
            new CustomMetadataTypeOption(CalibreCustomMetadataType.TextList, "逗号分隔文本"),
        };
    }

    public ObservableCollection<CustomMetadataEditRow> Rows { get; }
    public IReadOnlyList<CalibreCustomMetadata> Metadata { get; private set; } = [];

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var row = new CustomMetadataEditRow { Type = CalibreCustomMetadataType.TextList };
        Rows.Add(row);
        MetadataGrid.SelectedItem = row;
        MetadataGrid.ScrollIntoView(row);
        MetadataGrid.BeginEdit();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MetadataGrid.SelectedItem is CustomMetadataEditRow row) Rows.Remove(row);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        MetadataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MetadataGrid.CommitEdit(DataGridEditingUnit.Row, true);
        try
        {
            Metadata = CalibreCustomMetadata.NormalizeAll(Rows.Select(row => new CalibreCustomMetadata
            {
                LookupName = row.LookupName,
                ColumnHeading = row.ColumnHeading,
                Type = row.Type,
                Value = row.Value,
            }));
            DialogResult = true;
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "自定义元数据", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

public sealed class CustomMetadataEditRow
{
    public string LookupName { get; set; } = string.Empty;
    public string ColumnHeading { get; set; } = string.Empty;
    public CalibreCustomMetadataType Type { get; set; }
    public string Value { get; set; } = string.Empty;
}

public sealed record CustomMetadataTypeOption(CalibreCustomMetadataType Value, string Label);
