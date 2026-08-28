using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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
    private TextCleanupRuleRow? _editingRow;
    private bool _changingSelection;

    public TextCleanupRuleManagerWindow(IReadOnlyList<TextCleanupCustomRule> rules, string sourceText)
    {
        InitializeComponent();
        _sourceText = sourceText;
        _rows = new ObservableCollection<TextCleanupRuleRow>(rules.OrderBy(rule => rule.Order).Select(CreateRow));
        RulesGrid.DataContext = _rows;
        RulesGrid.ItemsSource = _rows;
        if (_rows.Count > 0) RulesGrid.SelectedIndex = 0;
    }

    public IReadOnlyList<TextCleanupCustomRule> Rules => _rows.Select((row, index) => row.Rule with { Order = index }).ToArray();

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitCurrentRule()) return;
        var row = CreateRow(new TextCleanupCustomRule { Name = $"规则 {_rows.Count + 1}", Order = _rows.Count });
        _rows.Add(row);
        ReindexRows();
        RulesGrid.SelectedItem = row;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not TextCleanupRuleRow row) return;
        var index = _rows.IndexOf(row);
        _editingRow = null;
        _rows.Remove(row);
        ReindexRows();
        if (_rows.Count > 0) RulesGrid.SelectedIndex = Math.Min(index, _rows.Count - 1);
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
        ReindexRows();
        RulesGrid.SelectedItem = row;
    }

    private void RulesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection) return;
        var row = RulesGrid.SelectedItem as TextCleanupRuleRow;
        if (_editingRow is not null && !ReferenceEquals(_editingRow, row) && !TryCommitRule(_editingRow))
        {
            _changingSelection = true;
            RulesGrid.SelectedItem = _editingRow;
            _changingSelection = false;
            return;
        }
        if (row is null)
        {
            _editingRow = null;
            return;
        }
        _editingRow = row;
        var rule = row.Rule;
        RuleNameText.Text = rule.Name;
        SelectByTag(RuleScopeCombo, rule.Scope.ToString());
        SelectByTag(RuleTypeCombo, rule.IsRegex ? "Regex" : "Literal");
        RulePatternText.Text = rule.Pattern;
        RuleReplacementText.Text = rule.Replacement;
        RuleEnabledCheck.IsChecked = rule.Enabled;
        IgnoreCaseCheck.IsChecked = rule.IgnoreCase;
        MultilineCheck.IsChecked = rule.Multiline;
        RuleTestResultText.Text = "修改后点击“更新当前规则”执行安全测试";
    }

    private void SaveRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not TextCleanupRuleRow) Add_Click(sender, e);
        _ = TryCommitCurrentRule();
    }

    private bool TryCommitCurrentRule() => _editingRow is null || TryCommitRule(_editingRow);

    private bool TryCommitRule(TextCleanupRuleRow row)
    {
        if (string.IsNullOrWhiteSpace(RuleNameText.Text) || string.IsNullOrEmpty(RulePatternText.Text))
        {
            RuleTestResultText.Text = "规则无效：名称和匹配内容不能为空";
            RuleTestResultText.Foreground = TryFindResource("ErrorBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Firebrick;
            return false;
        }
        var isRegex = SelectedTag(RuleTypeCombo) == "Regex";
        try
        {
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
            var matches = CountMatches(rule);
            row.UpdateRule(rule);
            RuleTestResultText.Text = $"测试通过：当前书稿实际命中 {matches} 处；应用规则组后可逐处定位与排除";
            RuleTestResultText.Foreground = TryFindResource("SuccessBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.SeaGreen;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            RuleTestResultText.Text = $"规则无效：{exception.Message}";
            RuleTestResultText.Foreground = TryFindResource("ErrorBrush") as System.Windows.Media.Brush ?? System.Windows.Media.Brushes.Firebrick;
            return false;
        }
    }

    private int CountMatches(TextCleanupCustomRule rule) =>
        TextCleanupPipeline.Apply(_sourceText, new TextCleanupOptions { CustomRules = [rule] }).Changes.Count;

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "导入文本清理规则组", Filter = "EasyPub 清理规则 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var rules = JsonSerializer.Deserialize<IReadOnlyList<TextCleanupCustomRule>>(File.ReadAllText(dialog.FileName), JsonOptions) ?? [];
            _changingSelection = true;
            _editingRow = null;
            _rows.Clear();
            foreach (var rule in rules.OrderBy(rule => rule.Order)) _rows.Add(CreateRow(rule));
            ReindexRows();
            _changingSelection = false;
            if (_rows.Count > 0) RulesGrid.SelectedIndex = 0;
        }
        catch (Exception exception)
        {
            _changingSelection = false;
            InkDialog.Show(this, exception.Message, "无法导入规则组", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitCurrentRule()) return;
        var dialog = new SaveFileDialog { Title = "导出文本清理规则组", Filter = "EasyPub 清理规则 (*.json)|*.json", FileName = "EasyPub-文本清理规则.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(Rules, JsonOptions)); }
        catch (Exception exception) { InkDialog.Show(this, exception.Message, "无法导出规则组", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!TryCommitCurrentRule()) return;
        DialogResult = true;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private static string SelectedTag(ComboBox combo) => ((ComboBoxItem)combo.SelectedItem).Tag!.ToString()!;
    private static void SelectByTag(ComboBox combo, string value) => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().First(item => string.Equals(item.Tag?.ToString(), value, StringComparison.Ordinal));

    private void ReindexRows()
    {
        for (var index = 0; index < _rows.Count; index++) _rows[index].Order = index;
    }

    private TextCleanupRuleRow CreateRow(TextCleanupCustomRule rule)
    {
        var row = new TextCleanupRuleRow(rule);
        row.PropertyChanged += RuleRow_PropertyChanged;
        return row;
    }

    private void RuleRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TextCleanupRuleRow.Enabled) && ReferenceEquals(sender, _editingRow) && sender is TextCleanupRuleRow row)
            RuleEnabledCheck.IsChecked = row.Enabled;
    }
}

public sealed class TextCleanupRuleRow(TextCleanupCustomRule rule) : INotifyPropertyChanged
{
    public TextCleanupCustomRule Rule { get; private set; } = rule;
    public bool Enabled
    {
        get => Rule.Enabled;
        set
        {
            if (Rule.Enabled == value) return;
            Rule = Rule with { Enabled = value };
            OnPropertyChanged();
        }
    }
    public int Order
    {
        get => Rule.Order;
        set
        {
            if (Rule.Order == value) return;
            Rule = Rule with { Order = value };
            OnPropertyChanged();
        }
    }
    public string Name => Rule.Name;
    public string TypeLabel => Rule.IsRegex ? "正则" : "普通";
    public string ScopeLabel => Rule.Scope switch { TextCleanupRuleScope.BodyOnly => "仅正文", TextCleanupRuleScope.ChapterTitlesOnly => "仅标题", _ => "全部" };

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateRule(TextCleanupCustomRule updated)
    {
        Rule = updated;
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(Order));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(ScopeLabel));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
