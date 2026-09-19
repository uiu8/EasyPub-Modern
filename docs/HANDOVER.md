# EasyPub Modern 接手文档

> 写给下一个接手这个项目的 agent。
>
> 这份文档的目标不是"介绍功能"，而是让你在**不重复踩坑**的前提下改动它。
> 所有事实都标注了是**实测**还是**推断** —— 请保持这个习惯，这个项目的历史上
> 有太多"文档写了但代码不是那样"的例子。

---

## 0. 先读这一段

这个项目最特殊的地方：**它有三条不可动摇的产品约束，一切设计争论都以它们为准**。

| 约束 | 含义 | 代码里的体现 |
|---|---|---|
| **正文守恒** | 修复可以改章节结构，但**不能凭空造出正文** | `RepairIntegrity.Verify`；`MissingBody` 动作只补标题不补正文 |
| **用户主权** | 手工改动优先于任何自动结果 | `ChapterTreeEntry.RecognitionSource == "manual"`；planner 不覆盖它 |
| **可核对** | 每一次删除都要能说清"删了什么、在哪一行、怎么恢复" | 移除清单 + SHA-256 + 备份路径 + 事务收据 |

**违反这三条的设计，无论多优雅都会被推翻。** 反过来，如果你觉得某处代码很别扭却不敢改，
先问一句"它是不是在守这三条之一"。

---

## 1. 五分钟认识这个项目

### 1.1 它做什么

把 TXT / EPUB 小说转成 EPUB / MOBI（Kindle）。核心难点不在格式转换，而在**章节识别**：
网文源文件里章节标题的写法五花八门，识别错了整本书的目录就错了。

所以它有一个**章节修复工作流**：识别 → 诊断 → 生成修复方案 → 逐项确认 → 应用（可只改成品，也可同时改原文）。

### 1.2 仓库与分支

- 路径：`D:\software\DeepSeek-Harness\workspace\easypub-branch`
- 分支：`main`
- 远端：`https://github.com/uiu8/EasyPub-Modern.git`
- 镜像：AtomGit（`tools/publish-atomgit.ps1`），国内用户更新检查走它

### 1.3 目录结构

```
src/
  EasyPub.Core/        业务逻辑，无 UI 依赖（net10.0）
  EasyPub.Desktop/     WPF 界面（net10.0-windows），大量 partial class
tests/
  EasyPub.Core.Tests/     825 个测试
  EasyPub.Desktop.Tests/  ~169 个测试（含渲染截图）
samples/六卷缺陷样书/      唯一的端到端验收素材（见 §9.2）
docs/                    设计与发布文档
tools/                   构建、发布脚本
installer/               Inno Setup 脚本
outputs/                  构建产物（Git 忽略）
work/                     临时工作区（Git 忽略）
```

---

## 2. 怎么跑起来

### 2.1 环境（这一节最容易踩坑）

**必须用 SDK 10.0.302 的 dotnet，不能用系统那个。**

```powershell
$env:DOTNET_ROOT='D:\software\dotnet-sdk-10.0.302'
D:\software\dotnet-sdk-10.0.302\dotnet.exe <命令>
```

原因：`C:\Program Files\dotnet\dotnet.exe` **只有运行时、没有 SDK**，用它跑 `dotnet build` 会失败。

### 2.2 构建与测试

```powershell
# 构建
D:\software\dotnet-sdk-10.0.302\dotnet.exe build src\EasyPub.Desktop\EasyPub.Desktop.csproj

# Core 测试（默认并行）
D:\software\dotnet-sdk-10.0.302\dotnet.exe test tests\EasyPub.Core.Tests\EasyPub.Core.Tests.csproj

# Desktop 测试 —— **必须串行**
D:\software\dotnet-sdk-10.0.302\dotnet.exe test tests\EasyPub.Desktop.Tests\EasyPub.Desktop.Tests.csproj `
    -m:1 -- xUnit.MaxParallelThreads=1
```

### 2.3 五个必须知道的坑

**坑 1：改了 `EasyPub.Core` 之后必须删它的 `obj` 和 `bin`。**

MSBuild 会**静默跳过** `CoreCompile`，于是你以为构建成功了，实际跑的还是旧 DLL。
症状是"我的改动没生效"，而且没有任何警告。

```powershell
Remove-Item src\EasyPub.Core\obj, src\EasyPub.Core\bin -Recurse -Force
```

**坑 2：Desktop 全量组合跑在本机严重 flaky。**

实测（同一组筛选、同一台机器）：

| 提交 | 总数 | 失败 |
|---|---|---|
| 改动前 `a64096f` | 112 | **60** |
| 改动后 | 128 | **12** |

**基线是失败集的超集** —— 也就是说这些失败与你的改动无关，它们是 WPF 渲染时序问题。
**所有失败项单类跑都通过。单类跑是验收路径，全量组合跑不是。**

```powershell
# 可靠的做法：按类跑
--filter "FullyQualifiedName~WorkbenchContextBarTests"
```

**坑 3：同时跑两个 `dotnet test` 会互相破坏。**

两个构建会写同一份 `EasyPub.Core.dll`。症状是**几十个莫名其妙的失败**。
一次只跑一个。

**坑 4：应用运行时无法构建。**

exe 锁住 DLL，报 `MSB3027 / MSB3021`。先 `Stop-Process -Name EasyPub.Desktop`。

**坑 5：`grep`/`read` 工具比 PowerShell 的 `Select-String` 好用得多**，而且后者输出中文时容易截断。

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

### 3.2 Core 的模块地图（按重要性）

| 文件 | 职责 | 备注 |
|---|---|---|
| `ChapterTree.cs` | 文档模型、`ChapterTreePlan` | `Entries` 是章节条目列表 |
| `ChapterDiagnostics.cs` | 低级诊断（gap / order / duplicate…） | 产生 7 个问题码 |
| `ChapterReviewAnalyzer.cs` | 把诊断聚合成**分组** | 16 个码的最终出处 |
| `IssueResolutionPolicy.cs` | **16 个码 → 建议动作** | 全软件唯一的语义来源 |
| `IssueCategory.cs` | 码 → 类别 | 委托给 policy，不再自己算 |
| `SelfRepairPlanner.cs` | 无目录时的方案 | 只发"树自身能判定"的动作 |
| `ReferencePlanner.cs` | 有目录时的方案 | 能回答"应该有哪些章" |
| `RepairPlanCompiler.cs` | 方案 → 可执行决策 | 纯函数 |
| `ChapterRepairApplier.cs` | **唯一写 TXT 的入口** | 见 §5.5 |
| `ChapterAutoRepair.cs` | 编排：取目录 / 选 planner / 准备方案 | |
| `RepairRequest.cs` | 依据的判别类型 | `CurrentTreeHeuristic` / `ReferenceRepair` |
| `ChapterRepairContext.cs` | 工作台三行事实的快照 | |
| `TreeProvenance.cs` | 树的来源（本地/目录/未知） | 持久化在 plan 里 |
| `FindingSuppression.cs` | 哪类 finding 可被目录证据推翻 | R3 的产物 |
| `InteractionLog.cs` | 交互日志（v1.61.0 新增） | 见 §7.4 |
| `HeadingSyntax.cs` | 标题正则的当前默认值 | |
| `ConversionRequest.cs` | `TocHierarchyOptions` + **旧默认值升级表** | 见 §8.2 |

### 3.3 Desktop 的模块地图

**这个项目大量使用 partial class，所以"一个类"往往散在多个文件里：**

| 文件 | 属于 | 职责 |
|---|---|---|
| `ChapterEditorWindow.xaml{,.cs}` | 工作台 | 窗口骨架、字段、构造、保存 |
| `ChapterWorkbench.cs` | 工作台 | 工具栏、菜单、状态条、下一步提示 |
| `ChapterReviewPanel.cs` | 工作台 | **提醒卡片**（最大的一个，800+ 行） |
| `ChapterAutoRepairPanel.cs` | 工作台 | 修复入口 `RunRepairReviewAsync` |
| `ChapterCatalogPanel.cs` | 工作台 | 目录辅助面板 |
| `ChapterEditorActions.cs` | 工作台 | 树编辑动作 |
| `ChapterMultiSelection.cs` | 工作台 | 多选与批量 |
| `ChapterContentComparison.cs` | 工作台 | 正文对比窗 |
| `ChapterIssueAction.cs` | 工作台 | 提醒卡片的 CTA 措辞 |
| `PreflightCta.cs` | 报告窗口 | 报告窗口的 CTA 措辞 |
| `PreflightWindow.xaml{,.cs}` | 报告窗口 | 转换前检查 |
| `RepairReviewWindow.cs` | 确认窗 | 逐项勾选 + 落地模式 |
| `MainWindow.*.cs` | 主窗口 | 按功能切成多个 partial |

---

## 4. 核心概念（词汇表）

**读代码前先读这一节。** 这个项目的命名是有含义的，而且注释里经常引用这些词。

### 4.1 章节树（ChapterTree）

- `ChapterTreeDocument`：一份 TXT 的解析结果。有 `Entries`（章节条目）、`SourceLines`（原始行）、`SourceSha256`。
- `ChapterTreeEntry`：一个章节。`Title`（成品标题）、`TitleLineNumber`（原文哪一行是标题）、`RecognitionSource`、`ContentRanges`（正文行范围）、`Level`、`Parent` 关系。
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
（`AppSettingsStore.DefaultRepairLandingMode`）。

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

### 5.4 apply：三条写 TXT 的路径

**全软件只有三条路径会写用户的 TXT：**

1. `ChapterRepairApplier.ApplyAsync(..., EditSource, ...)` —— 修复流程
2. `SourceEditRecovery.RecoverAllAsync` —— 启动时补完中断的事务（**唯一无 UI 的路径**）
3. 自更新落地脚本（改的是 exe，不是 TXT）

**找不到第四条。** 如果你发现某处直接 `File.WriteAllText(sourcePath)`，那是一个 bug。

### 5.5 事务与备份

`EditSource` 走完整事务：备份 → 原子替换 → journal → 恢复能力检查。
`TreeOnly` **没有事务**（因为不写文件），页面上必须如实说明这一点。

---

## 6. 界面结构

### 6.1 主窗口（`MainWindow`）

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

### 6.3 修复确认窗（`RepairReviewWindow`）

**这是唯一提交点。** 左栏是改动清单（逐项可勾），右栏是选中项详述，底部是落地模式单选。

**不要在这里加第二个提交按钮。**

### 6.4 目录面板（`ChapterCatalogPanel`）

粘贴网址 / 导入目录 TXT / 从书源抓取。**候选先只展示、不落盘**，
用户按「记住这份目录」才写入记录。

**⚠️ 这里有两句话被修过**，别改回去：
- 不能说"原始 TXT 不会被修改" —— 下一步的确认窗支持改原文
- 不能说"选中即应用" —— 选中不等于确认

---

## 7. 数据与存储

### 7.1 位置

| 内容 | 路径 | 覆盖方式 |
|---|---|---|
| 应用设置 | `%LOCALAPPDATA%\EasyPub Modern\app-settings.json` | — |
| 每本书的目录记录 | `…\Catalogs\<SourceSha256>.json` | `EASYPUB_REFERENCE_CATALOGS_PATH` |
| 原文备份 | `…\SourceBackups\` | `EASYPUB_SOURCE_BACKUP_ROOT` |
| 修复 journal | 备份目录下 | — |
| **交互日志** | `…\interaction.jsonl` | `EASYPUB_INTERACTION_LOG` |

**注意**：目录记录是**按原文 SHA-256** 索引的。
`File.Copy` 出来的副本 SHA 与原件**相同**，所以副本会命中同一份记录 ——
测试里要隔离的话，**必须重定向存储根，不能靠复制文件**。

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

**它是最有用的诊断工具。** 用户描述"我点了 X 结果 Y"时，读日志比问问题快得多。

---

## 8. 已知缺陷与未解之谜

### 8.1 ⚠️ 同一本书能识别出两种章节数：467 与 567（已查明，**未修**）

**这是目前已知最严重的缺陷族。** 它不报错、不影响文件能否生成，
但**界面上显示的一切数字、以及最终转换出的目录结构，都可能对应不同的树**。

#### 先摆平口径：三条路径各有各的"前置条目"

| 路径 | 它的"前置" |
|---|---|
| 章节树 `Entries` | 含一个合成的「序」，`TitleLineNumber` 为 `null` |
| 预检 `ChapterCandidateCount` | **只数有标题行号的条目**，天然不含前置 |
| `LegacyTextParser` | 无条件在开头塞一个「序」（源码第 33 行），也不是识别出来的 |

所以统一口径 = **由标题行识别出来的章节数**（样书此口径下前置恰好 1 条）。
**口径没摆平就对比，量到的是记账方式，不是缺陷。**（本文档前两版就是这么错的，见下。）

#### 实测矩阵（2026-09-18，样书，已摆平口径）

| 章节正则 | 分层 | Level 正则 | 章节树 | 预检 | 无树转换 |
|---|---|---|---|---|---|
| 现代默认 | **关** | — | **467** | **467 ✓** | **567 ✗** |
| 现代默认 | **开** | 新 | 567 | **567 ✓** | 567 ✓ |
| 旧 `LegacyPattern` | **关** | — | **460** | **460 ✓** | **560 ✗** |
| 旧 `LegacyPattern` | **开** | 旧 | **560** | **560 ✓** | 560 ✓ |
| 现代默认 | 开 | 旧 | 567 | — | 567 |
| 旧 `LegacyPattern` | 开 | 新 | 567 | — | 567 |

**这张表由契约测试 `tests/EasyPub.Core.Tests/ChapterCountContractTests.cs` 锁住**
（它现在应当是红的：4 行里 2 行红、2 行绿）。

**两个结论**：

1. **预检在全部四种配置下都与章节树一致 —— 预检路径从来没有缺陷。**
   走查时那个「560 项」与 Core 的 561 是同一份 document 的同一个数，
   **560 ≤ 561，完全自洽**。
2. **唯一真正的缺口是"分层关时无树转换多出 100 章"**（467 vs 567、460 vs 560）。
   分层开时三条路径一致 —— 也就是说 **`LegacyTextParser` 无论分层开关，
   行为都等价于"分层开"，即从不合并重复标题行。**

另外两行说明 **Level 正则是第二条独立的识别通道**：旧正则 + 开 + 旧 Level = 560，
旧正则 + 开 + 新 Level = 567（差 7）。**这 7 已查明，而且不是缺陷** ——
样书第一百零一章起有 7 章印成**裸数字标题**「一百零一章 戊101 断后险路」，没有「第」字：

- 旧 `LegacyPattern` 要求首字符必须是 第 或 卷，**结构上就匹不上**；
- 旧 Level2 要求 `第…章回`，也匹不上；
- 现代 `HeadingSyntax.ChapterPattern` 的 `NumberPrefix` 里「第」是**可选**的，所以认得出
  （见 `HeadingSyntax.cs:20-23` 的注释："plenty of releases print 「八百三十章 赌徒」"）。

**所以 Level 正则不只是"决定标题算第几层"，它还承担识别职责。**
不要写「Level 正则不该改变章节数」这种契约 —— 那会让这 7 个真实章节变成必须修掉的"计数 bug"。

#### 两个独立的成因，必须分开看

**成因一：`?? ConversionOptions.LegacyDefault`（9 处，5 个文件）—— ✅ P0-2 已修**

```
ConversionPreflightInspector.cs:88      PublicationChangeReceipt.cs:17
BookPreviewService.cs:51, :58           LegacyMobiWriter.cs:22,35,78,112
LegacyEpubWriter.cs:30
```

`Options` 为 null 时静默回退到 `ChapterPattern = HeadingSyntax.LegacyPattern`
（2015 年的宽松正则：连「第一章回」都吃，也不排除"回头/回来/回事"）。
**但它单独只能把 468 变成 461 —— 要得到 561 还需要第二个成因。**

**现在**：分析（预检 / 预览 / 回执 / 分析协调器）四条路径改走
`ConversionRequest.RequiredOptions`，**null 直接抛**；只有两个 `Legacy*Writer`
兼容出口保留显式回退（它们的 "Legacy" 指**输出格式兼容 v1.50**，是既有契约）。
实测记录见 §8.5 的 P0-2 记录。

**成因二：分层识别拆开重复标题行**

样书第六卷每章标题连写两次（100 章）。分层**关**时 `SkipRepeatedHeadingRun` 把它们合并成一章；
分层**开**时这 100 章各被拆成「正文 0 行的空章 + 带正文的同名章」——
矩阵里那 `正文空=100` 就是它。**这是 §8.4 单独记的缺陷。**

**所以 561 = 成因一（旧正则）+ 成因二（分层开）+ 旧 Level 正则，三者同时成立。**

#### ⚠️ 本文档前两版在这里写错了三次

1. **写「Core 层面任何参数组合都给不出 560」—— 不成立。** 当时只做了**逐字段替换**
   （一次换一个字段），**组合效应根本没测到**。这是方法错误：逐字段扫描只能证明
   "单个字段不改变结果"，不能证明"任何组合都不改变"。
2. **算错了口径**：拿 `Entries.Count`（561）去对界面上的 560，而界面数的是
   **非前置且有标题行号**的条目。**561 和 560 本来就不该相等。**
3. **把两个不同配置当成同一个配置在比** —— 由此写下"560 与 468 不可能来自同一个
   document"。真相是：走查那次的 560 来自「旧正则 + 分层开 + 旧 Level」，
   而我的 468 来自「现代默认 + 分层关」。**摆平口径后 560 与 561 完全自洽，
   预检根本没有缺陷。**

**三次错的共同形状：先有了一个数，就把它当成"系统应该给出的唯一答案"，
然后拿别的配置、别的口径下的数去证明它错了。**
**教训：报差异之前先确认两边是同一份输入、同一个口径。**

#### 后果（P0-3 / P0-4 之后的状态）

**章节树与预检现在始终一致**（同一个 document）。

**仍然存在的唯一差异**：没保存章节树时转换走 `LegacyTextParser`，它不合并重复标题行 ——
所以工作台说 467 章、直接转换产出 567 章。**这是有意保留的 v1.50 兼容行为**
（2026-09-18 决定），但它现在**不再沉默**：转换前检查会报出实测的两个数，
并指向「在工作台保存章节树」。详见 §8.5 的 P0-3 实测记录。

#### 实测证据（交互日志，2026-09-18）

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

#### 但"输入树不同"这件事仍然成立

用户的机器上是旧正则 + 分层开，所以：

- 「建议处理 155」基于 **560/561** 那棵树
- 「待核对 155 组」基于 **560/561** 那棵树
- 而样书基线测试锁的是 **467** 那棵树（44 组）
- 报告窗口那句「参考目录里有 **120** 章在章节树中没有」，描述的也是那棵旧正则 + 分层开的树

**排查时排除过的**（都是实测）：Core 完整修复后 `absent = 43 = outcome.Missing`（本来就一致）；
只应用部分动作 absent 只到 51。
**但这些只排除了"120 由修复逻辑造成"，没有排除"输入树不同" —— 恰恰是后者。**

#### 怎么修（**分两个成因，未实现**）

**成因一：把 9 处 `?? LegacyDefault` 收敛成一个入口。**

分析和预检路径在 `Options` 为 null 时应当**直接报错**，而不是悄悄回退到 2015 年的正则；
只有 `Legacy*Writer` 那几个兼容出口保留显式的 `LegacyDefault`。

⚠️ **不要直接把 `LegacyDefault.ChapterPattern` 改成现代默认** ——
那几个 `Legacy*` 名字里的 "Legacy" 指的是**输出格式兼容 EasyPub v1.50**，不是"用旧正则"。
直接改值会顺带改掉它们的识别行为，而那是产品决定，不是重构。

**成因二：见 §8.4（重复标题行），那是一个独立的纯 bug —— 而且是唯一真正造成
"看到 467、转出 567"的那个。**

**只修成因一不够**：它只能让"旧机器上的 560"变成"新机器上的 567"，
而 467 → 567 那 100 章的空章仍然存在（见 §8.3 的警告）。

**完整的分阶段计划见 §8.5。**

#### ⚠️ 一个容易误判的陷阱（我自己误判过一次）

**`TocHierarchyOptions` 的 `Enabled` 默认是 `false`。**

```csharp
var levelPatterns = hierarchyOptions.Enabled ? [Compile(Level1), Compile(Level2), Compile(Level3)] : [];
```

**所以传一条自定义 `Level2Pattern` 而不把 `Enabled` 设成 `true`，那条正则根本不会被使用。**
条目仍然来自 `ChapterEditingDocument.Candidates`（由 `chapterPattern` 决定），于是你会看到
"我改了正则但结果没变"，然后**误以为参数被忽略**。

**我上一版文档就是这么误判的** —— 我传了一条绝不可能匹配的 `Level2Pattern`，得到 468 不变，
就写下了"`hierarchy` 参数对样书不起作用"。**真正的原因是那个 `Enabled` 默认为 false。**

**教训**：看到"改了参数没反应"时，先确认**这个参数在当前配置下会不会被读到**，
再去怀疑它被忽略。矩阵里"分层开/关"两行的差异就是它生效的证据。

**这一条与 §8.2 是同一个病根：系统里对"什么算标题"有不止一个答案，而且哪个在生效并不显而易见。**

### 8.2 ⚠️ 正则表达式是整个系统的输入，而系统对它的假设散落在多处

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
| **从标题里读出编号** | `HeadingSyntax.ChapterNumber` | ❌ **常量** |
| **去掉编号、留下标题文字** | `HeadingSyntax.Numbering` | ❌ **常量** |
| 跳章区间的漏识别候选 | `MissingChapterHeadings` 内置 | ❌ 常量 |
| 异常长章里的无编号标题 | `UnnumberedHeadings` 内置 | ❌ 常量 |
| 对齐时的相似度分级 | `ReferenceLocator` 内置 | ❌ 常量 |
| 旧版兼容正则 | `HeadingSyntax.LegacyPattern` | ❌ 常量（**而且会悄悄顶上来**，见 §8.1） |

**上半是"什么算标题"，可配；下半是"标题长什么样"，写死。**
**两者不一致时，没有任何检查会拦下来。**

#### 这条链上每一环对正则的依赖程度

| 环节 | 依赖什么 | 换正则后 |
|---|---|---|
| **识别** | 只用正则本身 | ✅ 完全跟着走 |
| **对齐**（目录 ↔ 树） | `ParseKey` → `Canonical` | ⚠️ **自洽** —— 两边用同一套解析，即使解析错了也仍然对得上 |
| **重复检测** | `ParseKey` → `Words` | ⚠️ 同上，自洽 |
| **缺章判定**（absent） | `ParseKey` → `Canonical` | ⚠️ 自洽，但**混入未对齐的节点后就会失准**（§8.1 的 120 就是这么来的） |
| **卷重启检测** | `ParseKey` → **`Number`** | ❌ **失效** —— 读不出编号就 `continue` |
| **"无编号"判定** | `ParseKey` → **`Number.Length`** | ❌ **误判** —— 每一章都被当成无编号 |
| **跳章提示 / 区间补建** | 内置编号解析 | ❌ 失效 |
| **planner 的改名 / 补建** | 编号 + 文字 | ⚠️ 部分失效，**且不报错** |

**分水岭是"自洽"与"依赖编号"**：

- 只用 `Canonical` / `Words` 做**两边比较**的地方 → 解析错了也**仍然自洽**，能工作
- 任何**单独依赖 `Number`** 的地方（判空、转 int、按编号找区间）→ **静默失效**

**这是最危险的地方：坏掉的不是"比较"，而是"解读"。而解读错了不会报错，只会给出一个合法的错误答案。**

#### 为什么这件事比单个 bug 重要

**§8.1 那个 560 和这里的编号硬编码是同一个病根**：

> 系统里对"什么算一个章节标题"有**不止一个答案** —— 一个可配、一个写死、
> 还有一个在 `Options` 为 null 时悄悄顶上来。

**三处都不报错。** 它们只是让界面上显示的数字、和最终写进文件的东西，对应了不同的树。

**如果只修 §8.1（让分析传 Options），这个病根还在** —— 下一个用户的标题格式还是可能
和 `ChapterNumber` 不一致，而他会看到一个"没有问题"的报告。

#### 具体缺陷：编号解析写死了

`HeadingSyntax.cs:31-33`：

```csharp
internal static readonly Regex ChapterNumber = new(@"(?:第\s*)?(?<n>[一二三…0-9]+)\s*[章回节]", …);
internal static readonly Regex Numbering     = new(NumberPrefix + @"[章回节卷部篇集]", …);
```

**它只认 `第?<数字>[章回节]`。**

`ReferenceOutline.ParseKey(title)` → `TitleKey(Number, Words)` → `Canonical = Number + "|" + Words`，
**全系统靠它把标题变成可比较的键**：`ReferenceLocator`（8 处）、`ChapterDiagnostics`（2 处）、
`ChapterReviewAnalyzer`（2 处）、`ReferencePlan`（6 处）、`ChapterAutoRepair`、`ReferenceAlignment`（4 处）。

#### 换个正则会发生什么：不崩，静默降级（**2026-09-18 已实测**）

以用户配 `^第\d+话`（"话"而不是"章"）为例。下面这张表**不再是读代码得出的** ——
样本是一本 6 个标题的小书（`第1话 起点 / 第2话 相遇 / 第4话 缺口 / 第4话 缺口 / 第7话 断线`），
对照组是**完全相同的形状、只把「话」换成「章」**，由
`tests/EasyPub.Core.Tests/RecognitionConsistencyTests.cs` 锁住：

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

#### 方案 A 已实现：让"哪些检查不工作"说出来

`RecognitionConsistency.Inspect(document)`（`src/EasyPub.Core/RecognitionConsistency.cs`）：

- **判据是"全部读不出"，不是比例** —— 一本正常的书里「序」「番外」本来就没编号，
  用比例会误报；全都读不出才确定是词表不一致。少于 3 个标题不报（证据不足，且那是噪音）。
- 命中时预检报一条 `numbering_unreadable`，文案同时说清三件事：
  规则本身没问题 / 读不出编号是因为那一步只认「第…章 / 回 / 节」/ 因此少了哪几个检查。
- `UnavailableFeatures` **只列实测确认失效的两项**（跳章提示、编号顺序检查）。
  多列一条会让用户以为功能坏了 —— 而那正是这一整类缺陷原本的样子。
- CTA 是「在工作台核对识别规则」，不是通用兜底「处理章节」（同 §8.5 里那两条的做法）。
- **不改任何行为**：用户的自定义正则仍然说了算。修的是沉默，不是错。

**方案 B / C 仍未做**（编号解析可配 / 给 `ChapterNumber` 加变体），推荐顺序仍是 A → B。

#### 三个候选方案（**尚未决定，也未实现**）

| 方案 | 做法 | 代价 |
|---|---|---|
| **A（最小、最诚实）** | 加**一致性检查**：用户的规则能匹配、但 `ChapterNumber` 解析不出编号时，在识别结果上明确提醒「以下功能不可用：卷重启检测、跳章提示、区间补建候选」 | 不改现有行为，只把静默失效变成看得见的提醒 |
| **B（彻底）** | 把编号解析也做成可配（跟 `Level2Pattern` 一起定义 `(?<n>…)` 捕获组） | 工程量大，且 `Level2Pattern` 现在没有强制捕获组 |
| **C** | 给 `ChapterNumber` 加常见变体（话 / 集 / 篇…） | 成本低但治标 |

**推荐 A 先做**，因为它同时能覆盖 §8.1 那类"用错词表"的问题。

**✅ A 已实现（2026-09-18）**，见上一节。B / C 未做。

#### ⚠️ 上面那些影响表曾是**读代码得出的，没有实测** —— 现已实测

**"配 `^第\d+话` 之后会怎样"我没有真的跑过。** 要变成实测值，只需要一本用别的格式的小书
（几行就够）跑一遍 `ChapterReviewAnalyzer` + `ParseKey`。

**接手的人如果要做这一块，第一件事就是造那个样本，把上表验证一遍** ——
这个项目的历史反复证明：读代码得到的"应该会怎样"经常是错的（§11.1）。

### 8.3 旧默认值升级机制（v1.61.0 新增，机制本身仍需扩展）

`TocHierarchyOptions.SupersededDefaults` 是一张"旧默认值 → 新默认值"的表，
在 `AppSettingsStore.Load()` 里应用。

**它解决的问题**：设置文件里存着当年写入的默认值，它不为空，所以在"空则用默认"里
永远轮不到新默认值 —— **代码里的改进传不到老用户身上**。

**现状**：表里只有两条（`Level1Pattern` / `Level2Pattern` 的一个旧版本）。
**每次改任何默认值，都应该往这张表里加一条。**

### 8.4 ⚠️ 重复标题行：同一本书 468 还是 568，取决于两个开关

**样书第六卷每章标题连写两次（100 章）。** 这个形状在不同路径下被处理得不同：

| 路径 | 结果 |
|---|---|
| `ChapterTreeDocument.Load`，分层**关** | **468** 条 —— 重复行被合并（有专门的 `SkipRepeatedHeadingRun`） |
| `ChapterTreeDocument.Load`，分层**开** | ~~**568** 条~~ → **修好了**，见下 |
| `LegacyTextParser`（**没保存章节树时的默认转换路径**） | **567 章（非前置口径）/ 568 含「序」**、**100 个空章** —— 逐行匹配，**没有重复行合并** |

> **三行现在都独立复现过了。** 前两行在矩阵里（`正文空=100`）；
> 第三行在 P0-3 里用 `LegacyTextParser.ParseAsync(path, options, null, ct)` 直接跑出来
> （`Count - 1 = 567`，空章 100 个）。

**✅ 分层那一行已修（P0-4）**：`SkipRepeatedHeadingRun` 原先只作用于**正文范围**、
不管**条目列表**，所以分层开时重复标题的第二遍被层级正则单独认成一个空章。
现在分层开/关都给同一个数（现代默认 467、旧正则 460，都是非前置口径）。
实测与修法见 §8.5 的 P0-4 记录。

**所以现在只剩一条差异**：没保存章节树时转换走 `LegacyTextParser`，它**不合并重复标题行**，
比章节树多 100 章。

#### ✅ 产品决定（2026-09-18 已拍板）：**不改行为，只让它说出来**

**`LegacyTextParser` 的"不合并"保留** —— 那是 v1.50 原样兼容路径，
改它会让老用户的成品目录结构变化，属于**用户主权**，不该由一次重构顺手改掉。
**缺陷是"沉默"而不是"错"**，所以修的是沉默：转换前检查现在会报出**实测**的两个数
（工作台 N 章 / 直接转换 M 章）并指向「在工作台保存章节树」。

实现与实测见 §8.5 的 P0-3 记录（含"不推断差异、只实测差异"的做法，以及那道
为省 145 ms 而加的精确闸门）。

**`LegacyTextParser` 不做重复行合并，可能是刻意的 v1.50 兼容行为。**

从代码判断不出作者意图。**这一条要拍板**：

- 若**是刻意的** —— 它需要一条界面上可见的说明（"原版兼容排版会多出 100 个空章"），
  以及 P0-3 里那个 `RepeatedHeadingPolicy` 来把两条路分开
- 若**不是** —— P0-4 的合并应当同时补进 `LegacyTextParser`

**在拍板之前，不要动 `LegacyTextParser` 的行为。**

> ✅ **已拍板（2026-09-18）**：按"是刻意的"处理 —— 行为保留，加可见说明。
> 定案与实现见本节上方「✅ 产品决定」。原文保留在上面，是因为
> **"从代码判断不出作者意图、所以要拍板"这个判断本身是对的**，接手的人应该看到它。

### 8.5 章节数不一致的修复计划（**P0 与 P1-1 已完成**）

沿用本文档的边界：**不碰对齐算法、补丁编译、事务语义**；每步**先探针后断言**。

| 阶段 | 做什么 | 验收 | 状态 |
|---|---|---|---|
| **P0-1** | **契约测试**：同一本书、同一 `Options` 下，**预检 / 章节树 / 无树转换**三条路径的章节数必须一致（矩阵：分层开/关 × 新/旧正则） | **先红**，并作为后续基线 | ✅ **已完成**（`ChapterCountContractTests.cs`，**12 条全绿**） |
| **P0-2** | 把 9 处 `?? LegacyDefault` 收敛成一个 `Resolve(request)` 入口。分析与预检路径 `Options` 为 null 时**直接报错**；只在 `Legacy*Writer` 的兼容出口保留显式 `LegacyDefault` | 契约测试里"旧正则"那几行消失 | ✅ **已完成** |
| **P0-3** | 无树转换（`LegacyTextParser`）不合并重复标题行。**2026-09-18 决定：不改行为，只让它说出来** | 差异一旦存在，转换前检查必须报出**实测**的两个数 | ✅ **已完成（"说出来"版）** |
| **P0-4** | `Load` 的分层路径补重复行合并 —— **这是纯 bug，不涉及兼容** | 分层开时与分层关章数一致，空章仅「序」 | ✅ **已完成**（分层开/关现在都 467（现代默认）/ 460（旧正则）） |
| **P0-5** | 升级表把 `ChapterPattern` 的旧值也纳入（`UpgradeSupersededChapterPattern`，走遍 `LastProfile` + 每个预设） | 老设置机器 = 新装：**461 → 468**，实测 | ✅ **已完成** |
| **P1-1** | §8.2 方案 A：用户正则能匹配但读不出编号时，明确列出"不可用功能" | 样本上表格每行有实测值 | ✅ **已完成**（`RecognitionConsistency`；影响表已实测，见 §8.2） |
| **P1-2** | 界面显示**当前生效的识别配置来源**（默认 / 本书 / 全局 / 导入 config.xml） | 能解释任意一个章节数 | ⏸ **未开始** —— 要改 Desktop 界面，建议与 UI 轨道合并做，见下 |
| **P2** | 清理 §8.6 里那些小问题 | — | 🔄 §8.6 第 1 条已查清（非缺陷）；余 8 项未做 |

#### ⏸ P1-2 为什么单独留着

它要改的是 Desktop 界面（工作台 ContextBar / 设置页的章节正则框），
而那正是走查里被否掉的那一版界面所在的地方。

**"赶出来的界面"在这个项目上已经失败过一次**，所以这一条不与 Core 修复混在一起做 ——
它应该和 UI 轨道（提取 `Tokens/Controls` → 工作台 → 确认窗 → 主窗口 → 对话框）
一起排，而不是先改一遍再改一遍。

#### P0-4 实测记录（2026-09-18）

**缺陷**：`ChapterTree.cs` 的 `SkipRepeatedHeadingRun` 只作用于**正文范围**
（`bodyStart`），不作用于**条目列表**。于是分层**开**时，重复标题行的第二遍
被 Level 正则匹配成一个**独立的、正文为空的条目**：

```
for (var line in sourceLines)
{
    var level = MatchLevel(line.Text, levelPatterns);   // 第二遍标题在这里被匹配
    if (level == 0 && !candidates.TryGetValue(...)) continue;
    headings.Add(...);                                  // 于是多出一个条目
}
```

**样书第六卷每章标题印了两遍（100 章），所以开分层 = 关分层 + 100 个空章。**

**修法**：记下上一个被接受标题的**重复运行段末端**，落在这段里的行不再单独成条：

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

**实测结果**（章节树口径 = 由标题行识别出的条目数）：

| 配置 | 修前 | 修后 |
|---|---|---|
| 现代默认 + 分层关 | 467 | 467 |
| 现代默认 + 分层开 | **567** | **467** ✅ |
| 旧正则 + 分层关 | 460 | 460 |
| 旧正则 + 分层开 | **560** | **460** ✅ (旧 Level) |

全量 Core 测试：**827 通过 / 7 失败**，其中 6 条是本契约测试**故意留红**的
P0-3 缺口，1 条是已知的 `LongTextPerformanceTests`（对机器负载敏感）。

#### P0-3 实测记录（2026-09-18）

**产品决定**：无树转换的 v1.50 原样行为**保留不改**。理由是它会让老用户的成品目录结构
发生变化，属于"用户主权"，不能由重构顺手改掉。**缺陷是"沉默"而不是"错"** ——
所以要修的是沉默。

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

**提醒文案**（`PreflightIssueCodes.RepeatedHeadingSplit`）：

> 工作台识别出 467 章，但直接转换会产出 567 章（多 100 章，其中 100 章正文是空的）。
> 两条路径的识别规则不同：没保存章节树时走 v1.50 原样识别，且不合并连着印两遍的标题行。
> 这是为兼容旧成品保留的行为。要按工作台那份转换，请先在工作台保存章节树。

按这个 CTA 是「**在工作台保存章节树**」，不是 `ReadinessEvaluator.ActionLabel` 的通用兜底
「处理章节」—— 那个兜底正是走查时被抱怨的措辞，所以 `PreflightCta` 为这条码单独开路。

**为什么不进 `IssueResolutionPolicy.Definitions`**：那张 16 码表是**章节树里可处理的问题**
的词汇表，工作台会为每一项渲染处理入口。这条码没有"在树里处理"的动作（出口只有
"保存章节树"或"照旧转换"），混进去会让工作台渲染一个不存在的入口。

**契约测试换了，而不是删了。** 原 `All_three_paths_count_the_same_chapters` 要求三条路径
的数完全相等；在产品决定之后这个契约是**错的**（它会逼实现去掉兼容行为）。换成两条更硬的：

| 测什么 | 为什么更硬 |
|---|---|
| 工作台 ≡ 预检（6 组配置） | 这两个数出现在同一批界面上，必须一致 |
| 有差异时必须报出**实测的两个数** | 从"数必须相等"变成"差异必须可见" |
| 保存章节树后不许再报 | 一条永远出现的提醒等于没有提醒 |

契约测试现状：**12 条全绿**。全量 Core：**836 通过 / 1 失败**。

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

**修法：精确闸门。** `ChapterTreeDocument.RepeatedHeadingLinesSkipped` 顺手记下
识别时跳过了几行重复标题（`SkipRepeatedHeadingRun` 本来每个标题都要调一次），
为 0 就跳过那次昂贵实测。零额外扫描，单独跑恢复通过。

**闸门的第一版也是错的**，记下来免得再犯：

| 版本 | 判据 | 结果 |
|---|---|---|
| v1（错） | "有没有相邻的完全相同的非空行" | 长篇里重复正文行到处都是，性能测试文件 28 行正文压根同一句 → **闸门永远开着**，545 → 419 ms，仍红 |
| v2（对） | "识别时有没有跳过重复**标题**行" | 顺手就有，零成本，通过 |

**教训：判据必须落在"标题"上，不能落在"行"上。** 而更上一层的那条：

> **"这条测试本来就是红的"不是结论，是猜测。** 只要改动了那条路径，
> 就先把它关掉跑一次基线。三步，两分钟，换来的是没在发布说明里写下假话。

#### P0-2 实测记录（2026-09-18）

**做法**：`ConversionRequest` 新增 `RequiredOptions`，null 就抛，报错里带上
**是哪一本书**以及**怎么改成旧语义**：

```csharp
public ConversionOptions RequiredOptions =>
    Options ?? throw new InvalidOperationException(
        $"转换请求没有携带识别/排版选项（{nameof(ConversionRequest)}.{nameof(Options)} 为 null）：{InputPath}。"
        + "分析、预检、预览与输出必须使用**同一份**选项 —— 静默回退到 v1.50 旧默认会让同一本书"
        + "在不同路径上数出不同的章节数（HANDOVER §8.1）。"
        + $"要旧语义请显式传 {nameof(ConversionOptions)}.{nameof(ConversionOptions.LegacyDefault)}。");
```

- **四条分析路径**（`ConversionPreflightInspector` / `BookPreviewService` /
  `PublicationChangeReceipt`）改走 `RequiredOptions`。
- **两个兼容出口**（`LegacyEpubWriter` / `LegacyMobiWriter`）保留
  `request.Options ?? ConversionOptions.LegacyDefault`，但**加了注释说明为什么**：
  它们的 "Legacy" 指输出格式兼容 v1.50，那是既有契约。

**先探针后断言**：动之前先量了两件事 ——

1. 生产代码里 **77 处** `new ConversionRequest(` **没有一处省略 Options**，
   所以这个抛只会在程序员出错时触发，碰不到用户。
2. 测试里有 **39 处**省略 Options（11 个文件）。逐个看了它们的**方法名**：
   `Batch_conversion_preserves_input_order`、`Inspect_runs_once_and_reuses_the_completed_report`、
   `Epub_input_cannot_be_written_as_epub` ……
   **没有一条是关于识别正则的**，它们只是需要一个 request 对象。
   所以统一补 `Options: new ConversionOptions()`（现代默认 = 真实应用传的那份），
   而不是 `LegacyDefault` —— 后者会把"这些测试测的是旧识别"这个**假事实**固化下来。

**新增契约测试** `ConversionRequestOptionsContractTests`（5 条）：
存在则原样返回 / 缺失则抛且报错含书名与 `LegacyDefault` / 预检拒绝猜 / 分析拒绝猜 /
显式要旧语义仍然可用。

**⚠️ 这次批量改测试时我自己弄坏过 21 个文件（1964 行被删）。**
脚本在"这个构造点已经有 Options、跳过"的分支里**忘记把跳过的原文补回去**，
于是每个已带 Options 的构造点之前的内容全被吃掉。
`git checkout -- tests` 还原后修好那一行重跑，并加了防线：
**检查 `git diff --numstat` 里每个文件是否 N 增 N 删** —— 只替换不删除时必然如此，
出现 `\d+ 0` 就是有内容被吃掉了。

结果是全量 Core **841 通过 / 1 失败**（那条墙钟预算测试），
Desktop 的 `ChapterIssueActionTests` / `ChapterBatchTests` / `WorkbenchContextBarTests` 全绿。

**修完这一条，§8.1 的两个成因就都落地了**：成因一（静默换正则）现在会抛，
成因二（重复标题行被拆）已修并可见。剩下的是 §8.3 的升级表（P0-5）。

#### P0-5 记录：**实测之后，结论反过来**（2026-09-18）

**原计划**是"把 `ChapterPattern` 的旧值也纳入升级表"，让老机器与新装结果一致。
动手之前先跑了一次探针（样书，`Entries.Count`）：

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
   所以"把 `ChapterPattern` 纳入升级表"对这台机器是**多余的**。
2. **剩下的是窄得多的一个口子**：第 5 行 —— **分层关**时 Level 正则根本不被读取
   （`TocHierarchyOptions.Enabled` 默认 false），所以升级 Level 正则**不起作用**，
   仍是 461。而这**恰恰是新装的默认状态**（分层默认关），所以这个口子是现实的：
   老机器若从没开过分层，会一直漏掉那 7 个裸数字章节 —— 它们会被并进上一章的正文。

**但"静默把用户的 `ChapterPattern` 改掉"不能做。**

- 它违反三条约束里的**用户主权**：那是"用户在设置里存的规则"，不是内部默认值。
- 而且**无法区分**"旧默认残留"与"用户特意选了原版兼容" ——
  `ConversionProfile.Mode` 的默认值就是 `OriginalCompatible`，
  拿它当判据会让升级在多数配置上变成空操作，等于没修。

**所以 P0-5 改成"可见 + 一键升级"**：识别配置来源要显示出来（那是 **P1-2**），
并且当存着的值**等于已被取代的旧默认**时明说"这是旧默认，会让你漏掉 N 个章节"，
把升级做成用户点一下的动作 —— 和 P0-3 同一个形状：**不改行为，只让沉默停止。**

#### ⚠️ 上面这段结论**当天就被自己推翻了**，过程比结论有用

写完上面那段之后去查"用户怎么特意选旧正则"，结果那条路**不存在**：

| 我当时的假设 | 实际 |
|---|---|
| "原版兼容"会让用户存下旧正则 | `ConversionMode.OriginalCompatible` 讲的是**版式** —— 字号 110% / 行高 120% / 段间距 0.6em / 边距 0/0/3/3px / 首行 0em / 全角空格 ×2。**一个字都没提章节正则**（`MainWindow.UpdateModeDescription`） |
| 用户可能特意把章节正则写成旧默认 | 全代码库里产生 `HeadingSyntax.LegacyPattern` 的地方**只有 `ConversionOptions.LegacyDefault` 一处**，而它只被两个 `Legacy*Writer` 的兜底用到。界面上的章节正则只有**一个自由输入框**，用户真改过就不会恰好等于那一长串 |

**所以那条字符串只可能是旧默认残留。** 而 `TocHierarchyOptions.SupersededDefaults`
的文档早就把判据说清楚了："真的自己改过正则的人，不会恰好等于旧默认值" ——
**同一套判据、同一套理由，而且那一套已经在 v1.61.0 上线了。** 我却在 P0-5 上
另起了一套更严的标准，理由是"用户主权"，而那个"用户选择"是我想象出来的。

**最终做法（就是原计划）**：`ConversionOptions.UpgradeSupersededChapterPattern`
把 `== HeadingSyntax.LegacyPattern` 的章节正则升成 `null`。
关键细节：**章节正则不在 `EasyPubAppSettings` 上，而在各个 `ConversionProfile` 里**
（`LastProfile` + 每个预设各一份），所以 `UpgradeSupersededPatterns` 必须逐个走一遍 ——
只升全局那份等于没升。仍然只升内存、不写回文件。

**实测收益**（样书，分层关 = 新装的默认状态）：

| | 条目 |
|---|---|
| 老机器（旧正则 + 分层关） | **461** |
| 升级后 | **468** |
| 新装默认 | **468** |

差的 7 章正是那些没有「第」字的裸数字标题。**这一条不只是目录少 7 行 ——
旧规则认不出它们，它们会被并进上一章的正文，那是正文被改写。**

新增 `SupersededChapterPatternUpgradeTests`（7 条）：旧默认被升级 /
用户自己写的正则一个字符不动（含"旧默认的差不多版本"）/ 每个预设都走到 /
**老机器升级后与新装数出同一个数且差的正好是那 7 章** / 走真实设置文件端到端 /
只升内存不写回。

> 教训：**"这会侵犯用户主权"要先证明那个"用户选择"真的存在。**
> 我连着两次把"我自己想出来的风险"当成"已存在的路径"——
> 上一次是"这条测试本来就是红的"。两次都只要一次 grep 就能否掉。

### 8.6 其他已知缺陷（P2）

| # | 缺陷 | 位置 | 影响 |
|---|---|---|---|
| 1 | ~~`sourceProblems` 被传进 `RepairCompilation` 但全文件从未 `Add`，永远为空~~ → **已查明：不是漏掉的校验，是从未写入的提示通道** | `RepairPlanCompiler.cs:134` | ✅ **查清，非缺陷**。真正拦住不可应用补丁的是 `SourcePatchNormalizer.Normalize` 的 `validation`，它失败时走 `CanApply=false` + `Problems`（第 5 个参数）。实测全解决方案里 `SourceProblems` **只被 `Describe()` 读过**，没有任何下游拿它判断"补丁可应用"。已在代码原位写下说明，免得下一个人重复怀疑 |
| 2 | 硬编码的个人路径 `C:\Users\13168\Desktop\easypub\bin\kindlegen_v2.9.exe` | `LegacyMobiWriter.cs:306` | 在公开仓库里，删掉 |
| 3 | README 自相矛盾：徽章写 v1.56.0，正文写「当前正式版本为 v1.22.0」，实际 v1.61.0 | `README.md` | 对外可见 |
| 4 | 「原始 TXT 不变」在 Desktop 里**硬编码 21 处**，而默认落地模式是会改原文的 `EditSource` | Desktop 多处 | ⚠️ 纯树操作的多半成立，但**工作台底栏是 XAML 静态文字**，执行一次改原文修复后它仍会写「不变」。**代码层风险，未在界面验证** —— 需逐条对照落地模式审计 |
| 5 | 第 3 条状态测试缺覆盖（候选未确认 → 不落盘） | `ChapterCatalogPanel` | 关键路径要真实网络候选，面板没有可注入来源 |
| 6 | Desktop 全量组合跑 flaky | 测试基建 | 见 §2.3 坑 2 |
| 7 | 操作日志没有单元测试 | `InteractionLog` | 纯副作用设施，加抢全局状态的测试比没有更糟 |
| 8 | `RepairEffectCompiler.Indeterminate(string _)` 丢弃参数 | Core | 诊断信息丢失 |
| 9 | `SnapshotRecognitionPath` 从未被写入 | Core | 某个恢复路径可能退化 |

### 8.7 两个"看着像 bug 但不是"的地方

- **`chapter_content_duplicate` 归到"需要你核对"而不是"本地就能修"** ——
  对比窗只是辅助，**留哪一份必须人定**。这是刻意的。
- **修复后提醒数减少** —— 可能是三件事的任意组合：真的修好了 / 新树形状消除了 finding /
  只是被 reference suppression 不再重复显示。**`SuppressionDecision.Reason` 就是为区分这个而存在的。**

---

## 9. 测试

### 9.1 规模与结构

```
Core      825 个    （824 通过，1 个已知负载敏感失败）
Desktop   ~169 个   （组合跑 flaky，单类跑可靠）
```

**测试命名是一条完整的话**，例如：
`A_gap_recommends_the_local_check_only_when_there_is_something_local_to_check`

**这不是风格洁癖** —— 失败时不需要打开文件就知道断言的是什么。

### 9.2 六卷缺陷样书（**最重要的验收素材**）

`samples/六卷缺陷样书/`，三个文件：`缺陷样书.txt`、`目录.txt`、`自测说明.md`。

**它是唯一同时包含六种缺陷的样本**，每个数字都对应一句可人工核对的话。

**实测基线（2026-09-18，全部由测试锁住）**：

```
源文件行数        2503
源文件 SHA-256    69D722398237438EE098AE03A0F36A39AD78ACAFB430C8A912B5471F7FAEF926
章节树条目        468
目录卷 / 章       6 / 480
本地漏识别候选    0        ← 重要：所以跳章只能靠目录
重建条目          444
未定位（Missing） 43
动作总数          191
默认选中          117
```

**诊断形状**：`chapter_content_duplicate=32`、`chapter_number_gap=9`、
`chapter_repeated_sequence=2`、`chapter_structure_suggested=1`（共 44 组）

**⚠️ 样书是 Git 跟踪的，改坏了会进提交。** 每次提交前跑：

```powershell
git status --short -- samples   # 必须为空
```

历史上出过一次事故：`git add -A` 把 62 行被改坏的样书扫进了发布提交。

**走查时永远用副本**（复制到 `work/walkthrough/`），副本 SHA 与原件相同，
所以目录记录会共享 —— 要隔离就重定向 `EASYPUB_REFERENCE_CATALOGS_PATH`。

### 9.3 测试陷阱

| 陷阱 | 说明 |
|---|---|
| 环境变量是进程级的 | 用它的测试**必须串行** |
| WPF 窗口要 STA 线程 | `WorkbenchHarness.OnStaThread` 收好了样板 |
| `Application.Current` 为 null 会崩 | 同上 |
| 没有 `SynchronizationContext` 时 `await` 续体跑在线程池 | 同上 |
| 渲染截图需要 `EASYPUB_SCREENSHOT_DIR` | 不设就只断言渲染有效 |

**新增窗口测试请用 `WorkbenchHarness`**，不要重抄那 8 行样板。

### 9.4 界面的验证现状（**务必知道**）

**这个软件的界面从来没有被人手工点过。** 所有界面结论都来自渲染截图 + 断言，
而截图只覆盖被 `Save(...)` 明确渲染的那几个状态。

`docs/screenshots/` 里有 26 张，其中 `20`–`25` 是走查相关。

**这意味着**："渲染出来没崩、文字对" 与 "用户看得清、找得到、用得顺" 之间
**有一段没有被任何测试覆盖的距离**。改界面时请特别小心这一点。

---

## 10. 发布

见 `docs/RELEASING.md`。三步：

```powershell
# 1. 改版本号（csproj 三处 + iss 两处，必须一致；脚本会校验）
# 2. 写 docs/RELEASE_v<版本>.md（没有它脚本会拦下）
# 3. 
pwsh tools/build-release.ps1   -Version 1.61.0 -Codename next-step
pwsh tools/publish-github.ps1  -Version 1.61.0 -Codename next-step
pwsh tools/publish-atomgit.ps1 -Version 1.61.0 -Codename next-step   # 可选
```

**两道闸门**：版本号一致性、便携包体积（跌破 60MB 说明漏了 kindlegen）。

**发布说明必须写清"还有什么没验证"。** 这一项和前两项一样重要。

**发布前确认**：用户是否授权推送。这个项目的用户明确要求过"发布前先问"。

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

### 11.2 工作方式

1. **先测量，再断言。** 写一个临时探针测试（`Assert.Fail` 打印结果），跑完删掉。
   这比读十遍代码快。
2. **改 Core 之后删 `obj`/`bin`**，否则你在测旧 DLL。
3. **提交前 `git status --short -- samples`**。
4. **不要同时跑两个 `dotnet test`**。
5. **界面改动要渲染截图看**，不能只看编译通过。
6. **别用 `git add -A`**。

### 11.3 改界面时的三条纪律

这三条是从真实走查的反馈里得来的，都有具体教训：

1. **一次只让用户看到一个主动作。** 原来工具栏一行六个控件、主按钮名字长到没人读。
2. **说"该做什么"比说"现在是什么状态"重要。** 三行事实是准确的，但用户的原话是
   "我确实不知道应该点哪里"——准确的状态不等于可执行的引导。
3. **允许状态互相矛盾，因为它们本来就独立。** 「未加载」+「目录校验后」同时成立是对的，
   不要为了"看起来一致"把它们合并成一个值。

### 11.4 改语义时的纪律

**`IssueResolutionPolicy` 是全软件唯一的解决语义来源。** 三个界面表面
（工作台卡片 / 报告窗口 / 任务中心）各自只说自己的话，但**推荐的动作必须一致**。

新增一个 intent 的代价是：**它必须至少有一个真实的执行者**。
没有执行者的 intent 是死代码，会变成新的"看起来合理、实际什么也不做"。

**加完记得跑** `ChapterIssueActionTests` —— 它有一条 `Theory` 锁住了十种码的推荐动作。

### 11.5 边界

**只碰"决定怎么做"，不碰"怎么算"。**

- 可以改：状态、策略、路由、措辞、界面
- 不要碰：对齐算法、补丁编译、事务语义

唯一的例外是**当新元数据需要跟着 plan 一起持久化时**（例如 `Provenance`），
那种改动是必要的，但要单独提交、单独测。

### 11.6 没做完的事（按我判断的优先级）

**⚠️ 下面第 1、2 条已经查明根因但没有修。** 它们是这个项目当前最要紧的两件事。

1. **§8.1 —— 主界面与分析用的不是同一棵树（已查明根因，未修）**
   一行 `request.Options ?? ConversionOptions.LegacyDefault` 让界面上所有数字对应另一棵树。
   用户在导入时看到的结构、他在工作台里编辑的、最终转换用的，可能三者不一致。
   **严重程度高于其他所有。** 推荐修法见 §8.1。
2. **§8.2 —— 标题语义硬编码（诊断完成，修复方案未定）**
   "什么算标题"可配，"怎么读出编号"不可配，两者不一致时**静默降级**。
   推荐先做方案 A（一致性检查），它同时能覆盖第 1 条那类"用错词表"的问题。
3. 第 3 条状态测试（要可注入的目录来源）
4. 修复后 absent 的判据与 locator 的判据统一（现在是两套：canonical key vs locator）
5. **界面真的人工走查一遍** —— **这件事只有人能做**，而且它已经教过我们两次
   （按钮名与实际执行不符、进来不知道点哪里）

---

## 12. 相关文档

| 文件 | 内容 |
|---|---|
| `docs/2026-09-18_交互设计与情境决策映射.md` | 2400 行设计文档：16 类问题的归类、8 种情境的决策表、按钮字典、实施记录 |
| `docs/RELEASING.md` | 发布流程（含 AtomGit 的三个坑） |
| `docs/RELEASE_v*.md` | 各版本发布说明与"没验证的部分" |
| `docs/rust-rewrite/` | 650 个交互入口的调用链盘点（动机已放弃，映射仍有参考价值） |
| `docs/screenshots/` | 26 张界面截图 |

**如果时间只够读两节，读 §8.1（两棵树）和 §8.2（硬编码的标题语义）。**

**它们是同一个病根的两面：系统里对"什么算一个章节标题"有不止一个答案 ——
一个可配、一个写死、还有一个在 `Options` 为 null 时悄悄顶上来。
两处都不报错，只让界面上显示的数字对应另一棵树。**

**第三个该读的是 §11.1：这个项目的历史文档反复被实测推翻，所以别信"应该会怎样"。**
