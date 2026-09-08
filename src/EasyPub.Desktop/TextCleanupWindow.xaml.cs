using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class TextCleanupWindow : Window
{
    private readonly string _sourceText;
    private readonly string[] _sourceLines;
    private readonly TextCleanupOptions _initial;
    private TextCleanupPreview? _preview;
    private IReadOnlyList<TextCleanupChangeRow> _allRows = [];
    private sealed record CleanupGroupIdentity(string Rule, string Before, string After, string? CustomId, string RuleKeys);
    private CleanupGroupIdentity? _expandedGroup;
    private static CleanupGroupIdentity GroupIdentity(TextCleanupChangeRow row) => new(row.Rule, row.Before, row.After, row.Change.CustomRuleId,
        System.Text.Json.JsonSerializer.Serialize(row.Change.RuleKeys));
    private void Grouping_Changed(object sender, RoutedEventArgs e) { _expandedGroup = null; ApplyChangeFilter(); }
    private void ExpandGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ChangesGrid.SelectedItem is not TextCleanupChangeRow { Members.Count: > 1 } row) return;
        _expandedGroup = GroupIdentity(row); ApplyChangeFilter();
    }
    private void BackToGroups_Click(object sender, RoutedEventArgs e) { _expandedGroup = null; ApplyChangeFilter(); }
    private readonly HashSet<string> _excludedKeys = new(StringComparer.Ordinal);
    private IReadOnlyList<TextCleanupCustomRule> _customRules = [];
    private bool _loaded;
    private TextCleanupOptions _behavior = new();
    private TextCleanupOptions? _sharedOptions;
    public bool ApplyToShared => SharedScopeCheck.IsChecked == true;
    public bool InheritShared { get; private set; }

    public void ConfigureScope(TextCleanupOptions shared, bool hasOverride)
    {
        _sharedOptions = shared;
        ScopeHintText.Text = hasOverride ? "当前书稿使用独立规则。" : "当前书稿继承项目通用规则；应用后可保存为本书独立规则。";
        ScopeExpander.ToolTip = ScopeHintText.Text;
        ScopeExpander.Header = hasOverride ? "本书：独立规则" : "本书：继承通用规则";
    }
    private CancellationTokenSource? _previewCancellation;
    private int _previewVersion;
    private bool _previewReady;
    private sealed record RuleTarget(string Key, string Label, string? CustomId = null);
    private RuleTarget? _recentRule;
    private AdvertisementRuleOptions? _undoAdvertisement;
    private AdvertisementRuleOptions? _lastAddedAdvertisement;
    private bool _undoNoticeEnabled;
    private bool _lastNoticeEnabled;
    private IReadOnlyDictionary<string, BuiltinCleanupOverride>? _undoBuiltinOverrides;
    private IReadOnlyDictionary<string, BuiltinCleanupOverride>? _lastBuiltinOverrides;
    private void UndoCondition_Click(object sender, RoutedEventArgs e)
    {
        if (_undoAdvertisement is null || !_previewReady) return;
        ApplyOptions(CaptureOptions() with { Advertisement = _undoAdvertisement, RemoveSiteNotices = _undoNoticeEnabled,
            BuiltinOverrides = _undoBuiltinOverrides ?? CaptureOptions().BuiltinOverrides });
        _undoAdvertisement = null;
        UndoConditionButton.Visibility = Visibility.Collapsed;
        RecentActionText.Text = "已撤销条件修改";
        RefreshPreview();
    }

    private RuleTarget[] GetRuleTargets(TextCleanupChange? change)
    {
        if (change is null) return [];
        if (change.CustomRuleId is string id)
        {
            foreach (var pair in _behavior.BuiltinOverrides)
                foreach (var rule in pair.Value.Rules)
                    if (id == pair.Key + ":" + rule.Id)
                        return [new(pair.Key, rule.Name, rule.Id)];
            var custom = _customRules.FirstOrDefault(rule => rule.Id == id);
            return custom is null ? [] : [new("custom", custom.Name, id)];
        }
        if (change.RuleKeys.Count > 0)
            return change.RuleKeys.Select(key => new RuleTarget(key, AutomaticCheckOptions.CleanupLabels.GetValueOrDefault(key, "简繁转换"))).ToArray();
        return AutomaticCheckOptions.CleanupLabels.Where(pair => pair.Value == change.Rule
            || pair.Key == nameof(TextCleanupOptions.RemoveSiteNotices) && change.Rule == "清理网站广告/下载说明"
            || pair.Key == nameof(TextCleanupOptions.RemoveDuplicateChapterTitles) && change.Rule == "删除连续重复章节标题")
            .Select(pair => new RuleTarget(pair.Key, pair.Value)).ToArray();
    }

    private void UpdateRuleActions(TextCleanupChange? change)
    {
        var targets = GetRuleTargets(change);
        AddAdvertisementRuleButton.Visibility = targets.Any(target => target.Key == nameof(TextCleanupOptions.RemoveSiteNotices) && target.CustomId is null) ? Visibility.Visible : Visibility.Collapsed;
        CurrentRuleActions.Children.Clear();
        foreach (var target in targets.Where(target => target.Key != nameof(TextCleanupOptions.RemoveSiteNotices) || target.CustomId is not null))
        {
            var button = new Button { Content = "编辑当前规则：" + target.Label, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 6, 4) };
            button.Click += (_, _) => EditRule(target);
            CurrentRuleActions.Children.Add(button);
        }
    }

    private void RememberRule(RuleTarget target)
    {
        _recentRule = target;
        RecentRuleButton.ToolTip = "返回编辑：" + target.Label;
        RecentActionPanel.Visibility = Visibility.Visible;
    }

    private void RecentRule_Click(object sender, RoutedEventArgs e) { if (_recentRule?.Key == nameof(TextCleanupOptions.RemoveSiteNotices) && _recentRule.CustomId is null) OpenConditionEditor(_lastConditionKeyword, _lastConditionPreserve); else if (_recentRule is not null) EditRule(_recentRule); }

    private void EditRule(RuleTarget target)
    {
        if (!_previewReady) return;
        if (target.Key == nameof(TextCleanupOptions.ChineseVariant)) { RulesExpander.IsExpanded = true; ChineseVariantCombo.Focus(); ChineseVariantCombo.IsDropDownOpen = true; return; }
        if (target.CustomId is not null)
        {
            var current = CaptureOptions();
            var rules = target.Key == "custom" ? _customRules : current.BuiltinOverrides.GetValueOrDefault(target.Key, new()).Rules;
            var editor = new TextCleanupRuleManagerWindow(rules, _sourceText, target.CustomId) { Owner = this };
            if (editor.ShowDialog() != true) return;
            if (target.Key == "custom") _customRules = editor.Rules;
            else
            {
                var values = current.BuiltinOverrides.ToDictionary(pair => pair.Key, pair => pair.Value);
                values[target.Key] = values.GetValueOrDefault(target.Key, new()) with { Rules = editor.Rules };
                ApplyOptions(current with { BuiltinOverrides = values });
            }
        }
        else
        {
            var editor = new BuiltinCleanupWindow(CaptureOptions(), _sourceText, target.Key) { Owner = this };
            if (editor.ShowDialog() != true) return;
            ApplyOptions(editor.Result);
        }
        _undoAdvertisement = null;
        RememberRule(target);
        UndoConditionButton.Visibility = Visibility.Collapsed;
        RecentActionText.Text = "规则已更新";
        RefreshPreview();
    }

    private TextCleanupWindow(string inputPath, string sourceText, TextCleanupOptions initial)
    {
        InitializeComponent();
        _sourceText = sourceText;
        _sourceLines = sourceText.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        _initial = initial;
        Result = initial;
        BookPathText.Text = inputPath;
        BookPathText.ToolTip = inputPath;
        SharedScopeCheck.Checked += (_, _) => ScopeExpander.Header = "作用范围：项目通用 / 下次默认";
        SharedScopeCheck.Unchecked += (_, _) => ScopeExpander.Header = "作用范围：本书";
        ApplyOptions(initial);
        ChangeRuleFilterCombo.Items.Add("全部规则");
        ChangeRuleFilterCombo.SelectedIndex = 0;
        _loaded = true;
        ChangeSummaryText.Text = "正在分析文本…";
        Loaded += TextCleanupWindow_Loaded;
        Closed += (_, _) => _previewCancellation?.Cancel();
    }

    public TextCleanupOptions Result { get; private set; }

    private void TextCleanupWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= TextCleanupWindow_Loaded;
        RefreshPreview();
    }

    public static async Task<TextCleanupWindow> CreateAsync(
        string inputPath,
        TextEncodingMode encoding,
        TextCleanupOptions initial,
        CancellationToken cancellationToken = default)
    {
        var text = await TextCleanupPipeline.ReadFileAsync(inputPath, encoding, cancellationToken);
        return new TextCleanupWindow(inputPath, text, initial);
    }

    private TextCleanupOptions CaptureOptions() => _behavior with
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
        var wasLoaded = _loaded;
        _loaded = false;
        _behavior = options;
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
        _loaded = wasLoaded;
    }

    private async void RefreshPreview(bool runInBackground = true)
    {
        if (!_loaded) return;
        Result = CaptureOptions();
        var options = Result;
        if (_lastAddedAdvertisement is not null && (options.Advertisement != _lastAddedAdvertisement
            || options.RemoveSiteNotices != _lastNoticeEnabled || options.BuiltinOverrides != _lastBuiltinOverrides))
        {
            _undoAdvertisement = null;
            UndoConditionButton.Visibility = Visibility.Collapsed;
        }
        var selectedKey = (ChangesGrid.SelectedItem as TextCleanupChangeRow)?.Key;
        _previewReady = false;
        ApplyRulesButton.IsEnabled = false;
        AddAdvertisementRuleButton.IsEnabled = false;
        var version = ++_previewVersion;
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        var cancellationToken = _previewCancellation.Token;
        if (runInBackground) ChangeSummaryText.Text = "正在重新分析文本…";

        TextCleanupPreview preview;
        try
        {
            preview = runInBackground
                ? await Task.Run(() => TextCleanupPipeline.Apply(_sourceText, options, cancellationToken), cancellationToken)
                : TextCleanupPipeline.Apply(_sourceText, options, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            if (version != _previewVersion) return;
            ChangeSummaryText.Text = "预览失败；请修正规则后再应用";
            InkDialog.Show(this, exception.Message, "清理规则无法执行", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (version != _previewVersion || cancellationToken.IsCancellationRequested) return;
        _preview = preview;
        _allRows = _preview.Changes.Select(change => new TextCleanupChangeRow(change)).ToArray();
        var selectedRule = ChangeRuleFilterCombo.SelectedItem?.ToString() ?? "全部规则";
        var rules = _preview.Changes.Select(change => change.Rule).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.CurrentCulture).ToArray();
        ChangeRuleFilterCombo.Items.Clear();
        ChangeRuleFilterCombo.Items.Add("全部规则");
        foreach (var rule in rules) ChangeRuleFilterCombo.Items.Add(rule);
        ChangeRuleFilterCombo.SelectedItem = ChangeRuleFilterCombo.Items.Cast<object>().Any(item => Equals(item, selectedRule)) ? selectedRule : "全部规则";
        ApplyChangeFilter();
        _previewReady = true;
        ApplyRulesButton.IsEnabled = true;
        AddAdvertisementRuleButton.IsEnabled = true;
        ChangesGrid.SelectedItem = ChangesGrid.Items.Cast<TextCleanupChangeRow>().FirstOrDefault(row => row.Key == selectedKey)
            ?? ChangesGrid.Items.Cast<TextCleanupChangeRow>().FirstOrDefault();
        var applied = _preview.Changes.Count(change => change.IsApplied);
        ChangeSummaryText.Text = _preview.Changes.Count == 0
            ? "没有检测到需要修改的内容"
            : $"检测到 {_preview.Changes.Count} 处 · 采用 {applied} · 排除 {_preview.Changes.Count - applied}";
        CustomRuleSummaryText.Text = $"自定义规则：{_customRules.Count(rule => rule.Enabled)} 项启用";
        var active = AutomaticCheckOptions.CleanupLabels.Where(pair => (bool)typeof(TextCleanupOptions).GetProperty(pair.Key)!.GetValue(options)!).Select(pair => pair.Value).ToList();
        if (options.ChineseVariant != ChineseVariantConversion.None) active.Add("简繁转换");
        active.AddRange(_customRules.Where(rule => rule.Enabled).Select(rule => "自定义：" + rule.Name));
        ActiveRulesText.Text = active.Count == 0 ? "未开启清理规则" : "已开启：" + string.Join("、", active);
        UpdatePreview();
    }

    private void ChangesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _preview is null) return;
        if (ChangesGrid.SelectedItem is not TextCleanupChangeRow row) { UpdatePreview(); return; }
        UpdatePreview();
    }

    private void PreviewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loaded) UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_preview is null) return;
        var row = ChangesGrid.SelectedItem as TextCleanupChangeRow;
        var change = row?.Change;
        CurrentChangeSummary.Text = row is null ? "请选择检测记录" : $"{row.Status} · 第 {row.LineNumber} 行 · {row.Rule}" + (row.Members.Count > 1 ? "（正文显示首处；可展开逐处核对）" : "");
        ExpandAdGroupButton.Visibility = row?.Members.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        BackToGroupsButton.Visibility = _expandedGroup is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateRuleActions(change);
        var index = ChangesGrid.SelectedIndex;
        PreviousChangeButton.IsEnabled = index > 0;
        NextChangeButton.IsEnabled = index >= 0 && index < ChangesGrid.Items.Count - 1;
        ChangePositionText.Text = $"{(index < 0 ? 0 : index + 1)} / {ChangesGrid.Items.Count}";
        ChangeDetailReason.Text = row is null ? "请选择检测记录" : $"{row.Status} · {row.Rule}\n命中依据：{row.Reason}";
        ChangeDetailBefore.Text = row?.Before ?? "—";
        ChangeDetailAfter.Text = row is null ? "—" : string.IsNullOrEmpty(row.After) ? "（删除此内容）" : row.After;
        ShowPreview(OriginalPreviewRadio.IsChecked == true
            ? TextCleanupPreviewNavigator.CreateOriginal(_sourceLines, change)
            : TextCleanupPreviewNavigator.Create(_preview, change));
    }

    private void PreviousChange_Click(object sender, RoutedEventArgs e) => MoveChange(-1);
    private void NextChange_Click(object sender, RoutedEventArgs e) => MoveChange(1);
    private void MoveChange(int offset)
    {
        var target = ChangesGrid.SelectedIndex + offset;
        if (target < 0 || target >= ChangesGrid.Items.Count) return;
        ChangesGrid.SelectedIndex = target;
        ChangesGrid.ScrollIntoView(ChangesGrid.SelectedItem);
    }

    private string _lastConditionKeyword = "";
    private bool _lastConditionPreserve;
    private void AddAdvertisementRule_Click(object sender, RoutedEventArgs e)
    {
        if (!_previewReady || ChangesGrid.SelectedItem is not TextCleanupChangeRow row) return;
        OpenConditionEditor(row.Before, false);
    }

    private void OpenConditionEditor(string keyword, bool preserve)
    {
        if (!_previewReady) return;
        var current = CaptureOptions();
        var editor = new AdvertisementConditionWindow(_sourceText, current, keyword, preserve) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        _lastConditionKeyword = editor.Keyword;
        _lastConditionPreserve = editor.Preserve;
        _undoAdvertisement = current.Advertisement;
        _lastAddedAdvertisement = editor.Result.Advertisement;
        _undoNoticeEnabled = current.RemoveSiteNotices;
        _undoBuiltinOverrides = current.BuiltinOverrides;
        _lastBuiltinOverrides = editor.Result.BuiltinOverrides;
        _lastNoticeEnabled = editor.Result.RemoveSiteNotices;
        UndoConditionButton.Visibility = Visibility.Visible;
        RememberRule(new(nameof(TextCleanupOptions.RemoveSiteNotices), "网站广告/下载说明"));
        RecentActionText.Text = editor.ManagedExisting ? "已修改已有广告规则" : editor.Preserve ? "已保存为保留条件" : "已保存为广告条件";
        RecentActionText.ToolTip = editor.Keyword;
        ApplyOptions(editor.Result);
        RefreshPreview();
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
        var row = ChangesGrid.Items.Cast<TextCleanupChangeRow>().FirstOrDefault(item => item.Key == key);
        if (row is null) return;
        var keep = row.Members.Any(change => change.IsApplied);
        foreach (var change in row.Members)
            if (keep) _excludedKeys.Add(change.Key); else _excludedKeys.Remove(change.Key);
        RefreshPreview();
    }

    private void ApplyChangeFilter()
    {
        var selectedKey = (ChangesGrid.SelectedItem as TextCleanupChangeRow)?.Key;
        var rule = ChangeRuleFilterCombo.SelectedItem?.ToString();
        var search = ChangeSearchText.Text.Trim();
        var filtered = _allRows.Where(row =>
            (string.IsNullOrWhiteSpace(rule) || rule == "全部规则" || string.Equals(row.Rule, rule, StringComparison.Ordinal)) &&
            (search.Length == 0 || row.Rule.Contains(search, StringComparison.CurrentCultureIgnoreCase) || row.Before.Contains(search, StringComparison.CurrentCultureIgnoreCase) || row.After.Contains(search, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        var originalCount = filtered.Length;
        GroupingSummaryText.Text = "";
        if (_expandedGroup is not null)
        {
            filtered = filtered.Where(row => GroupIdentity(row) == _expandedGroup).ToArray();
            GroupingSummaryText.Text = $"本组 {filtered.Length} 处 · 正在逐处核对";
        }
        else if (GroupAdsCheck.IsChecked == true)
        {
            var groups = filtered.GroupBy(GroupIdentity).ToDictionary(group => group.Key, group => group.ToArray());
            filtered = groups.Values.Select(group => new TextCleanupChangeRow(group[0].Change) { Members = group.Select(item => item.Change).ToArray() }).ToArray();
            var repeated = groups.Count(group => group.Value.Length > 1);
            GroupingSummaryText.Text = repeated > 0 ? $"{originalCount} 条 → {filtered.Length} 项 · {repeated} 组完全重复修改（适用于所有规则）"
                : "未发现完全重复的修改；规则、修改前后内容均相同才会折叠。";
        }
        ChangesGrid.ItemsSource = filtered;
        ChangesGrid.SelectedItem = ChangesGrid.Items.Cast<TextCleanupChangeRow>().FirstOrDefault(row => row.Key == selectedKey)
            ?? ChangesGrid.Items.Cast<TextCleanupChangeRow>().FirstOrDefault();
        UpdatePreview();
    }
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        _undoAdvertisement = null;
        _recentRule = null;
        RecentActionPanel.Visibility = Visibility.Collapsed;

        ApplyOptions(_initial); RefreshPreview();
    }
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var current = CaptureOptions();
        ApplyOptions(new TextCleanupOptions { Advertisement = current.Advertisement, BuiltinOverrides = current.BuiltinOverrides, HardWrapMinimumLength = current.HardWrapMinimumLength, ExcludedChangeKeys = current.ExcludedChangeKeys, CustomRules = current.CustomRules.Select(rule => rule with { Enabled = false }).ToArray() });
        RefreshPreview();
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Apply_Click(object sender, RoutedEventArgs e) { if (!_previewReady) return; Result = CaptureOptions(); DialogResult = true; }

    private void ManageCustomRules_Click(object sender, RoutedEventArgs e)
    {
        var manager = new TextCleanupRuleManagerWindow(_customRules, _sourceText) { Owner = this };
        if (manager.ShowDialog() != true) return;
        _customRules = manager.Rules;
        RefreshPreview();
    }

    private void ManageBuiltins_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BuiltinCleanupWindow(CaptureOptions(), _sourceText) { Owner = this };
        if (editor.ShowDialog() != true) return;
        ApplyOptions(editor.Result); RefreshPreview();
    }

    private void InheritShared_Click(object sender, RoutedEventArgs e)
    {
        if (_sharedOptions is null) return;
        if (InkDialog.Show(this, "移除本书独立规则，重新继承项目通用规则？原始书稿不会修改。", "恢复继承", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        InheritShared = true; Result = _sharedOptions; DialogResult = true;
    }
}

public sealed class TextCleanupChangeRow(TextCleanupChange change)
{
    public TextCleanupChange Change { get; } = change;
    public IReadOnlyList<TextCleanupChange> Members { get; init; } = [change];
    public string Key => Change.Key;
    public int LineNumber => Change.LineNumber;
    public string Rule => Change.Rule;
    public string Reason => Change.Reason ?? Change.Rule;
    public string Before => Change.Before;
    public string After => Change.After;
    public string Summary => Before.Replace('\r', ' ').Replace('\n', ' ');
    public string Status => Members.Count > 1 ? $"共 {Members.Count} 处 · {(string.IsNullOrEmpty(After) ? "将删除" : "将替换")} {Members.Count(item => item.IsApplied)} · 已保留 {Members.Count(item => !item.IsApplied)}" : !Change.IsApplied ? "已保留" : string.IsNullOrEmpty(After) ? "将删除" : "将替换";
    public string ToggleLabel => Members.Count > 1 ? Members.Any(item => item.IsApplied) ? "整组保留" : "整组采用" : Change.IsApplied ? "保留本处" : "采用修改";
    public string AccessibleToggleLabel => $"{ToggleLabel}：第 {LineNumber} 行的{Rule}";
}
