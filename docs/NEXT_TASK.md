# 交付文档：接手 EasyPub Modern 支线

**把这一份读完就能开工。** 需要更长的背景再读 `docs/HANDOFF.md`。

---

## 0. 一句话

工作目录 `D:\software\DeepSeek-Harness\workspace\easypub-branch`。
原仓库 `C:\Users\13168\Documents\Codex\2026-08-22\easypub` **不得修改**。
当前任务在 §3，已知的坑在 §4——**先读 §4，它记录了我犯过的错，能省你几小时**。

---

## 1. 现状

| 项 | 值 |
|---|---|
| 最新提交 | `90d1c5c` fix: 章号解析误把「零」当系数; 章节树行间标出断点 (v1.55.0) |
| 工作区 | 干净 |
| 测试基线 | Core **327** + Desktop **109**，必须保持全绿 |
| 当前版本 | v1.55.0 |
| 交付物 | `outputs/EasyPubModern-v1.55.0-breakpoint-win-x64/` + `EasyPubModern-Setup-v1.55.0-x64.exe` |

**对齐基线（改动后不得劣化）**

| 书 | 基线 |
|---|---|
| 疯巫妖的实验日志 | 836 / 837 章 |
| 凡人修仙传 | 30 / 1004 章（源文件仅 0.18 MB / 1077 行，**这个数字本身是对的**；旧稿写的 1408 是当时另一次取到的目录章数） |

**Git 历史**（都在本地，**绝不 push**——remote 是用户线上仓库）

```
90d1c5c  fix: 章号解析误把「零」当系数; 章节树行间标出断点 (v1.55.0)   ← §3 三项做完
549270a  docs: 重写交接文档为单文件自包含 (NEXT_TASK.md)
26760d5  feat: 提醒条数独立成列 (v1.54.0)
e02df43  fix: 主窗口检查页未折叠同类提醒 (v1.53.0)
3d8947a  fix: 检查页同类提醒未折叠 (v1.52.0)     ← 改错了文件，仅对弹窗有效
b0ead5c  perf: 目录查询结果缓存 (v1.51.0)
1913b31  feat: 来源与文件不匹配判定; 目录获取耗时取证与预算 (v1.49.0 - v1.50.0)
3a5d08f  feat: 章节对齐、书源系统与一键修复报告 (v1.24.0 - v1.48.0)
```

---

## 2. 这个软件在做什么

以**官方目录**为准重建章节树，但**绝不因此丢失正文、绝不覆盖用户的编辑**。三条硬约束：

1. **正文守恒** —— 每个源行要么进某个章节，要么进移除清单。`RepairIntegrity.Verify` 在运行时逐行核对，不守恒就抛异常停止应用
2. **用户主权** —— 用户删过、改过、手动添加的，重新修复不能翻案
3. **可核对** —— 每次删除都有逐行清单 + 源文件 SHA-256

**两套逻辑必须区分**（用户明确要求）：

| | 有参考目录 · 严格对齐 | 无目录 · 本地启发式 |
|---|---|---|
| 依据 | 权威 | 猜测 |
| 缺章/多章/重复/标题/卷 | **全部和目录对比** | 本地规则推断 |
| 完整性 | **可判定**，逐章核对 | **不可判定**，如实说 |

**核心文件**

| 文件 | 职责 |
|---|---|
| `ReferenceCatalog.cs` | 多书源发现、并发查询、缓存、来源不匹配判定 |
| `ReferenceLocator.cs` | 三遍定位（候选集 → 间隙补位 → 兜底全文搜索） |
| `ReferenceAlignment.cs` | 标题匹配打分、章号解析 |
| `ReferencePlan.cs` | 生成方案、重建章节树 |
| `HeadingSyntax.cs` | **所有**标题识别规则的唯一来源 |
| `ChapterAutoRepair.cs` | 一键修复主流程 + 分类报告 |
| `RepairIntegrity.cs` | 行守恒校验、移除清单 |
| `CatalogCache.cs` | 目录查询缓存（24h） |

---

## 3. 当前任务：章节工作台的问题呈现要让人一眼看懂 —— **已完成（v1.55.0）**

用户原话：

> 这里的 401 到 412 跳章已经修复了……跳章的话，应该用高亮显示两个章节之间。
> 现在的修复界面太模糊了，用更清晰的方法让用户一眼就知道这个地方到底错在哪里了。
> 另外，如果是已经获取目录的小说，在跳章或者别的错误的地方，应该用一些方式标出这里本来该有什么章节。

### ① 提示不随修复更新 —— **真因不在刷新链，在章号解析**

先按要求打印了分析时的章节号序列（`work/workbench-probe`），结论是：**`RefreshSuggestions`
确实拿到了修复后的新树**，提示没消失是因为**树里章号本身被读错了**。

根因见 §4.5：`HeadingSyntax.ParseNumber` 把「零」当系数。《疯巫妖的实验日志》印的是
`第四百零九 / 第四百零十 / 第四百十一`，被读成 `409 → 400 → 411`，
`ChapterDiagnostics` 于是报「章节编号从 401 跳到 412」——用户看到的就是这条，
它对齐参考目录后依然在（`chapter_number_gap` 是 `referenceAligned` **故意不抑制**的那一类）。

实测（`workbench-probe`）：

| | 修复前 | 修复后 |
|---|---|---|
| 疯巫妖 提醒组数 | 3（含 3 条错号导致的跳章） | 1（唯一真实缺章：第 689 章） |

修法与语义见 §4.5；回归用例在 `tests/EasyPub.Core.Tests/ChapterBreakpointsTests.cs`。

### ②③ 行间高亮断点 + 标出参考目录里本应存在的章节 —— **已实现**

新增 `src/EasyPub.Core/ChapterBreakpoints.cs`（Core 层，无 UI 依赖）：

- 按 `ChapterDiagnostics` 的同一作用域（父章节 + 层级 + 章/回）比较相邻章号，产出三类断点：
  `NumberGap`（真缺章）、`ReferenceMissing`（目录里有、源文本找不到）、`HeadingTypo`（编号写法接不上）
- 有参考目录时用 `ReferenceOutline.Align` 对齐，把目录里缺的章节挂到**它本该跟随的那一行**
- 断点锚定 **entry id** 而不是行号：合并、拆分、拖动后行号会变，id 不变
- 重复号不再被当成缺口（《牧神记》`第一五五章` 重复 155，曾报出 1500 章缺口）

工作台（`ChapterEditorWindow.xaml` 的 `HierarchicalDataTemplate` 外套一层 `StackPanel`）在
两行之间渲染提示条：`↕ 此处跳过：缺第 689 章 · 目录里有「第六百八十九章 奥秘」`，
橙色=缺章，红色=目录缺章/编号有误；悬停显示完整说明。参考目录从
`ReferenceCatalogInput.Load(sha256)` 读——一键修复就是写在那儿的，所以**修复一结束标注就出现**。

实测断点（《疯巫妖》一键修复后，目录 837 章）：

```
[NumberGap]      前一章：第六百八十八章 元素狩猎 @52508  →  本章行：第六百九十章 灵魂之书
[ReferenceMissing] 同一位置：参考目录有「第六百八十九章 奥秘」，源文件里找不到
```

### 已知的收敛（不是缺陷）

- 「第六百八十久章 奥秘」这类**含错字、连内置识别都不接受**的标题（`久` 不是数字），
  不会进树，纠错表只对**已被识别的**标题生效。缺章会如实报，不会凭空造出章节（铁律 4）
- 编号**倒退**（`417 → 4080 → 419` 之外的重复号）只报"接不上"，不报缺章

---

## 4. 已知的坑 —— **先读这一节**

这一节记录我（DSH）在 v1.49–v1.54 期间犯的错，每条都有代价。

### 4.1 我算错过迭代量，还把错数字写成了"事实"

上一版 `NEXT_TASK.md` 里写「凡人修仙传 1405 章 × 61752 行 ≈ 8700 万次迭代」。
**61752 是《疯巫妖》的行数**，凡人只有 **1077** 行。真实是 151 万次，**差 58 倍**。
Codex 实测推翻：瓶颈是**目录获取的网络耗时（98%）**，对齐只占 1%。

**教训**：交付文档里只写复现命令和**实测**数据，不写推算的中间量。

### 4.2 我改错了两次文件

「检查页同类提醒不折叠」——用户截图的是**主窗口内嵌的「检查与修复」页**，
走 `MainWindow.Workflow.RefreshWorkflowRows`；我先改了 `PreflightWindow`（那是**弹窗**报告）。
**v1.52.0 是无效改动**，v1.53.0 才修对。

**教训**：主窗口有内嵌页面和弹窗两套界面，**同一个功能可能有两份实现**。改之前先确认用户看到的是哪一个。

### 4.3 两次"优化"都因为没先量数据而失败

- 加"目录远大于文本就跳过兜底搜索"的闸门 → 阈值比较对象错了（比的是未定位章节数 vs 文件行数 = 2.3%，触发不了），且对齐 30→3 章
- 给章号建索引 → **更慢**（14.3s → 18.5s），因为要救的路径是"章号根本不存在"，索引查不到仍落回线性扫描

**教训**：动手前先加计数器把事实钉死（现在 `AutoRepairTiming` / `ReferenceLocationStats` 常驻，直接用）。

### 4.4 工具用法坑

| 坑 | 后果 |
|---|---|
| `--no-build` 跑诊断 | 用的是**旧 dll**，会得出"改了没生效"的错误结论。我因此误判过一次 |
| **MSBuild 静默跳过 CoreCompile** | 改完 `EasyPub.Core` 后 `dotnet run` / `dotnet test` 可能只打印「正在跳过目标"CoreCompile"，因为所有输出文件相对于输入文件而言都是最新的」，于是**跑的是旧代码**。我因此反复怀疑自己的逻辑，查了好几轮。**改 Core 后先删 `src/EasyPub.Core/obj` 和 `bin` 再构建**，并确认输出里真的出现 `EasyPub.Core -> ...dll` |
| `Select-Object -First N` | **会掐断上游进程**。对 ISCC 用过一次，产出 0.9 MB 的残缺安装包（正常 64 MB） |
| `dotnet build *.slnx` | **静默失败**（0 error、2.5 秒退出、无产物）。必须用单个 `.csproj` |
| 改版本号只改一处 | 版本号有**三个**来源，全都要改：`csproj` 的 `<Version>`、`<AssemblyVersion>`、`<FileVersion>`，外加 `installer/EasyPubModern.iss` 的 `AppVersion` **和** `PublishDir`。漏掉 `AssemblyVersion` 时窗口标题仍显示旧版本（`MainWindow.AppVersion` 读的是程序集版本，不是 `<Version>`）；漏掉 `PublishDir` 时 ISCC 报 "No files found" |
| `dotnet` 不在 PATH | 用 `D:\software\dotnet-sdk-10.0.302\dotnet.exe` |
| `dotnet test` | 需要 danger-full-access 沙箱，否则 testhost 命名管道报"拒绝访问" |
| KindleGen | 不在仓库里，从 `outputs/EasyPubModern-v1.48.0-heading-guard-win-x64/bin/` 复制 |

### 4.5 章号解析的静默错误（v1.55.0 才修好）

`HeadingSyntax.ParseNumber` 原先把**「零」当成系数**：`'零'` 把 `number` 记成 0 并置
`hasNumber`，紧跟单位时按 `0 * unit` 相乘。实测：

```
零十=0   零二十=20   四百零十=400   一千零十=1000
```

后果：《疯巫妖》《牧神记》这类把「一十」印成「零十」的发布版被读错章号。
`第四百零九 / 第四百零十 / 第四百十一` 读成 `409 → 400 → 411`，
`ChapterDiagnostics` 据此报「**从 401 跳到 412**」——用户看到的就是这条，
一键修复对齐目录后它依然在，于是看起来像"提示不随修复更新"。

**教训**：诊断提示"跳章 / 缺章"时，先核对**树里章号本身**读得对不对，再怀疑树或刷新链。
`HeadingSyntaxTests` 里有 `一千零八=1008`，却**从来没有「零+单位」的用例**，所以这个 bug 藏了很久。
修好后的语义：`零+单位 = 一+单位`，`零+数字` 仍是占位零（`一千零八`=1008 不变）。

### 4.6 环境事实

- 原仓库**并非**未被修改：实际有 66 个未提交改动，最新 2026-09-14 18:23（v1.25.0 产物）。**我没有动它**，去留由用户决定
- `outputs/` 已被 `.gitignore` 排除（2.6 GB），不会误提交

---

## 5. 铁律

1. **原始 TXT 永不修改**；一切改动走 `Mutate`（可撤销）
2. **删除必须备份**，并生成逐行清单（`RepairIntegrity.SaveRemovedAsync`）
3. **缺章如实报告**，不粉饰
4. **不发明章节**——只补建在文本里真正定位到的
5. **可本地 commit，绝不 push**
6. **不要往 Memorix 写记忆**，除非用户明确要求
7. **不动原仓库**
8. **改完必须让用户截图确认**——树模板的视觉效果不能只看代码

---

## 6. 诊断工具

```powershell
$dotnet='D:\software\dotnet-sdk-10.0.302\dotnet.exe'
$r='D:\software\DeepSeek-Harness\workspace\easypub-branch'
& $dotnet run --project "$r\work\chapter-audit" -c Release -- <txt路径> <模式>
```

| 模式 | 作用 |
|---|---|
| `auto` | 完整 `RepairAsync`，打印参考/重建/缺章数（与 UI 一键修复同一路径） |
| `repair` | 同上 + 逐条打印重建后的树 + **分类报告** |
| `probe <行号>` | 打印指定源行在原始树中的身份（标题？正文？来源？长度？） |
| `tree` | 打印对齐后的树 |
| `sources` | 检测各书源可用性与响应耗时 |
| `reference` | 打印各源返回的目录前后若干条 |

**测试语料**

- `C:\Users\13168\Desktop\novel\so-novel\*.txt`（37 本）
- `C:\Users\13168\Desktop\novel\tomato\*.txt`（7 本，番茄）

**注意**：部分语料**本身不完整**（凡人修仙传只有 0.18 MB / 30 章）。
看到「缺 N 章」先核对**源文件本身有多少章**，再判断是不是软件的问题。

---

## 7. 构建与发布

```powershell
$dotnet='D:\software\dotnet-sdk-10.0.302\dotnet.exe'
$r='D:\software\DeepSeek-Harness\workspace\easypub-branch'

& $dotnet test "$r\tests\EasyPub.Core.Tests\EasyPub.Core.Tests.csproj" -c Release
& $dotnet test "$r\tests\EasyPub.Desktop.Tests\EasyPub.Desktop.Tests.csproj" -c Release

$out="$r\outputs\EasyPubModern-v<版本>-<代号>-win-x64"
& $dotnet publish "$r\src\EasyPub.Desktop\EasyPub.Desktop.csproj" -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
# 复制 bin/kindlegen_v2.9.exe 与 config.xml，压 ZIP
& 'D:\software\Inno Setup 7\ISCC.exe' "$r\installer\EasyPubModern.iss"
```

版本号只有一个来源：`src/EasyPub.Desktop/EasyPub.Desktop.csproj` 的 `<Version>`，
窗口标题和左下角标签都从程序集读（`MainWindow.AppVersion`）。**发布时同步 iss 的 AppVersion 与 PublishDir。**

---

## 8. 刻意不做的（不要当成缺陷去"修"）

| 项 | 原因 |
|---|---|
| 番茄**关键词搜索** | 需逆向字节风控签名（`a_bogus`/`msToken`），已试到底；目录页免签名可用，走"粘贴网址/编号" |
| **自动换源下载**缺失正文 | 那是使用者的活，不是产品功能 |
| 无头浏览器抓取 | 番茄会识别并返回空集 |
| 无目录时**弱化**手工核对 | 用户已确认保留手工路径 |
| 底部转换条在所有页面显示 | 用户已确认要保留 |

---

## 9. 相关文档

| 文件 | 内容 |
|---|---|
| **本文档** | 接手入口（读这一个就够） |
| `docs/HANDOFF.md` | 更长的支线档案：v1.24→v1.48 演进、逐条设计决策 |
| `docs/2026-09-15_目录获取耗时取证与预算-v1.49.0.md` | 性能取证：分阶段实测数据 |
| `docs/RELEASE_v1.45.0.md` / `RELEASE_v1.46.0.md` | Codex 两轮交付的修复清单 |
| `docs/2026-09-14_书源系统-v1.43.0.md` | 书源模型、番茄实测 |
| `docs/2026-09-14_对齐修复-v1.43.1.md` | 三处对齐根因的取证 |
