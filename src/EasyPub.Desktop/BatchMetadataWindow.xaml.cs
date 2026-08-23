using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

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
            book.Author,
            book.CoverImagePath is null ? "未设置" : "有封面")));
        MetadataGrid.ItemsSource = Rows;
    }

    private void ApplyAuthorToAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows) row.Author = BatchAuthorText.Text.Trim();
        MetadataGrid.Items.Refresh();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in Rows)
        {
            row.Book.Title = EmptyToNull(row.Title);
            row.Book.Author = EmptyToNull(row.Author);
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed class MetadataEditRow(InputBookItem book, string fileName, string? title, string? author, string coverState)
{
    public InputBookItem Book { get; } = book;
    public string FileName { get; } = fileName;
    public string? Title { get; set; } = title;
    public string? Author { get; set; } = author;
    public string CoverState { get; } = coverState;
}
