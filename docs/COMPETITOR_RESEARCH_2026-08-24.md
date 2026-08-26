# EasyPub Modern 竞品研究与产品建议

- 调研日期：2026-08-24
- 调研对象：桌面/在线电子书制作与转换软件、开源转换管线、Kindle 官方工具，以及中文/日文网文生态中的相邻产品
- 资料原则：仅采用产品官方网站、官方帮助、官方发布日志、GitHub 原仓库和原仓库 Releases；不采用测评站、聚合下载站或二手教程作为事实依据
- 范围声明：公开产品和 GitHub 仓库数量巨大，本报告是对仍可核查、具有代表性或与 EasyPub Modern 路线直接相关的产品进行系统抽样，不声称在数学意义上穷尽“市面上/GitHub 所有产品”

## 一、结论先行

EasyPub Modern 不应该变成缩小版 calibre，也不适合转型为 Scrivener、Atticus 那样的写作平台。它最有机会形成的清晰定位是：

> **Windows 优先、完全本地、面向中文长篇与网文的批量电子书编译器，同时保留原版 EasyPub 兼容输出，并提供现代 EPUB/Kindle 质量门禁。**

当前版本已经具备批量 TXT → EPUB/MOBI、逐书元数据、封面、插图、CSS、章节编辑、三级目录、预览、预设、项目保存与异常恢复。竞品调研后，真正值得补齐的短板集中在四处：

1. **缺少正式标准校验闭环**：现有“转换前检查”能够发现输入问题，但还不是 W3C EPUBCheck、DAISY Ace 和 Kindle Previewer 意义上的成品验收。
2. **章节编辑仍应升级为结构编辑器**：Kindle Create、Scrivener、Jutoh 都把章节视为可拖放、可拆分、可合并、有语义类型的树，而不是一组正则命中结果。
3. **需要把“原版兼容”和“现代发布”明确分成两条输出轨道**：传统联合 MOBI 对老 Kindle 和既有用户仍有价值，但 Amazon 当前出版工作流已优先接受 EPUB/KPF；两种目标不应混为一个模式。
4. **中文/日文排版能力仍有明显增量空间**：直排、右翻、简繁转换、硬换行修复、网站杂质清理、标点规范、注音/旁注、纵中横等，都是西方通用工具覆盖较弱而东亚工具证明有需求的能力。

建议首先完成 P0：成品一键校验、可拖放章节树、兼容/现代双输出模式、Kindle Previewer 一键联动。之后再做输入适配和中文清理流水线。插件、AI、印刷 PDF 等应后置。

## 二、EasyPub Modern 当前基线

依据本仓库 [README](../README.md)，v0.15.0 已经具备：

- 批量 TXT → EPUB、TXT → 联合 MOBI，并可并发执行；
- UTF-8/GBK 自动或指定编码、自定义章节正则、三级目录正则；
- 主列表全选、收藏目录、递归导入、输出冲突检查、失败重试与历史记录；
- 每本书独立封面、正文插图、WebP/PNG 解码后转高质量 JPEG；
- 逐书标题、作者和扩展出版信息，文件夹来源到元数据的自动映射；
- 四边页边距、字体子集嵌入、定制 CSS；
- 章节检查/编辑、标题格式规范化、真实 EPUB 整书预览；
- 命名预设、`.easypubproj` 项目、自动恢复快照；
- 原版 `config.xml` 兼容和默认 EPUB 黄金样本逐字节回归；
- KindleGen 2.9 联合 MOBI 兼容路径与 Kindle 真机可读性修复。

因此，下面的建议不会把“已有功能换个名字”当成新建议。

## 三、市场分层

| 产品类型 | 代表产品 | 用户真正购买/使用的价值 | 与 EasyPub Modern 的关系 |
|---|---|---|---|
| 全能书库与转换平台 | calibre | 格式覆盖、批量元数据、插件、设备和命令行生态 | 不应正面复制；应借鉴转换诊断、插件边界和成品编辑 |
| 专业 EPUB 编辑器 | Sigil、PageEdit、Jutoh | 对 XHTML/CSS/目录/资源做最后一公里修订和校验 | EasyPub 应补结构编辑与校验，但保持低学习成本 |
| 写作到出版一体化 | Scrivener、Atticus、Reedsy Studio | 写作、章节组织、主题模板、印刷与电子版多输出 | 借鉴章节树、前后置页、主题体验；不要变成写作软件 |
| 高品质排版工具 | Vellum | 模板质量、实时多设备预览、平台优化、可访问性 | 借鉴“少选项也能稳定漂亮”和质量报告 |
| Kindle 官方链路 | Kindle Create、Kindle Previewer | KDP/KPF 与 Kindle 最终显示的权威近似 | 必须作为现代 Kindle 验收链路，而非自行模拟全部设备 |
| 文档转换引擎 | Pandoc、Asciidoctor EPUB3、LibreOffice | 多输入格式、可脚本化、结构化源文件 | 可作为输入适配思路，不应让普通用户安装复杂依赖 |
| 互动/教育电子书 | Kotobee Author | 视频、测验、SCORM、应用与云分发 | 与中文小说主线距离较远，不宜近期追随 |
| 东亚文本/网文工具 | AozoraEpub3、Narou.rb、cn-epub-maker、OOMOL TXT Converter | 纵排、右翻、文本清理、网文章节、更新与批处理 | 是 EasyPub 最值得吸收的差异化能力来源 |
| 阅读器/效果反馈 | Koodo Reader | 多格式渲染、纵排、逐书样式、目录搜索 | 不是直接竞品，但可作为中文跨平台预览补充 |

## 四、代表性产品逐项核查

### 4.1 calibre / ebook-convert / Edit book

**活跃度**：高度活跃；官方 Windows 下载页在调研日显示 **9.13.0**，GitHub Release 标记发布日期为 **2026-08-07**。来源：[官方下载](https://www.calibre-ebook.com/download_windows64)、[GitHub Releases](https://github.com/kovidgoyal/calibre/releases)、[官方仓库](https://github.com/kovidgoyal/calibre)。

**能力**：

- 支持主流电子书格式的查看、转换、编辑、目录化、设备交互和在线元数据获取；
- `ebook-convert` 提供完整 CLI，按输入/输出类型开放大量参数，天然适合脚本批处理；
- 转换管线包含字体缩放、CSS/HTML 变换、启发式断行修复、章节 XPath、三级 TOC、元数据、封面和调试中间产物；
- Edit book 可编辑 EPUB、KEPUB、AZW3 内部 HTML/CSS，带实时预览、目录编辑、检查、链接检查、字体嵌入/子集、未用 CSS 清理、报告和跨文件 checkpoint；
- 插件 API 覆盖文件类型、元数据、转换、设备驱动和界面动作。

来源：[转换说明](https://manual.calibre-ebook.com/conversion.html)、[`ebook-convert` CLI](https://manual.calibre-ebook.com/en/generated/en/ebook-convert.html)、[Edit book](https://manual.calibre-ebook.com/en/edit.html)、[插件 API](https://manual.calibre-ebook.com/plugins.html)、[元数据编辑](https://manual.calibre-ebook.com/metadata.html)。

**差异化与局限**：它是生态平台，不是专门为中文 TXT 小说设计的顺手批量编译器；功能密度和参数复杂度很高。EasyPub 不可能也没有必要复制其书库、设备和全格式矩阵。最值得借鉴的是“保留调试中间产物”“成品检查定位到文件/行”“转换前后对比”和可插拔输入/后处理边界。

### 4.2 Sigil + PageEdit

**活跃度**：活跃；Sigil 与 PageEdit 最新公开版均为 **2.7.6（2026-03-19）**，Sigil 仓库在 2026-08 仍有提交。来源：[Sigil 官方仓库](https://github.com/Sigil-Ebook/Sigil)、[Sigil Releases](https://github.com/Sigil-Ebook/Sigil/releases)、[PageEdit Releases](https://github.com/Sigil-Ebook/PageEdit/releases)。

**能力**：

- Sigil 专注 EPUB 2/3，提供源码编辑、预览 Inspector、TOC 生成与手工调整、元数据、报告、拼写检查、XHTML/CSS 校验和 Python 插件；
- PageEdit 是独立或由 Sigil 调用的可视化 XHTML 编辑器，可插图、链接、特殊字符，并能按 OPF spine 顺序编辑多个文档；
- 不以海量格式批量转换见长，而以成品精修见长。

来源：[Sigil README](https://github.com/Sigil-Ebook/Sigil#readme)、[PageEdit README](https://github.com/Sigil-Ebook/PageEdit#readme)、[Sigil 调用 PageEdit](https://sigil-ebook.com/pageedit/running-pageedit/)。

**差异化与局限**：需要用户理解 EPUB、HTML/CSS 或至少理解资源树；不适合把大量 TXT 快速按统一规则出书。EasyPub 应吸收“目录树+资源定位+校验错误跳转”，但不应直接暴露一个像 IDE 的文件树给普通批量用户。

### 4.3 Pandoc

**活跃度**：高度活跃；最新发布记录为 **3.10.1（2026-07-21）**。来源：[官方 Releases 日志](https://pandoc.org/releases.html)、[官方仓库](https://github.com/jgm/pandoc)。

**能力**：

- 在 Markdown、HTML、DOCX、ODT、LaTeX、AsciiDoc、EPUB 2/3、FB2 等大量格式之间转换；
- EPUB 输出支持 CSS、封面、目录深度、元数据 XML/YAML、媒体收集、字体、EPUB 版本；
- CLI、defaults 文件、模板、Lua filters 和 JSON AST 很适合可复现自动化；
- 没有面向小说批量管理的原生桌面 GUI，也不是成品 EPUB 精修器。

来源：[支持格式](https://www.pandoc.org/)、[制作 EPUB](https://pandoc.org/epub.html)、[用户手册](https://pandoc.org/MANUAL.html)。

**启示**：EasyPub 的未来输入层应使用统一中间文档模型，让 TXT、Markdown、HTML、DOCX 只是不同 Reader；但不宜直接把 Pandoc 的复杂参数原样塞进 UI。

### 4.4 Asciidoctor EPUB3

**活跃度**：仍维护；最新 Release 为 **2.3.0（2025-08-11）**。来源：[官方仓库](https://github.com/asciidoctor/asciidoctor-epub3)、[Releases](https://github.com/asciidoctor/asciidoctor-epub3/releases)。

**能力**：将结构化 AsciiDoc 直接生成 EPUB 3，支持多部书、目录层级、代码高亮、内嵌字体、样式和 CLI；更适合技术书与结构化出版。

**差异化与局限**：源格式强、自动化好，但普通 TXT 小说用户需要学习 AsciiDoc/Ruby 工具链。启示是将“章节语义”和“外观主题”分离，而不是把全部格式藏在正则和 CSS 里。

### 4.5 Vellum

**活跃度**：商业产品持续更新；官方博客显示 **4.1.4（2026-07-15）**。来源：[更新博客](https://blog.vellum.pub/)、[技术规格](https://vellum.pub/specs/)。

**能力**：

- DOCX 导入；输出 EPUB 2/3、可选 MOBI、印刷 PDF；
- 实时多设备预览、专业主题、章节标题和各类文本组件；
- 自动按销售平台优化图片和输出；
- 官方规格明确声明输出经 EPUBCheck 验证，并用 DAISY Ace 评估可访问性；
- 可添加结构导航、语义角色、高对比文字和图片替代文本。

来源：[预览](https://help.vellum.pub/preview/)、[生成设置](https://help.vellum.pub/generating/settings/)、[文本组件](https://help.vellum.pub/text-features/)、[技术规格](https://vellum.pub/specs/)。

**差异化与局限**：强项是“默认即专业”和设备预览；仅支持 macOS，偏单本书精排，不是批量 TXT 工具。EasyPub 应学习其质量门禁和主题体验，而非追求同等印刷排版。

### 4.6 Atticus

**活跃度**：持续运营的在线/可安装 Web 应用；官方未提供可核验的公开版本号时间线，故不虚构“最新版本”。来源：[官网](https://www.atticus.io/)、[Quick Start](https://www.atticus.io/quick-start-guide/)。

**能力**：写作与排版一体，DOCX 导入，章节拖放、主题构建、前后置页、卷/合集、多设备预览，输出 EPUB 与印刷 PDF，并可导出 DOCX/JSON 快照。

来源：[格式化工作流](https://www.atticus.io/how-to-format-your-book-with-atticus/)、[预览与输出](https://www.atticus.io/preview-your-formatted-book/)、[格式基础](https://www.atticus.io/book-formatting-basics/)。

**差异化与局限**：体验友好、模板化强，但依赖账号/在线服务，底层 CSS 和批量自动化不是核心。其最值得借鉴的是前置页/正文/后置页的结构模型、卷级组织和实时预览。

### 4.7 Scrivener

**活跃度**：持续维护；官方 Release Notes 当前可核查到 **3.5.2（2025-12-18，平台版本可能不同）**。来源：[产品页](https://www.literatureandlatte.com/scrivener/overview)、[Release Notes](https://www.literatureandlatte.com/scrivener/release-notes)。

**能力**：长篇写作、资料库、Binder/Outliner/Corkboard、场景和章节拖放、快照与比较、自动备份；Compile 可从同一项目输出 DOCX、ODT、PDF、HTML、TXT、EPUB 3 和 Kindle 版本，并可定制 CSS、封面、前置页和元数据。

来源：[官方概览](https://www.literatureandlatte.com/scrivener/overview)、[Windows 手册](https://www.literatureandlatte.com/docs/Scrivener_Manual-Win.pdf)、[EPUB/Kindle 编译说明](https://www.literatureandlatte.com/blog/epub-kindle-and-multimarkdown-export-in-scrivener-3)。

**差异化与局限**：核心是写作项目而非批量转换；Compile 学习成本较高。EasyPub 可借鉴“内容树→输出配置”的模型和跨章节 checkpoint，但不应加入剧情资料库、卡片墙等写作功能。

### 4.8 Jutoh

**活跃度**：活跃；官方日志显示 **3.32（2026-07-27）**。来源：[更新日志](https://www.jutoh.com/whatsnew.html)、[下载页](https://www.jutoh.com/download)。

**能力**：

- 导入 DOCX、ODT、HTML、TXT、Markdown、CBZ、EPUB；
- 输出 EPUB 2/3、Kindle、ODT、HTML、TXT、Markdown、CBZ、MP3；
- 章节拆分、目录、元数据、图片、样式、自定义 CSS、脚注/尾注、索引、交叉引用；
- 编译分析、EPUBCheck 集成、源代码定位、多阅读器启动；
- 配置可从同一项目生成不同封面、ISBN、链接和格式版本；Jutoh Plus 还提供脚本和批量个性化编译。

来源：[功能](https://www.jutoh.com/features.html)、[规格](https://www.jutoh.com/specifications.html)、[Jutoh Plus 批量与脚本](https://www.jutoh.com/jutohplus.html)。

**差异化与局限**：这是和 EasyPub“项目→多版本编译”最接近的成熟商业参照，但 UI 和概念较多，中文网文清理不是重点。其“编译消息+上下文修复链接”和“一个项目多发布配置”尤其值得借鉴。

### 4.9 Kindle Create + Kindle Previewer

**活跃度**：Amazon 持续维护，官方帮助与下载页在调研日有效；Amazon 没有公开稳定的语义版本/Release 时间线，因此不臆测版本号。来源：[Kindle Create 入门](https://kdp.amazon.com/en_US/help/topic/GUGQ4WDZ92F733GC)、[Kindle Previewer](https://kdp.amazon.com/en_US/help/topic/G202131170)。

**能力**：

- Kindle Create 从 DOC/DOCX 制作可重排书，也支持 PDF/图像类书；可检测候选章节、接受/排除目录项、拖动顺序、拆分/合并章节、主题、图片、链接、前后置页；
- 输出 KPF，且可为可重排书输出 EPUB；Amazon 明确把 KPF 作为 KDP 首选；
- 内置预览可切换平板、手机和 E-reader；Kindle Previewer 是独立免费工具，可显示 Enhanced Typesetting，并按设备、方向、字号检查；
- 官方帮助列出的可重排书语言不含中文，且另页明确指出不支持日文可重排书，这是 EasyPub 的本地化机会。

来源：[可重排书与目录编辑](https://kdp.amazon.com/en_US/help/topic/G7R2L7V5X6SJH948)、[预览与发布](https://kdp.amazon.com/en_US/help/topic/GRVZMSZ2THRTR5V9)、[支持的稿件格式](https://kdp.amazon.com/en_US/help/topic/G200634390)。

**差异化与局限**：这是 Kindle 显示验收的最重要官方参照，但不是中文 TXT 批量器，也不提供开放自动化接口文档。EasyPub 应提供“生成后用 Kindle Previewer 打开”而不是假装自己的 WPF 预览等于 Kindle 真机。

### 4.10 Reedsy Studio

**活跃度**：持续运营的在线服务；无公开版本时间线。来源：[官方格式化页](https://reedsy.com/studio/format-a-book/)、[服务条款列出的当前能力](https://reedsy.com/studio/terms/)。

**能力**：在线写作、结构组织、协作、版本控制、主题、自动版权页/目录，输出 EPUB 3 与符合 POD 的 PDF/X。

**差异化与局限**：降低了排版门槛，但批量、本地隐私、精细 CSS 和老 Kindle MOBI 不是目标。EasyPub 可借鉴“导出向导只呈现决定结果的少数选项”。

### 4.11 Kotobee Author

**活跃度**：活跃；官方版本页显示 **1.9.8（2026-07-08）**。来源：[版本页](https://www.kotobee.com/en/products/author/versions)、[Release Notes](https://support.kotobee.com/en/support/solutions/folders/8000087304)。

**能力**：从 Word、PDF、HTML、EPUB 导入或从头制作；支持可重排/固定版式、音视频、测验、组件、AI 小工具；输出 EPUB/MOBI/PDF/Word、Web/桌面/移动应用、SCORM/LTI/Tin Can，并可云端分发。EPUB 导出具有 EPUBCheck、自动修复、Kindle 优化、字体和脚本选项。

来源：[产品功能](https://www.kotobee.com/features/products/author)、[EPUB 导出与校验](https://support.kotobee.com/en/support/solutions/articles/8000071383-export-an-epub-or-encrypted-epub-ebook)、[导出体系](https://kotobee.freshdesk.com/en/support/solutions/articles/8000016887-introduction-to-exporting)。

**差异化与局限**：教育互动、DRM、云库和应用包装很强，但与本地中文小说批量转换不是同一核心问题。近期不建议 EasyPub 追逐 SCORM、DRM 或 App 打包。

### 4.12 LibreOffice Writer

**活跃度**：高度活跃；调研日官方下载页列出 26.2 系列，官方博客确认 **26.2.4（2026-06-05）**。来源：[下载页](https://www.libreoffice.org/download/)、[26.2.4 公告](https://blog.documentfoundation.org/blog/2026/06/05/tdf-releases-libreoffice-26-2-4/)。

**能力**：完整字处理和样式系统，可直接导出 EPUB，选择 EPUB 版本、拆分方式、布局、封面、媒体目录和自定义 XMP 元数据；26.2 还加入 Markdown 支持并改进 EPUB 导出性能。

来源：[EPUB 导出帮助](https://help.libreoffice.org/latest/en-US/text/shared/01/ref_epub_export.html)、[26.2 发布说明](https://blog.documentfoundation.org/blog/2026/02/04/libreoffice-26-2-is-here/)。

**差异化与局限**：适合以 ODT 样式写作的人，但不是专业 EPUB 精修器、Kindle 验收器或 TXT 批量管线。它证明 DOCX/ODT/Markdown 输入值得支持。

### 4.13 SEED.html

**活跃度**：2025 年出现的早期项目；仓库尚无可信成熟 Release 轨迹，不能与 Sigil 的成熟度等量齐观。来源：[官方仓库](https://github.com/stewarthaines/seed-html)。

**能力**：浏览器内从纯文本制作可访问 EPUB 3，离线/PWA、跨设备预览、JavaScript 变换扩展、RTL 与多语言；还提出把编辑器作为非 manifest 资源随 EPUB 保存的实验方案。

**启示与局限**：其“可访问性优先”和可配置文本变换值得关注，但项目很新、用户规模和长期兼容性尚未证明。EasyPub 不应依赖它，但可借鉴可组合变换流水线。

### 4.14 AozoraEpub3-JDK21

**活跃度**：活跃的现代化分支；最新 **1.3.7-jdk21（2026-07-25）**。来源：[官方仓库与 Release Notes](https://github.com/AozoraEpub3-JDK21/AozoraEpub3-JDK21)。

**能力**：

- 青空文库注记 TXT/ZIP、图片归档、部分 Web 小说 URL → EPUB 3.3；
- 纵排/横排、右翻、外字/注记、平台模板、Kobo/Kindle/Reader 适配；
- GUI 与 CLI、批量拖放、预设、模板/CSS；
- 新版重视 EPUBCheck、非零失败退出码、破损输出删除、路径安全、可复现/字节一致回归；
- 明确记录 iOS Kindle 纵排标题页等设备差异。

**差异化与局限**：它是东亚排版最有价值的直接参照，但面向日文青空注记和 Java 生态。EasyPub 可借鉴其纵排/右翻、失败原子性、平台已知问题清单和模板体系。

### 4.15 Narou.rb

**活跃度**：成熟但当前活跃度相对谨慎判断；官方 Issues 在 2025 年仍有反馈，公开 wiki/ChangeLog 的大量核心资料较旧。来源：[官方仓库](https://github.com/whiteleaf7/narou)、[官方 Wiki](https://github.com/whiteleaf7/narou/wiki)、[Issues](https://github.com/whiteleaf7/narou/issues)。

**能力**：从日文网文站下载、增量更新、检测改稿 diff、清理整形，经 AozoraEpub3/KindleGen 输出 EPUB/MOBI，并可发送到设备；CLI 与 Web UI 并存。

**差异化与局限**：其核心价值是“来源更新→转换→发送”的自动流水线，而不是单次格式转换；但依赖站点规则和已停止官方分发的 KindleGen，维护风险高。EasyPub 不应内建抓取盗版内容，但可支持用户自有文件夹的增量监听/仅重转变更文件。

### 4.16 cn-epub-maker

**活跃度**：小型新项目，无可核验的稳定 Release 时间线。来源：[官方仓库](https://github.com/muyen/cn-epub-maker)。

**能力**：中文 TXT → 横排/直排 EPUB，自动识别 GBK/GB18030/UTF-8/Big5，OpenCC 简转繁，清除广告杂质，卷章识别和重编号，引号/数字规范，封面、自定义正则，并修补 RTL spine。

**差异化与局限**：范围窄、依赖 Pandoc、成熟度未证明，但准确展示了繁体/纵排中文用户的真实需求。EasyPub 可将这些能力做成可预览、可撤销的“文本清理方案”，不能未经确认就改写正文。

### 4.17 OOMOL TXT to EPUB Converter

**活跃度**：官方仓库 README/升级文档标记到 **0.2.0**，但没有核验到成熟的 GitHub Release 时间线。来源：[官方仓库](https://github.com/oomol-lab/txt-to-epub-converter)。

**能力**：中英文编码、卷/章/节多层检测、批量 Python 调用、断点恢复、内容字数校验、可选 OpenAI 兼容模型辅助结构分析、封面和水印。

**差异化与局限**：断点恢复和“输入输出字数守恒”很值得借鉴；AI 会带来费用、隐私和非确定性，不应成为 EasyPub 默认解析器。

### 4.18 Koodo Reader（相邻产品）

**活跃度**：高度活跃；最新公开 Release **2.4.0（2026-07-14）**。来源：[官方仓库](https://github.com/koodo-reader/koodo-reader)、[Releases](https://github.com/koodo-reader/koodo-reader/releases)。

**能力**：跨 Windows/macOS/Linux/移动/Web 的多格式阅读与书库；支持 TXT/EPUB 等、目录搜索、自定义 TXT 目录解析、竖排、逐书样式、字体、同步、备份、全文翻译和插件。

**差异化与局限**：它是阅读器，不是发行级 EPUB/MOBI 制作器；不能代替 EPUBCheck 或 Kindle Previewer。对 EasyPub 的价值是验证中文目录/纵排的跨平台可读性，并证明“每书规则覆盖全局规则”符合用户预期。

### 4.19 Quarto Books

**活跃度**：高度活跃；GitHub Releases 在调研日标记稳定版 **1.10.18（2026-07-24）**，同时已有 1.11 预发布版。来源：[官方仓库 Releases](https://github.com/quarto-dev/quarto-cli/releases)、[Books 官方文档](https://quarto.org/docs/books/)。

**能力**：把多个 Markdown/Notebook/AsciiDoc 章节组织为一本书，从同一源项目生成 HTML、PDF、Typst、Word、EPUB 或 AsciiDoc；支持跨章节引用、章节编号、书籍预览、配置文件和可复现构建。EPUB 格式可设置封面、目录、CSS、元数据、EPUB 子类型和 Apple Books 相关选项。

来源：[Books](https://quarto.org/docs/books/)、[EPUB 选项](https://quarto.org/docs/reference/formats/epub.html)。

**差异化与局限**：对技术书、研究文档和含代码内容极强，但依赖结构化源文件与命令行/编辑器生态，不处理中文网文脏 TXT。它进一步证明 EasyPub 应建立“项目配置 + 统一中间模型 + 多目标构建”，而非在每个输出器中重复解析正文。

### 4.20 ystyle/kaf-cli

**活跃度**：活跃；最新 Release **1.3.16（2026-05-29）**。来源：[官方仓库](https://github.com/ystyle/kaf-cli)、[Releases](https://github.com/ystyle/kaf-cli/releases)。

**能力**：面向中文 TXT 的跨平台 CLI/拖放工具，自动识别编码、书名/作者、章/卷，自定义正则、封面、字体、CSS、缩进/间距/行高；可生成 EPUB、AZW3、MOBI，并通过 KindleGen 生成 MOBI。它还提供 MCP/Agent Skill 入口，README 声称可达到数百章每秒的转换速度。

**差异化与局限**：它和 EasyPub 的输入场景最接近，优点是体积小、速度快、CLI 参数完整、AZW3 输出和拖到 EXE 即转换；缺少 EasyPub 已有的逐书可视化编辑、项目恢复、成品预览和复杂批量管理。EasyPub 应把“AZW3/现代 Kindle 输出是否值得支持”纳入技术验证，并借鉴其 CLI/UI 参数同源设计；不能只依据 README 性能声明得出自身更慢的结论，需用同一大书样本实测。

### 4.21 k4yt3x/txt2epub

**活跃度**：小型项目；仓库有 26 次提交、PyPI 分发，但没有公开 GitHub Release 时间线，故不虚构最新版本日期。来源：[官方仓库](https://github.com/k4yt3x/txt2epub)。

**能力**：Python 工具，既有 CLI 也有简单 GUI；输入单个 TXT，设置标题、作者、语言、封面后生成 EPUB。章节格式固定为“三个换行分隔章节，下一行首行为标题”。

**差异化与局限**：安装和模型简单，适合格式已经规整的文本；没有编码识别、复杂章节正则、批量、章节树、成品校验或 Kindle 输出。它说明一个重要 UX 原则：高级规则之外仍应保留“约定格式、零配置快速转换”，但 EasyPub 当前能力已经明显超过它。

### 4.22 bluicezhen/txt-to-epub

**活跃度**：小型前端项目；仓库有 9 次提交且未发布 GitHub Release，成熟度需谨慎评价。来源：[官方仓库](https://github.com/bluicezhen/txt-to-epub)。

**能力**：浏览器本地单页应用，文件不上传；用 `jschardet` 识别 UTF-8/GB18030/Big5，识别“第 x 章”，把章前内容作为简介，提供章节标题/字数/行数预览、改标题和与上一章合并，再生成带目录 EPUB。

**差异化与局限**：隐私说明直观、首次使用成本低，且把“章节预览→改名/合并→导出”做成短路径；但规则、元数据、资源、CSS、批量和验证都较基础。EasyPub 的章节树设计可借鉴其即时字数/行数反馈，同时继续保持桌面本地和更强的逐书能力。

## 五、能力矩阵

符号：●=原生强支持；◐=支持但不是核心/需外部工具；○=未见官方资料证明；—=不适用。

| 产品 | 批量/自动化 | 章节/目录编辑 | 元数据 | 封面/插图/CSS | 实时预览 | 标准/平台校验 | 插件/脚本 | 中文/日文专项 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| EasyPub Modern v0.15 | ● | ◐ | ● | ● | ● | ◐ | ◐（CLI，无插件 API） | ●（中文章节/编码） |
| calibre | ● | ● | ● | ● | ● | ● | ● | ◐ |
| Sigil + PageEdit | ◐ | ● | ● | ● | ● | ● | ● | ◐ |
| Pandoc | ● | ◐（源结构） | ● | ● | ○ | ◐（可外接） | ● | ◐ |
| Asciidoctor EPUB3 | ● | ◐（源结构） | ● | ● | ○ | ●（工具链） | ● | ◐ |
| Vellum | ○ | ● | ● | ● | ● | ● | ○ | ○ |
| Atticus | ○ | ● | ● | ● | ● | ◐ | ○ | ○ |
| Scrivener | ◐ | ● | ● | ● | ◐ | ◐ | ◐ | ○ |
| Jutoh | ●（Plus 更强） | ● | ● | ● | ● | ● | ● | ◐ |
| Kindle Create/Previewer | ○ | ● | ◐ | ● | ● | ●（Kindle） | ○ | ○（可重排中文不在官方列表） |
| Reedsy Studio | ○ | ● | ◐ | ●（主题化） | ● | ◐ | ○ | ○ |
| Kotobee Author | ◐ | ● | ● | ● | ● | ● | ◐ | ◐ |
| LibreOffice Writer | ◐（UNO/宏） | ◐ | ◐ | ◐ | ○ | ○ | ●（宏/扩展） | ◐ |
| AozoraEpub3-JDK21 | ● | ●（规则/注记） | ● | ● | ◐ | ● | ◐ | ● |
| Narou.rb | ● | ◐ | ◐ | ● | ◐（Web UI） | ◐ | ● | ● |
| cn-epub-maker | ●（CLI） | ◐ | ◐ | ● | ○ | ○ | ●（脚本） | ● |
| OOMOL TXT Converter | ● | ◐ | ◐ | ◐ | ○ | ◐（内容校验） | ●（Python） | ● |
| Koodo Reader | — | ◐（解析规则） | ●（书库） | ●（阅读样式） | ● | ◐（阅读效果） | ● | ● |
| Quarto Books | ● | ◐（源结构） | ● | ● | ●（书籍预览） | ◐（可外接） | ● | ◐ |
| ystyle/kaf-cli | ● | ◐（正则） | ◐ | ● | ○ | ○ | ●（CLI/MCP） | ● |
| k4yt3x/txt2epub | ○ | ◐（约定分隔） | ◐ | ◐ | ○ | ○ | ●（CLI） | ○ |
| bluicezhen/txt-to-epub | ○ | ◐（预览/改名/合并） | ○ | ○ | ◐ | ○ | ○ | ●（中文编码/章节） |

## 六、EasyPub Modern 的差异化判断

### 已经形成的优势

1. **真正的批量“逐书差异”**：单批次中每本书可有独立封面、插图和元数据，这比多数单书排版软件更适合整理大量小说。
2. **原版 EasyPub 兼容资产**：`config.xml`、黄金 EPUB、KindleGen 联合 MOBI 后处理与真机问题修复，构成其他项目没有的迁移价值。
3. **中文 TXT 优先**：GBK、中文章节、标题编号规范化、文件夹元数据映射，比西方工具更接近中文网文整理流程。
4. **完全本地和 Windows 原生体验**：相较 Atticus/Reedsy 的账号/云依赖，以及 Vellum 的 macOS 限制，有明确用户价值。
5. **项目可恢复且不修改原 TXT**：插图位置等编辑保存在项目侧，符合可逆原则。

### 目前容易被竞品超过的地方

1. **发行级质量证明不足**：没有把 EPUBCheck/Ace/Kindle Previewer 结果整合成可读报告。
2. **章节只完成了“识别与修订”，还未成为真正内容树**：缺拖放重排、升级/降级、拆分、合并、排除 TOC、前后置页语义。
3. **现代 Kindle 路线不够突出**：当前 MOBI 很有兼容价值，但 Amazon 当前官方工作流更偏向 EPUB/KPF。
4. **格式输入面窄**：只有 TXT，无法直接接收 Markdown、HTML、DOCX/ODT、EPUB。
5. **东亚高级排版仍浅**：缺直排/右翻、Ruby/注音、纵中横、简繁和标点清理。
6. **转换可解释性仍不足**：用户应能知道“哪条规则命中了哪一行、清理前后改了什么、目录项指到哪里”。

## 七、建议路线图

### P0：下一阶段必须完成

#### P0-1 成品“一键验收中心”

将当前转换前检查升级成两阶段：

1. **输入预检**：保留现有编码、章节、封面、插图、字体、路径冲突检查；
2. **成品验收**：转换后自动检查 EPUB/MOBI，并为每本书显示“通过/警告/失败”。

EPUB 至少应集成或可配置调用：

- [EPUBCheck](https://github.com/w3c/epubcheck)（调研日官方仓库列出的生产版为 5.3.0）；
- [Ace by DAISY](https://github.com/daisy/ace) 的基础可访问性检查（最新公开版 1.4.6）；
- 自有规则：目录链接、spine 顺序、重复 ID、封面声明、图片尺寸/体积、语言、空章节、不可达资源、CSS/字体引用；
- 校验结果应能双击定位到“书→章节/资源→问题”，并支持导出纯文本/JSON 报告。

MOBI 至少继续执行现有 Palm/EXTH/BOUNDARY 结构验证，并在报告中明确“结构通过不等于真机显示已确认”。

**验收标准**：一个故意破坏目录链接、封面声明、语言标签和图片引用的测试 EPUB，界面能逐项报告并定位；合法输出在 EPUBCheck 中零 error。

#### P0-2 可拖放章节树编辑器

把章节检查窗口升级成单书结构树：

- 卷/章/节三级节点可拖放重排；
- 一键升级/降级层级；
- 在正文任意行拆分章节；
- 合并到上一章/下一章；
- 修改显示标题但不改原 TXT；
- 设置“正文存在但不进入目录”或“进入目录”；
- 标记前置页、正文、后置页；
- 右侧显示所选节点正文预览、来源行号和命中规则；
- 批量操作前显示变更摘要，并写入项目文件。

这比继续增加更多正则输入框更重要。Kindle Create 的拆分/合并/拖放和 Scrivener/Jutoh 的树模型已经证明此交互有效。

**验收标准**：对同一本 TXT，用户能不修改原文件完成“移动一章、把一章挂入某卷、拆分一章、排除一个目录项”，EPUB nav/NCX、HTML 目录和 MOBI 目录保持一致。

#### P0-3 “原版兼容”与“现代发布”双模式

不要删除现有 MOBI 路线，而要明确分流：

- **原版 EasyPub 兼容模式**：默认参数、HTML/CSS、EPUB 与 KindleGen 联合 MOBI 继续受黄金样本保护；
- **现代 EPUB/Kindle 模式**：输出 EPUB 3.3 结构、nav、landmarks、可访问性元数据、替代文本等，目标是 KDP/Send to Kindle/现代阅读器；
- 预设名称、输出扩展和帮助文本明确说明两者用途，不让用户误以为 MOBI 仍是 Amazon 当前出版首选。

现代模式应逐步以 [W3C EPUB 3.3](https://www.w3.org/TR/epub-33/) 为规范基线，但不能让改动破坏兼容模式的黄金样本。

#### P0-4 Kindle Previewer 一键联动

- 自动发现或让用户选择 Kindle Previewer；
- 转换成功后提供“用 Kindle Previewer 打开所选书”；
- 首次使用给出精简检查清单：封面、开始阅读位置、目录、字体大小切换、横竖屏、插图、章节跳转；
- 项目中记录“尚未检查/已人工确认”状态，但不把打开过 Previewer 当成自动通过。

Amazon 官方文档未承诺稳定的无界面自动化接口，因此第一阶段应做可靠的一键打开与人工验收记录，不要依赖未公开参数。

#### P0-5 转换完整性与可复现报告

借鉴 OOMOL 的字数校验和 calibre 的调试输出，每本书记录：

- 输入字节数、识别编码、有效字符/段落/章节数；
- 清理/规范化改动数量；
- 输出章节数、正文有效字符数、图片/字体清单；
- 使用的预设、目录规则、CSS 摘要、程序版本；
- 警告和校验器版本。

报告应进入历史记录并可导出。对“字数减少异常”“章节从 300 变成 3”“重复标题过多”等设置阈值警告。

### P1：形成中文长篇专业优势

#### P1-1 可预览、可撤销的文本清理流水线

新增“文本处理”分页，以顺序规则组成管线，每项都可关闭并预览差异：

- 硬换行智能合并；
- 多余空行/空白规范；
- 行首缩进统一；
- 全角/半角和标点规范；
- 简体↔繁体（建议外接 OpenCC，默认关闭）；
- 网站尾注、网址、广告和重复分隔线清理；
- 自定义查找替换规则，可限定正文/标题；
- 章节编号重排只提供预览，不默认改写。

关键原则：永远保留原 TXT，显示逐条 diff，可为不同来源目录绑定不同清理预设。

#### P1-2 东亚排版模式

增加现代 EPUB 模式专属选项：

- 横排左翻、直排右翻；
- `writing-mode: vertical-rl` 与 `page-progression-direction="rtl"`；
- Ruby/注音、纵中横（TCY）、标点避头尾；
- 简中/繁中/日文语言标签与字体回退预设；
- 针对 Kindle、Kobo、Apple Books 的已知差异说明和设备测试样本。

实现时应建立专门黄金样本，不要把纵排 CSS 叠加到原版兼容模式。

#### P1-3 输入适配器

按优先级支持：

1. Markdown；
2. HTML/HTMLZ；
3. DOCX/ODT；
4. EPUB 导入后重编译。

各输入统一转换成同一个 Book/Section/Block 中间模型，再走目录、元数据、资源、预览、校验和输出。不要为每种格式复制一套转换器 UI。

#### P1-4 前置页与后置页生成器

提供模板化页面：扉页、版权页、献词、序、简介、作者介绍、后记、同作者作品、来源说明。页面可选择：

- 是否进入 spine；
- 是否进入导航目录；
- 前置/正文/后置语义；
- 仅兼容模式、仅现代模式或两者都输出。

#### P1-5 元数据模型继续扩展

在现有译者、ISBN、日期、出版社、类别、语言、简介基础上增加：

- 副标题、系列名与系列序号；
- 多作者/贡献者及角色；
- 多标识符（ISBN、ASIN、自定义来源 ID）；
- 权利、来源 URL、主题/标签、版本/修订号；
- EPUB 3 accessibilitySummary、accessMode、accessibilityFeature 等现代字段。

文件夹元数据映射应支持：规则优先级、子目录继承、文件名正则捕获、匹配预览、冲突提示和“逐书手动值始终最高优先级”。

#### P1-6 预览矩阵

现有整书预览可增加：

- 手机/平板/Kindle 常见宽高预设；
- 字号小/中/大、横竖屏、浅色/深色；
- 目录树与正文联动；
- 图片超宽、固定字号、横向溢出、低对比度的自动提示。

界面需明确标注“近似预览”，并保留 Kindle Previewer 作为 Kindle 最终检查入口。

### P2：生态与高级生产力

#### P2-1 稳定的过滤器/插件接口

第一阶段无需做插件商店，只定义本地、可版本化的四个钩子：

1. 输入读取后；
2. 章节识别后；
3. EPUB 打包前；
4. 成品生成后。

插件获得受限的结构化 Book 模型，不直接随意修改任意文件；项目应记录插件 ID/版本，缺失插件时明确警告。可先支持本地可执行程序/脚本的 JSON 输入输出，再考虑托管插件。

#### P2-2 项目 checkpoint 与差异比较

- 手动创建检查点；
- 批量章节改动、清理规则应用前自动检查点；
- 比较章节树、元数据、CSS、插图和正文派生差异；
- 可恢复但不覆盖源 TXT。

这比增加更多自动保存副本更有用，也直接借鉴 calibre 和 Scrivener 的成熟模式。

#### P2-3 一个项目多发布变体

借鉴 Jutoh：同一本书可建立“Kindle 现代版”“原版 MOBI”“Kobo EPUB”“繁体直排版”等配置，共享正文和章节树，仅覆盖封面、ISBN、CSS、语言、排版方向和输出选项。

#### P2-4 可选 AI 助手

只把 AI 用在需要判断、但可人工确认的环节：

- 从异常标题中建议章节层级；
- 识别疑似广告/重复段落；
- 生成图片替代文本草稿；
- 给出元数据/简介草稿。

必须默认关闭、明确显示将发送的文本范围、支持本地 OpenAI 兼容服务、所有结果先预览后应用，并且黄金兼容测试永远不依赖 AI。

#### P2-5 文件夹增量构建

对收藏目录保存输入文件哈希和相关资源哈希，只重转发生变化的书；提供“监视模式”时必须由用户显式开启。这个方向吸收 Narou.rb 的更新管理价值，但不内建站点抓取。

#### P2-6 印刷 PDF（低优先级）

只有当用户明确有自出版印刷需求时再做。Vellum、Atticus、Reedsy、Scrivener 已证明这是一个独立且复杂的产品面：开本、出血、孤行寡行、页眉页脚、PDF/X、字体与图片色彩都不是“把 EPUB 打印一下”。短期可导出 DOCX/ODT 或交给专业工具，不应草率加入“PDF”按钮。

## 八、明确不建议近期做的事情

1. **不做完整书库/阅读进度/设备管理来追 calibre 或 Koodo**：会稀释转换器定位。
2. **不做云账号、DRM、在线书城**：与本地隐私和可控性冲突，维护/合规成本高。
3. **不做 SCORM、测验、互动 App 打包**：这是 Kotobee 的教育出版赛道。
4. **不把 AI 变成必经步骤**：会破坏可复现性、离线能力和原版兼容。
5. **不因“现代”而移除原版兼容 MOBI**：现有用户和老设备仍有明确需求，应通过双模式隔离演进。
6. **不宣称内置预览等同 Kindle 真机**：必须保留 Amazon 官方 Previewer 和真机确认层级。
7. **不默认自动清理正文**：任何删广告、合并行、简繁/标点转换都应先给 diff。

## 九、建议的版本顺序

| 版本目标 | 主要内容 | 成功判据 |
|---|---|---|
| v0.16 | EPUBCheck 成品验收、报告导出、Kindle Previewer 一键打开 | 合法 EPUB 零 error；错误可定位；不破坏原版黄金样本 |
| v0.17 | 可拖放章节树、拆分/合并、升级/降级、TOC 排除 | HTML TOC、nav/NCX、MOBI 目录一致；项目往返无损 |
| v0.18 | 原版兼容/现代 EPUB 双模式、EPUB 3.3 元数据和 landmarks | 两套独立黄金样本；现代 EPUB 可通过 EPUBCheck |
| v0.19 | 文本清理流水线、差异预览、来源目录绑定 | 所有变更可预览/撤销；源 TXT 零修改 |
| v0.20 | Markdown/HTML 输入与东亚直排基础 | 同一中间模型；直排/右翻样本在 Kindle/Kobo/Apple 至少选定设备验证 |
| 后续 | DOCX/ODT/EPUB 输入、项目变体、checkpoint、插件、可选 AI | 按真实用户需求逐项立项，不提前堆叠 |

## 十、最终产品建议

EasyPub Modern 最值得守住的不是“格式数量”，而是以下组合：

- **比 calibre 更专注中文 TXT 长篇和批量任务；**
- **比 Sigil/Jutoh 更容易上手；**
- **比 Atticus/Reedsy 更本地、更可控、更适合批量；**
- **比 Vellum 更适合 Windows 和中文网文，但用标准校验缩小成品质差距；**
- **比 Kindle Create 更懂中文章节、编码、直排和老 Kindle 兼容；**
- **比小型 TXT→EPUB 脚本更可靠：有项目、预览、成品验收、真机链路和回归证据。**

如果只选择一个最有价值的下一步，应该是：

> **把章节编辑升级成真正的可拖放内容树，并让每次输出都经过 EPUBCheck + Kindle Previewer 可核查闭环。**

这会直接改善用户能感知的编辑效率和最终可靠性，同时不会把项目带离“现代版 EasyPub”的主线。

## 十一、主要一手来源索引

- calibre：[官网](https://calibre-ebook.com/)、[官方仓库](https://github.com/kovidgoyal/calibre)、[转换文档](https://manual.calibre-ebook.com/conversion.html)、[编辑器文档](https://manual.calibre-ebook.com/en/edit.html)
- Sigil/PageEdit：[Sigil](https://github.com/Sigil-Ebook/Sigil)、[PageEdit](https://github.com/Sigil-Ebook/PageEdit)
- Pandoc：[官网](https://pandoc.org/)、[EPUB 指南](https://pandoc.org/epub.html)、[Releases](https://pandoc.org/releases.html)
- Asciidoctor EPUB3：[仓库](https://github.com/asciidoctor/asciidoctor-epub3)、[Releases](https://github.com/asciidoctor/asciidoctor-epub3/releases)
- Vellum：[官网](https://vellum.pub/)、[技术规格](https://vellum.pub/specs/)、[帮助](https://help.vellum.pub/)
- Atticus：[官网](https://www.atticus.io/)、[Quick Start](https://www.atticus.io/quick-start-guide/)
- Scrivener：[官网](https://www.literatureandlatte.com/scrivener/overview)、[Release Notes](https://www.literatureandlatte.com/scrivener/release-notes)、[手册](https://www.literatureandlatte.com/docs/Scrivener_Manual-Win.pdf)
- Jutoh：[官网](https://www.jutoh.com/)、[功能](https://www.jutoh.com/features.html)、[更新日志](https://www.jutoh.com/whatsnew.html)
- Amazon：[Kindle Create](https://kdp.amazon.com/en_US/help/topic/GUGQ4WDZ92F733GC)、[Kindle Previewer](https://kdp.amazon.com/en_US/help/topic/G202131170)
- Reedsy Studio：[官方格式化页](https://reedsy.com/studio/format-a-book/)
- Kotobee Author：[产品页](https://www.kotobee.com/features/products/author)、[版本页](https://www.kotobee.com/en/products/author/versions)
- LibreOffice：[官网](https://www.libreoffice.org/)、[EPUB 导出帮助](https://help.libreoffice.org/latest/en-US/text/shared/01/ref_epub_export.html)
- EPUBCheck：[W3C 官方仓库](https://github.com/w3c/epubcheck)
- Ace：[DAISY 官方仓库](https://github.com/daisy/ace)
- EPUB 3.3：[W3C Recommendation](https://www.w3.org/TR/epub-33/)
- AozoraEpub3-JDK21：[官方仓库](https://github.com/AozoraEpub3-JDK21/AozoraEpub3-JDK21)
- Narou.rb：[官方仓库](https://github.com/whiteleaf7/narou)、[官方 Wiki](https://github.com/whiteleaf7/narou/wiki)
- cn-epub-maker：[官方仓库](https://github.com/muyen/cn-epub-maker)
- OOMOL TXT to EPUB Converter：[官方仓库](https://github.com/oomol-lab/txt-to-epub-converter)
- SEED.html：[官方仓库](https://github.com/stewarthaines/seed-html)
- Koodo Reader：[官方仓库](https://github.com/koodo-reader/koodo-reader)、[Releases](https://github.com/koodo-reader/koodo-reader/releases)
- Quarto：[Books 文档](https://quarto.org/docs/books/)、[EPUB 选项](https://quarto.org/docs/reference/formats/epub.html)、[Releases](https://github.com/quarto-dev/quarto-cli/releases)
- ystyle/kaf-cli：[官方仓库](https://github.com/ystyle/kaf-cli)、[Releases](https://github.com/ystyle/kaf-cli/releases)
- k4yt3x/txt2epub：[官方仓库](https://github.com/k4yt3x/txt2epub)
- bluicezhen/txt-to-epub：[官方仓库](https://github.com/bluicezhen/txt-to-epub)
