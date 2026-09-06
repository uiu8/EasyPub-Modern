# Kindle 章节顶部导航与脚注弹窗研究

日期：2026-09-02

## 结论

第一次测试版能同时保留“上一章 / 目录 / 下一章”而不弹脚注，最合理的解释不是“Kindle 允许双向链接”，而是它的脚注启发式只把目标位置之后附近出现的返回链接纳入了弹窗判断：第一次的源码顺序是“导航 -> 带目标 ID 的标题”，反向链接位于目标之前；后续版本把目标放到导航之前或把导航移到标题之后，反向链接进入目标之后的内容，于是被识别为脚注。这是依据同一台真机结果形成的假设，不是 Amazon 公布的算法。

Amazon 只公开了高层规则：双向内部链接可能被识别为脚注，非脚注链接应避免 `A -> B`、`B -> A`。[Amazon Hyperlink Guidelines](https://kdp.amazon.com/en_US/help/topic/GQ6JQ7FM6C72HE4X)

test6 的真机结果进一步缩小了问题范围：`tfoot(nav) -> tbody(h#title)` 可以一击直达且不弹脚注，但 Kindle 确实按表格视觉顺序显示为“标题 -> 导航”。因此表格方案只验证了“链接在落点之前”这一条件，不能作为最终布局。

test7 改为普通文档流中的“导航链接 -> 导航末尾落点 -> 章节标题”。这样所有往返链接仍位于落点之前，导航本身则自然显示在标题上方；链接目标由标题改为导航末尾的真实布局节点，避免再次出现跳转后需要向前翻页。

## test7：导航末尾落点

建议的最小结构：

```xhtml
<div class="chapter-nav">
  <a href="上一章#chapter-nav-target">上一章</a>
  <a href="下一章#chapter-nav-target">下一章</a>
  <span id="chapter-nav-target">&#160;</span>
</div>
<h2 id="title">章节标题</h2>
```

这里有三个关键约束：

1. 目标元素放在导航的最后，确保当前章的“上一章 / 下一章”链接都排在目标之前。
2. 目标使用包含不换行空格的真实 `span`，不使用 `display:none` 或零尺寸隐藏锚点，避免 Kindle 忽略目标或把视口对齐到错误位置。
3. 导航与标题保持普通源码和视觉顺序，并使用 `page-break-inside: avoid`、`page-break-after: avoid` 尽量避免二者被分页拆开。

实验性章节合包必须把 `chapter-nav-target` 与 `title` 一样改写为包内唯一 ID，并同步改写所有跨章链接。自动化测试只能确认 XHTML 结构和链接闭合；真机仍需确认跳转后的首屏位置。

## test6 历史实验：真实 table/tfoot 结构

建议的最小结构：

```xhtml
<table class="chapter-heading">
  <tfoot>
    <tr><td><div class="chapter-nav">上一章 | 目录 | 下一章</div></td></tr>
  </tfoot>
  <tbody>
    <tr><td><h2 id="title">章节标题</h2></td></tr>
  </tbody>
</table>
```

这里的源码顺序仍是 `nav -> title target`，和第一次不弹窗的版本一致；视觉表格顺序则是 `title -> nav`。

依据：

- W3C 的 XHTML Tables Module 内容模型本来就允许且要求 `tfoot` 位于 `tbody` 前：`thead?, tfoot?, tbody+`。[W3C XHTML Tables Module](https://www.w3.org/TR/xhtml-modularization/abstract_modules.html#s_tablemodule)
- CSS 2.2 规定 `table-footer-group` 在视觉格式化时始终显示于其他行和行组之后；HTML 的 `tfoot` 默认就是该显示类型。[W3C CSS 2.2 Tables](https://www.w3.org/TR/CSS22/tables.html#table-display)
- Amazon 的 KF8 支持表明确列出 `table`、`tbody`、`td`、`tfoot`、`tr` 均受支持。[Amazon KF8 HTML/CSS Support](https://kdp.amazon.com/en_US/help/topic/GG5R7N649LECKP7U)
- Amazon 的重排版表格指南说明简单 HTML 表格可在 Kindle 设备与应用中显示，并建议 Enhanced Typesetting 表格使用 `thead/tbody/tfoot`。[Amazon Reflowable Table Guidelines](https://kdp.amazon.com/en_US/help/topic/GZ8BAXASXKB5JVML)

风险：

1. KindleGen 可能在生成 MOBI/KF8 时按视觉顺序重排内部流位置。如果它把标题实际写到导航之前，脚注启发式仍可能触发。公开规范无法确认这一点。
2. MOBI7 对复杂表格的支持弱于 KF8；不过该结构只有一列两行，不使用合并或嵌套，属于最简单的表格形态。最坏情况可能退化为源码顺序，即重新显示“导航在标题上方”，而不是 flex/position 那样重叠正文。
3. 表格不是语义上最理想的章节标题容器；应清除边框、单元格内边距和强制字号，并保持表格极短，避免触发表格查看器或分页异常。为兼容 XHTML 1.1，不额外加入 HTML5 `role` 属性。
4. 必须分别测试普通章节文件与“实验性章节合包”，因为后者会改写 ID 和 href。

真机结果：链接一击直达且没有脚注弹窗，但视觉顺序稳定为“标题 -> 导航”，不符合章顶导航目标，因此由 test7 的导航末尾落点方案取代。

## 其他方案排序

### 2. CSS 模拟表格行组

让普通块容器 `display: table`，导航使用 `display: table-footer-group`，标题使用 `display: table-row-group`。W3C 标准可以实现相同重排；Amazon 的 KF8 表仅笼统列出 `display` 支持，却没有逐一保证 `table-footer-group` 值。MOBI7 的 CSS 能力更弱，因此兼容性低于真实的 `table/tfoot`，只适合作为真实表格失败后的试验。

### 3. 给导航源位置增加独立 bookmark/ID

Jutoh 记录过一个经验性规避法：在包含链接的段落前增加 bookmark，某些 Paperwhite 会因此不使用脚注弹窗。但该资料也明确称 Kindle 的判断是易变的启发式，不能保证成功。[Jutoh KB0139](https://www.jutoh.com/kb/html/section-0142.html)

可将它作为 table 方案后的单变量实验，不能与 table 改动同时加入，否则真机结果无法归因。

### 4. 使用方向性目标 ID 或无 fragment 的整章链接

可以让“上一章”和“下一章”落到不同 ID，或直接链接 `chapterN.html`，尝试破坏 Kindle 对精确往返锚点的配对。没有官方证据表明 Kindle 按 ID 而不是按文档/位置识别互链，成功率未知，只适合后续小样本矩阵。

### 5. `rel="prev"` / `rel="next"`

HTML 语义正确，但 Amazon 没有说明脚注启发式会读取 `rel`，MOBI7 也可能丢弃该属性。可以低成本附加，却不应把它当作修复机制。

## 不建议方案

- `display:flex` + `order`：Amazon 的 KF8 CSS 支持表中没有 `flex`、`flex-direction` 或 `order`；MOBI7 更不可靠。
- `position:absolute/relative`、负 margin 或 transform：章节标题和导航高度会随字体、边距、横竖屏变化，容易重叠、裁切或留白。Amazon 对重排版内容也明确建议避免负定位值；绝对定位的官方示例主要用于固定版式。
- `caption-side: bottom`：Amazon 明确说明 Enhanced Typesetting 不支持底部 caption，会按顶部 caption 显示。
- 隐藏目标 (`display:none`/`visibility:hidden`)：Amazon 的诊断文档指出，目录或链接若指向隐藏元素可能无法解析；也无法保证跳转后的视口位置。
- JavaScript 或私有 Kindle URI：Kindle 内容中的脚本保留给 Amazon 使用，没有受支持的作者 API 可直接打开原生目录或代替普通内部链接。

## 真机验证矩阵

每个测试文件只改变一个变量，并使用不同书名、唯一 identifier/ASIN 与文件名，先从 Kindle 删除旧副本，避免缓存干扰：

1. test7：`nav links -> trailing target -> h#title`，检查导航是否位于标题上方、三个链接是否一击直达且无弹窗。
2. 分别从正常翻页、Kindle 原生目录、上一章和下一章进入章节，记录首屏是否同时出现完整导航和标题。
3. 先关闭实验性章节合包，再开启合包复测，确认两种路径行为一致。
4. 若 test7 仍有视口偏移，再分别测试块级末尾目标或无 fragment 整章链接；不要组合多个变量。
5. 每个测试文件使用不同书名、唯一 identifier/ASIN 与文件名，并先从 Kindle 删除旧副本，避免缓存干扰。

“生成成功”和 Kindle Previewer 的表现不能替代真机结论；最终只应把用户设备的结果描述为已验证范围。
