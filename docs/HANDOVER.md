# EasyPub Modern 接手文档

> **基线：v1.61.2（`honest-footer`），HEAD `9121a8f`。**
> 写给下一个接手这个项目的 agent 或工程师。
>
> 这份文档的目标不是"介绍功能"，而是让你在**不重复踩坑**的前提下改动它。
> 所有事实都标注了是**实测**还是**推断**。请保持这个习惯 —— 这个项目的历史上
> 有太多"文档写了但代码不是那样"的例子，包括本文档自己的前几版。
>
> **不确定的不要照抄。** 本文档末尾 §14 集中列出了"未实测 / 未确认"的条目。

---

## 0. 先读这一段：三条不可动的约束

这个项目最特殊的地方：**它有三条不可动摇的产品约束，一切设计争论都以它们为准。**

| 约束 | 含义 | 代码里的体现（已核实位置） |
|---|---|---|
| **正文守恒** | 修复可以改章节结构，但**不能凭空造出正文** | `RepairIntegrity.Verify`（`src/EasyPub.Core/RepairIntegrity.cs:284`）—— 行数不守恒时抛 `InvalidDataException`「修复前后原文行不守恒，已停止应用」；`MissingBody` 动作只补标题不补正文 |
| **用户主权** | 手工改动优先于任何自动结果 | `ChapterTreeEntry.RecognitionSource == "manual"`；planner 不覆盖它（`ReferencePlan.cs:172,184,197,366,467,495,524,529`；`SelfRepairPlanner.cs:137`；`ChapterAutoRepair.cs:273,356,426`） |
| **可核对** | 每一次删除都要能说清"删了什么、在哪一行、怎么恢复" | 移除清单 `RepairIntegrity.SaveRemovedAsync`（`:294`）+ 原文 SHA-256（`ChapterTreeDocument.SourceSha256`）+ 备份路径（`SourceBackupStore`）+ 事务收据（`SourceEditTransaction`） |

**违反这三条的设计，无论多优雅都会被推翻。** 反过来，如果你觉得某处代码很别扭却不敢改，
先问一句"它是不是在守这三条之一"。

---

## 1. 认识这个项目

### 1.1 它做什么

把 TXT / EPUB 小说转成 EPUB / MOBI（Kindle）。核心难点不在格式转换，而在**章节识别**：
网文源文件里章节标题的写法五花八门，识别错了整本书的目录就错了。

所以它有一个**章节修复工作流**：识别 → 诊断 → 生成修复方案 → 逐项确认 → 应用
（可只改成品，也可同时改原文）。

### 1.2 仓库、分支、远端

- 路径：`D:\software\DeepSeek-Harness\workspace\easypub-branch`
- 分支：`main`
- 远端：`https://github.com/uiu8/EasyPub-Modern.git`
- 镜像：AtomGit（`tools/publish-atomgit.ps1`），国内用户更新检查走它
- 本地**没有** git tag —— tag 由 `tools/publish-github.ps1` 在发布时创建（实测：`git tag --list "v1.61*"` 为空）

### 1.3 目录结构

```
src/
  EasyPub.Core/        业务逻辑，无 UI 依赖（net10.0）—— 104 个 .cs
  EasyPub.Desktop/     WPF 界面（net10.0-windows），大量 partial class —— 57 个 .cs + 23 个 .xaml
  EasyPub.Cli/         可脚本化的批量转换入口
tests/
  EasyPub.Core.Tests/     97 个 .cs
  EasyPub.Desktop.Tests/  29 个 .cs
samples/六卷缺陷样书/      唯一的端到端验收素材（见 §9.2）
docs/                    设计与发布文档
tools/                   构建、发布脚本（build-release / publish-github / publish-atomgit）
installer/               Inno Setup 脚本（EasyPubModern.iss）
outputs/                 构建产物（Git 忽略）
work/                    临时工作区（Git 忽略）
```

### 1.4 当前基线，以及一处已知的文档滞后

- 版本号：`1.61.2`（`EasyPub.Desktop.csproj:18-20`、`installer/EasyPubModern.iss:2,5`）
- ✅ `docs/RELEASE_v1.61.2.md` 已写
- ⚠️ **`README.md` 没有跟着升**：徽章与下载链接仍指 `v1.61.1`（`README.md:11,16,41,223`）。
  v1.61.2 的发布提交 `9121a8f` 只动了 4 个文件（发布说明、iss、csproj、`ControlStyleContractTests.cs`），
  **没有动 README**。这是实测事实，不是推断。

---

## 2. 跑起来

### 2.1 环境（这一节最容易踩坑）

**必须用 SDK 10.0.302 的 dotnet，不能用系统那个。**

```powershell
$env:DOTNET_ROOT='D:\software\dotnet-sdk-10.0.302'
D:\software\dotnet-sdk-10.0.302\dotnet.exe <命令>
```

原因（已实测）：`C:\Program Files\dotnet\dotnet.exe` **存在但只有运行时、没有 SDK** ——
它的 `--list-sdks` 输出为空，`D:\software\dotnet-sdk-10.0.302\dotnet.exe --list-sdks` 输出
`10.0.302 [D:\software\dotnet-sdk-10.0.302\sdk]`。

### 2.2 构建与测试

```powershell
# 构建
D:\software\dotnet-sdk-10.0.302\dotnet.exe build src\EasyPub.Desktop\EasyPub.Desktop.csproj

# Core 测试（默认并行）
D:\software\dotnet-sdk-10.0.302\dotnet.exe test tests\EasyPub.Core.Tests\EasyPub.Core.Tests.csproj

# Desktop 测试 —— **必须串行**
D:\software\dotnet-sdk-10.0.302\dotnet.exe test tests\EasyPub.Desktop.Tests\EasyPub.Desktop.Tests.csproj `
    -m:1 -- xUnit.MaxParallelThreads=1

# 可靠的做法：按类跑
#   --filter "FullyQualifiedName~WorkbenchContextBarTests"
```

### 2.3 六个必须知道的坑

**坑 1：改了 `EasyPub.Core` 之后必须删它的 `obj` 和 `bin`。**

MSBuild 会**静默跳过** `CoreCompile`，于是你以为构建成功了，实际跑的还是旧 DLL。
症状是"我的改动没生效"，而且没有任何警告。

```powershell
Remove-Item src\EasyPub.Core\obj, src\EasyPub.Core\bin -Recurse -Force
```

`tools/build-release.ps1` 也会替你清一遍（它的注释写明"改过 Core 必须清"）。

**坑 2：Desktop 全量组合跑在本机严重 flaky。**

实测（同一组筛选、同一台机器）：

| 提交 | 总数 | 失败 |
|---|---|---|
| 改动前 `a64096f` | 112 | **60** |
| 改动后 | 128 | **12** |

**基线是失败集的超集** —— 也就是说这些失败与你的改动无关，它们是 WPF 渲染时序问题。
**所有失败项单类跑都通过。单类跑是验收路径，全量组合跑不是。**

**坑 3：绝不并发跑两个 `dotnet test`。**

两个构建会写同一份 `EasyPub.Core.dll`。症状是**几十个莫名其妙的失败** ——
历史上曾一次产生 **66 个假失败**。一次只跑一个。

**坑 4：应用运行时无法构建。**

exe 锁住 DLL，报 `MSB3027 / MSB3021`。先 `Stop-Process -Name EasyPub.Desktop`。
（`build-release.ps1` 会先替你停掉锁住构建输出的进程。）

**坑 5：`grep` / `read` 工具比 PowerShell 的 `Select-String` 好用得多**，后者输出中文时容易截断。

**坑 6：PowerShell 里 `cd` 不影响 .NET 的当前目录。**
`[System.IO.File]::ReadAllBytes("相对路径")` 仍按**进程启动目录**解析，于是你会拿到
"找不到文件"然后怀疑文件不存在。用绝对路径。（本文档核实过程中踩到过一次。）

---

## 3. 架构

### 3.1 分层

```
EasyPub.Desktop (WPF)
   ├── 窗口 / 面板 / 控件
   ├── 交互策略的**措辞**（每个 surface 一套 CTA）
   └── 通过服务调用 Core
EasyPub.Core
   ├── 文档模型（ChapterTreeDocument / ChapterTreeEntry）
   ├── 诊断（ChapterDiagnostics / ChapterReviewAnalyzer）
   ├── 修复（planning → compile → apply）
   ├── 交互语义（IssueResolutionPolicy）
   └── 转换器（EPUB / MOBI）
```

**Core 不知道 UI 的存在**，但 Core 知道"**问题**"和"**解决意图**"这两个概念 ——
这是刻意的：`IssueResolutionPolicy` 住在 Core，因为它要保证同一件事在三个界面表面上语义一致。

### 3.2 Core 模块地图（按重要性）

| 文件 | 职责 | 备注 |
|---|---|---|
| `ChapterTree.cs` | 文档模型、`ChapterTreePlan` | `Entries` 是章节条目列表；`SkipRepeatedHeadingRun` 在 `:343`；`RepeatedHeadingLinesSkipped` 在 `:96` |
| `ChapterDiagnostics.cs` | 低级诊断（gap / order / duplicate…） | 产生 7 个问题码 |
| `ChapterReviewAnalyzer.cs` | 把诊断聚合成**分组** | 16 个码的最终出处 |
| `IssueResolutionPolicy.cs` | **16 个码 → 建议动作**（`Definitions` 在 `:275-293`） | 全软件唯一的语义来源 |
| `IssueCategory.cs` | 码 → 类别 | 委托给 policy，不再自己算 |
| `SelfRepairPlanner.cs` | 无目录时的方案 | 只发"树自身能判定"的动作 |
| `ReferencePlanner.cs` | 有目录时的方案 | 能回答"应该有哪些章" |
| `RepairPlanCompiler.cs` | 方案 → 可执行决策 | 纯函数；`SourceProblems` 的历史见 §8.4 第 1 条 |
| `ChapterRepairApplier.cs` | **唯一写 TXT 的入口** | `:313` `new SourceEditTransaction(...)`，见 §5.4 |
| `SourceEditTransaction.cs` | 事务（备份 → 原子替换 → journal） | `:69` 事务类；`:298` `SourceEditRecovery` 类；`:503` `RecoverAllAsync` |
| `ChapterAutoRepair.cs` | 编排：取目录 / 选 planner / 准备方案 | |
| `RepairRequest.cs` | 依据的判别类型 | `CurrentTreeHeuristic` / `ReferenceRepair` |
| `ChapterRepairContext.cs` | 工作台三行事实的快照 | |
| `TreeProvenance.cs` | 树的来源（本地/目录/未知） | 持久化在 plan 里 |
| `FindingSuppression.cs` | 哪类 finding 可被目录证据推翻 | R3 的产物 |
| `RecognitionConsistency.cs` | 识别规则与编号解析不一致时说出来 | P1-1 的产物，见 §8.2 |
| `InteractionLog.cs` | 交互日志（v1.61.0 新增，`:20`） | 见 §7.3；**没有单元测试**（实测：tests 下 0 命中） |
| `HeadingSyntax.cs` | 标题正则的当前默认值 | `LegacyPattern` `:9`；`ChapterNumber` `:31`；`Numbering` `:33` |
| `ConversionRequest.cs` | `TocHierarchyOptions` + **旧默认值升级表**（`SupersededDefaults` `:188`） | 见 §8.3 |

**行数最大的几个**（实测）：`SourceVersionTransition.cs` 894、`ReferenceCatalog.cs` 731、
`IssueResolutionPolicy.cs` 660、`ConversionPreflightInspector.cs` 626、`ReferencePlan.cs` 615。

### 3.3 Desktop 模块地图

**这个项目大量使用 partial class，所以"一个类"往往散在多个文件里：**

| 文件 | 属于 | 职责 | 行数 |
|---|---|---|---|
| `MainWindow.xaml.cs` | 主窗口 | **全仓库最大的文件** | 4891 |
| `RepairReviewWindow.cs` | 确认窗 | 逐项勾选 + 落地模式 | 1154 |
| `ChapterEditorWindow.xaml{,.cs}` | 工作台 | 窗口骨架、字段、构造、保存 | 1062 |
| `ChapterReviewPanel.cs` | 工作台 | **提醒卡片**（最大的一个面板） | 924 |
| `ChapterWorkbench.cs` | 工作台 | 工具栏、菜单、状态条、下一步提示、底栏 | 818 |
| `ChapterAutoRepairPanel.cs` | 工作台 | 修复入口 `RunRepairReviewAsync` | 275 |
| `ChapterCatalogPanel.cs` | 工作台 | 目录辅助面板 | 231 |
| `ChapterContentComparison.cs` | 工作台 | 正文对比窗 | 184 |
| `ChapterEditorActions.cs` | 工作台 | 树编辑动作 | 170 |
| `ChapterMultiSelection.cs` | 工作台 | 多选与批量 | 155 |
| `PreflightWindow.xaml.cs` | 报告窗口 | 转换前检查 | 137 |
| `ConversionSettingsDraft.cs` | 设置 | config.xml 采纳逻辑 | 124 |
| `PreflightCta.cs` | 报告窗口 | 报告窗口的 CTA 措辞 | — |
| `ChapterIssueAction.cs` | 工作台 | 提醒卡片的 CTA 措辞 | — |
| `Controls.xaml` | 全局 | 25 个具名样式（v1.61.2 新增） | — |

---

## 4. 词汇表（读代码前先读这一节）

这个项目的命名是有含义的，而且注释里经常引用这些词。

### 4.1 章节树（ChapterTree）

- `ChapterTreeDocument`：一份 TXT 的解析结果。有 `Entries`（章节条目）、`SourceLines`（原始行）、`SourceSha256`。
- `ChapterTreeEntry`：一个章节。`Title`（成品标题）、`TitleLineNumber`（原文哪一行是标题）、
  `RecognitionSource`、`ContentRanges`（正文行范围）、`Level`、`Parent` 关系。
- `RecognitionSource`：`pattern`（正则识别）/ `manual`（用户手工建立）/ `reference`（目录对齐）/ `front-matter` 等。
  **`manual` 是神圣的** —— planner 不会覆盖它。

### 4.2 修复的三阶段

```
planning   →   compile   →   apply
（产生动作）    （决定选哪些）  （写文件）
```

- **planning**：`SelfRepairPlanner.Build` 或 `ReferencePlanner.Build` → `ReferencePlan`（一堆 `ReferenceAction`，8 种 kind）
- **compile**：`RepairPlanCompiler.Compile(plan, decisions, mode)` → `RepairCompilation`（纯函数，可测）
- **apply**：`ChapterRepairApplier.ApplyAsync(...)` → 真正改树 / 写 TXT

**8 种动作 kind**：`AddChapter` / `DemoteExtra` / `KeepExtra` / `MissingBody` / `RemoveDuplicate` / `Retitle` / `VolumeNote` / `AdoptHeading`

### 4.3 依据（Basis）：两种，不是一个开关

| 依据 | 用哪个 planner | 能回答什么 |
|---|---|---|
| **当前章节树**（`CurrentTreeHeuristicRequest`） | `SelfRepairPlanner` | "已有的章有什么毛病" |
| **参考目录**（`ReferenceRepairRequest`） | `ReferencePlanner` | "应该有哪些章" |

**关键**：这两种不是"高级版/低级版"，而是**回答两类不同的问题**。
`CurrentTreeHeuristicRequest` 的名字刻意不叫 `SelfRepairRequest` —— 它只保证"本次不重新读取目录"，
**不**保证"输入树从未受过目录影响"。

### 4.4 provenance（树的来源）

`PersistedTreeProvenance` 有三个值：

- `Local` —— 按本地规则识别的
- `Reference` —— 按目录校验过的
- `UnknownLegacy` —— **旧项目文件里没有这个字段**

**⚠️ 这三个值与"现在磁盘上有没有目录"完全独立。** 用户可以：
- 按目录修完树之后删掉目录记录 → 第一行「未加载」+ 第二行「目录校验后」
- 只获取目录却从不使用 → 第一行有目录 + 第二行「本地识别」

**用 `RecognitionSource` 现推是不行的**：工作台手工编辑会把它覆盖成 `manual`，
于是一棵 reference-derived 树上 `reference` 标记会越来越少甚至消失，而它其实还是目录树。

### 4.5 落地模式（LandingMode）

`TreeOnly`（只改成品）或 `EditSource`（同时改原文）。**默认是 `EditSource`**
（`AppSettingsStore.cs:73` → `DefaultRepairLandingMode = RepairLandingMode.EditSource`）。

选择位置**在修复确认窗里**，不在工具栏 —— 因为用户要在看到"将改动原文的 N 行"的同时做这个决定。

### 4.6 三个事实（工作台顶部）

`ChapterRepairContextSnapshot` 回答三个**不能互相推导**的问题：

```
参考目录    未加载 / 已保存（未重新选择）/ 已选用 · N 章
当前章节树  本地识别 / 目录校验后 / 来源未知（旧项目）
原文版本    正常 / 已在程序之外变化
```

**这三行不是可有可无的装饰。** 它们替换掉了原来那个单一的「修复依据」开关，
因为一个开关只能说出其中一件，于是总有一半是假的。

---

## 5. 修复链路：从按钮到写文件

### 5.1 入口清单（按钮 → 方法 → 结果）

**工作台工具栏：**

| 按钮 | 方法 | 做什么 |
|---|---|---|
| 「检查并生成建议」/「按目录检查并生成建议」 | `AutoRepair_Click` → `RunRepairReviewAsync()` | 读**磁盘上已保存的**目录（不联网），生成方案，开确认窗 |
| 「选择参考目录…」 | `CatalogAssist_Click` | 打开目录面板（粘贴网址 / 导入 TXT / 从书源抓取） |
| 「识别设置…」 | `RecognitionSettings_Click` | 识别规则 |
| 「撤销」/「重做」 | `Undo_Click` / `Redo_Click` | 树编辑历史 |
| 「专项处理 ▾」 | `WorkbenchMore_Click` | 重建结构 / 清理重复 / 规范化数字标题 / 批量补建跳章 |

**提醒卡片：**

| 按钮 | 走哪个 intent |
|---|---|
| 「检查并清理本组…」 | `CleanDuplicateTitles` |
| 「对比正文，选择保留…」 | `CompareDuplicateBodies` |
| 「到原文定位并核对…」 | `NavigateToSource` |
| 「从选中行建立章节」 | `PromoteSelectedLine` |
| 「核对层级处理…」 | `ReviewHierarchy` |
| 「修改为：xxx」 | `ApplySuggestedTitle` |
| 「预览结构整理…」 | `RecoverStructure` |
| 「识别并规范标题」 | `ReRecognizeAsNumeric` |
| 「获取参考目录并对照…」 | `AcquireCatalog` |
| 「按目录补建章节…」 | `AdoptCatalogChapters` |

**⚠️ 按钮文案与执行分支读的是同一个 `AdviceFor(...)`。** 这一点是 v1.61.0 之前修的：
在那之前，"文案"和"执行"是两套独立的 code switch，结果是四处「说一套做一套」
（例如按钮写「编辑成品标题…」而点下去打开正文对比窗）。**改动时不要把它们重新拆开。**

### 5.2 planning 的关键规则

**`SelfRepairPlanner` 的铁律**：只发不依赖外部目录就能判定的动作。
要靠目录才能判定的一律不发：某章是不是*缺失*、某段正文该不该*立成标题*、某一章是不是真的*多余*。

**同名重复的证据模型**（这块逻辑反复改过，读之前务必理解）：
- "标题相同"只负责**发现候选**，不说明哪一份多余
- `Recommended = true` 只在**结构证据成立**时（恰好一份有正文）
- 跨卷同名、跨编号重启同名 → **不自动移除**（`DuplicateEvidenceTests` 的 A/B/C/D1/D2 钉住）

### 5.3 compile 的三个输入

```csharp
RepairPlanCompiler.Compile(plan, decisions, mode)
```

- `decisions`：用户勾了哪些（`RepairDecision`，有稳定 ID，不是 index）
- `mode`：`TreeOnly` / `EditSource`
- 输出 `RepairCompilation`：包含 `SourcePatch`（要改原文的哪些行）

**纯函数，没有副作用**，所以它有一整套测试。

### 5.4 写 TXT 的路径

旧版文档说"全软件只有三条路径会写用户的 TXT"。核实后的准确表述是：

| 路径 | 位置（已核实） | 说明 |
|---|---|---|
| 修复流程 | `ChapterRepairApplier.ApplyAsync`（`:127` / `:250`）→ `:313 new SourceEditTransaction(...)` → `SourceEditTransaction.cs:153` 原子写 | 走完整事务 |
| 启动时补完中断的事务 | `SourceEditRecovery.RecoverAllAsync`（`SourceEditTransaction.cs:503`） | **唯一无 UI 的路径**；调用处 `MainWindow.xaml.cs:719`、`SourceBackupWindow.cs:300` |
| 自更新落地脚本 | `UpdateInstaller.cs:187` | 改的是 exe，不是 TXT |

**`File.WriteAllText` 在仓库里还有很多处，但它们写的是别的东西**：移除清单
（`RepairIntegrity.cs:306`）、备份清单（`SourceBackupInventory.cs:213`）、目录缓存
（`CatalogCache.cs:59`）、验收报告（`ArtifactValidation.cs:396`）、设置与快照、输出成品
（`ChapterEditingDocument.cs:177`）。**判断标准是"这个路径写的是不是 `document.SourcePath`"。**
如果你发现某处直接对 `SourcePath` 写而绕开了事务，那是一个 bug。

### 5.5 事务与备份

`EditSource` 走完整事务：备份 → 原子替换 → journal → 恢复能力检查。
`TreeOnly` **没有事务**（因为不写文件），页面上必须如实说明这一点。

---

## 6. 界面

### 6.1 主窗口（`MainWindow`，4891 行）

四个工作阶段：**导入书稿 → 检查与修复 → 制作设置 → 转换与验收**。

- 左侧导航是**阶段**，不是功能模块
- 书稿列表 + 右侧检查器
- 「查看并处理」→ 打开章节工作台或报告窗口

### 6.2 章节工作台（`ChapterEditorWindow`）

```
标题区：书名 · N 章 · M 卷 · 最深 K 级
工具栏：[主按钮] [选择参考目录…] [识别设置…] [撤销] [重做] [专项处理 ▾]
         └ 下面一行小字说"依据：…"
状态条：参考目录 / 当前章节树 / 原文版本
下一步：这本书有 N 处提醒，按「要不要参考目录」分成三类
        9 需要参考目录 · 1 本地就能修 · 34 需要你核对
        [先做这一步的按钮]
次级：撤销 / 重做 / 识别设置 / 专项处理
主体：左＝章节树或提醒列表   右＝选中提醒的详情卡
底部：原文预览 + 「应用并返回」
```

**「下一步」那一块是 v1.61.0 加的**，它回答"我该按哪个按钮"。
它的数字来自 `IssueResolutionPolicy.Summarize`，与每条提醒的标签**同源**。

**底栏（`SaveStateText`）在 v1.61.2 改成说真话。** 控件的 XAML 初始值在
`ChapterEditorWindow.xaml:340`，但**运行时文案由 `ChapterWorkbench.cs:86-97 UpdateSaveState()`
按落地模式动态写出**：

```
本次修复：改原文（会写回 TXT，改动前自动备份） · 已保存；返回后自动保存…
本次修复：只改章节树（不碰 TXT） · 已保存；返回后自动保存…
```

它记的是**"刚刚真的做了什么"**，不是"设置里写着什么" —— 两者会分叉（确认窗里可以临时改），
而底栏该说的是前者。落地模式缓存在每次真的应用修复后被刷成实际用的那个
（`ChapterAutoRepairPanel.cs:241` 刷成 `TreeOnly`、`:253` 刷成 `EditSource`）。

> ⚠️ **本文档旧版在这里有一条错误的定性**：它说"工作台底栏是 XAML 静态文字，执行一次改原文修复后
> 它仍会写「不变」"。**那是错的** —— 底栏从来不是静态文字，它的文案由 `UpdateSaveState()` 生成。
> 真正的问题在 v1.61.2 之前是那段 C# 无条件写着「原始 TXT 不变」，现已修，并由
> `WorkbenchFooterTruthTests.cs:38`（`Assert.DoesNotContain("原始 TXT 不变", footer)`）锁死。

### 6.3 修复确认窗（`RepairReviewWindow`，1154 行）

**这是唯一提交点。** 左栏是改动清单（逐项可勾），右栏是选中项详述，底部是落地模式单选。

**不要在这里加第二个提交按钮。**

### 6.4 目录面板（`ChapterCatalogPanel`）

粘贴网址 / 导入目录 TXT / 从书源抓取。**候选先只展示、不落盘**，
用户按「记住这份目录」才写入记录。

**⚠️ 这里有两句话被修过，别改回去**（原文就在 `ChapterCatalogPanel.cs:40-44`）：
- 不能说"原始 TXT 不会被修改" —— 下一步的确认窗支持改原文
- 不能说"选中即应用" —— **选中不等于确认**

### 6.5 🔴 界面重做轨道的前置阻塞：窗口测试跑在没有真实资源的环境里

**这一节是 v1.61.2 明确留下的最大一块工作，也是整条"界面重做"轨道的前置条件。
要动界面，先读这一节。**

#### 现象

工作台开始用 `Style="{StaticResource Chip}"`（来自新的 `Controls.xaml`）之后，
`ChapterBatchTests` 21 条全红、`SampleBookRepairFlowTests` 红、`MainWindowLayoutTests` 红 3 条，报错都是：

```
XamlParseException: 无法找到名为"Chip"的资源。资源名称区分大小写。
```

#### 机理（已查清）

`DynamicResource` 缺键**不抛**，只留 null，界面照开、那块没颜色；`StaticResource` 缺键**会抛**。
所以这个洞一直躲着，直到有人用了 `StaticResource` 才炸，而且**按执行顺序偶发** ——
`Application.Current` 是**进程级静态量**，谁先创建就决定了整轮测试有没有应用级资源。

**而且建裸 `new Application()` 不载 `App.xaml`**，于是合并字典（`Controls.xaml`）、
那批画刷、`PrimaryButton` 全都不存在。

#### ✅ 决定性判据：Type 键有主题字典兜底，字符串键没有

谜题是"同一个窗口里 `BasedOn="{StaticResource {x:Type Button}}"` 能解析，而 `Chip` 不能"。
一度据此以为"那个宿主**有** App.xaml 资源" —— **那是过度更正**。

真正的差别在**键的类型**：WPF 在 `FindResource` 里对 `Type` 键有一条**主题字典兜底**
（取该类型的默认样式），字符串键没有。所以 `{x:Type Button}` 在没有 App.xaml 的环境里照样解析得到，
任何自定义字符串键在那里都会抛。

判据测试：`ControlStyleContractTests.A_type_key_falls_back_to_the_theme_but_a_string_key_does_not`（`:161`）。
它故意建裸 `Application` 复现那类宿主，然后断言 `TryFindResource(typeof(Button))` 非空、
`TryFindResource("Chip")` 为 null。

> **这条测试别删。** 它把"宿主到底有没有应用级资源"从猜测变成了可执行事实。
> 注意它用 `createdBare` 守卫（`:173`）—— 因为 `Application.Current` 是进程级静态量，
> 同类里另一条测试（载入真实 App 那条）可能已经先跑过。第一版写成无条件断言，于是它**按执行顺序偶发红**。

#### 宿主其实分四类（六处），不是"每个文件各写各的引导"

核实后的准确情况（这一点旧文档说得不准）：

| 类 | 宿主 | 位置 | 建什么 |
|---|---|---|---|
| **① 建裸 `Application`** | `WorkbenchHarness.OnStaThread` | `WorkbenchHarness.cs:73` | 裸 `new Application()`。**集中的一份**，但只有 4 个测试类用它（`WorkbenchContextBarTests` / `WorkbenchFooterTruthTests` / `WorkbenchForgetCatalogTests` / `ControlStyleContractTests`） |
| **① 建裸 `Application`** | `SampleBookRepairFlowTests` | `:76` | 裸 `new Application()`，**另写的一份** |
| **② 常规不建，截图时建真实 `App`** | `ChapterBatchTests.Run` | `:545-552` | **自建 STA 线程，常规不建 Application**；只有截图场景（`EASYPUB_REVIEW_CAPTURE` 非空）才建**真实 `App`** |
| **② 常规不建，截图时建真实 `App`** | `MainWindowLayoutTests.RunInWindow` | `:2199-2220` | 同上，截图场景由 **12 个** `EASYPUB_*_CAPTURE*` 环境变量决定 |
| **③ 刻意什么都不建** | `SampleBookBackupWindowTests` | `:98-103` | **刻意不建** —— 见下 |
| **④ 测试自己建** | `ControlStyleContractTests` | `:71,127,173` | 自己建（那正是它要测的东西） |

**所以第 ② 类（`ChapterBatchTests` / `MainWindowLayoutTests`）在常规跑时根本没有 `Application`**，
它们能跑过是靠 Type 键的主题兜底。**这正是"按执行顺序偶发"的全部来源** ——
第 ① 类谁先跑，就决定了整轮测试里 `Application.Current` 是不是 null。

**`SampleBookBackupWindowTests.cs:98-103` 有一条血泪注释**，它的主张是"**不要**建 Application"：

> 自建 STA 线程，且**不**创建 Application。
> 这套测试里 Application 是进程级单例，谁先建谁独占，线程一结束它就属于一条死线程 ——
> 之后所有靠 Application.Current 解析资源或找窗口的测试会一起倒。曾经在这里加过
> `if (Application.Current is null) _ = new Application();`，整份套件从偶发 1 个失败变成 **90 个**。
> 备份窗口不依赖 Application 的资源，所以这里什么都不建。

> ⚠️ 本文档旧版引用这条注释来论证"必须统一建真实 `App`" —— **引反了**。这条注释说的是建了会更糟。

#### ✅ 已经查清的一半（`Controls.xaml` 的合并本身是好的）

`ControlStyleContractTests.Loading_the_real_app_makes_the_new_styles_findable`（`:122`）真的建
`App` + `InitializeComponent()`，再逐个查那 25 个样式键 —— **全部解析得到**。

| | 结论 |
|---|---|
| 真实应用 | ✅ 合并生效，25 个样式全部可用（已用测试钉住） |
| `ChapterBatchTests` 那类宿主 | ❌ 构造窗口时没有真实的 `App`，所以找不到 `Chip` |

它和 `App_merges_the_controls_dictionary`（`:103`）的区别很实际：后者只证明 `App.xaml` 文本里有
`Source="Controls.xaml"` 这句话，前者证明**运行时真的合进来了**（相对 URI 会被解析，文本对不代表找得到）。

#### ❌ 修法已定但**未做**：试过，反而多红 6 条，这一层**未查清**

修法是"让所有窗口测试宿主统一建真实 `App`"。试过，**结果多红 6 条**。

已查到的原因：**真实 `App` 会带上 `Window` 隐式样式里的 `EventSetter`**
（`App.xaml:126` `<EventSetter Event="Loaded" Handler="Window_Loaded"/>`），
而那个处理器（`App.xaml.cs:64-67`）会做**真事**：

```csharp
private void Window_Loaded(object sender, RoutedEventArgs e)
{
    if (sender is Window window) ThemeManager.ApplyWindowChrome(window);
}
```

于是每个测试窗口突然都被套上了窗口镶边主题，改变了与外观/尺寸相关的断言。

**这一层没有查清，所以没有硬塞。** 具体没查清的是：
- 多红的 6 条**具体是哪 6 条**、各自断的是什么 —— 未记录（**未实测**）
- `ApplyWindowChrome` 在离屏窗口（`Left = -4000`、`Opacity = 0`）上的实际副作用 —— **未实测**
- 上一轮"25 条变红"里，引导那一半与 `StaticResource Chip` 那一半**各自贡献多少** ——
  上一轮**同时**改了两件事，红掉的 25 条里至少 `ChapterBatchTests` 那 21 条是**后者**造成的
  （报错就是"无法找到 Chip"）。**引导那一半从来没有被单独验证过。**

#### 从哪里接着查（按顺序）

1. **只改引导，XAML 一行不动**：把四类宿主收成**一个**入口，全部建真实 `App`
   （`new App(); app.InitializeComponent();`），并让"已有 Application 但不是 `App`"变成**响亮失败**。
   **先证明这一半是安全的** —— 记录到底红几条、红哪些。
2. 若红，逐条判断是 `ApplyWindowChrome` 造成的还是别的原因。可用的手段：
   在测试宿主里**不**走 `Loaded`（例如不 `Show()` 而只构造 + `UpdateLayout()` 是否可行 —— **未实测**），
   或给 `Window_Loaded` 一个"测试模式"短路（要评估是否污染产品代码）。
3. 绿了之后，**再**让工作台用 `Controls.xaml` 的样式。
4. 补一条断言：资源字典里的键在窗口里真的解析得出来（`ControlStyleContractTests` 已有一半）。

**在 1–3 做完之前，界面轨道不要用新的 `StaticResource` 样式** ——
要么先用 `DynamicResource`（缺键不抛，但运行时也不会报），要么先把基础设施修对。

**别用把资源塞进 `Window.Resources` 的办法绕过去**：那会让同一个字典在每个窗口各载一份，
而且掩盖"测试环境没有真实资源"这件事。

#### 附：同一文件里两段自相矛盾的注释（别被误导）

`ControlStyleContractTests.cs:118` 的注释写"说明那个宿主**有** App.xaml 的资源，
缺的正是被合并进来的这一份" —— 这句话在**同一个文件后面**（`:142-158` 那条判据测试）
已被推翻。**读这个仓库的注释时要注意它们可能比代码旧。**

同类例子：`ChapterCountContractTests.cs:30-32` 至今还写着"**这个测试当前应当是红的**"，
而它现在 **12 条全绿**（见 §8.1.5）。

---

## 7. 数据与存储

### 7.1 位置

| 内容 | 路径 | 覆盖方式 |
|---|---|---|
| 应用设置 | `%LOCALAPPDATA%\EasyPub Modern\app-settings.json` | `EASYPUB_APP_SETTINGS_PATH` |
| 每本书的目录记录 | `…\Catalogs\<SourceSha256>.json` | `EASYPUB_REFERENCE_CATALOGS_PATH` |
| 原文备份 | `…\SourceBackups\` | `EASYPUB_SOURCE_BACKUP_ROOT` |
| 修复 journal | 备份目录下 | — |
| **交互日志** | `…\interaction.jsonl` | `EASYPUB_INTERACTION_LOG` |
| 恢复快照 | — | `EASYPUB_RECOVERY_PATH` |

**注意**：目录记录是**按原文 SHA-256** 索引的。
`File.Copy` 出来的副本 SHA 与原件**相同**，所以副本会命中同一份记录 ——
测试里要隔离的话，**必须重定向存储根，不能靠复制文件**
（`WorkbenchHarness.IsolateCatalogStore()` 就是干这个的）。

### 7.2 项目文件（`.easypubproj`）

保存 `ChapterTreePlan`，含 `Provenance`、`ConfirmedReviews`、`ReferenceCatalog`。
**provenance 与已确认提醒都是绑定在原文版本上的**，换一份原文就没有意义。

### 7.3 交互日志（v1.61.0 新增）

每行一条 JSON：

```json
{"Ts":"2026-09-18T16:30:12.345+08:00","Ms":1234,"Kind":"user","Action":"AutoRepairButton","Detail":{"text":"检查并生成建议","window":"章节工作台"}}
{"Ts":"...","Ms":1300,"Kind":"decision","Action":"选择依据","Detail":{"来源":"磁盘上保存的目录","有目录":true,"章数":480}}
{"Ts":"...","Ms":2100,"Kind":"outcome","Action":"生成方案","Detail":{"有目录":true,"动作数":191}}
```

- `Kind`：`user`（按钮点击）/ `decision`（代码在岔路口选了哪条）/ `outcome`（结果）
- **按钮点击靠 `EventManager.RegisterClassHandler` 统一钩住**，不在每个处理器里插桩 ——
  这个软件一半按钮是运行时建出来的，逐个插桩既容易漏又会被后续改动绕过
- **写失败静默放弃**，绝不影响产品行为
- **没有单元测试**（实测：tests 下搜 `InteractionLog` 0 命中）。它是纯副作用设施，
  加一个依赖环境变量、又与并行测试抢全局状态的测试比没有更糟。**它的正确性目前只由"产品不崩"保证。**

**它是最有用的诊断工具。** 用户描述"我点了 X 结果 Y"时，读日志比问问题快得多。

---

## 8. 已知问题

> **本章的组织原则：一个事实只写一遍。**
> 章节数那件事（旧版的 §8.1 / §8.4 / §8.5）合并为 **§8.1** 一处权威叙述，
> 别处只放指针。实测数据表集中在 §8.1 与 §8.2，每张表标明口径。

### 8.1 章节数：唯一权威叙述

**这是这个项目历史上被误解最深的一件事。** 结论先行：

> **识别路径本身没有缺陷。三个数里只有一个是真的"错"，而错的不是识别，是沉默。**
> 同一个文件、同一份识别配置下，**章节树与预检始终一致**（6 组配置全绿）。
> 唯一残留的差异是"没保存章节树时转换走 `LegacyTextParser`"，那是**有意保留的 v1.50 兼容行为**，
> 现在**不再沉默**。

#### 8.1.1 先摆平口径（这一步不做，量到的是记账方式，不是缺陷）

三条路径各有各的"前置条目"：

| 路径 | 它的"前置" |
|---|---|
| 章节树 `Entries` | 含一个合成的「序」，`TitleLineNumber` 为 `null` |
| 预检 `ChapterCandidateCount` | **只数有标题行号的条目**，天然不含前置 |
| `LegacyTextParser` | 无条件在开头塞一个「序」（已核实：`LegacyTextParser.cs:33` → `new("序", hierarchy.Enabled ? 1 : 2)`） |

所以**统一口径 = 由标题行识别出来的章节数**（样书此口径下前置恰好 1 条）。

**口径没摆平就对比，量到的是记账方式，不是缺陷。** 本文档前两版就是这么错的（见 §8.1.4）。

#### 8.1.2 实测矩阵（2026-09-18，样书，已摆平口径）

**口径：由标题行识别出的条目数（不含合成的「序」）。**

**⚠️ 这张表是 P0-4 之前（2026-09-18 上午）的实测值**，六行同一次测出，所以可以直接横向比较：

| 章节正则 | 分层 | Level 正则 | 章节树 | 预检 | 无树转换 |
|---|---|---|---|---|---|
| 现代默认 | **关** | — | **467** | **467 ✓** | **567 ✗** |
| 现代默认 | **开** | 新 | **567** | **567 ✓** | 567 ✓ |
| 旧 `LegacyPattern` | **关** | — | **460** | **460 ✓** | **560 ✗** |
| 旧 `LegacyPattern` | **开** | 旧 | **560** | **560 ✓** | 560 ✓ |
| 现代默认 | 开 | 旧 | 567 | — | 567 |
| 旧 `LegacyPattern` | 开 | 新 | 567 | — | 567 |

修后的值见 §8.1.3 的「P0-4 实测结果」表 —— **只重测了其中四行**（后两行的修后值
**未实测**，因为它们与那四行是同一类组合，但本文档不替它们推断）。

**这张表现在由 `tests/EasyPub.Core.Tests/ChapterCountContractTests.cs` 锁住，12 条全绿**
（6 + 2 + 1 + 2 + 1 个用例）。

**三个结论**：

1. **预检在全部四种配置下都与章节树一致 —— 预检路径从来没有缺陷。**
   走查时那个「560 项」与 Core 的 561 是同一份 document 的同一个数，**560 ≤ 561，完全自洽**。
2. **唯一真正的缺口曾是"分层关时无树转换多出 100 章"**（467 vs 567、460 vs 560），
   即 **`LegacyTextParser` 无论分层开关，行为都等价于"分层开"，即从不合并重复标题行**。
   这是**有意保留**的兼容行为，处理方式是"让它说出来"（P0-3）。
3. **Level 正则是第二条独立的识别通道**：旧正则 + 开 + 旧 Level = 460，旧正则 + 开 + 新 Level = 467（差 7）。
   **这 7 已查明，而且不是缺陷** —— 样书第一百零一章起有 7 章印成**裸数字标题**
   「一百零一章 戊101 断后险路」，没有「第」字：
   - 旧 `LegacyPattern` 要求首字符必须是 第 或 卷，**结构上就匹不上**；
   - 旧 Level2 要求 `第…章回`，也匹不上；
   - 现代 `HeadingSyntax.ChapterPattern` 的 `NumberPrefix` 里「第」是**可选**的，所以认得出
     （`HeadingSyntax.cs:19-24` 的注释："plenty of releases print 「八百三十章 赌徒」"）。

   **所以 Level 正则不只是"决定标题算第几层"，它还承担识别职责。**
   不要写「Level 正则不该改变章节数」这种契约 —— 那会让这 7 个真实章节变成必须修掉的"计数 bug"。
   现在锁的是这个能力本身（`The_modern_level_patterns_see_bare_numbered_chapters_the_legacy_pattern_cannot`）。

#### 8.1.3 两个独立的成因，必须分开看

**成因一：`?? ConversionOptions.LegacyDefault`（曾 9 处 / 5 个文件）—— ✅ P0-2 已修**

原来 `Options` 为 null 时静默回退到 `ChapterPattern = HeadingSyntax.LegacyPattern`
（2015 年的宽松正则：连「第一章回」都吃，也不排除"回头/回来/回事"）。
**但它单独只能把 468 变成 461 —— 要得到 561 还需要第二个成因。**

**现在**（已核实）：分析路径改走 `ConversionRequest.RequiredOptions`（`ConversionRequest.cs:30`，null 直接抛）：

- `ConversionPreflightInspector.cs:162` `var options = request.RequiredOptions;`
- `PublicationChangeReceipt.cs:17` `var options = request.RequiredOptions;`
- `BookPreviewService.cs:51` 与 `:58`
- 只剩 **5 处** `?? ConversionOptions.LegacyDefault`，都在两个兼容出口：
  `LegacyEpubWriter.cs:34`、`LegacyMobiWriter.cs:26,43,90,128` ——
  它们的 "Legacy" 指**输出格式兼容 v1.50**，是既有契约，每处都有注释说明为什么。

**成因二：分层识别拆开重复标题行 —— ✅ P0-4 已修**

样书第六卷每章标题连写两次（100 章）。分层**关**时 `SkipRepeatedHeadingRun` 把它们合并成一章；
分层**开**时这 100 章各被拆成「正文 0 行的空章 + 带正文的同名章」。

**所以 561 = 成因一（旧正则）+ 成因二（分层开）+ 旧 Level 正则，三者同时成立。**
这就是**组合效应** —— 逐字段替换永远测不到它（见 §8.1.4）。

**P0-4 的修法**（`ChapterTree.cs`）：`SkipRepeatedHeadingRun` 原先只作用于**正文范围**
（`bodyStart`），不管**条目列表**。修法是记下上一个被接受标题的重复运行段末端：

```csharp
var repeatedRunEnd = 0;
foreach (var line in sourceLines)
{
    if (line.LineNumber < repeatedRunEnd) continue;   // ← 新增
    ...
    headings.Add(...);
    repeatedRunEnd = SkipRepeatedHeadingRun(sourceLines, line.LineNumber);   // ← 新增
}
```

**P0-4 实测结果**（口径 = 由标题行识别出的条目数）：

| 配置 | 修前 | 修后 |
|---|---|---|
| 现代默认 + 分层关 | 467 | 467 |
| 现代默认 + 分层开 | **567** | **467** ✅ |
| 旧正则 + 分层关 | 460 | 460 |
| 旧正则 + 分层开 | **560** | **460** ✅（旧 Level） |

#### 8.1.4 ⚠️ 本文档在这里写错过三次（过程比结论有用）

1. **写「Core 层面任何参数组合都给不出 560」—— 不成立。** 当时只做了**逐字段替换**
   （一次换一个字段），**组合效应根本没测到**。这是方法错误：逐字段扫描只能证明
   "单个字段不改变结果"，不能证明"任何组合都不改变"。
2. **算错了口径**：拿 `Entries.Count`（561）去对界面上的 560，而界面数的是
   **非前置且有标题行号**的条目。**561 和 560 本来就不该相等。**
3. **把两个不同配置当成同一个配置在比** —— 由此写下"560 与 468 不可能来自同一个
   document"。真相是：走查那次的 560 来自「旧正则 + 分层开 + 旧 Level」，
   而 468 来自「现代默认 + 分层关」。**摆平口径后 560 与 561 完全自洽，预检根本没有缺陷。**

**三次错的共同形状：先有了一个数，就把它当成"系统应该给出的唯一答案"，
然后拿别的配置、别的口径下的数去证明它错了。**

> **教训：报差异之前先确认两边是同一份输入、同一个口径。**
> 而"逐字段替换"证明不了"组合效应"。

#### 8.1.5 修复计划与状态（P0-1 … P2）

沿用本文档的边界：**不碰对齐算法、补丁编译、事务语义**；每步**先探针后断言**。

| 阶段 | 做什么 | 验收 | 状态 |
|---|---|---|---|
| **P0-1** | **契约测试**：同一本书、同一 `Options` 下三条路径章节数一致 | **先红**，并作为后续基线 | ✅ **已完成**（`ChapterCountContractTests.cs`，**12 条全绿**） |
| **P0-2** | 9 处 `?? LegacyDefault` 收敛成一个入口；分析路径 null 直接抛 | 契约测试里"旧正则"那几行消失 | ✅ **已完成**（余 5 处在两个兼容出口，刻意保留） |
| **P0-3** | 无树转换不合并重复标题行。**决定：不改行为，只让它说出来** | 差异一旦存在，预检必须报出**实测**的两个数 | ✅ **已完成**（"说出来"版） |
| **P0-4** | `Load` 的分层路径补重复行合并 —— 纯 bug，不涉及兼容 | 分层开/关章数一致 | ✅ **已完成** |
| **P0-5** | 升级表把 `ChapterPattern` 的旧值也纳入（`UpgradeSupersededChapterPattern`，走遍 `LastProfile` + 每个预设） | 老设置机器 = 新装：**461 → 468**，实测 | ✅ **已完成** |
| **P1-1** | §8.2 方案 A：用户正则能匹配但读不出编号时，明确列出"不可用功能" | 样本上表格每行有实测值 | ✅ **已完成**（`RecognitionConsistency`） |
| **P1-2** | 界面显示**当前生效的识别配置来源**（默认 / 本书 / 全局 / 导入 config.xml） | 能解释任意一个章节数 | ⏸ **未开始** —— 要改 Desktop 界面，见下 |
| **P2** | 清理 §8.4 里那些小问题 | — | 🔄 1 条查清（非缺陷）；余 8 项未做 |

**⚠️ 注意"完成"不等于"没有已知缺口"。** P0-2 的四条分析路径会抛了，但**产品层面的两个 `Legacy*Writer`
出口仍然静默回退** —— 那是刻意的（输出格式兼容），不是漏改。

#### P0-3 实测记录：不推断差异，只实测差异

**产品决定**：无树转换的 v1.50 原样行为**保留不改**。理由是它会让老用户的成品目录结构
发生变化，属于"用户主权"，不能由重构顺手改掉。**缺陷是"沉默"而不是"错"** —— 所以要修的是沉默。

**做法**：`ConversionPreflightInspector` 在 `request.ChapterTree is null` 时，
用**转换将要用的同一份 options、同一条无树路径真的跑一遍**，把两个数摆出来：

```csharp
if (request.ChapterTree is null)
{
    var withoutTree = await LegacyTextParser.ParseAsync(request.InputPath, options, null, token);
    var withoutTreeCount = withoutTree.Count - 1;   // 减 1：开头「序」是合成的前置条目
    var emptyBodies = withoutTree.Skip(1).Count(chapter => chapter.Paragraphs.Count == 0);
    if (withoutTreeCount != candidateCount) { /* 报 repeated_heading_split */ }
}
```

**关键设计：不推断差异，只实测差异。** 走的是转换本身要走的那个入口、同一份 `options`，
所以报出来的数**不可能与实际转换漂移**。这比"用正则再算一遍哪里重复"更省代码也更可靠 ——
后一种做法会引入第二套识别逻辑，正是这个项目已经吃过大亏的模式。

**提醒文案**（`PreflightIssueCodes.RepeatedHeadingSplit`，`ConversionPreflightInspector.cs:67`）：

> 工作台识别出 467 章，但直接转换会产出 567 章（多 100 章，其中 100 章正文是空的）。
> 两条路径的识别规则不同：没保存章节树时走 v1.50 原样识别，且不合并连着印两遍的标题行。
> 这是为兼容旧成品保留的行为。要按工作台那份转换，请先在工作台保存章节树。

按这个 CTA 是「**在工作台保存章节树**」（`PreflightCta.cs:24`），
不是 `ReadinessEvaluator.ActionLabel` 的通用兜底「处理章节」—— 那个兜底正是走查时被抱怨的措辞。

**为什么不进 `IssueResolutionPolicy.Definitions`**：那张 16 码表是**章节树里可处理的问题**的词汇表，
工作台会为每一项渲染处理入口。这条码没有"在树里处理"的动作（出口只有"保存章节树"或"照旧转换"），
混进去会让工作台渲染一个不存在的入口。

**契约测试换了，而不是删了。** 原 `All_three_paths_count_the_same_chapters` 要求三条路径的数完全相等；
在产品决定之后这个契约是**错的**（它会逼实现去掉兼容行为）。换成三条更硬的：

| 测什么 | 为什么更硬 |
|---|---|
| 工作台 ≡ 预检（6 组配置） | 这两个数出现在同一批界面上，必须一致 |
| 有差异时必须报出**实测的两个数** | 从"数必须相等"变成"差异必须可见" |
| 保存章节树后不许再报 | 一条永远出现的提醒等于没有提醒 |

#### ⚠️ P0-3 引入过一次性能回归，已修（务必读完）

**唯一那条失败是 `LongTextPerformanceTests`** —— 它给 28 MB 长篇设了墙钟预算
（转换前检查 < 400 ms）。它**单独跑通过，全量跑会因机器负载超时**。

但下面这件事比那条测试本身重要：

**第一版 P0-3 无条件跑一整条无树转换路径**（读文件 + 解码 + 文本清理 + 逐行识别）。
实测在 28 MB / 8000 章上把转换前检查从 400 ms 以内顶到 **545 ms** ——
那条测试**单独跑也红**。

**我当时的判断是"这条测试本来就是红的"，并且差点把这个说法写进发布说明。**
它是错的。正确做法只要三步：把新加的代码临时关掉 → 跑一次 → 对比。

- 关掉（`false && ...`）：**通过**
- 打开：**545 ms，红**

**修法：精确闸门。** `ChapterTreeDocument.RepeatedHeadingLinesSkipped`（`ChapterTree.cs:96`）
顺手记下识别时跳过了几行重复标题（`SkipRepeatedHeadingRun` 本来每个标题都要调一次），
为 0 就跳过那次昂贵实测。零额外扫描，单独跑恢复通过。

**闸门的第一版也是错的**，记下来免得再犯：

| 版本 | 判据 | 结果 |
|---|---|---|
| v1（错） | "有没有相邻的完全相同的非空行" | 长篇里重复正文行到处都是，性能测试文件 28 行正文压根同一句 → **闸门永远开着**，545 → 419 ms，仍红 |
| v2（对） | "识别时有没有跳过重复**标题**行" | 顺手就有，零成本，通过 |

**教训：判据必须落在"标题"上，不能落在"行"上。**

#### P0-2 实测记录

**做法**：`ConversionRequest` 新增 `RequiredOptions`，null 就抛，报错里带上
**是哪一本书**以及**怎么改成旧语义**（`ConversionRequest.cs:30-36`）。

**先探针后断言**：动之前先量了两件事 ——

1. 生产代码里 **77 处** `new ConversionRequest(` **没有一处省略 Options**，
   所以这个抛只会在程序员出错时触发，碰不到用户。
2. 测试里有 **39 处**省略 Options（11 个文件）。逐个看了它们的**方法名**：
   `Batch_conversion_preserves_input_order`、`Inspect_runs_once_and_reuses_the_completed_report`、
   `Epub_input_cannot_be_written_as_epub` …… **没有一条是关于识别正则的**，它们只是需要一个 request 对象。
   所以统一补 `Options: new ConversionOptions()`（现代默认），而不是 `LegacyDefault` ——
   后者会把"这些测试测的是旧识别"这个**假事实**固化下来。

**新增契约测试** `ConversionRequestOptionsContractTests`（5 条）：存在则原样返回 /
缺失则抛且报错含书名与 `LegacyDefault` / 预检拒绝猜 / 分析拒绝猜 / 显式要旧语义仍然可用。

**⚠️ 这次批量改测试时我自己弄坏过 21 个文件（1964 行被删）。**
脚本在"这个构造点已经有 Options、跳过"的分支里**忘记把跳过的原文补回去**。
`git checkout -- tests` 还原后修好那一行重跑，并加了防线：
**检查 `git diff --numstat` 里每个文件是否 N 增 N 删** —— 只替换不删除时必然如此，
出现 `\d+ 0` 就是有内容被吃掉了。

#### P0-5 记录：**实测之后，结论反过来**（2026-09-18）

**原计划**是"把 `ChapterPattern` 的旧值也纳入升级表"。动手之前先跑了一次探针（样书，`Entries.Count`）：

| # | 场景 | 条目 | 非前置 |
|---|---|---|---|
| 1 | 新装默认（正则 null + 分层**关**） | **468** | 467 |
| 2 | 新装默认 + 分层开 | **468** | 467 |
| 3 | 老机器 · 升级前（旧正则 + 分层开 + 旧 Level） | 461 | 460 |
| 4 | 老机器 · **升级后**（旧正则 + 分层开 + **新 Level**） | **468** | 467 |
| 5 | 老机器 · 若分层**关**（旧正则 + 分层关） | **461** | 460 |
| 6 | 旧正则 + 分层开 + 正则也升级成 null | 468 | 467 |

**两个结论，都不是原计划假设的那样：**

1. **常见情形已经被 v1.61.0 修好了。** 第 4 行说明：光靠 **Level 正则升级**，
   "旧正则 + 分层开"的老机器就得到 468，与新装**完全一致**。
2. **剩下的是窄得多的一个口子**：第 5 行 —— **分层关**时 Level 正则根本不被读取
   （`TocHierarchyOptions.Enabled` 默认 false），所以升级 Level 正则**不起作用**，仍是 461。
   而这**恰恰是新装的默认状态**（分层默认关），所以这个口子是现实的：
   老机器若从没开过分层，会一直漏掉那 7 个裸数字章节 —— 它们会被并进上一章的正文。

**中间我写过一版"不能静默升级，因为用户可能特意选了原版兼容"，当天就被自己推翻了。**
去查"用户怎么特意选旧正则"，结果那条路**不存在**：

| 我当时的假设 | 实际 |
|---|---|
| "原版兼容"会让用户存下旧正则 | `ConversionMode.OriginalCompatible` 讲的是**版式** —— 字号 110% / 行高 120% / 段间距 0.6em / 边距 0/0/3/3px / 首行 0em / 全角空格 ×2。**一个字都没提章节正则**（`MainWindow.UpdateModeDescription`） |
| 用户可能特意把章节正则写成旧默认 | 全代码库里产生 `HeadingSyntax.LegacyPattern` 的地方**只有 `ConversionOptions.LegacyDefault` 一处**（现在是 5 处 `??` 兜底），而界面上的章节正则只有**一个自由输入框**，用户真改过就不会恰好等于那一长串 |

**最终做法（就是原计划）**：`ConversionOptions.UpgradeSupersededChapterPattern`
（`ConversionRequest.cs:61`）把 `== HeadingSyntax.LegacyPattern` 的章节正则升成 `null`。
关键细节：**章节正则不在 `EasyPubAppSettings` 上，而在各个 `ConversionProfile` 里**
（`LastProfile` + 每个预设各一份），所以 `UpgradeSupersededPatterns`
（`AppSettingsStore.cs:151`）必须逐个走一遍 —— 只升全局那份等于没升。仍然只升内存、不写回文件。

**实测收益**（样书，分层关 = 新装的默认状态）：

| | 条目 |
|---|---|
| 老机器（旧正则 + 分层关） | **461** |
| 升级后 | **468** |
| 新装默认 | **468** |

差的 7 章正是那些没有「第」字的裸数字标题。**这一条不只是目录少 7 行 ——
旧规则认不出它们，它们会被并进上一章的正文，那是正文被改写。**

新增 `SupersededChapterPatternUpgradeTests`（7 条）：旧默认被升级 / 用户自己写的正则一个字符不动 /
每个预设都走到 / **老机器升级后与新装数出同一个数且差的正好是那 7 章** / 走真实设置文件端到端 /
只升内存不写回。

> 教训：**"这会侵犯用户主权"要先证明那个"用户选择"真的存在。**
> 我连着两次把"我自己想出来的风险"当成"已存在的路径"——
> 上一次是"这条测试本来就是红的"。两次都只要一次 grep 就能否掉。

#### 8.1.6 后果：现在的真实状态

**章节树与预检现在始终一致**（同一个 document，6 组配置全绿）。

**仍然存在的唯一差异**：没保存章节树时转换走 `LegacyTextParser`，它不合并重复标题行 ——
所以工作台说 467 章、直接转换产出 567 章。**这是有意保留的 v1.50 兼容行为**（2026-09-18 决定），
但它现在**不再沉默**：转换前检查会报出实测的两个数，并指向「在工作台保存章节树」。

**但"输入树不同"这件事仍然成立。** 用户的机器上是旧正则 + 分层开，所以：

- 「建议处理 155」基于 **560/561** 那棵树
- 「待核对 155 组」基于 **560/561** 那棵树
- 而样书基线测试锁的是 **467** 那棵树（44 组）
- 报告窗口那句「参考目录里有 **120** 章在章节树中没有」，描述的也是那棵旧正则 + 分层开的树

**排查时排除过的**（都是实测）：Core 完整修复后 `absent = 43 = outcome.Missing`（本来就一致）；
只应用部分动作 absent 只到 51。
**但这些只排除了"120 由修复逻辑造成"，没有排除"输入树不同" —— 恰恰是后者。**

> ⚠️ **「120 与 43 的差异」本身仍未查清**（见 §14）。上面这条只解释了
> "它为什么不是修复逻辑造成的"，没有给出 120 的完整推导。

#### 8.1.7 实测证据（交互日志，2026-09-18）

设 `EASYPUB_INTERACTION_LOG` 后导入样书，日志里那一条：

```
outcome · 识别书稿
      文件 = 缺陷样书-走查副本.txt
      条目 = 561                                    ← Entries.Count，含合成的「序」
      有标题行号 = 560                               ← 界面上那个「560 项」，两个数自洽
      行数 = 2503                                   ← 同一份文件，没读错
      分层 = True
      章正则 = ^\s*[第卷][0123456789…]*[章回部节集卷].*   ← ★ 不是「(默认)」
      级别2正则 = ^\s*第[…]+[章回].*                    ← ★ 还是旧值
```

**这组值 = 矩阵第三行**（旧正则 + 分层开 + 旧 Level）。

#### 8.1.8 ⚠️ 一个容易误判的陷阱（我自己误判过一次）

**`TocHierarchyOptions.Enabled` 的默认是 `false`。**

```csharp
var levelPatterns = hierarchyOptions.Enabled ? [Compile(Level1), Compile(Level2), Compile(Level3)] : [];
```

**所以传一条自定义 `Level2Pattern` 而不把 `Enabled` 设成 `true`，那条正则根本不会被使用。**
条目仍然来自 `ChapterEditingDocument.Candidates`（由 `chapterPattern` 决定），于是你会看到
"我改了正则但结果没变"，然后**误以为参数被忽略**。

**我上一版文档就是这么误判的** —— 传了一条绝不可能匹配的 `Level2Pattern`，得到 468 不变，
就写下了"`hierarchy` 参数对样书不起作用"。**真正的原因是那个 `Enabled` 默认为 false。**

> **教训**：看到"改了参数没反应"时，先确认**这个参数在当前配置下会不会被读到**，
> 再去怀疑它被忽略。矩阵里"分层开/关"两行的差异就是它生效的证据。

**这一条与 §8.2 是同一个病根：系统里对"什么算标题"有不止一个答案，而且哪个在生效并不显而易见。**

### 8.2 正则表达式是整个系统的输入，而系统对它的假设散落在多处

#### 它决定一切

```
用户配的正则（或默认正则）
   ↓  什么被识别为章节
树的 Entries
   ↓  有什么问题
诊断（gap / duplicate / order / unrecognized / …）
   ↓  生成什么动作
planner（补建 / 改名 / 移除 / 保留 / 加卷）
   ↓  改哪些行
源码补丁
   ↓
成品 EPUB / MOBI 的目录结构
```

**换一个正则，这条链上每一环的输出都会变。** 但链上有若干环**假设了某种标题形态**，
它们并不跟着正则走 —— 这是这个项目最根本的架构裂缝。

#### 正则的分布：上半可配，下半写死

| 在哪个环节 | 由谁决定 | 可配？ |
|---|---|---|
| 章节候选（什么行算标题） | `ConversionOptions.ChapterPattern` | ✅ 界面可配 |
| 分层（卷 / 章 / 节） | `TocHierarchyOptions.Level1/2/3Pattern` | ✅ 界面可配 |
| 数字章节 | `NumericHeadingPattern` + `RecognizeNumericHeadings` | ✅ 界面可配 |
| 章号纠错（久=九） | `HeadingNumberCorrections` | ✅ 界面可配 |
| **从标题里读出编号** | `HeadingSyntax.ChapterNumber`（`:31`） | ❌ **常量** |
| **去掉编号、留下标题文字** | `HeadingSyntax.Numbering`（`:33`） | ❌ **常量** |
| 跳章区间的漏识别候选 | `MissingChapterHeadings` 内置 | ❌ 常量 |
| 异常长章里的无编号标题 | `UnnumberedHeadings` 内置 | ❌ 常量 |
| 对齐时的相似度分级 | `ReferenceLocator` 内置 | ❌ 常量 |
| 旧版兼容正则 | `HeadingSyntax.LegacyPattern`（`:9`） | ❌ 常量（**而且会悄悄顶上来**，见 §8.1.3） |

**上半是"什么算标题"，可配；下半是"标题长什么样"，写死。**
**两者不一致时，没有任何检查会拦下来。**

#### 这条链上每一环对正则的依赖程度

| 环节 | 依赖什么 | 换正则后 |
|---|---|---|
| **识别** | 只用正则本身 | ✅ 完全跟着走 |
| **对齐**（目录 ↔ 树） | `ParseKey` → `Canonical` | ⚠️ **自洽** —— 两边用同一套解析，即使解析错了也仍然对得上 |
| **重复检测** | `ParseKey` → `Words` | ⚠️ 同上，自洽 |
| **缺章判定**（absent） | `ParseKey` → `Canonical` | ⚠️ 自洽，但**混入未对齐的节点后就会失准**（§8.1.6 的 120 就是这么来的） |
| **卷重启检测** | `ParseKey` → **`Number`** | ❌ **失效** —— 读不出编号就 `continue` |
| **"无编号"判定** | `ParseKey` → **`Number.Length`** | ❌ **误判** —— 每一章都被当成无编号 |
| **跳章提示 / 区间补建** | 内置编号解析 | ❌ 失效 |
| **planner 的改名 / 补建** | 编号 + 文字 | ⚠️ 部分失效，**且不报错** |

**分水岭是"自洽"与"依赖编号"**：

- 只用 `Canonical` / `Words` 做**两边比较**的地方 → 解析错了也**仍然自洽**，能工作
- 任何**单独依赖 `Number`** 的地方（判空、转 int、按编号找区间）→ **静默失效**

**这是最危险的地方：坏掉的不是"比较"，而是"解读"。而解读错了不会报错，只会给出一个合法的错误答案。**

#### 为什么这件事比单个 bug 重要

**§8.1 那个章节数和这里的编号硬编码是同一个病根**：

> 系统里对"什么算一个章节标题"有**不止一个答案** —— 一个可配、一个写死、
> 还有一个在 `Options` 为 null 时悄悄顶上来。

**三处都不报错。** 它们只是让界面上显示的数字、和最终写进文件的东西，对应了不同的树。

#### 具体缺陷：编号解析写死了

`HeadingSyntax.cs:31-33`（已核实）：

```csharp
internal static readonly Regex ChapterNumber = new(@"(?:第\s*)?(?<n>[一二三…0-9]+)\s*[章回节]", …);
internal static readonly Regex Numbering     = new(NumberPrefix + @"[章回节卷部篇集]", …);
```

**它只认 `第?<数字>[章回节]`。**

`ReferenceOutline.ParseKey(title)` → `TitleKey(Number, Words)` → `Canonical = Number + "|" + Words`，
**全系统靠它把标题变成可比较的键**：`ReferenceLocator`（8 处）、`ChapterDiagnostics`（2 处）、
`ChapterReviewAnalyzer`（2 处）、`ReferencePlan`（6 处）、`ChapterAutoRepair`、`ReferenceAlignment`（4 处）。

#### 换个正则会发生什么：不崩，静默降级（**2026-09-18 已实测**）

以用户配 `^第\d+话`（"话"而不是"章"）为例。**下面这张表不再是读代码得出的** ——
样本是一本 6 个标题的小书（`第1话 起点 / 第2话 相遇 / 第4话 缺口 / 第4话 缺口 / 第7话 断线`），
对照组是**完全相同的形状、只把「话」换成「章」**，由
`tests/EasyPub.Core.Tests/RecognitionConsistencyTests.cs` 锁住：

**口径：同一本小书，只改标题里的标记字（话 → 章）。**

| 环节 | 实测结果 |
|---|---|
| 识别 | ✅ 正常，6 个条目 —— 用户的规则说了算 |
| `ParseKey` 拆编号 | ❌ `Number` **全是空字符串**：`第1话 起点` → `Number=""`, `Words="第1话起点"` |
| 剥编号 | ❌ `Numbering` 常量也不认「话」，所以编号**混在标题文字里**（`第1话起点`） |
| 重复检测 | ✅ **仍然工作** —— 两边用同一套解析做比较，错了也自洽 |
| **跳章提示** | ❌ **静默消失** —— 对照组报 2 处，实测 0 处 |
| **编号顺序检查** | ❌ **静默消失** —— 对照组有，实测没有（**这一条原表没列，是实测补的**） |
| 卷重启检测 | ⚠️ **未实测**（本样本无卷标题） |
| "无编号"判定 | ⚠️ **未实测** —— 本样本是短章，`chapter_unnumbered` 来自"异常长章里的无编号标题"那条探测器，没被触发。原表写的"每一章都被当成无编号"从 `ParseKey` 的角度成立，但**实测的后果不是误报提醒，而是相反：本该由编号驱动的检查全部安静退出** |
| 区间补建候选 | ⚠️ **未实测** |

**一句话总结实测结论**：同一本书，只把标题里的「章」换成「话」，
`ChapterDiagnostics` 的提醒从 **4 组缩到 1 组**（`跳章 · 重复 · 编号顺序 · 跳章` → `重复`）。
**它不报错、不崩溃、界面上没有任何提示。**

**⚠️ 上表标"未实测"的三行不要当成结论** —— 这个项目的历史反复证明：
读代码得到的"应该会怎样"经常是错的（§11.1）。要补就得再造样本。

#### ✅ 方案 A 已实现：让"哪些检查不工作"说出来

`RecognitionConsistency.Inspect(document)`（`src/EasyPub.Core/RecognitionConsistency.cs`）：

- **判据是"全部读不出"，不是比例** —— 一本正常的书里「序」「番外」本来就没编号，
  用比例会误报；全都读不出才确定是词表不一致。少于 **3** 个标题不报（`MinimumTitles`，证据不足且是噪音）。
- 命中时预检报一条 `numbering_unreadable`（`ConversionPreflightInspector.cs:77`），文案同时说清三件事：
  规则本身没问题 / 读不出编号是因为那一步只认「第…章 / 回 / 节」/ 因此少了哪几个检查。
- `UnavailableFeatures` **只列实测确认失效的两项**（跳章提示、编号顺序检查，`:66-67`）。
  多列一条会让用户以为功能坏了 —— 而那正是这一整类缺陷原本的样子。
- **不改任何行为**：用户的自定义正则仍然说了算。修的是沉默，不是错。

**方案 B / C 仍未做**（编号解析可配 / 给 `ChapterNumber` 加变体）：

| 方案 | 做法 | 代价 |
|---|---|---|
| **B（彻底）** | 把编号解析也做成可配（跟 `Level2Pattern` 一起定义 `(?<n>…)` 捕获组） | 工程量大，且 `Level2Pattern` 现在没有强制捕获组 |
| **C** | 给 `ChapterNumber` 加常见变体（话 / 集 / 篇…） | 成本低但治标 |

**推荐顺序仍是 A → B**（A 已完成）。

### 8.3 旧默认值升级机制（v1.61.0 新增）

`TocHierarchyOptions.SupersededDefaults`（`ConversionRequest.cs:188`）是一张"旧默认值 → 新默认值"的表，
在 `AppSettingsStore.Load()` 里应用（`AppSettingsStore.cs:130,151,202`）。

**它解决的问题**：设置文件里存着当年写入的默认值，它不为空，所以在"空则用默认"里
永远轮不到新默认值 —— **代码里的改进传不到老用户身上**。

**现状**：表里只有两条（`Level1Pattern` / `Level2Pattern` 各一条旧版本）。
另外 `ChapterPattern` 的旧值由 `ConversionOptions.UpgradeSupersededChapterPattern`
（`ConversionRequest.cs:61`）单独处理，走遍 `LastProfile` + 每个预设。

**每次改任何默认值，都应该往这张表里加一条。**

### 8.4 其他已知缺陷（P2）

> 每条都带**位置 / 影响 / 实测数据 / 状态**。**没有实测数据的明确标注。**

| # | 缺陷 | 位置（已核实） | 影响 | 状态 |
|---|---|---|---|---|
| 1 | `SourceProblems` 永远是空列表，看起来像一道校验 | `RepairPlanCompiler.cs:10`（字段声明）、`:22`（只被 `Describe()` 读）、`:134-142`（解释性注释）、`:148-151`（真闸门）、参数传递在 `:159` | **非缺陷** | ✅ **已查清** —— 它是**一条从未被写入的提示通道**，不是漏掉的闸门。真正拦住不可应用补丁的是 `SourcePatchNormalizer.Normalize` 的 validation，失败时走 `CanApply=false` + `Problems`（第 5 个参数）。实测：全解决方案里 `SourceProblems` 只被 `Describe()` 读过，`tests/` 下 0 命中，没有任何下游拿它判断"补丁可应用"。已在代码原位写下说明 |
| 2 | 硬编码的个人路径 `C:\Users\13168\Desktop\easypub\bin\kindlegen_v2.9.exe` | `LegacyMobiWriter.cs:331`（候选数组 `:327-333`，类 `:313`，解释性注释 `:317-326`） | 把作者本机路径留在公开仓库里；对别人永远不存在 | ⏸ **不能直接删** —— 实测：删掉之后 **4 条 MOBI 测试立刻变红**（`TocHierarchyTests` / `EpubToMobiTests` ×2 / `MobiContentPackagerTests`），它们都没传 `MobiOptions.KindleGenPath`，于是落到了这条路径 —— **它是作者本机上测试套件的实际 kindlegen 来源**。正确顺序是**先改测试、再删路径**：让它们像 `ArtifactValidationTests` 那样显式指向 `work/easypub-compat/legacy-capture/bin/kindlegen_v2.9.exe` 并且文件不在就 `return`。理由已写在代码注释里。**⚠️ "4 条变红"来自注释与历史记录，本轮未独立重跑验证** |
| 3 | README 版本滞后 | `README.md:11,16,41,223` | 徽章与下载链接指 `v1.61.1`，而当前是 `v1.61.2`；v1.61.2 发布提交没动 README | ⏸ **未修**（v1.61.1 时曾修过一次同类问题） |
| 4 | 「原始 TXT 不变」等绝对说法散落多处 | `src/EasyPub.Desktop` 下**精确 19 处**（其中 3 处是解释性注释：`ChapterWorkbench.cs:74,89`、`RepairReviewWindow.cs:939`），另有「不修改原始 TXT」4 处 | 纯树操作的多半成立，但**其中若有会改原文的路径，就是界面在说假话** | ⚠️ **部分**：**工作台底栏已修**（v1.61.2，`ChapterWorkbench.cs:86-97` + `WorkbenchFooterTruthTests`）。其余 16 处**未逐条按落地模式审计** —— **未实测**。<br>旧文档说"21 处"且说"底栏是 XAML 静态文字"，**两者都不准确**：精确计数是 19（`Select-String -SimpleMatch` 口径），底栏文案由 C# 动态生成 |
| 5 | 第 3 条状态测试缺覆盖（候选未确认 → 不落盘） | `ChapterCatalogPanel.cs`（`SelectionChanged` `:183-204`，落盘只有 `:222` / `:119`） | 关键路径要真实网络候选，面板没有可注入来源（`ReferenceCatalogClient` 是 `public sealed`，面板内直接 `new`） | ⏸ **未修**。实测：`tests/` 下搜 `ChapterCatalogPanel` **0 命中**；`WorkbenchForgetCatalogTests` 直接调 `ReferenceCatalogInput.SaveCatalog` 或反射设 `_catalogChosenThisSession`，**绕过了面板**。代码已按安全文案那一版修正，但没有测试锁住它 |
| 6 | Desktop 全量组合跑 flaky | 测试基建 | 见 §2.3 坑 2 | ⏸ **未修**（验收路径改为单类跑） |
| 7 | 操作日志没有单元测试 | `InteractionLog.cs` | 纯副作用设施，加抢全局状态的测试比没有更糟 | ⏸ **刻意不做**。正确性目前只由"产品不崩"保证 |
| 8 | `RepairEffectCompiler.Indeterminate(string _)` 丢弃参数 | `RepairEffectCompiler.cs:167-170` | 诊断信息丢失 —— 调用点 `:57,:70,:105,:119` 都传了有意义的字符串（如"没有定位到标题行，无法说明要改哪一行"） | ⏸ **未修** |
| 9 | `SnapshotRecognitionPath` 从未被写入 | `SourceBackupLayout.cs:47`（**只有声明，无任何调用点**） | 某个恢复路径可能退化 | ⏸ **未修**。实测：全仓库代码仅此 1 处，其余 3 处是文档 |

**§8.4 第 2 条的另外两条个人路径（已处理）**：

| 位置 | 处理 |
|---|---|
| `MainWindow.xaml.cs:178-183` KindleGen 默认值里的个人路径 | ✅ **已删** —— 现在只认安装包自带的 `bin\kindlegen_v2.9.exe`；用户自己那份由 MOBI 选项覆盖。该文件已无 `C:\Users\13168` 字面量（只剩 `:179` 的注释提及） |
| `src/EasyPub.Desktop/config.xml:33` 的 `<outputfolder>C:\Users\13168\Desktop</outputfolder>` | ⚠️ **没改它，改的是采纳方** —— 见下 |

**`config.xml` 为什么不能手改。** 它是**原版文件原样**，
`LegacyCompatibilityTests.cs:257` 有一行逐值比对（`Assert.Equal(Values(config), Values(bundledConfig))`，
配套 `:263` 断言 `imported.OutputDirectory == @"C:\Users\13168\Desktop"`）锁着这一点 ——
那是"我们能原样读一份真的 v1.50 配置"这个兼容性承诺的凭据。
它里面的个人路径是**历史数据**，不是产品默认值。

**真正的问题在采纳那一侧**：`ConversionSettingsDraft.cs:78-80` 原来照单全收
`import.OutputDirectory`，于是谁导入这份示例配置，**输出目录就变成原作者的桌面**。
现在改成：

```csharp
OutputDirectory = Directory.Exists(import.OutputDirectory) ? import.OutputDirectory : OutputDirectory,
```

目录在本机存在才采纳（用户导入自己那份配置的正常场景），否则忽略。
测试在 `ConversionSettingsDraftTests.cs:74-93`。

> ⚠️ 这条测试的第一版写错了：拿"原作者的桌面"当反例，
> 而**在作者本机上那个目录是存在的**，于是测试随机器而变、实测红。
> 换成临时目录下保证不存在的随机路径，并加 `Assert.False(Directory.Exists(...))` 自证前提。

### 8.5 三个"看着像 bug 但不是"的地方

- **`chapter_content_duplicate` 归到"需要你核对"而不是"本地就能修"** ——
  对比窗只是辅助，**留哪一份必须人定**。这是刻意的。
- **修复后提醒数减少** —— 可能是三件事的任意组合：真的修好了 / 新树形状消除了 finding /
  只是被 reference suppression 不再重复显示。**`SuppressionDecision.Reason` 就是为区分这个而存在的。**
- **`chapter_duplicate` / `chapter_unrecognized` / `chapter_heading_typo` 在样书上不产生** ——
  各有具体原因（详见 §9.2 的诊断形状），不是探测器坏了。

---

## 9. 测试

### 9.1 规模与结构

```
Core      855 个用例   （854 通过 / 1 失败 —— 唯一失败是 LongTextPerformanceTests）
Desktop   约 170–180   （组合跑 flaky，单类跑可靠）
```

**口径说明**：Core 的 855 来自 `docs/RELEASE_v1.61.2.md` 的实测记录。
粗数（本文档核实时的静态统计）：Core 有 643 个 `[Fact]`/`[Theory]` 特性 + 243 条 `[InlineData]`；
Desktop 有 165 个特性 + 24 条 `[InlineData]`。**Desktop 的精确用例数未实测**（未跑）。

**唯一那条失败**：`LongTextPerformanceTests` 给 28 MB 长篇设了**墙钟预算**
（转换前检查 < 400 ms），全量跑（同时还有 854 条测试）时会超，**单独跑通过**。
**这个原因已独立复现过**（见 §8.1.5 的性能回归记录）。

**测试命名是一条完整的话**，例如：
`A_gap_recommends_the_local_check_only_when_there_is_something_local_to_check`

**这不是风格洁癖** —— 失败时不需要打开文件就知道断言的是什么。

### 9.2 六卷缺陷样书（**最重要的验收素材**）

`samples/六卷缺陷样书/`，三个文件：`缺陷样书.txt`（75299 字节）、`目录.txt`、`自测说明.md`。

**它是唯一同时包含六种缺陷的样本**，每个数字都对应一句可人工核对的话。

**实测基线（全部由测试锁住，2026-09-18 / v1.61.2）**：

| 项 | 值 | 锁在哪 |
|---|---|---|
| 源文件行数 | 2503 | `SampleBookRegressionTests.cs:45` |
| 源文件 SHA-256 | `69D722398237438EE098AE03A0F36A39AD78ACAFB430C8A912B5471F7FAEF926` | `:46-48` |
| 章节树条目 | 468（含合成的「序」） | `:49` |
| 目录卷 / 章 | 6 / 480 | `:50-51` |
| 重建卷 / 重建章 | 6 / 437 | `:65-66` |
| 未定位（Missing） | 43 | `:67` |
| 动作总数 | 191 | `:71` |
| 默认移除行数 | 160 | `:100` |
| 本地漏识别候选 | **0** ← 重要：所以跳章只能靠目录 | `:220` |
| 重复标题行 | 100 | `:159` |
| 诊断组数 | 44 | `SampleBookIssueAdviceTests.cs:45` |

**动作分布**（`SampleBookRegressionTests.cs:82-88`）：
`AddChapter=10` / `DemoteExtra=6` / `KeepExtra=32` / `MissingBody=41` /
`RemoveDuplicate=32` / `Retitle=69` / `VolumeNote=1`。

**"重建条目 444"的口径**（旧文档把它和上面那些混在一起写，**口径没标明**）：
444 = `CanonicalChapterModel.Chapters.Count`，锁在 **另一条测试**
`LandingModeEquivalenceTests.cs:90`，**不是**样书回归夹具里的数。
它与 `RebuiltChapters=437` + `RebuiltVolumes=6` 不是同一个口径，**不要把两者直接相减**。

**"默认选中 117"的口径**：117 是 `Plan.DefaultSelection` 的条数，
但**代码测试里没有断言锁住它** —— 它只出现在历史文档
（`docs/2026-09-17_Phase6-有目录改原文实施记录.md:94`、`docs/RELEASE_v1.60.1.md:56`）。
**⚠️ 当前值未实测。**

**诊断形状**：`chapter_content_duplicate=32`、`chapter_number_gap=9`、
`chapter_repeated_sequence=2`、`chapter_structure_suggested=1`（共 44 组）。
**这三个码不产生**：`chapter_duplicate`、`chapter_unrecognized`、`chapter_heading_typo` ——
各有具体原因，`SampleBookRegressionTests.cs:229-243` 的注释逐条写明了。

**⚠️ 样书是 Git 跟踪的，改坏了会进提交。** 每次提交前跑：

```powershell
git status --short -- samples   # 必须为空
```

历史上出过一次事故：`git add -A` 把 **62 行被改坏的样书**扫进了发布提交
（恢复提交 `ec79246`，同时新增 `.gitattributes` 锁住这两个文件的行尾）。

**走查时永远用副本**（复制到 `work/walkthrough/`），副本 SHA 与原件相同，
所以目录记录会共享 —— 要隔离就重定向 `EASYPUB_REFERENCE_CATALOGS_PATH`。

### 9.3 测试陷阱

| 陷阱 | 说明 |
|---|---|
| 环境变量是进程级的 | 用它的测试**必须串行** |
| WPF 窗口要 STA 线程 | `WorkbenchHarness.OnStaThread` 收好了样板 |
| `Application.Current` 为 null 会崩 | 同上 —— **但见 §6.5，这里的水比看上去深** |
| 没有 `SynchronizationContext` 时 `await` 续体跑在线程池 | 同上 |
| 渲染截图需要 `EASYPUB_SCREENSHOT_DIR` | 不设就只断言渲染有效 |

**新增窗口测试请优先用 `WorkbenchHarness`**，不要重抄那 8 行样板。
**但注意它只被 4 个测试类使用**（详见 §6.5 的宿主表），大多数窗口测试类仍自建 STA 线程。

### 9.4 界面的验证现状（**务必知道**）

**这个软件的界面从来没有被人手工点过。** 所有界面结论都来自渲染截图 + 断言，
而截图只覆盖被 `Save(...)` 明确渲染的那几个状态。

`docs/screenshots/` 下共 **16 张** PNG（实测：顶层 13 张 + `phase1b/` 1 张 + `phase2/` 2 张）。
其中顶层 `20`–`25` 是走查相关。
> 旧文档写"26 张"，**那个数字是错的**（已核实）。

**这意味着**："渲染出来没崩、文字对" 与 "用户看得清、找得到、用得顺" 之间
**有一段没有被任何测试覆盖的距离**。改界面时请特别小心这一点。

---

## 10. 发布

见 `docs/RELEASING.md`。三步：

```powershell
# 1. 改版本号（见下，必须一致；脚本会校验）
# 2. 写 docs/RELEASE_v<版本>.md（没有它脚本会拦下）
# 3. 构建并发布
pwsh tools/build-release.ps1   -Version 1.61.2 -Codename honest-footer
pwsh tools/publish-github.ps1  -Version 1.61.2 -Codename honest-footer
pwsh tools/publish-atomgit.ps1 -Version 1.61.2 -Codename honest-footer   # 可选
```

### 版本号要改的 5 处

| 文件 | 字段 | 当前值 |
|---|---|---|
| `src/EasyPub.Desktop/EasyPub.Desktop.csproj` | `Version`（`:18`） | `1.61.2` |
| 同上 | `AssemblyVersion`（`:19`） | `1.61.2.0` |
| 同上 | `FileVersion`（`:20`） | `1.61.2.0` |
| `installer/EasyPubModern.iss` | `AppVersion`（`:2`） | `"1.61.2"` |
| 同上 | `PublishDir`（`:5`） | `"..\outputs\EasyPubModern-v1.61.2-honest-footer-win-x64"`（**含版本号与代号**） |

> ⚠️ `tools/build-release.ps1:6` 与 `docs/RELEASING.md:5` 都写"**四处**"，
> 但脚本实际校验的是**五个字段**（csproj 三个 + iss 两个）。**按五个字段改。**

漏改 `AssemblyVersion` 时窗口标题会显示旧版本；漏改 `PublishDir` 时 ISCC 报 "No files found"。

### 两道闸门

1. **版本号一致性**（`build-release.ps1:30-45`）—— 不一致直接报错退出。
2. **便携包体积**（`:92-93`）—— 完整包约 65 MB，**跌破 60 MB 说明漏了 kindlegen**（约 7.5 MB）。

`build-release.ps1` 还会先**停掉锁住构建输出的进程**、**清 `EasyPub.Core` 的陈旧输出**
（改过 Core 必须清，MSBuild 会静默跳过 `CoreCompile`）。

`publish-github.ps1` 参数：`-Version` / `-Codename`（必填）、`-Repository`（默认 `uiu8/EasyPub-Modern`）、`-SkipPush`。
它会推送 → 创建 Release → 上传资产 → **回校验远端资产大小与本地一致**（上传中断会留下截断的资产）。

### AtomGit 镜像

`publish-atomgit.ps1` 参数：`-Version` / `-Codename`（必填）、`-Owner`（默认 `Wohl`）、
`-Repository`（默认 `EasyPub-Modern`）、`-TokenPath`。
需要仓库里放一份 `.atomgit-token`（已在 `.gitignore` 里，**绝不提交**）。

**三个实测出来的坑**（详见 `docs/RELEASING.md`）：
1. **AtomGit 上的 release 删不掉**（`DELETE` 返回 405）—— 脚本因此做成**幂等**的，发之前务必确认版本号。
2. 附件上传是**两步**（先拿预签名地址，再 `PUT`）。
3. `assets` 里混着**四个平台自动生成的源码包**（`type=source`），必须靠 `type` 过滤。

### 发布纪律

- **发布说明必须写清"还有什么没验证"。** 这一项和前两项一样重要。
- **发布前确认**：用户是否授权推送。这个项目的用户明确要求过"发布前先问"。

---

## 11. 给接手 agent 的忠告

### 11.1 最重要的一条

**这个项目的历史上，"文档说的"和"代码做的"反复对不上。**

举几个我亲自实测推翻的例子：

| 文档说 | 实测 |
|---|---|
| 第五卷产生 `chapter_heading_typo` | **不产生**，是 6 个 `chapter_number_gap` |
| 第二卷产生 `chapter_number_order` | **不产生**，是 `chapter_repeated_sequence` |
| 本地有 79/80 个高置信候选 | **0 个** |
| 样书上有 `chapter_duplicate` | **没有** |

**所以：任何"当前行为"的断言，都要跑一遍再写。** 探针比推理可靠。

**而且这条忠告对本文档同样适用。** 本文档这一版核实时就推翻了旧版五处：
- "工作台底栏是 XAML 静态文字" —— 错的（§6.2）
- "「原始 TXT 不变」硬编码 21 处" —— 精确计数是 19（§8.4 第 4 条）
- "screenshots 有 26 张" —— 是 16 张（§9.4）
- `KindleGenLocator` 个人路径在 `:318` —— 在 `:331`（§8.4 第 2 条）
- "测试宿主各写各的引导" —— 其实是四类不同宿主（§6.5）

**代码注释也会过期**：`ChapterCountContractTests.cs:30-32` 至今写着"这个测试当前应当是红的"，
而它已 12 条全绿；`ControlStyleContractTests.cs:118` 的结论被同文件后面那条判据测试推翻。

### 11.2 五条来之不易的教训

这五条都是"我当时的直觉看起来完全合理、结果错了"的形状。**它们的共同点是：
一个看起来合理的答案，不会自己声明它错了。**

1. **"这条测试本来就是红的"不是结论，是猜测。**
   只要改动了那条路径，就**先把它临时关掉跑一次基线**。三步，两分钟 ——
   换来的是没在发布说明里写下假话。（P0-3 引入 545 ms 性能回归那次）

2. **逐字段替换证明不了组合效应。**
   一次换一个字段只能证明"单个字段不改变结果"，**不能证明"任何组合都不改变"**。
   章节数那件事错的根源就在这里（§8.1.4）。

3. **"这会侵犯用户主权"要先证明那个"用户选择"真的存在。**
   我假设了一条"用户特意选了原版兼容所以存下旧正则"的路径，
   去 grep 之后发现**那条路不存在**。两次都只要一次 grep 就能否掉。（P0-5）

4. **视觉模型不能验收文案，只能验收布局。**
   实测：同一句底栏文字，视觉模型转写 45 个字里错了 6 处 ——
   `修复→修改`、`写回→回到`、`备份→保存`、`返回→回到`、`未命名→未保存`、`保留恢复快照→保存快照`，
   分隔符 ` · ` 读成 `。`。**危险性不在于错，在于错得像对的** ——
   每一个替换都是意思相近的常用词，读起来通顺，只有逐字比对才看得出来。
   而 `改动前自动备份` 与 `改动前自动保存` 是**两个不同的承诺**：
   前者可核对，后者什么也没说。
   **文案验收走代码**（`WorkbenchHarness.Line(window, 元素名)` 读控件真实属性，写成断言）；
   **视觉只验收布局**（按钮是不是一行、有没有溢出、层级对不对、焦点在哪）。
   更硬的理由：**断言会在以后每次改动时继续生效，看图只看这一次。**

5. **契约与产品决定冲突时改契约，但要换成一条更硬的，不是删掉。**
   P0-3 把"三个数必须相等"换成"差异必须可见 + 必须报实测值 + 保存后不许再报"。

### 11.3 三条操作纪律

1. **永远不要 `git add -A`。** 提交前先跑：

   ```powershell
   git status --short -- samples   # 必须为空
   ```

   曾把 62 行被改坏的样书扫进发布提交（`ec79246` 才恢复）。

2. **提交信息含引号时，写进文件再用 `git commit -F`。** 直接 `-m` 会被 shell 吃掉引号。

3. **不要同时跑两个 `dotnet test`。** 会破坏共享的 `EasyPub.Core.dll`，
   曾一次产生 **66 个假失败**。

### 11.4 工作方式

1. **先测量，再断言。** 写一个临时探针测试（`Assert.Fail` 打印结果），跑完删掉。
   这比读十遍代码快。
2. **改 Core 之后删 `obj`/`bin`**，否则你在测旧 DLL。
3. **提交前 `git status --short -- samples`**。
4. **界面改动要渲染截图看**，不能只看编译通过 —— 但记住 §11.2 第 4 条。
5. **批量改代码后检查 `git diff --numstat`**：只替换不删除时每个文件应当 N 增 N 删，
   出现 `\d+ 0` 就是有内容被吃掉了（我弄坏过 21 个文件、1964 行）。

### 11.5 改界面时的四条纪律

这四条是从真实走查的反馈里得来的，都有具体教训：

1. **一次只让用户看到一个主动作。** 原来工具栏一行六个控件、主按钮名字长到没人读。
2. **说"该做什么"比说"现在是什么状态"重要。** 三行事实是准确的，但用户的原话是
   "我确实不知道应该点哪里"——准确的状态不等于可执行的引导。
3. **允许状态互相矛盾，因为它们本来就独立。** 「未加载」+「目录校验后」同时成立是对的，
   不要为了"看起来一致"把它们合并成一个值。
4. **底栏说的是"这一次会发生什么"，不是一句固定标语。** 它原来无条件写
   「原始 TXT 不变」，而默认落地模式 `EditSource` 是会写回 TXT 的 ——
   执行完一次改原文的修复，底栏仍然写着「不变」。**那不是排版问题，是界面在说假话**，
   而且说的正是"可核对"那条约束最要紧的话。已在 v1.61.2 修。

**但在动界面之前，先读 §6.5。** 那条前置阻塞不解决，任何界面重做都会一次性打翻二十几条测试。

### 11.6 改语义时的纪律

**`IssueResolutionPolicy` 是全软件唯一的解决语义来源。** 三个界面表面
（工作台卡片 / 报告窗口 / 任务中心）各自只说自己的话，但**推荐的动作必须一致**。

新增一个 intent 的代价是：**它必须至少有一个真实的执行者**。
没有执行者的 intent 是死代码，会变成新的"看起来合理、实际什么也不做"。

**加完记得跑** `ChapterIssueActionTests` —— 它有一条 `Theory`（`:223`）锁住了**十种码**的推荐动作。

### 11.7 边界

**只碰"决定怎么做"，不碰"怎么算"。**

- 可以改：状态、策略、路由、措辞、界面
- 不要碰：对齐算法、补丁编译、事务语义

唯一的例外是**当新元数据需要跟着 plan 一起持久化时**（例如 `Provenance`），
那种改动是必要的，但要单独提交、单独测。

---

## 12. 这一版（v1.61.2）之后该做什么

**按优先级排列，每条说明为什么先做它。**

### 1. 🔴 解开界面轨道的前置阻塞（统一窗口测试宿主）

**做什么**：按 §6.5「从哪里接着查」的四步走 —— **只**改引导（四类宿主收成一个，
统一建真实 `App`），**XAML 一行不动**，记录到底红几条、红哪些；逐条判断是不是
`App.xaml.cs:64` 的 `ThemeManager.ApplyWindowChrome` 造成的；绿了之后才让工作台用 `Controls.xaml`。

**为什么先做它**：
- 它是**整条界面重做轨道的前置条件**，而界面重做是 v1.61.2 明确留下的最大一块工作。
- 它是唯一"试过又回退"的东西 —— 未知量已经被压缩到最小：机理查清了、
  判据测试留下了、剩下的唯一已知变量是 `EventSetter(Window_Loaded)` 一条。
- **不解决它，任何界面改动都会一次性打翻 21+ 条测试**，于是没人敢动界面。

### 2. 查清 `MainWindowLayoutTests` 那条待查的红

```powershell
--filter "FullyQualifiedName~Imported_txt_is_automatically_analyzed_without_opening_the_preflight_window"
```

断言 `book.ReadinessLabel == "所选检查通过"`（`MainWindowLayoutTests.cs:1996`）失败。
**怀疑**是 P0-3 / P1-1 新增的预检提醒改了主窗口的状态文案 —— **未确认**。

**为什么先做它**：它是当前**唯一一条没有归类的失败**。
在它被归类之前，任何人跑 Desktop 测试都会看到一条红而不知道该不该管 ——
而"这条测试本来就是红的"正是 §11.2 第 1 条明确禁止的猜测。
如果查明它确实是 P0-3/P1-1 的副作用，那说明**预检新增提醒影响了主窗口状态文案**，
那是产品行为问题，不只是测试问题。

### 3. 把 P0-5 的另一半做完（P1-2：界面显示识别配置来源）

**做什么**：在工作台 / 识别设置里显示**当前生效的识别配置是谁给的**
（默认 / 本书 profile / 全局 / 导入的 config.xml）。

**为什么先做它**：**P0-5 的产品决定依赖它**。P0-5 定案是「可见 + 一键升级」——
现在只做了"一键升级"（升级表），"可见"那一半（P1-2）还没做。
所以老用户机器上那个"存着旧默认值"的状态**仍然看不见**，
而 P0-5 的全部价值就在于让这件事可见。

它也顺带解释了"为什么我这台机器数出来和别人的不一样"这一类问题。

### 4. README 跟上 v1.61.2

**做什么**：徽章、下载链接、正文「当前正式版本」、以及新增一节 v1.61.2 摘要。

**为什么先做它**：**成本极低、结论明确、无需调查。**
而它违反的正是这个项目最在意的那条约束的同一精神 —— **可核对**：
README 说 v1.61.1，实际是 v1.61.2，读者无法据 README 核对任何东西。
（同类问题在 v1.61.1 时修过一次，说明它会反复发生。）

### 5. 清理 §8.4 余下的项

§8.4 共 9 项，第 1 条已查清（非缺陷）、第 3 条并入上一条、第 2 与第 4 条各完成一半。
**余下的是**：第 5（目录面板状态测试）、第 6（Desktop flaky，属测试基建）、第 7（操作日志无测试，**有意不做**）、
第 8（`Indeterminate` 丢参数）、第 9（`SnapshotRecognitionPath` 从未写入），
以及**第 4 条剩下的 16 处文案审计**。

**还欠一件旧文档列过的事**：**把"修复后 absent 的判据"与"locator 的判据"统一** ——
现在是两套（absent 靠标题的 canonical key，对齐靠 locator）。这正是 §8.1.6 那个
「120 vs 43」悬案的根源所在，所以它和上一条待查项应当一起做。

**为什么排在后面**：它们单个影响面小、互不依赖。**但第 4 条的审计值得单独做一次** ——
"原始 TXT 不变"这类承诺一旦落在会改原文的路径上，就是直接违反「可核对」，
性质和"改个措辞"完全不同。

### 6. 界面真的人工走查一遍

**这件事只有人能做**，而且它已经教过我们两次
（按钮名与实际执行不符、进来看不知道点哪里）。

**为什么排最后但必须做**：§9.4 那条 —— 界面的验证现状是"从来没有被人手工点过"。
自动化测试能证明"渲染出来没崩、文字对"，**证明不了"用户找得到"**。
走查应该在 §6.5 解决之后做，否则你走查的是那个将要被重做的界面。

---

## 13. 相关文档

| 文件 | 内容 |
|---|---|
| `docs/RELEASING.md` | 发布流程（含 AtomGit 的三个坑） |
| `docs/RELEASE_v1.61.2.md` | 本版发布说明（含"没做完的部分，以及为什么"） |
| `docs/RELEASE_v1.61.1.md` | 章节数查到底的那一版，含三次误判的自我更正 |
| `docs/RELEASE_v1.61.0.md` | 走查后的交互集中修改 |
| `docs/2026-09-18_交互设计与情境决策映射.md` | 约 2400 行设计文档：16 类问题的归类、8 种情境的决策表、按钮字典、实施记录 |
| `docs/2026-09-17_Phase0-基线与回归夹具.md` | 样书期望值的来源（重构**之前**的实测值） |
| `docs/rust-rewrite/` | 650 个交互入口的调用链盘点（动机已放弃，映射仍有参考价值） |
| `docs/screenshots/` | 16 张界面截图（顶层 13 张） |

**如果时间只够读两节，读 §8.1（章节数）和 §8.2（硬编码的标题语义）。**

**它们是同一个病根的两面：系统里对"什么算一个章节标题"有不止一个答案 ——
一个可配、一个写死、还有一个在 `Options` 为 null 时悄悄顶上来。**

**第三个该读的是 §11.1：这个项目的历史文档反复被实测推翻，所以别信"应该会怎样"。**
**第四个该读的是 §6.5：想动界面，先读它。**

---

## 14. 附录：未实测 / 未确认清单

**本文档中所有没有被实测或没有被独立验证的断言，集中列在这里。**
拿不准就不要把它们当结论用。

| # | 条目 | 状态 | 在哪一节 |
|---|---|---|---|
| 1 | `MainWindowLayoutTests.Imported_txt_is_automatically_analyzed_without_opening_the_preflight_window` 失败的原因 | **未确认** —— 怀疑是 P0-3/P1-1 的预检提醒改了主窗口状态文案 | §12 第 2 条 |
| 2 | 统一建真实 `App` 后"多红 6 条"具体是哪 6 条、各断什么 | **未实测**（未记录） | §6.5 |
| 3 | `ThemeManager.ApplyWindowChrome` 在离屏窗口上的实际副作用 | **未实测** | §6.5 |
| 4 | 上一轮"25 条变红"里，引导改动与 `StaticResource Chip` 各自的贡献 | **未实测** —— 当时两件事同时改，引导那一半从未被单独验证 | §6.5 |
| 5 | "不 `Show()` 只构造 + `UpdateLayout()`" 能否绕开 `Loaded` | **未实测** | §6.5 |
| 6 | 删掉 `KindleGenLocator` 个人路径会让 4 条 MOBI 测试变红 | **未独立重跑**（来自代码注释与历史记录） | §8.4 第 2 条 |
| 7 | §8.2 表里标"未实测"的三行：卷重启检测 / "无编号"判定 / 区间补建候选 | **未实测** | §8.2 |
| 7b | §8.1.2 矩阵后两行（现代默认+开+旧 Level / 旧正则+开+新 Level）的 **P0-4 修后值** | **未实测** —— 只重测了前四行 | §8.1.2 |
| 8 | 样书 `Plan.DefaultSelection` 当前是否仍为 117 | **未实测**（代码里无断言） | §9.2 |
| 9 | Desktop 的精确用例数 | **未实测**（只做了静态粗数） | §9.1 |
| 10 | §8.4 第 4 条余下 16 处「原始 TXT 不变」是否都在纯树路径上 | **未逐条审计** | §8.4 第 4 条 |
| 11 | 「参考目录里有 120 章在章节树中没有」与「未定位 43」的差异 | **未查清** —— 已排除"由修复逻辑造成"，未给出 120 的完整推导 | §8.1.6 |
| 12 | 旧文档"21 处"这个数字的口径 | **无法复现** | §8.4 第 4 条 |
| 13 | 视觉模型转写底栏 45 字错 6 处 | 历史实测记录，**本轮未复现** | §11.2 第 4 条 |
| 14 | 并发跑两个 `dotnet test` 产生 66 个假失败 | 历史实测记录，**本轮未复现** | §2.3 坑 3 |

---

### 14.1 ⚠️ 那条待查的红：`Imported_txt_is_automatically_analyzed_without_opening_the_preflight_window`

**2026-09-19 的实测补记（比 §14 那条更精确，以此为准）。**

**症状**（`MainWindowLayoutTests`，1 条红）：

```
Expected: "所选检查通过"
Actual:   "必须处理 1"
```

**这条测试用的书只有两行**：`第一章 雨夜\r\n正文`（1 章、1 行正文）。

**已经排除的**（可判定，不是猜）：

1. **不是 P0-3 的 `repeated_heading_split`** —— 它要求 `RepeatedHeadingLinesSkipped > 0`，
   而这棵树只有 1 个标题、没有重复行。
2. **不是 P1-1 的 `numbering_unreadable`** —— 它要求标题数 ≥ 3（少于 3 视为证据不足）。
3. **不是"警告"这一类** —— `必须处理 N` 出自 `MainWindow.xaml.cs:4696`
   的 `_ when (_preflightErrorCount ?? 0) > 0`，即 **`PreflightSeverity.Error`**，
   而上面两条都是 `Warning`。

**剩下的两种可能，哪一种成立尚未确定**：

- **(a) P0-2 的抛被转成了错误。** `ConversionRequest.RequiredOptions` 在 `Options` 为 null 时抛；
  预检若把异常记成一条 Error，就会得到 `必须处理 1`。
  **已知的反证**：Desktop 走的是 `BatchConversionRequestFactory.Create(..., options)`，
  它**确实传了** `options`（`MainWindow.xaml.cs:3906-3926`）。
  所以这条成立需要"某条自动分析路径绕过了那个工厂"，**未查证**。
- **(b) 这条在我动手之前就是红的。** 有可能 —— 我从未在改动 Core **之前**跑过这个类做基线。
  **这正是 §11 第一条忠告说的情况：我判断"是不是我造成的"时，凭的是印象不是基线。**

**一条命令即可定论**（下一轮请先做这个，别先改代码）：

```
git stash list                 # 确认工作区干净（HEAD 9f50082）
git checkout 26d6172 -- .      # 或直接 checkout 到本轮 Core 改动之前的提交
dotnet test tests/EasyPub.Desktop.Tests --filter FullyQualifiedName~Imported_txt_is_automatically_analyzed
```

或者在测试里临时把 issue 打出来（最直接）：

```csharp
Assert.Fail(string.Join(" | ", book.PreflightIssues.Select(i => $"{i.Severity}:{i.Code}")));
```

**看 `Code` 是什么就知道了**：是 `input_unreadable` 之类 → 走 (a)；
是别的既有码 → 走 (b)，与本次改动无关。

**为什么值得单独写一节**：它是当前唯一一条没有定论的红，
而"没定论"和"已知无关"在交接上是两件完全不同的事 —— 后者可以放着，
前者会让下一个人以为自己弄坏了什么。
