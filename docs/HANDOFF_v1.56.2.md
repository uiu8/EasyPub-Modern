# 交接报告：v1.56.0 → v1.56.2（界面细节修复）

**谁写的**：DSH。**时间段**：读取 Codex 的 `HANDOFF_v1.56.0.md` 之后，到现在的全部工作。
**给谁**：接手 EasyPub Modern 支线的下一个 agent。

---

## 0. 一句话

我这一轮**只做了界面细节**：三次用户反馈 → 三次修改 → 两次发布（v1.56.1、v1.56.2）。
**没有触碰识别、修复、记录、守恒这些核心逻辑**——Codex 在 v1.56.0 交付的那套保持原样。

**先读 §3（我判断错的两次）**，那两条比修了什么更值得看。

---

## 1. 现状

| 项 | 值 |
|---|---|
| 工作目录 | `D:\software\DeepSeek-Harness\workspace\easypub-branch` |
| 原仓库 | `C:\Users\13168\Documents\Codex\2026-08-22\easypub` **不得修改** |
| 基线 | v1.56.0（Codex 的 `1c8370b`） |
| 最新提交 | `c00350a` docs: 发布说明补记 v1.56.2（侧边栏对齐）与两条教训 |
| 当前版本 | `1.56.2` |
| 工作区 | 干净 |
| 测试 | Core **331** + Desktop **120** 全绿 |
| 交付物 | `outputs/EasyPubModern-v1.56.2-sidebar-align-win-x64/`（exe 67.60 MB）<br>`outputs/EasyPubModern-v1.56.2-sidebar-align-win-x64.zip`（64.93 MB）<br>`outputs/EasyPubModern-Setup-v1.56.2-x64.exe`（64.44 MB） |

`1c8370b` 之后的提交（全部在本地，**未 push**）：

```
c00350a  docs: 发布说明补记 v1.56.2（侧边栏对齐）与两条教训
c3da758  fix: 侧边栏底部导航未与上方四项左对齐 (v1.56.2)
e7c383a  docs: v1.56.1 发布说明（含 kindlegen 源目录已消失导致的漏打包事故）
efee967  chore: 版本号升到 1.56.1
376b7b6  fix: 侧边栏图标、快照横幅文案、转换设置面板滚动留白
```

改动的文件只有 6 个（`git diff --stat 1c8370b..HEAD`）：

| 文件 | 改了什么 |
|---|---|
| `src/EasyPub.Desktop/MainWindow.xaml` | 侧边栏图标字形 + 两个 `StackPanel` 加 `HorizontalAlignment="Stretch"` + 补 `Tag` 为空的 `Trigger` |
| `src/EasyPub.Desktop/MainWindow.xaml.cs` | 恢复快照横幅文案一句 |
| `src/EasyPub.Desktop/ConversionSettingsWindow.xaml` | 5 个页签滚动内容下边距 16 → 40 |
| `src/EasyPub.Desktop/EasyPub.Desktop.csproj` | 版本号（三处） |
| `installer/EasyPubModern.iss` | `AppVersion` + `PublishDir` |
| `docs/RELEASE_v1.56.1.md`（新增） | 发布说明，含两条教训 |

---

## 2. 做了什么（按时间顺序）

### 2.1 用户第一次反馈：侧边栏「转换记录与验收」的图标像坏字形

我先排除了"字体缺失"这个最可能的解释，方式是用字体文件本身验证而不是猜：

- `E9D5` 在 `Segoe Fluent Icons` 与 `Segoe MDL2 Assets` 中**都存在**（用 `GlyphTypeface.CharacterToGlyphMap` 逐码点查）
- 5 个导航项共用同一个 `SidebarNavigation` 样式

字形本身是**带对勾的待办清单**，与另外四项（书本／盾牌／图片／上传箭头）不同族，语义也不是"记录"。
于是改成 `E8FD`（列表）。**但这不是用户真正看到的问题**——见 §3.1。

顺带补上 `SidebarNavigation` 缺失的"无图标时折叠占位" `Trigger`（`MiniNav` 一直有，它没有）。

### 2.2 我主动审查其他界面

用户让我自己看。我的做法：启动应用、逐个页面截图、放大细节看。视觉模型服务当时不可用
（zai 后端配置错误、ovh 限流），所以是我逐张看图判断的。

发现的**真问题**（当时未修）：

1. **转换设置面板滚到底被底栏压住**：`ConversionSettingsWindow` 根布局是三行 Grid（86 / * / 72），
   底栏是独立固定行，而 5 个页签的滚动内容下边距只有 16，滚到底时最后一行贴住底栏。→ 加到 40
2. **恢复快照横幅文案断句不通**：「暂不恢复不会删除快照」→「**选「暂不恢复」不会删除这份快照。**」

发现的**误报**（我自己纠正的）：

- 「制作设置 → 排版」左侧小节导航"页边距"那行看起来文字偏下——放大 4 倍后看清，
  那是**选中的单选钮**（实心圆点），圆点视觉重心天然偏上、且比其他图标矮。每行其实都是居中对齐的。

覆盖到的页面：书库／检查与修复／制作设置·排版／转换与验收／任务中心。
**没覆盖**：对话框与子窗口（章节工作台、任务中心详情、设置弹窗、书源管理、封面灯箱）、
悬停/禁用/加载/深色主题等交互态。

### 2.3 发布 v1.56.1

版本号四处改齐 → 清 Core 的 `obj`/`bin` → publish → 补 kindlegen 与 config.xml → 压 ZIP → ISCC → 冒烟。
**打包时拦下一次事故**，见 §3.2。

### 2.4 用户第二次反馈：侧边栏那处仍然不对

这次我停止目测，改为**量坐标**：用同一窗口基准的两次 `PrintWindow` 截图对比。

```
上面四项   图标 x ≈ 60   文字起点 x ≈ 95
底部项     图标 x ≈ 68   文字起点 x ≈ 107   ← 整条右移约 12px
```

根因：底部导航所在的 `StackPanel`（`Grid.Row="3"`）**没有撑满父 `Grid`**，
整条少约 12px 宽度，把图标和文字一起推向右侧。

修法：给两个导航 `StackPanel` 显式加 `HorizontalAlignment="Stretch"`。
复测（**发布版**窗口截图）：底部项图标 x ≈ 61、文字 x ≈ 98，与上方四项对齐。

### 2.5 发布 v1.56.2

同上流程，产物见 §1。`FileVersion = 1.56.2.0`，冒烟通过。

---

## 3. 我判断错的两次（**重点读**）

### 3.1 把"错位"看成"字形不对"，白改一版

用户第一次发截图时，我盯着图标形状判断"这个字形选得不合适"，换了字形就交付了。
用户第二次反馈才说明他看到的其实是**整条错位**。

代价：v1.56.1 里那半个修复是无效的，等于多花一轮发布。

**教训：视觉问题先量坐标，再判断是什么问题。**
把坐标摆出来，12px 的偏移立刻可测；而"看着怪"会把注意力引向字形、颜色这些无关的地方。
我用 `PrintWindow`（不依赖窗口是否在前台）+ 像素对比做的复测，这套方法应该成为默认动作。

### 3.2 签名里的路径早已失效，`Copy-Item` 静默失败差点发出残包

所有历史文档（`NEXT_TASK.md`、几份 HANDOFF）都把 KindleGen 的复制源写成
`outputs/EasyPubModern-v1.48.0-heading-guard-win-x64/`。**这个目录已经不存在了。**

后果：两次 `Copy-Item` 失败，但**脚本不中断**（仍返回成功），流程"顺利"走完，
产出的 zip 只有 **62.37 MB**（正常 64.93 MB）——少了 kindlegen 的 7.54 MB。我是靠**体积对不上**发现的。

处置：

- 改从**最近一次**产物复制（本次用 `v1.56.0-reviewable-repair`），并先核对多份副本一致性：
  现存 4 份 kindlegen 大小与哈希完全相同（7908160 字节 / SHA-256 前缀 `A5DD234180344A32`）
- **打包后必须校验 zip 体积**：~64.9 MB 正常；跌破 ~63 MB 就是漏了 kindlegen

### 3.3 附带一次环境限制（不是我的错，但会影响你）

这台机器上 **League of Legends 长期占着前台**。我试过 `SetForegroundWindow`、
`BringWindowToTop`、`AppActivate`、键盘 `SendKeys`，**都无法把应用窗口切到前台**，
键盘输入也被截走。

结论：**点击、滚动这类需要前台的交互，我没法自动化验证**；`PrintWindow` 能拿到画面但只能看静态状态。
如果你也要做 UI 复验，直接上 `PrintWindow`，别在前台争抢上花时间。

---

## 4. 验证状态（如实说明）

| 项 | 状态 |
|---|---|
| 恢复快照横幅文案 | ✅ 在运行中的应用里读到新文案确认 |
| 侧边栏图标与对齐 | ✅ v1.56.2 用**发布版**窗口截图逐项量过坐标 |
| 转换设置面板滚动留白 | ⚠️ **未复验**——前台被占，点击与滚动无法自动化（§3.3） |

测试：Core **331** + Desktop **120**。

⚠️ 注意 `LongTextPerformanceTests` 是**负载敏感**项：首跑偶发失败（预算 400ms，实测会到 415–475ms），
单独重跑与整包重跑均通过。`NEXT_TASK.md` §4.4 明确写了**不要为了让它变绿而放宽预算**，单独重跑即可。

---

## 5. 待办

1. **手测转换设置面板滚到底的留白**（`4 转换与验收` → 右侧面板滚到最底 →
   最后一行应完整可见，不再被"恢复默认／取消／应用设置"底栏压住）。若仍有遮挡，
   按实际像素继续加下边距——我给的值是 40。
2. 我**没覆盖**的界面：各类对话框与子窗口、悬停/禁用/加载/深色主题。建议直接让用户
   遇到哪个界面对劲就截图——本轮两个真问题都是用户截图直接定位的。
3. Codex 在 `HANDOFF_v1.56.0.md` §3 标注的其他待办（Kindle 真机验收等）仍然有效，我没有动。

---

## 6. 发布流程（本机实测版，照做不会漏）

```powershell
$dotnet='D:\software\dotnet-sdk-10.0.302\dotnet.exe'
$r='D:\software\DeepSeek-Harness\workspace\easypub-branch'

# 0) 版本号四处一起改：csproj 的 Version / AssemblyVersion / FileVersion
#    + installer/EasyPubModern.iss 的 AppVersion 与 PublishDir
#    漏掉 AssemblyVersion 时窗口标题仍显示旧版本；漏掉 PublishDir 时 ISCC 报 "No files found"

# 1) 停掉正在运行的应用（它会锁住 bin 下的 dll，publish 报 MSB3027）
Get-Process -Name 'EasyPub.Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

# 2) 改过 Core 就先清，否则可能 publish 出旧逻辑（MSBuild 会静默跳过 CoreCompile）
Remove-Item "$r\src\EasyPub.Core\obj","$r\src\EasyPub.Core\bin" -Recurse -Force -ErrorAction SilentlyContinue

# 3) publish
$out="$r\outputs\EasyPubModern-v<版本>-<代号>-win-x64"
& $dotnet publish "$r\src\EasyPub.Desktop\EasyPub.Desktop.csproj" -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out

# 4) 补 kindlegen 与 config.xml —— 源用"最近一次产物"，别再用 v1.48.0（已不存在）
$src="$r\outputs\EasyPubModern-v1.56.0-reviewable-repair-win-x64"
New-Item -ItemType Directory -Path "$out\bin" -Force | Out-Null
Copy-Item "$src\bin\kindlegen_v2.9.exe" "$out\bin\" -Force
Copy-Item "$src\config.xml" "$out\" -Force

# 5) 压包并**校验体积**（这一步是闸门：~64.9 MB 正常，跌破 ~63 MB 就是漏了 kindlegen）
Compress-Archive -Path "$out\*" -DestinationPath "$out.zip" -CompressionLevel Optimal
Get-Item "$out.zip" | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,2)}}

# 6) 安装包
& 'D:\software\Inno Setup 7\ISCC.exe' "$r\installer\EasyPubModern.iss"

# 7) 冒烟：跑发布版的 exe，确认标题版本号正确
```

预期体积：exe ≈ 67.6 MB、zip ≈ 64.9 MB、Setup ≈ 64.4 MB。

---

## 7. 相关文档

| 文件 | 内容 |
|---|---|
| **本文档** | v1.56.0 → v1.56.2 的交接报告（读这一个就能接） |
| `docs/HANDOFF_v1.56.0.md` | Codex 的 v1.56.0 交付说明（含它的验证结果与待办） |
| `docs/RELEASE_v1.56.1.md` | v1.56.1/v1.56.2 的发布说明与两条教训 |
| `docs/2026-09-15_功能逻辑与双模式设计.md` | **模块地图 + 有目录/无目录双模式对照 + 想改某处时看哪里** |
| `docs/HANDOFF_v1.55.2.md` | 我上一轮的交接（章号解析修复、断点标注、目录获取入口） |
| `docs/NEXT_TASK.md` | v1.24→v1.55.0 的长档案（§4.4 有两处结论已被后续更正） |

**铁律照旧**：原仓库不动、绝不 push、原始 TXT 不原地修改、删除必须备份 + 逐行清单、
缺章如实报告且不发明章节、未经明确要求不写 Memorix、改完让用户截图确认。

**本轮新增一条**：**视觉问题先量坐标再下结论**（§3.1 的代价换来的）。
