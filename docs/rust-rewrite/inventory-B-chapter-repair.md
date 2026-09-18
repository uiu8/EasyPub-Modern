# 盘点 B · 章节编辑与修复相关窗口/面板的前端交互入口

## 覆盖范围

严格限于以下 17 个文件（路径相对 `src\EasyPub.Desktop\`）：

| 缩写 | 文件 | 说明 |
|---|---|---|
| `X` | `ChapterEditorWindow.xaml` | 章节工作台界面（321 行，46 个 `Click="..."`） |
| `W` | `ChapterEditorWindow.xaml.cs` | 工作台宿主 partial + `ChapterTreeNode`（1017 行） |
| `AC` | `ChapterEditorActions.cs` | 重复标题清理、标题编辑、外部编辑器（170 行） |
| `WB` | `ChapterWorkbench.cs` | 规则、搜索、右键菜单、整理工具、重新识别/迁移（640 行） |
| `CP` | `ChapterCatalogPanel.cs` | 目录辅助修复对话框（214 行） |
| `RP` | `ChapterReviewPanel.cs` | 待核对卡、建议导航、断点、目录获取（788 行） |
| `AR` | `ChapterAutoRepairPanel.cs` | 一键目录修复入口 + 修复落地（234 行） |
| `CC` | `ChapterContentComparison.cs` | 重复正文对比与删除（184 行） |
| `SR` | `ChapterStructureRecoveryPanel.cs` | 结构整理（66 行） |
| `RR` | `RepairReviewWindow.cs` | 修复核对窗（纯代码构建，1154 行） |
| `BC` | `BuiltinCleanupWindow.cs` | 内置清理规则窗（135 行） |
| `NR` | `NumericHeadingRulesWindow.cs` | 数字章号/标题纠错规则窗（129 行） |
| `CK` | `AutomaticCheckWindow.cs` | 自动检查设置窗（83 行） |
| `MS` | `ChapterMultiSelection.cs` | 多选与批量动作（155 行，无 UI 控件） |
| `DC` | `ChapterDisplayCollection.cs` | 显示投影集合（23 行，无 UI 控件） |
| `IA` | `ChapterIssueAction.cs` | 核对卡按钮文案映射（30 行，无 UI 控件） |
| `BL` | `ChapterBreakpointLabel.cs` | 章节间断点标签（32 行，无 UI 控件） |

Core 侧统一写作 `Core:<文件名>:<行>`，路径相对 `src\EasyPub.Core\`。

**入口总数：161**（`X` 声明式绑定 65 + 主工作台代码动态挂接 59 + `RR` 14 + `BC` 6 + `NR` 11 + `CK` 6）。
其中 `MS` / `DC` / `IA` / `BL` 四个辅助类**没有任何交互入口**（纯函数/集合），已在末尾单列。

### 副作用判定依据（读代码核实，不是照抄背景）

四种改动量级在代码里的真实落点：

1. **只报告**：`Core:ChapterReviewAnalyzer.cs:36`、`Core:ChapterBreakpoints.cs:45`、`Core:MissingChapterHeadings.cs:45`、`Core:ChapterStructureRecovery.cs:14`、`Core:ChapterContentDuplicates.cs:52`、`Core:DuplicateChapterCleaner.cs:9`、`Core:ChapterDiagnostics.cs:123` —— 全部纯读，已 grep 确认为零 `File.*`/`Save*` 写入。
2. **改内存树**：`W:Mutate()` (`W:547`) 及其调用者，操作 `Roots`/`ChapterTreeNode`；`_document` 只被 `ApplyRebuiltDocument` (`WB:166`)、`AdoptMigratedState` (`WB:350`)、`ApplyRepairEntries` (`AR:20`) 替换。
3. **写项目快照**：**本批 17 个文件里没有任何一处直接写项目快照**。唯一提交口是 `W:Save_Click` (`W:331`) 设 `DialogResult = true` + `ResultPlan`，由外层 `MainWindow.xaml.cs:1962-1982`（`book.SetChapterTree` + `SaveRecoveryIfChangedAsync`）落盘。
4. **⚠️写TXT**：全部 17 个文件里只有**一条**调用链能改用户原始 TXT：
   `AR:RunRepairReviewAsync` (`AR:103`) → `ChapterRepairApplier.ApplyAsync(..., EditSource, ...)` (`AR:213` → `Core:ChapterRepairApplier.cs:127` → `:250`) → `SourceEditTransaction.ExecuteAsync` (`Core:SourceEditTransaction.cs:111`) → `ReplaceAtomically()`（`Core:SourceEditTransaction.cs:166` 调用，实现 `:227`，内部 `File.Replace` `:231` / `File.Move` `:249-257`）。
   全仓库 grep `ReplaceAtomically|File.Replace|File.Move` 后确认：**只有 `SourceEditTransaction` 会覆盖「作为章节树依据的那份原始 TXT」**，其余命中都是设置文件（`AppSettingsStore.cs:177`、`BookSource.cs:240`…）、缓存（`ReferenceCatalogInput.cs:53`、`CatalogCache`…）、备份（`SourceBackupStore.cs:81`、`SourceBackupInventory.cs:214`）、事务日志（`RepairJournalStore.cs:79`、`SourceFullStateStore.cs:50`）和转换产物（`AtomicOutputWriter.cs:21`、`LegacyEpubWriter.cs:95`）。

`RepairLandingMode`（`Core:RepairDecision.cs:80`）只决定走不走上面这条链：`TreeOnly` 在 `Core:ChapterRepairApplier.cs:275-278` 提前返回，不创建事务、不写文件；`EditSource` 才落到 `:327` 的 `ExecuteAsync`。
`SourceEffectPolicy`（`Core:RepairDecision.cs:67`）与 `SourceEffectPolicies.Decide` (`:127`) 决定每个动作在 `EditSource` 下是否必须改 TXT；`ExplicitOptIn`（`:76`、`For` `:97`）只对 `RemoveDuplicate` 生效，`NeedsExplicitOptIn` 在 `:153`，由 `RR:529-530` 决定是否给该行画出「并从原文删除这一份」开关。
三个指纹（`BaseSourceSha256` / `BaseTreeFingerprint` / `RecognitionFingerprint`）由 `ChapterRepairApplier.ProposalFor` (`Core:ChapterRepairApplier.cs:145`) 经 `RepairBaseVersionFactory.Capture` (`:150`) 绑定，`RepairPlanValidator.Validate` (`:192`) 在预览与执行两侧复验；`SourceEditTransaction.ExecuteAsync:118-120` 还会再比对一次磁盘 SHA-256。

---

## 一、`ChapterEditorWindow.xaml` 声明式绑定（65 条）

| # | 界面元素（x:Name 或文本） | 位置（文件:行） | 事件 | 处理的 C# 方法（文件:行） | 该方法直接调用的方法/类（带文件:行） | 最终落到 Core 的公开 API（带文件:行） | 副作用 |
|---|---|---|---|---|---|---|---|
| E1 | `Window`（标题「章节工作台」） | `X:7` | `PreviewKeyDown` | `W:Window_PreviewKeyDown` `W:512` | `W:Undo` `W:529` / `W:Redo` `W:538` / `W:Close` `W:360`；`ChapterActionsPopup.IsOpen=false` `W:515` | — | 仅UI + 改内存树（撤销/重做路径） |
| E2 | `TreeViewItem` 样式 `EventSetter` | `X:187` | `Collapsed` | `WB:ChapterNode_Collapsed` `WB:511` | `WB:ClearHiddenSelection` `WB:503` → `MS:SetOperationSelection` `MS:16` | — | 仅UI（改内存里的选择集合） |
| E3 | 同上行 `IsExpanded="{Binding IsExpanded, Mode=TwoWay}"` | `X:186` | 属性双向绑定 | `W:ChapterTreeNode.IsExpanded` `W:929` | `W:SetField` `W:1008` → `Changed` 事件 → `W:Node_Changed` `W:583` | — | 仅UI + 触发 E2 |
| E4 | 章节行 `CheckBox IsChecked="{Binding IncludeInToc}"` | `X:90` | 数据绑定（点击即写模型） | `W:ChapterTreeNode.IncludeInToc` `W:985` | `W:SetField` `W:1008` → `Changed` → `W:Node_Changed` `W:583`（压撤销栈） | — | 改内存树 |
| E5 | `AutoRepairButton`「预览目录修复」 | `X:115` | `Click` | `AR:AutoRepair_Click` `AR:42` | `AR:RunRepairReviewAsync()` `AR:45` → `:103`（`ChapterAutoRepair.RepairAsync` `AR:143`、`new RepairReviewWindow` `AR:176`、`ChapterRepairApplier.Preview/DecisionsFor` `AR:181-183`、`ApplyRepairEntries` `AR:205`、`applier.ApplyAsync` `AR:213`） | `Core:ChapterAutoRepair.cs:176` `RepairAsync`；`Core:ChapterRepairApplier.cs:110/127/182/250`；`Core:RepairIntegrity.cs:294` `SaveRemovedAsync`；`Core:SourceBackupStore.cs:129` `EnsureSnapshot`；`Core:ReferenceCatalogInput.cs:17` `SaveCatalog`；`Core:ChapterTree.cs:76` `WithEntries` | 弹窗 + 改内存树 + 写备份 + 写产物（目录缓存）+ **⚠️写TXT（仅当核对窗选「同时改原文」并点「应用所选修改」）** |
| E6 | `CatalogAssistButton`「手动核对目录…」 | `X:121` | `Click` | `CP:CatalogAssist_Click` `CP:13` | `ReferenceCatalogClient.Sources`/`BookSourceStore.CreateDefault().LoadAsync` `CP:20`、`MetadataMappingStore.CreateDefault().LoadAsync` `CP:21`、`ReferenceCatalogInput.Load` `CP:118`、`ThemedWindow` `CP:25` | `Core:BookSource.cs:160` `BookSourceStore.CreateDefault` / `:171` `LoadAsync`；`Core:MetadataMappingStore.cs:23/34`；`Core:MetadataMapping.cs:59` `Match`；`Core:ReferenceCatalogInput.cs:36` `Load` | 弹窗 + 写产物（读/写目录偏好文件） |
| E7 | `RecognitionSettingsButton`「识别设置…」 | `X:122` | `Click` | `WB:RecognitionSettings_Click` `WB:135` | `WB:CaptureRules` `WB:101`、`WB:ThemedWindow` `WB:382`、`WB:ValidateRules` `WB:126`、`WB:ApplyRules` `WB:107`、`WB:UpdateSaveState` `WB:71` | `Core:NumericHeadingRule.cs:14` `Compile`；`Core:HeadingTypoRules.cs:6` `Parse`（均为校验，不落盘） | 弹窗 + 仅UI（规则只留在工作台内存，随 E51 提交） |
| E8 | `RecoverStructureButton`「结构整理…」`Visibility="Collapsed"` | `X:123` | `Click` | `SR:RecoverStructure_Click` `SR:24` | — | — | **死 UI（控件永不显示，全仓库无一处引用该 x:Name）** |
| E9 | `UndoButton`「撤销」 | `X:124` | `Click` | `W:Undo_Click` `W:509` | `W:Undo` `W:529` → `W:RestoreSnapshot` `W:612` | — | 改内存树 |
| E10 | `RedoButton`「重做」 | `X:125` | `Click` | `W:Redo_Click` `W:510` | `W:Redo` `W:538` → `W:RestoreSnapshot` `W:612` | — | 改内存树 |
| E11 | `WorkbenchMoreButton`「整理工具 ▾」 | `X:126` | `Click` | `WB:WorkbenchMore_Click` `WB:417` | `WB:Add` `WB:421`、`WB:Category` `WB:420`（仅建菜单，动作见 E83-E95） | — | 仅UI |
| E12 | 横幅按钮「预览结构整理…」 | `X:133` | `Click` | `SR:RecoverStructure_Click` `SR:24` | `ChapterStructureRecovery.Analyze` `SR:29`、`SR:ThemedWindow` `SR:32`、`W:Mutate` `SR:54`、`WB:ApplySearchFilter` `SR:62`、`WB:AllChapters_Click` `SR:62` | `Core:ChapterStructureRecovery.cs:14` `Analyze` | 弹窗 + 改内存树 |
| E13 | 横幅按钮「查看依据 / 涉及位置」 | `X:134` | `Click` | `SR:StructureEvidence_Click` `SR:9` | `W:NavigateToSuggestion` `SR:12` | — | 仅UI（重算 `ChapterReviewAnalyzer.Analyze` 并导航） |
| E14 | 横幅按钮「本次暂不提醒」 | `X:135` | `Click` | `SR:DismissStructure_Click` `SR:15` | `RP:ReviewKey` `SR:19`、`W:RefreshSuggestions` `SR:20`、`RP:SetReviewResult` `SR:21` | `Core:ChapterReviewAnalyzer.cs:36` `Analyze` | 仅UI（内存 `_confirmedGroups`，随 E51 提交） |
| E15 | `AllChaptersRadio`「全部章节」 | `X:152` | `Click` | `WB:AllChapters_Click` `WB:481` | `RP:UpdateReviewVisibility` `WB:482`、`W:UpdateActionButtons` `WB:482` | — | 仅UI（可能清隐藏选择） |
| E16 | `ReviewOnlyCheck`「待核对」 | `X:155` | `Click` | `RP:ReviewFilter_Click` `RP:168` | `RP:UpdateReviewVisibility` `RP:172`、`W:UpdateActionButtons` `RP:173` | — | 仅UI |
| E17 | `IssueCategoryCombo`（全部问题/漏识别/误识别/重复/编号与层级/其他提醒） | `X:159` | `SelectionChanged` | `RP:IssueCategory_Changed` `RP:159` | `W:RefreshSuggestions` `RP:163`、`RP:UpdateReviewVisibility` `RP:164` | `Core:ChapterReviewAnalyzer.cs:36` `Analyze` | 仅UI（重跑分析） |
| E18 | `FetchCatalogButton`「获取参考目录（联网）」 | `X:165` | `Click` | `RP:FetchCatalog_Click` `RP:311` | `RP:SavedReference` `RP:314`、`CP:CatalogAssist_Click` `RP:314/332`、`ReferenceCatalogInput.FindLocalBookUrl` `RP:324`、`ChapterAutoRepair.LoadPreferredSourcesAsync` `RP:326`、`ReferenceCatalogClient.DiscoverAsync` `RP:327`、`ReferenceCatalogInput.SaveCatalog` `RP:335`、`RP:ApplyBreakpoints` `RP:340` | `Core:ChapterAutoRepair.cs:162` `LoadPreferredSourcesAsync`；`Core:ReferenceCatalog.cs:496` `DiscoverAsync`/`:39` `ReferenceCatalogClient`；`Core:ReferenceCatalogInput.cs:17` `SaveCatalog`；`Core:ChapterBreakpoints.cs:45` `Between` | 弹窗 + 写产物（目录缓存）+ 仅UI（树上断点标记）+ 可能打开 E6 对话框 |
| E19 | `NumberingNotesButton`「编号差异 N 处…」 | `X:167` | `Click` | `RP:NumberingNotes_Click` `RP:419` | `WB:ThemedWindow` `RP:421`、`CC:LocateDuplicateChapter` `RP:434` | — | 弹窗（列表来自 E138 时算好的 `ChapterBreakpoints.Between`） |
| E20 | `ChapterSearchText` | `X:171` | `TextChanged` | `WB:ChapterSearch_Changed` `WB:483` | `_searchTimer.Stop/Start` `WB:483` → `WB:ApplySearchFilter` `WB:484`（经 E71） | — | 仅UI |
| E21 | `ChapterSearchText`（回车，支持 `行:51397`） | `X:171` | `KeyDown` | `WB:ChapterSearch_KeyDown` `WB:492` | `WB:ApplySearchFilter` `WB:495`、`W:NavigateToSourceLine` `WB:497`、`MS:SelectChapterForOperation` `WB:498` | — | 仅UI |
| E22 | 搜索行「清除」 | `X:172` | `Click` | `WB:ClearSearch_Click` `WB:501` | `WB:ApplySearchFilter` `WB:501` | — | 仅UI |
| E23 | 「取消选择」 | `X:176` | `Click` | `WB:ClearSelection_Click` `WB:502` | `MS:SetOperationSelection` `WB:502`、`W:RefreshSelectedLines` `WB:502`、`W:UpdateActionButtons` `WB:502` | — | 仅UI |
| E24 | `ChapterTree` | `X:182` | `SelectedItemChanged` | `W:ChapterTree_SelectedItemChanged` `W:145` | `MS:SetOperationSelection` `W:149`、`W:RefreshSelectedLines` `W:150`、`W:UpdateActionButtons` `W:151` | — | 仅UI（读原文行，不写） |
| E25 | `ChapterTree` | `X:183` | `PreviewMouseLeftButtonDown` | `W:ChapterTree_PreviewMouseLeftButtonDown` `W:362` | `MS:SelectChapterForOperation` `W:369`、`W:OperationSelection` `W:371` | — | 仅UI（选择；多选时同时取消拖放源 `W:371`） |
| E26 | `ChapterTree` | `X:183` | `PreviewMouseRightButtonUp` | `WB:ChapterTree_RightClick` `WB:512` | `MS:SelectChapterForOperation` `WB:516`、`RP:ReviewMore_Click` `WB:517` | — | 弹窗（右键菜单，动作见 E102-E104） |
| E27 | `ChapterTree` | `X:184` | `PreviewKeyDown` | `WB:ChapterTree_KeyDown` `WB:519` | `MS:SelectChapterForOperation` `WB:526` | — | 仅UI（↑/↓ 选择） |
| E28 | `ChapterTree`（拖放源） | `X:184` | `PreviewMouseMove` | `W:ChapterTree_PreviewMouseMove` `W:374` | `DragDrop.DoDragDrop` `W:380` | — | 仅UI（启动拖放） |
| E29 | `ChapterTree`（拖放目标，`AllowDrop="True"`） | `X:184` | `Drop` | `W:ChapterTree_Drop` `W:382` | `W:MaxRelativeDepth` `W:393`、`W:Mutate` `W:398`、`W:SetLevelRecursive` `W:405/411` | — | 改内存树 |
| E30 | 章节标题 `TextBlock`（双击） | `X:96` | `MouseLeftButtonDown` | `AC:Title_MouseLeftButtonDown` `AC:101` | `AC:EditTitle` `AC:105` | — | 弹窗 + 改内存树（确定后 `AC:135`） |
| E31 | `GroupLocationsCombo` | `X:202` | `SelectionChanged` | `RP:GroupLocation_Changed` `RP:760` | `W:NavigateToSourceLine` `RP:765`、`RP:UpdateReviewCard` `RP:767` | — | 仅UI |
| E32 | `SelectReviewGroupButton`「选择本组全部章节」 | `X:203` | `Click` | `RP:SelectReviewGroup_Click` `RP:770` | `MS:SetOperationSelection` `RP:774`、`W:UpdateActionButtons` `RP:776` | — | 仅UI |
| E33 | `OnlyCurrentIssueCheck`「仅处理当前处」 | `X:203` | `Click` | `RP:OnlyCurrentIssue_Click` `RP:779` | `RP:UpdateReviewCard` `RP:779` | — | 仅UI |
| E34 | `ReviewActionButton`（文案随问题类型变化，见 `RP:485-647`） | `X:208` | `Click` | `RP:ReviewAction_Click` `RP:649` | 分派：`CP:CatalogAssist_Click` `RP:653`、`W:NavigateToSourceLine` `RP:657`、`CC:CompareDuplicateContents` `RP:660`、`SR:RecoverStructure_Click` `RP:661`、`WB:RepairMissingHeadings` `RP:662/671/674`、`RP:FetchCatalog_Click` `RP:676`、`WB:RecognizeSuggestedNumericChaptersAsync` `RP:681`、`W:Merge_Click` `RP:688`、`AC:CleanDuplicateTitles` `RP:689`、`RP:ReviewMore_Click` `RP:690`、`W:Split_Click` `RP:691`、`AC:CorrectNumber_Click` `RP:692`、`AC:EditTitle` `RP:693` | `Core:MissingChapterHeadings.cs:45` `Find`；`Core:ChapterAutoRepair.cs:176`；`Core:ChapterReviewAnalyzer.cs:14` `FindNumericChapters`；其余见各被派发入口 | 弹窗 / 改内存树 / 写备份（走 `CleanDuplicateTitles` 时）/ **可间接进入 ⚠️写TXT 链（`chapter_unnumbered` → E114）** |
| E35 | `RecognizeNumericKeepButton`「仅识别，保留原标题」 | `X:209` | `Click` | `RP:RecognizeNumericKeep_Click` `RP:699` | `WB:RecognizeSuggestedNumericChaptersAsync(false)` `RP:702` | `Core:ChapterTree.cs:79` `LoadAsync`；`Core:ChapterReviewAnalyzer.cs:14` | 改内存树（重识别，不写 TXT） |
| E36 | `ReviewMoreButton`「其他处理 ▾」 | `X:210` | `Click` | `RP:ReviewMore_Click` `RP:738` | `W:UpdateActionButtons` `RP:740`、镜像 `CurrentActionsPanel` 子按钮 `RP:742-749`、`WB:RestoreContainerToBody` `RP:751/753` | — | 弹窗（动态右键菜单） |
| E37 | `ConfirmNormalButton`「确认正常」 | `X:211` | `Click` | `RP:ConfirmNormal_Click` `RP:726` | `RP:ReviewKey` `RP:729`、`W:RefreshSuggestions` `RP:730`、`RP:SetReviewResult` `RP:731`、`RP:UpdateReviewVisibility` `RP:735` | `Core:ChapterReviewAnalyzer.cs:36` | 仅UI（内存 `_confirmedGroups`，随 E51 提交） |
| E38 | `UndoResultButton`「撤销本次处理」 | `X:214` | `Click` | `W:Undo_Click` `W:509`（与 E9 同一处理函数） | `W:Undo` `W:529` | — | 改内存树 |
| E39 | `RefreshSourceButton`「刷新原文」/ 原文变化时改为「原文已变化 · 刷新」（`WB:78`） | `X:222` | `Click` | `W:RebuildFromRules_Click` `W:418` → `W:RebuildOrMigrateAsync(sender)` `W:428` | `WB:MigrateToExternalEditAsync` `W:437` → `WB:291`（`SourceVersionTransition.AfterExternalEditAsync` `WB:317`、`WB:AdoptMigratedState` `WB:325/350`）；否则 `ChapterTreeDocument.LoadAsync` `W:448`、`WB:ApplyRebuiltDocument` `W:449` | `Core:SourceVersionTransition.cs:339` `AfterExternalEditAsync`；`Core:ChapterTree.cs:79` `LoadAsync`/`:103` `Load` | 改内存树（`_sourceChanged` 时是**版本迁移**而非重识别）；不写 TXT |
| E40 | `EditSourceButton`「编辑副本…」 | `X:223` | `Click` | `WB:EditSource_Click` `WB:595` → `AC:EditOriginal_Click` `AC:140` | `SourceBackupStore.CreateDefault().CreateWorkingCopy` `AC:149`、`WorkingCopyCreated` 回调 `AC:150`、`Clipboard.SetText` `AC:151`、`AC:CreateEditorStartInfo` `AC:159`、`Process.Start` `AC:153` | `Core:SourceBackupStore.cs:36` `CreateDefault` / `:20` `CreateWorkingCopy` | 写备份（在备份目录建独立副本）+ 弹窗/外部进程 + 剪贴板；**不碰原稿 TXT** |
| E41 | `SourceLinesList` | `X:230` | `SelectionChanged` | `W:SourceLinesList_SelectionChanged` `W:706` | `W:CanSplitSelectedLine` `W:708`、`RP:UpdateReviewCard` `W:710` | — | 仅UI |
| E42 | `WrapSourceCheck`「换行」 | `X:243` | `Click` | `WB:WrapSource_Click` `WB:633` | `ScrollViewer.SetHorizontalScrollBarVisibility` `WB:633` | — | 仅UI |
| E43 | 「复制选中行」 | `X:244` | `Click` | `WB:CopySource_Click` `WB:631` | `Clipboard.SetText` `WB:632` | — | 仅UI（剪贴板） |
| E44 | `ExpandSourceButton`「展开本章全文」 | `X:245` | `Click` | `WB:ExpandSource_Click` `WB:634` | `W:RefreshSelectedLines` `WB:635` | — | 仅UI |
| E45 | `SplitButton`「从选中行建立章节」 | `X:246` | `Click` | `W:Split_Click` `W:266` | `W:CanSplitSelectedLine` `W:268`、`W:Mutate` `W:288`、`W:NotifyLineCount` `W:291` | — | 改内存树 |
| E46 | `PreviousSuggestionButton`「← 上一组」 | `X:250` | `Click` | `W:PreviousSuggestion_Click` `W:807` | 改 `ChapterSuggestionsCombo.SelectedIndex` → E47 | — | 仅UI |
| E47 | `ChapterSuggestionsCombo` | `X:253` | `SelectionChanged` | `W:ChapterSuggestions_SelectionChanged` `W:792` | `W:NavigateToSourceLine` `W:801`、`W:UpdateSuggestionButtons` `W:804` | — | 仅UI |
| E48 | `NextSuggestionButton`「下一组 →」 | `X:257` | `Click` | `W:NextSuggestion_Click` `W:810` | 改 `ChapterSuggestionsCombo.SelectedIndex` → E47 | — | 仅UI |
| E49 | `LoadMoreIssuesButton`「已加载 N / M 组 · 继续加载」 | `X:259` | `Click` | `RP:LoadMoreIssues_Click` `RP:780` | `_reviewLimit += 200` `RP:782`、`W:RefreshSuggestions` `RP:783` | `Core:ChapterReviewAnalyzer.cs:36` `Analyze` | 仅UI |
| E50 | 底部「取消」 | `X:265` | `Click` | `W:Close_Click` `W:360` | `Close()` → `WB:Workbench_Closing` `WB:60` → `WB:ConfirmWorkbench` `WB:55` | — | 仅UI + 弹窗（有未提交修改时确认放弃） |
| E51 | `SaveChapterTreeButton`「应用并返回」 | `X:265` | `Click` | `W:Save_Click` `W:331` | `WB:ValidateRules` `W:335`、`WB:RecognitionRulesChanged` `W:336`、`W:ConfirmWorkbench` `W:336`、SHA-256 复验 `W:337-339`、`ChapterTreeDocument.CreatePlan` `W:340`、`W:ReadHierarchyOptions` `W:341`、`WB:UsesGlobalNumeric` `W:344`、`RP:SavedReference` `W:348` | `Core:ChapterTree.cs:227` `CreatePlan`；`Core:SourceEditTransaction.State.cs:234` `HashOf`（只读校验）；`Core:ChapterTreePlan` `Core:ChapterTree.cs:37` | 写项目快照（`DialogResult=true` 后由外层 `MainWindow.xaml.cs:1962-1982` 落盘）+ 仅UI；**不写 TXT** |
| E52 | `MergeButton`「还原为上一章正文 / 还原 N 章为正文」 | `X:274` | `Click` | `W:Merge_Click` `W:235` | `MS:TryBatchChapterAction(true)` `W:237`、`W:Mutate` `W:253` | — | 改内存树（Popup 本身不显示，见「死 UI」；处理函数经 E102 镜像菜单可达） |
| E53 | `EditTitleButton`「编辑成品标题…」 | `X:276` | `Click` | `WB:EditCurrentTitle_Click` `WB:528` → `AC:EditTitle` `AC:113` | `W:Mutate` `AC:135` | — | 弹窗 + 改内存树（同上，经 E102 可达） |
| E54 | `CorrectNumberButton`「疑似错号 · 修改为：X」 | `X:277` | `Click` | `AC:CorrectNumber_Click` `AC:91` | `AC:SuggestedTitle` `AC:94`、`W:Mutate` `AC:95`；可见性由 `AC:UpdateNumberSuggestion` `AC:84` 控制 | `Core:ChapterDiagnostics.cs:123` `SuggestCorrectedTitle` | 改内存树（同上，经 E102 可达） |
| E55 | `DemoteButton`「设为上一章的子章节」 | `X:279` | `Click` | `W:Demote_Click` `W:198` | `MS:TryBatchChapterAction(false)` `W:200`、`W:MaxRelativeDepth` `W:217`、`W:Mutate` `W:222` | — | 改内存树（同上，经 E102 可达） |
| E56 | `PromoteButton`「移出父章节」 | `X:280` | `Click` | `W:Promote_Click` `W:168` | `WB:ConfirmWorkbench` `W:176`、`W:Mutate` `W:177` | — | 弹窗 + 改内存树（同上，经 E102 可达） |
| E57 | `MoveUpButton`「上移」 | `X:282` | `Click` | `W:MoveUp_Click` `W:154` | `W:MoveSelected(-1)` `W:156` → `W:Mutate` `W:163` | — | 改内存树（同上，经 E102 可达） |
| E58 | `MoveDownButton`「下移」 | `X:282` | `Click` | `W:MoveDown_Click` `W:155` | `W:MoveSelected(1)` `W:156` | — | 改内存树（同上，经 E102 可达） |
| E59 | 规则面板「恢复普通默认」 | `X:294` | `Click` | `WB:ResetChapterPattern_Click` `WB:594` | 置空 `ChapterPatternText.Text` | — | 仅UI |
| E60 | 规则面板「数字表达式与方案…」 | `X:300` | `Click` | `W:NumericRules_Click` `W:491` | `W:ReadHierarchyOptions` `W:495`、`new NumericHeadingRulesWindow` `W:496`、`W:UpdateRuleGuidance` `W:504` | `Core:NumericHeadingRule.cs:14/22`（窗口内校验） | 弹窗 + 仅UI（结果写回工作台内存） |
| E61 | 规则面板「标题纠错规则与方案…」 | `X:302` | `Click` | `WB:HeadingCorrections_Click` `WB:529` | `W:ReadHierarchyOptions` `WB:531`、`new NumericHeadingRulesWindow(correctionsOnly:true)` `WB:532`、`WB:UpdateSaveState` `WB:538` | `Core:HeadingTypoRules.cs:6` | 弹窗 + 仅UI |
| E62 | 规则面板「恢复层级默认」 | `X:309` | `Click` | `W:ResetRules_Click` `W:455` | 写 `Level1/2/3PatternText` | — | 仅UI |
| E63 | 规则面板 `RebuildRulesButton`「按当前规则重新识别…」 | `X:317` | `Click` | `W:RebuildFromRules_Click` `W:418` → `W:RebuildOrMigrateAsync(sender)` `W:428` | `W:ConfirmWorkbench` `W:441`、`ChapterTreeDocument.LoadAsync` `W:448`、`WB:ApplyRebuiltDocument` `W:449` | `Core:ChapterTree.cs:79` `LoadAsync` | 弹窗 + 改内存树（替换手工树；**与 E39 同一处理函数但走不同分支**） |
| E64 | `RecognitionRulesPanel`（含 `ChapterPatternText`、`Level1/2/3PatternText`、`NumericMinimumLinesText`） | 挂接于 `WB:30` | `TextBox.TextChangedEvent`（`AddHandler`，面板级） | `WB:UpdateRuleGuidance` `WB:84` | `WB:CaptureRules` `WB:101`、`WB:RecognitionRulesChanged` `WB:124`、`WB:UpdateSaveState` `WB:71` | — | 仅UI（提示文案） |
| E65 | 同上：`NumericHeadingsCheck`、`HierarchyEnabledCheck`、`IncludeHtmlTocPageCheck`、`IncludeChapterTopNavigationCheck` | 挂接于 `WB:31-32` | `ToggleButton.CheckedEvent` + `UncheckedEvent`（`AddHandler`，面板级） | `WB:UpdateRuleGuidance` `WB:84` | 同上 | — | 仅UI |

---

## 二、主工作台代码动态挂接与代码构建的对话框（59 条，E66–E124）

| # | 界面元素（代码创建时的变量名/文本） | 位置（文件:行） | 事件 | 处理的 C# 方法（文件:行） | 该方法直接调用的方法/类（带文件:行） | 最终落到 Core 的公开 API（带文件:行） | 副作用 |
|---|---|---|---|---|---|---|---|
| E66 | `ChapterEditorWindow` 自身 `Loaded` | `WB:35` | `Loaded` | 内联 lambda `WB:35-43` | `WB:SettingsFingerprint` `WB:37`、`WB:UpdateSaveState` `WB:39`、`RP:RefreshCatalogState` `WB:40`、`W:NavigateToSuggestion` `WB:42` | — | 仅UI |
| E67 | `ChapterEditorWindow` `Activated` | `WB:44` | `Activated` | `WB:CheckSourceVersionAsync` `WB:242` | `SHA256.HashData(File.ReadAllBytesAsync)` `WB:249`、`WB:ShowReviewFeedback` `WB:252`、`WB:UpdateSaveState` `WB:255` | `Core:SourceEditTransaction.State.cs:234`（同族哈希，只读） | 仅UI（只校验磁盘哈希，不改文件） |
| E68 | `ChapterEditorWindow` `Closing` | `WB:45` | `Closing` | `WB:Workbench_Closing` `WB:60` | `WB:HasUnsavedChanges` `WB:65`、`WB:ConfirmWorkbench` `WB:62` | — | 弹窗 |
| E69 | `ChapterEditorWindow` `Closed`（两处挂接） | `WB:46`、`W:49` | `Closed` | `_searchTimer.Stop()` `WB:46`；`W:49` 置 `_editorClosed`、取消 `_breakpointRequest`/`_catalogRequest` | `RP:ApplyBreakpoints` 取消令牌 `W:49` | — | 仅UI（取消后台任务） |
| E70 | `CurrentActionsPanel`（Popup 内容面板，面板级） | `WB:47` | `Button.ClickEvent`（`AddHandler`） | 内联 lambda `WB:47-52` | `ChapterActionsPopup.IsOpen=false` `WB:49`、`RP:UpdateReviewVisibility` `WB:50`、`W:UpdateActionButtons` `WB:51` | — | 仅UI |
| E71 | `_searchTimer`（180 ms 防抖） | `WB:33-34` | `Tick` | `WB:ApplySearchFilter` `WB:484` | `RP:FilteredReviewIssues` `WB:488`、`RP:UpdateReviewVisibility` `WB:490`、`W:UpdateSuggestionButtons` `WB:490` | `Core:ChapterReviewAnalyzer.cs:36` | 仅UI |
| E72 | 识别设置对话框「保留设置」（`apply`） | `WB:151-152` | `Click` | 内联 lambda `WB:152-156` | `WB:ValidateRules` `WB:154`、`InkDialog.Show` `WB:155` | `Core:NumericHeadingRule.cs:14`、`Core:HeadingTypoRules.cs:6` | 弹窗 + 仅UI（`DialogResult=true`） |
| E73 | 识别设置对话框「取消」（`cancel`，`IsCancel`） | `WB:150` | `Click`（WPF 取消语义） | `WB:160` 分支 `ApplyRules(before, includeShared:true)` | `WB:ApplyRules` `WB:107` | — | 仅UI（回滚规则内存） |
| E74 | `PreviewChanges` 确认窗「确认处理」（`apply`） | `WB:410-411` | `Click` | 内联 lambda `WB:411` | — | — | 仅UI（`DialogResult=true`；调用方再动树） |
| E75 | 清理重复标题窗 `min`（正文行数下限） | `AC:24`、`AC:55` | `TextChanged` | `AC:Preview` `AC:43` | `DuplicateChapterCleaner.Clean` `AC:49`、`apply.IsEnabled` `AC:53` | `Core:DuplicateChapterCleaner.cs:9` `Clean` | 仅UI（纯预览，不动树、不落盘） |
| E76 | 清理重复标题窗 `max`（正文行数上限） | `AC:25`、`AC:55` | `TextChanged` | `AC:Preview` `AC:43` | 同 E75 | `Core:DuplicateChapterCleaner.cs:9` | 仅UI |
| E77 | 清理重复标题窗 `ignoreNumber`「忽略章号，按标题文字匹配」 | `AC:28`、`AC:56` | `Click` | `AC:Preview` `AC:43` | 同 E75 | `Core:DuplicateChapterCleaner.cs:9` | 仅UI |
| E78 | 清理重复标题窗 `preferFirst`「采用每组前一个标题」 | `AC:29`、`AC:56` | `Click` | `AC:Preview` `AC:43` | 同 E75 | `Core:DuplicateChapterCleaner.cs:9` | 仅UI |
| E79 | 清理重复标题窗「清理」（`apply`） | `AC:35`、`AC:57` | `Click` | 内联 lambda `AC:57`，随后 `AC:58-71` | `RepairIntegrity.Coverage` `AC:61`、`RepairIntegrity.SaveRemovedAsync` `AC:63`、`W:Mutate` `AC:67`、`W:SetReviewResult` `AC:71` | `Core:RepairIntegrity.cs:260` `Coverage` / `:294` `SaveRemovedAsync`；`Core:DuplicateChapterCleaner.cs:9` | 改内存树 + **写备份**（原文快照 + `*.removed.txt` 移除清单）；不写 TXT |
| E80 | 清理重复标题窗「取消」（`cancel`，`IsCancel`） | `AC:34` | 对话框取消 | `AC:58` 早退 | — | — | 仅UI |
| E81 | 编辑标题窗「保存标题」（`save`） | `AC:122`、`AC:128` | `Click` | 内联 lambda `AC:128-132` | 空值/换行校验 `AC:130` | — | 仅UI（`DialogResult=true`） |
| E82 | 编辑标题窗 `Loaded` | `AC:133` | `Loaded` | 内联 lambda `AC:133` | `input.Focus/SelectAll` | — | 仅UI |
| E83 | 「整理工具 ▾」菜单项「重建编号分组与结构（预览）…」 | `WB:424` | `MenuItem.Click` | `SR:RecoverStructure_Click` `SR:24` | `ChapterStructureRecovery.Analyze` `SR:29`、`W:Mutate` `SR:54` | `Core:ChapterStructureRecovery.cs:14` `Analyze` | 弹窗 + 改内存树 |
| E84 | 菜单项「清理全书重复标题…」 | `WB:425` | `MenuItem.Click` | `AC:CleanDuplicateTitles_Click` `AC:14` → `AC:CleanDuplicateTitles(null)` `AC:16` | 同 E79 全流程（`scope` 传 `null` 表示全书） | `Core:DuplicateChapterCleaner.cs:9`；`Core:RepairIntegrity.cs:294` | 弹窗 + 改内存树 + **写备份** |
| E85 | 菜单项「规范化全书数字标题…」 | `WB:426` | `MenuItem.Click` | `W:NormalizeAll_Click` `W:307` | `ChapterTitleNormalizer.TryNormalizeNumericTitle` `W:309`、`WB:PreviewChanges` `W:312`、`W:Mutate` `W:314` | `Core:ChapterTitleNormalizer.cs:9` | 弹窗 + 改内存树 |
| E86 | 菜单项「批量修复跳章区间漏识别标题…」 | `WB:427` | `MenuItem.Click` | `WB:RepairMissingHeadings(null)` `WB:598` | `MissingChapterHeadings.Find` `WB:602`、`WB:ThemedWindow` `WB:611`、`MissingChapterHeadings.Repair` `WB:626`、`W:Mutate` `WB:627` | `Core:MissingChapterHeadings.cs:45` `Find` / `:122` `Repair` | 弹窗 + 改内存树 |
| E87 | 菜单项「全部加入目录」 | `WB:429` | `MenuItem.Click` | `W:SelectAll_Click` `W:319` | `W:Mutate` `W:321`、`W:UpdateSummary` `W:322` | — | 改内存树 |
| E88 | 菜单项「全部不加入目录（保留正文）」 | `WB:430-431` | `MenuItem.Click` | 内联 lambda `WB:430-431` → `WB:ConfirmWorkbench` 后 `W:ClearAll_Click` `W:325` | `W:Mutate` `W:327` | — | 弹窗 + 改内存树 |
| E89 | 菜单项「展开全部章节」 | `WB:433` | `MenuItem.Click` | 内联 lambda `WB:433` | 遍历 `Flatten()` 置 `IsExpanded` | — | 仅UI |
| E90 | 菜单项「折叠全部章节」 | `WB:434-438` | `MenuItem.Click` | 内联 lambda `WB:434` + `WB:ClearHiddenSelection` `WB:437` | `WB:ClearHiddenSelection` `WB:503` | — | 仅UI（收缩后的选择集合） |
| E91 | 菜单项「查看 / 恢复本书已确认的提醒…」 | `WB:439` | `MenuItem.Click` | `WB:ShowConfirmedGroups` `WB:455` | `WB:ThemedWindow` `WB:457`、`RP:RefreshSuggestions` `WB:466` | `Core:ChapterReviewAnalyzer.cs:36` | 弹窗 |
| E92 | 菜单项「恢复本书已确认的提醒」 | `WB:440` | `MenuItem.Click` | `WB:RestoreConfirmedGroups` `WB:448` | `WB:ClearReviewResult` `WB:450`、`W:RefreshSuggestions` `WB:451` | `Core:ChapterReviewAnalyzer.cs:36` | 仅UI（清内存 `_confirmedGroups`） |
| E93 | 菜单项「清除搜索与筛选」 | `WB:441` | `MenuItem.Click` | 内联 lambda `WB:441` → `WB:AllChapters_Click` `WB:481`、`WB:ApplySearchFilter` `WB:484` | 同 E15/E71 | — | 仅UI |
| E94 | 菜单项「复制原始文件路径」 | `WB:443` | `MenuItem.Click` | 内联 lambda `WB:443` | `Clipboard.SetText(_document.SourcePath)` | — | 仅UI（剪贴板） |
| E95 | 菜单项「文本编辑器设置…」 | `WB:444` | `MenuItem.Click` | `WB:ChooseTextEditor` `WB:472` | `OpenFileDialog.ShowDialog` `WB:475`、`WB:ShowReviewFeedback` `WB:478`、`WB:UpdateSaveState` `WB:478` | — | 弹窗 |
| E96 | 已确认提醒窗「恢复选中提醒」（`restore`） | `WB:461-462` | `Click` | 内联 lambda `WB:462-467` | `WB:ClearReviewResult` `WB:466`、`W:RefreshSuggestions` `WB:466` | `Core:ChapterReviewAnalyzer.cs:36` | 仅UI |
| E97 | 已确认提醒窗「关闭」（`IsCancel`） | `WB:468` | 对话框关闭 | `WB:469` `dialog.ShowDialog()` 返回 | — | — | 仅UI |
| E98 | 核对漏识别标题窗「修复勾选项」（`submit`） | `WB:613-622` | `Click` | 内联 lambda `WB:622`，随后 `WB:623-629` | `MissingChapterHeadings.Repair` `WB:626`、`W:Mutate` `WB:627` | `Core:MissingChapterHeadings.cs:122` `Repair` | 弹窗 + 改内存树 |
| E99 | 编号差异窗「定位选中位置」（`locate`） | `RP:424`、`RP:429` | `Click` | 内联 lambda `RP:429-435` | `CC:LocateDuplicateChapter` `RP:434` | — | 仅UI（导航） |
| E100 | 「补建范围」窗「应用」（`save`） | `RP:90`、`RP:100` | `Click` | 内联 lambda `RP:100-115` | `RP:InvalidateBreakpoints` `RP:118`、`RP:ShowReviewFeedback` `RP:119` | `Core:MissingChapterHeadings.cs:45`（参数对象 `MissingChapterHeadingOptions`） | 仅UI（改内存阈值并作废断点缓存） |
| E101 | 「补建范围…」动态按钮（`_headingRepairOptionsButton`，仅在 `chapter_number_gap` 分支创建一次） | `RP:609-616`、`Click` 挂接 `RP:615` | `Click` | `RP:EditHeadingRepairOptions` `RP:45` | `RP:InvalidateBreakpoints` `RP:118` | — | 弹窗 + 仅UI |
| E102 | `ReviewMore` 镜像菜单项（对 `CurrentActionsPanel` 中每个 `Visibility==Visible` 的 `Button` 生成一项，`RP:745`） | `RP:746-747` | `MenuItem.Click` | 内联 lambda `RP:747`：`button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))` | 转派到 E52–E58 各处理函数 | 见 E52–E58 | 与目标按钮相同（弹窗/改内存树） |
| E103 | `ReviewMore` 菜单项「取消章节身份，还原为正文…」（`restore`，插入到索引 0） | `RP:750-751`、`RP:755` | `MenuItem.Click` | `WB:RestoreContainerToBody()` `WB:541` | `WB:ConfirmWorkbench` `WB:552`、`W:Mutate` `WB:556`、`WB:ApplySearchFilter` `WB:587`、`W:SelectRestoredNode` `WB:589` | — | 弹窗 + 改内存树 |
| E104 | `ReviewMore` 菜单项「取消父级，保留为普通章节…」（`keep`） | `RP:752-753`、`RP:754` | `MenuItem.Click` | `WB:RestoreContainerToBody(keepHeading:true)` `WB:541` | 同 E103 | — | 弹窗 + 改内存树 |
| E105 | 目录辅助修复窗「书源…」（`sourceSettings`） | `CP:47-52`（`Click` 在 `CP:52`） | `Click` | `BookSourcePanel.cs:BookSources_Click` `BookSourcePanel.cs:142`（同类另一 partial 文件） | 跨文件；不在本次 17 文件范围内 | `Core:BookSource.cs` 系列 | 弹窗（跨文件） |
| E106 | 目录辅助修复窗「一键自动获取目录」（`auto`） | `CP:45`、`CP:188` | `Click` | `CP:Network(true)` `CP:143` | `ReferenceCatalogInput.FindLocalBookUrl` `CP:154`、`ReferenceCatalogClient.DiscoverAsync` `CP:156`、`ReferenceCatalogInput.Save`（经 E111） | `Core:ReferenceCatalog.cs:496` `DiscoverAsync`；`Core:ReferenceCatalogInput.cs:47` `Save` | 仅UI（结果列表）+ 网络 |
| E107 | 目录辅助修复窗「读取网址」（`fetch`） | `CP:62`、`CP:188` | `Click` | `CP:Network(false)` `CP:143` | `ReferenceCatalogClient.FetchAsync` `CP:157` | `Core:ReferenceCatalog.cs:398` `FetchAsync` | 仅UI + 网络 |
| E108 | 目录辅助修复窗「取消获取」（`cancelNetwork`） | `CP:46`、`CP:189` | `Click` | 内联 lambda `CP:189` | `CancellationTokenSource.Cancel` | — | 仅UI |
| E109 | 目录辅助修复窗「浏览器打开/登录」（`login`） | `CP:62`、`CP:190` | `Click` | 内联 lambda `CP:190-195` | `ReferenceCatalogClient.Supported` `CP:192`、`Process.Start` `CP:193` | `Core:ReferenceCatalog.cs:127` `Supported` | 仅UI + 外部进程（浏览器） |
| E110 | 目录辅助修复窗「导入目录 TXT」（`import`） | `CP:67`、`CP:196` | `Click` | 内联 lambda `CP:196-202` | `OpenFileDialog` `CP:198`、`File.ReadAllText` `CP:200`（2 MB 上限） | — | 弹窗 + 读文件（只读，不写） |
| E111 | 目录辅助修复窗 `sources`（`ComboBox`） | `CP:54`、`CP:169` | `SelectionChanged` | 内联 lambda `CP:169-187` | `UnnumberedHeadings.Key` `CP:173`、`CP:Save` `CP:186`（私有局部函数 `CP:134`）→ `ReferenceCatalogInput.Save` `CP:138`、`RP:InvalidateBreakpoints` `CP:140`、`RP:ScheduleBreakpoints` `CP:141` | `Core:UnnumberedHeadings.cs:12` `Key`；`Core:ReferenceCatalogInput.cs:47` `Save` | 写产物（目录偏好）+ 仅UI |
| E112 | 目录辅助修复窗 `catalogText`（目录文字框） | `CP:65`、`CP:203` | `TextChanged` | 内联 lambda `CP:203` | `ReferenceCatalogInput.ParseText` `CP:203` → 控制 `outline.IsEnabled` | `Core:ReferenceCatalogInput.cs:67` `ParseText` | 仅UI |
| E113 | 目录辅助修复窗「保存本书目录」（`saveSettings`） | `CP:93`、`CP:205` | `Click` | 内联 lambda `CP:205` | `CP:Save` `CP:134` → `ReferenceCatalogInput.Save` `CP:138`、`RP:InvalidateBreakpoints` `CP:140` | `Core:ReferenceCatalogInput.cs:47` `Save` | 写产物（目录偏好文件） |
| E114 | 目录辅助修复窗「预览章节调整…」（`outline`） | `CP:70-77`、`CP:101` | `Click` | 内联 lambda `CP:101-114` | `ReferenceCatalogInput.ParseText` `CP:103`、`CP:Save` `CP:109`、`AR:RunRepairReviewAsync(chosen, …)` `CP:112` | 同 E5（`Core:ChapterAutoRepair.cs:176`、`Core:ChapterRepairApplier.cs:127/250`、`Core:SourceEditTransaction.cs:111`） | 弹窗 + 写产物 + 改内存树 + 写备份 + **⚠️写TXT（仅当核对窗选「同时改原文」并点「应用所选修改」）** |
| E115 | 目录辅助修复窗 `dialog.Closed` | `CP:206` | `Closed` | 内联 lambda `CP:206` | `lifetime.Cancel()`、`request?.Cancel()` | — | 仅UI |
| E116 | 对比重复正文窗「定位此章到章节树（不删除）」（`locate`，两份各一个） | `CC:59-61` | `Click` | 内联 lambda `CC:61`，返回后 `CC:77` | `CC:LocateDuplicateChapter` `CC:143` → `WB:ApplySearchFilter` `CC:147`、`WB:AllChapters_Click` `CC:148`、`W:NavigateToSourceLine` `CC:149` | — | 仅UI（导航，不删除） |
| E117 | 对比重复正文窗「保留前一份/后一份，删除…」（`button`，两份各一个） | `CC:63-66` | `Click` | 内联 lambda `CC:66`，返回后 `CC:78-79` | `CC:RemoveDuplicateCopy` `CC:153` → `ChapterContentDuplicates.RemoveCopy` `CC:159`、`WB:ConfirmWorkbench` `CC:160`、`RepairIntegrity.Verify` `CC:166`、`RepairIntegrity.SaveRemovedAsync` `CC:167`、`W:Mutate` `CC:174` | `Core:ChapterContentDuplicates.cs:80` `RemoveCopy` / `:52` `Compare`；`Core:RepairIntegrity.cs:284` `Verify` / `:294` `SaveRemovedAsync` | 弹窗 + 改内存树 + **写备份**（快照 + 移除清单）；不写 TXT |
| E118 | 对比重复正文窗「暂不删除」（`cancel`，`IsCancel`） | `CC:73` | 对话框取消 | `CC:78` 早退 | — | — | 仅UI |
| E119 | 对比重复正文窗 `Loaded` | `CC:75` | `Loaded` | 内联 lambda `CC:75` | `preview.Select/ScrollToHome` | — | 仅UI |
| E120 | 结构整理窗「应用重建方案」（`apply`） | `SR:35-37` | `Click` | 内联 lambda `SR:37`，随后 `SR:53-64` | `W:Mutate` `SR:54`、`WB:ApplySearchFilter` `SR:62`、`WB:AllChapters_Click` `SR:62` | （数据来自 E12/E83 的 `ChapterStructureRecovery.Analyze`） | 弹窗 + 改内存树 |
| E121 | 结构整理窗「取消」（`cancel`） | `SR:36-38` | `Click` | 内联 lambda `SR:38` → `SR:53` 早退 | — | — | 仅UI |
| E122 | 目录修复进度窗「取消」（`stop`） | `AR:128-129` | `Click` | 内联 lambda `AR:129` | `cancellation.Cancel()`；影响 `ChapterAutoRepair.RepairAsync` 的 token `AR:143` | — | 仅UI（取消） |
| E123 | 目录修复进度窗 `Closing` | `AR:132` | `Closing` | 内联 lambda `AR:132` | `cancellation.Cancel()` | — | 仅UI（取消） |
| E124 | `ChapterTreeNode.Changed`（模型事件，由 E3/E4 及所有树改动触发） | 挂接于 `W:602` | `Changed` | `W:Node_Changed` `W:583` | `W:CaptureSnapshot` `W:587`、`W:SnapshotsEqual` `W:588`、`W:UpdateUndoRedoButtons` `W:593`、`W:UpdateSummary` `W:594` | — | 仅UI + 撤销栈（非改树本身） |

---

## 三、`RepairReviewWindow`（纯代码构建，14 条）

窗口本身没有任何 XAML；所有控件都在构造函数里建成。它是**唯一**能把「应用到原文」这个决定交出去的地方，`_apply` 回调在 `AR:176` 传入、`AR:188` 读回、`AR:213` 执行。

| # | 界面元素（代码创建时的变量名/文本） | 位置（文件:行） | 事件 | 处理的 C# 方法（文件:行） | 该方法直接调用的方法/类（带文件:行） | 最终落到 Core 的公开 API（带文件:行） | 副作用 |
|---|---|---|---|---|---|---|---|
| E125 | 变更行复选框 `box`（每个有 `ReferenceAction` 的行一个，`Tag=action.Key`） | 创建 `RR:577-596`，`Checked` 挂接 `RR:590` | `Checked` | `RR:Refresh` `RR:844` | `RR:PreviewChanges` `RR:810`、`ChapterAutoRepair.RebuildWithSelection` `RR:830`、`RR:RefreshLandingDetail` `RR:889` | `Core:ChapterAutoRepair.cs:335` `RebuildWithSelection`；`Core:RepairIntegrity.cs:179` `DescribeWithActions` | 仅UI（重算预览，纯 Core 计算） |
| E126 | 同上 `Unchecked` | 挂接 `RR:591` | `Unchecked` | `RR:Refresh` `RR:844` | 同 E125 | 同 E125 | 仅UI |
| E127 | 「并从原文删除这一份」复选框 `deleteFromSource`（仅 `EditSource` 且 `NeedsExplicitOptIn` 的行出现） | 创建 `RR:529-550`，`Checked` 挂接 `RR:539` | `Checked` | 内联 lambda `RR:539-543` → `RR:Refresh` `RR:542` | `_duplicateOptIn.Add`、`RR:Refresh` `RR:844` → `RefreshLandingDetail` `RR:1025` → `_previewMode(_mode, chosen)` `RR:1031` → `AR:181` `applier.Preview` | `Core:RepairDecision.cs:153` `NeedsExplicitOptIn` / `:127` `Decide`；`Core:ChapterRepairApplier.cs:110` `Preview` | 仅UI（预演）+ **决定后续 TXT 是否被物理删除** |
| E128 | 同上 `Unchecked` | 挂接 `RR:544` | `Unchecked` | 内联 lambda `RR:544-548` | 同 E127（移出 `_duplicateOptIn`） | 同 E127 | 仅UI + 同上（反向） |
| E129 | 变更分组标题 `Border`（`BuildGroupHeader`） | 创建 `RR:366-411`，`MouseLeftButtonUp` 挂接 `RR:410` | `MouseLeftButtonUp` | `RR:ShowGroupOverview` `RR:786` | `RR:BlurbOf` `RR:800` | — | 仅UI（右栏说明） |
| E130 | 变更行 `Border`（`BuildRow`） | 创建 `RR:471-570`，挂接 `RR:564` | `MouseLeftButtonUp` | `RR:ShowDetail` `RR:707` | `RR:ChangeGroupKey` `RR:710`、`RR:BlurbOf` `RR:731` | — | 仅UI |
| E131 | 目录事实行 `Border`（`BuildFactRow`，仅「参考目录未匹配」「疑似公告」两组） | 创建 `RR:598-643`，挂接 `RR:641` | `MouseLeftButtonUp` | `RR:ShowFacts` `RR:739` | — | — | 仅UI |
| E132 | 「已核实参考目录确属本书此版本」`_verified`（仅当报告含「来源与文件对不上」时创建） | 创建 `RR:905-911`，`Checked` 挂接 `RR:908` | `Checked` | `RR:Refresh` `RR:844` | `_applyButton.IsEnabled = … && (_verified?.IsChecked ?? true)` `RR:890` | — | 仅UI（门禁：不勾选则应用按钮禁用） |
| E133 | 同上 `Unchecked` | 挂接 `RR:909` | `Unchecked` | `RR:Refresh` `RR:844` | 同 E132 | — | 仅UI |
| E134 | 「暂不应用」`close`（`IsCancel`） | `RR:912-913` | `Click` | 内联 lambda `RR:913` | `DialogResult = false` → `AR:188` 早退 `SetReviewResult("未应用修复方案…")` `AR:189` | — | 仅UI（**不写任何文件**） |
| E135 | 「应用所选修改」`_applyButton`（`IsDefault`） | `RR:914-926`，`Click` 挂接 `RR:918` | `Click` | 内联 lambda `RR:918-924` | `RR:SelectedActions` `RR:920` → `_apply(chosen)` `RR:922`（回调在 `AR:176`）→ `DialogResult = true` `RR:923` | 回到 `AR:194` `dialog.SelectedDecisions()` → `Core:ChapterRepairApplier.cs:165` `DecisionsFor`；`TreeOnly` 分支 `AR:198-209`；`EditSource` 分支 `AR:213` → `Core:ChapterRepairApplier.cs:250` `ApplyAsync` → `Core:SourceEditTransaction.cs:111` `ExecuteAsync` → `:227` `ReplaceAtomically` | 弹窗关闭 + 改内存树 + 写备份 + **⚠️写TXT（当且仅当 E137 处于 `EditSource`；`TreeOnly` 则零字节改动）** |
| E136 | 「只改章节树」`_treeOnlyOption`（`RadioButton`，`GroupName=LandingMode`） | `RR:946-953`，`Checked` 挂接 `RR:961` | `Checked` | `RR:SetMode(TreeOnly)` `RR:1001` | `RR:RefreshLandingDetail` `RR:1005`、`RR:Refresh` `RR:1014` | `Core:RepairDecision.cs:80` `RepairLandingMode.TreeOnly`；`Core:ChapterRepairApplier.cs:275` 提前返回 | 仅UI（切模式 = 承诺「原文一个字节都不会改动」，文案 `RR:1035`） |
| E137 | 「同时改原文」`_editSourceOption`（默认值来自 `AppSettingsStore…DefaultRepairLandingMode`，`AR:180`） | `RR:954-960`，`Checked` 挂接 `RR:962` | `Checked` | `RR:SetMode(EditSource)` `RR:1001` | `RR:RefreshLandingDetail` `RR:1025` → `_previewMode` `RR:1031`（`AR:181` `applier.Preview`）；失败时禁用按钮 `RR:890` | `Core:ChapterRepairApplier.cs:110` `Preview`（`EditSource` 分支 `:214-233` 会真正渲染补丁并统计受影响行） | 仅UI（**唯一使能 ⚠️写TXT 的开关**） |
| E138 | 构造期首次预览（`_building=true` → `RefreshRows(PreviewChanges())` → `Refresh()`） | `RR:145-151` | 无用户交互（初始化入口） | `RR:PreviewChanges` `RR:810`、`RR:Refresh` `RR:844` | `RR:SelectedActions` `RR:823`、`Core:ChapterAutoRepair.cs:335`、`Core:RepairIntegrity.cs:179`、`RR:RefreshLandingDetail` `RR:889` → `_previewMode` `RR:1031` | `Core:ChapterAutoRepair.cs:335`；`Core:RepairIntegrity.cs:179`；`Core:ChapterRepairApplier.cs:110` | 仅UI（零写入；注释 `RR:816-821` 说明为何首帧必须用计划默认选择） |

---

## 四、`BuiltinCleanupWindow`（6 条）

界面全部由 `AddButton`（`BC:92-96`）与 `AddField`（`BC:87-91`）在构造期生成；`AddButton` 在 `BC:95` 统一挂 `Click`。

| # | 界面元素 | 位置（文件:行） | 事件 | 处理的 C# 方法（文件:行） | 该方法直接调用的方法/类（带文件:行） | 最终落到 Core 的公开 API（带文件:行） | 副作用 |
|---|---|---|---|---|---|---|---|
| E139 | 「编辑本项自定义替换与测试…」 | `BC:59-68`（`Click` 经 `BC:95`） | `Click` | 内联 lambda `BC:59-68` | `BC:SaveCurrent` `BC:61`、`new TextCleanupRuleManagerWindow` `BC:63`（**不在本批文件内**） | `Core:BuiltinCleanupOverride.cs`（`BuiltinOverrides` 读取） | 弹窗 + 仅UI |
| E140 | 「恢复本条默认」 | `BC:70`（`Click` 经 `BC:95`） | `Click` | `BC:Restore(false)` `BC:126` | `InkDialog.Show` `BC:128`、`BuiltinCleanupConfiguration.Restore` `BC:129`、`BC:LoadCurrent` `BC:129` | `Core:BuiltinCleanupOverride.cs:45` `Restore` | 弹窗 + 仅UI |
| E141 | 「恢复全部内置规则默认」（`singleRuleOnly=true` 时不创建） | `BC:71`（`Click` 经 `BC:95`） | `Click` | `BC:Restore(true)` `BC:126` | 同 E140，`all=true` | `Core:BuiltinCleanupOverride.cs:45` `Restore` | 弹窗 + 仅UI |
| E142 | 「取消」 | `BC:73`（`Click` 经 `BC:95`） | `Click` | `BC:Close` | — | — | 仅UI |
| E143 | 「保存并返回预览」 | `BC:74`（`Click` 经 `BC:95`） | `Click` | 内联 lambda `BC:74` | `BC:SaveCurrent` `BC:112`（`AdvertisementRuleOptions.CompileMatcher` `BC:117`、`ParseKeywords` `BC:118`）、`DialogResult=true` | `Core:BuiltinCleanupOverride.cs:22` `ParseKeywords` / `:34` `CompileMatcher` | 仅UI（`Result` 交回调用方重新预览；**本窗不写盘**） |
| E144 | `_rules`（内置规则下拉） | `BC:11`、`BC:75` | `SelectionChanged` | 内联 lambda `BC:75-81` | `BC:SaveCurrent` `BC:79`、`BC:LoadCurrent` `BC:80` | — | 仅UI |

---

## 五、`NumericHeadingRulesWindow`（11 条）

所有按钮都经 `Button(string, Action)`（`NR:85-90`），该包装器在 `NR:88` 统一挂 `Click` 并加 `try/catch → MessageBox`（这是本批唯一用 `MessageBox` 而非 `InkDialog` 的窗口）。

| # | 界面元素 | 位置（文件:行） | 事件 | 处理的 C# 方法（文件:行） | 该方法直接调用的方法/类（带文件:行） | 最终落到 Core 的公开 API（带文件:行） | 副作用 |
|---|---|---|---|---|---|---|---|
| E145 | 「取消」 | `NR:37`（`Click` 经 `NR:88`） | `Click` | `NR:Close` | — | — | 仅UI |
| E146 | 「应用到章节工作台」（`IsDefault` 语义由 `NR:128` `DialogResult=true` 实现） | `NR:38`（`Click` 经 `NR:88`） | `Click` | `NR:Apply` `NR:128` | `NR:Read` `NR:93`、`DialogResult=true` | `Core:NumericHeadingRule.cs:14` `Compile`；`Core:HeadingTypoRules.cs:6` `Parse`（`NR:95-96` 校验） | 仅UI（`Result` 交回 `W:491`/`WB:529`） |
| E147 | `_scope`（作用范围：仅本书 / 设为全局默认 / 恢复继承全局） | `NR:15`、`NR:44-49` | `SelectionChanged` | 内联 lambda `NR:45-49` | `NR:Load(_global)` `NR:47`、启停 `_corrections/_pattern/_minimum/_enabled` `NR:48` | — | 仅UI |
| E148 | `_presets`（已保存方案下拉） | `NR:16`、`NR:51-52` | `SelectionChanged` | 内联 lambda `NR:52` | 回填 `_presetName.Text` | — | 仅UI |
| E149 | 「载入方案」 | `NR:55`（`Click` 经 `NR:88`） | `Click` | `NR:LoadPreset` `NR:113` | `NR:Load` `NR:117` | `Core:NumericHeadingRule.cs:5` `NamedNumericHeadingPreset` | 仅UI |
| E150 | 「保存方案」 | `NR:56`（`Click` 经 `NR:88`） | `Click` | `NR:SavePreset` `NR:102` | `NR:Read` `NR:106`、`MessageBox.Show` `NR:108`（同名覆盖确认）、`NR:RefreshPresets` `NR:111` | `Core:NumericHeadingRule.cs:5` | 弹窗 + 仅UI |
| E151 | 「删除方案」 | `NR:57`（`Click` 经 `NR:88`） | `Click` | `NR:DeletePreset` `NR:119` | `MessageBox.Show` `NR:123` | — | 弹窗 + 仅UI |
| E152 | 「恢复内置默认」 | `NR:58`（`Click` 经 `NR:88`） | `Click` | 内联 lambda `NR:58` | `NR:Load(new())` `NR:58` | `Core:TocHierarchyOptions`（默认值） | 仅UI |
| E153 | 「填入三位数字示例」 | `NR:71`（`Click` 经 `NR:88`） | `Click` | 内联 lambda `NR:71` | 写入 `_pattern.Text` 示例正则 | — | 仅UI |
| E154 | 「测试匹配」 | `NR:78`（`Click` 经 `NR:88`） | `Click` | `NR:Test` `NR:126` | `NumericHeadingRule.Compile` `NR:126`、`NumericHeadingRule.Matches` `NR:126` | `Core:NumericHeadingRule.cs:14` `Compile` / `:22` `Matches` | 仅UI |
| E155 | 「所有按钮的统一捕获」（`Button` 包装器） | `NR:85-90`（挂接 `NR:88`） | `Click`（包装） | 内联 lambda `NR:88` | `MessageBox.Show(error.Message, "数字识别规则")` | — | 弹窗（异常兜底） |

---

## 六、`AutomaticCheckWindow`（6 条）

| # | 界面元素 | 位置（文件:行） | 事件 | 处理的 C# 方法（文件:行） | 该方法直接调用的方法/类（带文件:行） | 最终落到 Core 的公开 API（带文件:行） | 副作用 |
|---|---|---|---|---|---|---|---|
| E156 | 「保存检查设置」`save`（`IsDefault`） | `CK:32-33` | `Click` | 内联 lambda `CK:33` | `CK:Capture` `CK:77`、`DialogResult=true` | `Core:AutomaticCheckOptions.cs`（`TargetLabels`/`CleanupLabels`/`Summary`）；`Core:BuiltinCleanupOverride.cs:22` | 仅UI（`Result` 由 `MainWindow.xaml.cs:1557-1565` 写设置文件） |
| E157 | 「取消」`cancel`（`IsCancel`） | `CK:31` | 对话框取消 | `CK:31` 无处理器，由 WPF 关闭 | — | — | 仅UI |
| E158 | 「编辑自定义检查规则…」`custom` | `CK:59-60` | `Click` | 内联 lambda `CK:60-64` | `new TextCleanupRuleManagerWindow` `CK:62`（**不在本批文件内**）、`CK:Refresh` `CK:63` | — | 弹窗 + 仅UI |
| E159 | 「复制文本清理中的自定义规则」`import` | `CK:66-67` | `Click` | 内联 lambda `CK:67` | `_rules.Concat(existingRules…)` `CK:67`、`CK:Refresh` `CK:67` | — | 仅UI |
| E160 | 全部复选框：`_enabled` + `_targets`（`AutomaticCheckOptions.TargetLabels`）+ `_cleanup`（`AutomaticCheckOptions.CleanupLabels`） | 创建 `CK:39/47/54`，循环挂接 `CK:70-71` | `Checked` + `Unchecked` | 内联 lambda `CK:71` → `CK:Refresh` `CK:73` | `CK:Capture` `CK:73`（反射写 `TextCleanupOptions` 属性 `CK:80`） | `Core:AutomaticCheckOptions.cs:52` `ActiveLabels` | 仅UI |
| E161 | `_variant`（简繁转换：不检查/繁转简/简转繁） | `CK:57`、`CK:72` | `SelectionChanged` | 内联 lambda `CK:72` → `CK:Refresh` `CK:73` | `CK:Capture` `CK:77` | `Core:AutomaticCheckOptions.cs` | 仅UI |

---

## 七、辅助类：无交互入口（4 个文件）

| 文件 | 内容 | 被谁使用 | 说明 |
|---|---|---|---|
| `MS` `ChapterMultiSelection.cs` | `OperationSelection` `MS:12`、`SetOperationSelection` `MS:16`、`SelectChapterForOperation` `MS:24`、`UpdateBatchActionButtons` `MS:67`、`PlanBatchAction` `MS:79`、`TryBatchChapterAction` `MS:108` | E24/E25/E26/E27/E32/E45/E52/E55/E46 等 | 纯 C# 逻辑，**不创建任何控件**。`TryBatchChapterAction` 内部调 `WB:ConfirmWorkbench` (`MS:116`) 与 `W:Mutate` (`MS:122`)，副作用随调用者 |
| `DC` `ChapterDisplayCollection.cs` | `Synchronize` `DC:9` | `W:66-67`、`RP:RefreshFilteredTree` `RP:244`（其中 `RP:252` 调 `Synchronize`）、`W:RefreshVisibleChildren` `W:979`（内部 `W:981` 调 `Synchronize`） | 显示投影，`Items.Clear()` + 单次 Reset（`DC:16-21`）以避免虚拟化失效；不参与保存 |
| `IA` `ChapterIssueAction.cs` | `IsInformational` `IA:14`、`ForGeneric` `IA:20` | `RP:639`（决定 `ReviewActionButton` 文案） | 无控件。**注：`IsInformational` 在窗口代码里没有任何调用点**（`RP` 用显式 `issue?.Code == …` 分支替代），属未被引用的公开辅助方法 |
| `BL` `ChapterBreakpointLabel.cs` | `For` `BL:14` | `RP:417`（`RP:BreakLabel`） | 无控件，纯字符串格式化；`BL:17-23` 专门处理 `MissingNumbers.Count == 0` 的降级断点 |

---

## 八、非 UI 的程序化入口（附录，不计入 161）

这些不是界面元素，但外层壳与测试通过它们驱动本窗口，盘点重复入口时需要一并对照。

| 入口 | 位置 | 触发的等价 UI 路径 |
|---|---|---|
| `NavigateToSuggestion(ConversionPreflightIssue)` | `W:98` | 等价于点击某一组建议（E47） |
| `NavigateToSourceLine(int)` | `W:110` | 等价于搜索 `行:N`（E21） |
| `ApplySearchFilter()` | `WB:484` | 等价于 E71/E22/E93 |
| `RestoreConfirmedGroups()` | `WB:448` | 等价于 E92 |
| `ApplyRebuildEntries(ChapterTreeDocument)` / `ApplyRebuiltDocument` | `AR:20` / `WB:166` | 修复/E39/E63 的落地函数 |
| `DiscardChangesAndClose()` | `WB:58` | 等价于 E50 且强制放弃 |
| `ConfirmAction`（`Func<string,bool>` 注入点） | `WB:26`、`WB:55`、`WB:403` | 测试替身；非空时 E74/E88/E103/E104/E116/E117 的弹窗被短路 |
| `TextEditorPath` / `GlobalNumericDefaults` / `NumericPresets` / `InheritNumericDefaults` / `WorkingCopyCreated` | `AC:12`、`W:30-33` | 外层注入（`MainWindow.xaml.cs:1955-1959`） |
| `LastMigration` | `WB:279` | 记录 E39 的迁移结果，供断言 |

---

## 九、死 UI

| 名称 | 位置 | 判定依据 |
|---|---|---|
| `ChapterActionsPopup`（`Popup`） | `X:268-286` | 全仓库 grep `ChapterActionsPopup` 只有 2 处命中，且**都是 `IsOpen = false`**（`W:515`、`WB:49`），没有任何一处 `IsOpen = true`。这个 Popup 从不打开。它的角色已被 `RP:ReviewMore_Click`（`RP:738-758`）动态构建的 `ContextMenu` 取代 |
| `RecoverStructureButton`「结构整理…」 | `X:123` | `Visibility="Collapsed"` 且全仓库 grep 该 x:Name **零命中**（除 XAML 声明本身），永远不会显示 |

**半死（需要单独说明，不算死 UI）**：Popup 内的 7 个按钮 `MergeButton`(`X:274`)、`EditTitleButton`(`X:276`)、`CorrectNumberButton`(`X:277`)、`DemoteButton`(`X:279`)、`PromoteButton`(`X:280`)、`MoveUpButton`/`MoveDownButton`(`X:282`)。它们作为**可视控件**永远不会被渲染，但 `RP:742-749` 会把每个 `Visibility==Visible` 的按钮镜像成 `MenuItem`，并在点击时对**同一个 Button 对象** `RaiseEvent(Button.ClickEvent)`（`RP:747`），所以 E52–E58 的处理函数仍然可达 —— 只是入口表面从 Popup 变成了右键菜单。特别注意 `CorrectNumberButton`：它在 XAML 里默认 `Collapsed`，靠 `AC:UpdateNumberSuggestion` (`AC:87`) 变可见；由于 Popup 不渲染，用户唯一能见到「疑似错号 · 修改为：X」的地方就是镜像菜单。

**非死 UI 说明**：`RulesStorage`（`X:287`，`Visibility="Collapsed"`）是识别设置面板的隐藏宿主 —— `WB:146` 把 `RecognitionRulesPanel` 摘出来塞进对话框，`WB:161` 再放回去。它的 Collapsed 是设计的一部分。

---

## 十、重复 / 重叠功能清单

### D1. 同一个处理函数被两个不同按钮绑定，靠 `sender` 区分语义（**最危险的一处**）

- `RebuildFromRules_Click` 同时绑在 `RefreshSourceButton`(`X:222`) 与 `RebuildRulesButton`(`X:317`)。
- 分派点在 `W:435`：`if (_sourceChanged && ReferenceEquals(sender, RefreshSourceButton))` → 走**版本迁移**（`W:437` → `WB:MigrateToExternalEditAsync`），否则走**重新识别**（`W:448` `ChapterTreeDocument.LoadAsync` + `WB:ApplyRebuiltDocument`）。
- 也就是说：同样是「刷新/重新识别」，两个按钮语义不同，而**XAML 上看不出来**；`W:435` 一旦被改动（例如把 `ReferenceEquals` 换成别的东西），「刷新原文」会在用户改了一个错字后丢掉全部手工编排。`W:430-434` 的注释明确记录了这次分离的原因。

### D2. 「撤销」有两个入口，完全等价

- `UndoButton`(`X:124`) 与 `UndoResultButton`(`X:214`) 都绑 `Undo_Click`(`W:509`)。
- `UndoResultButton` 只在「刚处理完且撤销栈深度等于结果深度」时可见（`RP:492`），功能与主撤销按钮**没有任何区别**。

### D3. 「预览结构整理」有 4 个入口，其中 1 个是死的

| 入口 | 位置 | 状态 |
|---|---|---|
| `RecoverStructureButton`「结构整理…」 | `X:123` | **死 UI** |
| 横幅按钮「预览结构整理…」 | `X:133` | 活 |
| 整理工具菜单「重建编号分组与结构（预览）…」 | `WB:424` | 活 |
| 核对卡按钮（`chapter_structure_suggested` 时文案变「预览结构整理…」） | `RP:546`、转派 `RP:661` | 活 |

### D4. 「编辑成品标题」有三条入口

- `EditTitleButton`(`X:276`，Popup 表面死，经 E102 镜像菜单可达) → `WB:EditCurrentTitle_Click`(`WB:528`) → `AC:EditTitle`。
- `ReviewActionButton` 的兜底分支（`RP:693`：`else if (_selectedNode is not null) EditTitle(_selectedNode)`）—— 只要前面的问题类型都不匹配，核对卡按钮就变成「编辑成品标题…」（文案来自 `IA:28`）。
- 章节标题双击（`X:96` → `AC:Title_MouseLeftButtonDown`）。

  三者最终都是同一个 `AC:EditTitle`，但**入口条件完全不同**：一个要多选数为 1，一个没有任何提示。

### D5. 「取消章节身份 / 还原为正文」与「还原为上一章正文」语义相邻

- `MergeButton`「还原为上一章正文 / 还原 N 章为正文」(`X:274` → `W:Merge_Click` `W:235`)：合并正文到**上一章**，保留章节身份。
- `ReviewMore` 菜单「取消章节身份，还原为正文…」(`RP:750` → `WB:RestoreContainerToBody` `WB:541`)：**取消章节身份**，正文交回前文或提升子章节。
- 两者都能被 `ReviewActionButton` 触发（`RP:688` 走 Merge；`RP:637` 的按钮文案「还原 N 处为正文」也走 Merge），但 `RestoreContainerToBody` **只能**从右键菜单到达 —— 用户在核对卡上看不到它。

### D6. 「清理重复标题」有全书与分组两条入口，共用一套对话框

- E84「清理全书重复标题…」（`WB:425` → `AC:CleanDuplicateTitles_Click` → `CleanDuplicateTitles(null)`）。
- `RP:689`（`chapter_duplicate` 分支 → `CleanDuplicateTitles(ReviewGroup(issue)?.NodeIds.ToHashSet())`，只处理当前组）。
- 两者的区别只在 `scope` 参数（`AC:20` 的文案随之变化）；对话框、预览函数、备份动作完全相同。

### D7. 「获取参考目录」有三条入口，行为不同

| 入口 | 位置 | 行为 |
|---|---|---|
| `FetchCatalogButton`（工具条） | `X:165` → `RP:311` | 已有目录时**转派**到 E6 的手动核对窗（`RP:314`）；否则联网搜索并 `SaveCatalog` |
| `ReviewActionButton`（`chapter_number_gap` 且无目录） | `RP:676` | 直接调 `RP:FetchCatalog_Click` |
| `CatalogAssist_Click` 对话框内的 `auto`（一键自动获取） | `CP:188` → `CP:Network(true)` | **另一套**网络代码（`CP:151-167`），与 `RP:311-356` 不共享实现，只是都调 `ReferenceCatalogClient.DiscoverAsync` |

### D8. 重复正文删除：两条路径同名不同后果

- E117（对比窗「保留…删除…」）→ `CC:RemoveDuplicateCopy` → `WB:ConfirmWorkbench` 二次确认 + `RepairIntegrity.Verify` 守恒校验 + `SaveRemovedAsync` 留档。**只改内存树。**
- 修复核对窗的 `RemoveDuplicate` 动作（`RR:53`、`RR:1101`）→ 在 `EditSource` 模式下，若用户额外勾选 E127，`SourceEffectPolicies.Decide(..., deleteDuplicateFromSource: true)`（`Core:RepairDecision.cs:127`）会让它**物理删除 TXT 里的行**（`Core:RepairEffectCompiler.cs:116` `DescribeDuplicateDeletion`，由 `Core:ChapterRepairApplier.cs:374` 的 `NeedsExplicitOptIn` 分支放行，`Core:RepairPlanCompiler.cs:100` 实际生成删除操作）。
- 两者在 UI 上没有任何交叉提示：用户在工作台里删重复永远不会动 TXT，在修复核对窗里删重复**可能**会。

### D9. `AllChapters_Click` 被 4 处程序化调用 + 1 处自身绑定

`WB:588`（`RestoreContainerToBody` 末尾）、`CC:148`（`LocateDuplicateChapter`）、`SR:62`（结构整理落地后）、`WB:441`（清除搜索与筛选）各调一次，定义在 `WB:481`，另由 `X:152` 绑定。它同时改两个 RadioButton 的勾选状态并重算可见性。

### D10. 「全部加入/不加入目录」与逐行复选框入口并存

- E87/E88（整菜单）改全书 `IncludeInToc`。
- `X:90` 每行的 `CheckBox IsChecked="{Binding IncludeInToc}"` 逐章改。
- 两者都进同一撤销栈（经 `W:Node_Changed` `W:583`），但**只有 E88 会弹确认框**，逐章点击不会。

---

## 十一、最复杂的 3 个入口

### 第 1 名：E5 / E114 —— `RunRepairReviewAsync`（一键目录修复 / 手动核对后的预览）

复杂度来源（`AR:103-233`，131 行）：
1. **两种调用签名合流**：`AutoRepair_Click`(`AR:45`) 传 `reference=null, report=null`；`CP:112` 传已选定的目录与一个状态文本回调。`report` 是否为空决定要不要自建进度窗（`AR:120-134`），因此同一条链上有**两套进度 UI**。
2. **三种结果分支**：无目录且零动作 → 弹目录对话框（`AR:155-163`）；`TreeOnly` → 备份 + 移除清单 + 内存树（`AR:198-209`）；`EditSource` → 事务写盘 + 版本迁移 + 重载文档（`AR:213-224`）。
3. **横跨 5 个对象的时序**：`ChapterAutoRepair.RepairAsync`（网络 + 对齐）→ `RepairReviewWindow`（用户勾选 + 落地模式）→ `ChapterRepairApplier.Preview/DecisionsFor`（与执行同源的编译）→ `ApplyAsync`（写盘）→ `ReloadAfterEdit`(`AR:78`) + `ApplyRepairEntries`(`AR:20`)（把树搬到新行号）。
4. **两个指纹校验点**：`ChapterRepairApplier.ApplyAsync` 里先比对磁盘 SHA-256（`Core:ChapterRepairApplier.cs:265-268`），`SourceEditTransaction.ExecuteAsync` 里再比对一次（`Core:SourceEditTransaction.cs:118-120`）。
5. **这是 17 个文件里唯一能写用户 TXT 的地方**，且写入与否由另一个窗口（`RR`）里的一个 RadioButton 决定。

### 第 2 名：E34 —— `ReviewAction_Click`（核对卡主按钮）

复杂度来源（`RP:649-697`，49 行）：
1. **9 个按 `issue.Code` 分派的早期返回**：`chapter_unnumbered` → 目录对话框；`chapter_repeated_sequence` → 只看导航；`chapter_content_duplicate` → 对比窗；`chapter_structure_suggested` → 结构整理；`chapter_heading_typo` → 批量补建；`chapter_number_gap` → 三分支（本组补建 / 全书补建 / 抓目录）；`numeric_chapters_suspected` → 重识别。
2. **落到通用分支后再分 6 路**：多选或 `numeric_body_group` → `Merge_Click`；`chapter_duplicate` → `CleanDuplicateTitles(scope)`；`volume_number_duplicate`/`chapter_level_gap` → 转 `ReviewMore_Click`；`Missing` 类目 → `Split_Click`；有错号建议 → `CorrectNumber_Click`；否则 → `EditTitle`。
3. **按钮的文案、可见性、可用性本身是计算出来的**（`RP:470-647`，178 行的 `UpdateReviewCard`），同一段代码既决定「按钮写什么」也决定「点了会走哪条路」，两者必须一致 —— 这正是 `IA` 注释（`IA:5-10`）所说的历史缺陷来源。
4. 副作用范围从「纯导航」一直到「写备份 + 改内存树」，还有一条间接通向 ⚠️写TXT（`RP:653` → `CP` 对话框 → E114）。

### 第 3 名：E137 + E135 —— `RepairReviewWindow` 的落地模式与提交

复杂度来源（`RR:942-1053` + `RR:894-929` + `RR:159-188`）：
1. **一个开关决定是否覆盖用户文件**：`_editSourceOption.Checked`(`RR:962`) 只是切 `_mode`，但它同时驱动 `RefreshLandingDetail`(`RR:1025`) → `_previewMode`(`RR:1031`) → `ChapterRepairApplier.Preview`，后者在 `EditSource` 下会**真正渲染整个补丁**并数出受影响行数（`Core:ChapterRepairApplier.cs:214-233`），失败则禁用应用按钮（`RR:890`）。文案 「将改动原文的 N 行」是算出来的，不是写死的（对比 `RR:939-940` 记录的旧实现）。
2. **逐行可撤销 + 逐行可加码**：`SelectedActions`(`RR:159`) 从复选框读，`SelectedDecisions`(`RR:173`) 再叠加 `_duplicateOptIn`（E127），最后经 `AR:182` 传入的 `ChapterRepairApplier.DecisionsFor` 变成 `RepairDecision` 列表 —— 三层转换必须与编译器一致，否则「窗口说的」和「applier 做的」会分叉。
3. **首帧陷阱**：构造期复选框还不存在，`PreviewChanges`(`RR:810`) 必须用 `_building` 标志回退到 `Plan.DefaultSelection`（`RR:821-823`），否则整个窗口会画出一个空清单 —— 注释明确说这是曾经的缺陷。
4. **两个门禁**：`_verified`（`RR:905-911`，目录与文件对不上时必须人工确认）与 `CurrentPreview.CanApply`（`RR:890`）同时为真才允许提交。
5. 提交后 `AR:194-224` 还要再走一次「重新取决策 → 重新编译 → 分支落地 → 重载文档」，即**同一份决策在两个地方被编译两次**（一次为预览、一次为执行），靠 `ProposalFor`(`Core:ChapterRepairApplier.cs:145`) 保证两次输入同源。
