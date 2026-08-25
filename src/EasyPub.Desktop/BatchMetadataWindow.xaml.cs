using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class BatchMetadataWindow : Window
{
    private readonly IReadOnlyList<InputBookItem> _books;
    public ObservableCollection<MetadataEditRow> Rows { get; }

    public BatchMetadataWindow(
        IReadOnlyList<InputBookItem> books,
        IReadOnlyList<CalibreCustomMetadata>? customMetadataDefinitions = null)
    {
        InitializeComponent();
        _books = books;
        var definitions = CustomMetadataColumnFactory.CollectDefinitions(
            customMetadataDefinitions,
            books.SelectMany(book => book.MetadataOverrides.CustomMetadata));
        Rows = new ObservableCollection<MetadataEditRow>(books.Select(book => new MetadataEditRow(
            book,
            Path.GetFileName(book.InputPath),
            book.Title,
            book.Author ?? book.MetadataOverrides.Author,
            book.MetadataOverrides.Publisher,
            book.MetadataOverrides.Category,
            book.MetadataOverrides.Language,
            definitions,
            book.MetadataOverrides.CustomMetadata,
            book.MetadataRuleFolder is null ? "手动/默认" : $"映射：{Path.GetFileName(book.MetadataRuleFolder)}",
            book.CoverImagePath is null ? "未设置" : "有封面")));
        CustomMetadataColumnFactory.AddEditableColumns(
            MetadataGrid,
            definitions,
            insertIndex: 6,
            nameof(MetadataEditRow.CustomValues));
        MetadataGrid.ItemsSource = Rows;
    }

    private void ApplyAuthorToAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) row.Author = BatchAuthorText.Text.Trim();
        MetadataGrid.Items.Refresh();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        MetadataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        MetadataGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var row in Rows)
        {
            row.Book.Title = EmptyToNull(row.Title);
            row.Book.Author = EmptyToNull(row.Author);
            row.Book.SetMetadataOverrides(row.Book.MetadataOverrides with
            {
                Author = null,
                Publisher = EmptyToNull(row.Publisher),
                Category = EmptyToNull(row.Category),
                Language = EmptyToNull(row.Language),
                CustomMetadata = row.CustomValues.ToMetadata(),
            }, null);
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class MetadataEditRow(
    InputBookItem book,
    string fileName,
    string? title,
    string? author,
    string? publisher,
    string? category,
    string? language,
    IReadOnlyList<CalibreCustomMetadata> customMetadataDefinitions,
    IReadOnlyList<CalibreCustomMetadata> customMetadataValues,
    string metadataSource,
    string coverState)
{
    public InputBookItem Book { get; } = book;
    public string FileName { get; } = fileName;
    public string? Title { get; set; } = title;
    public string? Author { get; set; } = author;
    public string? Publisher { get; set; } = publisher;
    public string? Category { get; set; } = category;
    public string? Language { get; set; } = language;
    public CustomMetadataValueBag CustomValues { get; } = new(customMetadataDefinitions, customMetadataValues);
    public string MetadataSource { get; } = metadataSource;
    public string CoverState { get; } = coverState;
}
