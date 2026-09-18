# 清单 A：主流程窗口前端交互入口全量映射

## 本文件覆盖范围

只盘点下面这几个文件里的前端交互入口（其余窗口一律不展开，只在调用链里作为「弹窗」终点出现）：

| 文件 | 行数 | 角色 |
|---|---|---|
| `src\EasyPub.Desktop\MainWindow.xaml` | 357 | 主窗口视图 |
| `src\EasyPub.Desktop\MainWindow.xaml.cs` | 4890 | 主窗口逻辑主体 |
| `src\EasyPub.Desktop\MainWindow.Workflow.cs` | 100 | 主窗口 partial（检查与修复页） |
| `src\EasyPub.Desktop\MainWindow.Performance.cs` | 122 | 主窗口 partial（动效、预览分栏） |
| `src\EasyPub.Desktop\BookSourcePanel.cs` | 414 | **注意：此文件不是 MainWindow 的 partial，而是 `ChapterEditorWindow` 的 partial**（见 §6 说明） |
| `src\EasyPub.Desktop\TaskCenterWindow.xaml` / `.xaml.cs` | 61 / 247 | 任务中心子窗口 |
| `src\EasyPub.Desktop\ConversionHistoryWindow.xaml` / `.xaml.cs` | 27 / 54 | 转换历史子窗口 |

**入口总数：241。** 分布如下：

| 分区 | 类别 | 条数 | 编号 |
|---|---|---|---|
| §1 | MainWindow.xaml 的 `Click=` / 事件属性绑定 | 147 | 1–147 |
| §1b | MainWindow.xaml 的 `EventSetter`（收藏夹菜单项动态生成） | 1 | 148 |
| §2 | 代码里的 `+=` 动态绑定（构造函数、定时器、全局事件路由、回调） | 38 | 149–186 |
| §3 | 代码里动态创建的 `ContextMenu` / `MenuItem`（项目菜单 + 更多菜单 + 行内菜单 + 列表右键菜单） | 20 | 187–206 |
| §4 | `MainWindow_PreviewKeyDown` 里的快捷键分支（10 个可配置动作） | 10 | 207–216 |
| §5 | `MainWindow.Performance.cs` 动态创建的 GridSplitter 输入事件 | 3 | 217–219 |
| §6 | `BookSourcePanel.cs`（ChapterEditorWindow partial）的动态绑定 | 11 | 220–230 |
| §7 | TaskCenterWindow.xaml 绑定 | 9 | 231–239 |
| §8 | ConversionHistoryWindow.xaml 绑定 | 2 | 240–241 |
| | **合计** | **241** | 1–241 连续 |

> 编号 1–241 连续，**没有省略任何一行**。§6 的 11 条在实际运行时挂在 `ChapterEditorWindow` 上（见 §6 开头说明），若只统计「主流程窗口自身」，入口数为 **230**。
>
> 计数口径：一个「控件 × 事件 × 处理函数」算一个入口。同一个控件同一事件被绑定了两个处理函数（XAML 静态 + 代码 `+=`）时算两个入口（这是刻意的，见 §9 重复入口清单）。纯装饰属性（ToolTip/IsCancel/IsDefault/Panel.ZIndex）不算入口。

## 列约定

- **位置**：XAML 绑定写 `MainWindow.xaml:行`，代码绑定写 `文件:行`。所有行号来自实际读取的当前工作区版本。
- **处理的 C# 方法**：方法**定义**所在的行号（箭头简写 `=>` 也计入定义行）。
- **最终落到 Core**：顺着调用链追到 `EasyPub.Core` 的公开方法/类型为止；整条链都在 Desktop 层的写「**不出 Desktop**」。
- **副作用**取自给定枚举：`仅UI` / `改内存树` / `写项目快照` / `写产物` / `⚠️写TXT` / `弹窗` / `网络`。括号内是限定说明（例如「写项目快照（2 秒防抖恢复定时器触发）」表示该 handler 本身只标脏，真正落盘由定时器完成）。
- **`⚠️写TXT` 判定标准**：只有真正覆写**用户原始 TXT 文件**才算。写「副本 / 备份 / 工作副本」不算，会在备注里写明。

## 缩写

为控制表格宽度，文件名使用以下缩写，含义固定：

- `MW.cs` = `src\EasyPub.Desktop\MainWindow.xaml.cs`
- `MW.xaml` = `src\EasyPub.Desktop\MainWindow.xaml`
- `MW.Workflow.cs` = `src\EasyPub.Desktop\MainWindow.Workflow.cs`
- `MW.Perf.cs` = `src\EasyPub.Desktop\MainWindow.Performance.cs`
- `TCW.xaml(.cs)` = `TaskCenterWindow.xaml(.cs)`
- `CHW.xaml(.cs)` = `ConversionHistoryWindow.xaml(.cs)`
- `Core\X.cs` = `src\EasyPub.Core\X.cs`

---

## §1 MainWindow.xaml 的 XAML 静态事件绑定（147 条）

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| 1 | Window（根，`AllowDrop="True"`） | MW.xaml:9 | Drop | `Window_Drop` MW.cs:3418 | `AddFiles` MW.cs:3961 → `ApplyMetadataMapping` MW.cs:4002 | `MetadataMappingResolver.Match` Core\MetadataMapping.cs:59 | 改内存树 |
| 2 | Window（根） | MW.xaml:9 | DragOver | `Window_DragOver` MW.cs:3409 | `IsSupportedInput` MW.cs:4474 | 不出 Desktop | 仅UI（光标效果） |
| 3 | Window（根） | MW.xaml:9 | SizeChanged | `MainWindow_SizeChanged` MW.cs:4370 | `UpdateConversionPaneLayout` MW.cs:4360、`UpdateContextualControls` MW.cs:4119 | 不出 Desktop | 仅UI（紧凑布局切换，改文案与列宽） |
| 4 | Window（根） | MW.xaml:9 | PreviewKeyDown | `MainWindow_PreviewKeyDown` MW.cs:4416 | `ShortcutCatalog.Matches` Desktop\ShortcutCatalog.cs:26 | 不出 Desktop | 见 §4（10 个快捷键入口） |
| 5 | `ProjectMenuButton`「当前项目：网文整理 ⌄」 | MW.xaml:91 | Click | `ProjectMenu_Click` MW.cs:223 | 动态 `ContextMenu` + 4 个 `MenuItem`（MW.cs:233） | 不出 Desktop | 弹窗（项目菜单） |
| 6 | `BookSearchText`（搜索框） | MW.xaml:92 | TextChanged | `BookWorklistFilter_Changed` MW.cs:4071 | `_bookFilterTimer.Start` MW.cs:4076 → `ApplyBookWorklistFilter` MW.cs:4082 | 不出 Desktop | 仅UI（180ms 防抖后过滤，见 #152） |
| 7 | `LightThemeButton`「浅色」 | MW.xaml:93 | Click | `UseLightTheme_Click` MW.cs:311 | `ThemeManager.Apply` Desktop\ThemeManager.cs:25 | 不出 Desktop | 仅UI（主题） |
| 8 | `DarkThemeButton`「深色」 | MW.xaml:93 | Click | `UseDarkTheme_Click` MW.cs:317 | `ThemeManager.Apply` Desktop\ThemeManager.cs:25 | 不出 Desktop | 仅UI（主题） |
| 9 | `SettingsButton`「设置」 | MW.xaml:94 | Click | `ShowSettings_Click` MW.cs:444（async void）→ `ShowSettingsAsync` MW.cs:539 | `SettingsWindow.ShowDialog` MW.cs:564、`AppSettingsStore.SaveAsync` MW.cs:583 | `AppSettingsStore.SaveAsync` Core\AppSettingsStore.cs:155 | 弹窗 + 写项目快照（设置文件） |
| 10 | `PreviewBookButton`「成品预览」**Visibility=Collapsed** | MW.xaml:95 | Click | `PreviewBook_Click` MW.cs:3423（async void） | `BuildConversionRequestsAsync` MW.cs:3886、`BookPreviewService.BuildAsync` MW.cs:3453、`BookPreviewWindow.ShowDialog` MW.cs:3459 | `BookPreviewService.BuildAsync` Core\BookPreviewService.cs:31 | 弹窗 + 写产物（预览 HTML 工作副本，非 epub/mobi） |
| 11 | `TaskCenterButton`「任务中心」**Visibility=Collapsed** | MW.xaml:95 | Click | `ShowTaskCenter_Click` MW.cs:3621（async void）→ `ShowTaskCenterAsync` MW.cs:3623 | `ConversionHistoryStore.LoadAsync` MW.cs:3633、`TaskCenterWindow.Show` MW.cs:3657 | `ConversionHistoryStore.LoadAsync` Core\ConversionHistoryStore.cs:41 | 弹窗 |
| 12 | `RecoveryBanner` 内「恢复」 | MW.xaml:97 | Click | `RestoreRecovery_Click` MW.cs:1646（async void） | `ApplyProjectDocument` MW.cs:1354、`SaveRecoveryIfChangedAsync` MW.cs:1581 | `EasyPubProjectStore.SaveAsync` Core\EasyPubProjectStore.cs:91 | 改内存树 + 写项目快照 |
| 13 | `RecoveryBanner` 内「暂不恢复」 | MW.xaml:97 | Click | `IgnoreRecovery_Click` MW.cs:1665 | 只改 `RecoveryBanner.Visibility` 与状态栏 | 不出 Desktop | 仅UI |
| 14 | `RecoveryBanner` 内「删除快照」 | MW.xaml:97 | Click | `DeleteRecovery_Click` MW.cs:1671 | `_recoveryStore.Delete` MW.cs:1673 | `EasyPubProjectStore.Delete` Core\EasyPubProjectStore.cs:130 | 写项目快照（删除恢复快照文件） |
| 15 | `LibraryNavigationButton`「1 导入书稿」 | MW.xaml:104 | Click | `NavigateLibrary_Click` MW.cs:198 | `ShowWorkspacePage` MW.cs:239 | 不出 Desktop | 仅UI |
| 16 | `ReviewNavigationButton`「2 检查与修复」 | MW.xaml:105 | Click | `NavigateReview_Click` MW.Workflow.cs:14 | `ShowWorkspacePage` MW.cs:239、`CheckWorkflowAsync` MW.Workflow.cs:23 | `BookAnalysisCoordinator.AnalyzeAsync` Core\BookAnalysisCoordinator.cs:83 | 仅UI（重算问题清单，不落盘） |
| 17 | `LayoutNavigationButton`「3 制作设置」 | MW.xaml:106 | Click | `NavigateLayout_Click` MW.cs:205 | `ShowWorkspacePage` MW.cs:239 | 不出 Desktop | 仅UI |
| 18 | `ConvertNavigationButton`「4 转换与验收」 | MW.xaml:107 | Click | `NavigateConvert_Click` MW.cs:206 | `ShowWorkspacePage` + `OpenConversionSettingsPane` MW.cs:450 | 不出 Desktop | 仅UI |
| 19 | `ChaptersNavigationButton`「章节正文」**Visibility=Collapsed**（死 UI） | MW.xaml:108 | Click | `NavigateChapters_Click` MW.cs:199 | **重定向**：`EditChapters_Click` MW.cs:1904 | `ChapterTreeDocumentCache.GetOrLoadAsync` Core\ChapterTreeDocumentCache.cs:15 | 弹窗（被重定向，见 §9） |
| 20 | `CoverNavigationButton`「封面信息」**Visibility=Collapsed**（死 UI，方法仍活） | MW.xaml:109 | Click | `NavigateCover_Click` MW.cs:200 | `ShowWorkspacePage(Cover)`；设 `CoverBookCombo.SelectedItem` 触发 #71 | `CoverImageConverter.PrepareJpegAsync` Core\CoverImageConverter.cs:20（经 #71） | 仅UI |
| 21 | `TasksNavigationButton`「转换记录与验收」 | MW.xaml:112 | Click | `NavigateTasks_Click` MW.cs:212 | `ShowWorkspacePage(Tasks)` | 不出 Desktop | 仅UI |
| 22 | 侧栏「原文备份…」（无 x:Name） | MW.xaml:113 | Click | `ManageSourceBackups_Click` MW.Workflow.cs:98 | `SourceBackupWindow.ShowDialog` MW.Workflow.cs:99 | 不出 Desktop | 弹窗（子窗口内「恢复」会写原始 TXT，**不属本表入口**，见 §9 备注） |
| 23 | `ShortcutManagerButton`「快捷键管理」 | MW.xaml:114 | Click | `ManageShortcuts_Click` MW.cs:214（async void） | `new ShortcutManagerWindow` MW.cs:216（`ShowDialog` MW.cs:217）、`AppSettingsStore.SaveAsync` MW.cs:219 | `AppSettingsStore.SaveAsync` Core\AppSettingsStore.cs:155 | 弹窗 + 写项目快照（设置文件） |
| 24 | `AddBooksButton`「添加书稿」 | MW.xaml:121 | Click | `AddFiles_Click` MW.cs:1712 | `OpenFileDialog.ShowDialog` MW.cs:1720 → `AddFiles` MW.cs:3961 | `MetadataMappingResolver.Match` Core\MetadataMapping.cs:59 | 弹窗 + 改内存树 |
| 25 | 「导入文件夹」（无 x:Name） | MW.xaml:121 | Click | `ImportFolder_Click` MW.cs:1723 | `OpenFolderDialog` + `Directory.EnumerateFiles` MW.cs:1735 + `AddFiles` MW.cs:1747 | `MetadataMappingResolver.Match` Core\MetadataMapping.cs:59 | 弹窗 + 改内存树 |
| 26 | `FavoriteImportMenu` →「添加收藏文件夹…」`MenuItem` | MW.xaml:121 | Click | `AddFavoriteFolder_Click` MW.cs:1811（async void） | `OpenFolderDialog` + `FavoriteFolderStore.AddAsync` MW.cs:1825 | `FavoriteFolderStore.AddAsync` Core\FavoriteFolderStore.cs:42 | 弹窗 + 写项目快照（收藏列表文件） |
| 27 | `FavoriteImportMenu` →「管理收藏文件夹…」`MenuItem` | MW.xaml:121 | Click | `ManageFavoriteFolders_Click` MW.cs:434 | `ManageFavoriteFoldersWindow` MW.cs:436 → `FavoriteFolderManagerWindow` MW.cs:438、`ApplyFavoriteFolders` MW.cs:1883 | 不出 Desktop | 弹窗 |
| 28 | `RemoveSelectedButton`「移除所选」 | MW.xaml:121 | Click | `RemoveSelected_Click` MW.cs:2524 | `PushUndo` MW.cs:389、`InputBooks.Remove` MW.cs:2529 | 不出 Desktop | 改内存树 |
| 29 | `MoreLibraryActionsButton`「更多」 | MW.xaml:121 | Click | `MoreLibraryActions_Click` MW.cs:323 | 动态 `ContextMenu`（4 项，MW.cs:326–338） | 不出 Desktop | 弹窗 |
| 30 | `SelectAllFilesButton`「全选」**Visibility=Collapsed**（死 UI） | MW.xaml:121 | Click | `SelectAllFiles_Click` MW.cs:2533 | `FilesList.SelectAll` MW.cs:2536 | 不出 Desktop | 仅UI |
| 31 | `ClearFilesButton`「清空」**Visibility=Collapsed**（死 UI） | MW.xaml:121 | Click | `ClearFiles_Click` MW.cs:2540 | `PushUndo` + `InputBooks.Clear` MW.cs:2544 | 不出 Desktop | 改内存树 |
| 32 | 「封面与书籍信息」（无 x:Name） | MW.xaml:123 | Click | `NavigateCover_Click` MW.cs:200 | 同 #20 | 同 #20 | 仅UI |
| 33 | 「排版与插图」（无 x:Name） | MW.xaml:124 | Click | `NavigateLayout_Click` MW.cs:205 | 同 #17 | 不出 Desktop | 仅UI |
| 34 | 「输出设置…」（无 x:Name） | MW.xaml:125 | Click | `NavigateConvert_Click` MW.cs:206 | 同 #18 | 不出 Desktop | 仅UI |
| 35 | `WorkflowCategoryCombo`（问题分类） | MW.xaml:134 | SelectionChanged | `WorkflowFilter_Changed` MW.Workflow.cs:20 | `RefreshWorkflowRows` MW.Workflow.cs:55 | 不出 Desktop（`PreflightWindow.Collapse` Desktop\PreflightWindow.xaml.cs） | 仅UI |
| 36 | `WorkflowIncludeInformation`「显示已安排处理 / 信息」 | MW.xaml:135 | Click | `WorkflowFilter_Changed` MW.Workflow.cs:20 | 同 #35 | 同上 | 仅UI |
| 37 | `WorkflowRefreshButton`「重新检查全部书稿」 | MW.xaml:136 | Click | `WorkflowRefresh_Click` MW.Workflow.cs:21（async void） | `CheckWorkflowAsync` MW.Workflow.cs:23 → `Task.Run(_analysisCoordinator.AnalyzeAsync)` MW.Workflow.cs:35 | `BookAnalysisCoordinator.AnalyzeAsync` Core\BookAnalysisCoordinator.cs:83 | 仅UI（重算） |
| 38 | 问题行按钮 `Content="{Binding ActionLabel}"` | MW.xaml:149 | Click | `WorkflowIssue_Click` MW.Workflow.cs:84 | `NavigateToPreflightIssue` MW.cs:3536 | 视分支而定（可到 `ChapterTreeDocumentCache` / `IllustrationManagerWindow` / `TextCleanupWindow`） | 弹窗 |
| 39 | 「下一步：制作设置 →」 | MW.xaml:154 | Click | `NavigateLayout_Click` MW.cs:205 | 同 #17 | 不出 Desktop | 仅UI |
| 40 | 「自动检查设置」（无 x:Name） | MW.xaml:159 | Click | `AutomaticCheckSettings_Click` MW.cs:1553（async void） | `new AutomaticCheckWindow` MW.cs:1555（`ShowDialog` MW.cs:1556）、`ScheduleAutomaticAnalysis` MW.cs:1563、`AppSettingsStore.SaveAsync` MW.cs:1565 | `AppSettingsStore.SaveAsync` Core\AppSettingsStore.cs:155 | 弹窗 + 写项目快照（设置文件）+ 改内存树（检查选项） |
| 41 | `BookFilterCombo`（筛选） | MW.xaml:161 | SelectionChanged | `BookWorklistFilter_Changed` MW.cs:4071 | `ApplyBookWorklistFilter` MW.cs:4082 → `_bookWorklistView.Refresh` MW.cs:4089 | 不出 Desktop | 仅UI |
| 42 | `BookSortCombo`（排序） | MW.xaml:161 | SelectionChanged | `BookWorklistFilter_Changed` MW.cs:4071 | 同 #41 | 不出 Desktop | 仅UI |
| 43 | `SelectAllHeaderCheckBox`（表头全选） | MW.xaml:162 | Click | `SelectVisibleBooks_Click` MW.cs:2902 | `FilesList.SelectAll` MW.cs:2906 与 `FilesList.UnselectAll` MW.cs:2908 | 不出 Desktop | 仅UI（但会经 #44 触发预览刷新） |
| 44 | `FilesList`（书稿列表） | MW.xaml:163 | SelectionChanged | `FilesList_SelectionChanged` MW.cs:2715 | `RefreshSelectionPreviewsAsync` MW.cs:2784 → `RefreshCoverPreviewAsync` MW.cs:3193 + `RefreshInlineChapterPreviewAsync` MW.cs:2926 → `GetChapterDocumentAsync` MW.cs:3982 | `ChapterTreeDocumentCache.GetOrLoadAsync` Core\ChapterTreeDocumentCache.cs:15、`CoverImageConverter.PrepareJpegAsync` Core\CoverImageConverter.cs:20 | 仅UI（选择态 + 后台读正文；有 150ms 延迟与 CancellationToken 取消） |
| 45 | `FilesList` | MW.xaml:163 | PreviewMouseLeftButtonDown | `FilesList_PreviewMouseLeftButtonDown` MW.cs:2800 | `ApplyLibrarySelection` MW.cs:2824、`Mouse.Capture` MW.cs:2820 | 不出 Desktop | 仅UI（Ctrl 刷选起手；`e.Handled=true` 抑制 WPF 默认选择） |
| 46 | `FilesList` | MW.xaml:163 | PreviewMouseMove | `FilesList_PreviewMouseMove` MW.cs:2837 | `ItemsControl.ContainerFromElement` MW.cs:2842 | 不出 Desktop | 仅UI（刷选） |
| 47 | `FilesList` | MW.xaml:163 | PreviewMouseLeftButtonUp | `FilesList_PreviewMouseLeftButtonUp` MW.cs:2845 | `Mouse.Capture(null)` MW.cs:2848 | 不出 Desktop | 仅UI |
| 48 | `FilesList` | MW.xaml:163 | **PreviewMouseRightButtonUp** | `FilesList_PreviewMouseRightButtonDown` MW.cs:2851 | 动态 `ContextMenu`（MW.cs:2865–2897） | 见 §3 各菜单项 | 弹窗（**事件名与处理方法名不一致：绑定的是 Up，方法名叫 Down**） |
| 49 | 行内「•••」按钮（`Content="•••"`，无 x:Name） | MW.xaml:163 | Click | `BookActions_Click` MW.cs:343 | `SelectOnlyBook` MW.cs:376、动态 `ContextMenu`（MW.cs:348–371） | 见 §3 | 弹窗 |
| 50 | `SelectVisibleBooksCheckBox`「全选当前列表」 | MW.xaml:164 | Click | `SelectVisibleBooks_Click` MW.cs:2902 | 同 #43 | 不出 Desktop | 仅UI |
| 51 | `CoverDropPanel`（书库左侧封面区） | MW.xaml:166 | DragOver | `CoverDrop_DragOver` MW.cs:3153 | `TryGetSingleCoverPath` MW.cs:3376 | 不出 Desktop | 仅UI（光标效果） |
| 52 | `CoverDropPanel` | MW.xaml:166 | Drop | `CoverDrop_Drop` MW.cs:3161（async void） | `AssignCoverAsync` MW.cs:3173 → `CoverImageConverter.PrepareJpegAsync` MW.cs:3179 | `CoverImageConverter.PrepareJpegAsync` Core\CoverImageConverter.cs:20 | 改内存树（写 `book.CoverImagePath`） |
| 53 | `PreviousSelectedBookButton`「←」 | MW.xaml:170 | Click | `PreviousSelectedBook_Click` MW.cs:2756 | `MoveLibraryInspector(-1)` MW.cs:2760 | 不出 Desktop | 仅UI |
| 54 | `NextSelectedBookButton`「→」 | MW.xaml:170 | Click | `NextSelectedBook_Click` MW.cs:2758 | `MoveLibraryInspector(1)` MW.cs:2760 | 不出 Desktop | 仅UI |
| 55 | `OpenCoverPreviewButton`（封面缩略图） | MW.xaml:171 | Click | `OpenCoverPreview_Click` MW.cs:2684 | `CoverLightboxWindow.ShowDialog` MW.cs:2702；无封面时转 `BrowseCover_Click` MW.cs:2691 | 不出 Desktop | 弹窗 |
| 56 | `QuickEditTextButton`「编辑 TXT」 | MW.xaml:173 | Click | `EditSourceText_Click` MW.cs:1997 | `SourceBackupStore.CreateDefault().CreateWorkingCopy` MW.cs:2026、`AddFiles([copy])` MW.cs:2027、`Process.Start` 外置编辑器 MW.cs:2031 | `SourceBackupStore.CreateWorkingCopy` Core\SourceBackupStore.cs:20 | 弹窗（外置编辑器）+ 改内存树（副本入书库）+ 写盘（备份目录 + WorkingCopies 副本）。**不修改原始 TXT** |
| 57 | `ViewSelectedBookIssuesButton`「查看」 | MW.xaml:173 | Click | `ViewSelectedBookIssues_Click` MW.cs:3512（async void） | `BuildConversionRequestsForBooksAsync` MW.cs:3520、`_analysisCoordinator.AnalyzeAsync` MW.cs:3521、`PreflightWindow.ShowDialog` MW.cs:3524 | `BookAnalysisCoordinator.AnalyzeAsync` Core\BookAnalysisCoordinator.cs:83 | 弹窗 |
| 58 | `QuickChapterButton`（章节树） | MW.xaml:174 | Click | `EditChapters_Click` MW.cs:1904（async void） | `GetChapterDocumentAsync` MW.cs:1930、`ChapterEditorWindow.ShowDialog` MW.cs:1962、`book.SetChapterTree` MW.cs:1964、`SaveRecoveryIfChangedAsync` MW.cs:1982 | `ChapterTreeDocumentCache.GetOrLoadAsync` Core\ChapterTreeDocumentCache.cs:15、`EasyPubProjectStore.SaveAsync` Core\EasyPubProjectStore.cs:91 | 弹窗 + 改内存树 + 写项目快照 |
| 59 | `QuickMetadataButton`（封面信息） | MW.xaml:177 | Click | `NavigateCover_Click` MW.cs:200 | 同 #20 | 同 #20 | 仅UI |
| 60 | `QuickCleanupButton`（文本清理） | MW.xaml:180 | Click | `EditTextCleanup_Click` MW.cs:2084（async void） | `TextCleanupWindow.CreateAsync` MW.cs:2106、`SetCleanupOverride` MW.cs:2110/2114/2116、`SaveRecoveryIfChangedAsync` MW.cs:2121 | `EasyPubProjectStore.SaveAsync` Core\EasyPubProjectStore.cs:91 | 弹窗 + 改内存树 + 写项目快照 |
| 61 | `QuickIllustrationButton`「插图」**父 WrapPanel Visibility=Collapsed**（死 UI） | MW.xaml:183 | Click | `EditIllustrations_Click` MW.cs:2633 | `IllustrationManagerWindow.ShowDialog` MW.cs:2656、`book.SetIllustrations` MW.cs:2657 | 不出 Desktop | 弹窗 + 改内存树 |
| 62 | `QuickPreviewButton`「预览」**同上 Collapsed**（死 UI） | MW.xaml:183 | Click | `PreviewBook_Click` MW.cs:3423 | 同 #10 | `BookPreviewService.BuildAsync` Core\BookPreviewService.cs:31 | 弹窗 + 写产物 |
| 63 | `ChapterBookCombo`（章节页书稿选择） | MW.xaml:197 | SelectionChanged | `ChapterBookCombo_SelectionChanged` MW.cs:401 | 反向同步 `FilesList.SelectedItem` MW.cs:406 | 不出 Desktop | 仅UI |
| 64 | `ChapterNavigatorList`（章节目录） | MW.xaml:214 | SelectionChanged | `ChapterNavigatorList_SelectionChanged` MW.cs:3035 | `ShowSelectedChapterPreview` MW.cs:3037 | 不出 Desktop | 仅UI（渲染正文 + `SetLayoutPreviewSample` MW.cs:3060） |
| 65 | `EditSourceTextButton`（章节页「编辑 TXT」） | MW.xaml:217 | Click | `EditSourceText_Click` MW.cs:1997 | 同 #56 | 同 #56 | 弹窗 + 改内存树 |
| 66 | `OpenChapterTreeWorkspaceButton`（章节树工作台） | MW.xaml:220 | Click | `EditChapters_Click` MW.cs:1904 | 同 #58 | 同 #58 | 弹窗 + 改内存树 + 写项目快照 |
| 67 | `OpenTextCleanupWorkspaceButton`（文本清理） | MW.xaml:223 | Click | `EditTextCleanup_Click` MW.cs:2084 | 同 #60 | 同 #60 | 弹窗 + 改内存树 + 写项目快照 |
| 68 | `ChapterPreviewSearchText`（正文查找框） | MW.xaml:233 | TextChanged | `ChapterPreviewSearchText_Changed` MW.cs:3063 | `RefreshChapterSearchStatus` MW.cs:3075 | 不出 Desktop | 仅UI |
| 69 | `ChapterPreviewSearchText` | MW.xaml:233 | KeyDown | `ChapterPreviewSearchText_KeyDown` MW.cs:3065 | `FindInChapterPreview` MW.cs:3089（Enter 下一处 / Shift+Enter 上一处） | 不出 Desktop | 仅UI |
| 70 | `ChapterSearchPreviousButton`「上一处」 | MW.xaml:233 | Click | `ChapterSearchPrevious_Click` MW.cs:3072 | `FindInChapterPreview(false)` MW.cs:3089 | 不出 Desktop | 仅UI |
| 71 | `ChapterSearchNextButton`「下一处」 | MW.xaml:233 | Click | `ChapterSearchNext_Click` MW.cs:3073 | `FindInChapterPreview(true)` MW.cs:3089 | 不出 Desktop | 仅UI |
| 72 | `CoverBookCombo`（封面页书稿选择） | MW.xaml:247 | SelectionChanged | `CoverBookCombo_SelectionChanged` MW.cs:411（async void） | `LoadSelectedBookMetadataFields` MW.cs:3276、`RefreshCoverPreviewAsync` MW.cs:3193 | `CoverImageConverter.PrepareJpegAsync` Core\CoverImageConverter.cs:20 | 仅UI |
| 73 | `CoverBatchMetadataButton`「批量编辑」 | MW.xaml:251 | Click | `EditMetadata_Click` MW.cs:1758 | `EditMetadataBooks` MW.cs:1775 → `BatchMetadataWindow.ShowDialog` MW.cs:1779 | 不出 Desktop | 弹窗 |
| 74 | `CoverEditorPanel`（封面页拖放区） | MW.xaml:257 | DragOver | `CoverDrop_DragOver` MW.cs:3153 | 同 #51 | 不出 Desktop | 仅UI |
| 75 | `CoverEditorPanel` | MW.xaml:257 | Drop | `CoverDrop_Drop` MW.cs:3161 | 同 #52 | 同 #52 | 改内存树 |
| 76 | 封面大图按钮（无 x:Name） | MW.xaml:262 | Click | `OpenCoverPreview_Click` MW.cs:2684 | 同 #55 | 不出 Desktop | 弹窗 |
| 77 | `BrowseCoverButton`「选择封面」 | MW.xaml:263 | Click | `BrowseCover_Click` MW.cs:2667（async void） | `OpenFileDialog` MW.cs:2675、`AssignCoverAsync` MW.cs:2681 | `CoverImageConverter.PrepareJpegAsync` Core\CoverImageConverter.cs:20 | 弹窗 + 改内存树 |
| 78 | `ClearCoverButton`「清除封面」 | MW.xaml:263 | Click | `ClearCover_Click` MW.cs:2705（async void） | `book.CoverImagePath = null` MW.cs:2709、`RefreshCoverPreviewAsync` MW.cs:2712 | 不出 Desktop | 改内存树 |
| 79 | `MetadataMappingButton`「文件夹映射」 | MW.xaml:270 | Click | `EditMetadataMappings_Click` MW.cs:1789（async void） | `new MetadataMappingWindow` MW.cs:1791（`ShowDialog` MW.cs:1792）、`MetadataMappingStore.SaveAsync` MW.cs:1796、`ApplyMetadataMapping` MW.cs:1798 | `MetadataMappingStore.SaveAsync` Core\MetadataMappingStore.cs:59、`MetadataMappingResolver.Match` Core\MetadataMapping.cs:59 | 弹窗 + 写项目快照（映射规则文件）+ 改内存树 |
| 80 | `AuthorText`（作者） | MW.xaml:271 | TextChanged | `SelectedMetadataField_Changed` MW.cs:3298 | `book.SetMetadataOverrides` MW.cs:3302 | 不出 Desktop | 改内存树 + 写项目快照（经 #176 定时器） |
| 81 | `TranslatorText`（译者） | MW.xaml:271 | TextChanged | `SelectedMetadataField_Changed` MW.cs:3298 | 同 #80 | 不出 Desktop | 同上 |
| 82 | `PublisherText`（出版社） | MW.xaml:272 | TextChanged | `SelectedMetadataField_Changed` MW.cs:3298 | 同 #80 | 不出 Desktop | 同上 |
| 83 | `IsbnText`（ISBN） | MW.xaml:272 | TextChanged | `SelectedMetadataField_Changed` MW.cs:3298 | 同 #80 | 不出 Desktop | 同上 |
| 84 | `PublicationDatePicker`（出版日期） | MW.xaml:273 | SelectedDateChanged | `SelectedMetadataField_Changed` MW.cs:3298 | 同 #80 | 不出 Desktop | 同上 |
| 85 | `CategoryCombo`（类别） | MW.xaml:273 | SelectionChanged | `SelectedMetadataField_Changed` MW.cs:3298 | 同 #80 | 不出 Desktop | 同上 |
| 86 | `CategoryCombo` | MW.xaml:273 | LostKeyboardFocus | `SelectedMetadataField_Changed` MW.cs:3298 | 同 #80（可编辑 ComboBox 手输后失焦才落值） | 不出 Desktop | 同上 |
| 87 | `DescriptionText`（简介） | MW.xaml:274 | TextChanged | `SelectedMetadataField_Changed` MW.cs:3298 | 同 #80 | 不出 Desktop | 同上 |
| 88 | `LanguageCombo`（语言） | MW.xaml:275 | SelectionChanged | `SelectedMetadataField_Changed` MW.cs:3298 | 同 #80 | 不出 Desktop | 同上 |
| 89 | `LanguageCombo` | MW.xaml:275 | LostKeyboardFocus | `SelectedMetadataField_Changed` MW.cs:3298 | 同 #86 | 不出 Desktop | 同上 |
| 90 | `LayoutBaseNav`「基础版式」 | MW.xaml:283 | Checked | `LayoutSection_Checked` MW.cs:2212 | 切 6 个 `Layout*Panel.Visibility` MW.cs:2215–2220 | 不出 Desktop | 仅UI |
| 91 | `LayoutMarginsNav`「页边距」 | MW.xaml:283 | Checked | `LayoutSection_Checked` MW.cs:2212 | 同 #90 + `SyncVisibleLayoutControls` MW.cs:2227 | 不出 Desktop | 仅UI |
| 92 | `LayoutFontNav`「字体」 | MW.xaml:283 | Checked | `LayoutSection_Checked` MW.cs:2212 | 同 #91 | 不出 Desktop | 仅UI |
| 93 | `LayoutChapterNav`「章节标题」 | MW.xaml:283 | Checked | `LayoutSection_Checked` MW.cs:2212 | 同 #91 | 不出 Desktop | 仅UI |
| 94 | `LayoutCssNav`「自定义 CSS」 | MW.xaml:283 | Checked | `LayoutSection_Checked` MW.cs:2212 | `UpdateCssSummary` MW.cs:2569 | 不出 Desktop | 仅UI |
| 95 | `LayoutIllustrationNav`「正文插图」 | MW.xaml:283 | Checked | `LayoutSection_Checked` MW.cs:2212 | `UpdateLayoutIllustrationSummary` MW.cs:3335 | 不出 Desktop | 仅UI |
| 96 | 「管理排版方案」（无 x:Name） | MW.xaml:283 | Click | `ManagePresets_Click` MW.cs:1170（async void） | `PresetManagerWindow.ShowDialog` MW.cs:1175、`ApplyProfile` MW.cs:1178、`AppSettingsStore.SaveAsync` MW.cs:1183 | `AppSettingsStore.SaveAsync` Core\AppSettingsStore.cs:155 | 弹窗 + 写项目快照（设置文件） |
| 97 | `OriginalModeRadio`「原版兼容」 | MW.xaml:285 | Checked | `ConversionMode_Checked` MW.cs:2143 | 写死一套版式数值 MW.cs:2152–2156、`RefreshLayoutPreview` MW.cs:2182 | 不出 Desktop | 仅UI（`_applyingProfile` 期间抑制脏标记） |
| 98 | `ModernModeRadio`「现代排版」 | MW.xaml:285 | Checked | `ConversionMode_Checked` MW.cs:2143 | 写死另一套数值 MW.cs:2162–2165 | 不出 Desktop | 仅UI |
| 99 | `CustomModeRadio`「自定义」 | MW.xaml:285 | Checked | `ConversionMode_Checked` MW.cs:2143 | 保留当前值 MW.cs:2170 | 不出 Desktop | 仅UI |
| 100 | `FontSizeText`（字号 %） | MW.xaml:285 | TextChanged | `LayoutPreviewSetting_Changed` MW.cs:2339 | `SwitchToCustomModeAfterManualLayoutChange` MW.cs:4317、`MarkVisibleLayoutChanged` MW.cs:2360 → `MarkDirtyTab` MW.cs:4296 → `MarkProjectDirty` MW.cs:1457 | 不出 Desktop（标脏；落盘经 #176） | 仅UI + 写项目快照（2 秒防抖恢复定时器触发） |
| 101 | `LineHeightText`（行高 %） | MW.xaml:285 | TextChanged | `LayoutPreviewSetting_Changed` MW.cs:2339 | 同 #100 | 同上 | 同上 |
| 102 | `ParagraphSpacingText`（段间距 em） | MW.xaml:285 | TextChanged | `LayoutPreviewSetting_Changed` MW.cs:2339 | 同 #100 | 同上 | 同上 |
| 103 | `IndentText`（首行缩进 em） | MW.xaml:285 | TextChanged | `LayoutPreviewSetting_Changed` MW.cs:2339 | 同 #100 | 同上 | 同上 |
| 104 | `FullWidthIndentCheck`「全角空格缩进」 | MW.xaml:285 | Click | `LayoutPreviewSetting_Click` MW.cs:2346 | 同 #100（同步路径，不延迟预览） | 同上 | 同上 |
| 105 | `FullWidthIndentCountCombo`（全角缩进个数） | MW.xaml:285 | SelectionChanged | `LayoutPreviewSetting_SelectionChanged` MW.cs:2353 | 同 #104 | 同上 | 同上 |
| 106 | `KeepBlankLinesCheck`「保留空行」 | MW.xaml:285 | Click | `LayoutPreviewSetting_Click` MW.cs:2346 | 同 #104 | 同上 | 同上 |
| 107 | `VisibleMarginTopText` | MW.xaml:286 | TextChanged | `VisibleMarginText_Changed` MW.cs:2253 | 回写 `PageMarginTopText` 等 4 个 MW.cs:2256–2259、`MarkVisibleLayoutChanged` MW.cs:2260 | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 108 | `VisibleMarginLeftText` | MW.xaml:286 | TextChanged | `VisibleMarginText_Changed` MW.cs:2253 | 同 #107 | 同上 | 同上 |
| 109 | `VisibleMarginBottomText` | MW.xaml:286 | TextChanged | `VisibleMarginText_Changed` MW.cs:2253 | 同 #107 | 同上 | 同上 |
| 110 | `VisibleMarginRightText` | MW.xaml:286 | TextChanged | `VisibleMarginText_Changed` MW.cs:2253 | 同 #107 | 同上 | 同上 |
| 111 | `VisibleAlignmentCombo`（正文对齐） | MW.xaml:286 | SelectionChanged | `VisibleAlignmentCombo_SelectionChanged` MW.cs:2263 | 回写 `AlignmentCombo.SelectedIndex` MW.cs:2266、`MarkVisibleLayoutChanged` | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 112 | `VisibleEmbedFontCheck`「嵌入字体」 | MW.xaml:287 | Click | `VisibleFontSetting_Changed` MW.cs:2270 | 回写 `EmbedFontCheck`/`SubsetFontCheck`、`UpdateFontSummary` MW.cs:2609 | `FontEmbeddingService.Inspect` Core\TrueTypeFontSubsetter.cs:20 | 仅UI + 写项目快照（防抖） |
| 113 | 字体「选择」按钮（无 x:Name） | MW.xaml:287 | Click | `BrowseVisibleFont_Click` MW.cs:2287 | `OpenFileDialog` MW.cs:2289、`UpdateFontSummary` MW.cs:2307 | `FontEmbeddingService.Inspect` Core\TrueTypeFontSubsetter.cs:20 | 弹窗 + 写项目快照（防抖） |
| 114 | `VisibleFontFamilyText`（字体家族名） | MW.xaml:287 | TextChanged | `VisibleFontSetting_TextChanged` MW.cs:2280 | 回写 `FontFamilyText`、`MarkVisibleLayoutChanged` | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 115 | `VisibleSubsetFontCheck`「仅嵌入实际用字」 | MW.xaml:287 | Click | `VisibleFontSetting_Changed` MW.cs:2270 | 同 #112 | 同上 | 同上 |
| 116 | 「清除字体设置」（无 x:Name） | MW.xaml:287 | Click | `ClearVisibleFont_Click` MW.cs:2313 | 清空 6 个控件 MW.cs:2318–2323、`MarkVisibleLayoutChanged` | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 117 | `VisibleChapterRegexText`（章节标题正则） | MW.xaml:288 | TextChanged | `VisibleChapterRegexText_Changed` MW.cs:2332 | 回写 `ChapterRegexText`、`MarkVisibleLayoutChanged(AdvancedTab)` MW.cs:2336 | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 118 | 「打开 CSS 编辑器」（无 x:Name） | MW.xaml:289 | Click | `EditCss_Click` MW.cs:2558 | `new CustomCssWindow` MW.cs:2560（`ShowDialog` MW.cs:2561）、`MarkDirtyTab(CssTab)` MW.cs:2564 | 不出 Desktop | 弹窗 + 写项目快照（防抖） |
| 119 | `InkManageIllustrationsButton`「管理所选书稿插图」 | MW.xaml:290 | Click | `EditIllustrations_Click` MW.cs:2633 | 同 #61 | 不出 Desktop | 弹窗 + 改内存树 |
| 120 | `ExpandKindlePreviewButton`「放大预览」 | MW.xaml:292 | Click | `ExpandKindlePreview_Click` MW.cs:2374 | 动态 `new Window` MW.cs:2378、把 `KindlePreviewPanel` 移出/移回 MW.cs:2394/2404 | 不出 Desktop | 弹窗（同一面板搬进独立窗口，Esc 关闭） |
| 121 | `KindleModelCombo`（Kindle 型号） | MW.xaml:292 | SelectionChanged | `KindleModelCombo_SelectionChanged` MW.cs:2367 | `ApplySelectedKindleModel` MW.cs:2409、`RefreshLayoutPreview` MW.cs:2371 | `KindleDeviceProfiles.Custom` Core\KindleDeviceProfiles.cs:30 | 仅UI |
| 122 | `CustomKindleWidthText` | MW.xaml:292 | TextChanged | `CustomKindleSize_Changed` MW.cs:2424 | `ApplySelectedKindleModel`、`RefreshLayoutPreview` MW.cs:2431 | `LayoutPreviewPaginator.Paginate` Desktop\LayoutPreviewPaginator.cs:7 | 仅UI |
| 123 | `CustomKindleHeightText` | MW.xaml:292 | TextChanged | `CustomKindleSize_Changed` MW.cs:2424 | 同 #122 | 同上 | 仅UI |
| 124 | `CustomKindlePpiText` | MW.xaml:292 | TextChanged | `CustomKindleSize_Changed` MW.cs:2424 | 同 #122 | 同上 | 仅UI |
| 125 | `PreviousLayoutPreviewPageButton`「上一页」 | MW.xaml:292 | Click | `PreviousLayoutPreviewPage_Click` MW.cs:2506 | `ShowLayoutPreviewPage` MW.cs:2494 | 不出 Desktop | 仅UI |
| 126 | `NextLayoutPreviewPageButton`「下一页」 | MW.xaml:292 | Click | `NextLayoutPreviewPage_Click` MW.cs:2513 | 同 #125 | 不出 Desktop | 仅UI |
| 127 | `ConversionSelectAllCheckBox`「全选」（三态） | MW.xaml:302 | Click | `ConversionSelectAllCheckBox_Click` MW.cs:2911 | `FilesList.SelectAll` MW.cs:2914 与 `FilesList.UnselectAll` MW.cs:2916 | 不出 Desktop | 仅UI |
| 128 | 转换清单行内 `CheckBox`（`Tag="{Binding}"`） | MW.xaml:304 | Click | `ConversionBookCheckBox_Click` MW.cs:2919 | `FilesList.SelectedItems.Remove(book)` MW.cs:2923 | 不出 Desktop | 仅UI |
| 129 | `AdjustConversionSettingsButton`「打开转换设置」 | MW.xaml:309 | Click | `ShowConversionSettings_Click` MW.cs:448 | `OpenConversionSettingsPane` MW.cs:450（→ `ConversionSettingsPane.LoadDraft` MW.cs:471） | 不出 Desktop | 仅UI（面板显隐） |
| 130 | 「查看检查结果 ›」（无 x:Name） | MW.xaml:320 | Click | `RunPreflight_Click` MW.cs:3477（async void） | `BuildConversionRequestsAsync` MW.cs:3486、`GetPreflightReportAsync` MW.cs:3665、`PreflightWindow.ShowDialog` MW.cs:3490 | `BookAnalysisCoordinator.AnalyzeAsync` Core\BookAnalysisCoordinator.cs:83 | 弹窗 |
| 131 | `EpubModeCombo`（EPUB 转 MOBI 模式） | MW.xaml:330 | SelectionChanged | `EpubModeCombo_SelectionChanged` MW.cs:3399 | `UpdateConversionSummary` MW.cs:3401、`MarkDirtyTab(MobiTab)` MW.cs:3403 | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 132 | `ArtifactValidationCheck`（结构验收） | MW.xaml:333 | Click | `ArtifactValidationCheck_Click` MW.cs:1193 | 只切 `ValidationRetentionCombo.IsEnabled` + 状态栏 MW.cs:1195–1198 | 不出 Desktop | 仅UI（落盘由 #170/171 的 `+= MarkProjectDirty` 负责） |
| 133 | `BrowseLegacyConfigButton` | MW.xaml:336 | Click | `BrowseLegacyConfig_Click` MW.cs:821 | `OpenFileDialog` MW.cs:823、`LoadLegacyConfig` MW.cs:829 | `LegacyEasyPubConfig.Load` Core\LegacyEasyPubConfig.cs:29 | 弹窗 + 改内存树（覆盖一批选项控件） |
| 134 | `ClearLegacyConfigButton` | MW.xaml:336 | Click | `ClearLegacyConfig_Click` MW.cs:832（async void） | `AppSettingsStore.SaveAsync` MW.cs:841 | `AppSettingsStore.SaveAsync` Core\AppSettingsStore.cs:155 | 写项目快照（设置文件） |
| 135 | `ShowLegacyConfigDetailsButton` | MW.xaml:336 | Click | `ShowLegacyConfigDetails_Click` MW.cs:850 | `InkDialog.Show` MW.cs:862 | 不出 Desktop | 弹窗 |
| 136 | `FormatCombo`（输出格式） | MW.xaml:346 | SelectionChanged | `FormatCombo_SelectionChanged` MW.cs:3388 | `UpdateContextualControls` MW.cs:3396 | 不出 Desktop | 仅UI（落盘由 #176 的 `+= MarkProjectDirty` 负责） |
| 137 | `LayoutModeCombo`（排版方案） | MW.xaml:347 | SelectionChanged | `LayoutModeCombo_SelectionChanged` MW.cs:2186 | 单选 `Original/Modern/CustomModeRadio` MW.cs:2192–2194 | 不出 Desktop | 仅UI（`_syncingLayoutMode` 防回环） |
| 138 | `ManagePresetButton`（⚙，无 Content） | MW.xaml:347 | Click | `ManagePresets_Click` MW.cs:1170 | 同 #96 | 同 #96 | 弹窗 + 写项目快照 |
| 139 | `RetryFailedButton`「重试失败项」 | MW.xaml:349 | Click | `RetryFailed_Click` MW.cs:3690 | `LoadRetryInputs` MW.cs:3704 → `AddFiles` MW.cs:3961 | `MetadataMappingResolver.Match` Core\MetadataMapping.cs:59 | 改内存树 |
| 140 | `RunPreflightButton`「检查问题」 | MW.xaml:349 | Click | `RunPreflight_Click` MW.cs:3477 | 同 #130 | 同 #130 | 弹窗 |
| 141 | `PauseButton`「暂停」 | MW.xaml:349 | Click | `PauseResume_Click` MW.cs:3861 | `BatchExecutionControl.Pause/Resume` MW.cs:3872/3866 | `BatchExecutionControl.Pause` Core\ConversionConcurrencyPolicy.cs:30 / `Resume` :37 | 仅UI + 改内存树（影响批处理派发） |
| 142 | `CancelButton`「取消」 | MW.xaml:349 | Click | `Cancel_Click` MW.cs:3878 | `_operationCancellation.Cancel()` MW.cs:3881 | 不出 Desktop（CTS 传入 Core 的 `ConvertWithReportAsync`） | 改内存树（取消令牌） |
| 143 | `ConvertButton`「开始批量转换」 | MW.xaml:349 | Click | `Convert_Click` MW.cs:3712（async void） | `BuildConversionRequestsAsync` MW.cs:3728、`GetPreflightReportAsync` MW.cs:3731、`new BatchConverter(new EasyPubConverter()).ConvertWithReportAsync` MW.cs:3776、`_historyStore.AppendAsync` MW.cs:3792 | `BatchConverter.ConvertWithReportAsync` Core\BatchConverter.cs:34 → `EasyPubConverter.ConvertAsync` Core\EasyPubConverter.cs:7、`ConversionHistoryStore.AppendAsync` Core\ConversionHistoryStore.cs:55 | 写产物 + 弹窗（PreflightWindow / ConversionHistoryWindow / InkDialog）+ 打开资源管理器 |
| 144 | `FavoriteFolderCombo`（**位于 Visibility=Collapsed 的旧区块**，死 UI） | MW.xaml:352 | SelectionChanged | `FavoriteFolderCombo_SelectionChanged` MW.cs:1876 | 只切两个按钮 `IsEnabled` MW.cs:1879–1880 | 不出 Desktop | 仅UI |
| 145 | `AddFromFavoriteButton`（同一隐藏区块，死 UI） | MW.xaml:352 | Click | `AddFromFavoriteFolder_Click` MW.cs:1851 | `OpenFileDialog` MW.cs:1865、`AddFiles` MW.cs:1873 | `MetadataMappingResolver.Match` Core\MetadataMapping.cs:59 | 弹窗 + 改内存树 |
| 146 | `RemoveFavoriteButton`（同一隐藏区块，死 UI） | MW.xaml:352 | Click | `RemoveFavoriteFolder_Click` MW.cs:1835（async void） | `FavoriteFolderStore.RemoveAsync` MW.cs:1841 | `FavoriteFolderStore.RemoveAsync` Core\FavoriteFolderStore.cs:67 | 写项目快照（收藏列表文件） |
| 147 | `PresetCombo`（**位于 Visibility=Collapsed 的旧区块**，死 UI） | MW.xaml:354 | SelectionChanged | `PresetCombo_SelectionChanged` MW.cs:4237 | `ApplyProfile` MW.cs:4240 | 不出 Desktop | 仅UI |
| 148 | `FavoriteFoldersMenu` 生成的每个收藏项（`MenuItem.ItemContainerStyle` + `EventSetter`） | MW.xaml:121 | Click（EventSetter） | `FavoriteFolderMenuItem_Click` MW.cs:426 | 设 `FavoriteFolderCombo.SelectedItem` MW.cs:429 → **重定向** `AddFromFavoriteFolder_Click` MW.cs:430 | `MetadataMappingResolver.Match` Core\MetadataMapping.cs:59（经 AddFiles） | 弹窗 + 改内存树（**注意 `FavoriteFolderCombo` 在隐藏区块里，但 `AddFiles` 仍会真正执行**） |

---

## §2 代码里的 `+=` 动态绑定（38 条）

构造函数 `MainWindow()` 位于 MW.cs:127–196。以下 `+=` 里，凡是用 lambda 的都在「处理的 C# 方法」列写出 `文件:行`（lambda 本体所在行）。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| 149 | `ConversionSettingsPane`（转换设置侧栏控件） | MW.cs:133 | `Applied` | `ConversionSettingsPane_Applied` MW.cs:493（async void） | `LegacyEasyPubConfig.Load` MW.cs:507、`ApplyProfile` MW.cs:520、`AppSettingsStore.SaveAsync` MW.cs:529、`RefreshKindleGenSummaryAsync` MW.cs:536 | `LegacyEasyPubConfig.Load` Core\LegacyEasyPubConfig.cs:29、`AppSettingsStore.SaveAsync` Core\AppSettingsStore.cs:155 | 仅UI + 写项目快照（设置文件）+ 改内存树 |
| 150 | `ConversionSettingsPane` | MW.cs:134 | `CloseRequested` | `CloseConversionSettingsPane` MW.cs:478 | 切 `ConversionSettingsPaneHost.Visibility` / 列宽 MW.cs:480–483 | 不出 Desktop | 仅UI |
| 151 | `ConversionSettingsPane` | MW.cs:135 | `OpenEngineSettingsRequested` | `OpenEngineSettingsFromConversionPane` MW.cs:486（async void） | `ShowSettingsAsync(2)` MW.cs:489、再开面板 MW.cs:490 | `AppSettingsStore.SaveAsync` Core\AppSettingsStore.cs:155（经 ShowSettingsAsync） | 弹窗 + 写项目快照 |
| 152 | `InputBooks`（书库集合，非可视控件） | MW.cs:136 | `CollectionChanged` | `InputBooks_CollectionChanged` MW.cs:1381 | `UpdateConversionPreviewBooks` MW.cs:1412、`ScheduleAutomaticAnalysis` MW.cs:1416、`InputBook_PropertyChanged` 订阅 MW.cs:1397 | 不出 Desktop | 改内存树（订阅追踪）+ 仅UI |
| 153 | `OutputDirectoryText`（**输出目录是动态挂的 TextChanged，XAML 里没有**） | MW.cs:137 | `TextChanged` | lambda MW.cs:137 | `MarkProjectDirty` MW.cs:1457、`UpdateConversionSummary` MW.cs:4174 | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 154 | `_bookFilterTimer`（`DispatcherTimer` 180ms） | MW.cs:138 | `Tick` | lambda MW.cs:138–142 | `ApplyBookWorklistFilter` MW.cs:4082 | 不出 Desktop | 仅UI |
| 155 | `_statusRefreshTimer`（`DispatcherTimer` 120ms） | MW.cs:143 | `Tick` | lambda MW.cs:143–147 | `UpdateStatus` MW.cs:4040 | 不出 Desktop | 仅UI。**该定时器只有 `ScheduleStatusUpdate`（MW.cs:4065）会 `Start`，而后者是死代码（见 §9），所以实际上永不触发** |
| 156 | `_automaticAnalysisTimer`（`DispatcherTimer` 450ms） | MW.cs:148 | `Tick` | lambda MW.cs:148–152（async 委托） | `RunAutomaticAnalysisAsync` MW.cs:1492 | `BookAnalysisCoordinator.AnalyzeAsync` Core\BookAnalysisCoordinator.cs:83 | 仅UI + 改内存树（把分析结果写回 `InputBookItem`） |
| 157 | `_layoutPreviewTimer`（`DispatcherTimer` 200ms，定义在 MW.Perf.cs:71） | MW.cs:153 | `Tick` | lambda MW.cs:153–157 | `RefreshLayoutPreview` MW.cs:2431 | `LayoutPreviewPaginator.Paginate` Desktop\LayoutPreviewPaginator.cs:7 | 仅UI |
| 158 | `FormatCombo` | MW.cs:158 | `SelectionChanged` | lambda MW.cs:158 | `MarkProjectDirty` MW.cs:1457 | 不出 Desktop | 仅UI + 写项目快照（防抖）。**与 #136 是同一控件的两个 handler** |
| 159 | `ParallelismCombo`（并发数） | MW.cs:159 | `SelectionChanged` | lambda MW.cs:159 | `MarkProjectDirty` | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 160 | `KindleGenText`（kindlegen 路径） | MW.cs:160 | `TextChanged` | lambda MW.cs:160 | `MarkProjectDirty` | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 161 | `CompressionCombo`（MOBI 压缩） | MW.cs:161 | `SelectionChanged` | lambda MW.cs:161 | `MarkProjectDirty` | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 162 | `StripSourceCheck`「移除附加源文件」 | MW.cs:162 | `Checked` | lambda MW.cs:162 | `MarkProjectDirty` | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 163 | `StripSourceCheck` | MW.cs:163 | `Unchecked` | lambda MW.cs:163 | `MarkProjectDirty` | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 164 | `OptimizeMobiPackagingCheck`「优化超长小说编译速度」 | MW.cs:164 | `Checked` | lambda MW.cs:164 | `MarkProjectDirty` + `UpdateConversionSummary` MW.cs:4174 | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 165 | `OptimizeMobiPackagingCheck` | MW.cs:165 | `Unchecked` | lambda MW.cs:165 | 同 #164 | 不出 Desktop | 同上 |
| 166 | `MobiSyncCheck`「写入 ASIN/EBOK」 | MW.cs:166 | `Checked` | lambda MW.cs:166 | `MarkProjectDirty` | 不出 Desktop | 仅UI + 写项目快照（防抖） |
| 167 | `MobiSyncCheck` | MW.cs:167 | `Unchecked` | lambda MW.cs:167 | `MarkProjectDirty` | 不出 Desktop | 同上 |
| 168 | `MobiAsinText`（ASIN） | MW.cs:168 | `TextChanged` | lambda MW.cs:168 | `MarkProjectDirty` | 不出 Desktop | 同上 |
| 169 | `KindleGenArgsText`（额外参数） | MW.cs:169 | `TextChanged` | lambda MW.cs:169 | `MarkProjectDirty` | 不出 Desktop | 同上 |
| 170 | `ArtifactValidationCheck` | MW.cs:170 | `Checked` | lambda MW.cs:170 | `MarkProjectDirty` | 不出 Desktop | 仅UI + 写项目快照（防抖）。**与 #132 的 `Click` handler 并存** |
| 171 | `ArtifactValidationCheck` | MW.cs:171 | `Unchecked` | lambda MW.cs:171 | `MarkProjectDirty` | 不出 Desktop | 同上 |
| 172 | `ValidationRetentionCombo`（报告保留条数） | MW.cs:172 | `SelectionChanged` | lambda MW.cs:172 | `MarkProjectDirty` | 不出 Desktop | 同上 |
| 173 | MainWindow 自身 | MW.cs:183 | `Loaded` | `MainWindow_Loaded` MW.cs:599（async void） | `FavoriteFolderStore.LoadAsync` MW.cs:604、`MetadataMappingStore.LoadAsync` MW.cs:605、`AppSettingsStore.LoadAsync` MW.cs:609、`ConversionHistoryStore.LoadAsync` MW.cs:617、`OfferRecoveryAsync` MW.cs:627、`CheckForUpdatesInBackgroundAsync` MW.cs:640 | `FavoriteFolderStore.LoadAsync` Core\FavoriteFolderStore.cs:29、`MetadataMappingStore.LoadAsync` Core\MetadataMappingStore.cs:34、`AppSettingsStore.LoadAsync` Core\AppSettingsStore.cs:132、`ConversionHistoryStore.LoadAsync` Core\ConversionHistoryStore.cs:41、`EasyPubProjectStore.LoadAsync` Core\EasyPubProjectStore.cs:68 | 网络（检查更新）+ 写项目快照（`_recoveryTimer.Start` MW.cs:628 起开始周期写恢复快照） |
| 174 | MainWindow 自身 | MW.cs:184 | `Activated` | `MainWindow_Activated` MW.cs:686（async void） | `FinishInterruptedSourceEditsAsync` MW.cs:712、`RefreshPendingSourceEditsAsync` MW.cs:2046 | `SourceEditRecovery.RecoverAllAsync` Core\SourceEditTransaction.cs:503 → `RecoverAsync` Core\SourceEditTransaction.cs:305 → `File.Move(..., overwrite: true)` Core\SourceEditTransaction.cs:366 | **⚠️写TXT**（仅当存在上次中断的原文替换事务时；无确认弹窗，只写状态栏）。详见 §9.2 |
| 175 | MainWindow 自身 | MW.cs:185 | `Closing` | `MainWindow_Closing` MW.cs:726（async void） | `SaveRecoveryIfChangedAsync` MW.cs:751、`CaptureAppSettings` MW.cs:1131、`AppSettingsStore.SaveAsync` MW.cs:753、`UpdateExitHook.TryApplyOnExit` MW.cs:770、`Dispatcher.BeginInvoke(Close)` MW.cs:772 | `EasyPubProjectStore.SaveAsync` Core\EasyPubProjectStore.cs:91、`AppSettingsStore.SaveAsync` Core\AppSettingsStore.cs:155 | 写项目快照 + 弹窗（保存失败时 Yes/No）+ 改内存树（取消未完成操作 MW.cs:733–736）。**关闭被 `e.Cancel = true` 拦下再重入一次** |
| 176 | `_recoveryTimer`（`DispatcherTimer` 2 秒，MW.cs:50） | MW.cs:186 | `Tick` | `RecoveryTimer_Tick` MW.cs:1575（async void） | `SaveRecoveryIfChangedAsync` MW.cs:1581 | `EasyPubProjectStore.SaveAsync` Core\EasyPubProjectStore.cs:91 / `Delete` Core\EasyPubProjectStore.cs:130 | 写项目快照。**这是 §1 里所有「防抖落盘」的真实落盘者** |
| 177 | `OptionsTabs`（全局路由：`TextBox.TextChangedEvent`） | MW.cs:4258 | `AddHandler TextChanged` | `TrackedTextChanged` MW.cs:4265 | `TrackOptionChange` MW.cs:4279 → `MarkDirtyTab` MW.cs:4296 | 不出 Desktop | 仅UI（给分页标题加 ●）+ 写项目快照（防抖） |
| 178 | `OptionsTabs`（`Selector.SelectionChangedEvent`） | MW.cs:4259 | `AddHandler SelectionChanged` | `TrackedSelectionChanged` MW.cs:4267 | 同上 | 不出 Desktop | 同上 |
| 179 | `OptionsTabs`（`DatePicker.SelectedDateChangedEvent`） | MW.cs:4260 | `AddHandler SelectedDateChanged` | `TrackedDateChanged` MW.cs:4272 | 同上 | 不出 Desktop | 同上 |
| 180 | `OptionsTabs`（`ToggleButton.CheckedEvent`） | MW.cs:4261 | `AddHandler Checked` | `TrackedToggleChanged` MW.cs:4274 | 同上 | 不出 Desktop | 同上 |
| 181 | `OptionsTabs`（`ToggleButton.UncheckedEvent`） | MW.cs:4262 | `AddHandler Unchecked` | `TrackedToggleChanged` MW.cs:4274 | 同上 | 不出 Desktop | 同上 |
| 182 | `InputBookItem`（每个书稿对象） | MW.cs:1397 | `PropertyChanged` | `InputBook_PropertyChanged` MW.cs:1432 | `ScheduleAutomaticAnalysis` MW.cs:1447、`UpdateSelectedBookAnalysis` MW.cs:1454 | 不出 Desktop | 改内存树（触发重分析）+ 仅UI |
| 183 | 「放大预览」动态窗口 `preview` | MW.cs:2390 | `PreviewKeyDown` | lambda MW.cs:2390–2393 | `preview.Close()`（Esc） | 不出 Desktop | 仅UI（弹窗内快捷键） |
| 184 | 外置编辑器 `process`（`EditSourceText_Click` 内） | MW.cs:2035 | `Exited` | lambda MW.cs:2035 | `Dispatcher.BeginInvoke` → `RefreshPendingSourceEditsAsync` MW.cs:2046 | `ChapterTreeDocumentCache.Invalidate` Core\ChapterTreeDocumentCache.cs:75（经 MW.cs:2063） | 仅UI + 改内存树（检测到 TXT 被外部改动后重读；改的是**副本**） |
| 185 | `ChapterEditorWindow` 实例 `editor` | MW.cs:1961 | `Loaded` | lambda MW.cs:1961 | `editor.NavigateToSuggestion(diagnosticIssue)`（仅当 `sender` 是 `ConversionPreflightIssue`） | 不出 Desktop | 仅UI（问题定位跳转） |
| 186 | `ChapterEditorWindow.WorkingCopyCreated` 回调 | MW.cs:1956 | 委托赋值 | lambda MW.cs:1956 | `AddFiles([path])` MW.cs:3961、`_pendingSourceEdits[path] = SourceFileStamp.Capture(path)` MW.cs:2028 | `MetadataMappingResolver.Match` Core\MetadataMapping.cs:59 | 改内存树（工作副本入书库）+ 写盘（副本） |

---

## §3 代码里动态创建的 ContextMenu / MenuItem（20 条）

这些菜单**不在 XAML 里**，全部在运行时 new 出来。父级入口见 §1 #5 / #29 / #49 / #48。

| # | 界面元素（菜单项文本） | 位置（创建 + 绑定） | 事件 | 处理的 C# 方法 | 直接调用 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| 187 | 「新建项目」（项目菜单，MW.cs:228 标签 / MW.cs:233 绑定） | MW.cs:233 | Click | `NewProject_Click` MW.cs:1201（async void） | `EnsureCanReplaceProjectAsync` MW.cs:1203、`ApplyProfile` MW.cs:1210、`_recoveryStore.Delete` MW.cs:1214 | `EasyPubProjectStore.Delete` Core\EasyPubProjectStore.cs:130 | 改内存树 + 写项目快照（删除恢复快照）+ 弹窗（未保存时 Yes/No/Cancel） |
| 188 | 「打开项目…」 | MW.cs:233 | Click | `OpenProject_Click` MW.cs:1225（async void） | `OpenFileDialog` MW.cs:1228、`EasyPubProjectStore(dialog.FileName).LoadAsync` MW.cs:1238、`ApplyProjectDocument` MW.cs:1239 | `EasyPubProjectStore.LoadAsync` Core\EasyPubProjectStore.cs:68 | 弹窗 + 改内存树 + 写项目快照（删除恢复快照 MW.cs:1243） |
| 189 | 「保存项目」 | MW.cs:233 | Click | `SaveProject_Click` MW.cs:1256（async void）→ `SaveProjectInteractiveAsync(saveAs: false)` MW.cs:1262 | `CaptureProjectDocument` MW.cs:1325、`EasyPubProjectStore(savedPath).SaveAsync` MW.cs:1289 | `EasyPubProjectStore.SaveAsync` Core\EasyPubProjectStore.cs:91 | 写项目快照（+ 首次会弹 `SaveFileDialog`） |
| 190 | 「项目另存为…」 | MW.cs:233 | Click | `SaveProjectAs_Click` MW.cs:1259（async void）→ `SaveProjectInteractiveAsync(saveAs: true)` MW.cs:1262 | 同上 + `SaveFileDialog` MW.cs:1267 | 同上 | 弹窗 + 写项目快照 |
| 191 | 「全选全部书稿」（更多菜单） | MW.cs:327 | Click | `SelectAllFiles_Click` MW.cs:2533 | `FilesList.SelectAll` MW.cs:2536 | 不出 Desktop | 仅UI |
| 192 | 「清空书库」 | MW.cs:329 | Click | `ClearFiles_Click` MW.cs:2540 | `PushUndo` MW.cs:2543、`InputBooks.Clear` MW.cs:2544 | 不出 Desktop | 改内存树 |
| 193 | 「管理收藏文件夹」 | MW.cs:331 | Click | `ManageFavoriteFolders_Click` MW.cs:434 | `ManageFavoriteFoldersWindow` MW.cs:436 | 不出 Desktop | 弹窗 |
| 194 | 「撤销：{label}」/「撤销批量操作」 | MW.cs:333 | Click | `UndoLastBatch_Click` MW.cs:394 | `_undoStack.TryPop` MW.cs:396、`ApplyProjectDocument` MW.cs:397 | 不出 Desktop | 改内存树（回滚到快照） |
| 195 | 「编辑章节结构」（行内 ••• 菜单） | MW.cs:350 | Click | `EditChapters_Click` MW.cs:1904 | 同 §1 #58 | `ChapterTreeDocumentCache.GetOrLoadAsync` Core\ChapterTreeDocumentCache.cs:15 | 弹窗 + 改内存树 + 写项目快照 |
| 196 | 「编辑封面信息」 | MW.cs:352 | Click | lambda MW.cs:352 | `ShowWorkspacePage(WorkspacePage.Cover)` MW.cs:239 | 不出 Desktop | 仅UI |
| 197 | 「文本清理」 | MW.cs:354 | Click | `EditTextCleanup_Click` MW.cs:2084 | 同 §1 #60 | `EasyPubProjectStore.SaveAsync` Core\EasyPubProjectStore.cs:91 | 弹窗 + 改内存树 + 写项目快照 |
| 198 | 「在资源管理器中显示」 | MW.cs:356 | Click | lambda MW.cs:356–357 | `Process.Start("explorer.exe", /select,…)` | 不出 Desktop | 仅UI（外部进程） |
| 199 | 「从书库移除」 | MW.cs:359 | Click | lambda MW.cs:359–364 | `PushUndo("移除书稿")`、`InputBooks.Remove` MW.cs:362、`UpdateStatus` MW.cs:4040 | 不出 Desktop | 改内存树 |
| 200 | 「批量编辑元数据（N 本）」（列表右键菜单） | MW.cs:2869 | Click | `EditSelectedMetadata_Click` MW.cs:1768 | `EditMetadataBooks` MW.cs:1775 → `BatchMetadataWindow.ShowDialog` MW.cs:1779 | 不出 Desktop | 弹窗 |
| 201 | 「编辑章节结构」（右键） | MW.cs:2876 | Click | `EditChapters_Click` MW.cs:1904 | 同 #195 | 同 #195 | 弹窗 + 改内存树 + 写项目快照 |
| 202 | 「编辑封面信息」（右键） | MW.cs:2878 | Click | lambda MW.cs:2878 | `ShowWorkspacePage(Cover)` | 不出 Desktop | 仅UI |
| 203 | 「文本清理」（右键） | MW.cs:2880 | Click | `EditTextCleanup_Click` MW.cs:2084 | 同 §1 #60 | 同 §1 #60 | 弹窗 + 改内存树 + 写项目快照 |
| 204 | 「检查所选（N 本）」 | MW.cs:2888 | Click | `RunPreflight_Click` MW.cs:3477 | 同 §1 #130 | `BookAnalysisCoordinator.AnalyzeAsync` Core\BookAnalysisCoordinator.cs:83 | 弹窗 |
| 205 | 「转换所选（N 本）」 | MW.cs:2890 | Click | `Convert_Click` MW.cs:3712 | 同 §1 #143 | `BatchConverter.ConvertWithReportAsync` Core\BatchConverter.cs:34 | 写产物 + 弹窗 |
| 206 | 「从书库移除（N 本）」 | MW.cs:2892 | Click | `RemoveSelected_Click` MW.cs:2524 | 同 §1 #28 | 不出 Desktop | 改内存树 |

---

## §4 快捷键入口（`MainWindow_PreviewKeyDown`，10 条）

MainWindow.xaml **没有任何 `InputBindings` / `KeyBinding` / `Command` 绑定**（已全文件核对：非视觉属性里没有 `Command`、没有 `InputBindings`）。全部快捷键走一个 `PreviewKeyDown` 手写分发（MW.cs:4416–4472），手势由 `_shortcutBindings` 覆盖，默认值见 `ShortcutCatalog.All` Desktop\ShortcutCatalog.cs:9–21。匹配逻辑：`ShortcutCatalog.Matches` Desktop\ShortcutCatalog.cs:26（要求修饰键完全相等）。

| # | 界面元素（动作） | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| 207 | 快捷键 `add-files`（默认 Ctrl+O） | MW.cs:4419 | PreviewKeyDown | `MainWindow_PreviewKeyDown` MW.cs:4416 | `AddFiles_Click` MW.cs:1712 | `MetadataMappingResolver.Match` Core\MetadataMapping.cs:59 | 弹窗 + 改内存树（与 #24 完全等价） |
| 208 | `import-folder`（默认 Ctrl+Shift+O） | MW.cs:4424 | PreviewKeyDown | 同上 | `ImportFolder_Click` MW.cs:1723 | 同上 | 弹窗 + 改内存树（与 #25 等价） |
| 209 | `select-all`（默认 Ctrl+A，仅非文本输入时） | MW.cs:4429 | PreviewKeyDown | 同上 | `SelectAllFiles_Click` MW.cs:2533 | 不出 Desktop | 仅UI（与 #30/#191 等价） |
| 210 | `convert`（默认 Ctrl+Enter） | MW.cs:4434 | PreviewKeyDown | 同上 | `Convert_Click` MW.cs:3712 | `BatchConverter.ConvertWithReportAsync` Core\BatchConverter.cs:34 | 写产物 + 弹窗（与 #143/#205 等价） |
| 211 | `preflight`（默认 Ctrl+Shift+P） | MW.cs:4439 | PreviewKeyDown | 同上 | `RunPreflight_Click` MW.cs:3477 | `BookAnalysisCoordinator.AnalyzeAsync` Core\BookAnalysisCoordinator.cs:83 | 弹窗（与 #130/#140/#204 等价） |
| 212 | `save-project`（默认 Ctrl+S） | MW.cs:4444 | PreviewKeyDown | 同上 | `SaveProject_Click` MW.cs:1256 | `EasyPubProjectStore.SaveAsync` Core\EasyPubProjectStore.cs:91 | 写项目快照（与 #189 等价） |
| 213 | `settings`（默认 Ctrl+,） | MW.cs:4449 | PreviewKeyDown | 同上 | `ShowSettings_Click` MW.cs:444 | `AppSettingsStore.SaveAsync` Core\AppSettingsStore.cs:155 | 弹窗（与 #9 等价） |
| 214 | `pause`（默认 Ctrl+Space） | MW.cs:4454 | PreviewKeyDown | 同上 | `PauseResume_Click` MW.cs:3861 | `BatchExecutionControl.Pause/Resume` Core\ConversionConcurrencyPolicy.cs:30/:37 | 仅UI（与 #141 等价） |
| 215 | `focus-search`（默认 Ctrl+F） | MW.cs:4459 | PreviewKeyDown | 同上 | `BookSearchText.Focus()` MW.cs:4461 | 不出 Desktop | 仅UI |
| 216 | `cycle-focus`（默认 F6） | MW.cs:4464 | PreviewKeyDown | 同上 | 在 `FilesList`→`OutputDirectoryText`→`OptionsTabs`→`ConvertButton` 间轮转 MW.cs:4466–4469 | 不出 Desktop | 仅UI |

**Dispatcher 提示**：整个分发在 UI 线程同步执行；被调用的 `Convert_Click` / `RunPreflight_Click` / `ShowSettings_Click` 都是 `async void`，会把长任务交给后续的 `await`（`Task.Run` / `ConvertWithReportAsync`）而不阻塞按键处理。

---

## §5 `MainWindow.Performance.cs`：动态创建的 GridSplitter（3 条）

`InitializePreviewSplitter` MW.Perf.cs:40 在构造期 new 出 `GridSplitter`（`Name = "LayoutPreviewSplitter"`），**没有任何 XAML 声明**，事件全部代码挂接。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| 217 | `LayoutPreviewSplitter`（排版页设置/预览分栏） | MW.Perf.cs:56 | `DragCompleted` | lambda MW.Perf.cs:56 | 记 `_preferredLayoutSettingsWidth` MW.Perf.cs:38 | 不出 Desktop | 仅UI（记住宽度，下次 `MainWindow_SizeChanged` 复用 MW.cs:4400） |
| 218 | `LayoutPreviewSplitter` | MW.Perf.cs:57 | `KeyUp`（←/→） | lambda MW.Perf.cs:57–60 | 同上 | 不出 Desktop | 仅UI（键盘微调同样记宽） |
| 219 | `LayoutPreviewSplitter` | MW.Perf.cs:61 | `MouseDoubleClick` | lambda MW.Perf.cs:61–66 | 重置 `LayoutSettingsColumn.Width` MW.Perf.cs:64、`args.Handled = true` | 不出 Desktop | 仅UI（双击恢复默认宽度） |

---

## §6 `BookSourcePanel.cs` —— **这是 `ChapterEditorWindow` 的 partial，不是 MainWindow 的**（11 条）

重要事实：`BookSourcePanel.cs:17` 声明的是 `public partial class ChapterEditorWindow`。文件里唯一的入口方法 `BookSources_Click` MW-无关（`BookSourcePanel.cs:142`），由 `ChapterCatalogPanel.cs:52` 的「书源设置」按钮触发，而 `ChapterCatalogPanel` 又只在章节编辑器里出现。

因此本节 11 条入口**不在主流程窗口的 UI 树上**：它们属于「主窗口 → 章节编辑器 → 目录面板 → 书源设置」这条三层深链。列在这里是因为任务点名了该文件；如果要按「主流程窗口入口」计数，可以把这 11 条视为**不可从主窗口直接触达**。

| # | 界面元素（代码变量名） | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| 220 | `close`「关闭」（目录预览弹窗，BookSourcePanel.cs:62） | BookSourcePanel.cs:63 | Click | lambda BookSourcePanel.cs:63 | `dialog.Close()` | 不出 Desktop | 仅UI |
| 221 | `up`「上移」 | BookSourcePanel.cs:280 | Click | lambda BookSourcePanel.cs:280 → 局部函数 `Move` BookSourcePanel.cs:267 | `Renumber` BookSourcePanel.cs:260 | `BookSourceCatalog.Renumber` Core\BookSource.cs:125 | 仅UI（改内存顺序，保存前不落盘） |
| 222 | `down`「下移」 | BookSourcePanel.cs:281 | Click | lambda BookSourcePanel.cs:281 → `Move(1)` BookSourcePanel.cs:267 | 同上 | 同上 | 仅UI |
| 223 | `grid`（`DataGrid` 书源列表） | BookSourcePanel.cs:282 | SelectionChanged | lambda BookSourcePanel.cs:282–287 | 回填 `cookie.Password`、`remove.IsEnabled` | 不出 Desktop | 仅UI |
| 224 | `remove`「删除书源」 | BookSourcePanel.cs:289 | Click | lambda BookSourcePanel.cs:289–295 | `rows.Remove` + `Renumber` | `BookSourceCatalog.Renumber` Core\BookSource.cs:125 | 仅UI |
| 225 | `saveCookie`「填到所选书源」 | BookSourcePanel.cs:296 | Click | lambda BookSourcePanel.cs:296–302 | `row.Bind(row.Source with { Cookie = … })` | 不出 Desktop | 仅UI（Cookie 仅存内存，保存时才落盘） |
| 226 | `add`「添加」（自定义书源） | BookSourcePanel.cs:303 | Click | lambda BookSourcePanel.cs:303–329 | `ReferenceCatalogClient.Supported` BookSourcePanel.cs:310、`BookSourceCatalog.CustomId` BookSourcePanel.cs:315 | `ReferenceCatalogClient.Supported` Core\ReferenceCatalog.cs:127、`BookSourceCatalog.CustomId` Core\BookSource.cs:129 | 仅UI |
| 227 | `probe`「检测可用性」 | BookSourcePanel.cs:330 | Click | lambda BookSourcePanel.cs:330–349（async 委托） | `new ReferenceCatalogClient().ProbeAsync(row.Source, name, token)` BookSourcePanel.cs:342 | `ReferenceCatalogClient.ProbeAsync` Core\ReferenceCatalog.cs:671 | **网络**（批量探测所有书源，40 秒 `CancellationTokenSource` BookSourcePanel.cs:336）。注意 `async (_, _) =>` 挂在 `Click` 上，异常在 lambda 内被 catch BookSourcePanel.cs:343 |
| 228 | `openSource`「浏览器打开」 | BookSourcePanel.cs:350 | Click | lambda BookSourcePanel.cs:350–370 | `ReferenceCatalogClient.BuildSearchUri` BookSourcePanel.cs:365、`Process.Start` BookSourcePanel.cs:366 | `ReferenceCatalogClient.BuildSearchUri` Core\ReferenceCatalog.cs:639 | 仅UI + 外部进程（**会打开浏览器，属于出网动作但由用户自己发起**） |
| 229 | `preview`「预览目录」 | BookSourcePanel.cs:371 | Click | lambda BookSourcePanel.cs:371–394（async 委托） | `new ReferenceCatalogClient().FetchFromSourceAsync(row.Source, name, token)` BookSourcePanel.cs:383、`ShowCatalogPreview` BookSourcePanel.cs:23 | `ReferenceCatalogClient.FetchFromSourceAsync` Core\ReferenceCatalog.cs:588 | **网络** + 弹窗（目录预览窗口 BookSourcePanel.cs:25–66） |
| 230 | `save`「保存并关闭」 | BookSourcePanel.cs:395 | Click | lambda BookSourcePanel.cs:395–410（async 委托） | `store.SaveAsync(ordered)` BookSourcePanel.cs:401、`ReferenceCatalogClient.Sources = ordered` BookSourcePanel.cs:403 | `BookSourceStore.SaveAsync` Core\BookSource.cs:221、`BookSourceCatalog.Renumber` Core\BookSource.cs:125 | 写项目快照（书源配置文件）+ 网络配置生效（改写 Core 静态属性 `Sources`） |

---

## §7 TaskCenterWindow.xaml（9 条）

窗口由 §1 #11 `ShowTaskCenter_Click` → `ShowTaskCenterAsync` MW.cs:3623 创建（MW.cs:3640），并在 MW.cs:3641–3656 订阅它的四个事件回传主窗口。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| 231 | `TasksGrid`（当前任务表） | TCW.xaml:20 | SelectionChanged | `TasksGrid_SelectionChanged` TCW.xaml.cs:79 | `UpdateSelectedDetails` TCW.xaml.cs:82 | 不出 Desktop | 仅UI |
| 232 | 「查看报告」 | TCW.xaml:35 | Click | `OpenReport_Click` TCW.xaml.cs:94 | `Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })` TCW.xaml.cs:102 | 不出 Desktop | 仅UI（外部进程）+ 无报告时 `InkDialog.Show` TCW.xaml.cs:99（弹窗） |
| 233 | 「打开输出目录」 | TCW.xaml:35 | Click | `OpenOutput_Click` TCW.xaml.cs:105 | `Process.Start("explorer.exe", /select,…)` TCW.xaml.cs:111 | 不出 Desktop | 仅UI（外部进程） |
| 234 | 「重试所选」 | TCW.xaml:35 | Click | `Retry_Click` TCW.xaml.cs:114 | `RetryRequested?.Invoke(item.InputPath)` TCW.xaml.cs:117、`Close()` | 不出 Desktop（事件回传） | 改内存树（**经由主窗口订阅 MW.cs:3641–3649：清空书库 → 只放回该书的 `Clone()` → 选中**） |
| 235 | 「重新检查当前书稿」 | TCW.xaml:41 | Click | `RunPreflight_Click` TCW.xaml.cs:121 | `PreflightRequested?.Invoke()` TCW.xaml.cs:121 | `BookAnalysisCoordinator.AnalyzeAsync` Core\BookAnalysisCoordinator.cs:83（经 MW.cs:3651 → §1 #130） | 弹窗 |
| 236 | `PreflightGrid`（检查结果表） | TCW.xaml:42 | MouseDoubleClick | `PreflightGrid_MouseDoubleClick` TCW.xaml.cs:123 | `LocateSelectedPreflight` TCW.xaml.cs:125 → `NavigatePreflightRequested?.Invoke(issue)` | 视分支而定（经 MW.cs:3652 → `NavigateToPreflightIssue` MW.cs:3536） | 弹窗（**双击后还会 `_taskCenterWindow?.Close()` MW.cs:3655**） |
| 237 | 「定位所选问题」 | TCW.xaml:45 | Click | `LocatePreflight_Click` TCW.xaml.cs:122 | 同 #236 | 同上 | 弹窗 |
| 238 | 「载入所选失败项」（转换历史页） | TCW.xaml:54 | Click | `RetryHistory_Click` TCW.xaml.cs:131 | `RetryHistoryRequested?.Invoke(paths)` TCW.xaml.cs:141、`Close()` | `MetadataMappingResolver.Match` Core\MetadataMapping.cs:59（经 MW.cs:3650 → `LoadRetryInputs` MW.cs:3704 → `AddFiles`） | 改内存树 + 弹窗（无失败项时 `InkDialog.Show` TCW.xaml.cs:138） |
| 239 | 「关闭」 | TCW.xaml:59 | Click | `Close_Click` TCW.xaml.cs:145 | `Close()` | 不出 Desktop | 仅UI |

---

## §8 ConversionHistoryWindow.xaml（2 条）

窗口由三处创建：§1 #143 转换失败分支 MW.cs:3828、§1 #11 任务中心无关、以及 `ShowHistory_Click` MW.cs:3602（窗口创建在 MW.cs:3612）（**死代码**，见 §9）。返回 `DialogResult=true` 时调用方读 `RetryInputPaths`。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| 240 | 「关闭」 | CHW.xaml:23 | Click | `Close_Click` CHW.xaml.cs:41 | `DialogResult = false` | 不出 Desktop | 仅UI |
| 241 | 「载入选中/全部失败项」 | CHW.xaml:24 | Click | `Retry_Click` CHW.xaml.cs:20 | `HistoryGrid.SelectedItems` 过滤 + `RetryInputPaths` 赋值 CHW.xaml.cs:29、`DialogResult = true` CHW.xaml.cs:38 | 不出 Desktop | 仅UI（返回值由调用方消费）。无失败项时走 `InkDialog.Show` CHW.xaml.cs:35（弹窗）。**调用方 MW.cs:3829 / MW.cs:3613 会 `LoadRetryInputs` → `AddFiles` → 改内存树** |

---

## §9 汇总分析

### 9.1 总入口数

**241 条**（编号 1–241 连续，无省略）：

| 分区 | 条数 |
|---|---|
| §1 MainWindow.xaml 静态绑定 | 147 |
| §1b EventSetter（并入 §1 尾号为 #148） | 1 |
| §2 代码 `+=` 动态绑定 | 38 |
| §3 动态 ContextMenu / MenuItem | 20 |
| §4 快捷键分支 | 10 |
| §5 Performance.cs 分栏输入 | 3 |
| §6 BookSourcePanel.cs（ChapterEditorWindow） | 11 |
| §7 TaskCenterWindow.xaml | 9 |
| §8 ConversionHistoryWindow.xaml | 2 |
| **合计** | **241** |

若把 §6 那 11 条（不可从主窗口直接触达）剔除，**主流程窗口自身入口 = 230 条**。

### 9.2 `⚠️写TXT` 入口（会覆写用户原始 TXT 文件）

**只有 1 条真正写原始 TXT：**

| # | 名称 | 触发方式 | 写盘点 |
|---|---|---|---|
| #174 | `MainWindow_Activated` → `FinishInterruptedSourceEditsAsync` → `SourceEditRecovery.RecoverAllAsync` | **窗口首次激活时自动执行**（MW.cs:184 / MW.cs:712），无确认弹窗，只写状态栏 | `File.Move(sibling, journal.SourcePath, overwrite: true)` Core\SourceEditTransaction.cs:366（`RepairRecoveryAction.ContinueReplace` 分支） |

触发前提：`SourceBackupStore.CreateDefault().DirectoryPath` 下存在 `Pending` 事务（上一次「原文替换」在写临时文件后、重命名前崩溃）。正常使用不会命中；一旦命中，程序**会自己把临时文件改名覆盖到原 TXT**。这是本清单里唯一的高危入口，且它没有 UI 按钮——用户看不到它发生。

**容易误判为 `⚠️写TXT`、但实际不写原稿的入口**（都不改原始 TXT）：

| # | 名称 | 实际行为 |
|---|---|---|
| 56 / 65 | `EditSourceText_Click`（「编辑 TXT」两个入口） | `SourceBackupStore.CreateWorkingCopy` Core\SourceBackupStore.cs:20 → 先 `EnsureBackup` 备份原稿（Core:66），再 `File.Copy` 到 `WorkingCopies\<guid>\`（Core:26），然后把**副本**加入书库并用外置编辑器打开副本。状态栏原文：「已打开并导入独立 TXT 副本…原稿保持不变」（MW.cs:2037） |
| #184 | `process.Exited` → `RefreshPendingSourceEditsAsync` | 只检测副本 `LastWrite` 变化并重读（MW.cs:2060–2075），不写原文 |
| #186 | `ChapterEditorWindow.WorkingCopyCreated` | 新副本入书库 |
| #22 | `ManageSourceBackups_Click`（「原文备份…」） | 主窗口只负责打开 `SourceBackupWindow`。**该子窗口内部的「恢复」按钮会写原始 TXT**，但它不在本次盘点文件范围内，故未计入上表——重写时不要漏掉它 |
| 60 / 67 / 197 / 203 | `EditTextCleanup_Click`（4 个入口） | 文本清理只在转换时作用于产物；界面文案明确「不修改源文件」（MW.cs:2140） |

### 9.3 死 UI / 死代码

**A. 控件永远 `Visibility="Collapsed"`（XAML 写死，没有任何代码把它设回 Visible）——8 处**

| 元素 | 位置 | 说明 |
|---|---|---|
| `PreviewBookButton`「成品预览」 | MW.xaml:95 | 与 #10 绑定；同一功能还有 #62「预览」按钮（也在隐藏容器里）和快捷键路径。**功能实际不可达** |
| `TaskCenterButton`「任务中心」 | MW.xaml:95 | 与 #11 绑定；实际入口是 #21 侧栏「转换记录与验收」→ `NavigateTasks_Click` → `ShowWorkspacePage(Tasks)`（只切页面，**不打开窗口**）。**窗口版入口不可达** |
| `HeaderActionsPanel` | MW.xaml:95 | 空 `StackPanel`，从未填充 |
| `ChaptersNavigationButton`「章节正文」 | MW.xaml:108 | #19 被重定向到 `EditChapters_Click`；且 `ShowWorkspacePage` 开头就把 `Chapters` 页强制改写为 `Library`（MW.cs:242），所以这个页面永不存在 |
| `CoverNavigationButton`「封面信息」 | MW.xaml:109 | 按钮死，但 `NavigateCover_Click` 方法活得很好（#32/#59 都在用） |
| `SelectAllFilesButton`「全选」 | MW.xaml:121 | #30 死；等价功能由 #43/#50 两个 CheckBox 提供 |
| `ClearFilesButton`「清空」 | MW.xaml:121 | #31 死；等价功能在「更多」菜单 #192 |
| 含 `QuickIllustrationButton`/`QuickPreviewButton` 的 `WrapPanel` | MW.xaml:183 | #61/#62 死；等价功能是 #119 `InkManageIllustrationsButton` 与（同样死的）#10 |

**B. 整块被隐藏的旧版 UI（`<Grid Visibility="Collapsed">`，MW.xaml:352–354）——4 个绑定仍挂着**

`FavoriteFolderCombo_SelectionChanged`(#144)、`AddFromFavoriteFolder_Click`(#145)、`RemoveFavoriteFolder_Click`(#146)、`PresetCombo_SelectionChanged`(#147) 都绑在**用户点不到**的控件上。其中 #145/#148 仍会真正执行 `AddFiles`（如果被代码调用的话），#148 恰恰会调用它——这是一个「隐藏控件 + 隐藏按钮」的隐式耦合链。

**C. C# 里没有任何引用的处理方法（真死代码，全仓 grep 只出现 1 次）——4 个**

| 方法 | 位置 | 说明 |
|---|---|---|
| `BrowseFont_Click` | MW.cs:2585 | `OpenFileDialog` + `FontEmbeddingService.Inspect`；XAML 无绑定。活着的对应物是 `BrowseVisibleFont_Click` MW.cs:2287（#113） |
| `ClearFont_Click` | MW.cs:2601 | 同上；活着的对应物是 `ClearVisibleFont_Click` MW.cs:2313（#116） |
| `ShowHistory_Click` | MW.cs:3602 | 会 new `ConversionHistoryWindow` 并 `LoadRetryInputs`。XAML 无绑定 → **转换历史窗口的独立入口不存在了**，只能从转换失败时弹出（MW.cs:3828）或任务中心进 |
| `ScheduleStatusUpdate` | MW.cs:4065 | 唯一会 `Start` `_statusRefreshTimer` 的地方 → 120ms 状态刷新定时器（#155）**永不触发** |

**D. 事件名与方法名错位（不是死 UI，但会误导重写者）**

`MW.xaml:163` 写的是 `PreviewMouseRightButtonUp="FilesList_PreviewMouseRightButtonDown"`：事件是 **Up**，方法名是 **Down**（MW.cs:2851）。行为按 Up 触发。

### 9.4 最复杂的 3 个入口

1. **#143 `Convert_Click`（MW.cs:3712，async void）** —— 单方法内串起了「构造请求 → 复用/重跑预检 → 错误分流 → 并发批处理 → 写历史 → 任务中心 UI 回填 → 打开资源管理器 → 失败时再开历史窗口」全部阶段，还各自带 `CancellationToken`、`BatchExecutionControl` 暂停门、`DispatcherThrottledProgress`（MW.cs:3763，100ms 节流切回 UI 线程）和 `finally` 恢复按钮态，是唯一同时产生「写产物 + 弹窗 + 外部进程」三类副作用的入口。
2. **#174 `MainWindow_Activated` → `FinishInterruptedSourceEditsAsync`（MW.cs:686 → MW.cs:712，async void）** —— 唯一 `⚠️写TXT` 入口，却完全无 UI、无确认；它依赖一个 `static` 守卫（MW.cs:714 `_sourceEditsFinished`）保证「每进程一次」，其行为由磁盘上是否存在 `Pending` 事务、以及 `RepairRecoveryPlanner.Decide` 的九宫格（Core\SourceEditTransaction.cs:317–318）共同决定，触发条件无法从界面观察。
3. **#44 `FilesList_SelectionChanged`（MW.cs:2715）** —— 一个 SelectionChanged 里混合了选中态同步（`_syncingSelectedBook` 防回环 MW.cs:2726–2733）、6 个按钮的可用性、正文/封面两条异步预览链（`RefreshSelectionPreviewsAsync` MW.cs:2784，带 150ms 延迟 + `CancellationTokenSource` 换代取消 MW.cs:2748–2753），而且它还会被 #45/#46/#47 的刷选逻辑以高频方式反复触发。

### 9.5 同名 / 同功能多入口（重复）

| 功能 | 入口 |
|---|---|
| **打开章节树编辑器** | #19（被重定向）、#58 `QuickChapterButton`、#66 `OpenChapterTreeWorkspaceButton`、#195 行内 ••• 菜单、#201 列表右键菜单（共 5 条，其中 1 条是重定向） |
| **编辑 TXT（外置编辑器打开副本）** | #56 `QuickEditTextButton`、#65 `EditSourceTextButton`（2 条，逻辑完全相同） |
| **文本清理** | #60 `QuickCleanupButton`、#67 `OpenTextCleanupWorkspaceButton`、#197 行内菜单、#203 右键菜单，外加 #38 `WorkflowIssue_Click` 在 `PreflightTargetKind.TextCleanup` 分支里第三次调用（共 5 条） |
| **切换工作区页** | #15–#21 侧栏 7 个 RadioButton，加上 #32/#33/#34（封面/排版/转换的页内跳转按钮）、#39「下一步：制作设置 →」、#59 `QuickMetadataButton`、#196/#202 菜单项 —— 仅「去封面页」就有 #20、#32、#59、#196、#202 五条 |
| **检查问题（Preflight）** | #37 `WorkflowRefreshButton`、#130「查看检查结果 ›」、#140 `RunPreflightButton`、#204 右键菜单、#211 快捷键、#235 任务中心按钮、#57 `ViewSelectedBookIssuesButton`（单书变体）—— 7 条 |
| **开始转换** | #143 `ConvertButton`、#205 右键菜单、#210 Ctrl+Enter —— 3 条 |
| **打开转换设置** | #18 `ConvertNavigationButton`（顺带开）、#34「输出设置…」、#129 `AdjustConversionSettingsButton`、#151 面板回调再开 —— 4 条 |
| **添加书稿** | #24 `AddBooksButton`、#145（隐藏按钮）、#148（隐藏菜单项，间接调 #145）、#207 Ctrl+O、#208 Ctrl+Shift+O（文件夹变体）、#25 导入文件夹、#139 `RetryFailedButton`（经 `LoadRetryInputs`→`AddFiles`）、#238 任务中心载入失败项、#241 历史窗口载入失败项 —— 9 条最后都汇到 `AddFiles` |
| **主题切换** | #7 `LightThemeButton`、#8 `DarkThemeButton`，另在 `ShowSettingsAsync` 方法体内 MW.cs:566 与 `ApplyAppSettings` 方法体内 MW.cs:959 各有一份设置入口 |
| **同一个控件被绑两次** | `FormatCombo`：#136（XAML `SelectionChanged`）+ #158（代码 `+=`）；`ArtifactValidationCheck`：#132（XAML `Click`）+ #170/#171（代码 `+= Checked/Unchecked`）。**两套机制分别负责「刷新 UI」与「标脏」，重写时应合并成一个** |
| **同一个定时器语义被两套代码写** | `_statusRefreshTimer`（#155）注册了 Tick 却无人 Start；`_recoveryTimer`（#176）才是真正周期性落盘的那个 |

### 9.6 异步与 Dispatcher 提示（重写时最容易踩）

| 位置 | 形态 | 线程/Dispatcher 处理 |
|---|---|---|
| MW.cs:1904 `EditChapters_Click` | `async void` | 全程 UI 线程；`await GetChapterDocumentAsync`（缓存内部可能后台读盘）后直接操作控件 |
| MW.cs:3712 `Convert_Click` | `async void` | `new DispatcherThrottledProgress<BatchConversionProgress>(Dispatcher, 100ms, …)` MW.cs:3763 —— 把 Core 的进度回调节流并切回 UI 线程 |
| MW.cs:1492 `RunAutomaticAnalysisAsync` | `async Task` | `Task.Run(() => _analysisCoordinator.AnalyzeAsync(...))` MW.cs:1517 明确切到线程池，回来后有 `generation` 校验 MW.cs:1518 |
| MW.cs:2926 `RefreshInlineChapterPreviewAsync` | `async Task` | 开头 `Dispatcher.CheckAccess()` + `Dispatcher.InvokeAsync(...).Task.Unwrap()` MW.cs:2928–2931；后段用 `ConfigureAwait(false)` 再 `Dispatcher.InvokeAsync` 回 UI MW.cs:2971/2977 |
| MW.cs:686 `MainWindow_Activated` / MW.cs:726 `MainWindow_Closing` | `async void` | `Closing` 里先 `e.Cancel = true` 再 `await` 保存，最后 `Dispatcher.BeginInvoke(Close)` MW.cs:772 重入关闭 |
| MW.cs:2035 `process.Exited` / MW.cs:2390 `PreviewKeyDown` | lambda | 都通过 `Dispatcher.BeginInvoke` 回到 UI 线程（MW.cs:2035） |
| MW.cs:2098 / MW.cs:3521（`ViewSelectedBookIssues_Click`） | `async void` | `_analysisCoordinator.AnalyzeAsync` 未包 `Task.Run`，缓存在 Core 侧；`PreflightWindow` 在 UI 线程 `ShowDialog` |
| BookSourcePanel.cs:330 / :371 / :395 | `async (_, _) =>` 匿名委托挂在 `Click` 上 | 等价于 `async void`，异常必须在 lambda 内自行 catch（三处都做了） |

