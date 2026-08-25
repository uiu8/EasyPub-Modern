<div align="center">

# EasyPub Modern

面向中文小说与 Kindle 用户的现代化 Windows 批量电子书转换器。

保留 EasyPub v1.50 的经典转换效果，同时加入批量任务、逐书封面与元数据、章节树、正文插图、文本清理、项目恢复和成品验收。

[![当前版本](https://img.shields.io/badge/当前版本-v0.21.2-2563EB?style=flat-square)](https://github.com/uiu8/EasyPub-Modern/releases/tag/v0.21.2)
![系统](https://img.shields.io/badge/系统-Windows%20x64-0F6CBD?style=flat-square)
![格式](https://img.shields.io/badge/格式-TXT%20%7C%20EPUB%20%7C%20MOBI-16A34A?style=flat-square)
![处理方式](https://img.shields.io/badge/处理方式-本地处理-7C3AED?style=flat-square)

**[下载最新版](https://github.com/uiu8/EasyPub-Modern/releases/download/v0.21.2/EasyPubModern-v0.21.2-win-x64.zip)** · [查看 Release](https://github.com/uiu8/EasyPub-Modern/releases/tag/v0.21.2)

</div>

![EasyPub Modern v0.21.2 主界面](assets/readme/overview.png)

## 适合谁

- 希望批量把中文 TXT 小说转换为 EPUB 或 Kindle MOBI 的用户。
- 希望尽量延续原版 EasyPub 排版和 MOBI 兼容行为的用户。
- 需要为每本书分别设置封面、作者、出版社、插图和章节目录的用户。
- 希望转换前发现问题、转换后保留检查报告，同时不改写原始书稿的用户。

## 支持的转换

| 输入 | 输出 | 说明 |
|---|---|---|
| TXT | EPUB | 使用 EasyPub 兼容解析与排版流程 |
| TXT | MOBI | 生成 KindleGen 联合 MOBI，并执行兼容后处理 |
| EPUB | MOBI | 可保留原 EPUB 版式，或使用 EasyPub 兼容重排 |

> MOBI 转换需要 KindleGen 2.9。程序会优先使用当前版本配置的路径，也能手动重新选择。带 DRM 的 EPUB 不支持转换。

## 快速开始

1. 从 [最新版 Release](https://github.com/uiu8/EasyPub-Modern/releases/tag/v0.21.2) 下载并解压 Windows x64 压缩包。
2. 运行 `EasyPub.Desktop.exe`。
3. 添加或拖入一个或多个 TXT / EPUB，也可以从收藏文件夹批量选书。
4. 选择 EPUB 或 MOBI，检查封面、书籍信息和排版模式。
5. 点击“转换前检查”，确认无阻断问题后开始批量转换。
6. 在“任务与验收”中查看每本书的进度、问题和输出位置。

程序完全在本机处理文件。文本清理、章节树和插图设置都不会改写原始 TXT。

## 核心能力

| 能力 | 当前版本提供的功能 |
|---|---|
| 批量工作流 | 混合添加 TXT / EPUB、递归导入文件夹、1–4 个并发任务、逐书进度、失败重试与转换历史 |
| 逐书设置 | 每本书独立保存封面、标题、作者、译者、ISBN、出版社、分类、语言、简介、Calibre 自定义元数据、插图和章节树 |
| 封面与图片 | 拖放预览 JPG / PNG / WebP；PNG、WebP 解码后转为高质量 JPEG；插图可选择正文位置 |
| 章节与目录 | 卷／章／节层级目录、数字标题一键规范化、顺序与父子关系调整、目录包含开关 |
| 排版与样式 | 字号、行高、段距、缩进、对齐、四边页边距、嵌入 TTF 字体与定制 CSS |
| 文本清理 | 空行、硬换行、全角空格、章节编号、网站说明、简繁转换与中文标点逐项预览 |
| 项目与恢复 | `.easypubproj` 保存与打开、命名预设、异常退出恢复快照、收藏文件夹和来源目录元数据映射 |
| 检查与验收 | 转换前检查输入、封面、插图、字体、输出冲突和 KindleGen；可选 EPUB/MOBI 结构验收与独立报告目录 |
| 兼容旧工作流 | 可导入原版 EasyPub `config.xml`，并保留原版兼容排版与 MOBI 后处理逻辑 |

## v0.21.2 重点优化

新增 Calibre 自定义元数据与文件夹映射：

- 自定义列的“字段定义”与“当前范围的值”已经分离，创建字段时不再强制填写固定值。
- 定义“Kindle书架”后，逐书信息表和文件夹映射表会直接出现名为“Kindle书架”的可编辑列，不再使用笼统的“自定义列”汇总。
- 新建或编辑文件夹规则时直接显示每个真实字段的输入框，可按文件夹填写不同值。
- 在统一书籍信息、逐书填写和文件夹规则中复用已有字段，并按各自范围灵活填写不同值。
- 检索名可填写 `kindlecollections` 或 `#kindlecollections`，内部统一匹配 `#kindlecollections`。
- 列标题只负责显示，例如“Kindle书架”；匹配已有 Calibre 列时以检索名为准。
- 支持单值文本与逗号分隔文本，文件夹值会按相同检索名覆盖统一值。
- EPUB 中写入 Calibre 可识别的安全自定义元数据；不接受复合模板或可执行脚本。

> MOBI 格式本身不能保存任意 Calibre 自定义列。EasyPub 会保存相关项目和映射设置，但需要输出 EPUB 才能让 Calibre 从电子书文件直接读取这些列。

### 文本清理定位

文本清理不再只是列出变化。点击左侧任意修改记录，右侧会立即定位并高亮处理后的正文：

- 普通替换直接高亮修改后的文字。
- 被删除的广告、下载说明或多余空行会定位到相邻正文，并明确说明原行已删除。
- 超长小说会按选中位置加载附近正文，后半本书不再受预览长度限制。
- 修改记录不再限制为前 500 条，可以检查全部变化。

![文本清理修改记录定位](assets/readme/text-cleanup-navigation.png)

## 三种排版模式

三种模式只控制正文排版基线，不会覆盖封面、元数据、插图、章节树、字体、定制 CSS、KindleGen 或验收设置。

| 模式 | 字号 | 行高 | 段间距 | 首行缩进 | 上/下/左/右边距 | 对齐 | 全角空格 |
|---|---:|---:|---:|---:|---|---|---|
| 原版兼容 | 110% | 120% | 0.6em | 0em | 0/0/3/3px | 默认 | 保留 |
| 现代排版 | 105% | 165% | 0.35em | 2em | 12/12/18/18px | 两端对齐 | 不额外添加 |
| 自定义 | 使用当前值 | 使用当前值 | 使用当前值 | 使用当前值 | 使用当前值 | 使用当前值 | 使用当前值 |

- **原版兼容**：适合希望延续 EasyPub v1.50 正文密度与缩进习惯的书稿。
- **现代排版**：增加行距和留白，更适合高分辨率 Kindle。
- **自定义**：保留当前参数，适合在任一方案基础上继续微调。

手动修改版式后，程序会自动切换到“自定义”，避免模式名称与实际参数不一致。

## 常用高级功能

### 为来源文件夹自动填写元数据

在“书籍信息 → 文件夹元数据映射”中指定来源目录和出版社、分类、语言或 Calibre 自定义列。以后从该目录导入的书会自动带入对应元数据，也可以逐书覆盖。

### 对接 Calibre 自定义列

以 Calibre 中“检索名 `kindlecollections`、列标题 `Kindle书架`”为例：先在“管理字段 / 统一值”中定义该字段；之后“逐书填写书籍信息”和“文件夹元数据映射”都会直接增加“Kindle书架”列。检索名必须与 Calibre 一致，列标题就是 EasyPub 表格中显示的字段名。

### 在正文中插入图片

在“插图”分页选择图片，再从正文预览中指定插入位置。每本小说的插图相互独立，图片缺失或损坏会在转换前检查中提示。

### 使用原版 config.xml

在“高级”分页选择原版 EasyPub 的 `config.xml`。程序会显示已应用项目和暂未实现项目；随时可以“取消选择”恢复新版设置。

### 保存项目和预设

- **项目**保存书稿列表、逐书数据、输出位置和当前工作状态，适合稍后继续处理。
- **预设**保存整批通用的转换参数，适合重复使用同一套排版和 MOBI 设置。

## 检查边界

- 结构验收默认关闭，需要排查成品问题时再开启，报告保存在输出目录下的 `EasyPub-验收报告` 文件夹。
- “结构通过”表示 EPUB/MOBI 内部目录、链接、图片、字体和关键 Kindle 字段符合当前检查规则，不等同于所有 Kindle 型号的真机确认。
- 固定版式、复杂 SVG、脚本交互 EPUB、TTC/CFF 字体、AZW3、单独 MOBI7/KF8 仍不在当前完整支持范围内。

## 从源码构建

需要 Windows x64 与 .NET SDK 10：

```powershell
dotnet build EasyPub.Modern.slnx -c Release
dotnet test EasyPub.Modern.slnx -c Release
```

项目结构：

- `src/EasyPub.Core`：TXT、EPUB、MOBI 与兼容处理核心。
- `src/EasyPub.Cli`：可脚本化的批量转换入口。
- `src/EasyPub.Desktop`：WPF 桌面应用。
- `tests`：核心转换、兼容性和真实 WPF 交互回归测试。

## 当前版本

当前唯一发布版本为 **v0.21.2**。后续 GitHub Release 只保留最新版，首页也只描述当前可下载版本。
