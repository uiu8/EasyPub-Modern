using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public sealed class AutomaticCheckWindow : Window
{
    private readonly CheckBox _enabled;
    private readonly Dictionary<PreflightTargetKind, CheckBox> _targets = [];
    private readonly Dictionary<string, CheckBox> _cleanup = [];
    private readonly ComboBox _variant;
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12) };
    private IReadOnlyList<TextCleanupCustomRule> _rules;
    public AutomaticCheckOptions Result { get; private set; }

    public AutomaticCheckWindow(AutomaticCheckOptions initial, IReadOnlyList<TextCleanupCustomRule> existingRules)
    {
        Result = initial;
        _rules = initial.Cleanup.CustomRules;
        Title = "自动检查设置 · EasyPub Modern";
        Width = 700; Height = 760; MinWidth = 540; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "PrimaryTextBrush");
        FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(8) };
        var save = new Button { Content = "保存检查设置", Margin = new Thickness(8), IsDefault = true };
        save.Click += (_, _) => { Result = Capture(); DialogResult = true; };
        footer.Children.Add(cancel); footer.Children.Add(save);
        var panel = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        panel.Children.Add(new TextBlock { Text = "自动检查", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "导入书稿或调整设置后，按所选规则检查。检查只提供建议；清理需在预览中确认。TXT 清理规则不用于 EPUB。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 16) });
        _enabled = new CheckBox { Content = "开启自动检查", IsChecked = initial.Enabled, Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_enabled);
        panel.Children.Add(_summary);
        var choices = new StackPanel(); panel.Children.Add(choices);
        choices.Children.Add(new TextBlock { Text = "基础检查", FontSize = 17, Margin = new Thickness(0, 10, 0, 8) });
        var targetWrap = new WrapPanel(); choices.Children.Add(targetWrap);
        foreach (var item in AutomaticCheckOptions.TargetLabels)
        {
            var check = new CheckBox { Content = item.Value, IsChecked = initial.Targets.Contains(item.Key), Width = 280, Margin = new Thickness(0, 6, 0, 6) };
            _targets.Add(item.Key, check); targetWrap.Children.Add(check);
        }
        choices.Children.Add(new TextBlock { Text = "文本清理规则", FontSize = 17, Margin = new Thickness(0, 18, 0, 8) });
        var cleanupWrap = new WrapPanel(); choices.Children.Add(cleanupWrap);
        foreach (var item in AutomaticCheckOptions.CleanupLabels)
        {
            var check = new CheckBox { Content = item.Value, IsChecked = (bool)typeof(TextCleanupOptions).GetProperty(item.Key)!.GetValue(initial.Cleanup)!, Width = 280, Margin = new Thickness(0, 6, 0, 6) };
            _cleanup.Add(item.Key, check); cleanupWrap.Children.Add(check);
        }
        _variant = new ComboBox { ItemsSource = new[] { "不检查简繁转换", "繁体转简体", "简体转繁体" }, SelectedIndex = (int)initial.Cleanup.ChineseVariant, Margin = new Thickness(0, 10, 0, 10) };
        choices.Children.Add(_variant);
        var custom = new Button { Content = "编辑自定义检查规则…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        custom.Click += (_, _) =>
        {
            var editor = new TextCleanupRuleManagerWindow(_rules, "第一章 示例\n正文示例，可在规则编辑器里输入测试文本。") { Owner = this };
            if (editor.ShowDialog() == true) { _rules = editor.Rules; Refresh(); }
        };
        choices.Children.Add(custom);
        var import = new Button { Content = "复制文本清理中的自定义规则", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        import.Click += (_, _) => { _rules = _rules.Concat(existingRules.Where(rule => !_rules.Any(saved => saved.Id == rule.Id))).ToArray(); Refresh(); };
        choices.Children.Add(import);
        choices.Children.Add(new TextBlock { Text = "自定义规则编辑器中的“启用”决定是否检查。复制和编辑不改变转换清理配置。", TextWrapping = TextWrapping.Wrap });
        foreach (var check in _targets.Values.Concat(_cleanup.Values).Append(_enabled))
        { check.Checked += (_, _) => Refresh(); check.Unchecked += (_, _) => Refresh(); }
        _variant.SelectionChanged += (_, _) => Refresh();
        void Refresh() { choices.IsEnabled = _enabled.IsChecked == true; _summary.Text = Capture().Summary; }
        Refresh();
    }

    private AutomaticCheckOptions Capture()
    {
        var cleanup = new TextCleanupOptions { ChineseVariant = (ChineseVariantConversion)_variant.SelectedIndex, CustomRules = _rules };
        foreach (var item in _cleanup) typeof(TextCleanupOptions).GetProperty(item.Key)!.SetValue(cleanup, item.Value.IsChecked == true);
        return new AutomaticCheckOptions { Enabled = _enabled.IsChecked == true, Targets = _targets.Where(item => item.Value.IsChecked == true).Select(item => item.Key).ToArray(), Cleanup = cleanup };
    }
}
