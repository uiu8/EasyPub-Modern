using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class TextCleanupWindow : Window
{
    private readonly string _sourceText;
    private readonly TextCleanupOptions _initial;
    private bool _loaded;

    private TextCleanupWindow(string inputPath, string sourceText, TextCleanupOptions initial)
    {
        InitializeComponent();
        _sourceText = sourceText;
        _initial = initial;
        Result = initial;
        BookPathText.Text = inputPath;
        ApplyOptions(initial);
        _loaded = true;
        RefreshPreview();
    }

    public TextCleanupOptions Result { get; private set; }

    public static async Task<TextCleanupWindow> CreateAsync(
        string inputPath,
        TextEncodingMode encoding,
        TextCleanupOptions initial,
        CancellationToken cancellationToken = default)
    {
        var text = await TextCleanupPipeline.ReadFileAsync(inputPath, encoding, cancellationToken);
        return new TextCleanupWindow(inputPath, text, initial);
    }

    private TextCleanupOptions CaptureOptions() => new()
    {
        CollapseBlankLines = BlankLinesCheck.IsChecked == true,
        RepairHardWraps = HardWrapCheck.IsChecked == true,
        NormalizeFullWidthSpaces = SpacesCheck.IsChecked == true,
        NormalizeChapterNumbers = ChapterCheck.IsChecked == true,
        RemoveSiteNotices = NoticeCheck.IsChecked == true,
        NormalizePunctuation = PunctuationCheck.IsChecked == true,
        ChineseVariant = Enum.Parse<ChineseVariantConversion>(((ComboBoxItem)ChineseVariantCombo.SelectedItem).Tag!.ToString()!),
    };

    private void ApplyOptions(TextCleanupOptions options)
    {
        BlankLinesCheck.IsChecked = options.CollapseBlankLines;
        HardWrapCheck.IsChecked = options.RepairHardWraps;
        SpacesCheck.IsChecked = options.NormalizeFullWidthSpaces;
        ChapterCheck.IsChecked = options.NormalizeChapterNumbers;
        NoticeCheck.IsChecked = options.RemoveSiteNotices;
        PunctuationCheck.IsChecked = options.NormalizePunctuation;
        ChineseVariantCombo.SelectedItem = ChineseVariantCombo.Items.OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag?.ToString(), options.ChineseVariant.ToString(), StringComparison.Ordinal));
    }

    private void RefreshPreview()
    {
        if (!_loaded) return;
        Result = CaptureOptions();
        var preview = TextCleanupPipeline.Apply(_sourceText, Result);
        ChangesGrid.ItemsSource = preview.Changes.Take(500).ToArray();
        ChangeSummaryText.Text = preview.Changes.Count == 0
            ? "没有检测到需要修改的内容"
            : $"检测到 {preview.Changes.Count} 处变化" + (preview.Changes.Count > 500 ? "（列表显示前 500 处）" : string.Empty);
        PreviewText.Text = preview.Text.Length > 120_000 ? preview.Text[..120_000] + "\r\n\r\n……预览已截断……" : preview.Text;
    }

    private void Option_Click(object sender, RoutedEventArgs e) => RefreshPreview();
    private void ChineseVariantCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();
    private void Undo_Click(object sender, RoutedEventArgs e) { ApplyOptions(_initial); RefreshPreview(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { ApplyOptions(new TextCleanupOptions()); RefreshPreview(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Apply_Click(object sender, RoutedEventArgs e) { Result = CaptureOptions(); DialogResult = true; }
}
