using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class SettingsWindow : Window
{
    private readonly Action _manageFavorites;
    private IReadOnlyDictionary<string, string> _shortcutBindings;

    public SettingsWindow(
        string theme,
        string density,
        int scalePercent,
        bool rememberWindowPlacement,
        bool reduceMotion,
        string outputDirectory,
        string kindleGenPath,
        int parallelism,
        bool validationEnabled,
        int reportRetention,
        bool autoOpenTaskCenter,
        bool autoOpenOutputDirectory,
        IReadOnlyDictionary<string, string> shortcutBindings,
        int favoriteFolderCount,
        Action manageFavorites)
    {
        InitializeComponent();
        Loaded += (_, _) => ThemeManager.Apply(theme, this);
        _manageFavorites = manageFavorites;
        _shortcutBindings = new Dictionary<string, string>(shortcutBindings, StringComparer.OrdinalIgnoreCase);
        SelectByTag(ThemeCombo, theme);
        SyncThemeRadios(theme);
        SelectByTag(DensityCombo, density);
        SelectByTag(ScaleCombo, scalePercent.ToString(CultureInfo.InvariantCulture));
        RememberWindowCheck.IsChecked = rememberWindowPlacement;
        ReduceMotionCheck.IsChecked = reduceMotion;
        DefaultOutputText.Text = outputDirectory;
        KindleGenPathText.Text = kindleGenPath;
        SelectByTag(ParallelismSettingsCombo, parallelism.ToString(CultureInfo.InvariantCulture));
        ValidationSettingsCheck.IsChecked = validationEnabled;
        SelectByTag(RetentionSettingsCombo, reportRetention.ToString(CultureInfo.InvariantCulture));
        RetentionSettingsCombo.IsEnabled = validationEnabled;
        AutoOpenTaskCenterSettingsCheck.IsChecked = autoOpenTaskCenter;
        AutoOpenOutputSettingsCheck.IsChecked = autoOpenOutputDirectory;
        FavoriteCountText.Text = $"已收藏 {favoriteFolderCount} 个常用目录；可从“添加书稿”菜单直接进入。";
    }

    public string Theme => SelectedTag(ThemeCombo, ThemeManager.LightTheme);
    public string Density => SelectedTag(DensityCombo, "Comfortable");
    public int ScalePercent => int.Parse(SelectedTag(ScaleCombo, "100"), CultureInfo.InvariantCulture);
    public bool RememberWindowPlacement => RememberWindowCheck.IsChecked == true;
    public bool ReduceMotion => ReduceMotionCheck.IsChecked == true;
    public string OutputDirectory => DefaultOutputText.Text.Trim();
    public string KindleGenPath => KindleGenPathText.Text.Trim();
    public int Parallelism => int.Parse(SelectedTag(ParallelismSettingsCombo, "0"), CultureInfo.InvariantCulture);
    public bool ValidationEnabled => ValidationSettingsCheck.IsChecked == true;
    public int ReportRetention => int.Parse(SelectedTag(RetentionSettingsCombo, "10"), CultureInfo.InvariantCulture);
    public bool AutoOpenTaskCenter => AutoOpenTaskCenterSettingsCheck.IsChecked == true;
    public bool AutoOpenOutputDirectory => AutoOpenOutputSettingsCheck.IsChecked == true;
    public IReadOnlyDictionary<string, string> ShortcutBindings => _shortcutBindings;
    public int SelectedSection
    {
        get => SettingsTabs.SelectedIndex;
        set => SettingsTabs.SelectedIndex = Math.Clamp(value, 0, SettingsTabs.Items.Count - 1);
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择默认输出目录", InitialDirectory = Directory.Exists(DefaultOutputText.Text) ? DefaultOutputText.Text : null };
        if (dialog.ShowDialog(this) == true) DefaultOutputText.Text = dialog.FolderName;
    }

    private void BrowseKindleGen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 kindlegen_v2.9.exe", Filter = "KindleGen (kindlegen*.exe)|kindlegen*.exe|可执行文件 (*.exe)|*.exe" };
        if (dialog.ShowDialog(this) == true) KindleGenPathText.Text = dialog.FileName;
    }

    private void TestKindleGen_Click(object sender, RoutedEventArgs e)
    {
        var path = KindleGenPathText.Text.Trim();
        if (!File.Exists(path))
        {
            EngineStatusText.Text = "未找到文件，请重新选择。";
            EngineStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["ErrorBrush"];
            return;
        }
        EngineStatusText.Text = Path.GetFileName(path).Contains("kindlegen", StringComparison.OrdinalIgnoreCase)
            ? "引擎文件存在，可以用于转换。"
            : "文件存在，但名称不像 KindleGen，请谨慎确认。";
        EngineStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SuccessBrush"];
    }

    private void ValidationSettingsCheck_Click(object sender, RoutedEventArgs e) => RetentionSettingsCombo.IsEnabled = ValidationSettingsCheck.IsChecked == true;
    private void ThemeRadio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: not null } radioButton) return;
        SelectByTag(ThemeCombo, radioButton.Tag.ToString()!);
    }

    private void ManageFavorites_Click(object sender, RoutedEventArgs e) { _manageFavorites(); FavoriteCountText.Text = "收藏目录已更新；返回工作区后菜单会立即刷新。"; }

    private void OpenAppData_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyPub Modern");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void OpenReports_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(OutputDirectory, EasyPub.Core.ArtifactValidationService.ReportDirectoryName);
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
    }

    private void ViewLatestReport_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(OutputDirectory, EasyPub.Core.ArtifactValidationService.ReportDirectoryName);
        var latest = Directory.Exists(path) ? new DirectoryInfo(path).EnumerateFiles("*", SearchOption.AllDirectories).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault(file => file.Extension is ".html" or ".json" or ".txt") : null;
        if (latest is null) { InkDialog.Show(this, "还没有验收报告。完成一次启用结构验收的转换后再查看。", "验收报告"); return; }
        Process.Start(new ProcessStartInfo(latest.FullName) { UseShellExecute = true });
    }

    private void ManageShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var window = new ShortcutManagerWindow(_shortcutBindings) { Owner = this };
        if (window.ShowDialog() == true) _shortcutBindings = window.Bindings;
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var result = InkDialog.Show(
            this,
            "将界面、性能、验收、自动打开选项和快捷键恢复为默认值。默认输出目录会改为桌面；KindleGen 路径和收藏文件夹不会被删除。是否继续？",
            "恢复全部默认",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        SelectByTag(ThemeCombo, ThemeManager.LightTheme);
        SyncThemeRadios(ThemeManager.LightTheme);
        SelectByTag(DensityCombo, "Comfortable");
        SelectByTag(ScaleCombo, "100");
        RememberWindowCheck.IsChecked = true;
        ReduceMotionCheck.IsChecked = false;
        DefaultOutputText.Text = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        SelectByTag(ParallelismSettingsCombo, "0");
        ValidationSettingsCheck.IsChecked = false;
        SelectByTag(RetentionSettingsCombo, "10");
        RetentionSettingsCombo.IsEnabled = false;
        AutoOpenTaskCenterSettingsCheck.IsChecked = false;
        AutoOpenOutputSettingsCheck.IsChecked = false;
        _shortcutBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SaveHintText.Text = "已恢复默认值；点击“保存并返回工作区”后生效";
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/uiu8/EasyPub-Modern") { UseShellExecute = true });

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OutputDirectory))
        {
            InkDialog.Show(this, "默认输出目录不能为空。", "EasyPub Modern");
            SettingsTabs.SelectedIndex = 1;
            DefaultOutputText.Focus();
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void SelectByTag(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (!string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase)) continue;
            comboBox.SelectedItem = item;
            return;
        }
        comboBox.SelectedIndex = 0;
    }

    private void SyncThemeRadios(string theme)
    {
        SystemThemeRadio.IsChecked = string.Equals(theme, ThemeManager.SystemTheme, StringComparison.OrdinalIgnoreCase);
        LightThemeRadio.IsChecked = string.Equals(theme, ThemeManager.LightTheme, StringComparison.OrdinalIgnoreCase);
        DarkThemeRadio.IsChecked = string.Equals(theme, ThemeManager.DarkTheme, StringComparison.OrdinalIgnoreCase);
        if (SystemThemeRadio.IsChecked != true && LightThemeRadio.IsChecked != true && DarkThemeRadio.IsChecked != true)
            LightThemeRadio.IsChecked = true;
    }

    private static string SelectedTag(ComboBox comboBox, string fallback) => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
}
