using System.Text.RegularExpressions;
using System.Windows;
using EasyPub.Core;

namespace EasyPub.Desktop;

public partial class TocHierarchyWindow : Window
{
    public TocHierarchyWindow(TocHierarchyOptions options)
    {
        InitializeComponent();
        EnableCheck.IsChecked = options.Enabled;
        Level1PatternText.Text = Normalize(options.Level1Pattern, TocHierarchyOptions.DefaultLevel1Pattern);
        Level2PatternText.Text = Normalize(options.Level2Pattern, TocHierarchyOptions.DefaultLevel2Pattern);
        Level3PatternText.Text = Normalize(options.Level3Pattern, TocHierarchyOptions.DefaultLevel3Pattern);
    }

    public TocHierarchyOptions? Result { get; private set; }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        Level1PatternText.Text = TocHierarchyOptions.DefaultLevel1Pattern;
        Level2PatternText.Text = TocHierarchyOptions.DefaultLevel2Pattern;
        Level3PatternText.Text = TocHierarchyOptions.DefaultLevel3Pattern;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ValidateRegex(Level1PatternText.Text, "一级目录");
            ValidateRegex(Level2PatternText.Text, "二级目录");
            ValidateRegex(Level3PatternText.Text, "三级目录");
        }
        catch (ArgumentException exception)
        {
            MessageBox.Show(this, exception.Message, "正则表达式无效", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new TocHierarchyOptions
        {
            Enabled = EnableCheck.IsChecked == true,
            Level1Pattern = Level1PatternText.Text.Trim(),
            Level2Pattern = Level2PatternText.Text.Trim(),
            Level3Pattern = Level3PatternText.Text.Trim(),
        };
        DialogResult = true;
    }

    private static void ValidateRegex(string pattern, string name)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new ArgumentException($"{name}正则不能为空。");
        try { _ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException exception) { throw new ArgumentException($"{name}正则无效：{exception.Message}", exception); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private static string Normalize(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;
}
