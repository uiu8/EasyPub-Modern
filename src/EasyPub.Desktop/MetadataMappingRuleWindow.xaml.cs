using System.IO;
using System.Windows;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class MetadataMappingRuleWindow : Window
{
    private IReadOnlyList<CalibreCustomMetadata> _customMetadata = [];

    public MetadataMappingRuleWindow(
        FolderMetadataRule? existing,
        IReadOnlyList<CalibreCustomMetadata>? customMetadataDefinitions = null)
    {
        InitializeComponent();
        _customMetadata = CalibreCustomMetadata.PrepareAssignments(
            customMetadataDefinitions,
            existing?.Metadata.CustomMetadata);
        UpdateCustomMetadataSummary();
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
            MessageBox.Show(this, "请选择一个当前存在的来源文件夹。", "EasyPub Modern");
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
            CustomMetadata = _customMetadata.Where(item => item.HasValue).ToArray(),
        };
        if (metadata.IsEmpty)
        {
            MessageBox.Show(this, "请至少填写一个需要自动写入的元数据字段。", "EasyPub Modern");
            return;
        }

        Rule = new FolderMetadataRule(MetadataMappingResolver.NormalizeFolder(folder), metadata);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void EditCustomMetadata_Click(object sender, RoutedEventArgs e)
    {
        var editor = new CustomMetadataWindow(_customMetadata) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _customMetadata = editor.Metadata;
        UpdateCustomMetadataSummary();
    }

    private void UpdateCustomMetadataSummary()
    {
        if (CustomMetadataSummaryText is null) return;
        var assigned = _customMetadata.Where(item => item.HasValue).ToArray();
        CustomMetadataSummaryText.Text = assigned.Length == 0
            ? _customMetadata.Count == 0
                ? "尚未定义自定义字段"
                : $"可填写 {_customMetadata.Count} 个已定义字段；当前规则均留空"
            : $"当前规则已填写 {assigned.Length} 项：{string.Join("、", assigned.Select(item => item.DisplayHeading))}";
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
