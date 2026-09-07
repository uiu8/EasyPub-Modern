using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public sealed class BuiltinCleanupWindow : Window
{
    private TextCleanupOptions _options;
    private readonly string _source;
    private readonly ComboBox _rules = new() { DisplayMemberPath = "Value", SelectedValuePath = "Key" };
    private readonly CheckBox _original = new() { Content = "执行默认算法（取消后只运行本项自定义替换）" };
    private readonly TextBox _pattern = new() { AcceptsReturn = false };
    private readonly TextBox _preserve = new();
    private readonly CheckBox _regex = new() { Content = "广告匹配使用正则表达式" };
    private readonly CheckBox _preserveRegex = new() { Content = "保留条件使用正则表达式" };
    private readonly CheckBox _heuristics = new() { Content = "同时使用网址与推广语境组合识别" };
    private readonly TextBox _minimum = new();
    private readonly StackPanel _adPanel = new();
    private readonly StackPanel _wrapPanel = new();
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap };
    private string? _key;
    public TextCleanupOptions Result => _options;

    public BuiltinCleanupWindow(TextCleanupOptions options, string source)
    {
        _options = options;
        _source = source;
        Title = "内置清理规则 · 自定义与恢复";
        Width = 760; Height = 720; MinWidth = 600; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        foreach (var check in new[] { _original, _regex, _preserveRegex, _heuristics })
        {
            check.SetResourceReference(Control.ForegroundProperty, "PrimaryTextBrush");
            check.Margin = new Thickness(0, 7, 0, 3);
        }
        var panel = new StackPanel { Margin = new Thickness(24) };
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "内置规则可以调整，默认模板始终保留", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "先在清理页勾选规则。这里修改其行为；保存返回后会用当前书稿重新预览，不修改原文件。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 16) });
        _rules.ItemsSource = AutomaticCheckOptions.CleanupLabels.Select(pair => new RuleChoice(pair.Key, pair.Value)).ToArray();
        panel.Children.Add(_rules);
        panel.Children.Add(_original);
        AddField(_adPanel, "广告匹配条件（普通文本为包含匹配，正则可用 | 分隔多个条件）", _pattern);
        _adPanel.Children.Add(_regex); _adPanel.Children.Add(_heuristics);
        AddField(_adPanel, "保留条件（优先于广告匹配；仅豁免广告算法，不影响其他规则）", _preserve);
        _adPanel.Children.Add(_preserveRegex);
        panel.Children.Add(_adPanel);
        AddField(_wrapPanel, "硬换行合并：前行最少字符数（1–1000，默认 8）", _minimum);
        panel.Children.Add(_wrapPanel);
        panel.Children.Add(_summary);
        AddButton(panel, "编辑本项自定义替换与测试…", () =>
        {
            if (!SaveCurrent()) return;
            var customization = _options.BuiltinOverrides.GetValueOrDefault(_key!, new());
            var editor = new TextCleanupRuleManagerWindow(customization.Rules, _source) { Owner = this };
            if (editor.ShowDialog() != true) return;
            var values = _options.BuiltinOverrides.ToDictionary(pair => pair.Key, pair => pair.Value);
            values[_key!] = customization with { Rules = editor.Rules };
            _options = _options with { BuiltinOverrides = values }; LoadCurrent();
        });
        var resetButtons = new WrapPanel(); panel.Children.Add(resetButtons);
        AddButton(resetButtons, "恢复本条默认", () => Restore(false));
        AddButton(resetButtons, "恢复全部内置规则默认", () => Restore(true));
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) }; panel.Children.Add(actions);
        AddButton(actions, "取消", Close);
        AddButton(actions, "保存并返回预览", () => { if (SaveCurrent()) DialogResult = true; });
        _rules.SelectionChanged += (_, _) =>
        {
            var next = _rules.SelectedValue as string;
            if (next == _key) return;
            if (_key is not null && !SaveCurrent()) { _rules.SelectedValue = _key; return; }
            _key = next; LoadCurrent();
        };
        _rules.SelectedIndex = 0;
    }

    private static void AddField(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 6) });
        panel.Children.Add(control);
    }
    private static void AddButton(Panel panel, string label, Action action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 10, 0) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void LoadCurrent()
    {
        if (_key is null) return;
        var customization = _options.BuiltinOverrides.GetValueOrDefault(_key, new());
        _original.IsChecked = customization.UseDefaultAlgorithm;
        _adPanel.Visibility = _key == nameof(TextCleanupOptions.RemoveSiteNotices) ? Visibility.Visible : Visibility.Collapsed;
        _wrapPanel.Visibility = _key == nameof(TextCleanupOptions.RepairHardWraps) ? Visibility.Visible : Visibility.Collapsed;
        _pattern.Text = _options.Advertisement.Pattern; _regex.IsChecked = _options.Advertisement.IsRegex;
        _preserve.Text = _options.Advertisement.PreservePattern; _preserveRegex.IsChecked = _options.Advertisement.PreserveIsRegex;
        _heuristics.IsChecked = _options.Advertisement.UsePromotionHeuristics;
        _minimum.Text = _options.HardWrapMinimumLength.ToString();
        _summary.Text = $"本项自定义替换：{customization.Rules.Count} 条。内置算法先运行，补充替换后运行；仍可在清理预览中逐项排除。";
    }
    private bool SaveCurrent()
    {
        try
        {
            var ad = _options.Advertisement with { Pattern = _pattern.Text, IsRegex = _regex.IsChecked == true, PreservePattern = _preserve.Text, PreserveIsRegex = _preserveRegex.IsChecked == true, UsePromotionHeuristics = _heuristics.IsChecked == true };
            ad.CompileMatcher(ad.Pattern, ad.IsRegex); ad.CompileMatcher(ad.PreservePattern, ad.PreserveIsRegex);
            if (!int.TryParse(_minimum.Text, out var minimum) || minimum is < 1 or > 1000) throw new InvalidOperationException("最少字符数须为 1–1000。");
            var values = _options.BuiltinOverrides.ToDictionary(pair => pair.Key, pair => pair.Value);
            values[_key!] = values.GetValueOrDefault(_key!, new()) with { UseDefaultAlgorithm = _original.IsChecked == true };
            _options = _options with { BuiltinOverrides = values, Advertisement = ad, HardWrapMinimumLength = minimum }; return true;
        }
        catch (Exception exception) { InkDialog.Show(this, exception.Message, "规则设置有误", MessageBoxButton.OK, MessageBoxImage.Warning); return false; }
    }
    private void Restore(bool all)
    {
        if (InkDialog.Show(this, "将清除所选范围的内置规则覆盖与补充替换，恢复软件默认算法和参数。独立自定义规则、规则勾选状态和逐项排除不会删除。继续？", "恢复默认", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _options = BuiltinCleanupConfiguration.Restore(_options, all ? null : _key); LoadCurrent();
    }
    private sealed record RuleChoice(string Key, string Value)
    {
        public override string ToString() => Value;
    }
}
