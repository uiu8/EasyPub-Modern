# EasyPub Modern：Kindle 格式预览集成研究

- 调研日期：2026-08-24
- 调研目标：判断能否在 EasyPub Modern 中加入类似 Vellum 的 Kindle 效果预览，并选择可靠、可维护、可合法发布的实现路线
- 资料原则：以 Amazon 当前产品文档、当前 Kindle Previewer 用户指南和开源项目原仓库为准；本机探测结果只作为 Windows 集成证据，不替代厂商承诺

## 一、结论先行

可以实现，而且第一版应采用 **“Kindle Previewer 官方预览 + EasyPub 内置快速预览”双层方案**：

1. EasyPub 继续保留现有整书预览，负责无等待地检查正文、目录、封面、插图和 CSS；界面明确标为“快速近似预览”。
2. 新增“在 Kindle Previewer 中预览”按钮，调用 Amazon 已公开的命令行接口，在独立 Kindle Previewer 窗口中打开当前书。
3. 对联合 MOBI 提供两个含义明确的入口：
   - **预览生成的 MOBI（老 Kindle 兼容）**：检查用户最终旁加载的实际成品；
   - **预览现代 Kindle 效果（同源 EPUB）**：把 KindleGen 之前的同源 EPUB 交给 Kindle Previewer，检查 Enhanced Typesetting 路线。
4. Kindle Previewer 不嵌入 EasyPub 窗口，也不随 EasyPub 的 GitHub 压缩包分发；用户从 Amazon 安装，EasyPub 只负责发现和调用。

这比自行制作一个“看起来像 Kindle”的皮肤可靠。Amazon 把 Kindle Previewer 定义为免费独立桌面应用，用于检查不同屏幕、方向、字体和字号下的 Kindle 呈现，并支持 Enhanced Typesetting。[Amazon Kindle Previewer 帮助](https://kdp.amazon.com/en_US/help/topic/G202131170)、[当前用户指南 v3.106.0](https://kindlepreviewer3.s3.amazonaws.com/UserGuide320_EN.pdf)

## 二、当前 Kindle Previewer 的可用能力

### 2.1 当前版本与系统要求

Amazon 当前英文用户指南标注 **Kindle Previewer 3.106.0，2026-07-09**，并说明这一指南发布于 3.106.0。指南和 KDP 帮助页列出的 Windows 最低要求是 Windows 8.1；Amazon 产品下载页可能给出更高的当前下载要求，因此 EasyPub 不应自行承诺兼容比 Amazon 当前安装器更旧的系统。[当前用户指南，首页和“System Requirements”](https://kindlepreviewer3.s3.amazonaws.com/UserGuide320_EN.pdf)、[KDP 帮助](https://kdp.amazon.com/en_US/help/topic/G202131170)

指南给出的 Windows 默认安装目录是：

```text
C:\Users\[user]\AppData\Local\Amazon\Kindle Previewer 3\
```

来源：[当前用户指南，“Installing on Microsoft Windows Systems”](https://kindlepreviewer3.s3.amazonaws.com/UserGuide320_EN.pdf)

### 2.2 输入格式

当前 v3.106.0 指南列出以下输入：

- KPF；
- MOBI、AZW、AZW3、AZW8、PRC；
- EPUB；
- HTM、HTML、XHTML、OPF；
- DOC、DOCX（指南注明仅限英文内容）。

因此，EasyPub 生成的 EPUB 和联合 MOBI 都可以交给当前 Kindle Previewer。需要注意，KDP 网页的简表只列出 EPUB、HTML/XHTML/OPF、KPF、DOC/DOCX，和当前 PDF 指南的完整列表不一致；实现应使用公开 CLI 做能力检查并允许用户更换路径，不把某个格式列表永久写死。[当前用户指南，“Supported Import File Formats”](https://kindlepreviewer3.s3.amazonaws.com/UserGuide320_EN.pdf)、[KDP Kindle Previewer 帮助](https://kdp.amazon.com/en_US/help/topic/G202131170)

Amazon 同时明确说明：MOBI 已不再是可重排内容的推荐发布格式，现代发布应使用 EPUB、DOCX 或 KPF；旁加载也不能准确反映 Enhanced Typesetting。因此“能打开 MOBI”和“代表当前 KDP 最终排版”是两件不同的事。[Amazon：Paths to Getting Your Content on Kindle](https://kdp.amazon.com/en_US/help/topic/G79CTKR8BX79E96L)

### 2.3 公开命令行接口

当前指南已经公开稳定的命令行用法：

```text
kindlepreviewer <input> <-command(s)> [-option(s)]
```

与 EasyPub 直接相关的命令为：

| 命令 | 官方含义 | EasyPub 用途 |
|---|---|---|
| `-showpreview` | 先转换输入，再在 Kindle Previewer 图形界面打开 | 一键官方预览 |
| `-convert` | 生成 KPF；不支持 Enhanced Typesetting 时生成 MOBI | 可选的成品验证/缓存，不代替 EasyPub 自身输出 |
| `-qualitychecks` | 检查外链、内链（目录除外）和脚注；目前是 Beta | 可选质量报告 |
| `-log` | 只生成验证日志，不生成 KPF/MOBI | 批量验收 |
| `-output <目录>` | 指定输出和日志目录 | 写入 EasyPub 独立预览缓存 |
| `-locale zh` | 使用中文界面/日志 | 中文用户默认值 |

官方给出的单书示例包括：

```text
kindlepreviewer inputfile.epub -showpreview
kindlepreviewer inputfile.epub -convert -qualitychecks
```

来源：[当前用户指南，第 3 章“Using Kindle Previewer from a Command-Line Interface”](https://kindlepreviewer3.s3.amazonaws.com/UserGuide320_EN.pdf)

这意味着 EasyPub 不需要使用窗口模拟点击，也不需要依赖未公开参数。但是官方只公开了“启动独立 GUI”和转换/日志命令，没有公开以下能力：

- 把 Kindle 渲染窗口嵌入第三方 WPF 窗口；
- 获取逐页位图或渲染 DOM；
- 通过 IPC 定位到某一章、某一页；
- 无界面控制设备型号、字号并返回截图。

所以第一版不应使用 `SetParent`、窗口句柄劫持、UI Automation 或解析 Kindle Previewer 私有缓存来伪造 Vellum 式内嵌界面。这些做法升级即可能失效，也没有官方接口保证。

## 三、本机 Windows 集成证据

2026-08-24 在当前开发机只读核查到：

- Kindle Previewer 位于官方默认目录：
  `C:\Users\13168\AppData\Local\Amazon\Kindle Previewer 3\Kindle Previewer 3.exe`；
- `where kindlepreviewer` 返回：
  `C:\Users\13168\AppData\Roaming\Amazon\kindlepreviewer.bat`；
- 该批处理文件把参数原样转交给 `Kindle Previewer 3.exe`；
- 当前用户注册表为 `.epub`、`.kpf` 和 `.mobi` 注册的“Open with Kindle Previewer 3”命令均是：
  `Kindle Previewer 3.exe "%1"`。

这证明当前安装器提供了位置参数和公开命令别名，但产品实现仍要兼容自定义安装目录、PATH 尚未刷新、注册表被用户修改等情况。

## 四、授权与发布边界

Amazon 把 Kindle Previewer称为免费工具，但安装流程要求用户审阅并接受 Amazon 软件最终用户许可协议。该许可授予的是用户下载、安装和使用 Amazon 软件的权利，并不是允许 EasyPub 把 Amazon 二进制再打包到自己的 GitHub Release 中。[Amazon Kindle Previewer 使用条款](https://www.amazon.com/b?node=23500831011)、[当前用户指南，安装步骤](https://kindlepreviewer3.s3.amazonaws.com/UserGuide320_EN.pdf)

产品应执行以下边界：

- **不打包** `Kindle Previewer 3.exe`、安装器或其 `lib` 目录；
- **不复制或调用** Kindle Previewer 安装目录里的私有内部转换器；
- 检测不到时提供 Amazon 官方下载入口，让用户自行安装并接受条款；
- EasyPub 只调用官方公开的 `kindlepreviewer ... -showpreview/-log/-qualitychecks`；
- 软件界面写“使用 Kindle Previewer 打开”，不要暗示 Kindle Previewer 是 EasyPub 自带组件或 Amazon 为 EasyPub 背书。

## 五、推荐架构

### 5.1 三层预览，不混淆含义

| 层级 | 入口名称 | 输入 | 能证明什么 | 不能证明什么 |
|---|---|---|---|---|
| A | 快速预览 | EasyPub 临时 EPUB/解包 HTML | 当前 CSS、图片、目录、正文大致正确 | Kindle 字体与 Enhanced Typesetting |
| B | Kindle 官方预览 | 同源 EPUB 或最终 MOBI | Kindle Previewer 对该输入的设备模式显示和转换警告 | 某一台真机固件百分之百一致 |
| C | Kindle 真机确认 | 最终 MOBI/EPUB 经用户实际投送 | 目标设备上的真实结果 | 其他 Kindle/应用的全部结果 |

界面不要把 A 层叫“Kindle 预览”，也不要把成功启动 B 层自动记为“已通过”。用户应能手动标记“已检查封面/目录/章节跳转/插图/字体/横竖屏”。

### 5.2 按钮与模式

主界面“整书预览”旁增加一个分裂按钮：

```text
[ Kindle 预览 ▾ ]
  ├─ 预览生成的 MOBI（老 Kindle 兼容）
  ├─ 预览现代 Kindle 效果（同源 EPUB）
  ├─ 仅运行 Kindle 质量检查
  └─ Kindle Previewer 设置…
```

规则：

- 尚未转换时，“预览现代 Kindle 效果”调用现有 `BookPreviewService` 所用的同一 EPUB 生成管线；
- 已有 MOBI 时，“预览生成的 MOBI”直接使用该输出，避免预览另一本临时书；
- TXT → MOBI 的现代预览使用 KindleGen 之前的同源 EPUB；
- EPUB → MOBI 的“保留原版式”模式使用原 EPUB；“EasyPub 兼容重排”模式使用重排后的临时 EPUB；
- 多选书时不同时打开多个 GUI；改为“批量 Kindle 检查”，调用目录输入配合 `-log` 或 `-convert`，然后显示汇总报告。

### 5.3 组件边界

建议增加以下小而明确的服务，不把外部程序逻辑塞进 `MainWindow`：

```text
KindlePreviewerDiscovery
  - 查找公开命令别名、官方默认目录、文件关联和用户指定路径

KindlePreviewArtifactService
  - 选择现有成品或生成同源 EPUB
  - 为外部预览建立稳定缓存

KindlePreviewerLauncher
  - 构造参数并启动 -showpreview/-qualitychecks/-log
  - 返回“已启动/未安装/输入不支持/启动失败”

KindleValidationReportReader
  - 只读取官方 -output 目录中的公开日志
  - 把 error/warning 映射为逐书状态
```

发现顺序建议：

1. 用户在“Kindle Previewer 设置”中明确选择的有效路径；
2. `where kindlepreviewer` 返回的公开命令别名；
3. `%LOCALAPPDATA%\Amazon\Kindle Previewer 3\Kindle Previewer 3.exe`；
4. 当前用户 `.epub`/`.mobi` 的“Open with Kindle Previewer 3”注册命令；
5. 未发现则展示“安装 Kindle Previewer”和“重新检测”。

不要把固定用户名、版本号目录或 Kindle Previewer 内部 `lib` 路径写进默认配置。用户路径属于本机应用设置，不应写入可分享的 `.easypubproj`，以免项目文件在另一台电脑上携带可执行路径。

### 5.4 外部预览缓存

现有 `BookPreviewService` 会创建临时 EPUB 并在预览包释放时清理。外部 Kindle Previewer 可能在 EasyPub 窗口关闭后仍继续读取文件，因此不能在启动进程后立即删除同一个临时目录。

建议写入：

```text
%LOCALAPPDATA%\EasyPubModern\PreviewCache\<书籍指纹>\
  source.epub
  kindle-output\
  manifest.json
```

- 指纹至少包含输入文件时间/大小、章节树、封面、插图、CSS、字体和转换选项；
- 同一指纹复用，设置变化后生成新指纹；
- 启动时清理超过 7 天且未使用的缓存，保留最近若干本；
- 外部进程启动失败时保留日志供诊断；
- 不修改用户原 TXT、EPUB 或正式输出 MOBI。

### 5.5 安全启动

- 使用参数列表 API 传递输入路径、`-showpreview`、`-output`、`-locale zh`，不要拼接一整条命令字符串；
- 只接受本机设置中发现/选择的可执行文件，不从 `.easypubproj` 自动运行任意路径；
- 对含空格、中文、`&`、括号的文件名做回归测试；
- 调用 `.bat` 时使用受控的 `cmd.exe /d /c` 参数；若已找到实际 `Kindle Previewer 3.exe`，优先直接调用；
- 质量检查需要捕获完成状态时使用 `-output` 并读取生成日志，不通过窗口标题猜测结果。

## 六、替代方案评估

### 6.1 calibre `ebook-viewer`

calibre 官方文档公开 `ebook-viewer [options] file`，并支持新窗口、置前、按目录项定位等参数；官方仓库说明它能查看主流电子书格式。它适合作为 MOBI/AZW3 的通用查看器降级，但使用的是 calibre 渲染，不是 Kindle Enhanced Typesetting。[ebook-viewer CLI](https://manual.calibre-ebook.com/generated/en/ebook-viewer.html)、[calibre 官方仓库](https://github.com/kovidgoyal/calibre)

calibre 为 GPL-3.0。第一版建议只调用用户自行安装的 calibre，不把庞大的 calibre 运行时并入 EasyPub 发布包。

### 6.2 Thorium Reader / Readium

Thorium Reader 基于 Readium Desktop，支持 Windows、中文界面和 EPUB，并公开 `thorium [path]` 导入并阅读 EPUB；仓库为 BSD-3-Clause。它适合现代 EPUB 的独立预览，不支持 MOBI/KF8，也不模拟 Kindle 排版。[Thorium Reader 官方仓库与 CLI](https://github.com/edrlab/thorium-reader)

Readium Web 提供 BSD-3-Clause 的 EPUB Web Reader 构建基础，可在长期方案中配合 WebView2 做更现代的内置预览；但它仍是 EPUB 阅读系统，不是 Kindle 引擎，而且引入 TypeScript/Go 工具链的成本高于直接升级现有 WPF 预览。[Readium Web 官方仓库](https://github.com/readium/web)

### 6.3 SumatraPDF

SumatraPDF 官方文档明确支持未加密 EPUB，以及 `.mobi`、未加密 `.azw/.azw3/.prc`，并公开 `SumatraPDF [arguments] [filepath]`。它启动很快，可作为轻量的直接 MOBI 查看器，但同样不提供 Kindle 设备、字体和 Enhanced Typesetting 仿真。[支持格式](https://github.com/sumatrapdfreader/sumatrapdf/blob/master/docs/md/Supported-document-formats.md)、[命令行参数](https://github.com/sumatrapdfreader/sumatrapdf/blob/master/docs/md/Command-line-arguments.md)

其许可包含 (A)GPL 约束；若采用，建议只检测和调用用户自行安装的程序，不随 EasyPub 分发。

### 6.4 epub.js

epub.js 是在浏览器中渲染 EPUB 的 BSD 许可库，支持分页/连续流、目录和归档 EPUB。它适合做更接近 Vellum 布局的内嵌双栏预览，但维护活跃度、WebView 安全边界和对 EPUB 脚本内容的处理需要单独评估。官方项目也提醒：即使使用 iframe 沙箱，启用脚本内容仍有安全风险。[epub.js 官方仓库](https://github.com/futurepress/epub.js)

### 6.5 Kindle Create

Kindle Create 内置平板、手机和 E-reader 预览，但它围绕自己的 KCB/KPF 编辑项目工作，并非公开的通用 EPUB/MOBI 启动器。它不适合作为 EasyPub 的外部预览依赖。[Kindle Create 预览与发布](https://kdp.amazon.com/en_US/help/topic/GRVZMSZ2THRTR5V9)

## 七、为什么不只用别的阅读器

| 目标 | 最合适工具 |
|---|---|
| 编辑时立即发现 CSS/目录/图片问题 | EasyPub 内置预览 |
| 检查当前 Kindle 转换和 Enhanced Typesetting | Kindle Previewer |
| 快速直接打开 MOBI/AZW3 | Kindle Previewer；失败时 calibre 或 SumatraPDF |
| 检查 EPUB 标准合法性 | EPUBCheck，不能用“能打开”代替 |
| 证明目标 Kindle 真机可用 | 真机测试 |

其他阅读器可以补充，但不能“更好地替代” Kindle Previewer 的官方 Kindle 近似。更好的产品体验来自把各工具的证明范围说清楚，而不是用一个渲染器冒充全部标准和设备。

## 八、分阶段实施建议

### 第一阶段：官方一键预览

- 自动发现 Kindle Previewer；
- 选中一本书后调用 `-showpreview -locale zh`；
- 同时支持最终 MOBI和同源 EPUB；
- 检测不到时提供官方安装入口和手工选择；
- 不打包 Amazon 文件；
- 设置页显示“已检测/未安装/路径无效”。

### 第二阶段：质量日志闭环

- 支持 `-qualitychecks` 与 `-log`；
- 读取 `-output` 下的逐书日志和汇总日志；
- 在转换结果中显示“Kindle：通过/警告/失败/未检查”；
- 明确 Beta 检查目前只覆盖外链、内链（不含目录）和脚注。

### 第三阶段：Vellum 式内置体验

- 将现有 IE `WebBrowser` 升级为 WebView2；
- 左侧目录、中间正文、右侧设备框/字号/主题切换；
- 预览内容变化后增量刷新；
- 始终显示“EasyPub 快速近似预览”，保留官方 Kindle Previewer 按钮。

## 九、验收标准

使用至少一份含中文、封面、三级目录、插图、自定义 CSS、字体和特殊路径字符的样书验证：

1. TXT 尚未正式转换时，能生成同源 EPUB并一键打开官方预览；
2. 正式生成 MOBI 后，预览按钮打开的是这一份成品，而不是旧文件或另一份临时文件；
3. 目录跳转、封面、章节标题、插图、边距、字号变化、横竖屏逐项可检查；
4. 找不到 Kindle Previewer 时不崩溃，提示安装/选择/使用快速预览；
5. 路径含中文、空格、`&`、括号时仍能启动；
6. 预览缓存不会在 Kindle Previewer 尚在读取时被删除，也不会无限增长；
7. 批量检查不会为每本书弹一个窗口，而是生成可读汇总；
8. EasyPub 发布 ZIP 中不包含任何 Kindle Previewer 文件；
9. 现有原版 EasyPub EPUB/MOBI 黄金样本保持不变；
10. 真机结论仍由用户实际导入 Kindle 后确认，不把 Kindle Previewer 成功等同于真机通过。

## 十、最终建议

近期直接实施 **Kindle Previewer 3.106.0 公开 CLI 联动**，而不是先引入新的内嵌阅读引擎。具体产品形态是：

> **编辑时用 EasyPub 快速预览；生成后用 Kindle Previewer 官方预览；必要时用 calibre/SumatraPDF 做直接 MOBI 降级查看；最终以目标 Kindle 真机确认。**

这条路线能最快获得真正有价值的 Kindle 设备/方向/字体检查，同时不污染原版兼容输出，也不把 Amazon 私有组件带进 EasyPub 发布包。
