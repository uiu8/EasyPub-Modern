# 前端交互入口盘点 C —— 设置类与杂项窗口

> 盘点对象：EasyPub Modern（`D:\software\DeepSeek-Harness\workspace\easypub-branch`）
> 范围：**除主窗口（`MainWindow`）与章节修复/编辑窗口（`ChapterEditorWindow` / `RepairReviewWindow` / `ChapterWorkbench` / `ChapterReviewPanel` 等）之外**的全部窗口与相关前端支撑文件。
> 所有 `文件:行` 均来自对仓库源码的实际读取，未做推测。

## 0. 覆盖范围与入口总数

### 0.1 覆盖文件（**44 个 .cs/.xaml，全部实读**；下表按 xaml/xaml.cs 成对合并展示）

| # | 文件（组） | 行数 | 类型 |
|---|---|---|---|
| 1 | `src\EasyPub.Desktop\SettingsWindow.xaml` | 64 | Window |
| 2 | `src\EasyPub.Desktop\SettingsWindow.xaml.cs` | 380 | 代码 |
| 3 | `src\EasyPub.Desktop\ConversionSettingsWindow.xaml` | 362 | **UserControl** |
| 4 | `src\EasyPub.Desktop\ConversionSettingsWindow.xaml.cs` | 398 | 代码 |
| 5 | `src\EasyPub.Desktop\ConversionSettingsDraft.cs` | 115 | 纯逻辑 |
| 6 | `src\EasyPub.Desktop\BatchMetadataWindow.xaml` / `.xaml.cs` | 36 / 89 | Window |
| 7 | `src\EasyPub.Desktop\MetadataMappingWindow.xaml` / `.xaml.cs` | 66 / 86 | Window |
| 8 | `src\EasyPub.Desktop\MetadataMappingRuleWindow.xaml` / `.xaml.cs` | 40 / 74 | Window |
| 9 | `src\EasyPub.Desktop\PresetManagerWindow.xaml` / `.xaml.cs` | 22 / 59 | Window |
| 10 | `src\EasyPub.Desktop\CustomCssWindow.xaml` / `.xaml.cs` | 35 / 77 | Window |
| 11 | `src\EasyPub.Desktop\TextCleanupWindow.xaml` / `.xaml.cs` | 97 / 546 | Window |
| 12 | `src\EasyPub.Desktop\TextCleanupRuleManagerWindow.xaml` / `.xaml.cs` | 31 / 244 | Window |
| 13 | `src\EasyPub.Desktop\TextCleanupPreviewNavigator.cs` | 145 | 纯逻辑 |
| 14 | `src\EasyPub.Desktop\IllustrationManagerWindow.xaml` / `.xaml.cs` | 48 / 257 | Window |
| 15 | `src\EasyPub.Desktop\IllustrationPositionWindow.xaml` / `.xaml.cs` | 64 / 113 | Window |
| 16 | `src\EasyPub.Desktop\CoverLightboxWindow.xaml` / `.xaml.cs` | 26 / 15 | Window |
| 17 | `src\EasyPub.Desktop\BookPreviewWindow.xaml` / `.xaml.cs` | 29 / 27 | Window |
| 18 | `src\EasyPub.Desktop\PreflightWindow.xaml` / `.xaml.cs` | 42 / 142 | Window |
| 19 | `src\EasyPub.Desktop\FavoriteFolderManagerWindow.xaml` / `.xaml.cs` | 19 / 51 | Window |
| 20 | `src\EasyPub.Desktop\ShortcutManagerWindow.xaml` / `.xaml.cs` | 11 / 47 | Window |
| 21 | `src\EasyPub.Desktop\AdvertisementConditionWindow.xaml` / `.xaml.cs` | 27 / 113 | Window |
| 22 | `src\EasyPub.Desktop\InkDialog.cs` | 143 | **纯代码窗口** |
| 23 | `src\EasyPub.Desktop\SourceBackupWindow.cs` | 493 | **纯代码窗口** |
| 24 | `src\EasyPub.Desktop\SourceRestoreWindow.cs` | 106 | **纯代码窗口** |
| 25 | `src\EasyPub.Desktop\App.xaml` / `App.xaml.cs` | 420 / 17 | 应用级 |
| 26 | `src\EasyPub.Desktop\ThemeManager.cs` | 157 | 支撑 |
| 27 | `src\EasyPub.Desktop\ShortcutCatalog.cs` | 47 | 支撑 |
| 28 | `src\EasyPub.Desktop\MotionEffects.cs` | 62 | 支撑 |

### 0.2 入口总数（逐文件正则统计 + 人工核对）

| 分类 | 数量 |
|---|---|
| XAML `Click="..."` 绑定 | **109** |
| XAML `Command="..."` 绑定 | 4（**全部**是 `App.xaml:86/88/102/104` 的框架滚动命令 `ScrollBar.PageUp/Down/Left/RightCommand`，无业务命令） |
| 代码 `.Click +=` 动态绑定 | **16** |
| 代码 `AddHandler(...)` 全局事件订阅 | 3（`ConversionSettingsWindow.xaml.cs:26-28`） |
| XAML `SelectionChanged="..."` | 15 |
| XAML `Checked="..."` / `Unchecked="..."` | 4 / 2 |
| XAML `TextChanged="..."` | 2 |
| XAML `MouseDoubleClick="..."` | 4 |
| XAML `KeyDown="..."` | 1 |
| 代码 `.Checked +=` / `.Unchecked +=` / `.CollectionChanged +=` / `.PropertyChanged +=` | 5 |
| 代码 `Loaded` / `Closed` / `Tick +=`（生命周期） | 10 |
| 拖放事件（`Drop` / `DragOver` / `AllowDrop`） | **0** —— 范围内 44 个文件全部没有拖放处理；全项目仅 `MainWindow.xaml:6/9/166/257` 与 `ChapterEditorWindow.xaml:182` 有，二者均在本次范围之外 |
| 事件绑定小计 | **175** |
| 无事件的输入控件行 + 非事件公开 API 入口行（`S31` / `C29` / `C32` / `SR6`） | 54 |
| 纯逻辑与支撑文件成员（无 UI 入口：`CD1-4`、`N1-3`、`TH1-4`、`SC1-4`、`ME1-4`） | 19 |
| **映射表条目合计（第 5 节）** | **248** |

> 口径说明：同一控件若同时被「XAML 事件」和「保存时读取」两条路径命中（例如 `DensityCombo`），只在表中占一行，事件列写「无（提交时读取）」。
> 除映射表外，另有三张专表：⚠️写 TXT 入口 **2** 条（第 1 节）、死 UI **7** 条（第 3 节）、自动执行逻辑 **20** 条（第 4 节）。

---

## 1. ⚠️ 写 TXT 入口清单（最高优先级）

**结论：范围内共 2 个入口会真正改写用户 TXT 原文，全部集中在 `SourceBackupWindow`，且都要经过 `SourceRestoreWindow` 或 `InkDialog` 的二次确认。**

| # | 入口 | 位置 | 确认步骤 | 事务边界 | 落到 Core |
|---|---|---|---|---|---|
| W1 | 「恢复此版本…」`_restore` | `SourceBackupWindow.cs:48`（控件）/ `:160`（绑定）/ `Restore_Click` `:277` | ① 必须先选中**恰好 1 条**备份（`:279`，否则 `InkDialog` 提示）；② 原文必须还存在（`:282`，否则 `InkDialog`）；③ **弹出 `SourceRestoreWindow` 模态窗**（`:285`），用户选「只恢复原文」或「恢复原文与当时的章节树」后点「开始恢复」（`SourceRestoreWindow.cs:63-64`）；④ 只有 `ShowDialog() == true` 才继续（`:286`） | `RestoreSelectedAsync` `:240` → `SourceTransitionState.LoadFromAsync`（读当前状态）`:260` → `new RestoreTransaction(...).ExecuteAsync(...)` `:261-262`。Core 侧：`RestoreTransaction.ExecuteAsync`（`RestoreTransaction.cs:111`）先做 5 项 preflight 拒绝（`:124-162`，文件不存在/版本已相同/被外部改过/不可写/无法对齐），再构造 `SourceEditTransaction`（`:177`）并调用 `SourceEditTransaction.ExecuteAsync`（`SourceEditTransaction.cs:111`）。**写盘在 6 步事务的第 5 步 `ReplaceAtomically()`（`SourceEditTransaction.cs:166`），第 2 步先备份（`:140-147`），每一步都先落 journal（`:133/144/154/167`）**。 |
| W2 | 「完成未完成的原文修改…」`_finish` | `SourceBackupWindow.cs:51`（控件）/ `:163`（绑定）/ `FinishPending_Click` `:316` | ① 若 `SourceEditRecovery.Outstanding` 为 0 直接返回（`:318-323`，不弹窗）；② 否则 `InkDialog.Show(..., YesNo, Question)` 确认「有 N 条原文修改没有收尾…是否继续？」（`:324-327`） | `FinishPendingAsync` `:295` → `SourceEditRecovery.RecoverAllAsync(_store.DirectoryPath)` `:300`。Core 侧：`SourceEditRecovery.RecoverAllAsync`（`SourceEditTransaction.cs:503`）→ 对 `Pending(transactionRoot)`（`SourceEditTransaction.cs:472`，排除 Committed/RolledBack/**Conflict**）的每条 journal 调 `RecoverAsync`，**可能重新执行第 5 步替换 → 写 TXT**；也可能 rollback（不写）。 |

**不写 TXT 但容易被误认的相邻操作（已核实）：**

| 操作 | 位置 | 实际行为 |
|---|---|---|
| 「导出为 TXT 副本…」 | `SourceBackupWindow.cs:49/161/361` → `Export` `:404` | `File.Copy(paths[0], full, overwrite: true)` `:414`，并**拒绝**目标等于任一项目原文（`:410-411`，`InkDialog` 提示）→ 只写新副本，不碰原文 |
| 「导出所选书籍的全部备份…」 | `:50/162/380` | `File.Copy(entry.Path, target, overwrite: false)` `:396`，目标名带序号去重（`UniquePath` `:460`） |
| 「按保留数清理…」 | `:53/165/420` | 二次确认（`:424-426`）后 `_store.Inventory().Prune(...)` `:427` → Core `SourceBackupInventory.Prune`（`SourceBackupInventory.cs:112`），只动备份 |
| 「将所选备份移入回收站…」 | `:54/166/435` | 二次确认（`:443-445`）后 `FileSystem.DeleteFile(..., SendToRecycleBin)` `:449`，只删备份 |
| 「更改备份位置…」/「恢复默认位置」 | `:81-84` → `SaveLocation` `:346` | 只改 `AppSettingsStore` 里的 `SourceBackupRoot`（`SourceBackupWindow.cs:352`，Core `AppSettingsStore.cs:53`），**明确不移动已有备份** |

---

## 2. 死设置清单（界面能改、代码从不读取）

**结论：本范围内没有发现「控件可改且完全不被读取」的死设置。** 逐项证据如下（每一项都追到了读取点）：

| 设置控件 | 位置 | 读取点（证明其有效） |
|---|---|---|
| `ThemeCombo` / 三个主题 RadioButton | `SettingsWindow.xaml:32` | `SettingsWindow.Theme`（`SettingsWindow.xaml.cs:82`）→ `MainWindow.xaml.cs:566` → `ThemeManager.Apply`（`:589`） |
| `DensityCombo` | `SettingsWindow.xaml:33` | `SettingsWindow.Density`（`.cs:83`）→ `MainWindow.xaml.cs:567` → `:592` / `:4387`（侧栏宽度） |
| `ScaleCombo` | `SettingsWindow.xaml:34` | `.cs:84` → `MainWindow.xaml.cs:568` → `:595`（`FontSize`） |
| `RememberWindowCheck` | `SettingsWindow.xaml:35` | `.cs:85` → `MainWindow.xaml.cs:569` → `:1160-1163`（写 `WindowLeft/Top/Width/Height`）、`MainWindow.xaml.cs:973`（恢复判定） |
| `ReduceMotionCheck` | `SettingsWindow.xaml:36` | `.cs:86` → `MainWindow.xaml.cs:570` → `ApplyMotionSettings()`（`MainWindow.Performance.cs:14-16`，写 `Resources["MotionEnabled"]`） |
| `DefaultOutputText` | `SettingsWindow.xaml:42` | `.cs:87` → `MainWindow.xaml.cs:571` |
| `KindleGenPathText` | `SettingsWindow.xaml:44` | `.cs:88` → `MainWindow.xaml.cs:572`（写 `KindleGenText.Text`） |
| `TextEditorPathText` | `SettingsWindow.xaml:50` | `.cs:89` → `MainWindow.xaml.cs:573` → `:1955` / `:1967` |
| `ParallelismSettingsCombo` | `SettingsWindow.xaml:54` | `.cs:90` → `MainWindow.xaml.cs:574`（**控件在 Collapsed 页内，用户改不了；值仍被读**） |
| `ValidationSettingsCheck` | `SettingsWindow.xaml:54` | `.cs:91` → `MainWindow.xaml.cs:575` |
| `RetentionSettingsCombo` | `SettingsWindow.xaml:54` | `.cs:92` → `MainWindow.xaml.cs:576` |
| `AutoOpenTaskCenterSettingsCheck` | `SettingsWindow.xaml:54` | `.cs:93` → `MainWindow.xaml.cs:578` |
| `AutoOpenOutputSettingsCheck` | `SettingsWindow.xaml:54` | `.cs:94` → `MainWindow.xaml.cs:579` |
| `AutoCheckUpdateCheck` | `SettingsWindow.xaml:58` | `.cs:95` → `MainWindow.xaml.cs:580` → `:640`（启动即查更新）、`:1152` |
| `LayoutModeCombo` | `ConversionSettingsWindow.xaml:162` | `CaptureDraftFromControls`（`.cs:184`）→ `ConversionSettingsPane_Applied`（`MainWindow.xaml.cs:520` `ApplyProfile`） |
| `CompressionCombo` | `ConversionSettingsWindow.xaml:237` | `.cs:172` → `MobiCompression` |
| `StripSourceCheck` | `ConversionSettingsWindow.xaml:247` | `.cs:173` → `MainWindow.xaml.cs:915/1054` |
| `ReadingSyncCheck` | `ConversionSettingsWindow.xaml:249` | `.cs:174` → `MainWindow.xaml.cs:917/1056` |
| `AsinText` | `ConversionSettingsWindow.xaml:250` | `.cs:175` → `MainWindow.xaml.cs:1110` 附近（`Asin`） |
| `KindleGenArgumentsText` | `ConversionSettingsWindow.xaml:258` | `.cs:176` → `ExtraArguments` |
| `ParallelismCombo` | `ConversionSettingsWindow.xaml:274` | `.cs:183` → `Profile.Parallelism` |
| `CollisionPolicyCombo` | `ConversionSettingsWindow.xaml:288` | `.cs:199` → `MainWindow.xaml.cs:522` |
| `OptimizeLongBookCheck` | `ConversionSettingsWindow.xaml:298` | `.cs:178` → `MainWindow.xaml.cs:916/1055` |
| `ArtifactValidationCheck` + `ReportRetentionCombo` | `ConversionSettingsWindow.xaml:321/325` | `.cs:190-191` → `MainWindow.xaml.cs:1060-1062/1114-1117` |
| `AutoOpenOutputCheck` / `AutoOpenTaskCenterCheck` | `ConversionSettingsWindow.xaml:337/338` | `.cs:200-201` → `MainWindow.xaml.cs:523-524` |
| `PreserveEpubRadio` / `ReflowEpubRadio` | `ConversionSettingsWindow.xaml:199/205` | `.cs:177` → `MainWindow.xaml.cs:920/1059/1111`（`EpubModeCombo`） |
| `IntentCombo` / `KeywordText` | `AdvertisementConditionWindow.xaml:11/13` | `Preserve`（`.cs:20`）/ `Keyword`（`.cs:19`）→ `AddKeyword`（Core `BuiltinCleanupOverride.cs:25`）→ 经 `TextCleanupWindow` 落地 |
| 快捷键表格 `ShortcutGrid` | `ShortcutManagerWindow.xaml:8` | `Save_Click`（`.cs:33`）→ `Bindings` → `MainWindow.xaml.cs:218/581/1154` → `ShortcutCatalog.Matches`（`MainWindow.xaml.cs:4419-4464`） |

**唯一的「半死」设置（可用但被隐藏）**：`SettingsWindow` 的「性能与验收」整页（`SettingsWindow.xaml:54`，`TabItem Visibility="Collapsed"`）。其中的 `ParallelismSettingsCombo`、`ValidationSettingsCheck`、`RetentionSettingsCombo`、`AutoOpenTaskCenterSettingsCheck`、`AutoOpenOutputSettingsCheck` 五个设置**用户无法修改**（页面不显示），但值仍会被 `Save_Click` 提交并覆盖主窗口的对应控件 —— 也就是说：**打开设置窗口点「保存」，会把主窗口当前的并发/验收/自动打开设置原样写回**（因为这些控件在构造时就是用主窗口当前值初始化的，`SettingsWindow.xaml.cs:58-63`）。这不产生功能缺陷，但属于「迁移到转换设置后遗留的死 UI」。

---

## 3. 死 UI 清单（控件存在但用户永远看不到 / 永远点不到）

| # | 死 UI 名称 | 位置 | 死因 | 影响 |
|---|---|---|---|---|
| D1 | 「性能与验收」整页 `TabItem`（含 5 个设置 + 「管理快捷键」按钮） | `SettingsWindow.xaml:54` | `Visibility="Collapsed"` 硬编码 | 用户无法从设置页改并发/验收/保留数/自动打开；**也无法从设置页进入快捷键管理**（`ManageShortcuts_Click` `SettingsWindow.xaml.cs:188` 成为死入口）。主窗口另有活入口（`MainWindow.xaml.cs:214`） |
| D2 | `ThemeCombo` | `SettingsWindow.xaml:32` | `Visibility="Collapsed"`，被同级三个 RadioButton 取代 | 无功能损失（Radio 会同步它，`SettingsWindow.xaml.cs:158-162`） |
| D3 | `OutputFormatCombo` | `ConversionSettingsWindow.xaml:132` | `Visibility="Collapsed"`，被 `EpubOutputRadio`/`MobiOutputRadio` 取代 | 无功能损失；但它挂了唯一的 `SelectionChanged="OutputFormatCombo_SelectionChanged"`（`.cs:283`）——该 handler 只对代码内的 `SelectByTag` 生效 |
| D4 | `ScopeHintText` | `TextCleanupWindow.xaml:11` | `Visibility="Collapsed"` 硬编码 | `ConfigureScope`（`.cs:37`）写入的「当前书稿使用独立规则 / 继承通用规则」提示**永远不可见**；文本被复制到 `ScopeExpander.ToolTip`（`.cs:38`）和 `Header`（`.cs:39`）作为补偿 |
| D5 | `PreviewLocationText` | `TextCleanupWindow.xaml:83` | `Visibility="Collapsed"` 硬编码 | `ShowPreview`（`.cs:396`）写入的定位说明（「原文第 N 行已删除，已定位到相邻正文第 M 行」）**永远不可见** |
| D6 | `PreflightWindow.ContinueButton`（「继续转换」） | `PreflightWindow.xaml:39` | 构造时按 `allowContinue` 决定（`PreflightWindow.xaml.cs:29`）；主窗口 4 个调用点中 3 个传 `allowContinue: false`（`MainWindow.xaml.cs:3490` / `:3524` / `:3748`） | 这三处调用中该按钮永远 `Collapsed`；只有 `MainWindow.xaml.cs:3753`（转换前拦截）会显示 |
| D7 | `CoverLightboxWindow` / `BookPreviewWindow` 的「关闭」按钮 | `.xaml:23` / `.xaml:26` | 只有 `IsCancel="True"`，无 `Click` 处理 | 正常关闭，非缺陷；但**没有任何业务入口**（这两个窗口是纯展示） |

> 另有三个「条件性隐形」控件，不属于死 UI：`TextCleanupWindow.AddAdvertisementRuleButton`（`.xaml:77` 默认 `Collapsed`，由 `UpdateRuleActions` `.cs:88` 按选中记录决定是否显示）、`ExpandAdGroupButton`/`BackToGroupsButton`（`.xaml:56`，由 `UpdatePreview` `.cs:338-339` 控制）、`RecentActionPanel`（`.xaml:92`，由 `RememberRule` `.cs:102` 控制）。

---

## 4. 不需要点按钮就自动执行的逻辑

| # | 触发时机 | 位置 | 做了什么 | 副作用 |
|---|---|---|---|---|
| A1 | **任意 Window 的 `Loaded`** | `App.xaml:114-115`（`Style TargetType="Window"` + `EventSetter Event="Loaded" Handler="Window_Loaded"`）→ `App.xaml.cs:12-15` → `ThemeManager.ApplyWindowChrome`（`ThemeManager.cs:81-84`）→ `ApplyToWindow`（`:86`） | 每开一个窗口就给标题栏刷 DWM 深色/浅色属性（`DwmSetWindowAttribute`，`:108-111`），未初始化句柄时挂 `SourceInitialized` 再刷一次（`:100-106`） | 仅UI（P/Invoke 调 `dwmapi.dll`） |
| A2 | `SettingsWindow` 构造 | `SettingsWindow.xaml.cs:67` → `ShowAlreadyDownloadedUpdate` `:71` | `_pendingUpdates.LoadReady()`（Core `PendingUpdateStore.cs:52`）读「上次下载好但没安装」的更新包，直接把 `UpdateCard` 显示成「立即重启更新」 | 仅UI（读磁盘） |
| A3 | `SettingsWindow` 构造 | `SettingsWindow.xaml.cs:46` → `MakeUpdateSectionScrollable` `:106` | 把 `UpdateSectionPanel` 从父 `Panel` 里摘出来塞进 `ScrollViewer`，否则展开 `UpdateCard` 后按钮被裁掉 | 仅UI |
| A4 | `SettingsWindow` `Loaded` | `SettingsWindow.xaml.cs:45` | `ThemeManager.Apply(theme, this)` —— 用传入主题立刻重刷窗口 | 仅UI |
| A5 | `ConversionSettingsWindow` 构造 | `.cs:25` → `:116` → `RefreshKindleGenStatusAsync` `:121` | **每次面板加载都实际启动一次 KindleGen 自检**：`KindleGenHealthProbe.ProbeAsync`（`KindleGenHealthProbe.cs:21`） | 仅UI（**启动外部进程**） |
| A6 | `ConversionSettingsWindow` 任意子控件变化 | `.cs:26-28`（`AddHandler` 监听 `TextBox.TextChangedEvent` / `Selector.SelectionChangedEvent` / `ButtonBase.ClickEvent`）→ `QueueDraftFeedback` `:67` → `Dispatcher.BeginInvoke` → `RefreshDraftFeedback` `:78` | 每次改动都序列化整个草稿（`SnapshotDraft` `:65`，`System.Text.Json`）与打开时快照比对，刷新「有未应用的修改 / 当前设置未修改」 | 仅UI |
| A7 | `TextCleanupWindow` `Loaded` | `.xaml.cs:163-164` → `TextCleanupWindow_Loaded` `:173-177` | 打开即跑一次完整清理分析：`RefreshPreview` `:231` → `TextCleanupPipeline.Apply`（Core `TextCleanupPipeline.cs:42` / `:56`），后台线程（`:263`） | 仅UI（**CPU 密集分析，不写文件**） |
| A8 | `TextCleanupWindow` 规则勾选变化 | `.cs:145-149`（`DispatcherTimer.Tick`）+ `SchedulePreviewRefresh` `:406` | 勾选任一规则后 120ms 防抖，再自动重跑分析；连续快速勾选会合并成一次 | 仅UI |
| A9 | `TextCleanupWindow` 规则变化后若用户已点过「应用规则」 | `.cs:310-315` | `_applyAfterPreview` 为真时，预览一完成就自动 `DialogResult = true`（把「点了应用但还在分析」补成真提交） | 写项目快照（由 `MainWindow.xaml.cs:2111-2118` 落地） |
| A10 | `AdvertisementConditionWindow` `Loaded` | `.cs:31` → `AnalyzeDraft` `:37` | 打开即自动跑基线分析 + 候选分析（`Task.Delay(250)` 防抖 `:56` + `Task.Run` `:58-63`，两次 `TextCleanupPipeline.Apply`） | 仅UI |
| A11 | `AdvertisementConditionWindow` 每次输入 | `.xaml:11`（`IntentCombo SelectionChanged`）+ `.xaml:13`（`KeywordText TextChanged`）→ `DraftChanged` `.cs:35` → `AnalyzeDraft` | 每敲一个字/每换一次意图就重跑全量对比分析（带版本号防竞态 `:39/:64`） | 仅UI |
| A12 | `IllustrationPositionWindow` `Loaded` | `.cs:22-25` → `SelectAndReveal` `:103` | 打开即自动定位到「当前行」或第一行非空行并滚动 | 仅UI |
| A13 | `IllustrationManagerWindow` 插图集合变化 | `.cs:40` → `UpdateCount` `:191` | 增删插图后自动刷新计数 | 仅UI |
| A14 | `CustomCssWindow` `Loaded` | `.cs:15` | 自动把焦点给 CSS 编辑器 | 仅UI |
| A15 | `BookPreviewWindow` 构造 | `.cs:19` | `ItemsList.SelectedIndex = 0` → 触发 `ItemsList_SelectionChanged` `.cs:22` → `PreviewBrowser.Navigate(...)` 立刻渲染第一章 | 仅UI（本地文件导航） |
| A16 | `MetadataMappingWindow` 构造 | `.cs:19` → `RefreshPreview` `:73` | 打开即用 `MetadataMappingResolver.Preview`（Core `MetadataMapping.cs:93`）算一遍「当前书稿命中预览」 | 仅UI |
| A17 | `PreflightWindow` 构造 | `.cs:43-45` | 填类别下拉（`:43`）、选中第一项（`:44`）→ 触发 `Filter_Changed` `:48` → `RefreshRows` `:49`，并对同类别提醒做折叠（`Collapse` `:70`） | 仅UI |
| A18 | `FavoriteFolderManagerWindow` 构造 | `.cs:16-17` | 用外部传入的收藏列表填充 `Folders` | 仅UI |
| A19 | `SourceBackupWindow` 构造 | `.cs:167` → `Reload` `:183` | 打开即读备份目录（`SourceBackupStore.CreateDefault()` `:185`，Core `SourceBackupStore.cs:36`）、枚举每本书的备份（Core `SourceBackupInventory.cs:69`）、并统计未完成事务数（`SourceEditRecovery.Outstanding` `.cs:223`，Core `SourceEditTransaction.cs:486`） | 仅UI（读磁盘） |
| A20 | `ShortcutManagerWindow` 构造 | `.cs:15-16` | 用 `ShortcutCatalog.All` 结合用户覆盖值展开快捷键表（`ShortcutCatalog.Resolve` `ShortcutCatalog.cs:23`） | 仅UI |

> 范围外但相关（主窗口启动链，供对照）：`MainWindow_Loaded`（`MainWindow.xaml.cs:599`）在 `finally` 里无条件执行 `RefreshKindleGenSummaryAsync`、`ScheduleAutomaticAnalysis`，并在 `_autoCheckUpdate` 为真时 `_ = CheckForUpdatesInBackgroundAsync()`（`:640` → `:648`，网络）。`App.xaml.cs:12` 的全局 `Window_Loaded` 同样作用于这两个窗口。

---

## 5. 逐窗口映射表

### 5.1 SettingsWindow（`SettingsWindow.xaml` / `.xaml.cs`）—— 模态对话框，全部值**点「保存并返回工作区」才生效**

主窗口读取点：`MainWindow.xaml.cs:564`（`ShowDialog() == true`）→ `:566-585` → `:583` `_appSettingsStore.SaveAsync(CaptureAppSettings())`。

| # | 界面元素 | 位置（文件:行） | 事件 | 处理的 C# 方法 | 该方法直接调用的方法/类 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| S1 | Button「返回工作区」 | `SettingsWindow.xaml:23` | `Click="Cancel_Click"` | `Cancel_Click`（`.cs:357`） | `DialogResult = false` | 不出 Desktop | 仅UI |
| S2 | `SystemThemeRadio`「跟随系统」 | `.xaml:32` | `Click="ThemeRadio_Click"` | `ThemeRadio_Click`（`.cs:158`） | `SelectByTag(ThemeCombo, tag)`（`.cs:161` → `:359`） | 不出 Desktop | 仅UI（只改内存选中项，保存后才落盘） |
| S3 | `LightThemeRadio`「浅色」 | `.xaml:32` | `Click="ThemeRadio_Click"` | 同 S2 | 同 S2 | 不出 Desktop | 仅UI |
| S4 | `DarkThemeRadio`「深色」 | `.xaml:32` | `Click="ThemeRadio_Click"` | 同 S2 | 同 S2 | 不出 Desktop | 仅UI |
| S5 | `ThemeCombo`（**死 UI**，见 D2） | `.xaml:32` | 无（`Theme` 属性读取，`.cs:82`） | — | `SelectedTag`（`.cs:379`） | 不出 Desktop | 仅UI |
| S6 | `DensityCombo`（舒适/紧凑） | `.xaml:33` | 无（提交时读，`.cs:83`） | — | `SelectedTag`（`.cs:379`）→ `MainWindow.xaml.cs:567` → `:592`/`:4387` | 不出 Desktop | 写设置（保存后）+ 仅UI |
| S7 | `ScaleCombo`（90%~125%） | `.xaml:34` | 无（提交时读，`.cs:84`） | — | `int.Parse(SelectedTag(...))` → `MainWindow.xaml.cs:568` → `:595` | 不出 Desktop | 写设置 + 仅UI |
| S8 | `RememberWindowCheck` | `.xaml:35` | 无（提交时读，`.cs:85`） | — | → `MainWindow.xaml.cs:569` → `:1160-1163` / `:973` | 不出 Desktop | 写设置 |
| S9 | `ReduceMotionCheck` | `.xaml:36` | 无（提交时读，`.cs:86`） | — | → `MainWindow.xaml.cs:570` → `ApplyMotionSettings()`（`MainWindow.Performance.cs:14`） | 不出 Desktop | 写设置 + 仅UI |
| S10 | Button「浏览」（默认输出目录） | `.xaml:42` | `Click="BrowseOutput_Click"` | `BrowseOutput_Click`（`.cs:127`） | `OpenFolderDialog.ShowDialog`（`.cs:129-130`） | 不出 Desktop | 仅UI |
| S11 | Button「查看最近报告」 | `.xaml:42` | `Click="ViewLatestReport_Click"` | `ViewLatestReport_Click`（`.cs:180`） | `Directory.EnumerateFiles(...).OrderByDescending(LastWriteTimeUtc)`（`.cs:183`）、`Process.Start`（`:185`）、无报告时 `InkDialog.Show`（`:184`） | `ArtifactValidationService.ReportDirectoryName`（`ArtifactValidation.cs:39`） | 仅UI + 弹窗 |
| S12 | Button「打开报告目录」 | `.xaml:42` | `Click="OpenReports_Click"` | `OpenReports_Click`（`.cs:173`） | `Directory.CreateDirectory` + `Process.Start("explorer.exe")`（`:176-177`） | `ArtifactValidationService.ReportDirectoryName`（`ArtifactValidation.cs:39`） | 仅UI |
| S13 | Button「管理收藏文件夹」 | `.xaml:42` | `Click="ManageFavorites_Click"` | `ManageFavorites_Click`（`.cs:164`） | `_manageFavorites()`（构造注入，`:47`）→ `MainWindow.xaml.cs:559` → `ManageFavoriteFoldersWindow`（`:436`）→ `FavoriteFolderManagerWindow`；返回后改 `FavoriteCountText` | `FavoriteFolderStore.AddAsync`（`FavoriteFolderStore.cs:42`）/ `RemoveAsync`（`:67`） | 弹窗 + 写设置（**即时落盘，不随本窗口的保存/取消**） |
| S14 | Button「更换」（KindleGen 路径） | `.xaml:44` | `Click="BrowseKindleGen_Click"` | `BrowseKindleGen_Click`（`.cs:133`） | `OpenFileDialog.ShowDialog`（`:135-136`） | 不出 Desktop | 仅UI |
| S15 | Button「执行引擎自检」 | `.xaml:44` | `Click="TestKindleGen_Click"` | `TestKindleGen_Click`（`.cs:147`，`async void`） | `KindleGenHealthProbe.ProbeAsync`（`.cs:151` → `KindleGenHealthProbe.cs:21`） | 不出 Desktop（Desktop 内部实现，实际启动 kindlegen 进程） | 仅UI |
| S16 | Button「选择程序」（TXT 编辑器） | `.xaml:50` | `Click="BrowseTextEditor_Click"` | `BrowseTextEditor_Click`（`.cs:139`） | `OpenFileDialog.ShowDialog`（`:141-142`） | 不出 Desktop | 仅UI |
| S17 | Button「恢复为记事本」 | `.xaml:50` | `Click="ResetTextEditor_Click"` | `ResetTextEditor_Click`（`.cs:145`） | `TextEditorPathText.Text = "notepad.exe"` | 不出 Desktop | 仅UI |
| S18 | `ParallelismSettingsCombo`（**死 UI**，D1） | `.xaml:54` | 无（提交时读，`.cs:90`） | — | → `MainWindow.xaml.cs:574` | 不出 Desktop | 写设置 |
| S19 | `ValidationSettingsCheck`（**死 UI**，D1） | `.xaml:54` | `Click="ValidationSettingsCheck_Click"` | `ValidationSettingsCheck_Click`（`.cs:157`） | `RetentionSettingsCombo.IsEnabled = ...` | 不出 Desktop | 仅UI |
| S20 | `RetentionSettingsCombo`（**死 UI**，D1） | `.xaml:54` | 无（提交时读，`.cs:92`） | — | → `MainWindow.xaml.cs:576` | 不出 Desktop | 写设置 |
| S21 | `AutoOpenTaskCenterSettingsCheck`（**死 UI**，D1） | `.xaml:54` | 无（提交时读，`.cs:93`） | — | → `MainWindow.xaml.cs:578` | 不出 Desktop | 写设置 |
| S22 | `AutoOpenOutputSettingsCheck`（**死 UI**，D1） | `.xaml:54` | 无（提交时读，`.cs:94`） | — | → `MainWindow.xaml.cs:579` | 不出 Desktop | 写设置 |
| S23 | Button「管理快捷键」（**死入口**，D1） | `.xaml:54` | `Click="ManageShortcuts_Click"` | `ManageShortcuts_Click`（`.cs:188`） | `new ShortcutManagerWindow(_shortcutBindings)`（`.cs:190`）→ `ShowDialog` → `_shortcutBindings = window.Bindings`（`:191`） | 不出 Desktop（结果随 S30 保存） | 弹窗 |
| S24 | Button「打开应用数据目录」 | `.xaml:56` | `Click="OpenAppData_Click"` | `OpenAppData_Click`（`.cs:166`） | `Directory.CreateDirectory` + `Process.Start("explorer.exe")`（`:169-170`） | 不出 Desktop | 仅UI |
| S25 | Button「检查更新」`CheckUpdateButton` | `.xaml:58` | `Click="CheckUpdate_Click"` | `CheckUpdate_Click`（`.cs:225`）→ `CheckForUpdateAsync`（`:227`） | `UpdateChecker.CheckAsync`（`:233`）、`UpdateCheckCacheStore.Save`（`:236`）、`ShowAvailableRelease`（`:241` → `:250`） | `UpdateChecker.CheckAsync`（`AppUpdate.cs:132`）、`UpdateCheckCacheStore.Save`（`UpdateCheckCacheStore.cs:58`） | **网络** + 写设置 |
| S26 | Button「下载并更新」/「立即重启更新」`InstallUpdateButton` | `.xaml:58` | `Click="InstallUpdate_Click"` | `InstallUpdate_Click`（`.cs:269`） | 已就绪 → `ApplyDownloadedUpdate`（`:274` → `:330`）→ `UpdateExitHook.Apply`（`:335` → `UpdateExitHook.cs:18`）+ `Application.Current.Shutdown()`（`:342`）；否则 `UpdateInstaller.CleanUp/PackagePath/DownloadAsync/ExtractPackage/StagingLooksComplete`（`:285-296`）+ `_pendingUpdates.Save`（`:300`） | `UpdateInstaller.CleanUp`（`UpdateInstaller.cs:295`）、`PackagePath`（`:312`）、`StagingRoot`（`:41`）、`StagingDirectory`（`:47`）、`DownloadAsync`（`:67`）、`ExtractPackage`（`:136`）、`StagingLooksComplete`（`:169`）、`PendingUpdateStore.Save`（`PendingUpdateStore.cs:58`） | **网络** + 写设置（暂存更新包 / 退出时替换程序） |
| S27 | `AutoCheckUpdateCheck` | `.xaml:58` | 无（提交时读，`.cs:95`） | — | → `MainWindow.xaml.cs:580` → `:640` | 不出 Desktop | 写设置（影响启动时的网络请求） |
| S28 | Button「打开 GitHub 项目主页」 | `.xaml:58` | `Click="OpenGitHub_Click"` | `OpenGitHub_Click`（`.cs:223`） | `Process.Start("https://github.com/uiu8/EasyPub-Modern")` | 不出 Desktop | **网络**（调用系统浏览器） |
| S29 | Button「恢复全部默认」`ResetAllDefaultsButton` | `.xaml:62` | `Click="ResetDefaults_Click"` | `ResetDefaults_Click`（`.cs:194`） | `InkDialog.Show(..., YesNo, Question)`（`:196`）；确认后重置全部控件（`:204-219`）并改 `SaveHintText`（`:220`） | 不出 Desktop | 弹窗 + 仅UI |
| S30 | Button「保存并返回工作区」 | `.xaml:62` | `Click="Save_Click"` | `Save_Click`（`.cs:345`） | 空输出目录 → `InkDialog.Show` + 跳回第 2 页（`:347-353`）；否则 `DialogResult = true`（`:354`）→ `MainWindow.xaml.cs:566-585` → `_appSettingsStore.SaveAsync`（`:583`） | `AppSettingsStore.SaveAsync`（`AppSettingsStore.cs:155`） | **写设置** + 弹窗 |
| S31 | `SettingsTabs` | `.xaml:26` | 外部驱动（无 XAML 事件） | `SelectedSection` 属性（`.cs:121-125`） | `MainWindow.xaml.cs:563` 指定初始页 | 不出 Desktop | 仅UI |
| S32 | 整个窗口 `Loaded` | — | `Loaded +=`（`.cs:45`） | lambda → `ThemeManager.Apply(theme, this)` | `ThemeManager.Apply`（`ThemeManager.cs:25`） | 不出 Desktop | 仅UI |
| S33 | 整个窗口构造 | — | 构造函数（`.cs:44-68`） | `ShowAlreadyDownloadedUpdate`（`:71`）、`MakeUpdateSectionScrollable`（`:106`） | `PendingUpdateStore.LoadReady`（`:73` → `PendingUpdateStore.cs:52`） | `PendingUpdateStore.LoadReady`（`PendingUpdateStore.cs:52`） | 仅UI |

### 5.2 ConversionSettingsWindow（`ConversionSettingsWindow.xaml` / `.cs`）—— **UserControl**，嵌在 `MainWindow.xaml:323` 的 `ConversionSettingsPaneHost` 里，走 `Applied` 事件**立即生效**

> 注意：它是 `UserControl`（`.cs:11`），不是 `Window`；`Window.GetWindow(this)` 取所属窗口做对话框 owner（`.cs:212`、`:377`）。事件接线在主窗口：`MainWindow.xaml.cs:133-135`。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 该方法直接调用的方法/类 | 最终落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| C1 | Button「×」`CloseSettingsButton` | `.xaml:117` | `Click="Cancel_Click"` | `Cancel_Click`（`.cs:368`） | `_draft = _openingDraft`、`ApplyDraftToControls`（`:370-371`）、`CloseRequested?.Invoke()`（`:372`）→ `MainWindow.xaml.cs:478` `CloseConversionSettingsPane` | 不出 Desktop | 仅UI |
| C2 | `EpubOutputRadio` | `.xaml:137` | `Click="OutputFormatRadio_Click"` | `OutputFormatRadio_Click`（`.cs:288`） | `SelectByTag(OutputFormatCombo, tag)`（`:291`）、`UpdateParallelismHint`（`:292` → `:310`） | 不出 Desktop | 仅UI（改草稿） |
| C3 | `MobiOutputRadio` | `.xaml:140` | `Click="OutputFormatRadio_Click"` | 同 C2 | 同 C2 | 不出 Desktop | 仅UI |
| C4 | `OutputFormatCombo`（**死 UI**，D3） | `.xaml:132` | `SelectionChanged="OutputFormatCombo_SelectionChanged"` | `.cs:283` | `UpdateParallelismHint`（`:285`） | 不出 Desktop | 仅UI |
| C5 | Button「浏览」（输出目录） | `.xaml:151` | `Click="BrowseOutput_Click"` | `BrowseOutput_Click`（`.cs:205`） | `OpenFolderDialog`（`:207-212`） | 不出 Desktop | 仅UI |
| C6 | `LayoutModeCombo`（原版兼容/现代排版/自定义） | `.xaml:162` | 无（提交时读，`.cs:184`） | — | `Enum.Parse<ConversionMode>` | `ConversionMode`（`AppSettingsStore.cs:20`） | 写设置（应用后） |
| C7 | `PresetCombo`「常用方案」 | `.xaml:170` | `SelectionChanged="PresetCombo_SelectionChanged"` | `.cs:266` | `CaptureDraftFromControls`（`:269` → `:164`）、`ApplyDraftToControls`（`:279` → `:129`） | `NamedConversionPreset`（`AppSettingsStore.cs:27`） | 仅UI（选中即改草稿，仍需点「应用设置」） |
| C8 | Button「选择文件」（原版 config.xml） | `.xaml:181` | `Click="BrowseLegacyConfig_Click"` | `BrowseLegacyConfig_Click`（`.cs:215`） | `OpenFileDialog`（`:217`）、`LegacyEasyPubConfig.Load`（`:227`）、`ConversionSettingsDraft.ApplyLegacyConfig`（`:228` → `ConversionSettingsDraft.cs:37`） | `LegacyEasyPubConfig.Load`（`LegacyEasyPubConfig.cs:29`）、`LegacyConfigImport`（`LegacyEasyPubConfig.cs:14`） | 仅UI（读外部 XML） |
| C9 | `ClearLegacyConfigButton`「取消选择」 | `.xaml:182` | `Click="ClearLegacyConfig_Click"` | `.cs:238` | `UpdateLegacyConfigState`（`:242` → `:329`） | 不出 Desktop | 仅UI |
| C10 | `ShowLegacyMappingButton`「查看映射」 | `.xaml:183` | `Click="ShowLegacyMapping_Click"` | `.cs:245` | `LegacyEasyPubConfig.Load`（`:251`）、`ShowMessage`（`:258` → `:375` → `InkDialog.Show`） | `LegacyEasyPubConfig.Load`（`LegacyEasyPubConfig.cs:29`） | 弹窗 |
| C11 | `PreserveEpubRadio` | `.xaml:199` | 无（提交时读，`.cs:177`） | — | `EpubInputMode.PreserveOriginal` | 写设置（应用后） |
| C12 | `ReflowEpubRadio` | `.xaml:205` | 无（提交时读，`.cs:177`） | — | `EpubInputMode.EasyPubCompatible` | 写设置（应用后） |
| C13 | Button「前往全局设置」 | `.xaml:230` | `Click="OpenEngineSettings_Click"` | `.cs:346` | `OpenEngineSettingsAfterClose = true`（`:348`）、`OpenEngineSettingsRequested?.Invoke()`（`:349`）→ `MainWindow.xaml.cs:486` → `ShowSettingsAsync(2)`（`:489`） | 不出 Desktop（打开 `SettingsWindow` 第 3 页） | 弹窗 |
| C14 | `CompressionCombo` | `.xaml:237` | 无（提交时读，`.cs:172`） | — | `MobiCompression` 枚举 | 写设置 |
| C15 | `StripSourceCheck` | `.xaml:247` | 无（提交时读，`.cs:173`） | — | `Mobi.StripSourceArchive` | 写设置 |
| C16 | `ReadingSyncCheck` | `.xaml:249` | `Checked`/`Unchecked="ReadingSyncCheck_Changed"` | `.cs:300` | `UpdateDependentControls`（`:300` → `:304`，开关 `AsinText.IsEnabled`） | 不出 Desktop | 仅UI |
| C17 | `AsinText` | `.xaml:250` | 无（提交时读，`.cs:175`） | — | `EmptyToNull`（`:392`） | 写设置 |
| C18 | `KindleGenArgumentsText` | `.xaml:258` | 无（提交时读，`.cs:176`） | — | `EmptyToNull` | 写设置 |
| C19 | `ParallelismCombo` | `.xaml:274` | `SelectionChanged="ParallelismCombo_SelectionChanged"` | `.cs:295` | `UpdateParallelismHint`（`:297` → `:310`） | `ConversionConcurrencyPolicy.Auto`（`ConversionConcurrencyPolicy.cs:5`）/ `Maximum`（`:6`） | 仅UI |
| C20 | `CollisionPolicyCombo` | `.xaml:288` | 无（提交时读，`.cs:199`） | — | `Enum.Parse<OutputCollisionPolicy>` | `OutputCollisionPolicy`（`OutputPathPolicy.cs:3`） | 写设置 |
| C21 | `OptimizeLongBookCheck` | `.xaml:298` | 无（提交时读，`.cs:178`） | — | `Mobi.OptimizeContentPackaging` | 写设置 |
| C22 | `ArtifactValidationCheck` | `.xaml:321` | `Checked`/`Unchecked="ArtifactValidationCheck_Changed"` | `.cs:302` | `UpdateDependentControls`（`:304`，开关 `ReportRetentionCombo.IsEnabled`） | 不出 Desktop | 仅UI |
| C23 | `ReportRetentionCombo` | `.xaml:325` | 无（提交时读，`.cs:191`） | — | `ArtifactValidationOptions.MaxReportCount` | 写设置 |
| C24 | `AutoOpenOutputCheck` | `.xaml:337` | 无（提交时读，`.cs:200`） | — | → `MainWindow.xaml.cs:523` | 写设置 |
| C25 | `AutoOpenTaskCenterCheck` | `.xaml:338` | 无（提交时读，`.cs:201`） | — | → `MainWindow.xaml.cs:524` | 写设置 |
| C26 | Button「恢复默认」 | `.xaml:353` | `Click="RestoreDefaults_Click"` | `.cs:338` | `ConversionSettingsDraft.CreateDefault`（`:341` → `ConversionSettingsDraft.cs:14`）、`ApplyDraftToControls`（`:342`） | `ConversionProfile.Default`（`AppSettingsStore.cs:12`） | 仅UI |
| C27 | Button「取消」`CancelSettingsButton` | `.xaml:356` | `Click="Cancel_Click"` | `.cs:368` | 同 C1 | 不出 Desktop | 仅UI |
| C28 | Button「应用设置」`ApplySettingsButton` | `.xaml:357` | `Click="Apply_Click"` | `Apply_Click`（`.cs:352`） | `CaptureDraftFromControls`（`:356` → `:164`）、`Normalize`（`:356` → `ConversionSettingsDraft.cs:82`）、`Applied?.Invoke`（`:360`）→ `MainWindow.xaml.cs:493` `ConversionSettingsPane_Applied` → `ApplyProfile`（`:520`）、`MarkProjectDirty`（`:525`）、`SaveAsync(CaptureAppSettings())`（`:529`）、`LegacyEasyPubConfig.Load`（`:507`） | `AppSettingsStore.SaveAsync`（`AppSettingsStore.cs:155`）、`LegacyEasyPubConfig.Load`（`LegacyEasyPubConfig.cs:29`）、`ConversionConcurrencyPolicy.Maximum`（`ConversionConcurrencyPolicy.cs:6`） | **写设置** + **写项目快照** + 弹窗（失败时） |
| C29 | `CategoryTabs` | `.xaml:121` | 外部驱动 | `ShowCategory`（`.cs:110`） | `MainWindow.xaml.cs:3559`（页 0）/ `:3582`（页 2） | 不出 Desktop | 仅UI |
| C30 | 面板内**所有** TextBox / Selector / Button（动态） | — | `AddHandler(TextBox.TextChangedEvent / Selector.SelectionChangedEvent / ButtonBase.ClickEvent)`（`.cs:26-28`） | `QueueDraftFeedback`（`.cs:67`）→ `RefreshDraftFeedback`（`:78`） | `SnapshotDraft`（`:65`） | 不出 Desktop | 仅UI |
| C31 | 整个面板 `Loaded` | — | `Loaded +=`（`.cs:25`） | `ConversionSettingsWindow_Loaded`（`:116`） | `RefreshKindleGenStatusAsync`（`:118` → `:121`）→ `KindleGenHealthProbe.ProbeAsync`（`:123` → `KindleGenHealthProbe.cs:21`） | 不出 Desktop | 仅UI（启动外部进程） |
| C32 | 外部驱动：`UpdateScope` | — | 无事件（公开 API） | `UpdateScope`（`.cs:94`） | `MainWindow.xaml.cs:4200` 在选中书稿变化时调用；更新 `ScopeSummaryText`/`EpubScopeText`/`ParallelismHintText` | 不出 Desktop | 仅UI |

### 5.3 ConversionSettingsDraft.cs（纯逻辑，无 UI）

| # | 成员 | 位置 | 调用者 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|
| CD1 | `CreateDefault(outputDirectory, kindleGenPath)` | `ConversionSettingsDraft.cs:14` | `ConversionSettingsWindow` 字段初始化（`.cs:15/16`）、「恢复默认」（`.cs:341`） | `ConversionProfile.Default with {...}`（`:16-27`） | `ConversionProfile.Default`（`AppSettingsStore.cs:12`）、`ConversionConcurrencyPolicy.Auto`（`ConversionConcurrencyPolicy.cs:5`）、`OutputCollisionPolicy.AutoRename`（`OutputPathPolicy.cs:3`） | 不出 Desktop |
| CD2 | `ApplyLegacyConfig(import, machineKindleGenPath)` | `:37` | 「选择文件」（`.cs:228`） | `LegacyConfigImport` 展开（`:40`）、`NormalizeOptional`（`:106`） | `LegacyConfigImport`（`LegacyEasyPubConfig.cs:14`）、`ConversionMode.OriginalCompatible`（`AppSettingsStore.cs:20`） | 不出 Desktop |
| CD3 | `Normalize(machineKindleGenPath)` | `:82` | `LoadDraft`（`.cs:43`）、`Result`（`.cs:61`）、`Apply_Click`（`.cs:356`） | `Path.GetFullPath`（`:95`）、`Math.Clamp(..., ConversionConcurrencyPolicy.Maximum)`（`:99`） | `ConversionConcurrencyPolicy.Maximum`（`ConversionConcurrencyPolicy.cs:6`） | 不出 Desktop |
| CD4 | `ConversionSettingsContext` | `:110` | `MainWindow.xaml.cs:465` 构造 | — | 不出 Desktop | 仅UI |

### 5.4 BatchMetadataWindow（`BatchMetadataWindow.xaml` / `.cs`）—— 值先落内存，**点「保存元数据」才写回书稿对象**

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| B1 | Button「应用到全部」 | `.xaml:14` | `Click="ApplyFieldToAll_Click"` | `.cs:30` | `MetadataGrid.Items.Refresh()`（`:45`）；逐行改 `MetadataEditRow` 属性（`:38-43`） | 不出 Desktop | 仅UI（仅对话框内存） |
| B2 | Button「取消」 | `.xaml:32` | `Click="Cancel_Click"` | `.cs:65` | `DialogResult = false` | 不出 Desktop | 仅UI |
| B3 | Button「保存元数据」 | `.xaml:33` | `Click="Save_Click"` | `.cs:48` | `row.Book.SetMetadataOverrides(...)`（`:54` → `MainWindow.xaml.cs:4728`）、`row.Book.Title/Author`（`:52-53`）、`InkDialog` 无（本窗无校验） | `BookMetadataOverrides`（`MetadataMapping.cs:5`） | 写项目快照（由 `MainWindow.xaml.cs:1778-1786` 压 `_undoStack` / `MarkDirtyTab` 后落盘） |
| B4 | `MetadataGrid` 6 个可编辑列 | `.xaml:21-25` | 双向绑定 `UpdateSourceTrigger=PropertyChanged` | `MetadataEditRow` 属性 setter（`.cs:82-86`） | — | 不出 Desktop | 仅UI（编辑即时进内存，未提交前取消即丢弃） |
| B5 | `BatchFieldCombo`（批量字段选择） | `.xaml:12` | 无（`ApplyFieldToAll_Click` 读取，`.cs:32`） | — | — | 不出 Desktop | 仅UI |
| B6 | `Rows` 初始化（构造） | `.cs:17-27` | 构造函数 | `MetadataEditRow`（`.cs:69`） | `InputBookItem`（`MainWindow.xaml.cs:4728` 附近） | 不出 Desktop | 仅UI |

### 5.5 MetadataMappingWindow（`MetadataMappingWindow.xaml` / `.cs`）—— 规则存内存，**点「保存规则」才落盘**

主窗口落盘点：`MainWindow.xaml.cs:1791-1796` → `MetadataMappingStore.SaveAsync`（Core `MetadataMappingStore.cs:59`）。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| MM1 | Button「添加规则」 | `.xaml:56` | `Click="AddRule_Click"` | `.cs:25` | `new MetadataMappingRuleWindow(null)`（`:27`）、`ReplaceSameFolder`（`:29` → `:63`）、`RefreshPreview`（`:31` → `:73`） | 不出 Desktop | 弹窗 + 仅UI |
| MM2 | Button「编辑」 | `.xaml:57` | `Click="EditRule_Click"` | `.cs:34` | `EditSelectedRule`（`:38`） | 不出 Desktop | 弹窗 + 仅UI |
| MM3 | `RulesGrid` 双击 | `.xaml:30` | `MouseDoubleClick="RulesGrid_MouseDoubleClick"` | `.cs:36` | `EditSelectedRule`（`:38`）→ `Rules.Remove`/`ReplaceSameFolder`（`:48-49`）、`RefreshPreview`（`:51`） | 不出 Desktop | 弹窗 + 仅UI |
| MM4 | Button「删除」 | `.xaml:58` | `Click="DeleteRule_Click"` | `.cs:54` | `Rules.Remove`（`:58`）、`RefreshPreview`（`:59`） | 不出 Desktop | 仅UI |
| MM5 | Button「取消」 | `.xaml:61` | `Click="Cancel_Click"` | `.cs:85` | `DialogResult = false` | 不出 Desktop | 仅UI |
| MM6 | Button「保存规则」 | `.xaml:62` | `Click="Save_Click"` | `.cs:84` | `DialogResult = true` → `MainWindow.xaml.cs:1796` `_metadataMappingStore.SaveAsync(editor.Rules)`、`:1798` `ApplyMetadataMapping(book)` | `MetadataMappingStore.SaveAsync`（`MetadataMappingStore.cs:59`）、`MetadataMappingStore.LoadAsync`（`:34`）、`MetadataMappingResolver.Apply`（`MetadataMapping.cs:75`） | **写设置** + 写项目快照 |
| MM7 | 预览区 `PreviewGrid` / `PreviewSummaryText` | `.xaml:42-43` | 构造 + 每次增删改（`RefreshPreview` `.cs:73`） | `MetadataMappingResolver.Preview`（`.cs:76`） | — | `MetadataMappingResolver.Preview`（`MetadataMapping.cs:93`）、`MetadataMappingPreview`（`MetadataMapping.cs:46`） | 仅UI |

### 5.6 MetadataMappingRuleWindow（`MetadataMappingRuleWindow.xaml` / `.cs`）—— 子对话框，**点「保存此规则」才产出 Rule**

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| MR1 | Button「选择…」（来源文件夹） | `.xaml:25` | `Click="BrowseFolder_Click"` | `.cs:27` | `OpenFolderDialog.ShowDialog`（`:30-37`） | 不出 Desktop | 仅UI |
| MR2 | Button「取消」 | `.xaml:36` | `Click="Cancel_Click"` | `.cs:72` | `DialogResult = false` | 不出 Desktop | 仅UI |
| MR3 | Button「保存此规则」 | `.xaml:37` | `Click="Save_Click"` | `.cs:40` | 目录存在性校验 + `InkDialog.Show`（`:43-47`）、`BookMetadataOverrides.IsEmpty` 校验 + `InkDialog.Show`（`:62-66`）、`MetadataMappingResolver.NormalizeFolder`（`:68`）、`new FolderMetadataRule(...)`（`:68`） | `BookMetadataOverrides`（`MetadataMapping.cs:5`）、`FolderMetadataRule`（`MetadataMapping.cs:33`）、`MetadataMappingResolver.NormalizeFolder`（`MetadataMapping.cs:126`） | 弹窗（校验失败）+ 仅UI |
| MR4 | `FolderPathText` / `AuthorText` / `TranslatorText` / `PublisherText` / `CategoryCombo` / `IsbnText` / `PublicationDatePicker` / `LanguageCombo` / `DescriptionText` | `.xaml:25-32` | 无事件（提交时读取，`.cs:42-61`） | — | — | 不出 Desktop | 仅UI |

### 5.7 PresetManagerWindow（`PresetManagerWindow.xaml` / `.cs`）—— 列表立即改内存，**「应用所选」/「完成」才返回**

主窗口落盘点：`MainWindow.xaml.cs:1174-1186`（`ApplyProfile` + `_appSettingsStore.SaveAsync`）。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| P1 | `PresetList` 选中 | `.xaml:11` | `SelectionChanged="PresetList_SelectionChanged"` | `.cs:24` | `ApplyButton.IsEnabled`、`NameText.Text = preset.Name`（`:26-27`） | 不出 Desktop | 仅UI |
| P2 | Button「另存当前批次」 | `.xaml:14` | `Click="Save_Click"` | `.cs:30` | 重名去重 `_presets.Remove`（`:35`）、`new NamedConversionPreset(name, _currentProfile)`（`:36`）、`_presets.Add`（`:37`）、`Changed = true`（`:39`）、空名 `InkDialog.Show`（`:33`） | `NamedConversionPreset`（`AppSettingsStore.cs:27`） | 弹窗 + 仅UI（内存；`Changed` 由主窗口保存） |
| P3 | Button「应用所选」`ApplyButton` | `.xaml:15` | `Click="Apply_Click"` | `.cs:51` | `AppliedPreset = preset`（`:54`）、`DialogResult = true`（`:55`）→ `MainWindow.xaml.cs:1176-1182` `ApplyProfile` | 不出 Desktop（Profile 由主窗口消费） | 写设置 |
| P4 | Button「删除所选」 | `.xaml:16` | `Click="Delete_Click"` | `.cs:42` | `InkDialog.Show(..., YesNo, Question)`（`:45`）、`_presets.Remove`（`:46`）、`Changed = true`（`:48`） | 不出 Desktop | 弹窗 + 仅UI |
| P5 | Button「完成」 | `.xaml:20` | `Click="Done_Click"` | `.cs:58` | `DialogResult = true` | 不出 Desktop | 仅UI |

### 5.8 CustomCssWindow（`CustomCssWindow.xaml` / `.cs`）—— **点「应用 CSS」才回传**

主窗口落盘点：`MainWindow.xaml.cs:2560-2566`（写入 `_customCss` 并 `MarkDirtyTab(CssTab)`）。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| X1 | Button「导入 .css」 | `.xaml:12` | `Click="Import_Click"` | `.cs:21`（`async void`） | `OpenFileDialog`（`:23`）、`File.ReadAllTextAsync`（`:32`）、`UpdateSourceText`（`:34` → `:74`）、失败 `InkDialog.Show`（`:38`） | 不出 Desktop | 仅UI（读外部文件）+ 弹窗 |
| X2 | Button「导出副本」 | `.xaml:13` | `Click="Export_Click"` | `.cs:42`（`async void`） | `SaveFileDialog`（`:44`）、`File.WriteAllTextAsync`（`:55`）、`UpdateSourceText`（`:57`） | 不出 Desktop | **写产物**（用户指定的 .css 文件） |
| X3 | Button「清空」 | `.xaml:14` | `Click="Clear_Click"` | `.cs:65` | `CssEditor.Clear()`、`SourcePath = null`、`UpdateSourceText`（`:67-69`） | 不出 Desktop | 仅UI |
| X4 | Button「取消」 | `.xaml:30` | `IsCancel="True"`（无 `Click`） | — | WPF 默认关闭 | 不出 Desktop | 仅UI |
| X5 | Button「应用 CSS」 | `.xaml:31` | `Click="Apply_Click"` | `.cs:72` | `DialogResult = true` → `MainWindow.xaml.cs:2562-2564` | 不出 Desktop | 写项目快照 |
| X6 | 窗口 `Loaded` | — | `Loaded +=`（`.cs:15`） | lambda → `CssEditor.Focus()` | — | 不出 Desktop | 仅UI |

### 5.9 TextCleanupWindow（`TextCleanupWindow.xaml` / `.cs`）—— 规则改动**自动重算预览**，**点「应用规则」才回传**

主窗口落盘点：`MainWindow.xaml.cs:2106-2124`（`window.Result` → `SetCleanupOverride` / `_textCleanupOptions` → `MarkProjectDirty` + `SaveRecoveryIfChangedAsync`）。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| T1 | 11 个规则 CheckBox：`BlankLinesCheck`「合并连续空行」 | `.xaml:22` | `Click="Option_Click"` | `Option_Click`（`.cs:417`） | `SchedulePreviewRefresh`（`:406`，120ms 防抖 → `_optionRefreshTimer` `.cs:145` → `RefreshPreview` `:231`） | `TextCleanupPipeline.Apply`（`TextCleanupPipeline.cs:42` / `:56`） | 仅UI |
| T2 | `HardWrapCheck`「修复正文硬换行」 | `.xaml:23` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T3 | `SpacesCheck`「统一全角空格」 | `.xaml:24` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T4 | `ChapterCheck`「标准化章节编号」 | `.xaml:25` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T5 | `NoticeCheck`「清理网站广告/下载说明」 | `.xaml:26` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T6 | `PunctuationCheck`「规范中文标点」 | `.xaml:27` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T7 | `InvisibleCheck`「删除零宽/控制字符」 | `.xaml:28` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T8 | `DuplicateChapterCheck`「删除重复章节标题」 | `.xaml:29` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T9 | `ParagraphBoundaryCheck`「修复段落粘连」 | `.xaml:30` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T10 | `RepeatedHeaderCheck`「删除重复页眉/页码」 | `.xaml:31` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T11 | `OcrCheck`「安全 OCR 空白修复」 | `.xaml:32` | `Click="Option_Click"` | 同 T1 | 同 T1 | 同上 | 仅UI |
| T12 | `ChineseVariantCombo`「简繁转换」 | `.xaml:34` | `SelectionChanged="ChineseVariantCombo_SelectionChanged"` | `.cs:418` | `SchedulePreviewRefresh`（`:406`） | `TextCleanupPipeline.Apply`（`TextCleanupPipeline.cs:42`） | 仅UI |
| T13 | Button「编辑自定义规则与测试…」`ManageCustomRulesButton` | `.xaml:37` | `Click="ManageCustomRules_Click"` | `.cs:509` | `new TextCleanupRuleManagerWindow(_customRules, _sourceText)`（`:511`）、`RefreshPreview`（`:514`） | 不出 Desktop（子窗返回 `Rules`） | 弹窗 + 仅UI |
| T14 | Button「内置规则自定义 / 恢复默认…」 | `.xaml:38` | `Click="ManageBuiltins_Click"` | `.cs:517` | `new BuiltinCleanupWindow(CaptureOptions(), _sourceText)`（`:519`，**范围外窗口** `BuiltinCleanupWindow.cs`）、`ApplyOptions`（`:521` → `:207`）、`RefreshPreview` | `TextCleanupPipeline.Apply`（同上） | 弹窗 + 仅UI |
| T15 | Button「关闭所有规则」 | `.xaml:40` | `Click="Clear_Click"` | `.cs:482` | `CaptureOptions`（`:484`）、`ApplyOptions(new TextCleanupOptions{...})`（`:485`）、`RefreshPreview`（`:486`） | `TextCleanupOptions`（`ConversionRequest.cs:50`）、`TextCleanupPipeline.Apply` | 仅UI |
| T16 | `ChangeRuleFilterCombo`「按规则筛选」 | `.xaml:51` | `SelectionChanged="ChangeFilter_Changed"` | `.cs:419` | `ApplyChangeFilter`（`:446`） | 不出 Desktop | 仅UI |
| T17 | `ChangeSearchText`「搜索修改」 | `.xaml:52` | `TextChanged="ChangeFilter_Changed"` | `.cs:419` | `ApplyChangeFilter`（`:446`，过滤 + 分组折叠） | 不出 Desktop | 仅UI |
| T18 | `GroupAdsCheck`「折叠完全重复项」 | `.xaml:56` | `Click="Grouping_Changed"` | `.cs:19` | `ApplyChangeFilter`（`:19`） | 不出 Desktop | 仅UI |
| T19 | Button「展开逐处核对」`ExpandAdGroupButton`（条件显示） | `.xaml:56` | `Click="ExpandGroup_Click"` | `.cs:20` | `GroupIdentity`（`:17`）、`ApplyChangeFilter`（`:23`） | 不出 Desktop | 仅UI |
| T20 | Button「返回全部记录」`BackToGroupsButton`（条件显示） | `.xaml:56` | `Click="BackToGroups_Click"` | `.cs:25` | `ApplyChangeFilter`（`:25`） | 不出 Desktop | 仅UI |
| T21 | Button「采用所选」`AdoptSelectedButton` | `.xaml:57` | `Click="AdoptSelected_Click"` | `.cs:432` | `SetSelectedChanges(keep: false)`（`:435`）、`RefreshPreview`（`:443`） | `TextCleanupPipeline.Apply` | 仅UI |
| T22 | Button「保留所选原文」`KeepSelectedButton` | `.xaml:57` | `Click="KeepSelected_Click"` | `.cs:433` | `SetSelectedChanges(keep: true)`（`:435`，写 `_excludedKeys`） | `TextCleanupOptions.ExcludedChangeKeys`（`ConversionRequest.cs:50`） | 仅UI |
| T23 | `ChangesGrid` 选中变化 | `.xaml:60` | `SelectionChanged="ChangesGrid_SelectionChanged"` | `.cs:318` | `UpdatePreview`（`:322` → `:330`） | `TextCleanupPreviewNavigator.Create` / `CreateOriginal`（`TextCleanupPreviewNavigator.cs:26` / `:16`） | 仅UI |
| T24 | `ChangesGrid` 行内 Button「采用修改 / 保留本处 / 整组…」 | `.xaml:63` | `Click="ToggleChange_Click"` | `.cs:420` | 改 `_excludedKeys`（`:427-428`）、`RefreshPreview`（`:429`） | `TextCleanupOptions.ExcludedChangeKeys` | 仅UI |
| T25 | Button「← 上一处」`PreviousChangeButton` | `.xaml:73` | `Click="PreviousChange_Click"` | `.cs:353` | `MoveChange(-1)`（`:355`） | 不出 Desktop | 仅UI |
| T26 | Button「下一处 →」`NextChangeButton` | `.xaml:73` | `Click="NextChange_Click"` | `.cs:354` | `MoveChange(1)`（`:355`） | 不出 Desktop | 仅UI |
| T27 | `OriginalPreviewRadio`「原文」 | `.xaml:73` | `Checked="PreviewMode_Changed"` | `.cs:325` | `UpdatePreview`（`:327` → `:330`）→ `CreateOriginal`（`TextCleanupPreviewNavigator.cs:16`） | 不出 Desktop | 仅UI |
| T28 | `ProcessedPreviewRadio`「清理后」 | `.xaml:73` | `Checked="PreviewMode_Changed"` | `.cs:325` | `UpdatePreview` → `TextCleanupPreviewNavigator.Create`（`TextCleanupPreviewNavigator.cs:26`） | 不出 Desktop | 仅UI |
| T29 | Button「添加到广告规则…」`AddAdvertisementRuleButton`（条件显示） | `.xaml:77` | `Click="AddAdvertisementRule_Click"` | `.cs:365` | `OpenConditionEditor(row.Before, false)`（`:368` → `:371`）→ `new AdvertisementConditionWindow(...)`（`:375`）、`ApplyOptions`（`:389`）、`RefreshPreview`（`:390`） | `AdvertisementRuleOptions.AddKeyword`（`BuiltinCleanupOverride.cs:25`） | 弹窗 + 仅UI |
| T30 | Button「撤销」`UndoConditionButton`（条件显示） | `.xaml:92` | `Click="UndoCondition_Click"` | `.cs:54` | `ApplyOptions(CaptureOptions() with {...})`（`:57`）、`RefreshPreview`（`:62`） | 不出 Desktop | 仅UI |
| T31 | Button「继续编辑」`RecentRuleButton` | `.xaml:92` | `Click="RecentRule_Click"` | `.cs:105` | `OpenConditionEditor`（`:105` → `:371`）或 `EditRule`（`:107`）→ `TextCleanupRuleManagerWindow`（`:115`）或 `BuiltinCleanupWindow`（`:127`，范围外） | `BuiltinCleanupOverride`（`BuiltinCleanupOverride.cs:11`） | 弹窗 + 仅UI |
| T32 | Button「恢复打开时设置」 | `.xaml:93` | `Click="Undo_Click"` | `.cs:474` | `ApplyOptions(_initial)`（`:480`）、`RefreshPreview`（`:480`） | 不出 Desktop | 仅UI |
| T33 | Button「取消」 | `.xaml:94` | `Click="Cancel_Click"` | `.cs:488` | `Close()` | 不出 Desktop | 仅UI（丢弃全部改动） |
| T34 | Button「应用规则」`ApplyRulesButton` | `.xaml:94` | `Click="Apply_Click"` | `.cs:489` | 未就绪 → 排队 `_applyAfterPreview = true`（`:494`）；就绪 → `Result = CaptureOptions()`（`:505`）、`DialogResult = true`（`:506`） → `MainWindow.xaml.cs:2109-2124` | `TextCleanupOptions`（`ConversionRequest.cs:50`）、`AppSettingsStore.SaveAsync`（`AppSettingsStore.cs:155`，仅通用规则时） | **写项目快照** + **写设置**（勾了「项目通用规则」时） + 弹窗（分析失败时 `:279`） |
| T35 | `SharedScopeCheck`「应用为项目通用规则…」 | `.xaml:18` | 动态 `Checked`/`Unchecked`（`.cs:156-157`） | lambda → 改 `ScopeExpander.Header` | — | 不出 Desktop | 仅UI（真实副作用在 T34 提交时） |
| T36 | Button「本书恢复继承通用规则」 | `.xaml:18` | `Click="InheritShared_Click"` | `.cs:524` | `InkDialog.Show(..., YesNo, Question)`（`:527`）、`InheritShared = true`、`Result = _sharedOptions`、`DialogResult = true`（`:528`） → `MainWindow.xaml.cs:2110` `SetCleanupOverride(null)`（`MainWindow.xaml.cs:4673`） | 不出 Desktop | 弹窗 + 写项目快照 |
| T37 | `_optionRefreshTimer.Tick`（120ms 防抖） | `.cs:145-149` | `DispatcherTimer.Tick +=`（构造） | `RefreshPreview(allowApplyWhilePreviewing: true)`（`:148`） | `TextCleanupPipeline.Apply`（`TextCleanupPipeline.cs:42` / `:56`） | 同上 | 仅UI |
| T38 | 窗口 `Loaded` | — | `Loaded +=`（`.cs:163`） | `TextCleanupWindow_Loaded`（`:173`） | `RefreshPreview`（`:176`） | `TextCleanupPipeline.Apply` | 仅UI（打开即全量分析） |
| T39 | 窗口 `Closed` | — | `Closed +=`（`.cs:164`） | lambda | `_optionRefreshTimer.Stop()`、`_previewCancellation?.Cancel()`（`:166-167`） | 不出 Desktop | 仅UI |
| T40 | 动态 Button「编辑当前规则：xxx」 | 代码创建（`.cs:92-94`） | `button.Click +=`（`.cs:93`） | lambda → `EditRule(target)`（`.cs:107`） | `TextCleanupRuleManagerWindow`（`:115`）/ `BuiltinCleanupWindow`（`:127`，范围外）、`RefreshPreview`（`:135`） | `BuiltinCleanupOverride`（`BuiltinCleanupOverride.cs:11`） | 弹窗 + 仅UI |
| T41 | `ScopeHintText`（**死 UI**，D4） | `.xaml:11` | 无 | `ConfigureScope` 写入（`.cs:37`） | `MainWindow.xaml.cs:2108` 调用 `ConfigureScope`（`.cs:34`） | 不出 Desktop | 仅UI（不可见） |
| T42 | `PreviewLocationText`（**死 UI**，D5） | `.xaml:83` | 无 | `ShowPreview` 写入（`.cs:396`） | `UpdatePreview`（`:330` → `:348-350`） | `TextCleanupPreviewNavigator`（`TextCleanupPreviewNavigator.cs:16/26`） | 仅UI（不可见） |
| T43 | 静态工厂 `CreateAsync` | `.cs:179` | 主窗口调用（`MainWindow.xaml.cs:2106`） | `TextCleanupPipeline.ReadFileAsync`（`.cs:185`） | — | `TextCleanupPipeline.ReadFileAsync`（`TextCleanupPipeline.cs:202`） | 仅UI（读 TXT，**不写**） |

### 5.10 TextCleanupRuleManagerWindow（`TextCleanupRuleManagerWindow.xaml` / `.cs`）—— 编辑在内存，**「应用规则组」才回传**

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| R1 | Button「＋ 添加」 | `.xaml:17` | `Click="Add_Click"` | `.cs:35` | `TryCommitCurrentRule`（`:37` → `:103`）、`CreateRow`（`:38` → `:189`）、`ReindexRows`（`:40` → `:184`） | `TextCleanupCustomRule`（`ConversionRequest.cs` 内，见 Core 引用） | 仅UI |
| R2 | Button「删除」 | `.xaml:17` | `Click="Delete_Click"` | `.cs:44` | `_rows.Remove`（`:49`）、`ReindexRows`（`:50`） | 不出 Desktop | 仅UI |
| R3 | Button「上移」 | `.xaml:17` | `Click="MoveUp_Click"` | `.cs:54` | `MoveSelected(-1)`（`:57`） | 不出 Desktop | 仅UI |
| R4 | Button「下移」 | `.xaml:17` | `Click="MoveDown_Click"` | `.cs:55` | `MoveSelected(1)`（`:57`，`_rows.Move` + `ReindexRows`） | 不出 Desktop | 仅UI |
| R5 | `RulesGrid` 选中变化 | `.xaml:14` | `SelectionChanged="RulesGrid_SelectionChanged"` | `.cs:68` | `TryCommitRule`（`:72` → `:105`）、把规则装进右侧编辑区（`:86-94`） | 不出 Desktop | 仅UI |
| R6 | `RulesGrid`「启用」复选框列 | `.xaml:15` | 双向绑定 `Enabled, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged` | `TextCleanupRuleRow.Enabled` setter（`.cs:206-215`） | `OnPropertyChanged`（`:213`）→ `RuleRow_PropertyChanged`（动态订阅 `.cs:192` → `:196`）→ 同步 `RuleEnabledCheck`（`:199`） | 不出 Desktop | 仅UI（内存；**不点应用不落盘**） |
| R7 | Button「更新当前规则」 | `.xaml:25` | `Click="SaveRule_Click"` | `.cs:97` | `TryCommitCurrentRule`（`:100` → `:103`）；无选中行时转 `Add_Click`（`:99`）；`CountMatches`（`:127` → `:141`） | `TextCleanupPipeline.Apply`（`TextCleanupPipeline.cs:42`） | 仅UI |
| R8 | Button「导入规则组」 | `.xaml:29` | `Click="Import_Click"` | `.cs:144` | `OpenFileDialog`（`:146`）、`JsonSerializer.Deserialize`（`:150`）、重建行（`:151-157`）、失败 `InkDialog.Show`（`:162`） | `TextCleanupCustomRule` | 仅UI（读外部 JSON）+ 弹窗 |
| R9 | Button「导出规则组」 | `.xaml:29` | `Click="Export_Click"` | `.cs:166` | `TryCommitCurrentRule`（`:168`）、`SaveFileDialog`（`:169`）、`File.WriteAllText`（`:171`） | `Rules` 属性（`.cs:33`） | **写产物**（用户指定的 .json） |
| R10 | Button「取消」 | `.xaml:29` | `Click="Cancel_Click"` | `.cs:180` | `DialogResult = false` | 不出 Desktop | 仅UI |
| R11 | Button「应用规则组」 | `.xaml:29` | `Click="Apply_Click"` | `.cs:175` | `TryCommitCurrentRule`（`:177`）、`DialogResult = true`（`:178`） → 调用方（如 `TextCleanupWindow.xaml.cs:511/115`） | 不出 Desktop | 仅UI（父窗消费 `Rules`） |
| R12 | `RuleNameText` / `RuleScopeCombo` / `RuleTypeCombo` / `RulePatternText` / `RuleReplacementText` / `RuleEnabledCheck` / `IgnoreCaseCheck` / `MultilineCheck` | `.xaml:21-25` | 无事件（`TryCommitRule` 统一读取，`.cs:107-126`） | `TryCommitRule`（`.cs:105`） | `CountMatches`（`:141`）、`Enum.Parse<TextCleanupRuleScope>`（`:122`）、失败 `InkDialog` 无（走 `RuleTestResultText`，`:109-110/135-136`） | `TextCleanupPipeline.Apply`（`TextCleanupPipeline.cs:42`） | 仅UI |
| R13 | `RuleRow_PropertyChanged`（动态订阅） | `.cs:192` | `row.PropertyChanged +=`（`CreateRow` `.cs:189-194`） | `.cs:196` | `RuleEnabledCheck.IsChecked = row.Enabled`（`:199`） | 不出 Desktop | 仅UI |

### 5.11 TextCleanupPreviewNavigator.cs（纯逻辑，**无 UI 入口**）

| # | 成员 | 位置 | 调用者 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|
| N1 | `CreateOriginal(lines, change, maximumCharacters)` | `TextCleanupPreviewNavigator.cs:16` | `TextCleanupWindow.UpdatePreview`（`.cs:349`） | `CreateInitialWindow`（`:21` → `:56`）、`CreateTargetWindow`（`:22` → `:83`） | `TextCleanupChange`（`TextCleanupPipeline.cs:8`） | 不出 Desktop（纯计算） |
| N2 | `Create(preview, selectedChange, maximumCharacters)` | `:26` | `TextCleanupWindow.UpdatePreview`（`.cs:350`） | `IsRemovedLine`（`:38` → `TextCleanupPipeline.cs:40`）、`FindNearestRenderedLine`（`:39` → `:127`） | `TextCleanupPipeline.IsRemovedLine`（`TextCleanupPipeline.cs:40`） | 不出 Desktop |
| N3 | `DefaultMaximumCharacters = 120_000` | `:14` | 截断超长正文预览 | — | 不出 Desktop | 仅UI |

### 5.12 IllustrationManagerWindow（`IllustrationManagerWindow.xaml` / `.cs`）—— 集合在内存，**「保存插图设置」才回传**

主窗口落盘点：`MainWindow.xaml.cs:2656-2657`（`book.SetIllustrations(editor.Result)`）；另一处 `:3577`（从预检问题跳入）。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| I1 | Button「＋ 添加图片」 | `.xaml:12` | `Click="Add_Click"` | `.cs:54` | `OpenFileDialog`（多选，`:56-63`）、标记去重循环（`:71-72`）、`Items.Add`（`:73`）、`ScrollIntoView`（`:76`） | `BookIllustration`（`ConversionRequest.cs:151`） | 仅UI |
| I2 | Button「选择插入位置」 | `.xaml:13` | `Click="ChoosePosition_Click"` | `.cs:85`（`async void`） | `ChapterEditingDocument.LoadAsync`（`:97` → `ChapterEditingDocument.cs:69`）、`new IllustrationPositionWindow(...)`（`:98`）、失败 `InkDialog.Show`（`:113`） | `ChapterEditingDocument.LoadAsync`（`ChapterEditingDocument.cs:69`） | 弹窗 + 仅UI（**只读 TXT**） |
| I3 | Button「移除选中」 | `.xaml:14` | `Click="Remove_Click"` | `.cs:79` | `Items.Remove`（`:82`） | 不出 Desktop | 仅UI |
| I4 | Button「更多」 | `.xaml:15` | `Click="More_Click"` | `.cs:146` | **动态建 `ContextMenu` + 两个 `MenuItem`**（`:149-156`）、`menu.IsOpen = true`（`:157`） | 不出 Desktop | 仅UI |
| I5 | `MenuItem`「改用手动正文标记」（动态） | 代码 `.cs:150-151` | `manual.Click += UseManualMarker_Click`（`.cs:151`） | `UseManualMarker_Click`（`.cs:124`） | `item.InsertAfterLine = null`（`:131`） | 不出 Desktop | 仅UI |
| I6 | `MenuItem`「复制手动标记」（动态） | 代码 `.cs:152-153` | `copy.Click += CopyMarker_Click`（`.cs:153`） | `CopyMarker_Click`（`.cs:135`） | `Clipboard.SetText(item.MarkerToken)`（`:142`） | 不出 Desktop | 仅UI（写剪贴板） |
| I7 | `IllustrationsGrid` 双击 | `.xaml:29` | `MouseDoubleClick="IllustrationsGrid_MouseDoubleClick"` | `.cs:118` | `ChoosePosition_Click`（`:121`） | `ChapterEditingDocument.LoadAsync`（`ChapterEditingDocument.cs:69`） | 弹窗 + 仅UI |
| I8 | Button「保存插图设置」 | `.xaml:44` | `Click="Save_Click"` | `.cs:160` | `CommitEdit`（`:162`）、标记非空/重复/文件存在三项校验 + `InkDialog`（`:166-180`）、`Result = Items.Select(new BookIllustration(...))`（`:183-187`） | `BookIllustration`（`ConversionRequest.cs:151`） | 弹窗 + 写项目快照（主窗口落地） |
| I9 | Button「取消」 | `.xaml:43` | `IsCancel="True"`（无 `Click`） | — | WPF 默认关闭 | 不出 Desktop | 仅UI |
| I10 | `IllustrationsGrid`「标记」「替代文字」列 | `.xaml:31-32` | 双向绑定 `UpdateSourceTrigger=PropertyChanged` | `IllustrationEditorItem.Marker` / `AltText` setter（`.cs:208-229`） | `OnPropertyChanged`（`:216-217`） | 不出 Desktop | 仅UI |
| I11 | 「图片诊断」列 | `.xaml:35` | 绑定读取 | `IllustrationEditorItem.DiagnosticLabel`（`.cs:232-239`） | `ImageDiagnostics.Inspect(ImagePath, cover: false)`（`:236` → `ImageDiagnostics.cs:13`） | 不出 Desktop | 仅UI（读图片文件） |
| I12 | `Items.CollectionChanged` | — | `Items.CollectionChanged +=`（`.cs:40`） | lambda → `UpdateCount`（`.cs:191`） | — | 不出 Desktop | 仅UI |

### 5.13 IllustrationPositionWindow（`IllustrationPositionWindow.xaml` / `.cs`）—— 选择器，**「插入在所选行之后」才回传行号**

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| IP1 | `ChapterCombo`「跳到章节」 | `.xaml:20` | `SelectionChanged="ChapterCombo_SelectionChanged"` | `.cs:30` | `SelectAndReveal`（`:33` → `:103`） | `ChapterCandidate`（`ChapterEditingDocument.cs:12`） | 仅UI |
| IP2 | `SearchText`「查找文字」+ Enter | `.xaml:22` | `KeyDown="SearchText_KeyDown"` | `.cs:38` | `FindNext`（`:41` → `:45`）、`SelectAndReveal`（`:64`）、未命中 `InkDialog.Show`（`:61`） | `TextSourceLine`（`ChapterEditingDocument.cs:20`） | 弹窗 + 仅UI |
| IP3 | Button「查找下一个」 | `.xaml:23` | `Click="FindNext_Click"` | `.cs:36` | `FindNext`（`:45`） | 同上 | 弹窗 + 仅UI |
| IP4 | `LinesGrid` 选中变化 | `.xaml:32` | `SelectionChanged="LinesGrid_SelectionChanged"` | `.cs:67` | 更新 `SelectedPositionText`（`:76`） | `TextSourceLine` | 仅UI |
| IP5 | `LinesGrid` 双击 | `.xaml:32` | `MouseDoubleClick="LinesGrid_MouseDoubleClick"` | `.cs:79` | `ConfirmSelection`（`:81` → `:86`） | 不出 Desktop | 仅UI |
| IP6 | Button「插入在所选行之后」 | `.xaml:59` | `Click="Confirm_Click"` | `.cs:84` | `ConfirmSelection`（`:86`，未选行时 `InkDialog.Show` `:90`） | 不出 Desktop | 弹窗 + 仅UI |
| IP7 | Button「改用手动标记」 | `.xaml:56` | `Click="UseManualMarker_Click"` | `.cs:97` | `SelectedLineNumber = null`、`DialogResult = true`（`:99-100`） | 不出 Desktop | 仅UI |
| IP8 | Button「取消」 | `.xaml:58` | `IsCancel="True"`（无 `Click`） | — | WPF 默认关闭 | 不出 Desktop | 仅UI |
| IP9 | 窗口 `Loaded` | — | `Loaded +=`（`.cs:22`） | lambda → `SelectAndReveal`（`:103`） | — | 不出 Desktop | 仅UI |

### 5.14 CoverLightboxWindow（`CoverLightboxWindow.xaml` / `.cs`）—— 纯展示

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| CL1 | Button「关闭」 | `.xaml:23` | `IsCancel="True"`（**无 `Click`，无业务入口**） | — | WPF 默认关闭 | 不出 Desktop | 仅UI |
| CL2 | `CoverImage` / `BookNameText` / `CoverInfoText` 初始化 | `.cs:11-13` | 构造函数 | — | 调用方 `MainWindow.xaml.cs:2695-2702` | 不出 Desktop | 仅UI |

### 5.15 BookPreviewWindow（`BookPreviewWindow.xaml` / `.cs`）

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| BP1 | `ItemsList` 选中变化 | `.xaml:18` | `SelectionChanged="ItemsList_SelectionChanged"` | `.cs:22` | `PreviewBrowser.Navigate(new Uri(item.HtmlPath))`（`:25`） | `BookPreviewPackage`（`BookPreviewService.cs:7`） | 仅UI（本地 HTML 导航） |
| BP2 | Button「关闭」 | `.xaml:26` | `IsCancel="True"`（无 `Click`） | — | WPF 默认关闭 | 不出 Desktop | 仅UI |
| BP3 | 窗口 `Closed` | — | `Closed +=`（`.cs:18`） | lambda → `_package.Dispose()` | `BookPreviewPackage : IDisposable`（`BookPreviewService.cs:7`） | 不出 Desktop | 仅UI（清理临时 EPUB） |
| BP4 | 构造即选中首项 | `.cs:19` | 构造函数 | `ItemsList.SelectedIndex = 0` → 触发 BP1 | 调用方 `MainWindow.xaml.cs:3453-3459` | 不出 Desktop | 仅UI |

### 5.16 PreflightWindow（`PreflightWindow.xaml` / `.cs`）

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| PF1 | `CategoryCombo` 类别筛选 | `.xaml:21` | `SelectionChanged="Filter_Changed"` | `.cs:48` | `RefreshRows`（`:49` → `:49`）、`Collapse`（`:59`/`:70`）、`BookKey`（`:82`） | `ConversionPreflightReport`（`ConversionPreflightInspector.cs:37`） | 仅UI |
| PF2 | `IncludeInformation`「显示已安排处理 / 信息」 | `.xaml:22` | `Click="Filter_Changed"` | `.cs:48` | `RefreshRows`（`:54` 过滤 Information 行） | 同上 | 仅UI |
| PF3 | `IssuesGrid` 选中变化 | `.xaml:24` | `SelectionChanged="IssuesGrid_SelectionChanged"` | `.cs:85` | 启停 `LocateButton`（`:86-87`） | `PreflightIssueRow`（`.cs:109`） | 仅UI |
| PF4 | `IssuesGrid` 双击 | `.xaml:24` | `MouseDoubleClick="IssuesGrid_MouseDoubleClick"` | `.cs:89` | `LocateSelected`（`:98`）→ `_navigate(issue)` + `Close()`（`:101-102`） | Desktop 回调 `MainWindow.xaml.cs:3530` 附近（`NavigateToPreflightIssue`，范围外） | 仅UI（跳到主窗口对应面板） |
| PF5 | 行内 Button「操作」列 | `.xaml:31` | `Click="HandleIssue_Click"` | `.cs:91` | `_navigate(issue)`（`:94`）、`Close()`（`:95`） | 同 PF4；动作文案由 `PreflightIssueRow.ActionLabel`（`.cs:125-137`）决定，含 `ReadinessEvaluator.ActionLabel`（`BookAnalysisCoordinator.cs:20`） | 仅UI |
| PF6 | Button「处理所选问题」`LocateButton` | `.xaml:37` | `Click="Locate_Click"` | `.cs:90` | `LocateSelected`（`:98`） | 同 PF4 | 仅UI |
| PF7 | Button「关闭」 | `.xaml:38` | `Click="Close_Click"` | `.cs:106` | `DialogResult = false` | 不出 Desktop | 仅UI |
| PF8 | Button「继续转换」`ContinueButton`（**条件死 UI**，D6） | `.xaml:39` | `Click="Continue_Click"` | `.cs:105` | `DialogResult = true` | 不出 Desktop | 仅UI（返回主窗口继续转换） |
| PF9 | 构造时自动筛选 | `.cs:43-45` | 构造函数 | `RefreshRows`（`:45`） | — | 不出 Desktop | 仅UI |

### 5.17 FavoriteFolderManagerWindow（`FavoriteFolderManagerWindow.xaml` / `.cs`）—— **增删即时落盘**，`Result` 只在「完成」后由主窗口使用

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| F1 | `FoldersList` 选中变化 | `.xaml:10` | `SelectionChanged="FoldersList_SelectionChanged"` | `.cs:42` | `RemoveButton.IsEnabled = ...` | 不出 Desktop | 仅UI |
| F2 | Button「＋ 添加收藏」 | `.xaml:15` | `Click="Add_Click"` | `.cs:23`（`async void`） | `OpenFolderDialog`（`:25-30`）、`_store.AddAsync`（`:31`）、`Replace`（`:32` → `:45`） | `FavoriteFolderStore.AddAsync`（`FavoriteFolderStore.cs:42`） | **写设置**（即时落盘，未点「完成」也已生效） |
| F3 | Button「移除所选」`RemoveButton` | `.xaml:15` | `Click="Remove_Click"` | `.cs:35`（`async void`） | `_store.RemoveAsync`（`:38`）、`Replace`（`:39`） | `FavoriteFolderStore.RemoveAsync`（`FavoriteFolderStore.cs:67`） | **写设置**（即时落盘） |
| F4 | Button「完成」 | `.xaml:16` | `Click="Done_Click"` | `.cs:43` | `DialogResult = true` → `MainWindow.xaml.cs:438-441` `ApplyFavoriteFolders(manager.Result)`（`:1883`，仅改内存列表，不再写盘） | 不出 Desktop | 仅UI |

### 5.18 ShortcutManagerWindow（`ShortcutManagerWindow.xaml` / `.cs`）

主窗口落盘点：`MainWindow.xaml.cs:216-220`（主窗口菜单）与 `SettingsWindow.xaml.cs:190-191`（**死入口**，D1）。

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| K1 | `ShortcutGrid`「快捷键」列编辑 | `.xaml:8` | 双向绑定 `Gesture, UpdateSourceTrigger=PropertyChanged` | `ShortcutRow.Gesture` setter（`.cs:46`） | — | 不出 Desktop | 仅UI |
| K2 | Button「恢复默认」 | `.xaml:9` | `Click="Reset_Click"` | `.cs:21` | 遍历 `ShortcutCatalog.All` 取 `DefaultGesture`（`:23`）、`ShortcutGrid.Items.Refresh()`（`:24`） | `ShortcutCatalog.All`（`ShortcutCatalog.cs:9`） | 仅UI |
| K3 | Button「取消」 | `.xaml:9` | `Click="Cancel_Click"` | `.cs:37` | `DialogResult = false` | 不出 Desktop | 仅UI |
| K4 | Button「保存快捷键」 | `.xaml:9` | `Click="Save_Click"` | `.cs:27` | `ShortcutCatalog.TryParse` 校验（`:29`）、重名冲突检查（`:31`）、`Bindings = Rows.ToDictionary(...)`（`:33`）、`InkDialog.Show` 提示无效/冲突（`:30`/`:32`） | `ShortcutCatalog.TryParse`（`ShortcutCatalog.cs:32`） | 弹窗 + 写设置（由调用方 `MainWindow.xaml.cs:219` 落盘） |
| K5 | 构造展开快捷键表 | `.cs:15-16` | 构造函数 | `ShortcutCatalog.Resolve`（`:15`） | — | `ShortcutCatalog.Resolve`（`ShortcutCatalog.cs:23`）、`ShortcutDefinition`（`ShortcutCatalog.cs:5`） | 仅UI |

> **快捷键落地链路（范围内无 KeyBinding，入口实际在主窗口）**：`ShortcutCatalog.Matches`（`ShortcutCatalog.cs:26`）被 `MainWindow.xaml.cs:4419-4464` 的 `OnPreviewKeyDown` 逐条匹配 10 个动作 id（`add-files` / `import-folder` / `select-all` / `convert` / `preflight` / `save-project` / `settings` / `pause` / `focus-search` / `cycle-focus`），全部定义于 `ShortcutCatalog.cs:11-20`。范围内**没有任何** `KeyBinding` 或 `InputBindings`（已 grep 全 `*.xaml`，除 `App.xaml:86-104` 的 `Command="ScrollBar.PageUpCommand"` 等框架滚动命令外无 `Command=`）。

### 5.19 AdvertisementConditionWindow（`AdvertisementConditionWindow.xaml` / `.cs`）

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| AQ1 | `IntentCombo`「作为广告 / 始终保留」 | `.xaml:11` | `SelectionChanged="DraftChanged"` | `DraftChanged`（`.cs:35`） | `AnalyzeDraft`（`:35` → `:37`） | `AdvertisementRuleOptions.AddKeyword`（`BuiltinCleanupOverride.cs:25`） | 仅UI |
| AQ2 | `KeywordText`「匹配文字」 | `.xaml:13` | `TextChanged="DraftChanged"` | `.cs:35` | `AnalyzeDraft`（`:37`，250ms 防抖 + 两次 `TextCleanupPipeline.Apply`） | `TextCleanupOptions`（`ConversionRequest.cs:50`）、`TextCleanupPipeline.Apply`（`TextCleanupPipeline.cs:42`/`:56`） | 仅UI |
| AQ3 | Button「管理已有条件…」 | `.xaml:10` | `Click="ManageConditions_Click"` | `.cs:105` | `new BuiltinCleanupWindow(_working, _source, ..., singleRuleOnly: true)`（`:107`，**范围外窗口**）、`ManagedExisting = true`、`AnalyzeDraft`（`:110-111`） | `BuiltinCleanupOverride`（`BuiltinCleanupOverride.cs:11`） | 弹窗 + 仅UI |
| AQ4 | Button「保存条件」`SaveConditionButton` | `.xaml:24` | `Click="SaveCondition_Click"` | `.cs:98` | `Result = _candidate`、`DialogResult = true`（`:101-102`）；按钮在分析完成前 `IsEnabled = false`（`:45`） | 不出 Desktop（结果由 `TextCleanupWindow.OpenConditionEditor` `.cs:375-390` 消费） | 仅UI（父窗消费后写项目快照） |
| AQ5 | Button「取消」 | `.xaml:24` | `IsCancel="True"`（无 `Click`） | — | WPF 默认关闭 | 不出 Desktop | 仅UI |
| AQ6 | 窗口 `Loaded` | — | `Loaded +=`（`.cs:31`） | lambda → `_ready = true; AnalyzeDraft()` | 同 AQ2 | 同上 | 仅UI |
| AQ7 | 窗口 `Closed` | — | `Closed +=`（`.cs:32`） | lambda → 取消分析（`_analysis?.Cancel()`） | — | 不出 Desktop | 仅UI |

### 5.20 InkDialog（`InkDialog.cs`）—— 纯代码窗口，全局确认/提示入口

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| IN1 | 静态 `Show(owner, message, caption)` | `.cs:9` | 被范围内 **12 个文件**调用（见下） | `Show(..., MessageBoxButton.OK, MessageBoxImage.None)`（`:10`） | `new InkDialogWindow(...)`（`:19`）→ `dialog.ShowDialog()`（`:20`） | 不出 Desktop | **弹窗**（模态） |
| IN2 | 静态 `Show(owner, message, caption, buttons, image)` | `.cs:12` | 同上，带 `YesNo` / `OKCancel` | 同上 | — | 不出 Desktop | **弹窗**（模态） |
| IN3 | 动态 Button（每个按钮） | 代码创建 `.cs:93-100` | `button.Click +=`（`.cs:107`） | lambda → `Result = result; DialogResult = true;`（`:109-110`） | — | 不出 Desktop | 仅UI（回传 `MessageBoxResult`） |
| IN4 | 按钮文案/默认/取消映射 | `Actions`（`.cs:118-124`） | — | — | 顺序：`YesNo` → 否/是；`OKCancel` → 取消/确定；`OK` → 确定 | 不出 Desktop | 仅UI |

**`InkDialog.Show` 的调用点（范围内）**：`SettingsWindow.xaml.cs:184/196/339/349`、`ConversionSettingsWindow.xaml.cs:379`、`MetadataMappingWindow.xaml.cs:42`、`MetadataMappingRuleWindow.xaml.cs:45/64`、`PresetManagerWindow.xaml.cs:33/45`、`CustomCssWindow.xaml.cs:38/61`、`TextCleanupWindow.xaml.cs:279/527`、`TextCleanupRuleManagerWindow.xaml.cs:162/172`、`IllustrationManagerWindow.xaml.cs:89/113/128/139/168/173/178`、`IllustrationPositionWindow.xaml.cs:61/90`、`PreflightWindow` 无、`SourceBackupWindow.cs:281/283/324/358/365/397/411/417/424/443/456/480`、`AdvertisementConditionWindow` 无。

### 5.21 SourceBackupWindow（`SourceBackupWindow.cs`，**纯代码窗口**）

> 打开入口：`MainWindow.Workflow.cs:98-99`（`ManageSourceBackups_Click`）。构造时还会 `ApplyThemeResources()`（`:68` → `:487`，仅在 `Application.Current` 非空时套用主题画刷）。

| # | 界面元素（变量名 / 文本） | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| SB1 | Button「更改备份位置…」`change` | `.cs:81` | `change.Click += ChangeLocation_Click`（`.cs:82`） | `ChangeLocation_Click`（`:331`） | `OpenFolderDialog`（`:333`）、`SaveLocation`（`:335` → `:346`）→ `AppSettingsStore.Load`（`:351`）+ `SaveAsync(... SourceBackupRoot ...)`（`:352`）+ `Reload`（`:354`） | `AppSettingsStore.Load`（`AppSettingsStore.cs:118`）、`SaveAsync`（`:155`）、`EasyPubAppSettings.SourceBackupRoot`（`AppSettingsStore.cs:53`）、`SourceBackupStore.CreateDefault`（`SourceBackupStore.cs:36`） | **写设置** + 仅UI（**不移动已有备份**） |
| SB2 | Button「恢复默认位置」`reset` | `.cs:83` | `reset.Click += ResetLocation_Click`（`.cs:84`） | `ResetLocation_Click`（`:338`） | `SaveLocation(null)`（`:346`）→ 同上（写 `SourceBackupRoot = null`） | 同 SB1 | **写设置** |
| SB3 | Button「关闭」`close` | `.cs:93` | `close.Click +=`（`.cs:94`） | lambda → `Close()` | — | 不出 Desktop | 仅UI |
| SB4 | `DataGrid _books`（书稿列表）选中变化 | `.cs:29-33`（控件）/ `.cs:159`（绑定） | `SelectionChanged +=`（`.cs:159`） | lambda → `ReloadEntries`（`:212`） | `_store.Inventory().ListEntries`（`:215` → Core `SourceBackupInventory.cs:69`）、`SourceEditRecovery.Outstanding`（`:223` → Core `SourceEditTransaction.cs:486`）、按结果启停 5 个按钮（`:217-222`） | `SourceBackupInventory.ListEntries`（`SourceBackupInventory.cs:69`）、`SourceEditRecovery.Outstanding`（`SourceEditTransaction.cs:486`） | 仅UI（读磁盘） |
| SB5 | **Button「恢复此版本…」`_restore`** | `.cs:48`（控件）/ `.cs:160`（绑定） | `_restore.Click += Restore_Click` | `Restore_Click`（`:277`）→ `RestoreSelectedAsync`（`:240`） | 前置校验 + `InkDialog`（`:279-283`）、`new SourceRestoreWindow(...)`（`:285`）、`SourceTransitionState.LoadFromAsync`（`:260`）、`new RestoreTransaction(...).ExecuteAsync(...)`（`:261-262`） | `SourceTransitionState.LoadFromAsync`（`SourceVersionTransition.cs:64`）、`RestoreTransaction`（`RestoreTransaction.cs:89`）、`ExecuteAsync`（`RestoreTransaction.cs:111`）→ `SourceEditTransaction.ExecuteAsync`（`SourceEditTransaction.cs:111`，**第 166 行 `ReplaceAtomically()` 才真正写 TXT**） | **⚠️写TXT** + **写备份**（事务第 2 步 `File.Copy` 先备份，`SourceEditTransaction.cs:140-147`）+ 弹窗 |
| SB6 | Button「导出为 TXT 副本…」`_exportOne` | `.cs:49` / `.cs:161` | `_exportOne.Click += ExportOne_Click` | `ExportOne_Click`（`:361`）→ `Export`（`:404`） | `SaveFileDialog`（`:366`）、`File.Copy(paths[0], full, overwrite: true)`（`:414`）、目标等于原文时 `InkDialog` 拒绝（`:410-411`） | 不出 Core（纯 `System.IO`） | **写产物** + 弹窗 |
| SB7 | Button「导出所选书籍的全部备份…」`_exportAll` | `.cs:50` / `.cs:162` | `_exportAll.Click += ExportAll_Click` | `ExportAll_Click`（`:380`） | `OpenFolderDialog`（`:385`）、逐条 `File.Copy(..., overwrite: false)`（`:396`）、`UniquePath`（`:395` → `:460`）、部分失败 `InkDialog`（`:397`） | 不出 Core | **写产物** + 弹窗 |
| SB8 | **Button「完成未完成的原文修改…」`_finish`** | `.cs:51` / `.cs:163` | `_finish.Click += FinishPending_Click` | `FinishPending_Click`（`:316`）→ `FinishPendingAsync`（`:295`） | `SourceEditRecovery.Outstanding`（`:318`）、`InkDialog.Show(YesNo, Question)`（`:324-327`）、`SourceEditRecovery.RecoverAllAsync`（`:300`） | `SourceEditRecovery.Outstanding`（`SourceEditTransaction.cs:486`）、`RecoverAllAsync`（`SourceEditTransaction.cs:503`）→ 可能重新执行替换（写 TXT） | **⚠️写TXT**（当 journal 处于替换后状态时补完）+ **写备份** + 弹窗 |
| SB9 | Button「打开备份位置」`_openFolder` | `.cs:52` / `.cs:164` | `_openFolder.Click +=`（lambda） | lambda → `OpenInExplorer(_store.DirectoryPath)`（`:473`） | `Directory.CreateDirectory` + `Process.Start`（`:477-478`） | `SourceBackupStore.DirectoryPath`（`SourceBackupStore.cs:9`） | 仅UI |
| SB10 | Button「按保留数清理…」`_prune` | `.cs:53` / `.cs:165` | `_prune.Click += Prune_Click` | `Prune_Click`（`:420`） | `InkDialog.Show(YesNo, Question)`（`:424-426`）、`_store.Inventory().Prune(book.SourcePath, limit)`（`:427`）、`Reload`（`:428`） | `SourceBackupStore.SnapshotRetentionLimit`（`SourceBackupStore.cs:15`）、`SourceBackupInventory.Prune`（`SourceBackupInventory.cs:112`）、`SourceBackupRetention.DefaultSnapshotLimit`（`SourceBackupLayout.cs` 内） | **写备份**（移入回收站）+ 弹窗 |
| SB11 | Button「将所选备份移入回收站…」`_remove` | `.cs:54` / `.cs:166` | `_remove.Click += Remove_Click` | `Remove_Click`（`:435`） | `InkDialog.Show(YesNo, Warning)`（`:443-445`）、`FileSystem.DeleteFile(..., RecycleOption.SendToRecycleBin)`（`:449`）、`Reload`（`:450`）、失败提示（`:456`） | 不出 Core（`Microsoft.VisualBasic.FileIO`） | **写备份**（删除备份）+ 弹窗 |
| SB12 | 构造时自动加载 | `.cs:167` | 构造函数 | `Reload`（`:183`） | `SourceBackupStore.CreateDefault()`（`:185`）、`_store.Inventory().ListEntries`（`:192`）、`File.Exists` 判定原文是否还在（`:203`）、`ReloadEntries`（`:206`） | `SourceBackupStore.CreateDefault`（`SourceBackupStore.cs:36`）、`SourceBackupEntry`（`SourceBackupLayout.cs:95`）、`SourceBackupKind`（`SourceBackupLayout.cs:128`） | 仅UI（读磁盘） |
| SB13 | 主题套用 | `.cs:68` → `:487` | 构造函数 | `ApplyThemeResources`（`:487`） | `SetResourceReference`（`:490-491`），无 `Application` 时跳过（`:489`） | 不出 Desktop | 仅UI |

**恢复链路的每个确认步骤与事务边界（如实记录）**

1. `_restore` 点击 → `Restore_Click`（`SourceBackupWindow.cs:277`）。
2. 必须**恰好选中 1 条**备份：`_entries.SelectedItems.Count != 1` → `InkDialog.Show("请选择一份备份。")` 并返回（`:279-281`）。**注意 `_entries` 的 `SelectionChanged` 没有绑定**（`:34-42` 只有 `SelectionMode = Extended`），所以多选/取消选不会刷新任何提示。
3. 原文必须仍存在：`!File.Exists(book.SourcePath)` → `InkDialog.Show("这本书的原文已经不在了，无法恢复。")`（`:282-283`）。
4. 弹 `SourceRestoreWindow` 模态窗（`:285`），窗内两个选项（`SourceRestoreWindow.cs:17-28`）：
   - `_sourceOnly`「只恢复原文（章节重新识别）」，默认选中（`:21`）；
   - `_fullState`「恢复原文与当时的章节树」，当 `entry.HasSavedTree == false` 时 `IsEnabled = false`（`:86`）并显示「这个版本没有保存章节树，所以只能选上面那一项。」（`:83`）；
   - 按钮「取消」（`IsCancel`，`:61-62`）与「开始恢复」（`IsDefault`，`:63-64`）；
   - 文案明确说明「当前原文会先自动备份一份，所以这次恢复本身还能再恢复回去。恢复过程中如果程序中断，下次打开会自动收尾。」（`:89-90`）。
5. 只有 `ShowDialog() == true` 才触发 `_ = RestoreSelectedAsync(dialog.Policy)`（`SourceBackupWindow.cs:286-287`；`Policy` 由 `SourceRestoreWindow.cs:99-101` 依 `_fullState.IsChecked` 给出）。
6. `RestoreSelectedAsync`（`:240`）：
   - 再次校验选中数 / 原文存在（`:243-249`）；
   - `FullBookState` 时读 `_store.ReadSnapshotTree(...)`（`:254` → Core `SourceBackupStore.cs:168`），**为 null 就交给事务去拒绝**（注释 `:251-252`）；
   - `IsEnabled = false` 冻结整个窗口（`:259`，`finally` 恢复 `:274`）；
   - `SourceTransitionState.LoadFromAsync`（`:260`）——从磁盘现读当前状态；
   - `RestoreTransaction.ExecuteAsync(current, entry.Path, policy, savedTree, token)`（`:261-262`）。
7. Core 事务边界（`RestoreTransaction.cs:111`）：
   - 拒绝点（**全部在读/写之前**）：备份文件不存在（`:124-125`）、原文不存在（`:126-127`）、`FullBookState` 但无保存的树（`:138-139`）、树与备份 sha 不匹配（`:140-141`）、原文已是该版本（`:147-149`）、原文被外部改过（`:150-151`）、`SourceEditPreflightInspector` 判定不可写（`:153-156`）、坐标映射无法对齐（`:158-162`）；
   - 迁移到 `SourceEditTransaction`（`:177-189`），`afterReplace` 里跑 `SourceVersionTransition.AfterReplaceAsync`（`:180-183`）；
   - `transaction.ExecuteAsync(manifest, targetDocument, targetText, backupDestination, token)`（`:192`）。
8. `SourceEditTransaction.ExecuteAsync`（`SourceEditTransaction.cs:111`）的 6 步：
   - ① 校验磁盘 sha 与清单基线一致（`:115-120`），`newSha` 相同则拒绝（`:124-126`）；
   - ② 建事务目录 + **写 journal `Prepared`**（`:128-133`）；
   - ③ **先备份**：`File.Copy(_sourcePath, backupPath, overwrite: true)`（`:143`），journal → `BackupCreated`（`:144-145`）；
   - ④ 渲染到同级临时文件（`:152-153`），journal → `TempWritten`（`:154-155`）；
   - ⑤ 清单落盘并核对（`:159-162`）；
   - ⑥ **`ReplaceAtomically()` 原子替换（`:166`）→ 这一步才改写用户 TXT**，journal → `SourceReplaced`（`:167-168`）；之后 `_afterReplace` 迁移章节树（`:173-174`）并继续 journal 状态推进；
   - 收尾 `DeleteLeftovers` 只删同级临时文件与工作目录，**保留 journal**（`:454-469`）。
9. 结果回写：`Reload()` + `_statusText.Text = result.Message + (result.Succeeded ? "" : "（原文没有改动）")`（`SourceBackupWindow.cs:263-265`；`RestoreResult.Succeeded` 定义于 `RestoreTransaction.cs:57`，`Message` 于 `:44`）。异常路径同样 `Reload()` 并写「恢复未完成：」+ 消息（`:268-273`）。

### 5.22 SourceRestoreWindow（`SourceRestoreWindow.cs`，**纯代码窗口**）

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| SR1 | RadioButton「只恢复原文（章节重新识别）」`_sourceOnly` | `.cs:17-22`（`IsChecked = true` 默认） | 无事件（`Policy` 读取，`:99-101`） | — | — | `RestoreTargetPolicy.SourceOnly`（`RestoreTransaction.cs:20`） | 仅UI |
| SR2 | RadioButton「恢复原文与当时的章节树」`_fullState` | `.cs:24-28` | 无事件（`:99-101`）；`hasSavedTree == false` 时 `IsEnabled = false`（`:86`） | — | — | `RestoreTargetPolicy.FullBookState`（`RestoreTransaction.cs:28`） | 仅UI |
| SR3 | Button「取消」`cancel` | `.cs:61` | `cancel.Click +=`（`.cs:62`） | lambda → `DialogResult = false` | — | 不出 Desktop | 仅UI |
| SR4 | Button「开始恢复」`confirm` | `.cs:63` | `confirm.Click +=`（`.cs:64`） | lambda → `DialogResult = true` → 触发 SB5 的 `RestoreSelectedAsync` | — | 见 SB5 | **⚠️写TXT**（确认后才走） |
| SR5 | 主题套用 | `.cs:37-41` | 构造函数 | `SetResourceReference`（有 `Application` 时） | — | 不出 Desktop | 仅UI |
| SR6 | 测试用免鼠标入口 `Choose(policy)` | `.cs:104-105` | `internal`，无 UI | `_fullState.IsChecked = ...` | — | 不出 Desktop | 仅UI |

### 5.23 App.xaml.cs / App.xaml（应用级）

| # | 界面元素 | 位置 | 事件 | 处理的 C# 方法 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|---|
| AP1 | 全局 `Style TargetType="Window"` 的 `EventSetter Loaded` | `App.xaml:114-115` | 每个 Window 的 `Loaded` | `Window_Loaded`（`App.xaml.cs:12-15`） | `ThemeManager.ApplyWindowChrome(window)`（`:14` → `ThemeManager.cs:81`）→ `ApplyToWindow`（`ThemeManager.cs:86`） | 不出 Desktop | 仅UI（DWM P/Invoke） |
| AP2 | `Style TargetType="Button"` 里的 `local:MotionEffects.HoverEnabled="{DynamicResource MotionEnabled}"` | `App.xaml:140` | 模板加载（附加属性变更） | `MotionEffects.OnHoverEnabledChanged`（`MotionEffects.cs:19`） | 挂 `MouseEnter/Leave/PreviewMouseDown/IsEnabledChanged/Unloaded`（`:29-33`） | 不出 Desktop | 仅UI |
| AP3 | `sys:Boolean x:Key="MotionEnabled"` | `App.xaml:8` | 资源 | 被 `MainWindow.Performance.cs:14-15` 覆写（`!_reduceMotion`） | — | 不出 Desktop | 仅UI |
| AP4 | `StartupUri="MainWindow.xaml"` | `App.xaml:6` | 启动 | WPF 启动主窗口 | — | 不出 Desktop | 仅UI |
| AP5 | 全局 `Style TargetType="MenuItem"` / `ContextMenu` / `ComboBox` / `DatePicker` / `ScrollBar` / `DataGrid` 等 | `App.xaml:279/352/180/239/75/383` | 样式应用 | — | — | 不出 Desktop | 仅UI（范围内窗口共用） |

### 5.24 ThemeManager.cs（支撑，无 UI 入口）

| # | 成员 | 位置 | 调用者 | 直接调用 | 落到 Core | 副作用 |
|---|---|---|---|---|---|---|
| TH1 | `Apply(requestedTheme, window)` | `ThemeManager.cs:25` | `SettingsWindow.xaml.cs:45`、`MainWindow.xaml.cs:589` | 写 17 个画刷资源（`:31-74`）、遍历 `Application.Current.Windows` 刷 chrome（`:76-78`）、`ResolveDark`（`:29` → `:136`，读注册表 `AppsUseLightTheme` `:142-143`） | 不出 Desktop | 仅UI + 读注册表 |
| TH2 | `ApplyWindowChrome(window)` | `:81` | `App.xaml.cs:14` | `ApplyToWindow`（`:86`） | 不出 Desktop | 仅UI |
| TH3 | `ApplyToWindow`（`private`） | `:86` | TH1 / TH2 / `Window_SourceInitialized`（`:114`） | `DwmSetWindowAttribute`（`:123-128`，4 个属性）、未就绪则挂 `SourceInitialized`（`:100-106`） | 不出 Desktop | 仅UI |
| TH4 | 常量 `SystemTheme` / `LightTheme` / `DarkTheme` | `:11-13` | `SettingsWindow.xaml.cs:82`、`:204-205`、`:372-374`；`MainWindow.xaml.cs:959` | — | 不出 Desktop | 仅UI |

### 5.25 ShortcutCatalog.cs（支撑，无 UI 入口）

| # | 成员 | 位置 | 调用者 | 落到 Core | 副作用 |
|---|---|---|---|---|---|
| SC1 | `All`（10 条定义） | `ShortcutCatalog.cs:9-21` | `ShortcutManagerWindow.xaml.cs:15/23`、`MainWindow.xaml.cs:4419-4464` | 不出 Desktop | 仅UI |
| SC2 | `Resolve(overrides, id)` | `:23` | `ShortcutManagerWindow.xaml.cs:15` | 不出 Desktop | 仅UI |
| SC3 | `Matches(overrides, id, e)` | `:26` | `MainWindow.xaml.cs:4419-4464`（主窗口 `OnPreviewKeyDown`） | 不出 Desktop | 仅UI |
| SC4 | `TryParse(gesture, key, modifiers)` | `:32` | `ShortcutManagerWindow.xaml.cs:29`、`Resolve`（`:24`） | 不出 Desktop | 仅UI |

### 5.26 MotionEffects.cs（支撑，无 UI 入口）

| # | 成员 | 位置 | 调用者 | 落到 Core | 副作用 |
|---|---|---|---|---|---|
| ME1 | 附加属性 `HoverEnabled` | `:10-14` | `App.xaml:140` | 不出 Desktop | 仅UI |
| ME2 | `OnHoverEnabledChanged` | `:19` | ME1 变更回调 | 不出 Desktop | 仅UI |
| ME3 | `CanAnimate(enabled)` | `:16` | `Hover`（`:45`）、`ShowPage`（`:55`） | 不出 Desktop | 仅UI（读 `SystemParameters.ClientAreaAnimation` / `HighContrast`） |
| ME4 | `ShowPage(page, enabled)` | `:52` | `MainWindow.Performance.cs:35` | 不出 Desktop | 仅UI |

---

## 6. 生效时机总表（设置类窗口）

| 窗口 / 设置 | 生效时机 | 证据 |
|---|---|---|
| `SettingsWindow` 全部设置（主题、密度、缩放、窗口位置、动画、输出目录、KindleGen、TXT 编辑器、并发、验收、保留数、自动打开、自动查更新、快捷键） | **点「保存并返回工作区」才生效**；点「返回工作区」全部丢弃 | 所有 `Xxx =>` 属性只在 `DialogResult == true` 后被读：`MainWindow.xaml.cs:564-585`；`Cancel_Click` 只设 `DialogResult = false`（`SettingsWindow.xaml.cs:357`） |
| `SettingsWindow` 内的「管理收藏文件夹」 | **立即生效**（不受本窗口保存/取消影响） | `SettingsWindow.xaml.cs:164` 直接调 `_manageFavorites()` → `MainWindow.xaml.cs:436-441` → `FavoriteFolderManagerWindow` 内的 `AddAsync`/`RemoveAsync`（`FavoriteFolderManagerWindow.xaml.cs:31/38`） |
| `SettingsWindow` 的「恢复全部默认」 | 只重置控件，**仍需点保存才生效** | `SettingsWindow.xaml.cs:194-221`（末尾改 `SaveHintText` 明确写着「点击"保存并返回工作区"后生效」`：220`） |
| `ConversionSettingsWindow`（UserControl） | **点「应用设置」立即生效**（写 app settings + 标脏项目）；点「取消」/「×」回滚到打开时快照 | `Apply_Click`（`.cs:352-366`）→ `Applied`（`MainWindow.xaml.cs:493-537`）；`Cancel_Click`（`.cs:368-373`）恢复 `_openingDraft` 并发 `CloseRequested` |
| `ConversionSettingsWindow` 的「常用方案」下拉 | **选中即改草稿**，仍需点「应用设置」 | `.cs:266-281` |
| `BatchMetadataWindow` | **点「保存元数据」生效**（写入 `InputBookItem`，随后由主窗口压撤销栈/标脏） | `.cs:48-63` → `MainWindow.xaml.cs:1778-1786` |
| `MetadataMappingWindow` | **点「保存规则」生效并立即落盘**（`MetadataMappingStore.SaveAsync`） | `.cs:84` → `MainWindow.xaml.cs:1796` |
| `MetadataMappingRuleWindow` | **点「保存此规则」产出 `Rule`** | `.cs:69` |
| `PresetManagerWindow` | 增删改**立即改内存 `ObservableCollection`**；「应用所选」才切当前 Profile | `.cs:30-49` / `:51-56`；`MainWindow.xaml.cs:1176-1186` |
| `CustomCssWindow` | **点「应用 CSS」生效**；「取消」丢弃 | `.cs:72` → `MainWindow.xaml.cs:2560-2564` |
| `TextCleanupWindow` | 勾选规则**自动重算预览**；**点「应用规则」才提交**（未就绪会自动排队 `_applyAfterPreview`） | `.cs:489-507` / `:310-315` → `MainWindow.xaml.cs:2109-2124` |
| `TextCleanupWindow` 的「应用为项目通用规则」 | 勾选后提交时**额外写 app settings** | `MainWindow.xaml.cs:2111-2122` |
| `TextCleanupRuleManagerWindow` | **点「应用规则组」生效**；勾选「启用」列只改内存 | `.cs:175-179` / `:206-215` |
| `IllustrationManagerWindow` | **点「保存插图设置」生效** | `.cs:160-189` → `MainWindow.xaml.cs:2657` |
| `IllustrationPositionWindow` | **点「插入在所选行之后」或双击行生效** | `.cs:84-95` / `:79-82` |
| `FavoriteFolderManagerWindow` | **增删立即落盘**；「完成」只是回传列表给主窗口刷新菜单 | `.cs:23-40` / `:43` |
| `ShortcutManagerWindow` | **点「保存快捷键」生效**（由调用方落盘） | `.cs:27-35`；`MainWindow.xaml.cs:218-219` |
| `AdvertisementConditionWindow` | 输入**即时自动分析**；**点「保存条件」才回传**（分析完成前按钮禁用） | `.cs:37-89` / `:98-103` |
| `SourceBackupWindow` 的「更改/恢复默认备份位置」 | **立即写 app settings** | `.cs:346-358` |
| `SourceBackupWindow` 的恢复 / 收尾 | **二次确认后立即写 TXT** | 见第 1 节 |
| `PreflightWindow` | 「继续转换」返回 `DialogResult = true` | `.cs:105` |

---

## 7. 复核说明

- 范围内 **没有任何拖放事件处理**：对范围内全部 44 个文件 grep `DragOver|DragEnter|AllowDrop|DragDrop` 均无匹配。全项目仅 `MainWindow.xaml:6`（`AllowDrop="True"`）、`:9`（`Drop="Window_Drop" DragOver="Window_DragOver"`）、`:166`/`:257`（`CoverDrop_DragOver` / `CoverDrop_Drop`）、以及 `ChapterEditorWindow.xaml:182`（`AllowDrop="True"`）有拖放 —— 这两处都在本次范围之外。
- 范围内 **没有任何 `Command="..."` 业务绑定**：仅 `App.xaml:86/88/102/104` 使用框架滚动命令（`ScrollBar.PageUpCommand` 等）。
- 范围内 **没有任何 `KeyBinding` / `InputBindings`**：快捷键全部通过 `ShortcutCatalog.Matches` 在主窗口 `OnPreviewKeyDown` 手工匹配（`MainWindow.xaml.cs:4419-4464`）。
- `ConversionSettingsWindow` 的三处 XAML 事件（`OutputFormatCombo_SelectionChanged`、`PresetCombo_SelectionChanged`、`ParallelismCombo_SelectionChanged`/`ReadingSyncCheck_Changed`/`ArtifactValidationCheck_Changed`）都以 `_syncing` 标志防回环（`.cs:18`、`:131`、`:160`、`:268`、`:285`、`:290`、`:297`）。
- 所有「最终落到 Core」列已追到 `EasyPub.Core` 的公开类型/方法；整条链都在 Desktop 层时统一写「不出 Desktop」。

---

## 8. 行号校验记录（机器复核）

写完后用脚本把文档中引用的行号逐条读回、与该行实际包含的符号比对：

| 校验批次 | 数量 | 失败 |
|---|---|---|
| Desktop 层关键行（`SettingsWindow.xaml:54/32`、`SourceBackupWindow.cs:160/163/261`、`SourceRestoreWindow.cs:64`、`IllustrationManagerWindow.xaml.cs:151/153`、`InkDialog.cs:107`、`PreflightWindow.xaml.cs:29`、`TextCleanupWindow.xaml:11/83`、`ConversionSettingsWindow.xaml.cs:26/352` 等） | 23 | 0 |
| `MainWindow.xaml.cs` 侧（`:3753` / `:4728` / `:4419`） | 3 | 0 |
| `EasyPub.Core` 层（`RestoreTransaction.cs:111/57/20/28`、`SourceEditTransaction.cs:111/166/472/486/503`、`SourceBackupStore.cs:9/15/36/168`、`SourceBackupInventory.cs:69/112`、`TextCleanupPipeline.cs:8/21/40/42/56/202`、`AppSettingsStore.cs:12/20/27/53/155`、`UpdateInstaller.cs:41/47/67/136/169/295/312` 等） | 64 | 0 |
| **合计** | **90** | **0** |

另外两项结构性统计也做了机器核对：
- 第 5 节映射表的条目行（行首为 `S1`/`C1`/`T1`/`SB1` … 形态）共 **248** 行；第 1 节 **2** 行、第 3 节 **7** 行、第 4 节 **20** 行。
- XAML 事件绑定按文件逐个正则统计（排除 `IsChecked=`、`MouseDoubleClick=` 对 `Checked=`/`Click=` 的前缀干扰）：`Click` 109、`SelectionChanged` 15、`Checked` 4、`Unchecked` 2、`TextChanged` 2、`MouseDoubleClick` 4、`KeyDown` 1、`Command` 4、`Drop`/`DragOver` 0。
