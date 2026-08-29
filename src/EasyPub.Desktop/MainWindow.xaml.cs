using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using EasyPub.Core;
using Microsoft.Win32;

namespace EasyPub.Desktop;

public partial class MainWindow : Window
{
    private enum WorkspacePage
    {
        Library,
        Chapters,
        Cover,
        Layout,
        Convert,
        Tasks,
    }

    private LegacyConfigImport? _legacyConfig;
    private bool _useLegacyConfig = true;
    private int _coverPreviewVersion;
    private readonly FavoriteFolderStore _favoriteFolderStore = FavoriteFolderStore.CreateDefault();
    private readonly MetadataMappingStore _metadataMappingStore = MetadataMappingStore.CreateDefault();
    private readonly AppSettingsStore _appSettingsStore = AppSettingsStore.CreateDefault();
    private readonly ConversionHistoryStore _historyStore = ConversionHistoryStore.CreateDefault();
    private readonly ConversionPreflightCache _preflightCache = new();
    private readonly object _chapterDocumentCacheGate = new();
    private readonly Dictionary<ChapterDocumentCacheKey, Task<ChapterTreeDocument>> _chapterDocumentCache = [];
    private readonly EasyPubProjectStore _recoveryStore = EasyPubProjectStore.CreateRecoveryDefault();
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _bookFilterTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _statusRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly Dictionary<string, BookTaskViewModel> _bookTasksByInputPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<InputBookItem> _trackedBooks = [];
    private IReadOnlyList<string> _lastFailedInputPaths = [];
    private IReadOnlyList<InputBookItem> _lastFailedBooks = [];
    private string? _customCss;
    private string? _customCssSourcePath;
    private TocHierarchyOptions _tocHierarchy = new();
    private TextCleanupOptions _textCleanupOptions = new();
    private ConversionMode _conversionMode = ConversionMode.OriginalCompatible;
    private bool _applyingProfile;
    private TaskCenterWindow? _taskCenterWindow;
    private bool _closeSaveInProgress;
    private bool _allowClose;
    private bool _recoverySaveInProgress;
    private string? _lastRecoveryFingerprint;
    private string? _lastExplicitSaveFingerprint;
    private string? _currentProjectPath;
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<FolderMetadataRule> _metadataMappings = [];
    private EasyPubProjectDocument? _pendingRecovery;
    private readonly Dictionary<TabItem, string> _optionTabNames = [];
    private readonly HashSet<TabItem> _dirtyOptionTabs = [];
    private bool _optionTrackingReady;
    private bool _compactLayout;
    private ICollectionView? _bookWorklistView;
    private ConversionPreflightReport? _lastPreflightReport;
    private string? _activeConversionPlanName;
    private WorkspacePage _workspacePage = WorkspacePage.Library;
    private string _theme = ThemeManager.LightTheme;
    private string _uiDensity = "Comfortable";
    private int _uiScalePercent = 100;
    private bool _rememberWindowPlacement = true;
    private bool _reduceMotion;
    private bool _syncingSelectedBook;
    private bool _syncingVisibleLayout;
    private bool _syncingLayoutMode;
    private bool _syncingSelectedMetadata;
    private string? _profileAuthor;
    private PublicationMetadata _profileMetadata = new();
    private IReadOnlyDictionary<string, string> _shortcutBindings = new Dictionary<string, string>();
    private BatchExecutionControl? _batchExecutionControl;
    private bool _brushSelecting;
    private bool _brushSelectValue;
    private readonly BoundedHistory<(string Label, EasyPubProjectDocument Snapshot)> _undoStack = new(10);
    private string _layoutPreviewDocumentTitle = "第一章　书页预览";
    private IReadOnlyList<string> _layoutPreviewParagraphs = ["这里会显示所选书稿的真实正文片段。", "调整字号、行高、段间距和页边距后，预览会立即更新。"];
    private IReadOnlyList<LayoutPreviewPage> _layoutPreviewPages = [];
    private int _layoutPreviewPageIndex;
    private CancellationTokenSource? _selectionPreviewCancellation;
    private ChapterTreeDocument? _chapterPreviewDocument;
    private long _projectChangeGeneration;
    private long _savedRecoveryGeneration;
    public ObservableCollection<InputBookItem> InputBooks { get; } = [];
    public ObservableCollection<InputBookItem> ConversionPreviewBooks { get; } = [];
    public ObservableCollection<string> FavoriteFolders { get; } = [];
    public ObservableCollection<NamedConversionPreset> ConversionPresets { get; } = [];
    public ObservableCollection<BookTaskViewModel> BookTasks { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        InputBooks.CollectionChanged += InputBooks_CollectionChanged;
        OutputDirectoryText.TextChanged += (_, _) => MarkProjectDirty();
        _bookFilterTimer.Tick += (_, _) =>
        {
            _bookFilterTimer.Stop();
            ApplyBookWorklistFilter();
        };
        _statusRefreshTimer.Tick += (_, _) =>
        {
            _statusRefreshTimer.Stop();
            UpdateStatus();
        };
        FormatCombo.SelectionChanged += (_, _) => MarkProjectDirty();
        ParallelismCombo.SelectionChanged += (_, _) => MarkProjectDirty();
        KindleGenText.TextChanged += (_, _) => MarkProjectDirty();
        CompressionCombo.SelectionChanged += (_, _) => MarkProjectDirty();
        StripSourceCheck.Checked += (_, _) => MarkProjectDirty();
        StripSourceCheck.Unchecked += (_, _) => MarkProjectDirty();
        OptimizeMobiPackagingCheck.Checked += (_, _) => MarkProjectDirty();
        OptimizeMobiPackagingCheck.Unchecked += (_, _) => MarkProjectDirty();
        MobiSyncCheck.Checked += (_, _) => MarkProjectDirty();
        MobiSyncCheck.Unchecked += (_, _) => MarkProjectDirty();
        MobiAsinText.TextChanged += (_, _) => MarkProjectDirty();
        KindleGenArgsText.TextChanged += (_, _) => MarkProjectDirty();
        ArtifactValidationCheck.Checked += (_, _) => MarkProjectDirty();
        ArtifactValidationCheck.Unchecked += (_, _) => MarkProjectDirty();
        ValidationRetentionCombo.SelectionChanged += (_, _) => MarkProjectDirty();
        _bookWorklistView = CollectionViewSource.GetDefaultView(InputBooks);
        _bookWorklistView.Filter = FilterBookWorklist;
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
        InitializeOptionTracking();
        KindleModelCombo.ItemsSource = KindleDeviceProfiles.BuiltIn.Concat([KindleDeviceProfiles.Custom(1264, 1680, 300)]).ToArray();
        KindleModelCombo.SelectedItem = KindleDeviceProfiles.BuiltIn.First(item => item.Id == "kpw6");
        SyncVisibleLayoutControls();
        RefreshLayoutPreview();
        UpdateModeDescription();
        UpdateProjectTitle();
        UpdateContextualControls();
        ShowWorkspacePage(WorkspacePage.Library);
    }

    private void NavigateLibrary_Click(object sender, RoutedEventArgs e) => ShowWorkspacePage(WorkspacePage.Library);
    private void NavigateChapters_Click(object sender, RoutedEventArgs e) => EditChapters_Click(sender, e);
    private void NavigateCover_Click(object sender, RoutedEventArgs e) => ShowWorkspacePage(WorkspacePage.Cover);
    private void NavigateLayout_Click(object sender, RoutedEventArgs e) => ShowWorkspacePage(WorkspacePage.Layout);
    private void NavigateConvert_Click(object sender, RoutedEventArgs e) => ShowWorkspacePage(WorkspacePage.Convert);

    private void NavigateTasks_Click(object sender, RoutedEventArgs e) => ShowWorkspacePage(WorkspacePage.Tasks);

    private async void ManageShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var window = new ShortcutManagerWindow(_shortcutBindings) { Owner = this };
        if (window.ShowDialog() != true) return;
        _shortcutBindings = window.Bindings;
        await _appSettingsStore.SaveAsync(CaptureAppSettings());
        StatusText.Text = "快捷键设置已保存";
    }

    private void ProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = ProjectMenuButton };
        foreach (var (label, action) in new (string, RoutedEventHandler)[]
        {
            ("新建项目", NewProject_Click), ("打开项目…", OpenProject_Click),
            ("保存项目", SaveProject_Click), ("项目另存为…", SaveProjectAs_Click),
        })
        {
            var item = new MenuItem { Header = label };
            item.Click += action;
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ShowWorkspacePage(WorkspacePage page)
    {
        if (PageTitleText is null) return;
        if (page == WorkspacePage.Chapters) page = WorkspacePage.Library;
        _workspacePage = page;
        LibraryNavigationButton.IsChecked = page == WorkspacePage.Library;
        ChaptersNavigationButton.IsChecked = page == WorkspacePage.Chapters;
        CoverNavigationButton.IsChecked = page == WorkspacePage.Cover;
        LayoutNavigationButton.IsChecked = page == WorkspacePage.Layout;
        ConvertNavigationButton.IsChecked = page == WorkspacePage.Convert;
        TasksNavigationButton.IsChecked = page == WorkspacePage.Tasks;

        InkLibraryPage.Visibility = page == WorkspacePage.Library ? Visibility.Visible : Visibility.Collapsed;
        InkChaptersPage.Visibility = page == WorkspacePage.Chapters ? Visibility.Visible : Visibility.Collapsed;
        InkCoverPage.Visibility = page == WorkspacePage.Cover ? Visibility.Visible : Visibility.Collapsed;
        InkLayoutPage.Visibility = page == WorkspacePage.Layout ? Visibility.Visible : Visibility.Collapsed;
        InkConvertPage.Visibility = page == WorkspacePage.Convert ? Visibility.Visible : Visibility.Collapsed;
        TaskCenterLanding.Visibility = page == WorkspacePage.Tasks ? Visibility.Visible : Visibility.Collapsed;
        LibraryToolbarPanel.Visibility = page == WorkspacePage.Library ? Visibility.Visible : Visibility.Collapsed;
        FileCountBadge.Visibility = page is WorkspacePage.Library or WorkspacePage.Convert ? Visibility.Visible : Visibility.Collapsed;

        switch (page)
        {
            case WorkspacePage.Library:
                PageTitleText.Text = "书库";
                PageSubtitleText.Text = "导入、筛选并批量管理待转换书稿";
                UpdateSelectedBookInspector(FilesList.SelectedItems.Count == 1 ? FilesList.SelectedItem as InputBookItem : null);
                break;
            case WorkspacePage.Chapters:
                PageTitleText.Text = "章节正文";
                PageSubtitleText.Text = "按章节检查与查找正文；章节结构统一在章节树工作台中编辑";
                break;
            case WorkspacePage.Cover:
                PageTitleText.Text = "封面信息";
                PageSubtitleText.Text = "为每本书分别设置封面与书籍元数据";
                if (CoverBookCombo.SelectedItem is null)
                    CoverBookCombo.SelectedItem = FilesList.SelectedItems.Count == 1
                        ? FilesList.SelectedItem as InputBookItem
                        : InputBooks.FirstOrDefault();
                _ = RefreshCoverPreviewAsync();
                break;
            case WorkspacePage.Layout:
                PageTitleText.Text = "排版插图";
                PageSubtitleText.Text = "设置版式、页边距、字体、CSS 与正文插图";
                SyncVisibleLayoutControls();
                RefreshLayoutPreview();
                break;
            case WorkspacePage.Convert:
                PageTitleText.Text = "转换输出";
                PageSubtitleText.Text = "选择目标格式与输出位置，然后开始批量转换";
                break;
            case WorkspacePage.Tasks:
                PageTitleText.Text = "任务中心";
                PageSubtitleText.Text = "查看转换进度、验收结果并处理失败项目";
                break;
        }

        UpdateContextualControls();
    }

    private void UseLightTheme_Click(object sender, RoutedEventArgs e)
    {
        _theme = ThemeManager.LightTheme;
        ThemeManager.Apply(_theme, this);
    }

    private void UseDarkTheme_Click(object sender, RoutedEventArgs e)
    {
        _theme = ThemeManager.DarkTheme;
        ThemeManager.Apply(_theme, this);
    }

    private void MoreLibraryActions_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var selectAll = new MenuItem { Header = "全选全部书稿", IsEnabled = InputBooks.Count > 0 };
        selectAll.Click += SelectAllFiles_Click;
        var clear = new MenuItem { Header = "清空书库", IsEnabled = InputBooks.Count > 0 };
        clear.Click += ClearFiles_Click;
        var favorites = new MenuItem { Header = "管理收藏文件夹" };
        favorites.Click += ManageFavoriteFolders_Click;
        var undo = new MenuItem { Header = _undoStack.TryPeek(out var entry) ? $"撤销：{entry.Label}" : "撤销批量操作", IsEnabled = _undoStack.Count > 0 };
        undo.Click += UndoLastBatch_Click;
        menu.Items.Add(selectAll);
        menu.Items.Add(clear);
        menu.Items.Add(undo);
        menu.Items.Add(new Separator());
        menu.Items.Add(favorites);
        menu.PlacementTarget = sender as UIElement;
        menu.IsOpen = true;
    }

    private void BookActions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InputBookItem book } button) return;
        SelectOnlyBook(book);

        var menu = new ContextMenu { PlacementTarget = button };
        var editChapters = new MenuItem { Header = "编辑章节结构", IsEnabled = !book.IsEpub };
        editChapters.Click += EditChapters_Click;
        var editMetadata = new MenuItem { Header = "编辑封面信息" };
        editMetadata.Click += (_, _) => ShowWorkspacePage(WorkspacePage.Cover);
        var cleanup = new MenuItem { Header = "文本清理", IsEnabled = !book.IsEpub };
        cleanup.Click += EditTextCleanup_Click;
        var openFolder = new MenuItem { Header = "在资源管理器中显示" };
        openFolder.Click += (_, _) => System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{book.InputPath}\"") { UseShellExecute = true });
        var remove = new MenuItem { Header = "从书库移除" };
        remove.Click += (_, _) =>
        {
            PushUndo("移除书稿");
            InputBooks.Remove(book);
            UpdateStatus();
        };
        menu.Items.Add(editChapters);
        menu.Items.Add(editMetadata);
        menu.Items.Add(cleanup);
        menu.Items.Add(openFolder);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        button.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void SelectOnlyBook(InputBookItem book)
    {
        _syncingSelectedBook = true;
        FilesList.SelectedItems.Clear();
        FilesList.SelectedItem = book;
        FilesList.ScrollIntoView(book);
        ChapterBookCombo.SelectedItem = book;
        if (_workspacePage != WorkspacePage.Cover) CoverBookCombo.SelectedItem = book;
        _syncingSelectedBook = false;
        UpdateContextualControls();
        _ = RefreshCoverPreviewAsync();
    }

    private void PushUndo(string label)
    {
        _undoStack.Push((label, CaptureProjectDocument()));
    }

    private void UndoLastBatch_Click(object sender, RoutedEventArgs e)
    {
        if (!_undoStack.TryPop(out var item)) return;
        ApplyProjectDocument(item.Snapshot);
        StatusText.Text = $"已撤销：{item.Label}";
    }

    private void ChapterBookCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelectedBook || ChapterBookCombo.SelectedItem is not InputBookItem book) return;
        _syncingSelectedBook = true;
        FilesList.SelectedItems.Clear();
        FilesList.SelectedItem = book;
        FilesList.ScrollIntoView(book);
        _syncingSelectedBook = false;
    }

    private async void CoverBookCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingProfile || CoverBookCombo.SelectedItem is not InputBookItem book)
        {
            if (_workspacePage == WorkspacePage.Cover)
                await RefreshCoverPreviewAsync();
            return;
        }

        LoadSelectedBookMetadataFields(book);
        UpdateMetadataMappingSummary();
        await RefreshCoverPreviewAsync();
        StatusText.Text = $"正在编辑《{book.DisplayName}》的封面与书籍信息；书库批量选择保持不变";
    }

    private void FavoriteFolderMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { DataContext: string folder }) return;
        FavoriteFolderCombo.SelectedItem = FavoriteFolders.FirstOrDefault(item => string.Equals(item, folder, StringComparison.OrdinalIgnoreCase));
        AddFromFavoriteFolder_Click(sender, e);
        e.Handled = true;
    }

    private void ManageFavoriteFolders_Click(object sender, RoutedEventArgs e) => ManageFavoriteFoldersWindow(this);

    private void ManageFavoriteFoldersWindow(Window owner)
    {
        var manager = new FavoriteFolderManagerWindow(FavoriteFolders) { Owner = owner };
        if (manager.ShowDialog() != true) return;
        ApplyFavoriteFolders(manager.Result);
        StatusText.Text = $"收藏文件夹已更新，共 {FavoriteFolders.Count} 个";
    }

    private async void ShowSettings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async void OpenEngineSettings_Click(object sender, RoutedEventArgs e) => await ShowSettingsAsync(2);

    private async Task ShowSettingsAsync(int initialSection = 0)
    {
        SettingsWindow? settingsWindow = null;
        settingsWindow = new SettingsWindow(
            _theme,
            _uiDensity,
            _uiScalePercent,
            _rememberWindowPlacement,
            _reduceMotion,
            OutputDirectoryText.Text,
            KindleGenText.Text,
            int.Parse((ParallelismCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0", CultureInfo.InvariantCulture),
            ArtifactValidationCheck.IsChecked == true,
            int.Parse(((ComboBoxItem)ValidationRetentionCombo.SelectedItem).Tag!.ToString()!, CultureInfo.InvariantCulture),
            AutoOpenTaskCenterCheck.IsChecked == true,
            AutoOpenOutputDirectoryCheck.IsChecked == true,
            _shortcutBindings,
            FavoriteFolders.Count,
            () => ManageFavoriteFoldersWindow(settingsWindow!))
        {
            Owner = this,
        };
        settingsWindow.SelectedSection = initialSection;
        if (settingsWindow.ShowDialog() != true) return;

        _theme = settingsWindow.Theme;
        _uiDensity = settingsWindow.Density;
        _uiScalePercent = settingsWindow.ScalePercent;
        _rememberWindowPlacement = settingsWindow.RememberWindowPlacement;
        _reduceMotion = settingsWindow.ReduceMotion;
        OutputDirectoryText.Text = settingsWindow.OutputDirectory;
        KindleGenText.Text = settingsWindow.KindleGenPath;
        SelectComboItemByTag(ParallelismCombo, settingsWindow.Parallelism.ToString(CultureInfo.InvariantCulture));
        ArtifactValidationCheck.IsChecked = settingsWindow.ValidationEnabled;
        SelectComboItemByTag(ValidationRetentionCombo, settingsWindow.ReportRetention.ToString(CultureInfo.InvariantCulture));
        ValidationRetentionCombo.IsEnabled = settingsWindow.ValidationEnabled;
        AutoOpenTaskCenterCheck.IsChecked = settingsWindow.AutoOpenTaskCenter;
        AutoOpenOutputDirectoryCheck.IsChecked = settingsWindow.AutoOpenOutputDirectory;
        _shortcutBindings = settingsWindow.ShortcutBindings;
        ApplyAppearanceSettings();
        await _appSettingsStore.SaveAsync(CaptureAppSettings());
        StatusText.Text = "全局设置已保存";
    }

    private void ApplyAppearanceSettings()
    {
        ThemeManager.Apply(_theme, this);
        var compact = string.Equals(_uiDensity, "Compact", StringComparison.OrdinalIgnoreCase);
        SidebarColumn.Width = new GridLength(compact ? 166 : 184);
        foreach (var button in new[] { LibraryNavigationButton, ChaptersNavigationButton, CoverNavigationButton, LayoutNavigationButton, ConvertNavigationButton, TasksNavigationButton })
            button.Height = compact ? 42 : 48;
        FontSize = Math.Clamp(12d * _uiScalePercent / 100d, 10.5, 15);
        ShowWorkspacePage(_workspacePage);
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
        finally
        {
            _optionTrackingReady = true;
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _recoveryTimer.Stop();
        _statusRefreshTimer.Stop();
        _selectionPreviewCancellation?.Cancel();
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
            InkDialog.Show(this, exception.Message, "无法保存配置选择", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowLegacyConfigDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_legacyConfig is null)
        {
            InkDialog.Show(this, "尚未加载原版 config.xml。", "EasyPub Modern");
            return;
        }

        var message = $"配置来源：\n{_legacyConfig.SourcePath}\n\n已应用：\n- "
            + string.Join("\n- ", _legacyConfig.AppliedSettings)
            + "\n\n尚未应用（配置值已保留在原文件中）：\n- "
            + string.Join("\n- ", _legacyConfig.UnsupportedSettings);
        InkDialog.Show(this, message, "原版配置映射", MessageBoxButton.OK, MessageBoxImage.Information);
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
                InkDialog.Show(this, exception.Message, "无法加载原版配置", MessageBoxButton.OK, MessageBoxImage.Error);
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
        SelectComboItemByTag(FullWidthIndentCountCombo, Math.Clamp(options.FullWidthIndentCount, 0, 20).ToString(CultureInfo.InvariantCulture));
        KindleGenText.Text = KindleGenPathPreference.ResolveForCurrentInstallation(
            options.Mobi.KindleGenPath,
            AppContext.BaseDirectory) ?? string.Empty;
        SelectComboItemByTag(CompressionCombo, ((int)options.Mobi.Compression).ToString(CultureInfo.InvariantCulture));
        StripSourceCheck.IsChecked = options.Mobi.StripSourceArchive;
        OptimizeMobiPackagingCheck.IsChecked = options.Mobi.OptimizeContentPackaging;
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
        AutoOpenTaskCenterCheck.IsChecked = settings.AutoOpenTaskCenter;
        AutoOpenOutputDirectoryCheck.IsChecked = settings.AutoOpenOutputDirectory;
        SelectComboItemByTag(OutputCollisionCombo, settings.OutputCollisionPolicy.ToString());
        _shortcutBindings = settings.ShortcutBindings ?? new Dictionary<string, string>();
        CustomKindleWidthText.Text = settings.CustomKindleWidth.ToString(CultureInfo.InvariantCulture);
        CustomKindleHeightText.Text = settings.CustomKindleHeight.ToString(CultureInfo.InvariantCulture);
        CustomKindlePpiText.Text = settings.CustomKindlePpi.ToString(CultureInfo.InvariantCulture);
        KindleModelCombo.SelectedItem = KindleModelCombo.Items.OfType<KindleDeviceProfile>().FirstOrDefault(item => item.Id == settings.KindlePreviewDeviceId) ?? KindleModelCombo.Items.OfType<KindleDeviceProfile>().First();
        _theme = string.IsNullOrWhiteSpace(settings.Theme) ? ThemeManager.LightTheme : settings.Theme;
        _uiDensity = string.IsNullOrWhiteSpace(settings.UiDensity) ? "Comfortable" : settings.UiDensity;
        _uiScalePercent = Math.Clamp(settings.UiScalePercent, 90, 125);
        _rememberWindowPlacement = settings.RememberWindowPlacement;
        _reduceMotion = settings.ReduceMotion;
        ApplyAppearanceSettings();
        ApplyWindowPlacement(settings);
    }

    private void ApplyWindowPlacement(EasyPubAppSettings settings)
    {
        if (!settings.RememberWindowPlacement
            || settings.WindowWidth is not double width
            || settings.WindowHeight is not double height
            || !double.IsFinite(width)
            || !double.IsFinite(height)) return;

        Width = Math.Clamp(width, MinWidth, Math.Max(MinWidth, SystemParameters.VirtualScreenWidth));
        Height = Math.Clamp(height, MinHeight, Math.Max(MinHeight, SystemParameters.VirtualScreenHeight));
        if (settings.WindowLeft is double left && settings.WindowTop is double top
            && double.IsFinite(left) && double.IsFinite(top))
        {
            var visibleLeft = SystemParameters.VirtualScreenLeft;
            var visibleTop = SystemParameters.VirtualScreenTop;
            var visibleRight = visibleLeft + SystemParameters.VirtualScreenWidth;
            var visibleBottom = visibleTop + SystemParameters.VirtualScreenHeight;
            Left = Math.Clamp(left, visibleLeft, Math.Max(visibleLeft, visibleRight - 120));
            Top = Math.Clamp(top, visibleTop, Math.Max(visibleTop, visibleBottom - 80));
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        if (string.Equals(settings.WindowState, nameof(System.Windows.WindowState.Maximized), StringComparison.OrdinalIgnoreCase))
            WindowState = System.Windows.WindowState.Maximized;
    }

    private void ApplyProfile(ConversionProfile profile)
    {
        _applyingProfile = true;
        _conversionMode = profile.Mode;
        OriginalModeRadio.IsChecked = profile.Mode == ConversionMode.OriginalCompatible;
        ModernModeRadio.IsChecked = profile.Mode == ConversionMode.ModernLayout;
        CustomModeRadio.IsChecked = profile.Mode == ConversionMode.Custom;
        FormatCombo.SelectedIndex = string.Equals(profile.OutputFormat, "mobi", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        _profileAuthor = profile.Author;
        AuthorText.Text = profile.Author ?? string.Empty;
        var metadata = profile.Options.Metadata ?? new PublicationMetadata();
        _profileMetadata = metadata;
        TranslatorText.Text = metadata.Translator ?? string.Empty;
        IsbnText.Text = metadata.Isbn ?? string.Empty;
        PublicationDatePicker.SelectedDate = metadata.PublicationDate?.ToDateTime(TimeOnly.MinValue);
        PublisherText.Text = metadata.Publisher ?? string.Empty;
        CategoryCombo.Text = metadata.Category ?? string.Empty;
        LanguageCombo.Text = string.IsNullOrWhiteSpace(metadata.Language) ? "zh-CN" : metadata.Language;
        DescriptionText.Text = metadata.Description ?? string.Empty;
        var parallelItem = ParallelismCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag?.ToString() == profile.Parallelism.ToString(CultureInfo.InvariantCulture));
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
        _textCleanupOptions = options.TextCleanup ?? new TextCleanupOptions();
        UpdateTextCleanupSummary();
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
        SelectComboItemByTag(FullWidthIndentCountCombo, Math.Clamp(options.FullWidthIndentCount, 0, 20).ToString(CultureInfo.InvariantCulture));
        KindleGenText.Text = KindleGenPathPreference.ResolveForCurrentInstallation(
            options.Mobi.KindleGenPath,
            AppContext.BaseDirectory) ?? string.Empty;
        SelectComboItemByTag(CompressionCombo, ((int)options.Mobi.Compression).ToString(CultureInfo.InvariantCulture));
        StripSourceCheck.IsChecked = options.Mobi.StripSourceArchive;
        OptimizeMobiPackagingCheck.IsChecked = options.Mobi.OptimizeContentPackaging;
        MobiSyncCheck.IsChecked = options.Mobi.EnableReadingProgressSync;
        MobiAsinText.Text = options.Mobi.Asin ?? string.Empty;
        KindleGenArgsText.Text = options.Mobi.ExtraArguments ?? string.Empty;
        SelectComboItemByTag(EpubModeCombo, options.Mobi.EpubInputMode.ToString());
        var artifactValidation = options.ArtifactValidation ?? new ArtifactValidationOptions();
        ArtifactValidationCheck.IsChecked = artifactValidation.Enabled;
        SelectComboItemByTag(ValidationRetentionCombo, Math.Clamp(artifactValidation.MaxReportCount, 1, 1000).ToString(CultureInfo.InvariantCulture));
        ValidationRetentionCombo.IsEnabled = artifactValidation.Enabled;
        _applyingProfile = false;
        ClearAllDirtyTabs();
        UpdateModeDescription();
        SyncLayoutModeCombo();
        SyncVisibleLayoutControls();
        RefreshLayoutPreview();
        LoadSelectedBookMetadataFields(SelectedCoverBook());
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
            FullWidthIndentCount = SelectedFullWidthIndentCount(),
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
            Metadata = _profileMetadata,
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
                OptimizeContentPackaging = OptimizeMobiPackagingCheck.IsChecked == true,
                EnableReadingProgressSync = MobiSyncCheck.IsChecked == true,
                Asin = EmptyToNull(MobiAsinText.Text),
                ExtraArguments = EmptyToNull(KindleGenArgsText.Text),
                EpubInputMode = Enum.Parse<EpubInputMode>(((ComboBoxItem)EpubModeCombo.SelectedItem).Tag.ToString()!),
            },
            TextCleanup = _textCleanupOptions,
            ArtifactValidation = new ArtifactValidationOptions
            {
                Enabled = ArtifactValidationCheck.IsChecked == true,
                MaxReportCount = int.Parse(((ComboBoxItem)ValidationRetentionCombo.SelectedItem).Tag!.ToString()!, CultureInfo.InvariantCulture),
            },
        };
        return new ConversionProfile(
            FormatCombo.SelectedIndex == 1 ? "mobi" : "epub",
            _profileAuthor,
            int.Parse((ParallelismCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0", CultureInfo.InvariantCulture),
            EmptyToNull(_customCssSourcePath ?? string.Empty),
            options)
        {
            Mode = _conversionMode,
        };
    }

    private EasyPubAppSettings CaptureAppSettings()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        return new EasyPubAppSettings(
            EmptyToNull(OutputDirectoryText.Text),
            CaptureProfile(),
            ConversionPresets.ToArray())
        {
            UseLegacyConfig = _useLegacyConfig,
            LegacyConfigPath = _useLegacyConfig ? _legacyConfig?.SourcePath : null,
            AutoOpenTaskCenter = AutoOpenTaskCenterCheck.IsChecked == true,
            AutoOpenOutputDirectory = AutoOpenOutputDirectoryCheck.IsChecked == true,
            OutputCollisionPolicy = Enum.TryParse<OutputCollisionPolicy>((OutputCollisionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var collisionPolicy) ? collisionPolicy : OutputCollisionPolicy.AutoRename,
            KindlePreviewDeviceId = (KindleModelCombo.SelectedItem as KindleDeviceProfile)?.Id ?? "kpw6",
            CustomKindleWidth = int.TryParse(CustomKindleWidthText.Text, out var customWidth) ? customWidth : 1264,
            CustomKindleHeight = int.TryParse(CustomKindleHeightText.Text, out var customHeight) ? customHeight : 1680,
            CustomKindlePpi = int.TryParse(CustomKindlePpiText.Text, out var customPpi) ? customPpi : 300,
            ShortcutBindings = _shortcutBindings,
            Theme = _theme,
            UiDensity = _uiDensity,
            UiScalePercent = _uiScalePercent,
            RememberWindowPlacement = _rememberWindowPlacement,
            ReduceMotion = _reduceMotion,
            WindowLeft = _rememberWindowPlacement && double.IsFinite(bounds.Left) ? bounds.Left : null,
            WindowTop = _rememberWindowPlacement && double.IsFinite(bounds.Top) ? bounds.Top : null,
            WindowWidth = _rememberWindowPlacement && double.IsFinite(bounds.Width) ? bounds.Width : null,
            WindowHeight = _rememberWindowPlacement && double.IsFinite(bounds.Height) ? bounds.Height : null,
            WindowState = this.WindowState == System.Windows.WindowState.Maximized
                ? nameof(System.Windows.WindowState.Maximized)
                : nameof(System.Windows.WindowState.Normal),
        };
    }

    private async void ManagePresets_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var manager = new PresetManagerWindow(ConversionPresets, CaptureProfile()) { Owner = this };
            manager.ShowDialog();
            if (manager.AppliedPreset is not null)
            {
                ApplyProfile(manager.AppliedPreset.Profile);
                _activeConversionPlanName = manager.AppliedPreset.Name;
                UpdateWorkspaceScope();
                StatusText.Text = $"已应用转换方案：{manager.AppliedPreset.Name}";
            }
            if (manager.Changed) await _appSettingsStore.SaveAsync(CaptureAppSettings());
            if (manager.Changed && manager.AppliedPreset is null)
                StatusText.Text = $"转换方案已更新，共 {ConversionPresets.Count} 个";
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法管理转换方案", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ArtifactValidationCheck_Click(object sender, RoutedEventArgs e)
    {
        ValidationRetentionCombo.IsEnabled = ArtifactValidationCheck.IsChecked == true;
        StatusText.Text = ArtifactValidationCheck.IsChecked == true
            ? "已开启转换后结构验收；报告将保存到独立目录，默认保留最新 10 条"
            : "已关闭转换后结构验收；转换速度更快，可在出问题时重新开启";
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
        MarkRecoveryClean();
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
            MarkRecoveryClean();
            UpdateProjectTitle();
            StatusText.Text = $"已打开项目：{Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法打开项目", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MarkRecoveryClean();
            UpdateProjectTitle();
            StatusText.Text = $"项目已保存：{Path.GetFileName(_currentProjectPath)}";
            return true;
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法保存项目", MessageBoxButton.OK, MessageBoxImage.Error);
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
        var answer = InkDialog.Show(
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

    private void InputBooks_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var book in e.OldItems.OfType<InputBookItem>())
            {
                book.PropertyChanged -= InputBook_PropertyChanged;
                _trackedBooks.Remove(book);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var book in e.NewItems.OfType<InputBookItem>())
            {
                if (!_trackedBooks.Add(book)) continue;
                book.PropertyChanged += InputBook_PropertyChanged;
            }
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var book in _trackedBooks.Where(book => !InputBooks.Contains(book)).ToArray())
            {
                book.PropertyChanged -= InputBook_PropertyChanged;
                _trackedBooks.Remove(book);
            }
            foreach (var book in InputBooks.Where(book => _trackedBooks.Add(book)))
                book.PropertyChanged += InputBook_PropertyChanged;
        }

        UpdateConversionPreviewBooks(e);
        MarkProjectDirty();
    }

    private void UpdateConversionPreviewBooks(NotifyCollectionChangedEventArgs change)
    {
        if (change.Action == NotifyCollectionChangedAction.Add
            && change.NewStartingIndex >= ConversionPreviewBooks.Count
            && change.NewStartingIndex < 6
            && change.NewItems is not null)
        {
            foreach (var book in change.NewItems.OfType<InputBookItem>())
            {
                if (ConversionPreviewBooks.Count == 6) break;
                ConversionPreviewBooks.Add(book);
            }
            return;
        }

        if (change.Action == NotifyCollectionChangedAction.Add && change.NewStartingIndex >= 6)
            return;

        RefreshConversionPreviewBooks();
    }

    private void RefreshConversionPreviewBooks()
    {
        ConversionPreviewBooks.Clear();
        foreach (var book in InputBooks.Take(6)) ConversionPreviewBooks.Add(book);
    }

    private void InputBook_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InputBookItem.InputPath)
            or nameof(InputBookItem.Title)
            or nameof(InputBookItem.Author)
            or nameof(InputBookItem.CoverImagePath)
            or nameof(InputBookItem.Illustrations)
            or nameof(InputBookItem.MetadataOverrides)
            or nameof(InputBookItem.MetadataRuleFolder)
            or nameof(InputBookItem.ChapterTree))
            MarkProjectDirty();
    }

    private void MarkProjectDirty()
    {
        if (_optionTrackingReady) Interlocked.Increment(ref _projectChangeGeneration);
    }

    private void MarkRecoveryClean()
    {
        _savedRecoveryGeneration = Interlocked.Read(ref _projectChangeGeneration);
    }

    private async void RecoveryTimer_Tick(object? sender, EventArgs e) =>
        await SaveRecoveryIfChangedAsync();

    private async Task SaveRecoveryIfChangedAsync()
    {
        var generation = Interlocked.Read(ref _projectChangeGeneration);
        if (_recoverySaveInProgress || InputBooks.Count == 0 || generation == _savedRecoveryGeneration) return;
        EasyPubProjectDocument snapshot;
        try { snapshot = CaptureProjectDocument(); }
        catch { return; }
        var fingerprint = await Task.Run(() => EasyPubProjectStore.Fingerprint(snapshot));
        if (fingerprint == _lastRecoveryFingerprint)
        {
            _savedRecoveryGeneration = generation;
            return;
        }
        _recoverySaveInProgress = true;
        try
        {
            await _recoveryStore.SaveAsync(snapshot);
            _lastRecoveryFingerprint = fingerprint;
            _savedRecoveryGeneration = generation;
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
            _pendingRecovery = recovery;
            RecoveryBannerText.Text = $"{recovery.Books.Count} 本书，保存于 {recovery.UpdatedAt.LocalDateTime:g}。暂不恢复不会删除快照。";
            RecoveryBanner.Visibility = Visibility.Visible;
        }
        catch
        {
            _recoveryStore.Delete();
        }
    }

    private void RestoreRecovery_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingRecovery is not { } recovery) return;
        ApplyProjectDocument(recovery);
        _currentProjectPath = recovery.ProjectPathHint;
        _lastRecoveryFingerprint = EasyPubProjectStore.Fingerprint(recovery);
        MarkRecoveryClean();
        UpdateProjectTitle();
        RecoveryBanner.Visibility = Visibility.Collapsed;
        _pendingRecovery = null;
        StatusText.Text = $"已恢复上次工作：{recovery.Books.Count} 本小说";
    }

    private void IgnoreRecovery_Click(object sender, RoutedEventArgs e)
    {
        RecoveryBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = "已暂不恢复；快照仍保留，可在下次启动时继续恢复";
    }

    private void DeleteRecovery_Click(object sender, RoutedEventArgs e)
    {
        _recoveryStore.Delete();
        _pendingRecovery = null;
        RecoveryBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = "已删除自动恢复快照";
    }

    private void UpdateProjectTitle()
    {
        var projectName = _currentProjectPath is null ? "未保存项目" : Path.GetFileNameWithoutExtension(_currentProjectPath);
        if (ProjectMenuButton is not null) ProjectMenuButton.Content = $"当前项目：{projectName}  ⌄";
        Title = _currentProjectPath is null
            ? "EasyPub Modern v1.16"
            : $"{Path.GetFileNameWithoutExtension(_currentProjectPath)} · EasyPub Modern v1.16";
        UpdateWorkspaceScope();
    }

    private void UpdateWorkspaceScope()
    {
        if (WorkspaceScopeText is null) return;
        WorkspaceScopeText.Text = _currentProjectPath is null ? "▣  未保存" : "▣  已保存";
        WorkspaceScopeText.ToolTip = _currentProjectPath is null
            ? "当前工作尚未保存为项目"
            : $"已保存到：{_currentProjectPath}";
    }

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
            InkDialog.Show(this, exception.Message, "无法导入文件夹", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EditMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (InputBooks.Count == 0)
        {
            InkDialog.Show(this, "请先添加至少一本小说。", "EasyPub Modern");
            return;
        }
        EditMetadataBooks(InputBooks.ToArray());
    }

    private void EditSelectedMetadata_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedBooksForOperation();
        if (selected.Count == 0) return;
        EditMetadataBooks(selected);
    }

    private void EditMetadataBooks(IReadOnlyList<InputBookItem> books)
    {
        var beforeEdit = CaptureProjectDocument();
        var editor = new BatchMetadataWindow(books) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            _undoStack.Push(("批量编辑书籍信息", beforeEdit));
            MarkDirtyTab(MetadataTab);
            UpdateMetadataMappingSummary();
            UpdateSelectedBookInspector(SelectedCoverBook());
            StatusText.Text = $"已保存 {books.Count} 本小说的逐书书籍信息";
        }
    }

    private async void EditMetadataMappings_Click(object sender, RoutedEventArgs e)
    {
        var editor = new MetadataMappingWindow(_metadataMappings, InputBooks.Select(book => book.InputPath).ToArray()) { Owner = this };
        if (editor.ShowDialog() != true) return;

        try
        {
            await _metadataMappingStore.SaveAsync(editor.Rules);
            _metadataMappings = await _metadataMappingStore.LoadAsync();
            foreach (var book in InputBooks) ApplyMetadataMapping(book);
            MarkDirtyTab(MetadataTab);
            UpdateMetadataMappingSummary();
            UpdateSelectedBookInspector(SelectedCoverBook());
            UpdateStatus();
            StatusText.Text = $"已保存 {_metadataMappings.Count} 条文件夹元数据映射，并重新匹配当前书稿";
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法保存元数据映射", MessageBoxButton.OK, MessageBoxImage.Error);
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
            InkDialog.Show(this, exception.Message, "无法添加收藏", MessageBoxButton.OK, MessageBoxImage.Error);
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
            InkDialog.Show(this, exception.Message, "无法移除收藏", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddFromFavoriteFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FavoriteFolderCombo.SelectedItem is not string selected) return;
        if (!Directory.Exists(selected))
        {
            InkDialog.Show(
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

    private void UpdateTocHierarchySummary()
    {
        if (TocHierarchyStatusText is null) return;
        var hierarchyText = _tocHierarchy.Enabled ? "层级目录：一级 / 二级 / 三级" : "层级目录：关闭";
        TocHierarchyStatusText.Text = $"{hierarchyText} · 正文目录页：{(_tocHierarchy.IncludeHtmlTocPage ? "开" : "关")}";
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
            InkDialog.Show(this, "请在待转换文件中只选中一个 TXT，再打开章节编辑器。", "EasyPub Modern");
            return;
        }
        if (!string.Equals(Path.GetExtension(book.InputPath), ".txt", StringComparison.OrdinalIgnoreCase))
        {
            InkDialog.Show(this, "EPUB 会直接读取原书目录；章节树编辑目前用于 TXT 书稿。", "EasyPub Modern");
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
                document = await GetChapterDocumentAsync(
                    inputPath,
                    chapterPattern,
                    _tocHierarchy,
                    encoding,
                    book.ChapterTree);
            }
            catch (InvalidDataException exception) when (book.ChapterTree is not null)
            {
                var rebuild = InkDialog.Show(
                    this,
                    exception.Message + "\n\n是否丢弃旧章节树并按当前 TXT 重新识别？",
                    "章节树已失效",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (rebuild != MessageBoxResult.Yes) return;
                InvalidateChapterDocumentCache(inputPath);
                document = await GetChapterDocumentAsync(
                    inputPath,
                    chapterPattern,
                    _tocHierarchy,
                    encoding,
                    existingPlan: null);
            }
            var editor = new ChapterEditorWindow(document, _tocHierarchy, chapterPattern, encoding) { Owner = this };
            if (editor.ShowDialog() == true && editor.ResultPlan is not null)
            {
                book.SetChapterTree(editor.ResultPlan);
                if (editor.ResultHierarchyOptions is not null) _tocHierarchy = editor.ResultHierarchyOptions;
                ChapterRegexText.Text = editor.ResultChapterPattern ?? string.Empty;
                InvalidateChapterDocumentCache(inputPath);
                UpdateTocHierarchySummary();
                MarkDirtyTab(ChaptersTab);
                UpdateSelectedBookInspector(book);
                StatusText.Text = $"已保存《{book.DisplayName}》的章节树，共 {editor.ResultPlan.Entries.Count} 章";
                _ = RefreshInlineChapterPreviewAsync(book);
            }
            else
            {
                UpdateStatus();
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "章节分析失败";
            InkDialog.Show(this, exception.Message, "无法打开章节编辑器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void EditTextCleanup_Click(object sender, RoutedEventArgs e)
    {
        var selected = FilesList.SelectedItems.Cast<InputBookItem>().FirstOrDefault(book => !book.IsEpub)
            ?? InputBooks.FirstOrDefault(book => !book.IsEpub);
        if (selected is null)
        {
            InkDialog.Show(this, "请先添加并选择一本 TXT 小说。EPUB 输入不会使用 TXT 清理规则。", "EasyPub Modern");
            return;
        }
        try
        {
            var encoding = Enum.Parse<TextEncodingMode>(((ComboBoxItem)EncodingCombo.SelectedItem).Tag!.ToString()!);
            var window = await TextCleanupWindow.CreateAsync(selected.InputPath, encoding, _textCleanupOptions);
            window.Owner = this;
            if (window.ShowDialog() != true) return;
            _textCleanupOptions = window.Result;
            UpdateTextCleanupSummary();
            _conversionMode = ConversionMode.Custom;
            CustomModeRadio.IsChecked = true;
            StatusText.Text = _textCleanupOptions.Enabled
                ? "已保存文本清理规则；转换时只在内存中处理，不修改源 TXT"
                : "已关闭文本清理规则";
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法预览文本清理", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateTextCleanupSummary()
    {
        if (TextCleanupStatusText is null) return;
        var count = new[]
        {
            _textCleanupOptions.CollapseBlankLines,
            _textCleanupOptions.RepairHardWraps,
            _textCleanupOptions.NormalizeFullWidthSpaces,
            _textCleanupOptions.NormalizeChapterNumbers,
            _textCleanupOptions.RemoveSiteNotices,
            _textCleanupOptions.NormalizePunctuation,
            _textCleanupOptions.ChineseVariant != ChineseVariantConversion.None,
        }.Count(enabled => enabled);
        TextCleanupStatusText.Text = count == 0
            ? "使用原文，不做额外清理"
            : $"已启用 {count} 项规则 · 可预览、可撤销 · 不修改源文件";
    }

    private void ConversionMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingProfile) return;
        _applyingProfile = true;
        try
        {
            if (OriginalModeRadio.IsChecked == true)
            {
                _conversionMode = ConversionMode.OriginalCompatible;
                FontSizeText.Text = "110"; LineHeightText.Text = "120"; ParagraphSpacingText.Text = "0.6"; IndentText.Text = "0";
                PageMarginTopText.Text = "0"; PageMarginBottomText.Text = "0"; PageMarginLeftText.Text = "3"; PageMarginRightText.Text = "3";
                SelectComboItemByTag(AlignmentCombo, EasyPub.Core.TextAlignment.Default.ToString());
                FullWidthIndentCheck.IsChecked = true;
                SelectComboItemByTag(FullWidthIndentCountCombo, "2");
                StatusText.Text = "已切换到原版兼容排版；其他分页设置保持不变";
            }
            else if (ModernModeRadio.IsChecked == true)
            {
                _conversionMode = ConversionMode.ModernLayout;
                FontSizeText.Text = "105"; LineHeightText.Text = "165"; ParagraphSpacingText.Text = "0.35"; IndentText.Text = "2";
                PageMarginTopText.Text = "12"; PageMarginBottomText.Text = "12"; PageMarginLeftText.Text = "18"; PageMarginRightText.Text = "18";
                SelectComboItemByTag(AlignmentCombo, EasyPub.Core.TextAlignment.Justify.ToString());
                FullWidthIndentCheck.IsChecked = false;
                StatusText.Text = "已切换到现代排版；MOBI 仍使用同一兼容生成链路";
            }
            else
            {
                _conversionMode = ConversionMode.Custom;
                StatusText.Text = "自定义排版：保留当前值，可逐项微调";
            }
        }
        finally
        {
            _applyingProfile = false;
        }
        ClearDirtyTab(LayoutTab);
        UpdateModeDescription();
        SyncLayoutModeCombo();
        SyncVisibleLayoutControls();
        RefreshLayoutPreview();
    }

    private void LayoutModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingLayoutMode || LayoutModeCombo is null) return;
        _syncingLayoutMode = true;
        try
        {
            OriginalModeRadio.IsChecked = LayoutModeCombo.SelectedIndex == 0;
            ModernModeRadio.IsChecked = LayoutModeCombo.SelectedIndex == 1;
            CustomModeRadio.IsChecked = LayoutModeCombo.SelectedIndex == 2;
        }
        finally { _syncingLayoutMode = false; }
    }

    private void SyncLayoutModeCombo()
    {
        if (LayoutModeCombo is null) return;
        _syncingLayoutMode = true;
        LayoutModeCombo.SelectedIndex = _conversionMode switch
        {
            ConversionMode.ModernLayout => 1,
            ConversionMode.Custom => 2,
            _ => 0,
        };
        _syncingLayoutMode = false;
    }

    private void LayoutSection_Checked(object sender, RoutedEventArgs e)
    {
        if (LayoutBasePanel is null) return;
        LayoutBasePanel.Visibility = LayoutBaseNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LayoutMarginsPanel.Visibility = LayoutMarginsNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LayoutFontPanel.Visibility = LayoutFontNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LayoutChapterPanel.Visibility = LayoutChapterNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LayoutCssPanel.Visibility = LayoutCssNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LayoutIllustrationPanel.Visibility = LayoutIllustrationNav.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (LayoutMarginsNav.IsChecked == true || LayoutFontNav.IsChecked == true || LayoutChapterNav.IsChecked == true)
            SyncVisibleLayoutControls();
        if (LayoutCssNav.IsChecked == true) UpdateCssSummary();
        if (LayoutIllustrationNav.IsChecked == true) UpdateLayoutIllustrationSummary();
    }

    private void SyncVisibleLayoutControls()
    {
        if (VisibleMarginTopText is null || PageMarginTopText is null) return;
        _syncingVisibleLayout = true;
        try
        {
            VisibleMarginTopText.Text = PageMarginTopText.Text;
            VisibleMarginBottomText.Text = PageMarginBottomText.Text;
            VisibleMarginLeftText.Text = PageMarginLeftText.Text;
            VisibleMarginRightText.Text = PageMarginRightText.Text;
            VisibleAlignmentCombo.SelectedIndex = Math.Max(0, AlignmentCombo.SelectedIndex);
            VisibleEmbedFontCheck.IsChecked = EmbedFontCheck.IsChecked;
            VisibleFontPathText.Text = FontPathText.Text;
            VisibleFontFamilyText.Text = FontFamilyText.Text;
            VisibleSubsetFontCheck.IsChecked = SubsetFontCheck.IsChecked;
            VisibleFontStatusText.Text = FontStatusText.Text;
            VisibleChapterRegexText.Text = ChapterRegexText.Text;
            VisibleCssSummaryText.Text = CssSummaryText.Text;
        }
        finally
        {
            _syncingVisibleLayout = false;
        }
        UpdateLayoutIllustrationSummary();
    }

    private void VisibleMarginText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingVisibleLayout || PageMarginTopText is null) return;
        PageMarginTopText.Text = VisibleMarginTopText.Text;
        PageMarginBottomText.Text = VisibleMarginBottomText.Text;
        PageMarginLeftText.Text = VisibleMarginLeftText.Text;
        PageMarginRightText.Text = VisibleMarginRightText.Text;
        MarkVisibleLayoutChanged(LayoutTab);
    }

    private void VisibleAlignmentCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingVisibleLayout || AlignmentCombo is null) return;
        AlignmentCombo.SelectedIndex = Math.Max(0, VisibleAlignmentCombo.SelectedIndex);
        MarkVisibleLayoutChanged(LayoutTab);
    }

    private void VisibleFontSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingVisibleLayout || EmbedFontCheck is null) return;
        EmbedFontCheck.IsChecked = VisibleEmbedFontCheck.IsChecked;
        SubsetFontCheck.IsChecked = VisibleSubsetFontCheck.IsChecked;
        UpdateFontSummary();
        VisibleFontStatusText.Text = FontStatusText.Text;
        MarkVisibleLayoutChanged(FontTab);
    }

    private void VisibleFontSetting_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingVisibleLayout || FontFamilyText is null) return;
        FontFamilyText.Text = VisibleFontFamilyText.Text;
        MarkVisibleLayoutChanged(FontTab);
        RefreshLayoutPreview();
    }

    private void BrowseVisibleFont_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择允许嵌入电子书的 TrueType 字体",
            Filter = "TrueType 字体 (*.ttf)|*.ttf",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;
        _syncingVisibleLayout = true;
        try
        {
            VisibleFontPathText.Text = dialog.FileName;
            VisibleFontFamilyText.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            VisibleEmbedFontCheck.IsChecked = true;
            FontPathText.Text = VisibleFontPathText.Text;
            FontFamilyText.Text = VisibleFontFamilyText.Text;
            EmbedFontCheck.IsChecked = true;
        }
        finally { _syncingVisibleLayout = false; }
        UpdateFontSummary();
        VisibleFontStatusText.Text = FontStatusText.Text;
        MarkVisibleLayoutChanged(FontTab);
        RefreshLayoutPreview();
    }

    private void ClearVisibleFont_Click(object sender, RoutedEventArgs e)
    {
        _syncingVisibleLayout = true;
        try
        {
            VisibleEmbedFontCheck.IsChecked = false;
            VisibleFontPathText.Clear();
            VisibleFontFamilyText.Clear();
            EmbedFontCheck.IsChecked = false;
            FontPathText.Clear();
            FontFamilyText.Clear();
        }
        finally { _syncingVisibleLayout = false; }
        UpdateFontSummary();
        VisibleFontStatusText.Text = FontStatusText.Text;
        MarkVisibleLayoutChanged(FontTab);
        RefreshLayoutPreview();
    }

    private void VisibleChapterRegexText_Changed(object sender, TextChangedEventArgs e)
    {
        if (_syncingVisibleLayout || ChapterRegexText is null) return;
        ChapterRegexText.Text = VisibleChapterRegexText.Text;
        MarkVisibleLayoutChanged(AdvancedTab);
    }

    private void LayoutPreviewSetting_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _applyingProfile) return;
        SwitchToCustomModeAfterManualLayoutChange();
        MarkVisibleLayoutChanged(LayoutTab);
    }

    private void LayoutPreviewSetting_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _applyingProfile) return;
        SwitchToCustomModeAfterManualLayoutChange();
        MarkVisibleLayoutChanged(LayoutTab);
    }

    private void LayoutPreviewSetting_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _applyingProfile) return;
        SwitchToCustomModeAfterManualLayoutChange();
        MarkVisibleLayoutChanged(LayoutTab);
    }

    private void MarkVisibleLayoutChanged(TabItem tab)
    {
        if (!_applyingProfile) MarkDirtyTab(tab);
        RefreshLayoutPreview();
    }

    private void KindleModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviewDeviceFrame is null) return;
        ApplySelectedKindleModel();
        RefreshLayoutPreview();
    }

    private void ApplySelectedKindleModel()
    {
        if (PreviewDeviceFrame is null || KindleModelCombo?.SelectedItem is not KindleDeviceProfile selected) return;
        var profile = selected;
        CustomKindleSizePanel.Visibility = selected.Id == "custom" ? Visibility.Visible : Visibility.Collapsed;
        if (selected.Id == "custom" && int.TryParse(CustomKindleWidthText.Text, out var width) && int.TryParse(CustomKindleHeightText.Text, out var height) && int.TryParse(CustomKindlePpiText.Text, out var ppi))
        {
            try { profile = KindleDeviceProfiles.Custom(width, height, ppi); }
            catch (ArgumentOutOfRangeException) { return; }
        }
        PreviewDeviceFrame.Width = profile.ViewportWidth;
        PreviewDeviceFrame.Height = profile.ViewportHeight;
        PreviewDeviceStatusText.Text = $"{profile.DisplayName} · {profile.PixelWidth} × {profile.PixelHeight} · {profile.Ppi} PPI";
    }

    private void CustomKindleSize_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || KindleModelCombo.SelectedItem is not KindleDeviceProfile { Id: "custom" }) return;
        ApplySelectedKindleModel();
        RefreshLayoutPreview();
    }

    private void RefreshLayoutPreview()
    {
        if (LayoutPreviewBody is null || PreviewDeviceFrame is null) return;
        var fontPercent = ParsePreviewNumber(FontSizeText?.Text, 110);
        var linePercent = ParsePreviewNumber(LineHeightText?.Text, 120);
        var fontSize = Math.Clamp(16 * fontPercent / 100, 12, 28);
        LayoutPreviewBody.FontSize = fontSize;
        LayoutPreviewBody.LineHeight = Math.Clamp(fontSize * linePercent / 100, fontSize * 1.05, fontSize * 2.4);
        LayoutPreviewTitle.FontSize = Math.Clamp(fontSize * 1.35, 18, 34);
        var top = ParsePreviewNumber(PageMarginTopText?.Text, 0);
        var bottom = ParsePreviewNumber(PageMarginBottomText?.Text, 0);
        var left = ParsePreviewNumber(PageMarginLeftText?.Text, 3);
        var right = ParsePreviewNumber(PageMarginRightText?.Text, 3);
        PreviewDeviceFrame.Padding = new Thickness(
            Math.Clamp(24 + left, 18, 70),
            Math.Clamp(26 + top, 18, 70),
            Math.Clamp(24 + right, 18, 70),
            Math.Clamp(26 + bottom, 18, 70));
        LayoutPreviewBody.TextAlignment = AlignmentCombo?.SelectedIndex == 1 ? System.Windows.TextAlignment.Justify : System.Windows.TextAlignment.Left;
        var family = VisibleEmbedFontCheck?.IsChecked == true && !string.IsNullOrWhiteSpace(VisibleFontFamilyText?.Text)
            ? VisibleFontFamilyText.Text
            : "SimSun";
        try
        {
            LayoutPreviewBody.FontFamily = new FontFamily(family);
            LayoutPreviewTitle.FontFamily = new FontFamily(family);
        }
        catch { }
        RebuildLayoutPreviewPages();
    }

    private void RebuildLayoutPreviewPages()
    {
        if (PreviewDeviceFrame is null || LayoutPreviewBody is null) return;
        var availableWidth = PreviewDeviceFrame.Width - PreviewDeviceFrame.Padding.Left - PreviewDeviceFrame.Padding.Right;
        var availableHeight = PreviewDeviceFrame.Height - PreviewDeviceFrame.Padding.Top - PreviewDeviceFrame.Padding.Bottom;
        var fullWidthIndent = FullWidthIndentCheck?.IsChecked == true ? SelectedFullWidthIndentCount() : 0;
        var cssIndent = Math.Clamp((int)Math.Round(ParsePreviewNumber(IndentText?.Text, 0), MidpointRounding.AwayFromZero), 0, 20);
        var indent = new string('　', Math.Clamp(fullWidthIndent + cssIndent, 0, 40));
        var displayParagraphs = _layoutPreviewParagraphs.Select(paragraph => indent + paragraph).ToArray();
        _layoutPreviewPages = LayoutPreviewPaginator.Paginate(
            _layoutPreviewDocumentTitle,
            displayParagraphs,
            availableWidth,
            availableHeight,
            LayoutPreviewBody.FontSize,
            LayoutPreviewBody.LineHeight);
        _layoutPreviewPageIndex = Math.Clamp(_layoutPreviewPageIndex, 0, Math.Max(0, _layoutPreviewPages.Count - 1));
        ShowLayoutPreviewPage();
    }

    private void ShowLayoutPreviewPage()
    {
        if (_layoutPreviewPages.Count == 0 || LayoutPreviewTitle is null || LayoutPreviewBody is null) return;
        var page = _layoutPreviewPages[_layoutPreviewPageIndex];
        LayoutPreviewTitle.Text = page.Title ?? string.Empty;
        LayoutPreviewTitle.Visibility = page.Title is null ? Visibility.Collapsed : Visibility.Visible;
        LayoutPreviewBody.Text = page.Body;
        LayoutPreviewPageText.Text = $"第 {_layoutPreviewPageIndex + 1} / {_layoutPreviewPages.Count} 页";
        PreviousLayoutPreviewPageButton.IsEnabled = _layoutPreviewPageIndex > 0;
        NextLayoutPreviewPageButton.IsEnabled = _layoutPreviewPageIndex + 1 < _layoutPreviewPages.Count;
    }

    private void PreviousLayoutPreviewPage_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPreviewPageIndex <= 0) return;
        _layoutPreviewPageIndex--;
        ShowLayoutPreviewPage();
    }

    private void NextLayoutPreviewPage_Click(object sender, RoutedEventArgs e)
    {
        if (_layoutPreviewPageIndex + 1 >= _layoutPreviewPages.Count) return;
        _layoutPreviewPageIndex++;
        ShowLayoutPreviewPage();
    }

    private static double ParsePreviewNumber(string? text, double fallback) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.SelectedItems.Count == 0) return;
        PushUndo("移除所选书稿");
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
        if (InputBooks.Count == 0) return;
        PushUndo("清空书库");
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
        MarkDirtyTab(CssTab);
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
        if (VisibleCssSummaryText is not null)
        {
            VisibleCssSummaryText.Text = CssSummaryText.Text;
            VisibleCssSummaryText.ToolTip = CssSummaryText.ToolTip;
        }
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
        if (VisibleFontStatusText is not null) VisibleFontStatusText.Text = FontStatusText.Text;
    }

    private void EditIllustrations_Click(object sender, RoutedEventArgs e)
    {
        var book = SelectedCoverBook();
        if (book is null)
        {
            InkDialog.Show(this, "请只选中一本小说，再管理它的正文插图。", "EasyPub Modern");
            return;
        }
        if (book.IsEpub)
        {
            InkDialog.Show(this, "EPUB 内已有的正文图片会自动处理；手动插图位置编辑目前用于 TXT 书稿。", "EasyPub Modern");
            return;
        }

        var encoding = Enum.Parse<TextEncodingMode>(
            ((ComboBoxItem)EncodingCombo.SelectedItem).Tag.ToString()!);
        var editor = new IllustrationManagerWindow(
            book.DisplayName,
            book.InputPath,
            EmptyToNull(ChapterRegexText.Text),
            encoding,
            book.Illustrations)
        { Owner = this };
        if (editor.ShowDialog() != true) return;
        book.SetIllustrations(editor.Result);
        MarkDirtyTab(IllustrationsTab);
        UpdateSelectedBookInspector(book);
        if (SelectedBookOptionsText is not null)
            SelectedBookOptionsText.Text = $"当前小说：《{book.DisplayName}》· {book.Illustrations.Count} 张正文插图";
        UpdateStatus();
        StatusText.Text = $"已为《{book.DisplayName}》保存 {book.Illustrations.Count} 张正文插图";
        UpdateLayoutIllustrationSummary();
    }

    private async void BrowseCover_Click(object sender, RoutedEventArgs e)
    {
        var book = SelectedCoverBook();
        if (book is null)
        {
            InkDialog.Show(this, "请只选中一本小说，再为它选择封面。", "EasyPub Modern");
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

    private void OpenCoverPreview_Click(object sender, RoutedEventArgs e)
    {
        var book = SelectedCoverBook();
        if (book is null) return;
        var cover = _workspacePage == WorkspacePage.Cover ? CoverEditorImage.Source : CoverPreviewImage.Source;
        if (cover is null)
        {
            BrowseCover_Click(sender, e);
            return;
        }

        var preview = new CoverLightboxWindow(
            book.DisplayName,
            cover,
            book.CoverImagePath)
        {
            Owner = this,
        };
        preview.ShowDialog();
    }

    private async void ClearCover_Click(object sender, RoutedEventArgs e)
    {
        var book = SelectedCoverBook();
        if (book is null) return;
        book.CoverImagePath = null;
        UpdateSelectedBookInspector(book);
        StatusText.Text = $"已清除《{Path.GetFileNameWithoutExtension(book.InputPath)}》的封面";
        await RefreshCoverPreviewAsync();
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelectedBook)
        {
            _syncingSelectedBook = true;
            ChapterBookCombo.SelectedItem = FilesList.SelectedItems.Count == 1 ? FilesList.SelectedItem : null;
            if (_workspacePage != WorkspacePage.Cover)
                CoverBookCombo.SelectedItem = FilesList.SelectedItems.Count == 1 ? FilesList.SelectedItem : null;
            _syncingSelectedBook = false;
        }
        UpdateContextualControls();
        UpdateMetadataMappingSummary();
        var selectedBook = FilesList.SelectedItems.Count == 1 ? FilesList.SelectedItem as InputBookItem : null;
        var coverBook = SelectedCoverBook();
        BrowseCoverButton.IsEnabled = coverBook is not null;
        BrowseCoverButton.Content = coverBook?.CoverImagePath is null ? "选择封面" : "更换封面";
        OpenCoverPreviewButton.IsEnabled = selectedBook is not null;
        ClearCoverButton.IsEnabled = coverBook?.CoverImagePath is not null;
        if (_workspacePage != WorkspacePage.Cover) UpdateSelectedBookInspector(selectedBook);
        if (SelectedBookOptionsText is not null)
            SelectedBookOptionsText.Text = selectedBook is null
                ? "请先在上方选择一本小说"
                : $"当前小说：《{selectedBook.DisplayName}》· {selectedBook.Illustrations.Count} 张正文插图";
        _selectionPreviewCancellation?.Cancel();
        _selectionPreviewCancellation?.Dispose();
        _selectionPreviewCancellation = new CancellationTokenSource();
        _ = RefreshSelectionPreviewsAsync(
            selectedBook,
            _selectionPreviewCancellation.Token);
    }

    private async Task RefreshSelectionPreviewsAsync(InputBookItem? book, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.WhenAll(
                RefreshCoverPreviewAsync(),
                RefreshInlineChapterPreviewAsync(book, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Rapid multi-selection and brush selection intentionally supersede older previews.
        }
    }

    private void FilesList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject ?? e.Source as DependencyObject;
        if (source is null || FindVisualParent<Button>(source) is not null) return;
        var item = source as ListBoxItem
            ?? ItemsControl.ContainerFromElement(FilesList, source) as ListBoxItem
            ?? (source is null ? null : FindVisualParent<ListBoxItem>(source));
        if (item is null) return;

        var extendSelection = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        ApplyLibrarySelection(FilesList, item, extendSelection);
        if (!extendSelection)
        {
            _brushSelecting = false;
            e.Handled = true;
            return;
        }

        _brushSelecting = true;
        _brushSelectValue = item.IsSelected;
        Mouse.Capture(FilesList);
        e.Handled = true;
    }

    internal static void ApplyLibrarySelection(ListBox list, ListBoxItem item, bool extendSelection)
    {
        if (extendSelection)
        {
            item.IsSelected = !item.IsSelected;
            return;
        }

        if (item.IsSelected && list.SelectedItems.Count == 1) return;
        list.SelectedItems.Clear();
        item.IsSelected = true;
    }

    private void FilesList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_brushSelecting || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(FilesList);
        var hit = FilesList.InputHitTest(point) as DependencyObject;
        if (ItemsControl.ContainerFromElement(FilesList, hit) is ListBoxItem item) item.IsSelected = _brushSelectValue;
    }

    private void FilesList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _brushSelecting = false;
        if (Mouse.Captured == FilesList) Mouse.Capture(null);
    }

    private void FilesList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject ?? e.Source as DependencyObject;
        var item = source as ListBoxItem
            ?? ItemsControl.ContainerFromElement(FilesList, source) as ListBoxItem
            ?? (source is null ? null : FindVisualParent<ListBoxItem>(source));
        if (item is null) return;
        if (!item.IsSelected)
        {
            FilesList.SelectedItems.Clear();
            item.IsSelected = true;
        }

        var selected = SelectedBooksForOperation();
        var menu = new ContextMenu { PlacementTarget = item };
        if (selected.Count > 1)
        {
            var editMetadata = new MenuItem { Header = $"批量编辑元数据（{selected.Count} 本）" };
            editMetadata.Click += EditSelectedMetadata_Click;
            menu.Items.Add(editMetadata);
        }
        else
        {
            var book = selected[0];
            var editChapters = new MenuItem { Header = "编辑章节结构", IsEnabled = !book.IsEpub };
            editChapters.Click += EditChapters_Click;
            var editMetadata = new MenuItem { Header = "编辑封面信息" };
            editMetadata.Click += (_, _) => ShowWorkspacePage(WorkspacePage.Cover);
            var cleanup = new MenuItem { Header = "文本清理", IsEnabled = !book.IsEpub };
            cleanup.Click += EditTextCleanup_Click;
            menu.Items.Add(editChapters);
            menu.Items.Add(editMetadata);
            menu.Items.Add(cleanup);
        }

        menu.Items.Add(new Separator());
        var preflight = new MenuItem { Header = $"检查所选（{selected.Count} 本）" };
        preflight.Click += RunPreflight_Click;
        var convert = new MenuItem { Header = $"转换所选（{selected.Count} 本）" };
        convert.Click += Convert_Click;
        var remove = new MenuItem { Header = $"从书库移除（{selected.Count} 本）" };
        remove.Click += RemoveSelected_Click;
        menu.Items.Add(preflight);
        menu.Items.Add(convert);
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        item.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void SelectVisibleBooks_Click(object sender, RoutedEventArgs e)
    {
        var selectAll = sender is CheckBox checkBox && checkBox.IsChecked == true;
        if (selectAll)
            FilesList.SelectAll();
        else
            FilesList.UnselectAll();
    }

    private async Task RefreshInlineChapterPreviewAsync(InputBookItem? book, CancellationToken cancellationToken = default)
    {
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => RefreshInlineChapterPreviewAsync(book, cancellationToken)).Task.Unwrap();
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (ChapterPreviewText is null || ChapterPreviewStatsText is null || ChapterTreeSummaryText is null) return;
        if (book is null)
        {
            ClearChapterWorkspace(
                "请先选择一本 TXT 书稿。",
                "选择一本 TXT 后，可按章节查看并查找正文。",
                "选择一本 TXT 后，可识别卷、章和前置章节。");
            return;
        }

        ChapterWorkspaceFormatText.Text = book.FormatLabel;
        if (book.IsEpub)
        {
            ClearChapterWorkspace(
                "EPUB 会使用原书目录和正文结构；当前正文检查与章节树编辑用于 TXT 书稿。",
                "EPUB 目录会在转换时直接读取，无需在这里重复编辑。",
                "EPUB 使用原书目录；章节树工作台仅编辑 TXT。",
                preserveFormatLabel: true);
            SetLayoutPreviewSample("EPUB 原有版式", "　　EPUB 输入会保留其正文与图片结构；转换方式可在“转换输出”页选择。");
            return;
        }

        try
        {
            ChapterWorkspaceHintText.Text = "正在读取章节正文…";
            ChapterNavigatorList.IsEnabled = false;
            ChapterPreviewSearchText.IsEnabled = false;
            var inputPath = book.InputPath;
            var chapterRegex = string.IsNullOrWhiteSpace(ChapterRegexText.Text) ? null : ChapterRegexText.Text;
            var hierarchy = _tocHierarchy;
            var encoding = Enum.Parse<TextEncodingMode>(((ComboBoxItem)EncodingCombo.SelectedItem).Tag!.ToString()!);
            var document = await GetChapterDocumentAsync(
                inputPath,
                chapterRegex,
                hierarchy,
                encoding,
                book.ChapterTree,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var items = document.Entries
                .Select((entry, index) => new ChapterPreviewNavigationItem(entry, index))
                .ToArray();

            await Dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                _chapterPreviewDocument = document;
                ChapterNavigatorList.ItemsSource = items;
                ChapterNavigatorList.IsEnabled = items.Length > 0;
                ChapterPreviewSearchText.IsEnabled = items.Length > 0;
                ChapterTreeSummaryText.Text = book.ChapterTree is null
                    ? $"已识别 {items.Length} 项，尚未保存；可进入章节树工作台确认结构。"
                    : $"已保存 {items.Length} 项；层级、拆分、合并与目录属性统一在章节树工作台中编辑。";
                ChapterWorkspaceHintText.Text = items.Length == 0
                    ? "没有找到可显示的章节，请进入章节树工作台检查识别规则。"
                    : $"共 {items.Length} 个章节；从左侧目录定位，右侧正文保持宽阔易读。";
                ChapterNavigatorList.SelectedItem = items.FirstOrDefault(item => !item.Entry.IsFrontMatter) ?? items.FirstOrDefault();
                if (ChapterNavigatorList.SelectedItem is null)
                    ShowSelectedChapterPreview();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                _chapterPreviewDocument = null;
                ChapterNavigatorList.ItemsSource = null;
                ChapterNavigatorList.IsEnabled = false;
                ChapterPreviewSearchText.IsEnabled = false;
                ChapterPreviewTitleText.Text = "正文读取失败";
                ChapterPreviewText.Text = $"无法读取正文预览：{exception.Message}";
                ChapterPreviewStatsText.Text = "读取失败";
                ChapterPreviewSearchStatusText.Text = "无法查找";
                ChapterWorkspaceHintText.Text = "请检查源文件，或进入章节树工作台重新识别。";
            });
        }
    }

    private void ClearChapterWorkspace(
        string previewText,
        string hintText,
        string treeSummary,
        bool preserveFormatLabel = false)
    {
        _chapterPreviewDocument = null;
        ChapterNavigatorList.ItemsSource = null;
        ChapterNavigatorList.IsEnabled = false;
        ChapterPreviewSearchText.IsEnabled = false;
        ChapterPreviewTitleText.Text = "正文检查";
        ChapterPreviewText.Text = previewText;
        ChapterPreviewStatsText.Text = "0 行 / 0 字";
        ChapterPreviewSearchStatusText.Text = "输入文字即可定位";
        ChapterWorkspaceHintText.Text = hintText;
        ChapterTreeSummaryText.Text = treeSummary;
        if (!preserveFormatLabel) ChapterWorkspaceFormatText.Text = "—";
    }

    private void ChapterNavigatorList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowSelectedChapterPreview();

    private void ShowSelectedChapterPreview()
    {
        if (_chapterPreviewDocument is null || ChapterNavigatorList.SelectedItem is not ChapterPreviewNavigationItem item)
        {
            ChapterPreviewTitleText.Text = "正文检查";
            ChapterPreviewText.Text = "当前书稿没有可显示的章节。";
            ChapterPreviewStatsText.Text = "0 行 / 0 字";
            RefreshChapterSearchStatus();
            return;
        }

        var lines = _chapterPreviewDocument.GetSourceLines(item.Entry);
        var body = string.Join(Environment.NewLine, lines.Select(line => line.Text));
        ChapterPreviewTitleText.Text = item.Entry.Title;
        ChapterPreviewText.Text = string.IsNullOrWhiteSpace(body) ? "本章没有正文内容。" : body;
        var characterCount = lines.Sum(line => line.Text.Length);
        var firstLine = lines.Count == 0 ? item.Entry.TitleLineNumber : lines[0].LineNumber;
        var lastLine = lines.Count == 0 ? item.Entry.TitleLineNumber : lines[^1].LineNumber;
        var range = firstLine.HasValue && lastLine.HasValue ? $" · 源文件第 {firstLine}–{lastLine} 行" : string.Empty;
        ChapterPreviewStatsText.Text = $"{lines.Count:N0} 行 / {characterCount:N0} 字{range}";
        ChapterPreviewText.Select(0, 0);
        ChapterPreviewText.ScrollToHome();
        RefreshChapterSearchStatus();
        SetLayoutPreviewSample(item.Entry.Title, string.IsNullOrWhiteSpace(body) ? "本章没有正文内容。" : body);
    }

    private void ChapterPreviewSearchText_Changed(object sender, TextChangedEventArgs e) => RefreshChapterSearchStatus();

    private void ChapterPreviewSearchText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        FindInChapterPreview(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
        e.Handled = true;
    }

    private void ChapterSearchPrevious_Click(object sender, RoutedEventArgs e) => FindInChapterPreview(forward: false);
    private void ChapterSearchNext_Click(object sender, RoutedEventArgs e) => FindInChapterPreview(forward: true);

    private void RefreshChapterSearchStatus()
    {
        if (ChapterPreviewSearchStatusText is null || ChapterPreviewSearchText is null || ChapterPreviewText is null) return;
        var query = ChapterPreviewSearchText.Text;
        if (string.IsNullOrEmpty(query))
        {
            ChapterPreviewSearchStatusText.Text = "输入文字即可定位";
            return;
        }

        var positions = FindAllOccurrences(ChapterPreviewText.Text, query);
        ChapterPreviewSearchStatusText.Text = positions.Count == 0 ? "当前章节无匹配" : $"共 {positions.Count} 处";
    }

    private void FindInChapterPreview(bool forward)
    {
        var query = ChapterPreviewSearchText.Text;
        if (string.IsNullOrEmpty(query))
        {
            ChapterPreviewSearchText.Focus();
            ChapterPreviewSearchStatusText.Text = "请先输入查找内容";
            return;
        }

        var positions = FindAllOccurrences(ChapterPreviewText.Text, query);
        if (positions.Count == 0)
        {
            ChapterPreviewSearchStatusText.Text = "当前章节无匹配";
            return;
        }

        var current = ChapterPreviewText.SelectionStart;
        var forwardStart = current + ChapterPreviewText.SelectionLength;
        var target = forward
            ? positions.FirstOrDefault(position => position >= forwardStart, positions[0])
            : positions.LastOrDefault(position => position < current, positions[^1]);
        ChapterPreviewText.Focus();
        ChapterPreviewText.Select(target, query.Length);
        var line = ChapterPreviewText.GetLineIndexFromCharacterIndex(target);
        if (line >= 0) ChapterPreviewText.ScrollToLine(line);
        ChapterPreviewSearchStatusText.Text = $"第 {positions.IndexOf(target) + 1} / {positions.Count} 处";
    }

    private static List<int> FindAllOccurrences(string text, string query)
    {
        var positions = new List<int>();
        var offset = 0;
        while (offset <= text.Length - query.Length)
        {
            var index = text.IndexOf(query, offset, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) break;
            positions.Add(index);
            offset = index + Math.Max(1, query.Length);
        }
        return positions;
    }

    private void SetLayoutPreviewSample(string title, string body)
    {
        if (LayoutPreviewTitle is null || LayoutPreviewBody is null) return;
        _layoutPreviewDocumentTitle = title;
        _layoutPreviewParagraphs = body.Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToArray();
        _layoutPreviewPageIndex = 0;
        RefreshLayoutPreview();
    }

    private static bool LooksLikeChapterHeading(string text)
    {
        if (text.Length is < 2 or > 80) return false;
        return text.Contains("序章", StringComparison.Ordinal)
            || text.Contains("楔子", StringComparison.Ordinal)
            || (text.StartsWith("第", StringComparison.Ordinal) &&
                (text.Contains('章') || text.Contains('节') || text.Contains('卷') || text.Contains('回')));
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
            InkDialog.Show(this, "请只选中一本小说，并拖入一个 JPG、PNG 或 WebP 图片。", "EasyPub Modern");
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
            UpdateSelectedBookInspector(book);
            StatusText.Text = $"已为《{Path.GetFileNameWithoutExtension(book.InputPath)}》设置封面：{Path.GetFileName(coverPath)}";
            if (ReferenceEquals(SelectedCoverBook(), book))
                ShowCoverPreview(book, prepared);
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法读取封面", MessageBoxButton.OK, MessageBoxImage.Error);
            await RefreshCoverPreviewAsync();
        }
    }

    private async Task RefreshCoverPreviewAsync()
    {
        var version = ++_coverPreviewVersion;
        var book = SelectedCoverBook();
        BrowseCoverButton.IsEnabled = book is not null;
        BrowseCoverButton.Content = book?.CoverImagePath is null ? "选择封面" : "更换封面";
        OpenCoverPreviewButton.IsEnabled = book is not null;
        ClearCoverButton.IsEnabled = book?.CoverImagePath is not null;
        CoverPreviewImage.Source = null;
        CoverPreviewImage.ToolTip = null;
        CoverPlaceholderText.Visibility = Visibility.Visible;
        CoverDiagnosticsText.Text = "JPG / PNG / WebP";

        if (book is null)
        {
            UpdateSelectedBookInspector(null);
            if (SelectedBookOptionsText is not null)
                SelectedBookOptionsText.Text = FilesList.SelectedItems.Count > 1
                    ? $"当前选中 {FilesList.SelectedItems.Count} 本；插图设置需要单独选择一本"
                    : "请先在上方选择一本小说";
            CoverPlaceholderText.Text = FilesList.SelectedItems.Count > 1
                ? "请只选择一本小说"
                : "选择一本小说后，将图片拖到这里";
            return;
        }
        UpdateSelectedBookInspector(book);
        if (SelectedBookOptionsText is not null)
            SelectedBookOptionsText.Text = $"当前小说：《{book.DisplayName}》· {book.Illustrations.Count} 张正文插图";
        if (string.IsNullOrWhiteSpace(book.CoverImagePath))
        {
            CoverPlaceholderText.Text = "拖入封面\n或点击选择";
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

    private void UpdateSelectedBookInspector(InputBookItem? book)
    {
        if (book is null)
        {
            SelectedBookNameText.Text = "所选小说概览";
            SelectedBookNameText.ToolTip = null;
            SelectedBookFormatText.Text = "—";
            SelectedBookSummaryText.Text = "请只选择一本小说查看封面、元数据、插图和章节树状态";
            LoadSelectedBookMetadataFields(null);
            UpdateLayoutIllustrationSummary();
            return;
        }

        SelectedBookNameText.Text = book.DisplayName;
        SelectedBookNameText.ToolTip = book.InputPath;
        SelectedBookFormatText.Text = book.FormatLabel;
        SelectedBookSummaryText.Text = $"封面：{(book.CoverImagePath is null ? "无" : "有")} · 元数据：{(book.MetadataOverrides.IsEmpty ? "无" : "有")} · 插图：{book.Illustrations.Count} · 章节树：{(book.ChapterTree is null ? "无" : book.ChapterTree.Entries.Count + " 项")}";
        LoadSelectedBookMetadataFields(book);
        UpdateLayoutIllustrationSummary();
    }

    private void LoadSelectedBookMetadataFields(InputBookItem? book)
    {
        if (AuthorText is null) return;
        _syncingSelectedMetadata = true;
        try
        {
            var metadata = book?.MetadataOverrides;
            AuthorText.Text = book?.Author ?? metadata?.Author ?? _profileAuthor ?? string.Empty;
            TranslatorText.Text = metadata?.Translator ?? _profileMetadata.Translator ?? string.Empty;
            IsbnText.Text = metadata?.Isbn ?? _profileMetadata.Isbn ?? string.Empty;
            PublicationDatePicker.SelectedDate = (metadata?.PublicationDate ?? _profileMetadata.PublicationDate)?.ToDateTime(TimeOnly.MinValue);
            PublisherText.Text = metadata?.Publisher ?? _profileMetadata.Publisher ?? string.Empty;
            CategoryCombo.Text = metadata?.Category ?? _profileMetadata.Category ?? string.Empty;
            LanguageCombo.Text = metadata?.Language ?? _profileMetadata.Language ?? "zh-CN";
            DescriptionText.Text = metadata?.Description ?? _profileMetadata.Description ?? string.Empty;
        }
        finally
        {
            _syncingSelectedMetadata = false;
        }
    }

    private void SelectedMetadataField_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSelectedMetadata || _applyingProfile || SelectedCoverBook() is not { } book) return;
        book.Author = EmptyToNull(AuthorText.Text);
        book.SetMetadataOverrides(new BookMetadataOverrides
        {
            Translator = EmptyToNull(TranslatorText.Text),
            Isbn = EmptyToNull(IsbnText.Text),
            PublicationDate = PublicationDatePicker.SelectedDate is DateTime date ? DateOnly.FromDateTime(date) : null,
            Publisher = EmptyToNull(PublisherText.Text),
            Category = EmptyToNull(CategoryCombo.Text),
            Language = EmptyToNull(LanguageCombo.Text),
            Description = EmptyToNull(DescriptionText.Text),
        }, null);
        UpdateMetadataMappingSummary();
        UpdateSelectedBookInspectorSummaryOnly(book);
    }

    private void UpdateSelectedBookInspectorSummaryOnly(InputBookItem book)
    {
        SelectedBookSummaryText.Text = $"封面：{(book.CoverImagePath is null ? "无" : "有")} · 元数据：{(book.MetadataOverrides.IsEmpty ? "无" : "有")} · 插图：{book.Illustrations.Count} · 章节树：{(book.ChapterTree is null ? "无" : book.ChapterTree.Entries.Count + " 项")}";
    }

    private void UpdateLayoutIllustrationSummary()
    {
        if (VisibleIllustrationSummaryText is null || InkManageIllustrationsButton is null) return;
        var book = SelectedCoverBook();
        InkManageIllustrationsButton.IsEnabled = book is { IsEpub: false };
        VisibleIllustrationSummaryText.Text = book switch
        {
            null when FilesList?.SelectedItems.Count > 1 => "当前选中了多本书。插图位置属于单本正文，请只选择一本 TXT。",
            null => "请先在书库中只选中一本 TXT 书稿。",
            { IsEpub: true } => "EPUB 会保留并处理书内原有图片；手动定位插图目前用于 TXT。",
            _ => $"《{book.DisplayName}》当前有 {book.Illustrations.Count} 张正文插图。点击下方按钮可选择图片并定位到正文段落。",
        };
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
        book.SetCoverThumbnail(bitmap);
        CoverPreviewImage.ToolTip = $"{book.CoverImagePath}\n{prepared.PixelWidth} × {prepared.PixelHeight} · {prepared.SourceFormat}"
            + (prepared.WasConverted ? " → JPG" : string.Empty);
        try { CoverDiagnosticsText.Text = ImageDiagnostics.Inspect(book.CoverImagePath!, cover: true).Summary; }
        catch (Exception exception) { CoverDiagnosticsText.Text = $"图片诊断失败：{exception.Message}"; }
        CoverPlaceholderText.Visibility = Visibility.Collapsed;
        ClearCoverButton.IsEnabled = true;
        BrowseCoverButton.Content = "更换封面";
    }

    private InputBookItem? SelectedCoverBook()
    {
        if (_workspacePage == WorkspacePage.Cover && CoverBookCombo?.SelectedItem is InputBookItem coverBook)
            return coverBook;
        return FilesList.SelectedItems.Count == 1 ? FilesList.SelectedItem as InputBookItem : null;
    }

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

    private void FormatCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConvertFormatSummaryText is not null)
            ConvertFormatSummaryText.Text = FormatCombo.SelectedIndex == 0 ? "EPUB" : "MOBI";
        if (StatusText is not null && FormatCombo.SelectedIndex == 1 && InputBooks.Count == 0)
            StatusText.Text = "已选择 MOBI；转换引擎状态可在“转换输出”页查看";
        if (StatusText is not null && FormatCombo?.SelectedIndex == 0 && InputBooks.Any(book => book.IsEpub))
            StatusText.Text = "当前含 EPUB 输入；EPUB 输入只能转换为 MOBI";
        if (IsInitialized) UpdateContextualControls();
    }

    private void EpubModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _applyingProfile || MobiTab is null) return;
        MarkDirtyTab(MobiTab);
        StatusText.Text = EpubModeCombo.SelectedIndex == 0
            ? "EPUB 转 MOBI：保留原 EPUB 版式"
            : "EPUB 转 MOBI：使用 EasyPub 兼容重排";
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
            InkDialog.Show(this, "请只选中一本小说，再打开近似版式预览。", "EasyPub Modern");
            return;
        }
        if (book.IsEpub)
        {
            InkDialog.Show(this, "EPUB 输入可直接转换为 MOBI；当前近似版式预览用于 TXT 重排结果。", "EasyPub Modern");
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
            StatusText.Text = $"近似版式预览已生成：《{book.DisplayName}》；Kindle 真机效果仍需实际确认";
            new BookPreviewWindow(book.Title ?? book.DisplayName, package) { Owner = this }.ShowDialog();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消生成预览";
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法生成近似版式预览", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CancelButton.IsEnabled = false;
            ConvertButton.IsEnabled = FilesList.SelectedItems.Count > 0;
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
            var (report, reused) = await GetPreflightReportAsync(requests, _operationCancellation.Token);
            _lastPreflightReport = report;
            ApplyPreflightToWorklist(report);
            _taskCenterWindow?.UpdatePreflight(report);
            new PreflightWindow(report, allowContinue: false, NavigateToPreflightIssue) { Owner = this }.ShowDialog();
            StatusText.Text = report.HasErrors
                ? $"检查完成：发现 {report.Issues.Count(issue => issue.Severity == PreflightSeverity.Error)} 个错误"
                : reused
                    ? $"检查结果未变化，已直接复用：{report.Books.Count} 本可转换，{report.WarningCount} 个提醒"
                    : $"检查完成：{report.Books.Count} 本可转换，{report.WarningCount} 个提醒";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消转换前检查";
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法执行转换前检查", MessageBoxButton.OK, MessageBoxImage.Error);
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
                    issue.RelatedValue)
                { Owner = this };
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
                InkDialog.Show(this, "还没有转换历史。", "EasyPub Modern");
                return;
            }
            var window = new ConversionHistoryWindow(entries) { Owner = this };
            if (window.ShowDialog() == true) LoadRetryInputs(window.RetryInputPaths);
        }
        catch (Exception exception)
        {
            InkDialog.Show(this, exception.Message, "无法加载转换历史", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ShowTaskCenter_Click(object sender, RoutedEventArgs e) => await ShowTaskCenterAsync();

    private async Task ShowTaskCenterAsync()
    {
        if (_taskCenterWindow is { IsVisible: true })
        {
            _taskCenterWindow.Activate();
            return;
        }
        IReadOnlyList<ConversionHistoryEntry> history;
        try
        {
            history = await _historyStore.LoadAsync();
        }
        catch (Exception exception)
        {
            history = [];
            StatusText.Text = $"任务中心已打开，但历史加载失败：{exception.Message}";
        }
        _taskCenterWindow = new TaskCenterWindow(BookTasks, history, _lastPreflightReport) { Owner = this };
        _taskCenterWindow.RetryRequested += path =>
        {
            var original = InputBooks.FirstOrDefault(book => string.Equals(book.InputPath, path, StringComparison.OrdinalIgnoreCase));
            InputBooks.Clear();
            if (original is not null) InputBooks.Add(original.Clone()); else AddFiles([path]);
            FilesList.SelectedItem = InputBooks.FirstOrDefault();
            UpdateStatus();
            StatusText.Text = "已载入所选任务，可调整设置后重新转换";
        };
        _taskCenterWindow.RetryHistoryRequested += LoadRetryInputs;
        _taskCenterWindow.PreflightRequested += () => RunPreflight_Click(this, new RoutedEventArgs());
        _taskCenterWindow.NavigatePreflightRequested += issue =>
        {
            NavigateToPreflightIssue(issue);
            _taskCenterWindow?.Close();
        };
        _taskCenterWindow.Show();
    }

    private void ShowTaskCenterWhenRequested()
    {
        if (AutoOpenTaskCenterCheck.IsChecked == true) _ = ShowTaskCenterAsync();
    }

    private async Task<(ConversionPreflightReport Report, bool Reused)> GetPreflightReportAsync(
        IReadOnlyList<ConversionRequest> requests,
        CancellationToken cancellationToken)
        => await _preflightCache.InspectAsync(requests, cancellationToken);

    private void InitializeBookTasks(IReadOnlyList<ConversionRequest> requests)
    {
        BookTasks.Clear();
        _bookTasksByInputPath.Clear();
        foreach (var request in requests)
        {
            var task = new BookTaskViewModel(request.InputPath, request.OutputPath);
            BookTasks.Add(task);
            _bookTasksByInputPath[request.InputPath] = task;
        }
        _taskCenterWindow?.RefreshSummary();
    }

    private BookTaskViewModel? FindBookTask(string? inputPath) =>
        inputPath is not null && _bookTasksByInputPath.TryGetValue(inputPath, out var task) ? task : null;

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
        PauseButton.IsEnabled = true;
        PauseButton.Content = "暂停";
        _batchExecutionControl = new BatchExecutionControl();
        Progress.IsIndeterminate = false;
        Progress.Value = 0;

        try
        {
            StatusText.Text = "正在执行转换前检查…";
            var requests = await BuildConversionRequestsAsync();
            InitializeBookTasks(requests);
            foreach (var task in BookTasks) task.Update(BookTaskStage.Checking, 0.02, "正在检查");
            var (report, reusedPreflight) = await GetPreflightReportAsync(requests, cancellationToken);
            _lastPreflightReport = report;
            ApplyPreflightToWorklist(report);
            _taskCenterWindow?.UpdatePreflight(report);
            if (reusedPreflight) StatusText.Text = "输入和选项未变化，已复用上次转换前检查结果";
            if (report.HasErrors)
            {
                var issueIndex = PreflightIssueIndex.Create(report.Issues);
                foreach (var task in BookTasks)
                {
                    var issues = issueIndex.For(task.InputPath);
                    if (issues.Any(issue => issue.Severity == PreflightSeverity.Error))
                    {
                        task.SetFailure("转换前检查未通过：" + string.Join("；", issues.Select(issue => issue.Message)));
                    }
                    else task.Update(BookTaskStage.Waiting, 0, "等待修正其他错误");
                }
                ShowTaskCenterWhenRequested();
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
            ShowTaskCenterWhenRequested();
            StatusText.Text = $"正在转换 {requests.Count} 本小说…";
            var parallelism = int.Parse((ParallelismCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0", CultureInfo.InvariantCulture);
            using var batchProgress = new DispatcherThrottledProgress<BatchConversionProgress>(
                Dispatcher,
                TimeSpan.FromMilliseconds(100),
                value =>
            {
                Progress.Value = value.Fraction;
                var name = string.IsNullOrWhiteSpace(value.CurrentInputPath)
                    ? string.Empty
                    : $" · {Path.GetFileName(value.CurrentInputPath)}";
                StatusText.Text = $"{value.Stage}{name} · 已完成 {value.CompletedCount}/{value.TotalCount} · 失败 {value.FailedCount} · 取消 {value.CancelledCount}";
                FindBookTask(value.CurrentInputPath)?.Update(value.ItemStage, value.ItemFraction, value.Stage, value.Validation);
                _taskCenterWindow?.RefreshSummary();
            });
            var outcomes = await new BatchConverter(new EasyPubConverter())
                .ConvertWithReportAsync(requests, parallelism, batchProgress, cancellationToken, _batchExecutionControl);
            batchProgress.Flush();
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
                _taskCenterWindow?.UpdateHistory(await _historyStore.LoadAsync());
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
            foreach (var outcome in outcomes)
            {
                var task = FindBookTask(outcome.Request.InputPath);
                if (task is null) continue;
                if (!outcome.Succeeded) task.SetFailure(outcome.ErrorMessage ?? (outcome.Cancelled ? "已取消" : "转换失败"), outcome.Cancelled);
                else if (outcome.Validation is { } validation)
                    task.Update(validation.StructurePassed && validation.WarningCount == 0 ? BookTaskStage.Completed : BookTaskStage.Warning, 1, validation.ResultLabel, validation);
                else
                    task.Update(BookTaskStage.Completed, 1, "转换完成（未启用结构验收）");
            }
            _taskCenterWindow?.RefreshSummary();
            StatusText.Text = $"已完成：成功 {successes.Length} 本，失败 {failures.Length - cancelled} 本，取消 {cancelled} 本，合计 {totalBytes / 1024d:F1} KB";

            if (successes.Length > 0 && AutoOpenOutputDirectoryCheck.IsChecked == true)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", OutputDirectoryText.Text.Trim()) { UseShellExecute = true });

            if (failures.Length > 0)
            {
                var historyWindow = new ConversionHistoryWindow(historyEntries) { Owner = this };
                if (historyWindow.ShowDialog() == true) LoadRetryInputs(historyWindow.RetryInputPaths);
            }
            else
            {
                var validated = outcomes.Count(outcome => outcome.Validation is not null);
                InkDialog.Show(this,
                    validated == 0
                        ? $"批量转换完成。结构验收未启用。\n输出目录：{OutputDirectoryText.Text.Trim()}"
                        : $"批量转换与结构验收完成（{validated} 本）。\n报告目录：{Path.Combine(OutputDirectoryText.Text.Trim(), ArtifactValidationService.ReportDirectoryName)}",
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
            InkDialog.Show(this, exception.Message, "EasyPub Modern", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Progress.Value = 0;
            CancelButton.IsEnabled = false;
            PauseButton.IsEnabled = false;
            PauseButton.Content = "暂停";
            _batchExecutionControl = null;
            ConvertButton.IsEnabled = FilesList.SelectedItems.Count > 0;
        }
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_batchExecutionControl is null) return;
        if (_batchExecutionControl.IsPaused)
        {
            _batchExecutionControl.Resume();
            PauseButton.Content = "暂停";
            StatusText.Text = "转换已继续";
        }
        else
        {
            _batchExecutionControl.Pause();
            PauseButton.Content = "继续";
            StatusText.Text = "已暂停派发新任务；正在运行的转换会安全完成";
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
        var operationBooks = SelectedBooksForOperation();
        if (operationBooks.Count == 0) throw new InvalidOperationException("请先在书库中选择至少一本要转换的书稿。");
        var outputDirectory = OutputDirectoryText.Text.Trim();
        if (outputDirectory.Length == 0) throw new InvalidOperationException("请选择输出目录。");

        var profile = CaptureProfile();
        if (!string.Equals(profile.OutputFormat, "mobi", StringComparison.OrdinalIgnoreCase)
            && operationBooks.Any(book => book.IsEpub))
            throw new InvalidOperationException("EPUB 输入只能输出 MOBI。请把输出格式切换为 MOBI。");
        var options = profile.Options;
        if (string.IsNullOrWhiteSpace(options.AdditionalCss) &&
            !string.IsNullOrWhiteSpace(profile.AdditionalCssFilePath))
            options = options with { AdditionalCss = await File.ReadAllTextAsync(profile.AdditionalCssFilePath) };
        var requests = BatchConversionRequestFactory.Create(
            operationBooks.Select(book => new BookConversionSource(
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
        var collisionPolicy = Enum.TryParse<OutputCollisionPolicy>((OutputCollisionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString(), out var parsedPolicy)
            ? parsedPolicy : OutputCollisionPolicy.AutoRename;
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ConversionRequest>(requests.Count);
        var skipped = 0;
        foreach (var request in requests)
        {
            var decision = OutputPathPolicy.Resolve(request.OutputPath, collisionPolicy, reserved);
            if (decision.Skipped) { skipped++; continue; }
            resolved.Add(request with { OutputPath = decision.Path! });
        }
        if (resolved.Count == 0) throw new InvalidOperationException(skipped > 0 ? "所有输出文件都已存在，已按设置跳过。" : "没有可转换的书稿。");
        if (skipped > 0) StatusText.Text = $"已按同名文件策略跳过 {skipped} 本";
        return resolved;
    }

    private IReadOnlyList<InputBookItem> SelectedBooksForOperation() =>
        FilesList?.SelectedItems.Cast<InputBookItem>().ToArray() ?? [];

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

    private async Task<ChapterTreeDocument> GetChapterDocumentAsync(
        string inputPath,
        string? chapterPattern,
        TocHierarchyOptions hierarchy,
        TextEncodingMode encoding,
        ChapterTreePlan? existingPlan,
        CancellationToken cancellationToken = default)
    {
        var source = new FileInfo(Path.GetFullPath(inputPath));
        var key = new ChapterDocumentCacheKey(
            source.FullName.ToUpperInvariant(),
            source.Length,
            source.LastWriteTimeUtc.Ticks,
            chapterPattern ?? string.Empty,
            hierarchy.Enabled,
            hierarchy.Level1Pattern,
            hierarchy.Level2Pattern,
            hierarchy.Level3Pattern,
            encoding,
            existingPlan?.SourceSha256 ?? string.Empty,
            existingPlan?.Entries.Count ?? 0);
        Task<ChapterTreeDocument> task;
        lock (_chapterDocumentCacheGate)
        {
            foreach (var staleKey in _chapterDocumentCache.Keys
                         .Where(candidate => candidate.SourcePath == key.SourcePath && candidate != key)
                         .ToArray())
                _chapterDocumentCache.Remove(staleKey);
            if (!_chapterDocumentCache.TryGetValue(key, out task!))
            {
                task = Task.Run(() => ChapterTreeDocument.LoadAsync(
                    source.FullName,
                    chapterPattern,
                    hierarchy,
                    encoding,
                    existingPlan,
                    CancellationToken.None));
                _chapterDocumentCache[key] = task;
                while (_chapterDocumentCache.Count > 2)
                {
                    var oldestOtherKey = _chapterDocumentCache.Keys.First(candidate => candidate != key);
                    _chapterDocumentCache.Remove(oldestOtherKey);
                }
            }
        }

        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch
        {
            if (task.IsCanceled || task.IsFaulted)
            {
                lock (_chapterDocumentCacheGate)
                    if (_chapterDocumentCache.TryGetValue(key, out var cached) && ReferenceEquals(cached, task))
                        _chapterDocumentCache.Remove(key);
            }
            throw;
        }
    }

    private void InvalidateChapterDocumentCache(string inputPath)
    {
        var fullPath = Path.GetFullPath(inputPath).ToUpperInvariant();
        lock (_chapterDocumentCacheGate)
            foreach (var key in _chapterDocumentCache.Keys.Where(candidate => candidate.SourcePath == fullPath).ToArray())
                _chapterDocumentCache.Remove(key);
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
        var selected = _workspacePage == WorkspacePage.Cover
            ? SelectedCoverBook()
            : selectedBooks.Length == 1 ? selectedBooks[0] : null;
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
        var epubCount = 0;
        var chapterTreeCount = 0;
        foreach (var book in InputBooks)
        {
            if (book.IsEpub) epubCount++;
            if (book.ChapterTree is not null) chapterTreeCount++;
        }
        FileCountText.Text = $"{count} 本";
        EmptyFilesHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateContextualControls();
        StatusText.Text = count == 0
            ? "准备就绪，可添加或拖入多个 TXT / EPUB 文件"
            : $"已添加 {count} 本 · TXT {count - epubCount} · EPUB {epubCount} · {chapterTreeCount} 本有章节树";
        if (LibrarySelectionSummaryText is not null)
            LibrarySelectionSummaryText.Text = FilesList.SelectedItems.Count == 0
                ? $"共 {count} 本书稿"
                : $"已选择 {FilesList.SelectedItems.Count} 本，共 {count} 本书稿";
    }

    private void ScheduleStatusUpdate()
    {
        _statusRefreshTimer.Stop();
        _statusRefreshTimer.Start();
    }

    private void BookWorklistFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, BookSearchText))
        {
            _bookFilterTimer.Stop();
            _bookFilterTimer.Start();
            return;
        }
        ApplyBookWorklistFilter();
    }

    private void ApplyBookWorklistFilter()
    {
        if (_bookWorklistView is null || BookFilterCombo is null || BookSortCombo is null) return;
        _bookWorklistView.SortDescriptions.Clear();
        var sort = (BookSortCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        if (sort == "Name") _bookWorklistView.SortDescriptions.Add(new SortDescription(nameof(InputBookItem.DisplayName), ListSortDirection.Ascending));
        else if (sort == "Issues") _bookWorklistView.SortDescriptions.Add(new SortDescription(nameof(InputBookItem.ReadinessPriority), ListSortDirection.Descending));
        _bookWorklistView.Refresh();
    }

    private bool FilterBookWorklist(object value)
    {
        if (value is not InputBookItem book) return false;
        var search = BookSearchText?.Text.Trim() ?? string.Empty;
        if (search.Length > 0 && !book.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase) && !book.InputPath.Contains(search, StringComparison.CurrentCultureIgnoreCase)) return false;
        var filter = (BookFilterCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        return filter switch
        {
            "Issues" => book.HasPreflightIssues,
            "Unchecked" => !book.HasBeenChecked,
            "NoCover" => book.CoverImagePath is null,
            "Txt" => !book.IsEpub,
            "Epub" => book.IsEpub,
            _ => true,
        };
    }

    private void ApplyPreflightToWorklist(ConversionPreflightReport report)
    {
        var issueIndex = PreflightIssueIndex.Create(report.Issues);
        foreach (var book in InputBooks)
        {
            var issues = issueIndex.For(book.InputPath);
            book.SetPreflightResult(
                issues.Count(issue => issue.Severity == PreflightSeverity.Error),
                issues.Count(issue => issue.Severity == PreflightSeverity.Warning));
        }
        _bookWorklistView?.Refresh();
    }

    private void UpdateContextualControls()
    {
        if (FilesList is null) return;
        var count = InputBooks.Count;
        var selectedCount = FilesList.SelectedItems.Count;
        var singleBook = selectedCount == 1 ? FilesList.SelectedItem as InputBookItem : null;
        if (LibrarySelectionSummaryText is not null)
            LibrarySelectionSummaryText.Text = selectedCount == 0
                ? $"共 {count} 本书稿"
                : $"已选择 {selectedCount} 本，共 {count} 本书稿";
        SelectAllFilesButton.IsEnabled = count > 0 && selectedCount < count;
        RemoveSelectedButton.IsEnabled = selectedCount > 0;
        ClearFilesButton.IsEnabled = count > 0;
        RunPreflightButton.IsEnabled = selectedCount > 0;
        ConvertButton.IsEnabled = selectedCount > 0 && !CancelButton.IsEnabled;
        ConvertButton.Content = selectedCount == 0
            ? (_compactLayout ? "开始转换" : "请先选择书稿")
            : (_compactLayout ? $"转换 {selectedCount} 本" : $"开始转换所选 {selectedCount} 本");
        ConvertButton.ToolTip = selectedCount == 0 ? "请先在书库选择至少一本书稿" : $"仅转换当前选中的 {selectedCount} 本书稿";
        AutomationProperties.SetName(ConvertButton, selectedCount == 0 ? "开始转换，当前没有选择书稿" : $"开始转换所选 {selectedCount} 本书稿");
        var allVisibleSelected = count > 0 && selectedCount == FilesList.Items.Count;
        if (SelectVisibleBooksCheckBox is not null) SelectVisibleBooksCheckBox.IsChecked = allVisibleSelected;
        if (SelectAllHeaderCheckBox is not null) SelectAllHeaderCheckBox.IsChecked = allVisibleSelected;
        if (ConversionSelectionList is not null)
            ConversionSelectionList.ItemsSource = SelectedBooksForOperation();
        if (EpubInputModePanel is not null)
        {
            var hasSelectedEpub = FilesList.SelectedItems.Cast<InputBookItem>().Any(book => book.IsEpub);
            EpubInputModePanel.Visibility = FormatCombo.SelectedIndex == 1 && hasSelectedEpub
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        PreviewBookButton.IsEnabled = singleBook is { IsEpub: false };
        QuickChapterButton.IsEnabled = singleBook is { IsEpub: false };
        QuickCleanupButton.IsEnabled = singleBook is { IsEpub: false };
        QuickMetadataButton.IsEnabled = singleBook is not null;
        QuickIllustrationButton.IsEnabled = singleBook is { IsEpub: false };
        QuickPreviewButton.IsEnabled = singleBook is { IsEpub: false };
        var showCover = singleBook is not null && _workspacePage is WorkspacePage.Library or WorkspacePage.Cover;
        CoverDropPanel.Visibility = showCover ? Visibility.Visible : Visibility.Collapsed;
        CoverGapColumn.Width = new GridLength(showCover ? 14 : 0);
        CoverColumn.Width = new GridLength(showCover ? (_compactLayout ? 270 : 330) : 0);
        UpdateLayoutIllustrationSummary();
    }

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingProfile || PresetCombo.SelectedItem is not NamedConversionPreset preset) return;
        ApplyProfile(preset.Profile);
        _activeConversionPlanName = preset.Name;
        UpdateWorkspaceScope();
        StatusText.Text = $"已应用转换方案：{preset.Name}";
    }

    private void InitializeOptionTracking()
    {
        _optionTabNames.Clear();
        _optionTabNames[ChaptersTab] = "章节";
        _optionTabNames[LayoutTab] = "版式";
        _optionTabNames[FontTab] = "字体";
        _optionTabNames[MetadataTab] = "书籍信息";
        _optionTabNames[CssTab] = "定制 CSS";
        _optionTabNames[IllustrationsTab] = "插图";
        _optionTabNames[MobiTab] = "MOBI 选项";
        _optionTabNames[AdvancedTab] = "高级";

        OptionsTabs.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler(TrackedTextChanged), true);
        OptionsTabs.AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(TrackedSelectionChanged), true);
        OptionsTabs.AddHandler(DatePicker.SelectedDateChangedEvent, new EventHandler<SelectionChangedEventArgs>(TrackedDateChanged), true);
        OptionsTabs.AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(TrackedToggleChanged), true);
        OptionsTabs.AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(TrackedToggleChanged), true);
    }

    private void TrackedTextChanged(object sender, TextChangedEventArgs e) => TrackOptionChange(e.OriginalSource as DependencyObject);

    private void TrackedSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is ComboBox or DatePicker) TrackOptionChange(e.OriginalSource as DependencyObject);
    }

    private void TrackedDateChanged(object? sender, SelectionChangedEventArgs e) => TrackOptionChange(e.OriginalSource as DependencyObject);

    private void TrackedToggleChanged(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is CheckBox) TrackOptionChange(e.OriginalSource as DependencyObject);
    }

    private void TrackOptionChange(DependencyObject? source)
    {
        if (!_optionTrackingReady || _applyingProfile || source is null) return;
        var tab = FindVisualParent<TabItem>(source) ?? OptionsTabs.SelectedItem as TabItem;
        if (tab is null) return;
        MarkDirtyTab(tab);
        if (!string.IsNullOrWhiteSpace(_activeConversionPlanName))
        {
            UpdateWorkspaceScope();
        }
        if (ReferenceEquals(tab, LayoutTab) || ReferenceEquals(source, FullWidthIndentCheck))
        {
            SwitchToCustomModeAfterManualLayoutChange();
            UpdateModeDescription();
        }
    }

    private void MarkDirtyTab(TabItem tab)
    {
        MarkProjectDirty();
        if (!_optionTabNames.TryGetValue(tab, out var name) || !_dirtyOptionTabs.Add(tab)) return;
        tab.Header = $"{name} ●";
        tab.ToolTip = "本分页有尚未写入转换方案的修改";
    }

    private void ClearDirtyTab(TabItem tab)
    {
        if (!_optionTabNames.TryGetValue(tab, out var name)) return;
        _dirtyOptionTabs.Remove(tab);
        tab.Header = name;
        tab.ToolTip = null;
    }

    private void ClearAllDirtyTabs()
    {
        foreach (var tab in _optionTabNames.Keys) ClearDirtyTab(tab);
    }

    private void SwitchToCustomModeAfterManualLayoutChange()
    {
        if (_conversionMode == ConversionMode.Custom) return;
        _applyingProfile = true;
        CustomModeRadio.IsChecked = true;
        _applyingProfile = false;
        _conversionMode = ConversionMode.Custom;
        UpdateModeDescription();
        StatusText.Text = "检测到手动排版修改，已自动切换为自定义模式";
    }

    private void UpdateModeDescription()
    {
        if (ModeDescriptionText is null || ModeParameterText is null) return;
        switch (_conversionMode)
        {
            case ConversionMode.OriginalCompatible:
                ModeDescriptionText.Text = "原版兼容：复现原版 EasyPub 的正文密度与缩进习惯。";
                ModeParameterText.Text = "字号 110% · 行高 120% · 段间距 0.6em · 首行 0em · 边距 0/0/3/3px · 默认对齐 · 全角空格缩进 × 2";
                break;
            case ConversionMode.ModernLayout:
                ModeDescriptionText.Text = "现代排版：增加留白和行距，适合高分辨率 Kindle 长时间阅读。";
                ModeParameterText.Text = "字号 105% · 行高 165% · 段间距 0.35em · 首行 2em · 边距 12/12/18/18px · 两端对齐 · 不额外添加全角空格";
                break;
            default:
                ModeDescriptionText.Text = "自定义：保留当前数值；手动修改排版参数时会自动进入此模式。";
                var fullWidthSummary = FullWidthIndentCheck?.IsChecked == true ? $"全角空格 × {SelectedFullWidthIndentCount()}" : "不添加全角空格";
                ModeParameterText.Text = $"当前：字号 {FontSizeText?.Text}% · 行高 {LineHeightText?.Text}% · 段间距 {ParagraphSpacingText?.Text}em · 首行 {IndentText?.Text}em · {fullWidthSummary}";
                break;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (RootLayout is null || OptionsTabs is null) return;
        var compact = ActualWidth < 980;
        if (_compactLayout == compact && IsLoaded) return;
        _compactLayout = compact;
        RootLayout.Margin = compact ? new Thickness(10) : new Thickness(18);
        MainContentGrid.Margin = compact ? new Thickness(12, 14, 8, 10) : new Thickness(18, 18, 12, 12);
        HeaderLogo.Width = HeaderLogo.Height = compact ? 40 : 48;
        HeaderTitleText.FontSize = compact ? 22 : 27;
        PreviewBookButton.Content = compact ? "预览" : "近似预览";
        TaskCenterButton.Content = compact ? "任务" : "任务中心";
        SettingsButton.Content = compact ? string.Empty : "设置";
        SidebarColumn.Width = new GridLength(compact ? 132 : string.Equals(_uiDensity, "Compact", StringComparison.OrdinalIgnoreCase) ? 166 : 184);
        SidebarPanel.Padding = compact ? new Thickness(7) : new Thickness(10);
        LibraryNavigationButton.Content = "书库";
        ChaptersNavigationButton.Content = compact ? "章节" : "章节正文";
        CoverNavigationButton.Content = compact ? "封面" : "封面信息";
        LayoutNavigationButton.Content = compact ? "排版" : "排版插图";
        ConvertNavigationButton.Content = compact ? "转换" : "转换输出";
        TasksNavigationButton.Content = compact ? "任务" : "任务中心";
        ModeLabelText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        PresetLabelText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LayoutModeCombo.Width = compact ? 128 : 178;
        LayoutNavigationColumn.Width = new GridLength(compact ? 178 : 210);
        LayoutSettingsColumn.Width = new GridLength(compact ? 340 : 390);
        KindleModelCombo.Width = compact ? 210 : 250;
        ManagePresetButton.Content = string.Empty;
        RunPreflightButton.Content = compact ? string.Empty : "检查问题";
        ConvertButton.Content = compact ? "开始转换" : "开始批量转换";
        StatusText.FontSize = compact ? 11 : 12;
        var showsBooks = _workspacePage is WorkspacePage.Library or WorkspacePage.Chapters or WorkspacePage.Cover or WorkspacePage.Layout;
        BookListRow.Height = showsBooks
            ? new GridLength(_workspacePage == WorkspacePage.Library ? (compact ? 300 : 430) : (compact ? 180 : 220))
            : new GridLength(0);
        CoverPreviewBorder.Visibility = Visibility.Visible;
        WorkspaceScopeText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var tab in OptionsTabs.Items.OfType<TabItem>()) tab.Padding = compact ? new Thickness(10, 8, 10, 8) : new Thickness(15, 8, 15, 8);
        UpdateContextualControls();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var textEditing = Keyboard.FocusedElement is TextBoxBase or PasswordBox;
        if (ShortcutCatalog.Matches(_shortcutBindings, "add-files", e))
        {
            AddFiles_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ShortcutCatalog.Matches(_shortcutBindings, "import-folder", e))
        {
            ImportFolder_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (!textEditing && ShortcutCatalog.Matches(_shortcutBindings, "select-all", e) && InputBooks.Count > 0)
        {
            SelectAllFiles_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ShortcutCatalog.Matches(_shortcutBindings, "convert", e) && ConvertButton.IsEnabled)
        {
            Convert_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ShortcutCatalog.Matches(_shortcutBindings, "preflight", e) && RunPreflightButton.IsEnabled)
        {
            RunPreflight_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ShortcutCatalog.Matches(_shortcutBindings, "save-project", e))
        {
            SaveProject_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ShortcutCatalog.Matches(_shortcutBindings, "settings", e))
        {
            ShowSettings_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ShortcutCatalog.Matches(_shortcutBindings, "pause", e) && PauseButton.IsEnabled)
        {
            PauseResume_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (ShortcutCatalog.Matches(_shortcutBindings, "focus-search", e))
        {
            BookSearchText.Focus();
            e.Handled = true;
        }
        else if (ShortcutCatalog.Matches(_shortcutBindings, "cycle-focus", e))
        {
            if (FilesList.IsKeyboardFocusWithin) OutputDirectoryText.Focus();
            else if (OutputDirectoryText.IsKeyboardFocusWithin) OptionsTabs.Focus();
            else if (OptionsTabs.IsKeyboardFocusWithin) ConvertButton.Focus();
            else FilesList.Focus();
            e.Handled = true;
        }
    }

    private static bool IsSupportedInput(string path) => Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".epub";

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void SelectComboItemByTag(ComboBox comboBox, string tag)
    {
        var item = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase));
        if (item is not null) comboBox.SelectedItem = item;
    }

    private int SelectedFullWidthIndentCount()
    {
        if (FullWidthIndentCountCombo?.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return Math.Clamp(value, 0, 20);
        return 2;
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

    private sealed class PreflightIssueIndex(
        ConversionPreflightIssue[] globalIssues,
        Dictionary<string, ConversionPreflightIssue[]> issuesByInputPath)
    {
        public static PreflightIssueIndex Create(IReadOnlyList<ConversionPreflightIssue> issues)
        {
            var global = issues.Where(issue => string.IsNullOrWhiteSpace(issue.InputPath)).ToArray();
            var byPath = issues
                .Where(issue => !string.IsNullOrWhiteSpace(issue.InputPath))
                .GroupBy(issue => issue.InputPath!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            return new PreflightIssueIndex(global, byPath);
        }

        public ConversionPreflightIssue[] For(string inputPath)
        {
            if (!issuesByInputPath.TryGetValue(inputPath, out var local)) return globalIssues;
            if (globalIssues.Length == 0) return local;
            return [.. globalIssues, .. local];
        }
    }

    private sealed record ChapterPreviewNavigationItem(ChapterTreeEntry Entry, int Index)
    {
        public string DisplayTitle => $"{new string('　', Math.Max(0, Entry.Level - 1))}{Entry.Title}";
        public override string ToString() => DisplayTitle;
    }

    private sealed record ChapterDocumentCacheKey(
        string SourcePath,
        long SourceLength,
        long SourceLastWriteUtcTicks,
        string ChapterPattern,
        bool HierarchyEnabled,
        string Level1Pattern,
        string Level2Pattern,
        string Level3Pattern,
        TextEncodingMode Encoding,
        string PlanSourceSha256,
        int PlanEntryCount);
}

public sealed class InputBookItem : INotifyPropertyChanged
{
    private string _inputPath;
    private string? _coverImagePath;
    private ImageSource? _coverThumbnail;
    private int _coverThumbnailVersion;
    private string? _title;
    private string? _author;
    private IReadOnlyList<BookIllustration> _illustrations = [];
    private BookMetadataOverrides _metadataOverrides = new();
    private string? _metadataRuleFolder;
    private ChapterTreePlan? _chapterTree;
    private int? _preflightErrorCount;
    private int _preflightWarningCount;

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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibilityName)));
        }
    }

    public string DisplayName => Path.GetFileNameWithoutExtension(InputPath);
    public override string ToString() => DisplayName;

    public string DirectoryPath => Path.GetDirectoryName(InputPath) ?? string.Empty;
    public bool IsEpub => string.Equals(Path.GetExtension(InputPath), ".epub", StringComparison.OrdinalIgnoreCase);
    public string FormatLabel => IsEpub ? "EPUB" : "TXT";
    public string AccessibilityName => $"{DisplayName}，{FormatLabel}，封面{(CoverImagePath is null ? "未设置" : "已设置")}，插图 {_illustrations.Count} 张，元数据{(_metadataOverrides.IsEmpty ? "未设置" : "已设置")}，章节树{(_chapterTree is null ? "未设置" : $"{_chapterTree.Entries.Count} 项")}";

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
            var normalized = value is null ? null : Path.GetFullPath(value);
            if (!SetField(ref _coverImagePath, normalized)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverBadgeVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibilityName)));
            _ = RefreshCoverThumbnailAsync(normalized);
        }
    }

    public string CoverLabel => CoverImagePath is null ? string.Empty : "有封面";

    public Visibility CoverBadgeVisibility => CoverImagePath is null ? Visibility.Collapsed : Visibility.Visible;

    public ImageSource? CoverThumbnail
    {
        get => _coverThumbnail;
        private set
        {
            if (!SetField(ref _coverThumbnail, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverThumbnailVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverThumbnailPlaceholderVisibility)));
        }
    }

    public Visibility CoverThumbnailVisibility => CoverThumbnail is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility CoverThumbnailPlaceholderVisibility => CoverThumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    internal void SetCoverThumbnail(ImageSource? thumbnail)
    {
        if (thumbnail is Freezable freezable && freezable.CanFreeze && !freezable.IsFrozen) freezable.Freeze();
        CoverThumbnail = thumbnail;
    }

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
    public bool HasBeenChecked => _preflightErrorCount is not null;
    public bool HasPreflightIssues => (_preflightErrorCount ?? 0) > 0 || _preflightWarningCount > 0;
    public int ReadinessPriority => (_preflightErrorCount ?? 0) > 0 ? 3 : _preflightWarningCount > 0 ? 2 : !HasBeenChecked ? 1 : 0;
    public string ReadinessLabel => (_preflightErrorCount ?? 0) > 0
        ? $"错误 {_preflightErrorCount}"
        : _preflightWarningCount > 0 ? $"提醒 {_preflightWarningCount}" : HasBeenChecked ? "检查通过" : "未检查";
    public Brush ReadinessForeground => (_preflightErrorCount ?? 0) > 0
        ? Brushes.Firebrick
        : _preflightWarningCount > 0 ? Brushes.DarkOrange : HasBeenChecked ? Brushes.SeaGreen : Brushes.SlateGray;

    public void SetIllustrations(IReadOnlyList<BookIllustration> illustrations)
    {
        _illustrations = illustrations.ToArray();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Illustrations)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IllustrationLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IllustrationBadgeVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibilityName)));
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibilityName)));
    }

    public void SetChapterTree(ChapterTreePlan? chapterTree)
    {
        _chapterTree = chapterTree;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterTree)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterTreeLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterTreeBadgeVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibilityName)));
    }

    public void SetPreflightResult(int errors, int warnings)
    {
        _preflightErrorCount = Math.Max(0, errors);
        _preflightWarningCount = Math.Max(0, warnings);
        foreach (var name in new[] { nameof(HasBeenChecked), nameof(HasPreflightIssues), nameof(ReadinessPriority), nameof(ReadinessLabel), nameof(ReadinessForeground) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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

    private async Task RefreshCoverThumbnailAsync(string? coverPath)
    {
        var version = ++_coverThumbnailVersion;
        CoverThumbnail = null;
        if (string.IsNullOrWhiteSpace(coverPath)) return;

        try
        {
            var prepared = await CoverImageConverter.PrepareJpegAsync(coverPath);
            if (version != _coverThumbnailVersion || !string.Equals(CoverImagePath, coverPath, StringComparison.OrdinalIgnoreCase)) return;
            using var stream = new MemoryStream(prepared.JpegBytes, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 120;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            CoverThumbnail = bitmap;
        }
        catch
        {
            if (version == _coverThumbnailVersion) CoverThumbnail = null;
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
