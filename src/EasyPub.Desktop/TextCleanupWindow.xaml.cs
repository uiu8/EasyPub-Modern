using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class TextCleanupWindow : Window
{
    private readonly string _sourceText;
    private readonly TextCleanupOptions _initial;
    private TextCleanupPreview? _preview;
    private IReadOnlyList<TextCleanupChangeRow> _allRows = [];
    private readonly HashSet<string> _excludedKeys = new(StringComparer.Ordinal);
    private IReadOnlyList<TextCleanupCustomRule> _customRules = [];
    private bool _loaded;

    private TextCleanupWindow(string inputPath, string sourceText, TextCleanupOptions initial)
    {
        InitializeComponent();
        _sourceText = sourceText;
        _initial = initial;
        Result = initial;
        BookPathText.Text = inputPath;
        ApplyOptions(initial);
        ChangeRuleFilterCombo.Items.Add("全部规则");
        ChangeRuleFilterCombo.SelectedIndex = 0;
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
        RemoveInvisibleCharacters = InvisibleCheck.IsChecked == true,
        RemoveDuplicateChapterTitles = DuplicateChapterCheck.IsChecked == true,
        RepairParagraphBoundaries = ParagraphBoundaryCheck.IsChecked == true,
        RemoveRepeatedHeaders = RepeatedHeaderCheck.IsChecked == true,
        ApplyOcrCorrections = OcrCheck.IsChecked == true,
        CustomRules = _customRules,
        ChineseVariant = Enum.Parse<ChineseVariantConversion>(((ComboBoxItem)ChineseVariantCombo.SelectedItem).Tag!.ToString()!),
        ExcludedChangeKeys = _excludedKeys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
    };

    private void ApplyOptions(TextCleanupOptions options)
    {
        BlankLinesCheck.IsChecked = options.CollapseBlankLines;
        HardWrapCheck.IsChecked = options.RepairHardWraps;
        SpacesCheck.IsChecked = options.NormalizeFullWidthSpaces;
        ChapterCheck.IsChecked = options.NormalizeChapterNumbers;
        NoticeCheck.IsChecked = options.RemoveSiteNotices;
        PunctuationCheck.IsChecked = options.NormalizePunctuation;
        InvisibleCheck.IsChecked = options.RemoveInvisibleCharacters;
        DuplicateChapterCheck.IsChecked = options.RemoveDuplicateChapterTitles;
        ParagraphBoundaryCheck.IsChecked = options.RepairParagraphBoundaries;
        RepeatedHeaderCheck.IsChecked = options.RemoveRepeatedHeaders;
        OcrCheck.IsChecked = options.ApplyOcrCorrections;
        _customRules = options.CustomRules ?? [];
        _excludedKeys.Clear();
        foreach (var key in options.ExcludedChangeKeys ?? []) _excludedKeys.Add(key);
        ChineseVariantCombo.SelectedItem = ChineseVariantCombo.Items.OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag?.ToString(), options.ChineseVariant.ToString(), StringComparison.Ordinal));
    }

    private void RefreshPreview()
    {
        if (!_loaded) return;
        Result = CaptureOptions();
        try { _preview = TextCleanupPipeline.Apply(_sourceText, Result); }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "清理规则无法执行", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _allRows = _preview.Changes.Select(change => new TextCleanupChangeRow(change)).ToArray();
        var selectedRule = ChangeRuleFilterCombo.SelectedItem?.ToString() ?? "全部规则";
        var rules = _preview.Changes.Select(change => change.Rule).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.CurrentCulture).ToArray();
        ChangeRuleFilterCombo.Items.Clear();
        ChangeRuleFilterCombo.Items.Add("全部规则");
        foreach (var rule in rules) ChangeRuleFilterCombo.Items.Add(rule);
        ChangeRuleFilterCombo.SelectedItem = ChangeRuleFilterCombo.Items.Cast<object>().Any(item => Equals(item, selectedRule)) ? selectedRule : "全部规则";
        ApplyChangeFilter();
        ChangesGrid.SelectedItem = null;
        var applied = _preview.Changes.Count(change => change.IsApplied);
        ChangeSummaryText.Text = _preview.Changes.Count == 0
            ? "没有检测到需要修改的内容"
            : $"检测到 {_preview.Changes.Count} 处 · 采用 {applied} · 排除 {_preview.Changes.Count - applied}";
        CustomRuleSummaryText.Text = $"自定义规则：{_customRules.Count(rule => rule.Enabled)} 项启用";
        ShowPreview(TextCleanupPreviewNavigator.Create(_preview));
    }

    private void ChangesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _preview is null || ChangesGrid.SelectedItem is not TextCleanupChangeRow row) return;
        ShowPreview(TextCleanupPreviewNavigator.Create(_preview, row.Change));
    }

    private void ShowPreview(TextCleanupPreviewView view)
    {
        PreviewText.Text = view.Text;
        PreviewLocationText.Text = view.LocationText;
        if (view.SelectionLength <= 0) return;

        PreviewText.Focus();
        PreviewText.Select(
            Math.Clamp(view.SelectionStart, 0, PreviewText.Text.Length),
            Math.Clamp(view.SelectionLength, 0, Math.Max(0, PreviewText.Text.Length - view.SelectionStart)));
        var line = PreviewText.GetLineIndexFromCharacterIndex(PreviewText.SelectionStart);
        if (line >= 0) PreviewText.ScrollToLine(line);
    }

    private void Option_Click(object sender, RoutedEventArgs e) => RefreshPreview();
    private void ChineseVariantCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPreview();
    private void ChangeFilter_Changed(object sender, RoutedEventArgs e) { if (_loaded) ApplyChangeFilter(); }
    private void ToggleChange_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        if (!_excludedKeys.Add(key)) _excludedKeys.Remove(key);
        RefreshPreview();
    }

    private void ApplyChangeFilter()
    {
        var rule = ChangeRuleFilterCombo.SelectedItem?.ToString();
        var search = ChangeSearchText.Text.Trim();
        ChangesGrid.ItemsSource = _allRows.Where(row =>
            (string.IsNullOrWhiteSpace(rule) || rule == "全部规则" || string.Equals(row.Rule, rule, StringComparison.Ordinal)) &&
            (search.Length == 0 || row.Rule.Contains(search, StringComparison.CurrentCultureIgnoreCase) || row.Before.Contains(search, StringComparison.CurrentCultureIgnoreCase) || row.After.Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToArray();
    }
    private void Undo_Click(object sender, RoutedEventArgs e) { ApplyOptions(_initial); RefreshPreview(); }
    private void Clear_Click(object sender, RoutedEventArgs e) { ApplyOptions(new TextCleanupOptions()); RefreshPreview(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Apply_Click(object sender, RoutedEventArgs e) { Result = CaptureOptions(); DialogResult = true; }

    private void ManageCustomRules_Click(object sender, RoutedEventArgs e)
    {
        var manager = new TextCleanupRuleManagerWindow(_customRules, _sourceText) { Owner = this };
        if (manager.ShowDialog() != true) return;
        _customRules = manager.Rules;
        RefreshPreview();
    }
}

public sealed class TextCleanupChangeRow(TextCleanupChange change)
{
    public TextCleanupChange Change { get; } = change;
    public string Key => Change.Key;
    public int LineNumber => Change.LineNumber;
    public string Rule => Change.Rule;
    public string Before => Change.Before;
    public string After => Change.After;
    public string ToggleLabel => Change.IsApplied ? "排除" : "恢复";
    public string AccessibleToggleLabel => Change.IsApplied ? $"排除第 {LineNumber} 行的{Rule}" : $"恢复第 {LineNumber} 行的{Rule}";
}
