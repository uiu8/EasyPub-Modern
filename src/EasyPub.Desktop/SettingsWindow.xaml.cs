using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class SettingsWindow : Window
{
    private readonly Action _manageFavorites;
    private readonly PendingUpdateStore _pendingUpdates = PendingUpdateStore.CreateDefault();
    private readonly UpdateCheckCacheStore _updateCheckCache = UpdateCheckCacheStore.CreateDefault();
    private IReadOnlyDictionary<string, string> _shortcutBindings;
    private UpdateRelease? _availableRelease;
    private bool _updateReadyToApply;
    private CancellationTokenSource? _updateCancellation;

    /// <summary>当前可执行文件的版本，取自程序集而不是写死的字符串。</summary>
    private static Version CurrentVersion => typeof(SettingsWindow).Assembly.GetName().Version ?? new Version(0, 0, 0);

    public SettingsWindow(
        string theme,
        string density,
        int scalePercent,
        bool rememberWindowPlacement,
        bool reduceMotion,
        string outputDirectory,
        string kindleGenPath,
        string textEditorPath,
        int parallelism,
        bool validationEnabled,
        int reportRetention,
        bool autoOpenTaskCenter,
        bool autoOpenOutputDirectory,
        bool autoCheckUpdate,
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
        TextEditorPathText.Text = string.IsNullOrWhiteSpace(textEditorPath) ? "notepad.exe" : textEditorPath;
        SelectByTag(ParallelismSettingsCombo, parallelism.ToString(CultureInfo.InvariantCulture));
        ValidationSettingsCheck.IsChecked = validationEnabled;
        SelectByTag(RetentionSettingsCombo, reportRetention.ToString(CultureInfo.InvariantCulture));
        RetentionSettingsCombo.IsEnabled = validationEnabled;
        AutoOpenTaskCenterSettingsCheck.IsChecked = autoOpenTaskCenter;
        AutoOpenOutputSettingsCheck.IsChecked = autoOpenOutputDirectory;
        FavoriteCountText.Text = $"已收藏 {favoriteFolderCount} 个常用目录；可从“添加书稿”菜单直接进入。";
        AutoCheckUpdateCheck.IsChecked = autoCheckUpdate;
        VersionText.Text = $"版本 {AppVersion.Display(CurrentVersion)} · TXT / EPUB 到 Kindle";
        ShowAlreadyDownloadedUpdate();
    }

    /// <summary>上一次运行已经下载好但没来得及安装的更新，进设置页时直接显示出来，不必重新下载。</summary>
    private void ShowAlreadyDownloadedUpdate()
    {
        if (_pendingUpdates.LoadReady() is not { } pending) return;
        _updateReadyToApply = true;
        UpdateCard.Visibility = Visibility.Visible;
        UpdateTitleText.Text = $"新版本 {pending.Version} 已下载";
        UpdateNotesText.Text = "更新包已准备就绪，重启软件即可完成更新。";
        InstallUpdateButton.Content = "立即重启更新";
        UpdateHintText.Text = "也可以直接关闭软件，退出时会自动完成更新。";
    }

    public string Theme => SelectedTag(ThemeCombo, ThemeManager.LightTheme);
    public string Density => SelectedTag(DensityCombo, "Comfortable");
    public int ScalePercent => int.Parse(SelectedTag(ScaleCombo, "100"), CultureInfo.InvariantCulture);
    public bool RememberWindowPlacement => RememberWindowCheck.IsChecked == true;
    public bool ReduceMotion => ReduceMotionCheck.IsChecked == true;
    public string OutputDirectory => DefaultOutputText.Text.Trim();
    public string KindleGenPath => KindleGenPathText.Text.Trim();
    public string TextEditorPath => string.IsNullOrWhiteSpace(TextEditorPathText.Text) ? "notepad.exe" : TextEditorPathText.Text.Trim();
    public int Parallelism => int.Parse(SelectedTag(ParallelismSettingsCombo, "0"), CultureInfo.InvariantCulture);
    public bool ValidationEnabled => ValidationSettingsCheck.IsChecked == true;
    public int ReportRetention => int.Parse(SelectedTag(RetentionSettingsCombo, "10"), CultureInfo.InvariantCulture);
    public bool AutoOpenTaskCenter => AutoOpenTaskCenterSettingsCheck.IsChecked == true;
    public bool AutoOpenOutputDirectory => AutoOpenOutputSettingsCheck.IsChecked == true;
    public bool AutoCheckUpdate => AutoCheckUpdateCheck.IsChecked == true;
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

    private void BrowseTextEditor_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 TXT 编辑器", Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == true) TextEditorPathText.Text = dialog.FileName;
    }

    private void ResetTextEditor_Click(object sender, RoutedEventArgs e) => TextEditorPathText.Text = "notepad.exe";

    private async void TestKindleGen_Click(object sender, RoutedEventArgs e)
    {
        EngineStatusText.Text = "正在实际启动 KindleGen 进行自检…";
        EngineStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources["SecondaryTextBrush"];
        var result = await KindleGenHealthProbe.ProbeAsync(KindleGenPathText.Text.Trim());
        EngineStatusText.Text = result.Message;
        EngineStatusText.Foreground = (System.Windows.Media.Brush)Application.Current.Resources[
            result.IsReady ? "SuccessBrush" : result.Status == KindleGenHealthStatus.Missing ? "ErrorBrush" : "WarningBrush"];
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
        AutoCheckUpdateCheck.IsChecked = true;
        TextEditorPathText.Text = "notepad.exe";
        _shortcutBindings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SaveHintText.Text = "已恢复默认值；点击“保存并返回工作区”后生效";
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e) => Process.Start(new ProcessStartInfo("https://github.com/uiu8/EasyPub-Modern") { UseShellExecute = true });

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e) => await CheckForUpdateAsync();

    private async Task CheckForUpdateAsync()
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "正在检查更新…";
        try
        {
            var result = await UpdateChecker.CheckAsync(CurrentVersion);
            // 手动检查不受节流限制，但结果照样记下来，省掉紧接着的下一次启动请求。
            if (result.Status is UpdateCheckStatus.UpToDate or UpdateCheckStatus.Available)
                _updateCheckCache.Save(new UpdateCheckCache(DateTimeOffset.Now, result.Release?.Tag ?? string.Empty));
            // 走的是哪个源值得写出来——GitHub 不通时它会自动落到镜像上，用户该知道。
            UpdateStatusText.Text = result is { Status: not UpdateCheckStatus.Failed, Source: { } source }
                ? $"{result.Message}（来源：{source.Name}）"
                : result.Message;
            if (result is { Status: UpdateCheckStatus.Available, Release: { } release }) ShowAvailableRelease(release);
            else if (!_updateReadyToApply) UpdateCard.Visibility = Visibility.Collapsed;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ShowAvailableRelease(UpdateRelease release)
    {
        _availableRelease = release;
        UpdateCard.Visibility = Visibility.Visible;
        UpdateTitleText.Text = $"新版本 {AppVersion.Display(release.Version)}（当前 {AppVersion.Display(CurrentVersion)}）";
        UpdateNotesText.Text = string.IsNullOrWhiteSpace(release.Notes) ? "该版本没有提供更新说明。" : Summarize(release.Notes);
        InstallUpdateButton.IsEnabled = release.PortablePackage is not null;
        if (release.PortablePackage is null)
            UpdateHintText.Text = "该版本没有可自动安装的便携包，请到项目主页手动下载。";
        else if (!_updateReadyToApply)
            InstallUpdateButton.Content = "下载并更新";
    }

    private static string Summarize(string notes)
    {
        var text = notes.Replace("\r\n", "\n").Trim();
        return text.Length <= 1200 ? text : text[..1200] + "…";
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        // 已经下载好了：这一下就是「立即重启更新」。
        if (_updateReadyToApply)
        {
            ApplyDownloadedUpdate();
            return;
        }

        if (_availableRelease?.PortablePackage is not { } package) return;
        InstallUpdateButton.IsEnabled = false;
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        _updateCancellation = new CancellationTokenSource();
        try
        {
            UpdateInstaller.CleanUp(UpdateInstaller.StagingRoot);
            var packagePath = UpdateInstaller.PackagePath(UpdateInstaller.StagingRoot);
            var progress = new Progress<UpdateDownloadProgress>(value =>
            {
                UpdateProgressBar.Value = value.Fraction * 100;
                UpdateHintText.Text = $"正在下载 {value.Describe()}";
            });
            await UpdateInstaller.DownloadAsync(package, packagePath, progress, _updateCancellation.Token);

            UpdateHintText.Text = "正在解压并校验…";
            var staging = UpdateInstaller.ExtractPackage(packagePath, UpdateInstaller.StagingDirectory);
            if (!UpdateInstaller.StagingLooksComplete(staging))
                throw new IOException("更新包内容不完整，已放弃这次更新。");

            // 记下来：即使这次不重启，下次退出时也会自动完成更新。
            _pendingUpdates.Save(new PendingUpdate(
                AppVersion.Display(_availableRelease.Version),
                staging,
                AppContext.BaseDirectory,
                DateTimeOffset.Now));

            _updateReadyToApply = true;
            UpdateProgressBar.Value = 100;
            InstallUpdateButton.Content = "立即重启更新";
            InstallUpdateButton.IsEnabled = true;
            UpdateHintText.Text = "已准备就绪。点这里重启完成更新，或稍后关闭软件时自动更新。";
            SaveHintText.Text = "新版本已下载，重启后生效";
        }
        catch (OperationCanceledException)
        {
            UpdateHintText.Text = "已取消下载。";
            InstallUpdateButton.IsEnabled = true;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            UpdateHintText.Text = "下载失败：" + exception.Message;
            InstallUpdateButton.IsEnabled = true;
        }
        finally
        {
            _updateCancellation?.Dispose();
            _updateCancellation = null;
        }
    }

    private void ApplyDownloadedUpdate()
    {
        if (_pendingUpdates.LoadReady() is not { } pending) return;
        try
        {
            UpdateExitHook.Apply(pending, _pendingUpdates);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            InkDialog.Show(this, "无法启动更新程序：" + exception.Message, "更新");
            return;
        }
        Application.Current.Shutdown();
    }

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
