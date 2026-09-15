# 交付文档：EasyPub Modern v1.55.1 之后

**把这一份读完就能开工。** 更长的背景看 `NEXT_TASK.md`（v1.55.0 之前的完整档案），
模块与双模式设计看 `docs/2026-09-15_功能逻辑与双模式设计.md`。

---

## 0. 一句话

工作目录 `D:\software\DeepSeek-Harness\workspace\easypub-branch`。
原仓库 `C:\Users\13168\Documents\Codex\2026-08-22\easypub` **不得修改**。
**先读 §3（我做错了什么）**——那五条里有四条是我这一轮亲手制造又亲手修掉的，能省你几小时。

---

## 1. 现状

| 项 | 值 |
|---|---|
| 最新提交 | `b577798` docs: NEXT_TASK.md 顶部加指针，指向 v1.55.1 交接文档 |
| 工作区 | 干净 |
| 测试基线 | 运行结果 Core **327** + Desktop **114**，必须保持全绿 |
| 当前版本 | `1.55.1` |
| 交付物 | ✅ 已发布（2026-09-15）：<br>`outputs/EasyPubModern-v1.55.1-breakpoint-catalog-win-x64/`（exe 67.59 MB）<br>`outputs/EasyPubModern-v1.55.1-breakpoint-catalog-win-x64.zip`（64.92 MB）<br>`outputs/EasyPubModern-Setup-v1.55.1-x64.exe`（64.43 MB）<br>exe `FileVersion = 1.55.1.0`；发布版冒烟通过，标题 `EasyPub Modern v1.55.1` |

**对齐基线（改动后不得劣化）**

| 书 | 基线 |
|---|---|
| 疯巫妖的实验日志 | 参考 837 章，已对齐 **836**，未定位 1（第六百八十九章 奥秘） |
| 凡人修仙传(忘语).txt | 参考 1004 章，已对齐 **30**（源文件只有 0.18 MB / 30 章，**这个数字本身是对的**） |

**Git 历史**（都在本地，**绝不 push**——remote 是用户线上仓库）

```
583d0d4  fix: 提醒卡片的按钮必须对应"这个问题"能做的那件事
501785d  feat: 章节工作台加「获取参考目录（联网）」(v1.55.1)
02e7340  fix: 断点只报可核对的缺口；删掉猜测性的"错号"判断
b8da35b  docs: 交接文档同步 v1.55.0
90d1c5c  fix: 章号解析误把「零」当系数; 章节树行间标出断点 (v1.55.0)  ← 本轮主线
549270a  docs: 重写交接文档为单文件自包含 (NEXT_TASK.md)
26760d5  feat: 提醒条数独立成列 (v1.54.0)
```

---

## 2. 这一轮（v1.55.0 → v1.55.1）做了什么

### 2.1 用户报的 bug：跳章提示修复后还在

用户原话：「401 到 412 跳章已经修复了……提示还在」。

**任务书要求先取证再改**，我照做了，结论和任务书里的三个猜测都不同：
`RefreshSuggestions` 一直拿到了新树，**真因在章号解析**。

`HeadingSyntax.ParseNumber` 把「零」当系数：`'零'` 把 `number` 记成 0 并置 `hasNumber`，
紧跟单位时按 `0 * unit` 相乘：

```
零十=0   零二十=20   四百零十=400   一千零十=1000
```

后果：《疯巫妖》印的 `第四百零九 / 第四百零十 / 第四百十一` 被读成 `409 → 400 → 411`，
`ChapterDiagnostics` 据此报「从 401 跳到 412」。这条属于 `chapter_number_gap`，
而它是 `referenceAligned` **刻意不抑制**的那一类（见记忆里的设计决策），
所以对齐目录后它依然在——看起来就像"提示不随修复更新"。

修法：单位前系数为 0 时按 1 计（"零"本意是「一」），`一千零八` 这类占位零不受影响。
实测《疯巫妖》提醒组数 3 → 1（只剩唯一真实缺章）。

### 2.2 新功能：章节树行间标出断点

`ChapterBreakpoints.Between` 产出三类断点，工作台渲染在两行之间（`ChapterEditorWindow.xaml`
的模板外套一层 `StackPanel`）：

| 类型 | 展示 |
|---|---|
| `NumberGap` | `↕ 此处跳过：缺第 689 章` |
| `ReferenceMissing` | `目录里有「第六百八十九章 奥秘」` |
| `HeadingTypo` | 编号写法接不上（仅当号码能解释缺口） |

断点锚定 **entry id** 而非行号（合并、拆分、拖动后行号会变，id 不变）。

### 2.3 目录覆盖率：2/44 → 可主动获取

③「标出本应有什么章节」依赖"本书已保存目录"，实测 44 本语料里只有 2 本有。
查清机制：目录**只**在 `ChapterCatalogPanel.cs:140` 的 `Save()` 里写，
触发点是用户打开「手动核对目录」面板并**选中一个书源**——几乎没人走过那条路。

改法：章节树工具栏新增「获取参考目录（联网）」，仅在本机无本书目录时可用；
成功后立即重算断点。**不自动联网**（用户明确选择：联网要写在按钮上）。

顺带把两处锁在别处的能力提到 Core，避免两份实现：
`ReferenceCatalogInput.Save`（原先是面板里的局部函数）、
`ReferenceCatalogInput.Pick`（原先内联在 `ChapterAutoRepair` 里的一行）。

### 2.4 提醒卡片的按钮文案

用户指出：**跳章提示下，右侧按钮却是「编辑成品标题…」**。

系统核对后发现这是**兜底文案的通用缺陷**，不是一处疏漏：

| 问题码 | 修改前 | 现在 |
|---|---|---|
| `chapter_number_gap` | 编辑成品标题…（点击走 Split_Click，对缺章无用） | 补建本组 N 个… / 批量补建全书的 N 处… / 获取参考目录并对照… |
| `chapter_number_order` | 编辑成品标题… | 有精确改号建议时「修改为：…」，否则「核对层级处理…」 |
| `volume_number_duplicate` / `chapter_level_gap` | 编辑成品标题… | 核对层级处理… |

另外 `ReviewActionButton.Visibility` 原先要求 `count > 0`，
**"待核对"模式没有选中章节时按钮根本是隐藏的**——批量修复跳章的入口只藏在「整理工具 ▾」菜单里。
现已允许 `Missing` 类提醒不选章节也显示。

映射提成纯函数 `ChapterIssueAction` 并由 `ChapterIssueActionTests` 锁住。

---

## 3. 我这一轮做错了什么（**重点读**）

### 3.1 我新加的检测器自己编造了 232 处问题

v1.55.0 我加了一条判断：「章号与前一章接不上 → 疑似编号写法有误」，
用 `Math.Abs(值 - (前一章+1)) > 50` 来猜错号。扫全部 44 本语料后发现：

- **232 处"接不上"**，绝大多数是假的
- 根因：《择天记》《将夜》这类发布版**在同一本书里混用中文数字与阿拉伯数字两套编号**
  （`第十七章` → `第335章`），跨体系比较凭空造出几百个"错号"
- 另一类：重号被当成缺口的左端点（《牧神记》`第一五五章` 重复 155，报出"缺 1398 章"）

**修法**：删掉整条猜测性信号，只留两类可核对的断点；缺口只在号码可信时逐章列举
（必须落在本节编号范围内、不得在全书别处出现过、超过 30 个连续号码降级为"编号对不上，请核对"）。
实测：错号 232 → 0，可逐章列举的缺口 97 处 / 192 章（全部经过三重过滤）。

**教训**：新加的判断**必须先在全部语料上量一遍**再交给用户。
我第一轮只测了《疯巫妖》一本，于是把它自己的假阳性带给了用户。

### 3.2 MSBuild 静默跳过 CoreCompile（浪费了好几轮）

改完 `EasyPub.Core` 后 `dotnet run` / `dotnet test` 可能只打印
「正在跳过目标"CoreCompile"，因为所有输出文件相对于输入文件而言都是最新的」，
于是**跑的是旧代码**。我因此反复怀疑自己的逻辑，查了三四轮。

**每次改 Core 后先删 `src/EasyPub.Core/obj` 和 `bin` 再构建**，
并确认输出里真的出现 `EasyPub.Core -> ...dll`。

### 3.3 版本号有三个来源

`csproj` 的 `<Version>`、`<AssemblyVersion>`、`<FileVersion>`，
外加 `installer/EasyPubModern.iss` 的 `AppVersion` **和** `PublishDir`。
漏掉 `AssemblyVersion` 时窗口标题仍显示旧版本（`MainWindow.AppVersion` 读的是程序集版本，
不是 `<Version>`）——我因此看到过 `v1.54.0` 的标题。
`NEXT_TASK.md` 里写的"版本号只有一个来源"是错的。

### 3.4 我写测试数据时想当然

调试重号假阳性时，我构造了「1555 ↔ 155 互换」的假数据，与真实书的结构不符，
于是花了很久追一个不存在的 bug。**改用真实语料的形态构造数据**后立刻清楚。

### 3.5 抓出一个我自己的回归（正面例子）

把按钮映射提成纯函数后，加的第一条测试当场失败：我重排优先级时
把 `chapter_number_order` 从条件里漏掉了，`修改为：正确章号` 这条更好的路径被覆盖。
**这就是"把判断提成可断言函数"的价值**——靠读代码不会发现。

---

## 4. 已被推翻的旧结论（不要照着旧文档做）

| 旧说法 | 实际 |
|---|---|
| 「版本号只有一个来源」(`NEXT_TASK.md` §4.4) | 三个来源 + iss 两处，见 §3.3 |
| 凡人修仙传基线「30 / 1408」 | 参考目录实测 **1004** 章（1408 是另一次取到的） |
| 「提示不随修复更新」是刷新链问题 | 是章号解析问题，见 §2.1 |
| `chapter_number_gap` 不抑制的理由是"目录说有 837、重建只有 836" | 它**不来自目录对比**，而是本地相邻章号比较，所以还会覆盖"章号被读错"这类假阳性 |

---

## 5. 下一步建议（按价值排序）

### ① ✅ 已完成：v1.55.1 已发布（2026-09-15）

产物与校验值见 §1。发布时的两个前置条件（**都踩过**，下次照做）：

1. **改过 Core 就要先清 `src/EasyPub.Core/obj` 和 `bin`**，否则可能 publish 出旧逻辑（§3.2）
2. **先停掉正在运行的应用**：它会锁住 `bin\Release\net10.0-windows\win-x64\EasyPub.Core.dll`，
   publish 会报 MSB3027「超出了重试计数 10。失败。文件被 EasyPub.Desktop (PID) 锁定」

### ② 混用编号体系的书噪音仍偏大

《择天记》123 处提示、其中真正可核对的是 18 处。现在的提示是诚实的
（"编号对不上，请核对"），但量大。改进方向在 `ChapterBreakpoints` 的作用域与
`present` 判定；**别动识别层**（风险高，且那是 `ChapterDiagnostics` 的地盘）。

### ③ 「获取参考目录」的使用率未验证

新按钮只在《牧神记》这类书上手动试过。可以在多本书上跑一遍，
确认 `DiscoverAsync` 的成功率与耗时（实测目录获取占一次修复的 98% 耗时）。

### ④ 断点机制可以更主动

现在只在树上标注。可以考虑：点提示条直接跳到原文对应行、或一键把该区间加入待核对组。

### 刻意不做的（不要当成缺陷去修）

见 `NEXT_TASK.md` §8：番茄关键词搜索、自动换源下载、无头浏览器抓取、
无目录时弱化手工核对、底部转换条常显。

---

## 6. 铁律

1. **原始 TXT 永不修改**；一切改动走 `Mutate`（可撤销）
2. **删除必须备份**，并生成逐行清单（`RepairIntegrity.SaveRemovedAsync`）
3. **缺章如实报告**，不粉饰；软件**不发明章节**——只补建文本里真正定位到的
4. **可本地 commit，绝不 push**
5. **不往 Memorix 写记忆**，除非用户明确要求
6. **不动原仓库**
7. **改完必须让用户截图确认**——树模板的视觉效果不能只看代码
8. **新加的判断先在全部 44 本语料上量一遍**（§3.1 的教训）

---

## 7. 构建、测试与发布

```powershell
$dotnet='D:\software\dotnet-sdk-10.0.302\dotnet.exe'
$r='D:\software\DeepSeek-Harness\workspace\easypub-branch'

# 改过 Core 就先清，否则可能跑到旧 dll（§3.2）
Remove-Item "$r\src\EasyPub.Core\obj","$r\src\EasyPub.Core\bin" -Recurse -Force -ErrorAction SilentlyContinue

& $dotnet test "$r\tests\EasyPub.Core.Tests\EasyPub.Core.Tests.csproj" -c Release      # 327
& $dotnet test "$r\tests\EasyPub.Desktop.Tests\EasyPub.Desktop.Tests.csproj" -c Release # 114

# 发布前：窗口在跑会锁住 dll，先关掉
Get-Process -Name 'EasyPub.Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force

$out="$r\outputs\EasyPubModern-v<版本>-<代号>-win-x64"
& $dotnet publish "$r\src\EasyPub.Desktop\EasyPub.Desktop.csproj" -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
New-Item -ItemType Directory -Path "$out\bin" -Force | Out-Null
Copy-Item "$r\outputs\EasyPubModern-v1.48.0-heading-guard-win-x64\bin\kindlegen_v2.9.exe" "$out\bin\"
Copy-Item "$r\outputs\EasyPubModern-v1.48.0-heading-guard-win-x64\config.xml" "$out\"
Compress-Archive -Path "$out\*" -DestinationPath "$out.zip" -CompressionLevel Optimal
& 'D:\software\Inno Setup 7\ISCC.exe' "$r\installer\EasyPubModern.iss"
```

预期产物：exe ≈ 67.6 MB，zip ≈ 64.9 MB，Setup ≈ 64.4 MB。
**校验**：zip < 20 MB 说明 `Select-Object -First` 掐断了管道（`NEXT_TASK.md` §4.4）。

---

## 8. 诊断工具

```powershell
& $dotnet run --project "$r\work\chapter-audit" -c Release -- <txt路径> <模式>
```

模式：`auto` / `repair` / `probe <行号>` / `tree` / `sources` / `reference`（见 `NEXT_TASK.md` §6）。

本轮新增（**未纳入 git**，`work/` 被 `.gitignore` 排除）：

```powershell
& $dotnet run --project "$r\work\workbench-probe" -c Release -- <txt路径>   # 断点与提醒明细
```

**测试语料**：`C:\Users\13168\Desktop\novel\so-novel\*.txt`（37）+ `tomato\*.txt`（7）。
部分语料本身不完整，看到"缺 N 章"先核对源文件本身有多少章。

---

## 9. 相关文档

| 文件 | 内容 |
|---|---|
| **本文档** | v1.55.x 的交接入口 |
| `docs/2026-09-15_功能逻辑与双模式设计.md` | **模块地图 + 双模式对照 + 想改某处时看哪里** |
| `NEXT_TASK.md` | v1.24→v1.55.0 的长档案、逐条设计决策、环境事实 |
| `docs/HANDOFF.md` | 更早的支线档案（v1.24→v1.48） |
| `docs/2026-09-15_目录获取耗时取证与预算-v1.49.0.md` | 性能取证：分阶段实测数据 |
