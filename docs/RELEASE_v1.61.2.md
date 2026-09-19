# v1.61.2 · honest-footer

一版**修界面说假话**的版本，外加一条把测试基础设施的地基钉住的探针。

## 一、工作台底栏不再说假话

底栏原来无条件写「原始 TXT 不变 · 按章节树顺序输出」。但默认设置下修复是**会写回 TXT** 的
（`DefaultRepairLandingMode` 的默认值就是 `EditSource`）—— 于是**执行完一次改原文的修复，
底栏仍然写着「不变」**。

这不是排版问题，是界面在说假话，而且说的正是三条约束里**「可核对」**那一条最要紧的话：
用户据此判断"这次不会动我的 TXT"，然后它动了。

现在它说的是**这一次的落地方式**：

```
本次修复：改原文（会写回 TXT，改动前自动备份） · 已保存；返回后自动保存…
本次修复：只改章节树（不碰 TXT） · 已保存；返回后自动保存…
```

记的是**"刚刚真的做了什么"**，不是"设置里写着什么"——这两者会分叉（确认窗里可以临时改），
而底栏该说的是前者。

新增验收 `WorkbenchFooterTruthTests`：**读真实窗口里控件的实际文字**，钉三条 ——
不许再出现「原始 TXT 不变」这种绝对说法；必须说清落地方式；
说到「改原文」时必须同时提到「备份」（那是可核对的凭据）。

## 二、把设计样式收成一份（为下一步铺路）

新增 `src/EasyPub.Desktop/Controls.xaml`，25 个具名样式。

先纠正一个判断：**设计 token 本来就有**，在 `App.xaml` 里（暖纸色系）。
真正缺的是**具名样式** —— 全应用只有 `PrimaryButton` 一个，其余按钮都得在窗口里自己写 setters。
工作台那一屏因此有 6 个等重按钮排成一行（下面还有 3 个）。

**这一步只新增、不搬家**：现有样式被 19 个对话框依赖，动它们没有价值，却会让每屏都要重验一遍。

新增 `ControlStyleContractTests`（6 条），针对两种**不说话**的坏法：
`BasedOn` 键名写错（走 `StaticResource`，会抛）与 `DynamicResource` 画刷键名写错
（**不抛**，只留 null，界面照开、那块没颜色）。以及一条判据：
载入真实 `App` 之后那 25 个样式键全部解析得到。

## ⚠️ 三、没做完的部分，以及为什么（重要）

**工作台顶部的重排没有做。** 计划是把 6 个等重按钮收成 1 实心 + 1 次 + 一行 ghost，
三行 12.5px 小字并成一行 chip。改了，打翻 25 条测试，**已回退**。

卡住的原因是一个**测试基础设施的缺陷，不是样式问题**：

> Desktop 的窗口测试**跑在没有应用级资源的环境里**。
> `Application.Current` 是进程级静态量，每个测试文件各写各的
> `if (Application.Current is null) _ = new Application();`，而建裸 `Application`
> **不载 `App.xaml`**。于是合并字典、画刷、`PrimaryButton` 全都不存在。
> `DynamicResource` 缺键**不抛**，所以这个洞一直躲着 ——
> 直到某个窗口用 `StaticResource`（它会抛）才炸，并且**按执行顺序偶发**。

这一版把它的机理查清了，并留下判据（`ControlStyleContractTests` 里那条
"Type 键有主题字典兜底、字符串键没有"—— 它解释了为什么 `{x:Type Button}` 能解析而
`Chip` 不能，也就解释了那些宿主到底有没有资源）。

**修法也已确定，但没做**：让所有窗口测试宿主统一建真实 `App`。
试过，**反而多红 6 条** —— 真实 `App` 会带上 `Window` 隐式样式里的
`EventSetter`（`Window_Loaded`），那会做真事。这一层没有查清，所以**没有硬塞**。

完整记录与"从哪里接着查"见 `docs/HANDOVER.md` §11.3.0。
另外 `MainWindowLayoutTests` 有 1 条红（`Imported_txt_is_automatically_analyzed…`
期望「所选检查通过」），**怀疑**是本轮 Core 新增的预检提醒改了主窗口状态文案，
**但未确认**，所以列在这里而不是当作已知无关。

## 四、顺带更正文档里三处我自己的错

`docs/HANDOVER.md` 里记了：

- **视觉模型不能验收文案，只能验收布局** —— 同一句底栏文字，模型转写 45 字里错 6 处，
  且每个替换都是意思相近的常用词（`备份→保存`）。**危险性不在于错，在于错得像对的。**
- P0-5 的结论当天被自己推翻：我写"不能静默升级，因为用户可能特意选了原版兼容"，
  去查那条路——**它不存在**。最终做的**就是原计划**。
- 界面轨道的前置条件（上面第三节那件事）。

## 验证

- Core：**854 通过 / 1 失败**。唯一失败是 `LongTextPerformanceTests`
  （给 28 MB 长篇设的墙钟预算，全量跑时超时，**单独跑通过**）。已独立复现过原因。
- Desktop：`ChapterBatchTests` 21 / `ChapterIssueActionTests` 23 / `MainWindowLayoutTests` 61
  / `ControlStyleContractTests` 6 / `WorkbenchContextBarTests` 3 / `WorkbenchForgetCatalogTests` 2
  / `WorkbenchFooterTruthTests` 1 / `ConversionSettingsDraftTests` 5 全部通过。
- 唯一一条例外是 `MainWindowLayoutTests` 里那条待查的红。

## 下载

- 安装版：`EasyPubModern-Setup-v1.61.2-x64.exe`
- 便携版：`EasyPubModern-v1.61.2-honest-footer-win-x64.zip`（解压后运行 `EasyPub.Desktop.exe`）
