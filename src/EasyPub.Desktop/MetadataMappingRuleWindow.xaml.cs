using System.IO;
using System.Windows;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class MetadataMappingRuleWindow : Window
{
    public MetadataMappingRuleWindow(FolderMetadataRule? existing)
    {
        InitializeComponent();
        if (existing is null) return;
        FolderPathText.Text = existing.FolderPath;
        AuthorText.Text = existing.Metadata.Author ?? string.Empty;
        TranslatorText.Text = existing.Metadata.Translator ?? string.Empty;
        PublisherText.Text = existing.Metadata.Publisher ?? string.Empty;
        CategoryCombo.Text = existing.Metadata.Category ?? string.Empty;
        IsbnText.Text = existing.Metadata.Isbn ?? string.Empty;
        PublicationDatePicker.SelectedDate = existing.Metadata.PublicationDate?.ToDateTime(TimeOnly.MinValue);
        LanguageCombo.Text = existing.Metadata.Language ?? string.Empty;
        DescriptionText.Text = existing.Metadata.Description ?? string.Empty;
    }

    public FolderMetadataRule? Rule { get; private set; }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var current = FolderPathText.Text.Trim();
        var dialog = new OpenFolderDialog
        {
            Title = "选择需要自动映射元数据的来源文件夹",
            InitialDirectory = Directory.Exists(current)
                ? current
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) == true) FolderPathText.Text = dialog.FolderName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var folder = FolderPathText.Text.Trim();
        if (!Directory.Exists(folder))
        {
            InkDialog.Show(this, "请选择一个当前存在的来源文件夹。", "EasyPub Modern");
            return;
        }

        var metadata = new BookMetadataOverrides
        {
            Author = EmptyToNull(AuthorText.Text),
            Translator = EmptyToNull(TranslatorText.Text),
            Publisher = EmptyToNull(PublisherText.Text),
            Category = EmptyToNull(CategoryCombo.Text),
            Isbn = EmptyToNull(IsbnText.Text),
            PublicationDate = PublicationDatePicker.SelectedDate is DateTime date
                ? DateOnly.FromDateTime(date)
                : null,
            Language = EmptyToNull(LanguageCombo.Text),
            Description = EmptyToNull(DescriptionText.Text),
        };
        if (metadata.IsEmpty)
        {
            InkDialog.Show(this, "请至少填写一个需要自动写入的元数据字段。", "EasyPub Modern");
            return;
        }

        Rule = new FolderMetadataRule(MetadataMappingResolver.NormalizeFolder(folder), metadata);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
