# 交接说明：这条支线是什么、怎么接着做

写给下一个接手的人（Codex 或任何 agent）。**先读这一页，再动代码。**

---

## 1. 这条支线在哪，和原仓库什么关系

| | 路径 |
|---|---|
| **支线（活的，在这里改）** | `D:\software\DeepSeek-Harness\workspace\easypub-branch` |
| **原仓库（不要动）** | `C:\Users\13168\Documents\Codex\2026-08-22\easypub` |

- 支线是原仓库的**完整拷贝**，`.git` 保留，remote 仍指向 `https://github.com/uiu8/EasyPub-Modern.git`
- **原仓库 HEAD 没有前进**：仍停在 `579fabe`（提交时间 2026-09-12），**但工作区有 66 个未提交改动**，
  最近修改时间是 **2026-09-14 18:23**（产物是 v1.25.0）。也就是说原仓库**不是完全没被动过**
  ——这与本文档早先「自 2026-08-29 起未被修改」的说法不符，接手时请以 `git status` 实际输出为准。
  **改造仍然只在支线做**；原仓库那批未提交改动的去留，由用户决定，不要动。
- 支线里 `.git` 的 HEAD 已经前进到 `3a5d08f feat: 章节对齐、书源系统与一键修复报告 (v1.24.0 - v1.48.0)`
  （早期文档写的 `579fabe` 已过时）
- **v1.24 → v1.49 的工作**：`3a5d08f` 之前的部分仍是未提交的工作区改动，v1.49.0 也在工作区里

### 这意味着什么

Git 历史**看不到 v1.24 之后的这些版本**：`git log` 里只有 `3a5d08f` 那一条汇总提交，
之后的改动在工作区里。
想知道"某个东西为什么是这样"，**只能靠这份文档 + `docs/` 里的实施记录 + 直接读代码**。

> 提交与否是用户的决定，未经允许**不要 push**（remote 是用户线上的仓库）。

---

## 2. 交付物

`outputs/` 下每个版本一对：

```
EasyPubModern-v<版本>-<代号>-win-x64.zip     便携版（含 bin/kindlegen_v2.9.exe）
EasyPubModern-Setup-v<版本>-x64.exe          安装包（Inno Setup）
EasyPubModern-v<版本>-<代号>-win-x64/        publish 中间目录
```

**该目录已累积到约 2.6 GB**，20 个版本的中间目录和 zip 都在。清理前先问用户。

---

## 3. 版本演进（v1.24 → v1.44）

| 版本 | 代号 | 做了什么 |
|---|---|---|
| v1.24.0 | catalog-assist | 目录辅助修复：抓公开目录、对齐、补建漏识别章节 |
| v1.41.0 | multi-source | 多书源发现（和图书/书读谷/爱下/偶书） |
| v1.42.0 | qidian-cookie | 起点源 + 用户提供的 Cookie；起点目录带真实分卷 |
| v1.43.0 | book-sources | 书源系统：可排序、可启停、可自定义、可检测可用性 |
| v1.43.1 | alignment-fix | 修三处对齐根因（见 §4.2、§4.3、§4.4） |
| v1.43.2 | chapter-number | 修章号正则（见 §4.5） |
| v1.44.0 | fuzzy-locate | 模糊匹配找漏章 + 修首页两个 UI 问题 |
| v1.45.0 | repair-integrity | 修复完整性校验（`RepairIntegrity.Verify`）|
| v1.46.0 | heading-workflow | 标题工作流 |
| v1.47.0 | repair-report | 一键修复的分类报告（`RepairReportGroup`）|
| v1.48.0 | heading-guard | 标题守卫 |
| **v1.49.0** | **locate-timing** | **定位分段计时 + 网络时间预算**（见 §4.9）|

v1.22 及之前是 Codex 在原仓库做的工作，记忆在 Memorix 的 `kindle` 项目里（v1.21.10 截止）。

---

## 4. 关键设计决策（含被否决的方案）

这一节是这份文档存在的主要理由——**这些推理不在代码注释里，也不在 git log 里。**

### 4.1 选源规则：从"卷数最多"改回"书源顺序"

曾经的规则是在章数达标的候选里取**卷数最多**的，理由是想拿到更细的分卷。

**被实测否决**。反例（《疯巫妖的实验日志》）：

```
[和图书]                卷 0  章 837
[偶书网 → biquge365]    卷 0  章 856
[偶书网 → biqusa]       卷 1  章 851   ← 旧规则选中它
```

被选中的那个目录，**前五个"章节"全是作者的推广语**：

```
C [-] 巫妖后第一本西幻千面之龙已发书
C [-] 新书《我怎么还活着》已经十万+
C [-] 合作新书地下城堡：前夜已上线
C [-] 那啥，新书疯骑士的宇宙时代，已发起点
C [-] 番外 第四章 简单任务:寻找走丢的母亲
```

它的"卷名"是 `《疯巫妖的实验日志》正文`——把书名和"正文"拼在一起，本身就是结构不可信的信号。

**现在的规则**（`ChapterAutoRepair.RepairAsync`）：在 `章数 ≥ 最全者 90%` 的候选里，**取书源顺序的第一个**。`DiscoverAsync` 已经按用户配置的顺序返回，所以直接 `FirstOrDefault()`。
这也正是用户的要求：「默认所有的书优先筛选起点和番茄」。

### 4.2 番茄：目录能拿，搜索拿不到

**分成两件事，不要混为一谈：**

| 能力 | 需要签名吗 | 实测 |
|---|---|---|
| 目录页 `https://fanqienovel.com/page/<bookId>` | **不需要** | 服务端渲染，直出完整分卷 |
| 同域接口 `/api/book/info`、`/api/rank/recommend/list` | **不需要** | 返回真实 JSON |
| **关键词搜索** `/search/<书名>` | **需要** | 拿不到 |

实测《十日终焉》目录页 638 KB → **10 卷 1496 章**，卷名是 `第一卷：我听到了你们` … `第十卷：这一切的终焉`，不是占位符。

搜索为什么放弃（**已经试到底，不要重走**）：

1. 搜索页是 SPA，数据走 `api5-normal-sinfonlineb.fqnovel.com/reading/bookapi/search/page/v`
2. 把 `aid / channel / app_name / version_code / device_platform / device_id / iid / filter / page_count / page_index / query_type` 全部补齐，服务端仍返回 `{"code":100103,"message":"PARAM_INVALID"}`——缺的是 `a_bogus`/`msToken` 这组字节风控签名，**不是设备号**
3. 用系统 Edge 无头渲染搜索页（真实 UA、`--disable-blink-features=AutomationControlled`、10 秒虚拟时间），页面渲染完成，正文却是 **"共 0 项相关的结果"**——风控识别了无头环境并返回空集

**结论**：番茄源定位为 **"仅网址/编号"**（`BookSource.DirectUrl`），不做关键词搜索。
用户在书名框粘贴 `https://fanqienovel.com/page/xxxxx` 或直接填 ID 即可读出分卷目录。
`DiscoverAsync` 开头会识别粘贴的公开书籍网址，这一条对起点、和图书同样有效。

### 4.3 定位器：章号一致必须压过标题词一致

`ReferenceOutline.PairScore` 的打分里，**章号不同但标题词完全相同**（WordsScore）**高于** **章号相同但标题词差一点**（PartialScore）。

后果（同一本书）：

```
参考目录: 第十六章 预谋
第 1002  行: 第十六章 预谋（新）      → 章号对、词差一点 → PartialScore
第 23690 行: 第二百五十七章 预谋      → 章号差 241、词全同 → WordsScore  ← 选中了它
```

行号一跳几千行，"按参考目录顺序重排"就会让正文互相跨越大段内容，**整棵树看着就是乱的**。

**修复**：`ReferenceLocator` 的排序加首位键 `NumberAgrees`（两边都有章号且相等）。
第三遍兜底搜索同样优先命中章号一致的行。

### 4.4 目录外、又没章号的短行不是章节

源 TXT 末尾常有作者的话：

```
61735| 那啥，新书疯骑士的宇宙时代，已发起点      ← 没有全角缩进
61739| 合作新书地下城堡：前夜已上线
61743| 新书《我怎么还活着》已经十万+
```

正文段落都带 `　　` 缩进，这几行没有，上下文又是空行——正好命中"孤立短行"的标题特征，于是被当成章节塞进树里。

**修复**：`ReferencePlanner.Apply` 里，**官方目录没列出、且标题里没有章号**的条目直接跳过，内容自然并回前一章。
**有章号的未列出章节（加更、番外）照样保留**——目录不完整时那些不能丢（有反向测试保护）。

### 4.5 章号正则："第"必须是可选的

`ReferenceOutline` 原本写死要求「第」：

```csharp
@"第\s*(?<n>[0-9零〇一二三四五六七八九十百千万两]+)\s*[章回节]"   // 旧
@"(?:第\s*)?(?<n>…)\s*[章回节]"                                    // 新
```

大量 TXT 的标题**没有「第」**，例如 `八百三十章 赌徒`。旧正则下：

1. 章号解析为空
2. 与参考目录比对得 **0 分**，被 `Score > 0` 过滤
3. 正文并进前一章（第 829 章膨胀成 387 行）→ 表现为「829 → 836 跳章」

修好后同一本书：`缺 53 章` → **`缺 1 章`**，一次找回 52 章。

> **教训**：排查这类问题时，`grep` 的模式本身可能就是错的。当时我用「**第**八百三十章」去搜，零命中，差点得出"源文件真缺章"的错误结论。

### 4.6 模糊匹配的边界

第三遍兜底搜索现有**三级证据**，从强到弱：

| 级别 | 依据 | 能救什么 |
|---|---|---|
| 1 | 章号一致 | 标题被改、增删字、甚至没标题 |
| 2 | 标题词精确包含 | 章号写错，标题词完好 |
| 3 | **字符相似度 ≥ 0.67** | **错字、漏字**（`大冒险` → `大冒除`） |

两个约束防止误伤：

- **长度差 > 1 个字符直接跳过**（差太多就是另一章）
- **2 字标题不参与模糊**——「赌徒」和「赌鬼」相似度 0.5，太容易认错

性能：每行的标题解析**预先算一次**（`ParseLineKeys`），否则"每个缺失章节 × 每行"会把正则跑成平方复杂度。

### 4.7 对齐之后，哪些本地检测还说话

`ChapterReviewAnalyzer` 里一旦 `RecognitionSource == "reference"`（树来自参考对齐），就**抑制**这些基于统计的猜测：

```
chapter_unrecognized / chapter_number_order / chapter_heading_typo
chapter_content_duplicate / chapter_duplicate / chapter_repeated_sequence
```

理由：官方目录已经权威回答了"哪些章存在"，启发式再说一遍只是刷屏。

**但 `chapter_number_gap` 不在此列**——对齐之后它不再是"猜"：目录说有 837 章、重建只有 836，那就是**真的缺**。这是最该告诉用户的一条。

### 4.8 一键修复的运行顺序

```
① 读 TXT → ② 备份原始文件 → ③ 找章号重启点
④ 按书源顺序搜目录 → 选源（§4.1）
⑤ ReferencePlanner.Build：三遍定位 + 生成动作
⑥ 预选三类动作：AddChapter / Retitle / RemoveDuplicate
⑦ Apply：正文范围按物理行序，阅读顺序按目录顺序，卷层级来自目录
   目录无卷但检测到重启 → 按重启建卷
   被移除的重复行写入 <书名>.removed.txt
⑧ 逐章核对，算出 missing，如实报出
```

### 4.9 耗时：慢的是网络，不是对齐（v1.49.0 实测）

**曾经有一条错误判断在这里流传**：「《凡人修仙传》14.3 秒是因为 `ReferenceLocator.Locate`
第三遍兜底搜索，1405 章未定位 × 61752 行 ≈ 8700 万次迭代」。**它是错的**，两个数字都不对：

- **61752 行是疯巫妖的**；凡人只有 **1078 行**，真实迭代 **150 万次、实测 92 ms**
- 差 58 倍

真实分布（凡人 `auto`）：

```
目录获取（网络）    7 754 – 27 922 ms   ← 98%，瓶口在这里
对齐 Build                  0 ms        ← 内部 <1 ms
  └ 候选行 / 前两遍 / 兜底   9 / 34 / 89–145 ms
重建 + 核对              ~170 ms
```

由此得到四条可复用的结论：

1. **先量再设计**。「网络只占 1–2 秒」这句话当时从未被验证，却让两个优化方向被反复
   尝试又回滚。分段计时（`ChapterAutoRepair.Timing`）现在常驻，`chapter-audit auto` 直接打印。
2. **跳过兜底搜索是负收益**。它只占 1%，却会丢掉**靠「章号一致」定位到的真实章节**
   （凡人 30 章 → 3 章）。最强的证据往往来自这最不起眼的一遍。
3. **调并发没用**。`MaxConcurrentSources` 4→6 实测无改善（10.0 / 8.8 s vs 8.4 s，已回滚）。
   慢的是单个站的**目录抓取**，不是排队。
4. **网络耗时波动极大**（同一命令 7.8 s → 27.9 s）。**单次测量不足以下结论**，至少跑 3 次。

**v1.49.0 的应对**：`SearchTimeoutSeconds=6`、`SourceBudgetMs=15 000`（单源总额）、
`AnsweredChapters=50`（拿到够大的目录就不再试该源后续候选）。设计上守住三条：
预算只决定「还开不开始下一个候选」（不取消在途请求）；预算内拿到的目录照常参与比较
（**选源结果不受影响**）；被截断的源显示「预算用尽，跳过 N 个候选」，不静默变少。

**还有一件事没做**：目录与文本版本不一致时（凡人 1408 章目录其实来自仙界篇），
报告只会说「未定位 1378 章」，不会说「这个来源和你的文件对不上，建议换源」。
详见 `docs/NEXT_TASK.md`。

> 详细取证与全部原始数据：`docs/2026-09-15_目录获取耗时取证与预算-v1.49.0.md`

---

## 5. 必须遵守的不变量

改任何东西都不能破坏这些：

1. **原始 TXT 永不修改**。所有改动只作用于章节树，且可撤销。
2. **一切改动走 `Mutate`**（工作台的撤销栈）。
3. **删除必须备份**，并生成 `.removed.txt` 逐行清单。
4. **缺章如实报告**。不粉饰、不用好看的百分比糊过去。
5. **不发明章节**。只补建在文本里**真正定位到**的章节。
6. **SSRF 防护**：拒绝回环、私有、链路本地地址和带凭据的网址（`ReferenceCatalogClient.Supported`）。
7. **不写 Memorix**（用户明确要求过；这条支线的记忆刻意留空）。
8. **不动原仓库**。

---

## 6. 已知缺口 / 刻意不做的

| 项 | 状态 |
|---|---|
| 番茄关键词搜索 | **刻意不做**——需逆向字节风控签名，且对方改版即失效（§4.2） |
| 自动换源下载缺失章节 | **刻意不做**——那是 agent 的活，不是产品功能 |
| `C:\Users\13168\Desktop\novel\tomato` 那 7 本 | 显示"无参考目录"（番茄没有关键词搜索）。用户若提供书籍 URL 即可读出分卷 |
| Kindle 实机验证分卷目录 | **从未做过**，只在预览里验证 |
| `.removed.txt` | 单元测试覆盖，真实批次里也生成过，但**没有大规模验证** |
| 首次目录获取从 8 s 再往下压 | **未做**——要动并发抓取或换策略，有被限流风险（§4.9）|
| 「来源与你的文件对不上，建议换源」的提示 | **未做**——目录版本不一致时报告只会说「未定位 N 章」（§4.9）|
| `SourceBudgetMs = 15 s` 在真实慢网络下的表现 | **未充分验证**——三次实测都没触发，价值在兜住尾部 |
| README / GitHub Release | 停在 v1.22.0；`docs/RELEASE_v1.22.1.md`、`v1.22.2.md` 提到的产物**不存在** |

---

## 7. 构建、测试、打包

### 环境陷阱（都踩过）

- **dotnet 不在 PATH**，用 `D:\software\dotnet-sdk-10.0.302\dotnet.exe`
- **`dotnet build EasyPub.Modern.slnx` 会静默失败**（输出 6 行、0 error、2.5 秒退出）——**改用单个 `.csproj`**
- **`dotnet test` 需要 danger-full-access**，否则 testhost 命名管道报 `Win32Exception (5): 拒绝访问`
- **`Select-Object -First N` 会掐断上游进程**——对 ISCC 用过一次，产出一个 0.9 MB 的残缺安装包。**用 `-Last`**
- **KindleGen 要从旧版产物复制**：`outputs/EasyPubModern-v1.48.0-heading-guard-win-x64/bin/kindlegen_v2.9.exe`
  （v1.45 起的产物里都有；旧文档写的 `v1.42.0` 目录**已经不存在了**）
- `LongTextPerformanceTests` 对负载敏感（预算 400 ms）。被计时的那条路径**已经预热**，但如果和 publish 并行跑仍可能超时——单独重跑即可
- 安装包编译：`D:\software\Inno Setup 7\ISCC.exe installer\EasyPubModern.iss`

### 命令

```powershell
$dotnet='D:\software\dotnet-sdk-10.0.302\dotnet.exe'
$r='D:\software\DeepSeek-Harness\workspace\easypub-branch'

# 测试（Core 317 / Desktop 101）
& $dotnet test "$r\tests\EasyPub.Core.Tests\EasyPub.Core.Tests.csproj" -c Release
& $dotnet test "$r\tests\EasyPub.Desktop.Tests\EasyPub.Desktop.Tests.csproj" -c Release

# 联网回归（默认跳过，用环境变量开启）
$env:EASYPUB_LIVE_TESTS='1'

# 发布
& $dotnet publish "$r\src\EasyPub.Desktop\EasyPub.Desktop.csproj" -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o <输出目录>
```

### 版本号只有一个来源

`src/EasyPub.Desktop/EasyPub.Desktop.csproj` 的 `<Version>`。窗口标题和左下角标签都从程序集读（`MainWindow.AppVersion`）。
发布时还需同步 `installer/EasyPubModern.iss` 的 `AppVersion` 和 `PublishDir`。

> 这条是 v1.44.0 才修的：在那之前左下角写死 `v1.42.0`、标题写死另一个值，两个版本号打架。

---

## 8. 诊断一本书

`work/chapter-audit` 是非交付的诊断工具，比在 UI 里点快得多：

```powershell
& $dotnet run --project "$r\work\chapter-audit" -c Release -- <txt路径> <模式>
```

| 模式 | 作用 |
|---|---|
| `auto` | 跑完整 `RepairAsync`，打印参考/重建/缺章数（**和 UI 一键修复同一条路径**） |
| `repair` | 同上，并逐条打印重建后的树（层级、源行号、内容段数） |
| `tree` | 打印对齐后的树 |
| `sources` | 检测每个书源的可用性（和设置对话框同一段代码） |
| `reference` | 打印**各源**返回的目录前 14 / 后 10 条，用来判断源的质量 |
| `<txt> <http-url>` | 直接抓取一个目录网址 |

测试语料：

- `C:\Users\13168\Desktop\novel\so-novel\*.txt`（37 本）
- `C:\Users\13168\Desktop\novel\tomato\*.txt`（7 本，番茄）
- 下载器：`C:\Users\13168\Desktop\so-novel-rs-0.3.6-windows-x86_64\so-novel-rs.exe`（CLI：`search` / `download` / `sources`）

**注意**：部分语料本身不完整（例如《凡人修仙传》只有 0.18 MB / 30 章）。
看到"缺 N 章"先核对**源文件本身有多少章**，再判断是不是软件的问题。

---

## 9. 相关文档

| 文件 | 内容 |
|---|---|
| `docs/2026-09-14_章节功能盘点与提示收敛.md` | 章节清理功能的盘点、方向、提示口径 |
| `docs/2026-09-14_实施验证-目录辅助修复-v1.24.0.md` | 目录辅助修复的实现与验证 |
| `docs/2026-09-14_书源系统-v1.43.0.md` | 书源模型、优先书源、检测、番茄实测数据 |
| `docs/2026-09-14_对齐修复-v1.43.1.md` | §4.3 / §4.4 / §4.1 三处根因的取证 |
| **`docs/2026-09-15_目录获取耗时取证与预算-v1.49.0.md`** | **耗时分段取证：慢的是网络不是对齐；两个「失败方向」为何不该做（§4.9）** |
| **本文档** | 接手入口：演进、决策、不变量、缺口、工具 |

`docs/` 里其余 19 篇属于原仓库 v1.22 及之前的工作。
