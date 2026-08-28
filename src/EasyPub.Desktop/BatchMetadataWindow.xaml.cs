using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace EasyPub.Desktop;

public partial class BatchMetadataWindow : Window
{
    private readonly IReadOnlyList<InputBookItem> _books;
    public ObservableCollection<MetadataEditRow> Rows { get; }

    public BatchMetadataWindow(IReadOnlyList<InputBookItem> books)
    {
        InitializeComponent();
        _books = books;
        Rows = new ObservableCollection<MetadataEditRow>(books.Select(book => new MetadataEditRow(
            book,
            Path.GetFileName(book.InputPath),
            book.Title,
            book.Author ?? book.MetadataOverrides.Author,
            book.MetadataOverrides.Publisher,
            book.MetadataOverrides.Category,
            book.MetadataOverrides.Language,
            book.MetadataRuleFolder is null ? "手动/默认" : $"映射：{Path.GetFileName(book.MetadataRuleFolder)}",
            book.CoverImagePath is null ? "未设置" : "有封面")));
        MetadataGrid.ItemsSource = Rows;
    }

    private void ApplyFieldToAll_Click(object sender, RoutedEventArgs e)
    {
        var field = (BatchFieldCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Author";
        var value = BatchValueText.Text.Trim();
        foreach (var row in Rows)
        {
            switch (field)
            {
                case "Title": row.Title = value; break;
                case "Publisher": row.Publisher = value; break;
                case "Category": row.Category = value; break;
                case "Language": row.Language = value; break;
                default: row.Author = value; break;
            }
        }
        MetadataGrid.Items.Refresh();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
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
    public string MetadataSource { get; } = metadataSource;
    public string CoverState { get; } = coverState;
}
