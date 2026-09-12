using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;

namespace EasyPub.Desktop;

public sealed class NumericHeadingRulesWindow : Window
{
    private readonly TextBox _pattern = new() { AcceptsReturn = true, MinHeight = 75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _minimum = new() { Width = 70 };
    private readonly TextBox _sample = new() { Text = "001：规则", MinHeight = 38 };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _enabled = new() { Content = "开启数字章号识别" };
    private readonly ComboBox _scope = new() { ItemsSource = new[] { "仅本书", "设为全局默认（本书也继承）", "本书恢复继承全局默认" } };
    private readonly ComboBox _presets = new() { MinWidth = 200 };
    private readonly TextBox _presetName = new() { ToolTip = "输入新名称另存方案；相同名称会询问是否覆盖。" };
    private readonly List<NamedNumericHeadingPreset> _saved;
    private readonly TocHierarchyOptions _global;
    public TocHierarchyOptions Result { get; private set; }
    public int Scope => _scope.SelectedIndex;
    public IReadOnlyList<NamedNumericHeadingPreset> Presets => _saved.ToArray();

    public NumericHeadingRulesWindow(TocHierarchyOptions current, TocHierarchyOptions global,
        IReadOnlyList<NamedNumericHeadingPreset> presets, bool inherits)
    {
        Title = "数字章号识别 · 规则与方案";
        Width = 760; Height = 650; MinWidth = 580; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        Result = current; _global = global; _saved = presets.ToList();
        var root = new DockPanel { Margin = new Thickness(20) };
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Children.Add(Button("取消", () => Close()));
        footer.Children.Add(Button("应用到章节工作台", Apply));
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var content = new StackPanel();
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        void Label(string text) => content.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 6) });
        Label("作用范围：全局影响继承默认的书；已有本书自定义和已保存章节树不会自动重建。");
        content.Children.Add(_scope); _scope.SelectedIndex = inherits ? 2 : 0;
        _scope.SelectionChanged += (_, _) =>
        {
            if (Scope == 2) Load(_global);
            _pattern.IsEnabled = _minimum.IsEnabled = _enabled.IsEnabled = Scope != 2;
        };
        Label("已保存方案（输入名称可另存；方案包含开关、表达式和最低正文行数）");
        content.Children.Add(_presets);
        _presets.SelectionChanged += (_, _) => { if (_presets.SelectedItem is string name) _presetName.Text = name; };
        Label("方案名称"); content.Children.Add(_presetName);
        var actions = new WrapPanel();
        actions.Children.Add(Button("载入方案", LoadPreset));
        actions.Children.Add(Button("保存方案", SavePreset));
        actions.Children.Add(Button("删除方案", DeletePreset));
        actions.Children.Add(Button("恢复内置默认", () => { _scope.SelectedIndex = 0; Load(new()); }));
        content.Children.Add(actions);
        content.Children.Add(_enabled);
        Label("识别表达式：保留 (?<number>...) 章号组、(?<title>...) 标题组。章号仍限 1–9999。");
        content.Children.Add(_pattern);
        Label("示例：至少三位数字加冒号（001 仍会匹配；未补零的 1～99 章会漏识别，请先测试）");
        content.Children.Add(Button("填入三位数字示例", () => { _scope.SelectedIndex = 0; _pattern.Text = @"^\s*(?<number>\d{3,4})[：:]\s*(?<title>\S.*?)\s*$"; }));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        line.Children.Add(new TextBlock { Text = "正文至少 ", VerticalAlignment = VerticalAlignment.Center });
        line.Children.Add(_minimum);
        line.Children.Add(new TextBlock { Text = " 行（不含空行和标题；0 不限）", VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(line);
        Label("测试一行文字（只测试格式，不判断正文行数）");
        content.Children.Add(_sample); content.Children.Add(Button("测试匹配", Test)); content.Children.Add(_feedback);
        Label("所有改动先留在工作台；保存章节树时提交。取消工作台则不保存方案和全局修改。修改识别条件后请重新识别。");
        Content = root; RefreshPresets(); Load(current);
        _pattern.IsEnabled = _minimum.IsEnabled = _enabled.IsEnabled = Scope != 2;
    }

    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(0, 6, 8, 6), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += (_, _) => { try { action(); } catch (Exception error) { MessageBox.Show(error.Message, "数字识别规则"); } };
        return button;
    }
    private void Load(TocHierarchyOptions value)
    { _pattern.Text = value.NumericHeadingPattern; _minimum.Text = value.NumericHeadingMinimumBodyLines.ToString(); _enabled.IsChecked = value.RecognizeNumericHeadings; }
    private TocHierarchyOptions Read()
    {
        NumericHeadingRule.Compile(_pattern.Text);
        if (!int.TryParse(_minimum.Text, out var minimum) || minimum < 0) throw new ArgumentException("正文行数请输入非负整数。");
        return new() { RecognizeNumericHeadings = _enabled.IsChecked == true, NumericHeadingMinimumBodyLines = minimum, NumericHeadingPattern = _pattern.Text };
    }
    private void RefreshPresets() => _presets.ItemsSource = _saved.Select(p => p.Name).ToArray();
    private void SavePreset()
    {
        var name = _presetName.Text.Trim();
        if (name.Length == 0) throw new ArgumentException("请先输入方案名称。");
        var value = Read();
        var index = _saved.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && MessageBox.Show(this, "替换同名方案？", "保存方案", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var preset = new NamedNumericHeadingPreset(name, value.NumericHeadingPattern, value.NumericHeadingMinimumBodyLines, value.RecognizeNumericHeadings);
        if (index >= 0) _saved[index] = preset; else _saved.Add(preset);
        RefreshPresets(); _presets.SelectedItem = name; _feedback.Text = "方案已暂存，保存章节树后写入设置。";
    }
    private void LoadPreset()
    {
        var preset = _saved.FirstOrDefault(p => p.Name == _presets.SelectedItem as string) ?? throw new ArgumentException("请选择已保存方案。");
        _scope.SelectedIndex = 0;
        Load(new() { RecognizeNumericHeadings = preset.Enabled, NumericHeadingMinimumBodyLines = preset.MinimumBodyLines, NumericHeadingPattern = preset.Pattern });
    }
    private void DeletePreset()
    {
        var index = _saved.FindIndex(p => p.Name == _presets.SelectedItem as string);
        if (index < 0) return;
        if (MessageBox.Show(this, "删除所选方案？当前已载入规则不受影响。", "删除方案", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _saved.RemoveAt(index); RefreshPresets(); _presetName.Text = "";
    }
    private void Test() => _feedback.Text = NumericHeadingRule.Matches(NumericHeadingRule.Compile(_pattern.Text), _sample.Text)
        ? "格式匹配；是否成为章节还取决于开关和正文行数。" : "格式不匹配，不会由数字规则识别。";
    private void Apply() { Result = Scope == 2 ? _global : Read(); DialogResult = true; }
}
