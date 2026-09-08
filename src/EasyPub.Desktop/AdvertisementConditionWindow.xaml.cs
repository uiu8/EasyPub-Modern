using System.Windows;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class AdvertisementConditionWindow : Window
{
    private readonly string _source;
    private readonly string[] _lines;
    private readonly TextCleanupOptions _initial;
    private TextCleanupOptions _working;
    private TextCleanupOptions? _candidate;
    private TextCleanupPreview? _baseline;
    private CancellationTokenSource? _analysis;
    private int _version;
    private bool _ready;
    public bool ManagedExisting { get; private set; }
    public TextCleanupOptions? Result { get; private set; }
    public string Keyword => KeywordText.Text.Trim();
    public bool Preserve => IntentCombo.SelectedIndex == 1;

    public AdvertisementConditionWindow(string source, TextCleanupOptions options, string keyword, bool preserve = false)
    {
        InitializeComponent();
        _source = source;
        _lines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        _initial = options;
        _working = options;
        KeywordText.Text = keyword;
        IntentCombo.SelectedIndex = preserve ? 1 : 0;
        Loaded += (_, _) => { _ready = true; AnalyzeDraft(); };
        Closed += (_, _) => { _ready = false; _version++; _analysis?.Cancel(); _analysis?.Dispose(); };
    }

    private void DraftChanged(object sender, RoutedEventArgs e) { if (_ready) { ManagedExisting = false; AnalyzeDraft(); } }

    private async void AnalyzeDraft()
    {
        var version = ++_version;
        _analysis?.Cancel();
        _analysis?.Dispose();
        _analysis = new CancellationTokenSource();
        var token = _analysis.Token;
        _candidate = null;
        SaveConditionButton.IsEnabled = false;
        ImpactExamples.Text = "";
        ImpactSummary.Text = "正在分析，尚未保存…";
        try
        {
            var keyword = Keyword;
            var preserve = Preserve;
            if (!ManagedExisting && _working.BuiltinOverrides.TryGetValue(nameof(TextCleanupOptions.RemoveSiteNotices), out var custom) && !custom.UseDefaultAlgorithm)
                throw new ArgumentException("广告默认算法已被替换。请先在“管理已有条件”中启用默认算法，或编辑原有自定义替换。");
            var candidate = ManagedExisting ? _working : _working with { Advertisement = _working.Advertisement.AddKeyword(keyword, preserve), RemoveSiteNotices = true };
            SaveConditionButton.Content = ManagedExisting ? "保存规则修改" : "保存条件";
            await Task.Delay(250, token);
            var baseline = _baseline;
            var comparison = await Task.Run(() =>
            {
                var before = baseline ?? TextCleanupPipeline.Apply(_source, _initial, token);
                var after = TextCleanupPipeline.Apply(_source, candidate, token);
                return (before, after);
            }, token);
            if (!_ready || version != _version || token.IsCancellationRequested) return;
            _baseline = comparison.before;
            var beforeLines = AdLines(comparison.before);
            var afterLines = AdLines(comparison.after);
            var removed = afterLines.Except(beforeLines).Order().ToArray();
            var retained = beforeLines.Except(afterLines).Order().ToArray();
            var affected = removed.Concat(retained).Order().ToArray();
            ImpactSummary.Text = (ManagedExisting ? "已调整已有规则；不会重新添加上方文字。\n" : "") + $"新增广告删除 {removed.Length} 处 · 不再被广告规则删除 {retained.Length} 处"
                + (keyword.Length <= 2 || affected.Length >= 100 ? "\n匹配范围可能较广，请先核对示例。" : "")
                + (affected.Length == 0 ? "\n本书没有新增变化。条件可保存，但保留条件及逐项排除仍优先。" : "\n以下显示最多 5 处原文示例：")
                + (!ManagedExisting && !preserve && AdvertisementPreservesKeyword(keyword) ? "\n已有保留条件优先：如需取消保留，请点击“管理已有条件”。" : "")
                + (!ManagedExisting && !_initial.RemoveSiteNotices ? "\n保存时将同时开启广告清理。" : "");
            ImpactExamples.Text = string.Join("\n\n", affected.Take(5).Select(line =>
            {
                var text = _lines[Math.Clamp(line - 1, 0, _lines.Length - 1)];
                return $"第 {line} 行 · {(removed.Contains(line) ? "新增删除" : "取消广告删除")}\n{(text.Length > 400 ? text[..400] + "…" : text)}";
            }));
            _candidate = candidate;
            SaveConditionButton.IsEnabled = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (version == _version && _ready) ImpactSummary.Text = exception.Message;
        }
    }

    private static HashSet<int> AdLines(TextCleanupPreview preview) => preview.Changes
        .Where(change => change.IsApplied && change.Rule == "清理网站广告/下载说明")
        .Select(change => change.LineNumber).ToHashSet();

    private bool AdvertisementPreservesKeyword(string keyword) => _working.Advertisement.PreserveKeywords
        .Any(value => !string.IsNullOrWhiteSpace(value) && keyword.Contains(value, StringComparison.OrdinalIgnoreCase));

    private void SaveCondition_Click(object sender, RoutedEventArgs e)
    {
        if (_candidate is null || !SaveConditionButton.IsEnabled) return;
        Result = _candidate;
        DialogResult = true;
    }

    private void ManageConditions_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BuiltinCleanupWindow(_working, _source, nameof(TextCleanupOptions.RemoveSiteNotices), singleRuleOnly: true) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _working = editor.Result;
        ManagedExisting = true;
        AnalyzeDraft();
    }
}
