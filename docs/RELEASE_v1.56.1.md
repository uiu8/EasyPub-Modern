# RELEASE v1.56.1 → v1.56.2 — 界面细节修复

日期：2026-09-15。基线：v1.56.0（`1c8370b`）。本次只动界面细节，不改识别、修复与记录逻辑。

> **v1.56.2 是对 v1.56.1 的补正**：v1.56.1 只换了图标字形，没解决用户真正看到的错位。
> 侧边栏那处请以 v1.56.2 为准，详见 §2 第 1 条与 §4 第 3 条。

## 0. v1.56.2 交付物（当前版本）

| 产物 | 大小 |
|---|---|
| `outputs/EasyPubModern-v1.56.2-sidebar-align-win-x64/` | exe 67.60 MB |
| `outputs/EasyPubModern-v1.56.2-sidebar-align-win-x64.zip` | 64.93 MB |
| `outputs/EasyPubModern-Setup-v1.56.2-x64.exe` | 64.44 MB |

exe `FileVersion = 1.56.2.0`；发布版冒烟通过，标题 `EasyPub Modern v1.56.2`。

## 1. v1.56.1 交付物

| 产物 | 大小 |
|---|---|
| `outputs/EasyPubModern-v1.56.1-ui-fixes-win-x64/` | exe 67.60 MB |
| `outputs/EasyPubModern-v1.56.1-ui-fixes-win-x64.zip` | 64.93 MB |
| `outputs/EasyPubModern-Setup-v1.56.1-x64.exe` | 64.44 MB |


exe `FileVersion = 1.56.1.0`；发布版冒烟通过，窗口标题 `EasyPub Modern v1.56.1`。
v1.55.x 与 v1.56.0 的产物保留。未 push，未修改原仓库，未写入 Memorix。

## 2. 修了什么

### 侧边栏「转换记录与验收」的图标与对齐

用户**两次**反馈这一处。

第一次：图标看起来像坏字形。查证后确认**不是字体缺失**：

- `E9D5` 在 `Segoe Fluent Icons` 与 `Segoe MDL2 Assets` 中都存在（用 `GlyphTypeface.CharacterToGlyphMap` 逐码点验证）
- 5 个导航项共用同一个 `SidebarNavigation` 样式

字形本身是**带对勾的待办清单**，与另外四项（书本／盾牌／图片／上传箭头）不同族，语义也不是"记录"。
改为 `E8FD`（列表）。**但这没有解决用户真正看到的问题。**

第二次：用户又发来截图，指出仍不对。这次改为**量坐标而不是目测**，
用同一窗口基准的两次 `PrintWindow` 截图对比：

```
上面四项图标 x ≈ 60，文字起点 x ≈ 95
底部项    图标 x ≈ 68，文字起点 x ≈ 107   ← 整条右移约 12px
```

根因：底部导航所在的 `StackPanel`（`Grid.Row="3"`）没有撑满父 `Grid`，
整条少约 12px 宽度，把图标和文字一起推向右侧。v1.56.1 新加的
"无图标时折叠占位" `Trigger` 不是原因（底部项始终有 `Tag`）。

修法：给两个导航 `StackPanel` 显式加 `HorizontalAlignment="Stretch"`。
v1.56.2 复测：底部项图标 x ≈ 61、文字 x ≈ 98，与上方四项对齐（已用发布版截图确认）。

顺带补上 `SidebarNavigation` 缺失的"无图标时折叠占位"处理——`MiniNav` 一直有，它没有；
将来给底部区加一个无 `Tag` 的导航项时会渲染出空白图标位。

### 恢复快照横幅的文案

原文「6 本书，保存于 …。暂不恢复不会删除快照。」断句不通、主语缺失，
容易被读成别的意思。改为「…。**选「暂不恢复」不会删除这份快照。**」

### 转换设置面板滚到底被底栏压住

`ConversionSettingsWindow` 根布局是三行 Grid（86 / * / 72），底栏是独立固定行；
5 个页签的滚动内容下边距只有 `16`，滚到底时最后一行贴住底栏，露出被压住的一小条。
统一加到 `40`。

## 3. 验证状态（如实说明）

| 项 | 状态 |
|---|---|
| 横幅文案 | ✅ 在运行中的应用里读到新文案确认 |
| 侧边栏图标与对齐 | ✅ v1.56.2 用**发布版**窗口截图逐项量过坐标（底部项图标 x≈61 对上面四项 x≈60） |
| 转换设置滚动留白 | ⚠️ **未复验**——本机前台被其他程序长期占用，`SetForegroundWindow` / `BringWindowToTop` / `AppActivate` / 键盘 `SendKeys` 都无法把窗口切到前台，而点击与滚动必须在前台才能生效 |

测试：Core **331**（`LongTextPerformanceTests` 为负载敏感项，首跑偶发失败、单独重跑与整包重跑均通过）
+ Desktop **120** 全绿。

## 4. 本次踩到的坑（重要）

**4.1 `outputs/EasyPubModern-v1.48.0-heading-guard-win-x64/` 已经不存在了。**
历次文档都把 KindleGen 的复制源写成这个目录，本次照做时两次 `Copy-Item` 静默失败，
产出的 zip 只有 **62.37 MB**（正常 64.93 MB）——**少了 kindlegen 的 7.54 MB**，这个包不能发布。

处置与建议：

- 从**最近一次**产物复制：`outputs/EasyPubModern-v1.56.0-reviewable-repair-win-x64/bin/kindlegen_v2.9.exe`
  （实测现存 4 份副本大小与哈希完全一致：7908160 字节 / SHA-256 前缀 `A5DD234180344A32`）
- **打包后必须校验 zip 体积**：~64.9 MB 正常；跌破 ~63 MB 就是漏了 kindlegen
- `Copy-Item` 失败在这条链路上不会中断脚本（仍返回成功），所以体积校验是必要的一道闸

**4.2 目测"图标怪"会让人改错东西。**
用户第一次反馈时，我盯着字形判断"这个图标选得不合适"，换了字形就交付了；
用户第二次反馈才说明他看到的其实是**错位**（底部整条右移 12px）。
**视觉问题先量坐标、再判断是什么问题**——把坐标摆出来，12px 的偏移立刻可测，
而"看着怪"会把注意力引向字形、颜色这些无关的地方。

## 5. 后续

`docs/HANDOFF_v1.56.0.md`（Codex 的交接说明）与本文档共同构成当前接手入口；
v1.55.2 之前的档案见 `HANDOFF_v1.55.2.md`、`NEXT_TASK.md` 与
`2026-09-15_功能逻辑与双模式设计.md`。

**仍然待办**：转换设置面板滚动留白那一处需要有人手测（见 §3）。
