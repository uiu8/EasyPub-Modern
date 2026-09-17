# EasyPub Modern 全貌：功能、架构与代码地图

> 对象：`main @ e5ce9f0`，v1.60.1。工作目录 `D:\software\DeepSeek-Harness\workspace\easypub-branch`。
> 规模：Core 98 文件 / 18,376 行；Desktop 79 文件 / 16,405 行；测试 105 文件 / 17,656 行。
> 方法：结论来自实际读到的代码，关键处标 `文件:行`。**未确认的地方会写明"未确认"**。
>
> 这份文档回答四个问题：**这个软件做什么**、**界面长什么样**、**代码怎么分层**、**为什么它看起来乱**。
> 第 11 章是重点 —— "乱"不是错觉，它有五个具体来源，其中三个是刻意的。

---

## 0. 一句话说清这个软件

把一本 **TXT（或 EPUB）书稿**读进来，**重新决定它的章节结构**，再把结构与被清洗过的正文一起渲染成 **EPUB / MOBI**。

它不是一个"格式转换器"。格式转换只是最后一公里；前面 90% 的代码在处理这件事：

> **源文件往往是有缺陷的** —— 缺章、重复章、章号跳号、标题写法不统一、正文被站点广告污染、整段被重复拼接。而用户手上有两个互相竞争的真相来源：**这本书的原始文字**，和**官方发布的章节目录**。

软件的立场是：**以官方目录为准重建章节结构，但绝不因此丢正文，绝不覆盖用户的手工编辑。**

### 三条不可退让的约束

| 约束 | 含义 | 代码落点 |
|---|---|---|
| **正文守恒** | 每个源行要么进某一章，要么进移除清单。修完之后能被章节树解释的原文行必须一模一样 | `RepairIntegrity.Verify` / `Coverage`，每次重建后立刻调用 |
| **用户主权** | 用户删过、改过、手动添加的条目，重新修复不能翻案 | `RecognitionSource = "manual"`，`ReferencePlan.cs` 三处显式跳过 |
| **可核对** | 每一次删除都有逐行清单 + 源文件 SHA-256 | `RepairIntegrity.SaveRemovedAsync`、备份清单、执行清单 |

**v1.60 之后第一条有了一个有意的例外**：用户可以要求"同时改原文"（`RepairLandingMode.EditSource`），这时 TXT 真的会被重写。约束因此升级为更严的版本 —— 改之前先备份、通过一个可中断的事务、中途崩溃能自动收尾（§7.3、§8）。

---

## 1. 功能全图（用户实际能做的事）

按用户走一遍的先后顺序。**"改什么"一列是关键**：这个软件有四种不同量级的"修改"，混淆它们是理解它的最大障碍。

| 阶段 | 用户能做什么 | 改什么 | 主要入口 |
|---|---|---|---|
| **导入** | 拖入 TXT / EPUB；自动分析（标题、作者、章节数、编码） | 只读 | 主窗口「书稿」栏 |
| | 从旧版 EasyPub 导入 `config.xml` 设置 | 设置 | 转换设置 → 导入旧配置 |
| **识别** | 用规则识别章节树（普通章节 / 数字标题 / 卷-章-节分层） | 内存树 | 章节工作台「识别设置」 |
| | 手动补标题、改层级、拖动排序、合并/拆分、删除 | 内存树 | 章节树右键与工具条 |
| **检查** | 运行检查建议：缺章、跳号、重复标题、重复正文、跨标题重复正文、结构可疑 | 只读报告 | 工作台右侧问题卡 |
| **修复** | **一键目录修复**：拿官方目录对齐，生成动作清单，逐条勾选后应用 | 内存树，或内存树 + 原文 | 工作台「一键目录修复」 |
| | **无目录自修复**：只用书自身能看出的问题（重复标题、数字标题写法） | 同上 | 同上（无目录时自动走这条） |
| | 手工核对目录：粘贴/抓取目录，逐章比对 | 内存树 | 工作台「手动核对目录」 |
| | 结构整理：按编号段重建层级、批量补漏识别标题 | 内存树 | 工作台「更多 → 视图」 |
| **清洗** | 文本清理规则：去广告、合空行、修硬换行、繁简、标点、OCR 纠正、自定义规则 | 成品文本（映射回原文行） | 主窗口「文本清理」 |
| **转换** | 输出 EPUB / MOBI；封面、插图、字体、元数据、排版参数 | 成品 | 主窗口「开始转换」 |
| **验收** | 转换前检查（阻断错误）、成品结构验收（EPUB/MOBI 内部结构） | 只读报告 | 检查窗、任务中心 |
| **事后** | 转换历史、任务中心、原文备份管理、恢复原文到某个版本、崩溃后自动收尾 | 文件 / 记录 | 侧边栏 |

---

## 2. 两层架构与它们的边界

```
EasyPub.Desktop  (WPF, 16.4k 行)   ← 窗口、面板、按钮、对话框、进度
        │  只做三件事：收集输入、显示结果、把用户的决定交下去
        ▼
EasyPub.Core     (纯逻辑, 18.4k 行) ← 没有一行 UI 代码，不引用 WPF
```

**边界规则**：`EasyPub.Core` 的 `.csproj` 不引用任何 WPF 程序集（未确认是否有例外，但读到的 Core 文件里没有 `using System.Windows`）。Desktop 通过 `ConversionRequest` / `ChapterTreeDocument` / `RepairProposal` 这些**数据记录**与 Core 交互。

**现实中的边界并不干净**，有三处例外：

1. `ChapterContentComparison.cs`（Desktop）自己拼装对话框、自己算"该留哪一份"之外的展示逻辑 —— 这是允许的，因为它只做展示。
2. 备份路径、目录存储路径、设置路径的**默认值**由 Core 决定，但 Desktop 会自己 `new SourceBackupStore(...)`（例如 `ChapterRepairApplier(store.DirectoryPath)`）。也就是说"备份放哪"这个决定实际由 Desktop 传下去。
3. `InternalsVisibleTo` 让测试能碰到 internal 成员，所以测试里的调用形态和真实调用形态不完全一致。

---

## 3. 前端结构地图

### 3.1 窗口清单

**27 个 `Window` 子类**，另有 **15 处代码内建的匿名对话框**（`new Window { … }` 或 `ThemedWindow` 工厂，工厂定义在 `ChapterWorkbench.cs:382`）。唯一非模态的是 `TaskCenterWindow`（`MainWindow.xaml.cs:3657` 用 `Show()`），其余全部 `ShowDialog()`。

| 类别 | 窗口 |
|---|---|
| 主流程 | `MainWindow`、`ChapterEditorWindow`（章节工作台）、`RepairReviewWindow`（目录修复 · 核对后应用）、`PreflightWindow`、`TaskCenterWindow`、`BookPreviewWindow`、`CoverLightboxWindow`、`SourceBackupWindow`（原文备份）、`SourceRestoreWindow`（恢复这个版本）、`ConversionHistoryWindow`、`InkDialogWindow`（通用确认框） |
| 设置 | `SettingsWindow`、`AutomaticCheckWindow`、`ShortcutManagerWindow`、`NumericHeadingRulesWindow` |
| 编辑与管理 | `TextCleanupWindow`、`TextCleanupRuleManagerWindow`、`BuiltinCleanupWindow`、`AdvertisementConditionWindow`、`BatchMetadataWindow`、`MetadataMappingWindow`、`MetadataMappingRuleWindow`、`PresetManagerWindow`、`FavoriteFolderManagerWindow`、`CustomCssWindow`、`IllustrationManagerWindow`、`IllustrationPositionWindow` |

**命名陷阱**：`ConversionSettingsWindow` 名为 Window，实际是 `UserControl`（`ConversionSettingsWindow.xaml.cs:11`），作为面板嵌在 `MainWindow.xaml:323`。它不是独立窗口。

### 3.2 会写用户书稿的三条路径

这是全文档最该记住的一张表。**Desktop 全项目没有一处绕过 Core 直接写 TXT**（0 处指向 TXT 的 `File.WriteAllText` / `StreamWriter`），能改书稿的只有这三条，全部经过 Core 的事务层：

| 入口 | 链路 | 写什么 |
|---|---|---|
| 工作台「预览目录修复」→ 确认窗里选「同时改原文」 | `ChapterAutoRepairPanel.cs:103` → `ChapterRepairApplier.ApplyAsync(…, EditSource, …)` → `SourceEditTransaction` | 章节树 + TXT（先备份、临时文件、原子替换） |
| 原文备份窗口「恢复此版本…」 | `SourceBackupWindow.cs:277` → `SourceRestoreWindow:285` → `RestoreSelectedAsync:240` → `RestoreTransaction` | TXT（可选连当时的树） |
| 原文备份窗口「完成未完成的原文修改…」 | `SourceBackupWindow.cs:316` → `SourceEditRecovery.RecoverAllAsync` | 完成上一次中断的替换 |

**不在这个表里的都不写 TXT。** 两个最容易误会的：

- 工作台的「应用并返回」（`ChapterEditorWindow.xaml.cs:331`）**不写 TXT**，只产出 `ResultPlan`，由 `MainWindow.xaml.cs:1962-1982` 存进项目/恢复快照。
- 确认窗里选「只改章节树」时走 `ChapterAutoRepairPanel.cs:198-206`，同样不碰文件。

### 3.3 死 UI 与隐藏入口

读主窗口 XAML 会撞见一批"代码里有、界面上没有"的东西。它们不是未清理的错误，但会浪费阅读时间：

| 位置 | 状况 |
|---|---|
| 「章节正文」页 | `MainWindow.xaml.cs:242` 把 `WorkspacePage.Chapters` 重定向为 `Library`，于是 `InkChaptersPage.Visibility`（`:256`）永远 Collapsed，`MainWindow.xaml:108` 的入口按钮自己也 `Visibility="Collapsed"`。**但该页控件仍被代码大量读写**（`ChapterBookCombo:401`、`ChapterPreviewText:3037+`） |
| 「成品预览」 | `PreviewBookButton`（`MainWindow.xaml:95`）与 `QuickPreviewButton`（`:183`）都是 Collapsed，`BookPreviewWindow` 只能被这两个隐藏按钮打开 |
| `ShowHistory_Click` | 定义在 `MainWindow.xaml.cs:3602`，XAML 里**没有任何 Click 绑定**；转换历史窗口实际只从 `Convert_Click` 的失败分支（`:3828`）弹出 |
| 隐藏设置区 | `MainWindow.xaml:352-355` 整块 `Visibility="Collapsed"`，但其中的 `EncodingCombo` / `ChapterRegexText` / `OutputCollisionCombo` / `ParallelismCombo` 仍被业务逻辑读取 |

### 3.4 Desktop 里的自研逻辑（Core 之外）

只有三处，都不是"绕过 Core 重写业务"：

- `MainWindow.xaml.cs:3144` `LooksLikeChapterHeading` —— **全项目 0 调用的死代码**
- `ImageDiagnostics.cs:11-23` —— Desktop 独有的 Kindle 适配阈值
- `LayoutPreviewPaginator.cs:5` —— 有意为之的近似分页

### 3.5 MainWindow 与工作台的 partial 分工

- `MainWindow` 由 **3 个 partial** 组成：`MainWindow.xaml.cs`（主体与绝大多数处理器）、`MainWindow.Workflow.cs`（工作流导航）、`MainWindow.Performance.cs`（性能相关，如列表虚拟化）。
- `ChapterEditorWindow`（章节工作台）由 **10 个 partial** 组成：

| 文件 | 行数 | 负责工作台的哪一块 | 会写 TXT？ |
|---|---|---|---|
| `ChapterEditorWindow.xaml.cs` | 1017 | 窗口本体、树绑定、全部 XAML 事件入口、撤销重做、保存 | **否**（只产出 `ResultPlan`） |
| `ChapterWorkbench.cs` | 640 | 顶部提示与保存状态、识别设置对话框、整理工具菜单、原文版本迁移 | 否 |
| `ChapterReviewPanel.cs` | 788 | 右侧核对卡全部文案与动作按钮、问题分类、断点、参考目录获取 | 否 |
| `ChapterAutoRepairPanel.cs` | 234 | 一键修复按钮与进度窗、**应用的唯一出口** | **可能** —— `:198` 的 TreeOnly 分支不写，`:213` 的 EditSource 分支写 |
| `ChapterContentComparison.cs` | 184 | 重复正文双栏对比、按参考目录定位、删除一份 | 否（只重建树 + 移除清单） |
| `ChapterCatalogPanel.cs` | 214 | 「目录辅助修复」大对话框（来源 / 粘贴 / 导入 / 书源） | 否 |
| `ChapterEditorActions.cs` | 170 | 改成品标题、按邻章推错号、清重复标题、开外部编辑器 | 否（开的是**独立副本**） |
| `ChapterMultiSelection.cs` | 155 | Ctrl/Shift 多选、批量分段与降级 | 否 |
| `ChapterStructureRecoveryPanel.cs` | 66 | 结构建议横幅三按钮 + 结构预览对话框 | 否 |
| `BookSourcePanel.cs` | 414 | 「目录书源 · 顺序与可用性」对话框、书源目录预览窗 | 否 |

**这三个 `*Panel.cs` 不是独立窗口类**，而是 `partial class ChapterEditorWindow` —— 按文件名找"窗口"会漏掉它们。

工作台的**唯一真源**是 `Roots`（`ChapterEditorWindow.xaml.cs:91`），出口只有三个属性：`ResultPlan` / `ResultHierarchyOptions` / `ResultChapterPattern`（`:94-96`）。

### 3.6 主窗口的分区

`MainWindow.xaml` 的 `RootLayout` 是 7 行网格（`84 / 10 / Auto / 10 / * / 10 / 72`）：

| 区 | 内容 |
|---|---|
| 顶栏 | Logo 与标题、项目菜单、搜索框（Ctrl+F）、主题切换、设置、**三个 `Visibility="Collapsed"` 的遗留按钮** |
| 恢复横幅 | 「上次的工作还在」+ 恢复 / 暂不恢复 / 删除快照 |
| 左栏（200px） | 四步导航：`1 导入书稿` / `2 检查与修复` / `3 制作设置` / `4 转换与验收`；末尾是转换记录、原文备份、快捷键、版本号。另有 `章节正文`、`封面信息` 两个入口是 `Collapsed` |
| 右内容卡 | 七个页面互斥显示：`InkLibraryPage`（书稿列表 + 检查器）、`WorkflowReviewPage`（问题总表）、`InkChaptersPage`（**不可达**）、`InkCoverPage`、`InkLayoutPage`（版式 + Kindle 预览）、`InkConvertPage`（书稿列表 + 转换设置面板）、`TaskCenterLanding` |
| 底部操作栏 | 输出格式、排版方案、状态与进度、重试失败 / 检查问题 / 暂停 / 取消 / **开始批量转换** |
| 隐藏设置区 | 整块 `Visibility="Collapsed"`，但 `EncodingCombo`、`ChapterRegexText`、`OutputCollisionCombo`、`ParallelismCombo` 仍被业务代码读取 |

页面切换是 `ShowWorkspacePage(page)`（`MainWindow.xaml.cs:239-309`）：逐个设 `Visibility`，在 `switch` 里改标题与副标题，调 `UpdateContextualControls()`，必要时播放过渡动画。

---

## 4. 后端结构：Core 的子系统

### 4.1 96 个手写文件按职责分成 19 个子系统

| # | 子系统 | 文件数 | 最关键的公开类型 |
|---|---|---|---|
| S1 | **转换编排与选项模型** | 6 | `EasyPubConverter`（唯一写盘调度点）、`BatchConverter`、`ConversionRequest` / `ConversionOptions` |
| S2 | **书稿读取、解码与行编辑** | 4 | `TextFileDecoder`（BOM → 严格 UTF-8 → GBK 回退）、`SourceTextDocument`、`ChapterEditingDocument` |
| S3 | **文本清理** | 2 | `TextCleanupPipeline` / `TextCleanupPreview` / `TextCleanupChange`、`BuiltinCleanupConfiguration` |
| S4 | **标题语法原子规则** | 4 | `HeadingSyntax`（**所有**标题规则的唯一来源）、`HeadingTypoRules`、`NumericHeadingRule` / `NumericHeadingFilter` |
| S5 | **章节树与规范化模型** | 3 | `ChapterTreeEntry` / `ChapterTreePlan` / `ChapterTreeDocument`、`CanonicalChapterModel`、`ChapterTreeDocumentCache` |
| S6 | **参考目录获取** | 4 | `ReferenceCatalogClient` / `ReferenceCatalog`、`BookSource` 族、`CatalogCache`、`ReferenceCatalogInput` |
| S7 | **参考目录对齐与修复方案** | 3 | `ReferenceLocator` / `ReferenceLocation` / `LocatedChapter`、`ReferenceAlignment`、`ReferencePlan` / `ReferenceAction` / `ReferencePlanner` |
| S8 | **章节诊断与人工审查** | 12 | `ChapterDiagnostics`、`ChapterBreakpoints`、`ChapterReviewAnalyzer`、`ChapterContentDuplicates`、`ChapterRepeatedSequences`、`MissingChapterHeadings`、`UnnumberedHeadings`… |
| S9 | **一键修复与自修复** | 2 | `ChapterAutoRepair` / `AutoRepairOutcome`、`SelfRepairPlanner`（`BasisName = "SelfRepair"`） |
| S10 | **修复方案编译** | 8 | `RepairDecision` / `SourceEffectPolicy`、`RepairEffect`（三维）、`RepairPlanCompiler` / `RepairCompilation`、`RepairBaseVersion`（三指纹）、`RepairIntegrity` |
| S11 | **补丁与树变更模型** | 3 | `SourcePatch` / `SourceOperation` / `SourcePatchNormalizer`、`SourcePatchRenderer`、`TreePatch` / `ChapterTreeMutation` |
| S12 | **修复执行、事务与版本迁移** | 7 | `ChapterRepairApplier`、`SourceEditTransaction` / `SourceEditRecovery`、`RepairJournal` / `RepairRecoveryPlanner`、`RepairExecutionManifest`、`SourceCoordinateMap`、`SourceVersionTransition` |
| S13 | **备份与恢复** | 6 | `SourceBackupStore` / `Inventory` / `Layout`、`SourceFullStateStore`、`SourceDiff`、`RestoreTransaction` |
| S14 | **EPUB / MOBI 输出、模板、字体、图片** | 10 | `LegacyEpubWriter`、`LegacyMobiWriter`、`LegacyMobiPostProcessor`、`LegacyTemplates`、`LegacyTextParser`、`MobiContentPackager`、`FontEmbeddingService` |
| S15 | **EPUB 导入** | 1 | `EpubInspectionService`、`EpubCompatibilityImporter`、`EpubPackage` |
| S16 | **前置检查、成品验收与就绪度** | 5 | `ConversionPreflightInspector` / `Cache`、`ArtifactValidationService`、`BookAnalysisCoordinator` / `ReadinessEvaluator` |
| S17 | **设置、历史、项目与元数据规则** | 7 | `EasyPubAppSettings`、`ConversionHistoryStore`、`EasyPubProjectStore`、`MetadataMappingResolver`、`LegacyEasyPubConfig` |
| S18 | **更新与版本** | 5 | `UpdateChecker`（GitHub + AtomGit 双源）、`AppVersion`、`UpdateInstaller` |
| S19 | **生成侧横切基础件** | 4 | `AtomicOutputWriter`、`OutputPathPolicy`、`PublicationChangeReceipt`、`BookPreviewService` |

**读代码的捷径**：S6 + S7 + S8 + S9 + S10 + S11 + S12 加起来 **44 个文件**，是"章节修复"这一件事的全部。它们不是可以跳过的支线 —— 用户遇到有缺陷的书稿时，全部主流程都在这条线上。

### 4.2 主路径 / 分支 / 遗留

**转换一本正常书的必经之路**：

```
BatchConverter.ConvertWithReportAsync                     批量调度
  └─ EasyPubConverter.ConvertAsync                        Core 里唯一的写盘调度点
       ├─ .epub → LegacyEpubWriter.WriteAsync
       │            └─ LegacyTextParser.ParseAsync         解码 → 清理 → 按章节树分章
       └─ .mobi → LegacyMobiWriter.WriteAsync
                    ├─ 先调 LegacyEpubWriter 生成中间 EPUB
                    ├─ 可选 MobiContentPackager.Optimize（章节合并进约 192 KB 分片）
                    ├─ kindlegen
                    └─ LegacyMobiPostProcessor（剥离源包、注入元数据、结构校验）
  └─ ArtifactValidationService.ValidateAndSaveAsync        仅当"成品验收"开关打开
```

**只有走到特定分支才会碰到的**：

| 分支 | 入口 | 涉及子系统 |
|---|---|---|
| 章节修复工作流 | `ChapterAutoRepair.RepairAsync` | S7 S8 S9 S10 S11 |
| 应用修复（会改原文） | `ChapterRepairApplier.ApplyAsync` | S12 S13 |
| 崩溃恢复 | `SourceEditRecovery.RecoverAllAsync` | S12 |
| 恢复到历史版本 | `RestoreTransaction.ExecuteAsync` | S13 |
| EPUB 导入 | `MobiOptions.EpubInputMode` | S15 |
| 导入旧版 EasyPub 配置 | `LegacyEasyPubConfig.Load` | S17 |
| 更新检查与安装 | 启动时或手动 | S18 |
| 工作台版式预览 | `BookPreviewService.BuildAsync` | S19 |

**可以确认的遗留代码（全仓零引用，含 tests）**：

| 位置 | 说明 |
|---|---|
| `ReferenceAlignment.cs:256` `ReferenceDiagnostics` / `:62` `ReferenceDiagnosis` | 它做的"目录 × 本地全局对齐"已被 S7 那条线取代 |
| `ConversionPreflightCache.TryGet`（`:53`）/ `Store`（`:76`） | `src\` 内零调用，只有测试用（产品入口是 `InspectAsync`） |
| `MainWindow.xaml.cs:3144` `LooksLikeChapterHeading` | Desktop 内零调用 |

**注意**：`CanonicalChapterModelFactory`、`TreePatch`、`RepairEffect` 族这些"Desktop 里不出现类型名"的类**不是**死代码 —— 它们由 Core 内部链路使用，只是没有外部命名它们。

### 4.3 `Legacy*` 命名陷阱（必读）

Core 里有 6 个 `Legacy` 前缀的文件。**名字说它们是遗留代码，实际上它们是转换的主路径**：

| 文件 | 实际角色 | 证据 |
|---|---|---|
| `LegacyEpubWriter.cs` | **EPUB 输出的唯一实现**（462 行） | `EasyPubConverter.cs:25` 直接调它 |
| `LegacyMobiWriter.cs` | **MOBI 输出的唯一实现**（327 行） | `EasyPubConverter.cs:27` 直接调它 |
| `LegacyTextParser.cs` | **文本解析与分章**，EPUB 输出的第一步 | `LegacyEpubWriter.cs:34`、`BookPreviewService.cs:49` |
| `LegacyTemplates.cs` | HTML / CSS 模板 | `LegacyEpubWriter.cs:67,88,132`、`LegacyMobiWriter.cs:275` |
| `LegacyMobiPostProcessor.cs` | MOBI 结构修补与元数据注入 | `LegacyMobiWriter.cs:212-217`、`ArtifactValidation.cs:214` |
| `LegacyEasyPubConfig.cs` | 读旧版 EasyPub 的 `config.xml` | `ConversionSettingsWindow.xaml.cs:227` |

"Legacy" 指的是**输出格式与旧版 EasyPub 兼容**，不是"这段代码要被删"。把它当成死代码是读这个仓库最容易犯的第一个错。

---

## 5. 数据模型：五组核心类型

理解这个软件，只需要理解五组类型。它们的**指纹字段**是把它们串起来的关键。

### 5.1 文本与坐标（原文的两种表示）

```csharp
SourceTextLine(string Text, string? Terminator)     // 每一行带自己的行尾；最后一行可以没有
SourceTextDocument(Lines, Encoding, Preamble, DefaultNewLine)
    .Render()           // 无损还原成文本；用它验证"我理解对了这份文件"
    .NewLineForInsertBefore/After(line)
```

**为什么要两种**：`ChapterTreeDocument` 面向"识别与编辑"（行号 + 文本），`SourceTextDocument` 面向"改写"（行尾、BOM、编码都要留住）。两者都走同一个解码器 `TextFileDecoder` —— 这一点曾经是缺陷：树走 GBK 回退，文档走 `File.ReadAllText`（UTF-8），在 GBK 书稿上行号会错位。

### 5.2 章节树（内存里的唯一真相）

```csharp
ChapterSourceRange(StartLine, EndLine)              // 闭区间，行号从 1 起，不含标题行
ChapterTreeEntry(Id, Title, Level, IncludeInToc, TitleLineNumber, ContentRanges)
    IsFrontMatter / HeadingLevel / RecognitionSource
ChapterTreePlan(SourceSha256, Entries)              // 可持久化；指纹不符就拒绝复用
ChapterTreeDocument                                 // 树 + 原文行的只读视图 + 识别设置
```

四个容易误读的字段：

| 字段 | 是什么 | 常见误解 |
|---|---|---|
| `Level` | 目录层级（树里第几层） | 与 `HeadingLevel` 混为一谈 |
| `HeadingLevel` | 成品 HTML 里用 h1..h4 的哪一个 | 以为它决定树层级 |
| `TitleLineNumber` | 标题所在**原文行**；`null` = 没有实体标题行 | 以为它等于 `Level` |
| `ContentRanges` | 该条目**独占**的正文行，**不含标题行** | 以为标题行也在里面 |

`ValidatePlan` 强制**任意两个条目的正文行不得重叠**。这条不变量是后面一切修复的安全网。

`Id` 是 `Guid`，**每次重建都会换新的** —— 这是很多 bug 的历史源头，所以修复链路一律用"稳定身份"（`StableKey`：有标题行用 `"L"+行号`，没有用 `"T"+标题`）而不是 `Id` 来绑定。

### 5.3 参考目录与对齐

```csharp
ReferenceCatalog(Source, PageTitle, Nodes)          // 抓来的或粘贴的官方目录
ReferenceNode(Title, Kind, ...)                     // 卷 / 章 / 节
ReferenceLocation(Chapters, Stats)                  // 逐章定位结果
LocatedChapter(Reference, Volume, Line, LocalTitle, Lines)
    Occurrences => Lines.Count                      // 原文里出现几次
    IsDuplicate => Lines.Count > 1
ReferencePlan(Catalog, Location, Actions)           // 动作清单
ReferenceAction(Kind, Line, Title, Detail, EntryId, Reference, Recommended)
```

`ReferenceActionKind` 有 8 种：`AddChapter` / `Retitle` / `AdoptHeading` / `MissingBody` / `RemoveDuplicate` / `KeepExtra` / `VolumeNote` / `DemoteExtra`。**其中只有 4 种可以被勾选执行**，另外 4 种是"只报告"——`MissingBody`（软件不补造正文）、`VolumeNote`、`KeepExtra`（默认不勾）、`AdoptHeading`（默认不勾）。

### 5.4 修复方案与决定（第二轮重构引入）

```csharp
RepairProposal(Basis, BaseVersion, Plan, Catalog)   // 方案 = 依据 + 版本指纹 + 动作清单
RepairDecision(ActionIdentity, Selected, SourceEffect)
SourceEffectPolicy                                  // 按动作种类：这类动作"能不能"改原文
SourceEffectDecision                                // 按这一条决定：None / ApplyIfAvailable / ApplyRequired
RepairBaseVersion(BaseSourceSha256, BaseTreeFingerprint, RecognitionFingerprint)
RepairCompilation(TreePatch, SourcePatch, ExpectedResult, Problems)
RepairExecutionManifest(...)                        // 写文件之前冻结的"这次要做什么"
```

**三个指纹**绑住一个方案：源文件 SHA-256、章节树指纹、识别设置指纹。任何一个变了，方案作废并说明原因（"原文已过期，请重新生成方案"）。

`SourceEffectPolicy` 与 `SourceEffectDecision` 的区别是刻意的：前者回答"改标题这类动作有没有能力改原文"（静态、按种类），后者回答"这一条用户决定怎么落地"（动态、按条目）。混在一起曾经导致"用户勾了但从没执行"。

### 5.5 事务、坐标与版本迁移

```csharp
SourcePatch(ActionSetId, BaseSourceSha256, Operations)   // 要改哪些行
    DeleteOriginalLine / ReplaceOriginalLine / InsertLineAtAnchor
SourceCoordinateMap(...)      // 旧行号 ↔ 新行号的完整对照，含删除/替换/插入集合
SourceEditTransaction(...)    // 可中断的原子替换：Prepared → BackupCreated → TempWritten
                              //   → SourceReplaced → DocumentReloaded → StateMigrated → Committed
RepairExecutionManifest       // 冻结的意图，崩溃恢复读它而不是重新规划
SourceTransitionState         // 一个版本下的全部内存模型（文本、树、计划、目录、复核）
SourceTransitionResult        // 迁移结果：哪些身份保留、哪些失效
```

---

## 6. 主流程一：从 TXT 到成品

```
Convert_Click (MainWindow.xaml.cs:3712)
  ├─ BuildConversionRequestsAsync()          把书单 + 设置折成 ConversionRequest
  ├─ GetPreflightReportAsync()               转换前检查（结果按输入+选项缓存）
  │    有 Error  → 弹 PreflightWindow 并停止
  │    有 Warning → 弹窗让用户确认
  ├─ BatchConverter.ConvertWithReportAsync() 并发调度（默认并行度由策略决定）
  │    └─ EasyPubConverter.ConvertAsync()    按扩展名二选一
  │         ├─ .epub → LegacyEpubWriter.WriteAsync()
  │         │     └─ LegacyTextParser.ParseAsync()   解码 → 行 → 清理 → 按树分章
  │         └─ .mobi → LegacyMobiWriter.WriteAsync()
  │               ├─ 内部先产 EPUB，再交给 kindlegen
  │               └─ LegacyMobiPostProcessor 修补结构、注入元数据、剥离源包
  ├─ ArtifactValidationService.ValidateAndSaveAsync()   成品验收（结构是否合格）
  └─ ConversionHistoryStore.AppendAsync()    写转换历史
```

**一个容易忽略的设计**：`EasyPubConverter.ConvertAsync` 在调用写入器之前，往请求里塞了一个 `CaptureTextChanges` 回调（`EasyPubConverter.cs:22-23`）。清理流水线每改一次文本就回调一次，最终汇总成 `PublicationChangeReceipt` —— 这是"变更记录"功能的唯一数据来源。

---

## 7. 主流程二：章节修复（这是全项目最复杂的一块）

### 7.1 两种依据

| | 有参考目录 | 无参考目录（自修复） |
|---|---|---|
| 谁产生方案 | `ReferencePlanner.Build` | `SelfRepairPlanner.Build` |
| 能判断什么 | 缺章、多章、重复、标题、卷结构 —— 全部与目录对比 | 只有"书自己能看出的"：重复标题、数字标题写法不规范 |
| 完整性 | **可判定**，逐章核对 | **不可判定**，如实说 |
| 方案来源标记 | `ReferenceCatalog` | `SelfRepair` |

自修复刻意收窄（`SelfRepairPlanner.cs` 的类注释）：凡是要靠目录才能决定的（这一章是不是**缺了**、这个标题该不该**采用**、这一章是不是**多余**）一律不做。"从书自身猜这些"就是"凭空造章"，规则是**永远不造**。

### 7.2 三段式：规划 → 编译 → 确认

```
① 规划（生成"要做什么"）
   ChapterAutoRepair.RepairAsync()
     ├─ 有目录：ReferenceLocator.Locate(三遍定位) → ReferencePlanner.Build() → ReferencePlan
     └─ 无目录：SelfRepairPlanner.Build() → ReferencePlan（同一套类型）
   产出：AutoRepairOutcome（动作清单 + 分类报告 + 统计）

② 编译（算出"会发生什么"，纯函数、只读）
   ChapterRepairApplier.Preview(proposal, document, decisions, mode)
     ├─ RepairPlanCompiler.Compile()      → TreePatch + SourcePatch + Problems
     ├─ RepairEffectCompiler              → 每条动作的三维影响（树 / 正文归属 / 原文文本）
     └─ RepairPlanValidator + SourcePatchNormalizer + SourceCoordinateMap
   产出：RepairApplicationPreview（能不能应用、会改几行、有哪些问题）

③ 确认与落地（用户点头之后才写盘）
   RepairReviewWindow  → 用户逐条勾选 + 选落地模式
   ChapterRepairApplier.ApplyAsync(proposal, document, decisions, mode)
```

**第 ② 步是这一轮重构的核心成果**：预览与实际应用走的是**同一份编译产物**，所以"窗口上显示的数字"和"接下来会发生的事"不可能来自两套计算。`ChapterRepairApplier` 是**唯一**的应用入口 —— 按钮和测试都走它。

### 7.3 两种落地模式

`RepairLandingMode` 决定的是**用哪套产物**，不是动作的含义：

| | `TreeOnly`（只改章节树） | `EditSource`（同时改原文） |
|---|---|---|
| 章节树 | 改 | 改 |
| 原文 TXT | **不动** | **被重写** |
| 事务 | **不创建**（没有文件改变，为它建记录等于记录"什么都没发生"） | 创建 `SourceEditTransaction` |
| 备份 | 快照（含当时的树） | 快照 + 事务自己的备份 |
| 可逆 | 工作台内撤销 | 事务可恢复 + 备份可恢复 |

**一条不变量**：同一个动作在两种模式下**含义完全相同**，只是能不能落到文件上不同。这是重构专门要消灭的缺陷 —— 重构之前，两种模式有各自的代码路径，会漂移。

### 7.4 哪些动作能改原文

`SourceEffectPolicy` 按种类回答，`NeedsExplicitOptIn(kind)` 标出**需要用户再勾一次**的种类（目前只有 `RemoveDuplicate`：它的"移除效果"是在别处算出来的，不能默认认为用户同意删那一行）。

---

## 8. 修复事务与崩溃恢复

这是 Phase 5 的产物，也是"允许改原文"这个决定的技术代价。

### 8.1 事务边界由文件字节决定，不由日志决定

```
PhysicalCommitState.Classify(当前哈希, BaseHash, ExpectedNewHash)
    == Base      → 提交还没发生
    == Expected  → 提交已经发生
    == 别的      → Conflict（程序之外有人改过它）
```

日志只记录"程序以为走到了哪"，**判断永远以磁盘为准**。理由是：崩溃恰好落在"做了但没记"之间，日志天然是滞后的。

### 8.2 七个状态与恢复动作

```
Prepared → BackupCreated → TempWritten → SourceReplaced → DocumentReloaded → StateMigrated → Committed
                                                                                          ↘ Conflict / RolledBack
```

`RepairRecoveryPlanner.Decide(日志状态, 磁盘状态)` 给出七种动作之一。**只有一格允许程序自己往前走**：

| 日志 | 磁盘 | 动作 |
|---|---|---|
| `TempWritten` | Base | **`ContinueReplace`** —— 唯一能自动完成替换的一格 |
| `Prepared` / `BackupCreated` | Base | `Discard`（作废记录，文件没动过） |
| `SourceReplaced` / `DocumentReloaded` / `StateMigrated` | Expected | `RollForward` / `ReRunTransition` / `Finish` / `CleanUp` |
| 任何已宣称替换 | Base | `Conflict`（**停下来，不猜**） |
| 任意 | Conflict | `Conflict` |

为什么只有一格：日志说 `TempWritten` 是"这次事务从未宣称跨过分界线"的陈述，文件也证实了这一点，而已渲染好的字节就躺在源文件旁边 —— 补完就是一次改名。再往后每一格，日志都在宣称一次文件没有显示的提交。

### 8.3 恢复是真的会做事

`SourceEditRecovery.RecoverAsync` 不只是报告，它会：完成替换（`ContinueReplace`）、重跑替换之后的簿记（`RollForward` / `ReRunTransition`，幂等）、收尾清理（`Finish` / `CleanUp`）、或判定冲突不动文件。

`RecoverAllAsync(root)` 一次处理**全部**未完成事务，最老的先做；一条失败不影响其余的。主窗口首次激活时自动调它，原文备份窗口的按钮调的是**同一个入口**。

---

## 9. 备份与恢复原文

| 层 | 存什么 | 文件 |
|---|---|---|
| 基线 | 每本书的首次原文，永不删除 | `baseline/original.bak` |
| 快照 | 每次修复前的原文，按内容哈希去重 | `snapshots/<sha256>.bak` |
| 全文状态 | 快照对应的**章节树**（可选） | `snapshots/<sha256>.tree.json` |
| 识别设置 | （**目前没人写**） | `snapshots/<sha256>.recognition.json` |
| 清单 | 这本书有哪些备份 | `manifest.json` |

`SourceBackupStore.EnsureSnapshot(document, tree)` 收下树时才会写 `.tree.json`。**这一步曾经没人做**（回调只传了路径，调用者拿不到树），导致 `FullStateSnapshotCount` 恒为 0 —— 也就是说"恢复到当时的完整书稿状态"这个选项在界面上永远选不到。v1.60 修掉了：责任放进 `ChapterRepairApplier` 的默认备份回调，任何调用者都自动带上树。

恢复走的是**和修复同一条事务路径**（`RestoreTransaction`），不是特殊通道：先给当前版本做备份，再原子替换，然后 `SourceVersionTransition` 把章节树搬到恢复后的文本上。两种恢复目标：

- `SourceOnly` —— 只恢复文本，章节重新识别。**总是可用**。
- `FullBookState` —— 连当时保存的树一起还原，包括编排。**只在该版本存了树时可用**，否则拒绝（用重新识别的树冒充，等于报告成功却悄悄丢掉用户要的东西）。

---

## 10. 代码地图（按"我要改什么"索引）

| 我想改…… | 去哪 |
|---|---|
| 标题怎么算一个标题 | `HeadingSyntax.cs`（**所有**标题识别规则的唯一来源） |
| 章节怎么识别、一行归谁 | `ChapterTree.cs` 的 `Load` + `ChapterEditingDocument` |
| 目录怎么抓、怎么存 | `ReferenceCatalog.cs`、`CatalogCache.cs`、`ReferenceCatalogInput.cs` |
| 目录怎么与原文对齐 | `ReferenceLocator.cs`（三遍定位）、`ReferenceAlignment.cs`（打分） |
| 生成哪些修复动作 | `ReferencePlan.cs`（有目录）、`SelfRepairPlanner.cs`（无目录） |
| 一条动作的影响怎么算 | `RepairEffectCompiler.cs`、`RepairEffect.cs` |
| 动作怎么变成补丁 | `RepairPlanCompiler.cs`、`RepairSourcePatchCompiler.cs`、`SourcePatchNormalizer.cs` |
| 补丁怎么渲染、怎么校验 | `SourcePatchRenderer.cs`、`SourceCoordinateMap.cs` |
| 写文件的全部流程 | `SourceEditTransaction.cs` + `.State.cs` |
| 崩溃了怎么办 | `RepairRecoveryPlanner`（同一文件）、`SourceEditRecovery` |
| 改完之后树怎么跟上 | `SourceVersionTransition.cs`（6 个步骤） |
| 备份与恢复 | `SourceBackupStore/Layout/Inventory.cs`、`SourceFullStateStore.cs`、`RestoreTransaction.cs` |
| 文本清理规则 | `TextCleanupPipeline.cs`、`BuiltinCleanupOverride.cs` |
| EPUB 长什么样 | `LegacyEpubWriter.cs` + `LegacyTemplates.cs` |
| MOBI 长什么样 | `LegacyMobiWriter.cs` + `LegacyMobiPostProcessor.cs` |
| 成品验收标准 | `ArtifactValidation.cs` |
| 设置项 | `AppSettingsStore.cs` |
| 界面 | `src/EasyPub.Desktop/`，先看 `MainWindow.xaml` 的分区 |

---

## 11. 为什么这个代码库看起来乱

不是错觉。有五个具体来源，**其中三个是刻意的**。

### 11.1 命名与职责不符（最坑）

**两个反例，方向相反：**

**（a）`Legacy*` 说自己是遗留，其实是主路径。** 6 个 `Legacy` 前缀的文件全部在转换主路径上（§4 的表）。"Legacy" 指的是**输出格式与旧版 EasyPub 兼容**，不是"要删的旧代码"。
**代价**：第一次读这个仓库的人会把它们划掉，然后发现找不到 EPUB 是怎么生成的。

**（b）`ConversionSettingsWindow` 叫 Window，其实是 `UserControl`。**（`ConversionSettingsWindow.xaml.cs:11`）它作为面板嵌在 `MainWindow.xaml:323`，永远不会作为一个窗口出现。
**代价**：按名字 `grep Window` 找"有哪些窗口"会多出一个，按名字找"转换设置面板在哪"又会漏掉它。

### 11.2 同一个"修复"概念横跨三层，且两层各有自己的类型系统

```
规划层   ReferencePlan / ReferenceAction          ← 第一轮（目录辅助修复，v1.24–v1.57）
编译层   RepairProposal / RepairDecision / RepairCompilation   ← 第二轮（Phase 0–7，v1.60）
执行层   ChapterRepairApplier / SourceEditTransaction
```

`ReferenceActionKind`（8 种）与 `RepairEffect`（三维影响）是**两套描述"这条动作是什么"的语言**，靠 `RepairEffectCompiler` 翻译。这是刻意的分层（规划回答"该做什么"，编译回答"会发生什么"），但读代码时必须同时在脑子里维持两套词汇。

**加上第二轮引入的新概念**：三种指纹、两种落地模式、两层效果策略（Policy / Decision）、六个迁移步骤、七个事务状态。**这就是 Core 一半代码的来源。**

### 11.3 "双模式"这个词有两个完全不同的含义

| 说法 | 含义 | 类型 |
|---|---|---|
| **旧**（`docs/2026-09-15_功能逻辑与双模式设计.md`） | 识别依据：手工编辑 vs 自动识别 | `RecognitionSource` |
| **新**（Phase 6/7） | 落地方式：只改树 vs 同时改原文 | `RepairLandingMode` |

看旧文档会得到错的理解。本仓库里凡是没标年份的"双模式"，都要先问是哪一种。

### 11.4 同一本原文被四套规则各读一遍

| 谁 | 读原文干什么 | 入口 |
|---|---|---|
| 章节识别 | 建章节树 | `ChapterTree.LoadAsync` |
| 目录定位 | 找每一章在哪一行（**独立于识别**，有自己的候选与打分） | `ReferenceLocator.Locate` |
| 检查建议 | 找缺号、重复标题、重复正文 | `ChapterReviewAnalyzer` |
| 跨标题重复 | 正文相同但标题不同 | `ChapterContentDuplicates.FindCrossTitle` |

四套规则各有各的判据（"同名"在两个地方的定义甚至不同：`ChapterContentDuplicates` 用 FormKC 归一化去空白，`SelfRepairPlanner` 用 `StringComparer.Ordinal` 精确比较）。**这不是设计失误，是四种问题本来就不同** —— 但它们共用"标题像不像"这个模糊地带，所以看起来重复。

### 11.5 一个 `Window` 类被切成十个文件

`ChapterEditorWindow` 有 10 个 partial、`MainWindow` 有 3 个。partial 是为了让单文件不超过可读上限（工作台合计约 6,000 行），代价是**没有一处能看到全貌** —— 想找"这个按钮调了什么"必须先知道它大概在哪个 partial 里。

**顺带一条**：10 个 `*Store` 类各自复制了同一段"环境变量覆盖 → 否则默认路径"的样板（`EASYPUB_APP_SETTINGS_PATH`、`EASYPUB_SOURCE_BACKUPS_PATH`、`EASYPUB_REFERENCE_CATALOGS_PATH` …共 11 个环境变量）。样板一致所以能用，但没有一个共同基类。

### 11.6 有一批代码活着，但界面上到不了

主窗口里至少四处是"看得见代码、看不见界面"（§3.3 的表）：整页死掉的「章节正文」、两个被 Collapsed 的预览按钮、没有 Click 绑定的 `ShowHistory_Click`、以及一整块隐藏的设置区 —— **而其中一部分控件仍在被业务逻辑读取**。

**代价**：读 `MainWindow.xaml.cs` 时会不断遇到"这个控件哪来的"和"这个功能用户怎么点得到"，而两个问题的答案都是"点不到"。反过来，想删除任何一处都不能只删 UI —— 读取那些控件的业务代码还在。

这不是疏忽，是功能迭代把入口收起来了但没拆掉旧线路。**判断方法**：`grep` 控件名，如果只有赋值没有读取，它才是真死代码。

### 11.7 同一件事被写了很多遍

这是"乱"最实质的一条。以下每一条都经过跨 `src\` 与 `tests\` 的全仓 grep 核实：

| 同一件事 | 有几份实现 | 代价 |
|---|---|---|
| **原子写文件**（临时文件 → 重命名） | **17 处手写**（`AppSettingsStore.cs:163`、`BookSource.cs:230`、`ConversionHistoryStore.cs:101`、`EasyPubProjectStore.cs:99`、`RepairJournalStore.cs:69`、`SourceBackupStore.cs:77`、`SourceFullStateStore.cs:44` …），而专门抽象出来的 `AtomicOutputWriter` **只被 1 处使用**（`LegacyMobiWriter.cs:219`） | 多数手写版本**没有** `Flush(flushToDisk:true)`，语义比抽出来的那个弱 —— 最该用统一实现的场合，用的是较弱的版本 |
| **把补丁渲染成文本** | 2 份必须永远一致的实现：`SourcePatchRenderer.Render` 与 `SourceCoordinateMap.Build`（连 `CompareInserts` 都各写一遍：`:136` 与 `:380`） | 改一处的分桶/排序规则而忘了另一处，坐标表与真实渲染会悄悄分叉 |
| **在一章正文里找漏识别标题并拆章** | 2 套：`MissingChapterHeadings`（靠章号缺口 + 纠错表）与 `UnnumberedHeadings`（靠目录标题匹配） | 拆章代码几乎逐行对应（`MissingChapterHeadings.cs:127-138` 与 `UnnumberedHeadings.cs:54-68`），却产出两种 issue code |
| **重复正文检测** | **4 套**：`ChapterContentDuplicates.Find`（5-gram）、`FindCrossTitle`（按正文精确分组）、`ChapterRepeatedSequences`（配对索引序列）、`DuplicateChapterCleaner`（相邻同级同标题合并） | 用户可能同时看到 `chapter_duplicate`、`chapter_content_duplicate`、`chapter_cross_title_duplicate`、`chapter_repeated_sequence` 四类卡片，**删除粒度各不相同** |
| **章号顺序 / 缺口判定** | 2 处：`ChapterDiagnostics.cs:65-67`（作用域 `(parent, level, unit)`）与 `ChapterBreakpoints.cs:97-135`（作用域字符串 `parent｜level｜unit`） | 口径与阈值都不同 |
| **"编号重启就建卷"的启发式** | 2 处：`ChapterStructureRecovery.cs:60-74` 与 `ChapterAutoRepair.cs:550` | 阈值不一致 |
| **"这本书能不能发"** | 3 处：前置检查 `ConversionPreflightInspector`、成品验收 `ArtifactValidationService`、就绪度 `ReadinessEvaluator` | 检查项几乎不重叠，唯一同义项是封面，而且方向相反 |
| **"无目录时整理结构"** | **三代并存**：`SelfRepairPlanner`（产出动作、复用整条修复链路）、`ChapterStructureRecovery`（直接重建整棵树、**抛异常**表示不可用）、`ChapterStructureSuggestion`（只读提示） | 启发式互有重复，失败方式完全不同 |

**这不是简单的复制粘贴失控**。每一次分家都有当时的理由（不同的产品阶段、不同的粒度需求）。但它造成两个实际后果：

1. **改一处不会改到另一处**。想调"什么算重复正文"，得先决定改四套里的哪几套。
2. **用户看到的是四张卡片，而不是一个结论**。产品层面没有人负责把它们合成一句话。

---

## 12. 测试、探针与验收

**测试怎么组织**：

- Core 测试按子系统分文件，文件名大多与 Core 的文件对应（`SourceVersionTransitionTests` ↔ `SourceVersionTransition`）。
- Desktop 测试分两类：**链路测试**（驱动真实窗口，断言按钮与开发路径走同一个 Core 入口 —— `LandingModeWiringTests`、`SourceBackupRestoreTests`、`ChapterWorkbenchCatalogPositionTests`）与**渲染测试**（`WindowScreenshotTests`、`PanelScreenshotTests`，把窗口渲成 PNG 供人工核对）。
- 有一批测试是**缺陷的回归守护**，注释里直接写着"实测抓到过"：

| 测试 | 守护的缺陷 |
|---|---|
| `SourceVersionTransitionTests.Reloading_the_recognition_tree_does_not_replace_the_users_own_tree` | 迁移顺手把用户编排的树换成"重新识别的结果"，报告却是"保留 0 个" |
| `SourceVersionTransitionTests.Deleting_a_whole_chapter_does_not_hand_its_lines_to_its_neighbour` | 相邻区间被合并，一章吞掉下一章（实测：第六卷第七章吞掉第八章） |
| `SourceDiffTests.A_changed_line_is_one_replacement_not_a_removal_plus_an_addition` | 改一行被写成"删一行 + 插一行"，坐标表对那一行没有答案，该章被判成"没有正文" |
| `FaultInjectionTests`（28 条，六个崩溃点各测一遍） | 恢复之后文件必须停在有人选过的状态上，且**绝不第二次替换原文** |
| `SourceBackupStoreTests` / `SampleBookRegressionTests` | 备份与样书整体不回归 |
| `ChapterWorkbenchCatalogPositionTests` | 对比重复正文时不告诉用户哪一份在原位置 |

**已知的骨架**：

| 层 | 规模 | 怎么跑 |
|---|---|---|
| Core 测试 | 81 文件 / 12,283 行；540 个 `[Fact]` + 51 个 `[Theory]`，展开 **794 个用例** | `dotnet test tests\EasyPub.Core.Tests` |
| Desktop 测试 | 24 文件 / 5,373 行；137 个 `[Fact]` + 5 个 `[Theory]`，展开 **148 个用例** | 必须串行：`-- -m:1 -- xUnit.MaxParallelThreads=1` |
| 样书探针 | `work\catalog-fix-sim`（25 文件 / 3,716 行，**未纳入版本控制**） | `dotnet run -c Release --project work/catalog-fix-sim/sim.csproj -- <探针名>` |

探针名（每个对应一个已完成的阶段，输出的是**实测数字**而非断言）：`phase1` `baseline` `census` `volloss` `versioning` `effect` `equiv` `compile` `recovery` `transition` `restore` `phase6` `nocatalog` `phase7` `wiring` `gaps`。

**两个必须知道的坑**：

1. **MSBuild 会静默跳过 `CoreCompile`**。改过 `src/EasyPub.Core` 之后必须先删 `src/EasyPub.Core/obj` 和 `bin`，否则跑的是上一次编译的 DLL，测试全绿但改的代码没生效。
2. **Desktop 全量套件本身不稳**：`Application` 是进程级单例，由创建它的线程独占，线程一结束它就属于一条死线程。基线（`119dc3d`）三次运行是 9（崩溃）/ 5 / 0 个失败，本轮改动后是 6～12 个失败 —— 崩溃已经修掉，剩下的波动没治理。所以**这一层的验收按单类运行 + 探针**，不是"全量绿"。

---

## 13. 已知边界与待办

| # | 事项 | 影响 |
|---|---|---|
| 1 | `SnapshotRecognitionPath`（`.recognition.json`）没人写 | 恢复全文状态后，分层开关与三个层级正则会回到全局设置；只在"恢复之后又点重新识别"时暴露 |
| 2 | `SourceDiff` 在两侧行尾约定不同时推出的补丁渲染不出目标文本 | **对软件运行无影响**（实测：写入的是目标版本的原始字节，坐标表从目标文本读，不渲染补丁）。是潜在陷阱：执行清单里存着一份渲染不出当时字节的补丁，目前无人重渲染它 |
| 3 | `InsertLineAtAnchor` 只由 `RepairSourcePatchCompiler` 和 `SourceDiff` 产生 | 没有修复动作走它 —— 是能力闲置，不是缺陷 |
| 4 | Desktop 全量套件失败波动 6～12 | 影响"以后能否发现缺陷"，不影响软件运行 |
| 5 | `docs/NEXT_TASK.md`、`HANDOFF*.md` 停在 v1.55 | 看它们会得到过时的版本号与测试基线 |
| 6 | `main` 领先 `origin/main` 29 个提交 | 全部未推送 |
| 7 | **`SelfRepairPlanner` 只按标题文字分组，不看层级与分卷**（`SelfRepairPlanner.cs:56` 的 `GroupBy(entry => entry.Title, StringComparer.Ordinal)`） | **不同卷里的合法重名章节会被判为副本**，而且 `Recommended: true`（默认勾选）。TreeOnly 模式下会让成品少一章；改原文还要再勾一次「并从原文删除这一份」，所以原文暂时安全。**未实测复现，但代码路径清楚** —— 这是本表里唯一可能丢内容的一条 |
| 8 | `KindleGenLocator.Resolve` 的候选路径里硬编码了开发机个人路径（`LegacyMobiWriter.cs:306`） | 只是候选之一，对别的机器无影响；但私人路径不该在仓库里 |
| 9 | `RepairPlanCompiler.Compile` 的 `sourceProblems` 列表创建后从未写入（`:134`） | 死变量：让"源码补丁问题"看起来有来源，实则永远为空 |
| 10 | `RepairEffectCompiler.Indeterminate(string _)` 丢弃入参（`:167`） | 调用点认真写的 5 处原因文案（`:57,70,105,119,169`）永远到不了用户 |

---

## 14. 文档索引（哪份讲哪块）

| 主题 | 文档 |
|---|---|
| **功能与流程图全景**（最完整的一份，基于 v1.57.4） | `docs/2026-09-16_功能逻辑与流程图全景.md` |
| 产品目标与约束、模块地图 | `docs/2026-09-15_功能逻辑与双模式设计.md`（注意 §11.3 的歧义） |
| 重构设计与冻结版 | `docs/2026-09-16_修复流程重构设计方案-v3.3.md` |
| 重构各阶段实施记录 | `docs/2026-09-17_Phase0…Phase7-*.md` |
| 本轮收尾接线（恢复、快照、迁移、标签） | `docs/2026-09-17_收尾接线实施记录.md` |
| 样书缺陷分析 | `docs/2026-09-16_六卷缺陷样书-两模式推演与缺陷报告.md` |
| 发布说明 | `docs/RELEASE_v*.md`、`docs/RELEASING.md` |
| 交接（**已过时**） | `docs/NEXT_TASK.md`、`docs/HANDOFF*.md` |
