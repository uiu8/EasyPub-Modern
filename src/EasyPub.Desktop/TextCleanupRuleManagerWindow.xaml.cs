using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class TextCleanupRuleManagerWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _sourceText;
    private readonly ObservableCollection<TextCleanupRuleRow> _rows;

    public TextCleanupRuleManagerWindow(IReadOnlyList<TextCleanupCustomRule> rules, string sourceText)
    {
        InitializeComponent();
        _sourceText = sourceText;
        _rows = new ObservableCollection<TextCleanupRuleRow>(rules.OrderBy(rule => rule.Order).Select(rule => new TextCleanupRuleRow(rule)));
        RulesGrid.DataContext = _rows;
        RulesGrid.ItemsSource = _rows;
        if (_rows.Count > 0) RulesGrid.SelectedIndex = 0;
    }

    public IReadOnlyList<TextCleanupCustomRule> Rules => _rows.Select((row, index) => row.Rule with { Order = index }).ToArray();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var row = new TextCleanupRuleRow(new TextCleanupCustomRule { Name = $"规则 {_rows.Count + 1}", Order = _rows.Count });
        _rows.Add(row);
        RulesGrid.SelectedItem = row;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is TextCleanupRuleRow row) _rows.Remove(row);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (RulesGrid.SelectedItem is not TextCleanupRuleRow row) return;
        var oldIndex = _rows.IndexOf(row);
        var newIndex = Math.Clamp(oldIndex + delta, 0, _rows.Count - 1);
        if (oldIndex == newIndex) return;
        _rows.Move(oldIndex, newIndex);
        RulesGrid.SelectedItem = row;
    }

    private void RulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not TextCleanupRuleRow row) return;
        var rule = row.Rule;
        RuleNameText.Text = rule.Name;
        RuleOrderText.Text = rule.Order.ToString();
        SelectByTag(RuleScopeCombo, rule.Scope.ToString());
        SelectByTag(RuleTypeCombo, rule.IsRegex ? "Regex" : "Literal");
        RulePatternText.Text = rule.Pattern;
        RuleReplacementText.Text = rule.Replacement;
        RuleEnabledCheck.IsChecked = rule.Enabled;
        IgnoreCaseCheck.IsChecked = rule.IgnoreCase;
        MultilineCheck.IsChecked = rule.Multiline;
        RuleTestResultText.Text = "修改后点击“保存当前规则”执行安全测试";
    }

    private void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not TextCleanupRuleRow row) { Add_Click(sender, e); row = (TextCleanupRuleRow)RulesGrid.SelectedItem; }
        if (string.IsNullOrWhiteSpace(RuleNameText.Text) || string.IsNullOrEmpty(RulePatternText.Text))
        {
            InkDialog.Show(this, "规则名称和匹配内容不能为空。", "无法保存规则");
            return;
        }
        var isRegex = SelectedTag(RuleTypeCombo) == "Regex";
        try
        {
            var matches = CountMatches(RulePatternText.Text, isRegex, IgnoreCaseCheck.IsChecked == true, MultilineCheck.IsChecked == true);
            var rule = row.Rule with
            {
                Name = RuleNameText.Text.Trim(),
                Pattern = RulePatternText.Text,
                Replacement = RuleReplacementText.Text,
                IsRegex = isRegex,
                Scope = Enum.Parse<TextCleanupRuleScope>(SelectedTag(RuleScopeCombo)),
                IgnoreCase = IgnoreCaseCheck.IsChecked == true,
                Multiline = MultilineCheck.IsChecked == true,
                Enabled = RuleEnabledCheck.IsChecked == true,
            };
            var index = _rows.IndexOf(row);
            var replacement = new TextCleanupRuleRow(rule);
            _rows[index] = replacement;
            RulesGrid.SelectedItem = replacement;
            RuleTestResultText.Text = $"测试通过：当前书稿命中 {matches} 处";
            RuleTestResultText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SuccessBrush"];
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            RuleTestResultText.Text = $"规则无效：{exception.Message}";
            RuleTestResultText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ErrorBrush"];
        }
    }

    private int CountMatches(string pattern, bool isRegex, bool ignoreCase, bool multiline)
    {
        if (!isRegex)
        {
            var comparison = ignoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.Ordinal;
            var count = 0;
            for (var index = 0; (index = _sourceText.IndexOf(pattern, index, comparison)) >= 0; index += Math.Max(1, pattern.Length)) count++;
            return count;
        }
        var options = RegexOptions.CultureInvariant;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        if (multiline) options |= RegexOptions.Multiline | RegexOptions.Singleline;
        return Regex.Matches(_sourceText, pattern, options, TimeSpan.FromMilliseconds(250)).Count;
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入文本清理规则组", Filter = "EasyPub 清理规则 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var rules = JsonSerializer.Deserialize<IReadOnlyList<TextCleanupCustomRule>>(File.ReadAllText(dialog.FileName), JsonOptions) ?? [];
            _rows.Clear();
            foreach (var rule in rules.OrderBy(rule => rule.Order)) _rows.Add(new TextCleanupRuleRow(rule));
            if (_rows.Count > 0) RulesGrid.SelectedIndex = 0;
        }
        catch (Exception exception) { InkDialog.Show(this, exception.Message, "无法导入规则组", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出文本清理规则组", Filter = "EasyPub 清理规则 (*.json)|*.json", FileName = "EasyPub-文本清理规则.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(Rules, JsonOptions)); }
        catch (Exception exception) { InkDialog.Show(this, exception.Message, "无法导出规则组", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private static string SelectedTag(ComboBox combo) => ((ComboBoxItem)combo.SelectedItem).Tag!.ToString()!;
    private static void SelectByTag(ComboBox combo, string value) => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(item => string.Equals(item.Tag?.ToString(), value, StringComparison.Ordinal));
}

public sealed class TextCleanupRuleRow(TextCleanupCustomRule rule)
{
    public TextCleanupCustomRule Rule { get; } = rule;
    public bool Enabled => Rule.Enabled;
    public int Order => Rule.Order;
    public string Name => Rule.Name;
    public string TypeLabel => Rule.IsRegex ? "正则" : "普通";
    public string ScopeLabel => Rule.Scope switch { TextCleanupRuleScope.BodyOnly => "仅正文", TextCleanupRuleScope.ChapterTitlesOnly => "仅标题", _ => "全部" };
}
