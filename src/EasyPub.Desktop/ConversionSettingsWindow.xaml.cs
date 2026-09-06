using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class ConversionSettingsWindow : UserControl
{
    private ConversionSettingsContext _context = new(string.Empty, 0, 0, [], Path.GetTempPath());
    private IReadOnlyList<PresetChoice> _presetChoices = [];
    private ConversionSettingsDraft _draft = ConversionSettingsDraft.CreateDefault(Path.GetTempPath());
    private ConversionSettingsDraft _openingDraft = ConversionSettingsDraft.CreateDefault(Path.GetTempPath());
    private LegacyConfigImport? _legacyImport;
    private bool _syncing;

    public ConversionSettingsWindow()
    {
        InitializeComponent();
        Loaded += ConversionSettingsWindow_Loaded;
    }

    public ConversionSettingsWindow(ConversionSettingsDraft draft, ConversionSettingsContext context) : this() =>
        LoadDraft(draft, context);

    public event Action<ConversionSettingsDraft>? Applied;
    public event Action? CloseRequested;
    public event Action? OpenEngineSettingsRequested;

    public void LoadDraft(ConversionSettingsDraft draft, ConversionSettingsContext context)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
        _draft = draft.Normalize(context.KindleGenPath);
        _openingDraft = _draft;
        _presetChoices = [new PresetChoice("当前设置", null), .. context.Presets.Select(preset => new PresetChoice(preset.Name, preset))];
        PresetCombo.ItemsSource = _presetChoices;
        PresetCombo.SelectedIndex = 0;
        ScopeSummaryText.Text = context.SelectedBookCount == 0
            ? "设置下次转换使用的默认值"
            : $"应用于当前选择的 {context.SelectedBookCount} 本书稿";
        EpubScopeText.Text = context.SelectedEpubCount == 0
            ? "当前没有选择 EPUB，但仍可提前保存默认值；该设置只对 EPUB 输入生效。"
            : $"当前选择中包含 {context.SelectedEpubCount} 本 EPUB；固定版式书稿不能兼容重排。";
        KindleGenPathText.Text = string.IsNullOrWhiteSpace(context.KindleGenPath) ? "未配置路径" : context.KindleGenPath;
        ApplyDraftToControls(_draft);
        if (IsLoaded) _ = RefreshKindleGenStatusAsync();
    }

    public ConversionSettingsDraft Result => _draft.Normalize(_context.KindleGenPath);

    public bool OpenEngineSettingsAfterClose { get; private set; }

    public void UpdateScope(int selectedBookCount, int selectedEpubCount)
    {
        _context = _context with
        {
            SelectedBookCount = selectedBookCount,
            SelectedEpubCount = selectedEpubCount,
        };
        ScopeSummaryText.Text = selectedBookCount == 0
            ? "设置下次转换使用的默认值"
            : $"应用于当前选择的 {selectedBookCount} 本书稿";
        EpubScopeText.Text = selectedEpubCount == 0
            ? "当前没有选择 EPUB，但仍可提前保存默认值；该设置只对 EPUB 输入生效。"
            : $"当前选择中包含 {selectedEpubCount} 本 EPUB；固定版式书稿不能兼容重排。";
        UpdateParallelismHint();
    }

    private async void ConversionSettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshKindleGenStatusAsync();
    }

    private async Task RefreshKindleGenStatusAsync()
    {
        var result = await KindleGenHealthProbe.ProbeAsync(_context.KindleGenPath);
        KindleGenStatusText.Text = result.Message;
        var brushKey = result.IsReady ? "SuccessBrush" : result.Status == KindleGenHealthStatus.Missing ? "ErrorBrush" : "WarningBrush";
        if (TryFindResource(brushKey) is Brush brush) KindleGenStatusText.Foreground = brush;
    }

    private void ApplyDraftToControls(ConversionSettingsDraft draft)
    {
        _syncing = true;
        try
        {
            SelectByTag(OutputFormatCombo, draft.Profile.OutputFormat.ToLowerInvariant());
            EpubOutputRadio.IsChecked = string.Equals(draft.Profile.OutputFormat, "epub", StringComparison.OrdinalIgnoreCase);
            MobiOutputRadio.IsChecked = EpubOutputRadio.IsChecked != true;
            OutputDirectoryText.Text = draft.OutputDirectory;
            SelectByTag(LayoutModeCombo, draft.Profile.Mode.ToString());
            var mobi = draft.Profile.Options.Mobi;
            PreserveEpubRadio.IsChecked = mobi.EpubInputMode == EpubInputMode.PreserveOriginal;
            ReflowEpubRadio.IsChecked = mobi.EpubInputMode == EpubInputMode.EasyPubCompatible;
            SelectByTag(CompressionCombo, ((int)mobi.Compression).ToString(CultureInfo.InvariantCulture));
            StripSourceCheck.IsChecked = mobi.StripSourceArchive;
            ReadingSyncCheck.IsChecked = mobi.EnableReadingProgressSync;
            AsinText.Text = mobi.Asin ?? string.Empty;
            KindleGenArgumentsText.Text = mobi.ExtraArguments ?? string.Empty;
            SelectByTag(ParallelismCombo, draft.Profile.Parallelism.ToString(CultureInfo.InvariantCulture));
            SelectByTag(CollisionPolicyCombo, draft.CollisionPolicy.ToString());
            OptimizeLongBookCheck.IsChecked = mobi.OptimizeContentPackaging;
            ArtifactValidationCheck.IsChecked = draft.Profile.Options.ArtifactValidation.Enabled;
            SelectByTag(ReportRetentionCombo, Math.Clamp(draft.Profile.Options.ArtifactValidation.MaxReportCount, 1, 1000).ToString(CultureInfo.InvariantCulture));
            AutoOpenOutputCheck.IsChecked = draft.AutoOpenOutputDirectory;
            AutoOpenTaskCenterCheck.IsChecked = draft.AutoOpenTaskCenter;
            UpdateLegacyConfigState(draft.LegacyConfigPath);
            UpdateDependentControls();
            UpdateParallelismHint();
        }
        finally
        {
            _syncing = false;
        }
    }

    private ConversionSettingsDraft CaptureDraftFromControls()
    {
        var outputDirectory = OutputDirectoryText.Text.Trim();
        if (outputDirectory.Length == 0) throw new InvalidOperationException("请选择输出目录。");
        var currentOptions = _draft.Profile.Options;
        var mobi = currentOptions.Mobi with
        {
            KindleGenPath = string.IsNullOrWhiteSpace(_context.KindleGenPath) ? null : _context.KindleGenPath,
            Compression = (MobiCompression)int.Parse(SelectedTag(CompressionCombo, "1"), CultureInfo.InvariantCulture),
            StripSourceArchive = StripSourceCheck.IsChecked == true,
            EnableReadingProgressSync = ReadingSyncCheck.IsChecked == true,
            Asin = EmptyToNull(AsinText.Text),
            ExtraArguments = EmptyToNull(KindleGenArgumentsText.Text),
            EpubInputMode = ReflowEpubRadio.IsChecked == true ? EpubInputMode.EasyPubCompatible : EpubInputMode.PreserveOriginal,
            OptimizeContentPackaging = OptimizeLongBookCheck.IsChecked == true,
        };
        var profile = _draft.Profile with
        {
            OutputFormat = SelectedTag(OutputFormatCombo, "mobi"),
            Parallelism = int.Parse(SelectedTag(ParallelismCombo, "0"), CultureInfo.InvariantCulture),
            Mode = Enum.Parse<ConversionMode>(SelectedTag(LayoutModeCombo, ConversionMode.OriginalCompatible.ToString())),
            Options = currentOptions with
            {
                Mobi = mobi,
                ArtifactValidation = new ArtifactValidationOptions
                {
                    Enabled = ArtifactValidationCheck.IsChecked == true,
                    MaxReportCount = int.Parse(SelectedTag(ReportRetentionCombo, "10"), CultureInfo.InvariantCulture),
                },
            },
        };
        return _draft with
        {
            OutputDirectory = outputDirectory,
            Profile = profile,
            CollisionPolicy = Enum.Parse<OutputCollisionPolicy>(SelectedTag(CollisionPolicyCombo, OutputCollisionPolicy.AutoRename.ToString())),
            AutoOpenOutputDirectory = AutoOpenOutputCheck.IsChecked == true,
            AutoOpenTaskCenter = AutoOpenTaskCenterCheck.IsChecked == true,
        };
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择电子书输出目录",
            InitialDirectory = Directory.Exists(OutputDirectoryText.Text) ? OutputDirectoryText.Text : _context.DefaultOutputDirectory,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) OutputDirectoryText.Text = dialog.FolderName;
    }

    private void BrowseLegacyConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择原版 EasyPub config.xml",
            Filter = "EasyPub 配置 (config.xml)|config.xml|XML 文件 (*.xml)|*.xml",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            _draft = CaptureDraftFromControls();
            _legacyImport = LegacyEasyPubConfig.Load(dialog.FileName);
            _draft = _draft.ApplyLegacyConfig(_legacyImport, _context.KindleGenPath);
            ApplyDraftToControls(_draft);
            LegacyConfigStatusText.Text = $"已载入 {Path.GetFileName(_legacyImport.SourcePath)} · 应用 {_legacyImport.AppliedSettings.Count} 组设置";
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, "无法载入原版配置", MessageBoxImage.Error);
        }
    }

    private void ClearLegacyConfig_Click(object sender, RoutedEventArgs e)
    {
        _legacyImport = null;
        _draft = _draft with { LegacyConfigPath = null };
        UpdateLegacyConfigState(null);
    }

    private void ShowLegacyMapping_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var import = _legacyImport
                ?? (!string.IsNullOrWhiteSpace(_draft.LegacyConfigPath) && File.Exists(_draft.LegacyConfigPath)
                    ? LegacyEasyPubConfig.Load(_draft.LegacyConfigPath!)
                    : null);
            if (import is null) return;
            var message = $"配置来源：\n{import.SourcePath}\n\n已应用：\n- "
                + string.Join("\n- ", import.AppliedSettings)
                + "\n\n尚未应用：\n- "
                + string.Join("\n- ", import.UnsupportedSettings);
            ShowMessage(message, "原版配置映射", MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, "无法读取配置映射", MessageBoxImage.Error);
        }
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || PresetCombo.SelectedItem is not PresetChoice { Preset: { } preset }) return;
        _draft = CaptureDraftFromControls() with
        {
            Profile = preset.Profile with
            {
                Options = preset.Profile.Options with
                {
                    Mobi = preset.Profile.Options.Mobi with { KindleGenPath = _context.KindleGenPath },
                },
            },
        };
        ApplyDraftToControls(_draft);
        PresetCombo.SelectedItem = _presetChoices.First(choice => choice.Preset == preset);
    }

    private void OutputFormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) UpdateParallelismHint();
    }

    private void OutputFormatRadio_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton radioButton) return;
        SelectByTag(OutputFormatCombo, radioButton.Tag?.ToString() ?? "mobi");
        UpdateParallelismHint();
    }

    private void ParallelismCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncing) UpdateParallelismHint();
    }

    private void ReadingSyncCheck_Changed(object sender, RoutedEventArgs e) => UpdateDependentControls();

    private void ArtifactValidationCheck_Changed(object sender, RoutedEventArgs e) => UpdateDependentControls();

    private void UpdateDependentControls()
    {
        if (AsinText is not null) AsinText.IsEnabled = ReadingSyncCheck?.IsChecked == true;
        if (ReportRetentionCombo is not null) ReportRetentionCombo.IsEnabled = ArtifactValidationCheck?.IsChecked == true;
    }

    private void UpdateParallelismHint()
    {
        if (ParallelismCombo?.SelectedItem is null || ParallelismHintText is null) return;
        var requested = int.Parse(SelectedTag(ParallelismCombo, "0"), CultureInfo.InvariantCulture);
        if (requested > 0)
        {
            ParallelismHintText.Text = $"同时处理最多 {requested} 本书稿。";
            return;
        }

        var count = Math.Max(1, _context.SelectedBookCount);
        var processors = Math.Max(1, Environment.ProcessorCount);
        var mobi = string.Equals(SelectedTag(OutputFormatCombo, "mobi"), "mobi", StringComparison.OrdinalIgnoreCase);
        var recommended = mobi ? Math.Clamp((processors + 1) / 2, 2, 8) : Math.Clamp(processors, 2, 16);
        ParallelismHintText.Text = _context.SelectedBookCount == 0
            ? "开始转换时根据电脑资源、输出格式和书稿数量自动决定。"
            : $"当前批次预计同时处理 {Math.Min(recommended, count)} 本书稿。";
    }

    private void UpdateLegacyConfigState(string? path)
    {
        var hasConfig = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
        LegacyConfigStatusText.Text = hasConfig ? $"已选择 {path}" : "尚未选择；不会导入原版配置";
        LegacyConfigStatusText.ToolTip = hasConfig ? path : null;
        ClearLegacyConfigButton.IsEnabled = hasConfig;
        ShowLegacyMappingButton.IsEnabled = hasConfig;
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        _legacyImport = null;
        _draft = ConversionSettingsDraft.CreateDefault(_context.DefaultOutputDirectory, _context.KindleGenPath);
        ApplyDraftToControls(_draft);
        PresetCombo.SelectedIndex = 0;
    }

    private void OpenEngineSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenEngineSettingsAfterClose = true;
        OpenEngineSettingsRequested?.Invoke();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _draft = CaptureDraftFromControls().Normalize(_context.KindleGenPath);
            _openingDraft = _draft;
            Applied?.Invoke(_draft);
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, "无法应用转换设置", MessageBoxImage.Error);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _draft = _openingDraft;
        ApplyDraftToControls(_draft);
        CloseRequested?.Invoke();
    }

    private void ShowMessage(string message, string caption, MessageBoxImage image)
    {
        var owner = Window.GetWindow(this);
        if (owner is not null)
            InkDialog.Show(owner, message, caption, MessageBoxButton.OK, image);
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private static string SelectedTag(ComboBox comboBox, string fallback) =>
        (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record PresetChoice(string Name, NamedConversionPreset? Preset)
    {
        public override string ToString() => Name;
    }
}
