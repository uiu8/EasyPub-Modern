using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class MainWindow : Window
{
    private LegacyConfigImport? _legacyConfig;
    private bool _useLegacyConfig = true;
    private int _coverPreviewVersion;
    private readonly FavoriteFolderStore _favoriteFolderStore = FavoriteFolderStore.CreateDefault();
    private readonly MetadataMappingStore _metadataMappingStore = MetadataMappingStore.CreateDefault();
    private readonly AppSettingsStore _appSettingsStore = AppSettingsStore.CreateDefault();
    private readonly ConversionHistoryStore _historyStore = ConversionHistoryStore.CreateDefault();
    private readonly EasyPubProjectStore _recoveryStore = EasyPubProjectStore.CreateRecoveryDefault();
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private IReadOnlyList<string> _lastFailedInputPaths = [];
    private IReadOnlyList<InputBookItem> _lastFailedBooks = [];
    private string? _customCss;
    private string? _customCssSourcePath;
    private TocHierarchyOptions _tocHierarchy = new();
    private bool _closeSaveInProgress;
    private bool _allowClose;
    private bool _recoverySaveInProgress;
    private string? _lastRecoveryFingerprint;
    private string? _lastExplicitSaveFingerprint;
    private string? _currentProjectPath;
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<FolderMetadataRule> _metadataMappings = [];

    public ObservableCollection<InputBookItem> InputBooks { get; } = [];
    public ObservableCollection<string> FavoriteFolders { get; } = [];
    public ObservableCollection<NamedConversionPreset> ConversionPresets { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        OutputDirectoryText.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EasyPub Modern");
        var bundledKindleGen = Path.Combine(AppContext.BaseDirectory, "bin", "kindlegen_v2.9.exe");
        var legacyKindleGen = @"C:\Users\13168\Desktop\easypub\bin\kindlegen_v2.9.exe";
        KindleGenText.Text = File.Exists(bundledKindleGen)
            ? bundledKindleGen
            : File.Exists(legacyKindleGen) ? legacyKindleGen : string.Empty;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        _recoveryTimer.Tick += RecoveryTimer_Tick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        try
        {
            ApplyFavoriteFolders(await _favoriteFolderStore.LoadAsync());
            _metadataMappings = await _metadataMappingStore.LoadAsync();
            UpdateMetadataMappingSummary();
            if (File.Exists(_appSettingsStore.StoragePath))
            {
                var settings = await _appSettingsStore.LoadAsync();
                RestoreLegacyConfigSelection(settings);
                ApplyAppSettings(settings);
            }
            else
            {
                LoadAutomaticLegacyConfig();
            }
            var history = await _historyStore.LoadAsync();
            var latestFailureTime = history.Where(entry => !entry.Succeeded).Select(entry => (DateTimeOffset?)entry.Timestamp).Max();
            _lastFailedInputPaths = latestFailureTime is null
                ? []
                : history
                    .Where(entry => !entry.Succeeded && entry.Timestamp == latestFailureTime)
                    .Select(entry => entry.InputPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            RetryFailedButton.IsEnabled = _lastFailedInputPaths.Count > 0;
            await OfferRecoveryAsync();
            _recoveryTimer.Start();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"收藏文件夹加载失败：{exception.Message}";
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _recoveryTimer.Stop();
        _operationCancellation?.Cancel();
        if (string.Equals(
                Environment.GetEnvironmentVariable("EASYPUB_DISABLE_SETTINGS_SAVE"),
                "1",
                StringComparison.Ordinal))
            return;

        if (_allowClose) return;
        e.Cancel = true;
        if (_closeSaveInProgress) return;
        _closeSaveInProgress = true;

        try
        {
            var settings = CaptureAppSettings();
            await _appSettingsStore.SaveAsync(settings);
            await SaveRecoveryIfChangedAsync();
        }
        catch
        {
            // Closing must remain reliable even if the local settings file is temporarily unavailable.
        }
        finally
        {
            _allowClose = true;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void LoadAutomaticLegacyConfig()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "config.xml"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "easypub", "config.xml"),
            Path.Combine(Environment.CurrentDirectory, "work", "easypub-compat", "legacy-capture", "config.xml"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            _legacyConfig = null;
            LegacyConfigStatusText.Text = "未找到 config.xml；可手动选择原版配置";
            LegacyConfigStatusText.ToolTip = null;
            UpdateLegacyConfigButtons();
            return;
        }
        LoadLegacyConfig(path, showError: false);
    }

    private void RestoreLegacyConfigSelection(EasyPubAppSettings settings)
    {
        if (!settings.UseLegacyConfig)
        {
            _legacyConfig = null;
            _useLegacyConfig = false;
            LegacyConfigStatusText.Text = "未选择配置；不会在下次启动时自动加载 config.xml";
            LegacyConfigStatusText.ToolTip = null;
            UpdateLegacyConfigButtons();
            return;
        }

        if (!string.IsNullOrWhiteSpace(settings.LegacyConfigPath) && File.Exists(settings.LegacyConfigPath))
            LoadLegacyConfig(settings.LegacyConfigPath, showError: false);
        else
            LoadAutomaticLegacyConfig();
    }

    private void BrowseLegacyConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "EasyPub 配置 (config.xml)|config.xml|XML 文件 (*.xml)|*.xml",
            CheckFileExists = true,
            Title = "选择原版 EasyPub config.xml",
        };
        if (dialog.ShowDialog(this) == true) LoadLegacyConfig(dialog.FileName, showError: true);
    }

    private async void ClearLegacyConfig_Click(object sender, RoutedEventArgs e)
    {
        _legacyConfig = null;
        _useLegacyConfig = false;
        LegacyConfigStatusText.Text = "已取消选择；当前界面中的设置保留，下次启动不会自动加载 config.xml";
        LegacyConfigStatusText.ToolTip = null;
        UpdateLegacyConfigButtons();
        try
        {
            await _appSettingsStore.SaveAsync(CaptureAppSettings());
            StatusText.Text = "已取消原版 config.xml；当前设置仍可继续使用";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法保存配置选择", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowLegacyConfigDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_legacyConfig is null)
        {
            MessageBox.Show(this, "尚未加载原版 config.xml。", "EasyPub Modern");
            return;
        }

        var message = $"配置来源：\n{_legacyConfig.SourcePath}\n\n已应用：\n- "
            + string.Join("\n- ", _legacyConfig.AppliedSettings)
            + "\n\n尚未应用（配置值已保留在原文件中）：\n- "
            + string.Join("\n- ", _legacyConfig.UnsupportedSettings);
        MessageBox.Show(this, message, "原版配置映射", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LoadLegacyConfig(string path, bool showError)
    {
        try
        {
            _legacyConfig = LegacyEasyPubConfig.Load(path);
            _useLegacyConfig = true;
            ApplyLegacyConfig(_legacyConfig);
            LegacyConfigStatusText.Text = $"已加载 {_legacyConfig.SourcePath} · 应用 {_legacyConfig.AppliedSettings.Count} 组，待实现 {_legacyConfig.UnsupportedSettings.Count} 组";
            LegacyConfigStatusText.ToolTip = _legacyConfig.SourcePath;
            UpdateLegacyConfigButtons();
        }
        catch (Exception exception)
        {
            _legacyConfig = null;
            LegacyConfigStatusText.Text = $"配置加载失败：{exception.Message}";
            LegacyConfigStatusText.ToolTip = null;
            UpdateLegacyConfigButtons();
            if (showError)
                MessageBox.Show(this, exception.Message, "无法加载原版配置", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateLegacyConfigButtons()
    {
        var hasConfig = _legacyConfig is not null;
        ClearLegacyConfigButton.IsEnabled = hasConfig;
        ShowLegacyConfigDetailsButton.IsEnabled = hasConfig;
    }

    private void ApplyLegacyConfig(LegacyConfigImport import)
    {
        var options = import.Options;
        if (!string.IsNullOrWhiteSpace(import.OutputDirectory)) OutputDirectoryText.Text = import.OutputDirectory;
        FormatCombo.SelectedIndex = import.OutputFormat == LegacyOutputFormat.Epub ? 0 : 1;
        ChapterRegexText.Text = options.ChapterPattern ?? string.Empty;
        _tocHierarchy = options.TocHierarchy ?? new TocHierarchyOptions();
        UpdateTocHierarchySummary();
        FontSizeText.Text = options.FontSizePercent.ToString(CultureInfo.InvariantCulture);
        LineHeightText.Text = options.LineHeightPercent.ToString(CultureInfo.InvariantCulture);
        ParagraphSpacingText.Text = options.ParagraphSpacingEm.ToString("0.###", CultureInfo.InvariantCulture);
        IndentText.Text = options.ParagraphIndentEm.ToString("0.###", CultureInfo.InvariantCulture);
        PageMarginTopText.Text = options.PageMarginTopPx.ToString(CultureInfo.InvariantCulture);
        PageMarginBottomText.Text = options.PageMarginBottomPx.ToString(CultureInfo.InvariantCulture);
        PageMarginLeftText.Text = options.PageMarginLeftPx.ToString(CultureInfo.InvariantCulture);
        PageMarginRightText.Text = options.PageMarginRightPx.ToString(CultureInfo.InvariantCulture);
        SelectComboItemByTag(AlignmentCombo, options.TextAlignment.ToString());
        KeepBlankLinesCheck.IsChecked = !options.RemoveBlankLines;
        FullWidthIndentCheck.IsChecked = options.AddFullWidthIndent;
        KindleGenText.Text = KindleGenPathPreference.ResolveForCurrentInstallation(
            options.Mobi.KindleGenPath,
            AppContext.BaseDirectory) ?? string.Empty;
        SelectComboItemByTag(CompressionCombo, ((int)options.Mobi.Compression).ToString(CultureInfo.InvariantCulture));
        StripSourceCheck.IsChecked = options.Mobi.StripSourceArchive;
        MobiSyncCheck.IsChecked = options.Mobi.EnableReadingProgressSync;
        MobiAsinText.Text = options.Mobi.Asin ?? string.Empty;
        KindleGenArgsText.Text = options.Mobi.ExtraArguments ?? string.Empty;
        SelectComboItemByTag(EpubModeCombo, options.Mobi.EpubInputMode.ToString());
        Topmost = import.AlwaysOnTop;
    }

    private void ApplyAppSettings(EasyPubAppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.OutputDirectory))
            OutputDirectoryText.Text = settings.OutputDirectory;
        ConversionPresets.Clear();
        foreach (var preset in settings.Presets.OrderBy(preset => preset.Name, StringComparer.CurrentCultureIgnoreCase))
            ConversionPresets.Add(preset);
        ApplyProfile(settings.LastProfile);
    }

    private void ApplyProfile(ConversionProfile profile)
    {
        FormatCombo.SelectedIndex = string.Equals(profile.OutputFormat, "mobi", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        AuthorText.Text = profile.Author ?? string.Empty;
        var metadata = profile.Options.Metadata ?? new PublicationMetadata();
        TranslatorText.Text = metadata.Translator ?? string.Empty;
        IsbnText.Text = metadata.Isbn ?? string.Empty;
        PublicationDatePicker.SelectedDate = metadata.PublicationDate?.ToDateTime(TimeOnly.MinValue);
        PublisherText.Text = metadata.Publisher ?? string.Empty;
        CategoryCombo.Text = metadata.Category ?? string.Empty;
        LanguageCombo.Text = string.IsNullOrWhiteSpace(metadata.Language) ? "zh-CN" : metadata.Language;
        DescriptionText.Text = metadata.Description ?? string.Empty;
        var parallelItem = ParallelismCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Content?.ToString() == profile.Parallelism.ToString(CultureInfo.InvariantCulture));
        if (parallelItem is not null) ParallelismCombo.SelectedItem = parallelItem;
        var options = profile.Options;
        _customCss = options.AdditionalCss;
        _customCssSourcePath = profile.AdditionalCssFilePath;
        if (string.IsNullOrWhiteSpace(_customCss) &&
            !string.IsNullOrWhiteSpace(_customCssSourcePath) &&
            File.Exists(_customCssSourcePath))
        {
            try { _customCss = File.ReadAllText(_customCssSourcePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        UpdateCssSummary();
        EmbedFontCheck.IsChecked = options.Font.Enabled;
        FontPathText.Text = options.Font.FontPath ?? string.Empty;
        FontFamilyText.Text = options.Font.FamilyName ?? string.Empty;
        SubsetFontCheck.IsChecked = options.Font.Subset;
        UpdateFontSummary();
        ChapterRegexText.Text = options.ChapterPattern ?? string.Empty;
        _tocHierarchy = options.TocHierarchy ?? new TocHierarchyOptions();
        UpdateTocHierarchySummary();
        SelectComboItemByTag(EncodingCombo, options.TextEncoding.ToString());
        FontSizeText.Text = options.FontSizePercent.ToString(CultureInfo.InvariantCulture);
        LineHeightText.Text = options.LineHeightPercent.ToString(CultureInfo.InvariantCulture);
        ParagraphSpacingText.Text = options.ParagraphSpacingEm.ToString("0.###", CultureInfo.InvariantCulture);
        IndentText.Text = options.ParagraphIndentEm.ToString("0.###", CultureInfo.InvariantCulture);
        PageMarginTopText.Text = options.PageMarginTopPx.ToString(CultureInfo.InvariantCulture);
        PageMarginBottomText.Text = options.PageMarginBottomPx.ToString(CultureInfo.InvariantCulture);
        PageMarginLeftText.Text = options.PageMarginLeftPx.ToString(CultureInfo.InvariantCulture);
        PageMarginRightText.Text = options.PageMarginRightPx.ToString(CultureInfo.InvariantCulture);
        SelectComboItemByTag(AlignmentCombo, options.TextAlignment.ToString());
        KeepBlankLinesCheck.IsChecked = !options.RemoveBlankLines;
        FullWidthIndentCheck.IsChecked = options.AddFullWidthIndent;
        KindleGenText.Text = KindleGenPathPreference.ResolveForCurrentInstallation(
            options.Mobi.KindleGenPath,
            AppContext.BaseDirectory) ?? string.Empty;
        SelectComboItemByTag(CompressionCombo, ((int)options.Mobi.Compression).ToString(CultureInfo.InvariantCulture));
        StripSourceCheck.IsChecked = options.Mobi.StripSourceArchive;
        MobiSyncCheck.IsChecked = options.Mobi.EnableReadingProgressSync;
        MobiAsinText.Text = options.Mobi.Asin ?? string.Empty;
        KindleGenArgsText.Text = options.Mobi.ExtraArguments ?? string.Empty;
        SelectComboItemByTag(EpubModeCombo, options.Mobi.EpubInputMode.ToString());
    }

    private ConversionProfile CaptureProfile()
    {
        var options = new ConversionOptions
        {
            ChapterPattern = EmptyToNull(ChapterRegexText.Text),
            TocHierarchy = _tocHierarchy,
            TextEncoding = Enum.Parse<TextEncodingMode>(((ComboBoxItem)EncodingCombo.SelectedItem).Tag.ToString()!),
            RemoveBlankLines = KeepBlankLinesCheck.IsChecked != true,
            AddFullWidthIndent = FullWidthIndentCheck.IsChecked == true,
            FontSizePercent = ParseInt(FontSizeText.Text, "字号"),
            LineHeightPercent = ParseInt(LineHeightText.Text, "行高"),
            ParagraphSpacingEm = ParseDouble(ParagraphSpacingText.Text, "段间距"),
            ParagraphIndentEm = ParseDouble(IndentText.Text, "首行缩进"),
            TextAlignment = Enum.Parse<EasyPub.Core.TextAlignment>(((ComboBoxItem)AlignmentCombo.SelectedItem).Tag.ToString()!),
            PageMarginTopPx = ParseNonNegativeInt(PageMarginTopText.Text, "上边距"),
            PageMarginBottomPx = ParseNonNegativeInt(PageMarginBottomText.Text, "下边距"),
            PageMarginLeftPx = ParseNonNegativeInt(PageMarginLeftText.Text, "左边距"),
            PageMarginRightPx = ParseNonNegativeInt(PageMarginRightText.Text, "右边距"),
            AdditionalCss = EmptyToNull(_customCss ?? string.Empty),
            Metadata = new PublicationMetadata
            {
                Translator = EmptyToNull(TranslatorText.Text),
                Isbn = EmptyToNull(IsbnText.Text),
                PublicationDate = PublicationDatePicker.SelectedDate is DateTime publicationDate
                    ? DateOnly.FromDateTime(publicationDate)
                    : null,
                Publisher = EmptyToNull(PublisherText.Text),
                Category = EmptyToNull(CategoryCombo.Text),
                Language = EmptyToNull(LanguageCombo.Text) ?? "zh-CN",
                Description = EmptyToNull(DescriptionText.Text),
            },
            Font = new EmbeddedFontOptions
            {
                Enabled = EmbedFontCheck.IsChecked == true,
                FontPath = EmptyToNull(FontPathText.Text),
                FamilyName = EmptyToNull(FontFamilyText.Text),
                Subset = SubsetFontCheck.IsChecked == true,
            },
            Mobi = new MobiOptions
            {
                KindleGenPath = EmptyToNull(KindleGenText.Text),
                Compression = (MobiCompression)int.Parse(((ComboBoxItem)CompressionCombo.SelectedItem).Tag.ToString()!, CultureInfo.InvariantCulture),
                StripSourceArchive = StripSourceCheck.IsChecked == true,
                EnableReadingProgressSync = MobiSyncCheck.IsChecked == true,
                Asin = EmptyToNull(MobiAsinText.Text),
                ExtraArguments = EmptyToNull(KindleGenArgsText.Text),
                EpubInputMode = Enum.Parse<EpubInputMode>(((ComboBoxItem)EpubModeCombo.SelectedItem).Tag.ToString()!),
            },
        };
        return new ConversionProfile(
            FormatCombo.SelectedIndex == 1 ? "mobi" : "epub",
            EmptyToNull(AuthorText.Text),
            int.Parse(((ComboBoxItem)ParallelismCombo.SelectedItem).Content.ToString()!, CultureInfo.InvariantCulture),
            EmptyToNull(_customCssSourcePath ?? string.Empty),
            options);
    }

    private EasyPubAppSettings CaptureAppSettings() => new(
        EmptyToNull(OutputDirectoryText.Text),
        CaptureProfile(),
        ConversionPresets.ToArray())
    {
        UseLegacyConfig = _useLegacyConfig,
        LegacyConfigPath = _useLegacyConfig ? _legacyConfig?.SourcePath : null,
    };

    private void ApplyPreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not NamedConversionPreset preset)
        {
            MessageBox.Show(this, "请先从下拉框选择一个预设。", "EasyPub Modern");
            return;
        }
        ApplyProfile(preset.Profile);
        PresetNameText.Text = preset.Name;
        StatusText.Text = $"已应用预设：{preset.Name}";
    }

    private async void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        var name = PresetNameText.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "请先输入预设名称。", "EasyPub Modern");
            return;
        }
        try
        {
            var preset = new NamedConversionPreset(name, CaptureProfile());
            var existing = ConversionPresets.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (existing is not null) ConversionPresets.Remove(existing);
            ConversionPresets.Add(preset);
            PresetCombo.SelectedItem = preset;
            await _appSettingsStore.SaveAsync(CaptureAppSettings());
            StatusText.Text = $"已保存转换预设：{name}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法保存预设", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (PresetCombo.SelectedItem is not NamedConversionPreset preset) return;
        ConversionPresets.Remove(preset);
        PresetNameText.Clear();
        await _appSettingsStore.SaveAsync(CaptureAppSettings());
        StatusText.Text = $"已删除预设：{preset.Name}";
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCanReplaceProjectAsync()) return;
        InputBooks.Clear();
        _currentProjectPath = null;
        _lastExplicitSaveFingerprint = null;
        ApplyProfile(ConversionProfile.Default);
        OutputDirectoryText.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "EasyPub Modern");
        _recoveryStore.Delete();
        _lastRecoveryFingerprint = null;
        UpdateProjectTitle();
        UpdateStatus();
        StatusText.Text = "已新建空白项目";
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!await EnsureCanReplaceProjectAsync()) return;
        var dialog = new OpenFileDialog
        {
            Title = "打开 EasyPub 项目",
            Filter = "EasyPub 项目 (*.easypubproj)|*.easypubproj",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var document = await new EasyPubProjectStore(dialog.FileName).LoadAsync();
            ApplyProjectDocument(document);
            _currentProjectPath = Path.GetFullPath(dialog.FileName);
            _lastExplicitSaveFingerprint = EasyPubProjectStore.Fingerprint(CaptureProjectDocument());
            _lastRecoveryFingerprint = _lastExplicitSaveFingerprint;
            _recoveryStore.Delete();
            UpdateProjectTitle();
            StatusText.Text = $"已打开项目：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开项目", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e) =>
        await SaveProjectInteractiveAsync(saveAs: false);

    private async void SaveProjectAs_Click(object sender, RoutedEventArgs e) =>
        await SaveProjectInteractiveAsync(saveAs: true);

    private async Task<bool> SaveProjectInteractiveAsync(bool saveAs)
    {
        var target = saveAs ? null : _currentProjectPath;
        if (target is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "保存 EasyPub 项目",
                Filter = "EasyPub 项目 (*.easypubproj)|*.easypubproj",
                DefaultExt = ".easypubproj",
                AddExtension = true,
                FileName = InputBooks.Count == 1 ? InputBooks[0].DisplayName : "EasyPub项目",
            };
            if (dialog.ShowDialog(this) != true) return false;
            target = dialog.FileName;
        }

        try
        {
            _currentProjectPath = Path.GetFullPath(target);
            var document = CaptureProjectDocument() with
            {
                ProjectPathHint = _currentProjectPath,
                UpdatedAt = DateTimeOffset.Now,
            };
            await new EasyPubProjectStore(_currentProjectPath).SaveAsync(document);
            _lastExplicitSaveFingerprint = EasyPubProjectStore.Fingerprint(document);
            _lastRecoveryFingerprint = _lastExplicitSaveFingerprint;
            _recoveryStore.Delete();
            UpdateProjectTitle();
            StatusText.Text = $"项目已保存：{Path.GetFileName(_currentProjectPath)}";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法保存项目", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private async Task<bool> EnsureCanReplaceProjectAsync()
    {
        EasyPubProjectDocument current;
        try { current = CaptureProjectDocument(); }
        catch { return true; }
        var fingerprint = EasyPubProjectStore.Fingerprint(current);
        if (fingerprint == _lastExplicitSaveFingerprint || InputBooks.Count == 0) return true;
        var answer = MessageBox.Show(
            this,
            "当前项目有尚未保存的修改。\n\n是：先保存\n否：放弃修改\n取消：留在当前项目",
            "EasyPub Modern",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        return answer != MessageBoxResult.Yes || await SaveProjectInteractiveAsync(saveAs: false);
    }

    private EasyPubProjectDocument CaptureProjectDocument() => new(
        EasyPubProjectDocument.CurrentSchemaVersion,
        _currentProjectPath,
        EmptyToNull(OutputDirectoryText.Text),
        CaptureProfile(),
        InputBooks.Select(book => new EasyPubProjectBook(
            book.InputPath,
            book.Title,
            book.Author,
            book.CoverImagePath,
            book.Illustrations)
        {
            MetadataOverrides = book.MetadataOverrides,
            MetadataRuleFolder = book.MetadataRuleFolder,
            ChapterTree = book.ChapterTree,
        }).ToArray(),
        DateTimeOffset.Now);

    private void ApplyProjectDocument(EasyPubProjectDocument document)
    {
        ApplyProfile(document.Profile);
        if (!string.IsNullOrWhiteSpace(document.OutputDirectory))
            OutputDirectoryText.Text = document.OutputDirectory;
        InputBooks.Clear();
        foreach (var saved in document.Books)
        {
            var item = new InputBookItem(saved.InputPath)
            {
                Title = saved.Title,
                Author = saved.Author,
                CoverImagePath = saved.CoverImagePath,
            };
            item.SetIllustrations(saved.Illustrations);
            item.SetMetadataOverrides(saved.MetadataOverrides, saved.MetadataRuleFolder);
            item.SetChapterTree(saved.ChapterTree);
            InputBooks.Add(item);
        }
        FilesList.SelectedItem = InputBooks.FirstOrDefault();
        UpdateStatus();
    }

    private async void RecoveryTimer_Tick(object? sender, EventArgs e) =>
        await SaveRecoveryIfChangedAsync();

    private async Task SaveRecoveryIfChangedAsync()
    {
        if (_recoverySaveInProgress || InputBooks.Count == 0) return;
        EasyPubProjectDocument snapshot;
        try { snapshot = CaptureProjectDocument(); }
        catch { return; }
        var fingerprint = EasyPubProjectStore.Fingerprint(snapshot);
        if (fingerprint == _lastRecoveryFingerprint) return;
        _recoverySaveInProgress = true;
        try
        {
            await _recoveryStore.SaveAsync(snapshot);
            _lastRecoveryFingerprint = fingerprint;
        }
        finally
        {
            _recoverySaveInProgress = false;
        }
    }

    private async Task OfferRecoveryAsync()
    {
        if (!File.Exists(_recoveryStore.StoragePath)) return;
        try
        {
            var recovery = await _recoveryStore.LoadAsync();
            if (recovery.Books.Count == 0) { _recoveryStore.Delete(); return; }
            var answer = MessageBox.Show(
                this,
                $"发现自动恢复内容（{recovery.Books.Count} 本书，保存于 {recovery.UpdatedAt.LocalDateTime:g}）。是否恢复？",
                "恢复上次工作",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes)
            {
                ApplyProjectDocument(recovery);
                _currentProjectPath = recovery.ProjectPathHint;
                _lastRecoveryFingerprint = EasyPubProjectStore.Fingerprint(recovery);
                UpdateProjectTitle();
                StatusText.Text = $"已恢复上次工作：{recovery.Books.Count} 本小说";
            }
            else
            {
                _recoveryStore.Delete();
            }
        }
        catch
        {
            _recoveryStore.Delete();
        }
    }

    private void UpdateProjectTitle() => Title = _currentProjectPath is null
        ? "EasyPub Modern v0.15.0"
        : $"{Path.GetFileNameWithoutExtension(_currentProjectPath)} · EasyPub Modern v0.15.0";

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "支持的书稿 (*.txt;*.epub)|*.txt;*.epub|文本文件 (*.txt)|*.txt|EPUB 电子书 (*.epub)|*.epub",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    private void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择要递归导入的小说文件夹",
            InitialDirectory = FavoriteFolderCombo.SelectedItem as string
                ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var files = Directory.EnumerateFiles(
                    dialog.FolderName,
                    "*",
                    new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true,
                        MatchCasing = MatchCasing.CaseInsensitive,
                    })
                .Where(IsSupportedInput)
                .ToArray();
            var before = InputBooks.Count;
            AddFiles(files);
            StatusText.Text = files.Length == 0
                ? $"文件夹中没有找到 TXT 或 EPUB：{dialog.FolderName}"
                : $"已从文件夹及子目录导入 {InputBooks.Count - before} 本，跳过 {files.Length - (InputBooks.Count - before)} 个重复项";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法导入文件夹", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (InputBooks.Count == 0)
        {
            MessageBox.Show(this, "请先添加至少一本小说。", "EasyPub Modern");
            return;
        }
        var editor = new BatchMetadataWindow(InputBooks.ToArray()) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            UpdateMetadataMappingSummary();
            StatusText.Text = $"已保存 {InputBooks.Count} 本小说的逐书书籍信息";
        }
    }

    private async void EditMetadataMappings_Click(object sender, RoutedEventArgs e)
    {
        var editor = new MetadataMappingWindow(_metadataMappings) { Owner = this };
        if (editor.ShowDialog() != true) return;

        try
        {
            await _metadataMappingStore.SaveAsync(editor.Rules);
            _metadataMappings = await _metadataMappingStore.LoadAsync();
            foreach (var book in InputBooks) ApplyMetadataMapping(book);
            UpdateMetadataMappingSummary();
            UpdateStatus();
            StatusText.Text = $"已保存 {_metadataMappings.Count} 条文件夹元数据映射，并重新匹配当前书稿";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法保存元数据映射", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void AddFavoriteFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = FavoriteFolderCombo.SelectedItem as string;
        var dialog = new OpenFolderDialog
        {
            Title = "选择要收藏的小说文件夹",
            InitialDirectory = selected is not null && Directory.Exists(selected)
                ? selected
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var folders = await _favoriteFolderStore.AddAsync(dialog.FolderName);
            ApplyFavoriteFolders(folders, dialog.FolderName);
            StatusText.Text = $"已收藏文件夹：{dialog.FolderName}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法添加收藏", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void RemoveFavoriteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteFolderCombo.SelectedItem is not string selected) return;

        try
        {
            var folders = await _favoriteFolderStore.RemoveAsync(selected);
            ApplyFavoriteFolders(folders);
            StatusText.Text = $"已移除收藏（原文件夹及文件未删除）：{selected}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法移除收藏", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddFromFavoriteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteFolderCombo.SelectedItem is not string selected) return;
        if (!Directory.Exists(selected))
        {
            MessageBox.Show(
                this,
                $"收藏文件夹当前不可用，可能已移动或所在磁盘未连接：\n{selected}",
                "找不到收藏文件夹",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"从 {Path.GetFileName(selected)} 添加 TXT / EPUB",
            Filter = "支持的书稿 (*.txt;*.epub)|*.txt;*.epub|文本文件 (*.txt)|*.txt|EPUB 电子书 (*.epub)|*.epub",
            InitialDirectory = selected,
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    private void FavoriteFolderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = FavoriteFolderCombo.SelectedItem is string;
        AddFromFavoriteButton.IsEnabled = hasSelection;
        RemoveFavoriteButton.IsEnabled = hasSelection;
    }

    private void ApplyFavoriteFolders(IReadOnlyList<string> folders, string? selectedPath = null)
    {
        FavoriteFolders.Clear();
        foreach (var folder in folders) FavoriteFolders.Add(folder);

        FavoriteFolderCombo.SelectedItem = selectedPath is null
            ? FavoriteFolders.FirstOrDefault()
            : FavoriteFolders.FirstOrDefault(folder =>
                string.Equals(folder, Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedPath)), StringComparison.OrdinalIgnoreCase));
    }

    private void EditTocHierarchy_Click(object sender, RoutedEventArgs e)
    {
        var editor = new TocHierarchyWindow(_tocHierarchy) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;
        _tocHierarchy = editor.Result;
        UpdateTocHierarchySummary();
        StatusText.Text = _tocHierarchy.Enabled
            ? "已启用三级层级目录；转换时会生成嵌套目录"
            : "已关闭层级目录；章节将使用原版单层目录";
    }

    private void UpdateTocHierarchySummary()
    {
        if (TocHierarchyStatusText is null) return;
        TocHierarchyStatusText.Text = _tocHierarchy.Enabled ? "层级目录：一级 / 二级 / 三级" : "层级目录：关闭";
        TocHierarchyStatusText.Foreground = _tocHierarchy.Enabled
            ? System.Windows.Media.Brushes.SeaGreen
            : System.Windows.Media.Brushes.SlateGray;
    }

    private async void EditChapters_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesList.SelectedItems.Cast<InputBookItem>().ToArray();
        var book = selected.Length == 1
            ? selected[0]
            : InputBooks.Count == 1
                ? InputBooks[0]
                : null;
        if (book is null)
        {
            MessageBox.Show(this, "请在待转换文件中只选中一个 TXT，再打开章节编辑器。", "EasyPub Modern");
            return;
        }
        if (!string.Equals(Path.GetExtension(book.InputPath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "EPUB 会直接读取原书目录；章节树编辑目前用于 TXT 书稿。", "EasyPub Modern");
            return;
        }

        try
        {
            var inputPath = book.InputPath;
            StatusText.Text = $"正在分析章节：{Path.GetFileName(inputPath)}";
            var encoding = Enum.Parse<TextEncodingMode>(
                ((ComboBoxItem)EncodingCombo.SelectedItem).Tag.ToString()!);
            var chapterPattern = EmptyToNull(ChapterRegexText.Text);
            ChapterTreeDocument document;
            try
            {
                document = await Task.Run(() => ChapterTreeDocument.LoadAsync(
                    inputPath,
                    chapterPattern,
                    _tocHierarchy,
                    encoding,
                    book.ChapterTree));
            }
            catch (InvalidDataException exception) when (book.ChapterTree is not null)
            {
                var rebuild = MessageBox.Show(
                    this,
                    exception.Message + "\n\n是否丢弃旧章节树并按当前 TXT 重新识别？",
                    "章节树已失效",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (rebuild != MessageBoxResult.Yes) return;
                document = await Task.Run(() => ChapterTreeDocument.LoadAsync(
                    inputPath,
                    chapterPattern,
                    _tocHierarchy,
                    encoding));
            }
            var editor = new ChapterEditorWindow(document) { Owner = this };
            if (editor.ShowDialog() == true && editor.ResultPlan is not null)
            {
                book.SetChapterTree(editor.ResultPlan);
                StatusText.Text = $"已保存《{book.DisplayName}》的章节树，共 {editor.ResultPlan.Entries.Count} 章";
            }
            else
            {
                UpdateStatus();
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "章节分析失败";
            MessageBox.Show(this, exception.Message, "无法打开章节编辑器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FilesList.SelectedItems.Cast<InputBookItem>().ToArray())
            InputBooks.Remove(item);
        UpdateStatus();
    }

    private void SelectAllFiles_Click(object sender, RoutedEventArgs e)
    {
        if (InputBooks.Count == 0) return;
        FilesList.SelectAll();
        StatusText.Text = $"已选中全部 {InputBooks.Count} 本小说";
    }

    private void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        InputBooks.Clear();
        UpdateStatus();
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择电子书输出目录",
            InitialDirectory = OutputDirectoryText.Text,
        };
        if (dialog.ShowDialog(this) == true) OutputDirectoryText.Text = dialog.FolderName;
    }

    private void EditCss_Click(object sender, RoutedEventArgs e)
    {
        var editor = new CustomCssWindow(_customCss, _customCssSourcePath) { Owner = this };
        if (editor.ShowDialog() != true) return;
        _customCss = EmptyToNull(editor.CssText);
        _customCssSourcePath = _customCss is null ? null : editor.SourcePath;
        UpdateCssSummary();
        StatusText.Text = _customCss is null ? "已清除定制 CSS" : "已应用定制 CSS";
    }

    private void UpdateCssSummary()
    {
        if (CssSummaryText is null) return;
        CssSummaryText.Text = string.IsNullOrWhiteSpace(_customCss)
            ? "未设置"
            : _customCssSourcePath is null
                ? $"已设置 · {_customCss.Length} 字符"
                : $"已导入 · {Path.GetFileName(_customCssSourcePath)}";
        CssSummaryText.ToolTip = _customCssSourcePath ?? _customCss ?? "直接编辑 CSS，或从 .css 文件导入";
    }

    private void BrowseFont_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择允许嵌入电子书的 TrueType 字体",
            Filter = "TrueType 字体 (*.ttf)|*.ttf",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        FontPathText.Text = dialog.FileName;
        EmbedFontCheck.IsChecked = true;
        if (string.IsNullOrWhiteSpace(FontFamilyText.Text))
            FontFamilyText.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
        UpdateFontSummary();
    }

    private void ClearFont_Click(object sender, RoutedEventArgs e)
    {
        EmbedFontCheck.IsChecked = false;
        FontPathText.Clear();
        FontFamilyText.Clear();
        UpdateFontSummary();
    }

    private void UpdateFontSummary()
    {
        if (FontStatusText is null) return;
        if (string.IsNullOrWhiteSpace(FontPathText.Text))
        {
            FontStatusText.Text = "未选择字体。会在转换前检查字体许可与格式。";
            return;
        }
        try
        {
            var info = FontEmbeddingService.Inspect(FontPathText.Text);
            FontStatusText.Text = !info.CanEmbed
                ? "该字体许可禁止嵌入，转换前检查会阻止输出。"
                : SubsetFontCheck.IsChecked == true && !info.CanSubset
                    ? "字体允许嵌入但禁止子集化，将完整嵌入。"
                    : "字体允许嵌入；将按书稿实际用字生成子集。";
        }
        catch (Exception exception)
        {
            FontStatusText.Text = $"字体不可用：{exception.Message}";
        }
    }

    private void EditIllustrations_Click(object sender, RoutedEventArgs e)
    {
        var book = SelectedCoverBook();
        if (book is null)
        {
            MessageBox.Show(this, "请只选中一本小说，再管理它的正文插图。", "EasyPub Modern");
            return;
        }
        if (book.IsEpub)
        {
            MessageBox.Show(this, "EPUB 内已有的正文图片会自动处理；手动插图位置编辑目前用于 TXT 书稿。", "EasyPub Modern");
            return;
        }

        var encoding = Enum.Parse<TextEncodingMode>(
            ((ComboBoxItem)EncodingCombo.SelectedItem).Tag.ToString()!);
        var editor = new IllustrationManagerWindow(
            book.DisplayName,
            book.InputPath,
            EmptyToNull(ChapterRegexText.Text),
            encoding,
            book.Illustrations) { Owner = this };
        if (editor.ShowDialog() != true) return;
        book.SetIllustrations(editor.Result);
        UpdateStatus();
        StatusText.Text = $"已为《{book.DisplayName}》保存 {book.Illustrations.Count} 张正文插图";
    }

    private async void BrowseCover_Click(object sender, RoutedEventArgs e)
    {
        var book = SelectedCoverBook();
        if (book is null)
        {
            MessageBox.Show(this, "请只选中一本小说，再为它选择封面。", "EasyPub Modern");
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "选择电子书封面",
            Filter = "支持的图片 (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp|JPEG (*.jpg;*.jpeg)|*.jpg;*.jpeg|PNG (*.png)|*.png|WebP (*.webp)|*.webp",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true) await AssignCoverAsync(book, dialog.FileName);
    }

    private async void ClearCover_Click(object sender, RoutedEventArgs e)
    {
        var book = SelectedCoverBook();
        if (book is null) return;
        book.CoverImagePath = null;
        StatusText.Text = $"已清除《{Path.GetFileNameWithoutExtension(book.InputPath)}》的封面";
        await RefreshCoverPreviewAsync();
    }

    private async void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateMetadataMappingSummary();
        await RefreshCoverPreviewAsync();
    }

    private void CoverDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = SelectedCoverBook() is not null && TryGetSingleCoverPath(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void CoverDrop_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        var book = SelectedCoverBook();
        if (book is null || !TryGetSingleCoverPath(e.Data, out var path))
        {
            MessageBox.Show(this, "请只选中一本小说，并拖入一个 JPG、PNG 或 WebP 图片。", "EasyPub Modern");
            return;
        }
        await AssignCoverAsync(book, path);
    }

    private async Task AssignCoverAsync(InputBookItem book, string coverPath)
    {
        try
        {
            CoverPlaceholderText.Visibility = Visibility.Visible;
            CoverPlaceholderText.Text = "正在读取封面…";
            var prepared = await CoverImageConverter.PrepareJpegAsync(coverPath);
            book.CoverImagePath = Path.GetFullPath(coverPath);
            StatusText.Text = $"已为《{Path.GetFileNameWithoutExtension(book.InputPath)}》设置封面：{Path.GetFileName(coverPath)}";
            if (ReferenceEquals(SelectedCoverBook(), book))
                ShowCoverPreview(book, prepared);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法读取封面", MessageBoxButton.OK, MessageBoxImage.Error);
            await RefreshCoverPreviewAsync();
        }
    }

    private async Task RefreshCoverPreviewAsync()
    {
        var version = ++_coverPreviewVersion;
        var book = SelectedCoverBook();
        BrowseCoverButton.IsEnabled = book is not null;
        ClearCoverButton.IsEnabled = book?.CoverImagePath is not null;
        CoverPreviewImage.Source = null;
        CoverPreviewImage.ToolTip = null;
        CoverPlaceholderText.Visibility = Visibility.Visible;

        if (book is null)
        {
            if (SelectedBookOptionsText is not null)
                SelectedBookOptionsText.Text = FilesList.SelectedItems.Count > 1
                    ? $"当前选中 {FilesList.SelectedItems.Count} 本；插图设置需要单独选择一本"
                    : "请先在上方选择一本小说";
            CoverPlaceholderText.Text = FilesList.SelectedItems.Count > 1
                ? "请只选择一本小说"
                : "选择一本小说后，将图片拖到这里";
            return;
        }
        if (SelectedBookOptionsText is not null)
            SelectedBookOptionsText.Text = $"当前小说：《{book.DisplayName}》· {book.Illustrations.Count} 张正文插图";
        if (string.IsNullOrWhiteSpace(book.CoverImagePath))
        {
            CoverPlaceholderText.Text = "拖入 JPG、PNG、WebP\n或点击“选择封面”";
            return;
        }

        CoverPlaceholderText.Text = "正在加载预览…";
        try
        {
            var prepared = await CoverImageConverter.PrepareJpegAsync(book.CoverImagePath);
            if (version != _coverPreviewVersion || !ReferenceEquals(SelectedCoverBook(), book)) return;
            ShowCoverPreview(book, prepared);
        }
        catch (Exception exception)
        {
            if (version != _coverPreviewVersion) return;
            CoverPlaceholderText.Text = $"封面读取失败\n{exception.Message}";
        }
    }

    private void ShowCoverPreview(InputBookItem book, PreparedCoverImage prepared)
    {
        using var stream = new MemoryStream(prepared.JpegBytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        CoverPreviewImage.Source = bitmap;
        CoverPreviewImage.ToolTip = $"{book.CoverImagePath}\n{prepared.PixelWidth} × {prepared.PixelHeight} · {prepared.SourceFormat}"
            + (prepared.WasConverted ? " → JPG" : string.Empty);
        CoverPlaceholderText.Visibility = Visibility.Collapsed;
        ClearCoverButton.IsEnabled = true;
    }

    private InputBookItem? SelectedCoverBook() =>
        FilesList.SelectedItems.Count == 1 ? FilesList.SelectedItem as InputBookItem : null;

    private static bool TryGetSingleCoverPath(IDataObject data, out string path)
    {
        path = string.Empty;
        if (data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } paths) return false;
        var extension = Path.GetExtension(paths[0]);
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp")
            && extension.ToLowerInvariant() is not (".jpg" or ".jpeg" or ".png" or ".webp"))
            return false;
        path = paths[0];
        return File.Exists(path);
    }

    private void BrowseKindleGen_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "KindleGen (kindlegen*.exe)|kindlegen*.exe|程序 (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) KindleGenText.Text = dialog.FileName;
    }

    private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StatusText is not null && FormatCombo.SelectedIndex == 1 && InputBooks.Count == 0)
            StatusText.Text = "已选择 MOBI；KindleGen 设置位于“MOBI 选项”分页";
        if (StatusText is not null && FormatCombo?.SelectedIndex == 0 && InputBooks.Any(book => book.IsEpub))
            StatusText.Text = "当前含 EPUB 输入；EPUB 输入只能转换为 MOBI";
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] paths
                    && paths.Any(IsSupportedInput)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddFiles(paths);
    }

    private async void PreviewBook_Click(object sender, RoutedEventArgs e)
    {
        var book = SelectedCoverBook();
        if (book is null)
        {
            MessageBox.Show(this, "请只选中一本小说，再打开整书预览。", "EasyPub Modern");
            return;
        }
        if (book.IsEpub)
        {
            MessageBox.Show(this, "EPUB 输入可直接转换为 MOBI；当前整书预览用于 TXT 重排结果。", "EasyPub Modern");
            return;
        }

        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        ConvertButton.IsEnabled = false;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        try
        {
            var requests = await BuildConversionRequestsAsync();
            var request = requests.First(item =>
                string.Equals(item.InputPath, book.InputPath, StringComparison.OrdinalIgnoreCase));
            var previewProgress = new Progress<ConversionProgress>(value =>
            {
                Progress.Value = value.Fraction;
                StatusText.Text = $"预览：{value.Stage}";
            });
            var package = await Task.Run(() => new BookPreviewService().BuildAsync(
                request,
                previewProgress,
                _operationCancellation.Token), _operationCancellation.Token);
            Progress.Value = 1;
            StatusText.Text = $"整书预览已生成：《{book.DisplayName}》";
            new BookPreviewWindow(book.Title ?? book.DisplayName, package) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消生成预览";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法生成整书预览", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CancelButton.IsEnabled = false;
            ConvertButton.IsEnabled = InputBooks.Count > 0;
            Progress.Value = 0;
        }
    }

    private async void KindlePreviewBook_Click(object sender, RoutedEventArgs e)
    {
        var book = SelectedCoverBook();
        if (book is null)
        {
            MessageBox.Show(this, "请只选中一本小说，再打开 Kindle 预览。", "EasyPub Modern");
            return;
        }

        var launcher = new KindlePreviewerLauncher();
        var installation = launcher.Discover();
        if (installation is null)
        {
            var install = MessageBox.Show(
                this,
                "没有检测到官方 Kindle Previewer。是否打开 Amazon 官方下载页？\n\n你仍可使用“整书预览”检查 EPUB 排版。",
                "需要 Kindle Previewer",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (install == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(KindlePreviewerLauncher.OfficialDownloadPage) { UseShellExecute = true });
            return;
        }

        if (book.IsEpub)
        {
            try
            {
                launcher.Launch(book.InputPath, book.Title ?? book.DisplayName, installation);
                StatusText.Text = $"已在 Kindle Previewer 中打开：《{book.DisplayName}》";
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "无法打开 Kindle 预览", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return;
        }

        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        ConvertButton.IsEnabled = false;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;
        try
        {
            var requests = await BuildConversionRequestsAsync();
            var request = requests.First(item =>
                string.Equals(item.InputPath, book.InputPath, StringComparison.OrdinalIgnoreCase));
            var previewProgress = new Progress<ConversionProgress>(value =>
            {
                Progress.Value = value.Fraction;
                StatusText.Text = $"Kindle 预览：{value.Stage}";
            });
            using var package = await Task.Run(() => new BookPreviewService().BuildAsync(
                request,
                previewProgress,
                _operationCancellation.Token), _operationCancellation.Token);
            launcher.Launch(package.EpubPath, book.Title ?? book.DisplayName, installation);
            Progress.Value = 1;
            StatusText.Text = $"已在 Kindle Previewer 中打开：《{book.DisplayName}》";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消 Kindle 预览";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法打开 Kindle 预览", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CancelButton.IsEnabled = false;
            ConvertButton.IsEnabled = InputBooks.Count > 0;
            Progress.Value = 0;
        }
    }

    private async void RunPreflight_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        try
        {
            StatusText.Text = "正在执行转换前检查…";
            Progress.IsIndeterminate = true;
            var requests = await BuildConversionRequestsAsync();
            var report = await Task.Run(
                () => new ConversionPreflightInspector().InspectAsync(requests, _operationCancellation.Token),
                _operationCancellation.Token);
            new PreflightWindow(report, allowContinue: false, NavigateToPreflightIssue) { Owner = this }.ShowDialog();
            StatusText.Text = report.HasErrors
                ? $"检查完成：发现 {report.Issues.Count(issue => issue.Severity == PreflightSeverity.Error)} 个错误"
                : $"检查完成：{report.Books.Count} 本可转换，{report.WarningCount} 个提醒";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消转换前检查";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法执行转换前检查", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Progress.IsIndeterminate = false;
            CancelButton.IsEnabled = false;
        }
    }

    private void NavigateToPreflightIssue(ConversionPreflightIssue issue)
    {
        var book = issue.InputPath is null ? null : InputBooks.FirstOrDefault(item =>
            string.Equals(item.InputPath, issue.InputPath, StringComparison.OrdinalIgnoreCase));
        if (book is not null)
        {
            FilesList.SelectedItems.Clear();
            FilesList.SelectedItem = book;
            FilesList.ScrollIntoView(book);
        }

        switch (issue.Target)
        {
            case PreflightTargetKind.Chapters:
                OptionsTabs.SelectedIndex = 0;
                EditChapters_Click(this, new RoutedEventArgs());
                break;
            case PreflightTargetKind.Output:
                OutputDirectoryText.Focus();
                break;
            case PreflightTargetKind.Cover:
                BrowseCoverButton.Focus();
                break;
            case PreflightTargetKind.Illustrations when book is not null:
                OptionsTabs.SelectedIndex = 5;
                var encoding = Enum.Parse<TextEncodingMode>(
                    ((ComboBoxItem)EncodingCombo.SelectedItem).Tag.ToString()!);
                var editor = new IllustrationManagerWindow(
                    book.DisplayName,
                    book.InputPath,
                    EmptyToNull(ChapterRegexText.Text),
                    encoding,
                    book.Illustrations,
                    issue.RelatedValue) { Owner = this };
                if (editor.ShowDialog() == true) book.SetIllustrations(editor.Result);
                break;
            case PreflightTargetKind.Mobi:
                OptionsTabs.SelectedIndex = 6;
                KindleGenText.Focus();
                break;
            case PreflightTargetKind.Font:
                OptionsTabs.SelectedIndex = 2;
                FontPathText.Focus();
                break;
            case PreflightTargetKind.BookInformation:
                OptionsTabs.SelectedIndex = 3;
                IsbnText.Focus();
                break;
            default:
                FilesList.Focus();
                break;
        }
        StatusText.Text = $"已定位：{issue.Message}";
    }

    private async void ShowHistory_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var entries = await _historyStore.LoadAsync();
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "还没有转换历史。", "EasyPub Modern");
                return;
            }
            var window = new ConversionHistoryWindow(entries) { Owner = this };
            if (window.ShowDialog() == true) LoadRetryInputs(window.RetryInputPaths);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "无法加载转换历史", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFailedBooks.Count > 0)
        {
            InputBooks.Clear();
            foreach (var book in _lastFailedBooks) InputBooks.Add(book.Clone());
            FilesList.SelectedItem = InputBooks.FirstOrDefault();
            UpdateStatus();
            StatusText.Text = $"已载入 {InputBooks.Count} 个失败或取消项目，并保留逐书封面、插图与元数据";
            return;
        }
        LoadRetryInputs(_lastFailedInputPaths);
    }

    private void LoadRetryInputs(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        InputBooks.Clear();
        AddFiles(paths);
        StatusText.Text = $"已载入 {InputBooks.Count} 个失败项目；请检查设置后重新转换";
    }

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        var cancellationToken = _operationCancellation.Token;
        ConvertButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;

        try
        {
            StatusText.Text = "正在执行转换前检查…";
            var requests = await BuildConversionRequestsAsync();
            var report = await Task.Run(
                () => new ConversionPreflightInspector().InspectAsync(requests, cancellationToken),
                cancellationToken);
            if (report.HasErrors)
            {
                new PreflightWindow(report, allowContinue: false, NavigateToPreflightIssue) { Owner = this }.ShowDialog();
                StatusText.Text = "转换已停止：请先修正检查错误";
                return;
            }
            if (report.WarningCount > 0
                && new PreflightWindow(report, allowContinue: true, NavigateToPreflightIssue) { Owner = this }.ShowDialog() != true)
            {
                StatusText.Text = "已取消转换";
                return;
            }

            Directory.CreateDirectory(OutputDirectoryText.Text.Trim());
            StatusText.Text = $"正在转换 {requests.Count} 本小说…";
            var parallelism = int.Parse(
                ((ComboBoxItem)ParallelismCombo.SelectedItem).Content.ToString()!,
                CultureInfo.InvariantCulture);
            var batchProgress = new Progress<BatchConversionProgress>(value =>
            {
                Progress.Value = value.Fraction;
                var name = string.IsNullOrWhiteSpace(value.CurrentInputPath)
                    ? string.Empty
                    : $" · {Path.GetFileName(value.CurrentInputPath)}";
                StatusText.Text = $"{value.Stage}{name} · 已完成 {value.CompletedCount}/{value.TotalCount} · 失败 {value.FailedCount} · 取消 {value.CancelledCount}";
            });
            var outcomes = await new BatchConverter(new EasyPubConverter())
                .ConvertWithReportAsync(requests, parallelism, batchProgress, cancellationToken);
            var timestamp = DateTimeOffset.Now;
            var historyEntries = outcomes.Select(outcome => new ConversionHistoryEntry(
                Guid.NewGuid(),
                timestamp,
                outcome.Request.InputPath,
                outcome.Request.OutputPath,
                outcome.Succeeded,
                outcome.Result?.ChapterCount,
                outcome.Result?.OutputBytes,
                outcome.Result is null ? null : (long)outcome.Result.Elapsed.TotalMilliseconds,
                outcome.ErrorMessage)).ToArray();
            try
            {
                await _historyStore.AppendAsync(historyEntries);
            }
            catch (Exception historyException)
            {
                StatusText.Text = $"转换已完成，但历史保存失败：{historyException.Message}";
            }

            var successes = outcomes.Where(outcome => outcome.Succeeded).ToArray();
            var failures = outcomes.Where(outcome => !outcome.Succeeded).ToArray();
            _lastFailedInputPaths = failures
                .Select(outcome => outcome.Request.InputPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _lastFailedBooks = failures.Select(outcome => InputBookItem.FromRequest(outcome.Request)).ToArray();
            RetryFailedButton.IsEnabled = _lastFailedInputPaths.Count > 0;
            var totalBytes = successes.Sum(outcome => outcome.Result!.OutputBytes);
            var cancelled = failures.Count(outcome => outcome.Cancelled);
            StatusText.Text = $"已完成：成功 {successes.Length} 本，失败 {failures.Length - cancelled} 本，取消 {cancelled} 本，合计 {totalBytes / 1024d:F1} KB";

            if (failures.Length > 0)
            {
                var historyWindow = new ConversionHistoryWindow(historyEntries) { Owner = this };
                if (historyWindow.ShowDialog() == true) LoadRetryInputs(historyWindow.RetryInputPaths);
            }
            else
            {
                MessageBox.Show(
                    this,
                    $"批量转换完成（{Path.GetExtension(requests[0].OutputPath).TrimStart('.').ToUpperInvariant()}）。\n输出目录：{OutputDirectoryText.Text.Trim()}",
                    "EasyPub Modern");
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "转换已取消；已经完成的文件会保留，未完成文件不会覆盖旧输出";
        }
        catch (Exception exception)
        {
            StatusText.Text = "转换失败";
            MessageBox.Show(this, exception.Message, "EasyPub Modern", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Progress.Value = 0;
            CancelButton.IsEnabled = false;
            ConvertButton.IsEnabled = InputBooks.Count > 0;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_operationCancellation is null || _operationCancellation.IsCancellationRequested) return;
        _operationCancellation.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在安全取消；当前 KindleGen 进程也会停止…";
    }

    private async Task<IReadOnlyList<ConversionRequest>> BuildConversionRequestsAsync()
    {
        if (InputBooks.Count == 0) throw new InvalidOperationException("请先添加至少一个 TXT 或 EPUB 文件。");
        var outputDirectory = OutputDirectoryText.Text.Trim();
        if (outputDirectory.Length == 0) throw new InvalidOperationException("请选择输出目录。");

        var profile = CaptureProfile();
        if (!string.Equals(profile.OutputFormat, "mobi", StringComparison.OrdinalIgnoreCase)
            && InputBooks.Any(book => book.IsEpub))
            throw new InvalidOperationException("EPUB 输入只能输出 MOBI。请把输出格式切换为 MOBI。");
        var options = profile.Options;
        if (string.IsNullOrWhiteSpace(options.AdditionalCss) &&
            !string.IsNullOrWhiteSpace(profile.AdditionalCssFilePath))
            options = options with { AdditionalCss = await File.ReadAllTextAsync(profile.AdditionalCssFilePath) };
        return BatchConversionRequestFactory.Create(
            InputBooks.Select(book => new BookConversionSource(
                book.InputPath,
                book.CoverImagePath,
                book.Title,
                book.Author,
                book.Illustrations,
                book.MetadataOverrides,
                book.ChapterTree)),
            outputDirectory,
            profile.OutputFormat,
            profile.Author,
            options);
    }

    private void AddFiles(IEnumerable<string> paths)
    {
        InputBookItem? firstAdded = null;
        foreach (var path in paths
                     .Where(IsSupportedInput)
                     .Select(Path.GetFullPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (InputBooks.Any(book => string.Equals(book.InputPath, path, StringComparison.OrdinalIgnoreCase)))
                continue;
            var book = new InputBookItem(path);
            ApplyMetadataMapping(book);
            InputBooks.Add(book);
            firstAdded ??= book;
        }
        if (firstAdded is not null && InputBooks.Any(book => book.IsEpub)) FormatCombo.SelectedIndex = 1;
        if (FilesList.SelectedItems.Count == 0 && firstAdded is not null)
            FilesList.SelectedItem = firstAdded;
        UpdateStatus();
    }

    private void ApplyMetadataMapping(InputBookItem book)
    {
        var matched = MetadataMappingResolver.Match(book.InputPath, _metadataMappings);
        var hasManualOverrides = book.MetadataRuleFolder is null && !book.MetadataOverrides.IsEmpty;
        if (hasManualOverrides) return;

        if (matched is null)
        {
            if (book.MetadataRuleFolder is not null)
                book.SetMetadataOverrides(new BookMetadataOverrides(), null);
            return;
        }

        book.SetMetadataOverrides(matched.Metadata, matched.FolderPath);
    }

    private void UpdateMetadataMappingSummary()
    {
        if (MetadataMappingStatusText is null) return;
        var selectedBooks = FilesList?.SelectedItems.Cast<InputBookItem>().ToArray() ?? [];
        var selected = selectedBooks.Length == 1 ? selectedBooks[0] : null;
        if (selected?.MetadataRuleFolder is not null)
        {
            var publisher = selected.MetadataOverrides.Publisher;
            MetadataMappingStatusText.Text = string.IsNullOrWhiteSpace(publisher)
                ? $"所选书已匹配：{Path.GetFileName(selected.MetadataRuleFolder)}"
                : $"所选书出版社：{publisher}";
            MetadataMappingStatusText.ToolTip = selected.MetadataRuleFolder;
            return;
        }

        MetadataMappingStatusText.Text = _metadataMappings.Count == 0
            ? "尚未设置文件夹映射"
            : $"已设置 {_metadataMappings.Count} 条文件夹映射";
        MetadataMappingStatusText.ToolTip = null;
    }

    private void UpdateStatus()
    {
        var count = InputBooks.Count;
        FileCountText.Text = $"{count} 本";
        EmptyFilesHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = count > 0;
        StatusText.Text = count == 0
            ? "准备就绪，可添加或拖入多个 TXT / EPUB 文件"
            : $"已添加 {count} 本 · TXT {InputBooks.Count(book => !book.IsEpub)} · EPUB {InputBooks.Count(book => book.IsEpub)} · {InputBooks.Count(book => book.ChapterTree is not null)} 本有章节树";
    }

    private static bool IsSupportedInput(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".epub";

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SelectComboItemByTag(ComboBox comboBox, string tag)
    {
        var item = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        if (item is not null) comboBox.SelectedItem = item;
    }

    private static int ParseInt(string value, string name) =>
        int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new ArgumentException($"{name}必须是整数。");

    private static int ParseNonNegativeInt(string value, string name)
    {
        var result = ParseInt(value, name);
        return result >= 0 ? result : throw new ArgumentException($"{name}不能小于 0。");
    }

    private static double ParseDouble(string value, string name) =>
        double.TryParse(value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : throw new ArgumentException($"{name}必须是数字。");
}

public sealed class InputBookItem : INotifyPropertyChanged
{
    private string _inputPath;
    private string? _coverImagePath;
    private string? _title;
    private string? _author;
    private IReadOnlyList<BookIllustration> _illustrations = [];
    private BookMetadataOverrides _metadataOverrides = new();
    private string? _metadataRuleFolder;
    private ChapterTreePlan? _chapterTree;

    public InputBookItem(string inputPath)
    {
        _inputPath = Path.GetFullPath(inputPath);
    }

    public string InputPath
    {
        get => _inputPath;
        set
        {
            if (!SetField(ref _inputPath, Path.GetFullPath(value))) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DirectoryPath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEpub)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FormatLabel)));
        }
    }

    public string DisplayName => Path.GetFileNameWithoutExtension(InputPath);

    public string DirectoryPath => Path.GetDirectoryName(InputPath) ?? string.Empty;
    public bool IsEpub => string.Equals(Path.GetExtension(InputPath), ".epub", StringComparison.OrdinalIgnoreCase);
    public string FormatLabel => IsEpub ? "EPUB" : "TXT";

    public string? Title
    {
        get => _title;
        set => SetField(ref _title, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string? Author
    {
        get => _author;
        set => SetField(ref _author, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string? CoverImagePath
    {
        get => _coverImagePath;
        set
        {
            if (!SetField(ref _coverImagePath, value is null ? null : Path.GetFullPath(value))) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverBadgeVisibility)));
        }
    }

    public string CoverLabel => CoverImagePath is null ? string.Empty : "有封面";

    public Visibility CoverBadgeVisibility => CoverImagePath is null ? Visibility.Collapsed : Visibility.Visible;

    public IReadOnlyList<BookIllustration> Illustrations => _illustrations;

    public string IllustrationLabel => $"插图 {_illustrations.Count}";

    public Visibility IllustrationBadgeVisibility => _illustrations.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public BookMetadataOverrides MetadataOverrides => _metadataOverrides;

    public string? MetadataRuleFolder => _metadataRuleFolder;

    public string MetadataLabel => string.IsNullOrWhiteSpace(MetadataOverrides.Publisher)
        ? "有元数据"
        : MetadataOverrides.Publisher;

    public Visibility MetadataBadgeVisibility => MetadataOverrides.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public ChapterTreePlan? ChapterTree => _chapterTree;
    public string ChapterTreeLabel => _chapterTree is null ? string.Empty : $"章节树 {_chapterTree.Entries.Count}";
    public Visibility ChapterTreeBadgeVisibility => _chapterTree is null ? Visibility.Collapsed : Visibility.Visible;

    public void SetIllustrations(IReadOnlyList<BookIllustration> illustrations)
    {
        _illustrations = illustrations.ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Illustrations)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IllustrationLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IllustrationBadgeVisibility)));
    }

    public void SetMetadataOverrides(BookMetadataOverrides? metadata, string? ruleFolder)
    {
        _metadataOverrides = metadata ?? new BookMetadataOverrides();
        _metadataRuleFolder = string.IsNullOrWhiteSpace(ruleFolder)
            ? null
            : MetadataMappingResolver.NormalizeFolder(ruleFolder);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataOverrides)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataRuleFolder)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MetadataBadgeVisibility)));
    }

    public void SetChapterTree(ChapterTreePlan? chapterTree)
    {
        _chapterTree = chapterTree;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterTree)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterTreeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterTreeBadgeVisibility)));
    }

    public InputBookItem Clone()
    {
        var clone = new InputBookItem(InputPath)
        {
            Title = Title,
            Author = Author,
            CoverImagePath = CoverImagePath,
        };
        clone.SetIllustrations(Illustrations);
        clone.SetMetadataOverrides(MetadataOverrides, MetadataRuleFolder);
        clone.SetChapterTree(ChapterTree);
        return clone;
    }

    public static InputBookItem FromRequest(ConversionRequest request)
    {
        var item = new InputBookItem(request.InputPath)
        {
            Title = request.Title,
            Author = request.Author,
            CoverImagePath = request.Options?.CoverImagePath,
        };
        item.SetIllustrations(request.Options?.Illustrations ?? []);
        item.SetChapterTree(request.ChapterTree);
        item.SetMetadataOverrides(new BookMetadataOverrides
        {
            Translator = request.Options?.Metadata.Translator,
            Isbn = request.Options?.Metadata.Isbn,
            PublicationDate = request.Options?.Metadata.PublicationDate,
            Publisher = request.Options?.Metadata.Publisher,
            Category = request.Options?.Metadata.Category,
            Language = request.Options?.Metadata.Language,
            Description = request.Options?.Metadata.Description,
        }, null);
        return item;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
