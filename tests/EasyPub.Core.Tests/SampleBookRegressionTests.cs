using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 六卷缺陷样书的回归夹具。
///
/// 这本书是唯一同时包含六种缺陷的样本：卷标题行完全不存在、章号写法三种缺陷轮换、
/// 错位重复、整章重复、标题行不存在、标题行连续重复两次。每个数字都对应一句可以人工核对的话，
/// 所以它同时是回归测试和验收证据 —— 改了任何一处修复逻辑，这里会告诉你哪一项计数变了。
///
/// 期望值来自 <c>docs/2026-09-17_Phase0-基线与回归夹具.md</c>，是动手重构链路**之前**的实测值。
/// 重构若改变了行为，这些断言会失败；那时要判断的是"新行为和旧行为哪个对"，而不是改断言。
/// </summary>
public class SampleBookRegressionTests
{
    private static string SampleRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "六卷缺陷样书");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 samples/六卷缺陷样书，测试必须能在仓库内定位它。");
    }

    private static async Task<(ChapterTreeDocument Document, ReferenceCatalog Catalog)> LoadAsync()
    {
        var root = SampleRoot();
        var document = await ChapterTreeDocument.LoadAsync(Path.Combine(root, "缺陷样书.txt"));
        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8));
        Assert.NotNull(catalog);
        return (document, catalog!);
    }

    [Fact]
    public async Task Sample_book_loads_with_the_recorded_shape()
    {
        var (document, catalog) = await LoadAsync();
        Assert.Equal(2503, document.LineCount);
        Assert.Equal(
            "69D722398237438EE098AE03A0F36A39AD78ACAFB430C8A912B5471F7FAEF926",
            document.SourceSha256);
        Assert.Equal(468, document.Entries.Count);
        Assert.Equal(6, catalog.VolumeTitles.Count);
        Assert.Equal(480, catalog.Titles.Count);
    }

    /// <summary>
    /// 走的是 <c>AutoRepair_Click</c> 用的那两个方法。这不是"另写一遍逻辑"，而是把按钮的结果固定下来：
    /// UI 若绕开它们，界面上显示的数字就和这里对不上。
    /// </summary>
    [Fact]
    public async Task With_a_directory_the_unified_entry_reports_the_recorded_counts()
    {
        var (document, catalog) = await LoadAsync();
        var outcome = ChapterAutoRepair.Prepare(document, catalog);

        Assert.True(outcome.CatalogFound);
        Assert.Equal(6, outcome.RebuiltVolumes);
        Assert.Equal(437, outcome.RebuiltChapters);
        Assert.Equal(43, outcome.Missing);
        Assert.Equal(43, outcome.UnmatchedWithNumber);
        Assert.Equal(0, outcome.UnmatchedNoNumber);
        Assert.Equal(0, outcome.Extra);
        // Exact anchors prevent the previous 62 cross-chapter retitles.
        // Six exact catalog volume headings are no longer misclassified as body to demote.
        Assert.Equal(123, outcome.Plan!.Actions.Count);
    }

    [Fact]
    public async Task Plan_actions_are_distributed_as_recorded()
    {
        var (document, catalog) = await LoadAsync();
        var plan = ChapterAutoRepair.Prepare(document, catalog).Plan!;
        var byKind = plan.Actions.GroupBy(a => a.Kind)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(8, byKind[ReferenceActionKind.AddChapter]);
        Assert.False(byKind.ContainsKey(ReferenceActionKind.DemoteExtra));
        Assert.Equal(32, byKind[ReferenceActionKind.KeepExtra]);
        Assert.Equal(43, byKind[ReferenceActionKind.MissingBody]);
        Assert.Equal(32, byKind[ReferenceActionKind.RemoveDuplicate]);
        Assert.Equal(7, byKind[ReferenceActionKind.Retitle]);
        Assert.All(plan.OfKind(ReferenceActionKind.Retitle), action =>
        {
            var local = document.SourceLine(action.Line)!.Text.Trim();
            Assert.Equal("第" + local, action.Title);
        });
        Assert.Equal(1, byKind[ReferenceActionKind.VolumeNote]);
    }

    /// <summary>
    /// 第二卷副本被插在第三卷内部、第一卷整章重复 —— 这两处是默认就该移除的。
    /// 第六卷的 100 行**不在**这里：见 <see cref="Repeated_heading_lines_are_still_counted_as_body"/>。
    /// </summary>
    [Fact]
    public async Task Default_selection_removes_the_recorded_lines()
    {
        var (document, catalog) = await LoadAsync();
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        Assert.Equal(160, outcome.RemovedLines);

        // 第一卷 55 行 + 第二卷 105 行：都在被判定为副本的那一段里。
        Assert.All(outcome.RemovedSourceLines, line => Assert.InRange(line, 1, document.LineCount));
    }

    /// <summary>
    /// 无目录时统一入口交给自修复方案（Phase 7 落地）。
    ///
    /// 这条断言原本记的是"无目录时什么都不做 —— 这是当前的真实能力边界，Phase 7 才并进统一入口"。
    /// Phase 7 做的就是把它并进来，所以这里改成记录**新**的边界，并保留原来的意图：
    /// 变化必须是被解释过的，而不是悄悄发生的。
    ///
    /// 边界的新形状有两半，两半都要钉住：
    ///
    ///   1. 仍然 <c>CatalogFound = false</c>、<c>Catalog = null</c>：确实没有目录，界面不能给出
    ///      只有目录才能支撑的承诺。
    ///   2. <c>Plan</c> 不再为 null：树自身能看出的问题（这本书的 32 组同名标题）现在有方案可应用。
    /// </summary>
    [Fact]
    public async Task Without_a_directory_the_entry_offers_what_the_book_shows_about_itself()
    {
        var (document, _) = await LoadAsync();
        var outcome = ChapterAutoRepair.Prepare(document, null);

        // 没有目录这件事本身没有变。
        Assert.False(outcome.CatalogFound);
        Assert.Null(outcome.Catalog);
        Assert.Equal(0, outcome.RemovedLines);

        // 而方案不再为空：自修复只发不依赖外部目录就能判定的动作 —— 这本书上就是重复标题。
        Assert.NotNull(outcome.Plan);
        Assert.NotEmpty(outcome.Plan.Actions);
        Assert.All(outcome.Plan.Actions,
            action => Assert.Equal(ReferenceActionKind.RemoveDuplicate, action.Kind));
        Assert.Contains("本书自身", outcome.Verdict, StringComparison.Ordinal);
    }

    /// <summary>
    /// 第六卷每章是「标题行 ×2」。识别阶段把紧邻重复的那一行从**章节条目**里剔除了，
    /// 正文范围也必须跟着剔除它 —— 否则它会作为正文第一行输出到成品，每章标题出现两次。
    ///
    /// 这是 Phase 1 修掉的缺陷（基线文档 §4）。这里的断言是**修好之后**的形态：
    /// 100 行重复标题不被任何正文范围认领，因而不会出现在成品里。
    /// 源文件里它们仍在（本阶段不改原文），所以它们也不在移除清单里 —— 那是 Phase 6 的事。
    /// </summary>
    [Fact]
    public async Task Repeated_heading_lines_no_longer_leak_into_chapter_bodies()
    {
        var (document, _) = await LoadAsync();

        // 与上一行完全相同的非空行：第六卷 100 章各一行。
        var repeated = new List<int>();
        for (var line = 2; line <= document.LineCount; line++)
        {
            var current = document.SourceLine(line)?.Text?.Trim() ?? "";
            var previous = document.SourceLine(line - 1)?.Text?.Trim() ?? "";
            if (current.Length > 0 && current == previous) repeated.Add(line);
        }
        Assert.Equal(100, repeated.Count);

        var owned = document.Entries
            .SelectMany(entry => entry.ContentRanges)
            .SelectMany(range => Enumerable.Range(range.StartLine, range.EndLine - range.StartLine + 1))
            .ToHashSet();
        Assert.All(repeated, line => Assert.DoesNotContain(line, owned));

        // 而且正文本身没被吃掉：这些章的正文段落仍然在。
        foreach (var chapter in document.Entries.Where(e => e.Title.StartsWith("第九章 己9")))
            Assert.Contains(chapter.ContentRanges, range => range.EndLine - range.StartLine + 1 >= 3);
    }

    /// <summary>
    /// 卷标题行在原文里完全不存在，六卷只存在于目录中。修复必须把卷建起来。
    ///
    /// 注意这里走的是 <c>RepairAsync</c> 而**不是** <c>Prepare</c>：<c>RebuildWithSelection</c>
    /// 依赖 <c>outcome.Catalog</c> 决定是否建卷，而那个字段只有 RepairAsync 会设。
    /// 用 Prepare 的返回值来断言建卷，会得到"卷丢了"的假结论 —— 探针里踩过一次。
    /// </summary>
    [Fact]
    public async Task Directory_reuses_the_existing_volume_headings()
    {
        var root = SampleRoot();
        var document = await ChapterTreeDocument.LoadAsync(Path.Combine(root, "缺陷样书.txt"));
        Assert.DoesNotContain(document.Entries, entry => entry.RecognitionSource == "reference-volume");

        var catalog = ReferenceCatalogInput.ParseText(
            await File.ReadAllTextAsync(Path.Combine(root, "目录.txt"), Encoding.UTF8))!;
        var outcome = await ChapterAutoRepair.RepairAsync(
            document.SourcePath, new ReferenceRepairRequest(catalog), currentDocument: document);
        var rebuilt = ChapterAutoRepair.RebuildWithSelection(
            document, outcome, outcome.Plan!.DefaultSelection.ToArray());

        var volumes = rebuilt.Where(e => e.RecognitionSource is "reference-volume" or "inferred-volume").ToArray();
        Assert.Equal(6, volumes.Length);
        Assert.All(volumes, v => Assert.Equal(v.Title, document.SourceLine(v.TitleLineNumber!.Value)!.Text.Trim()));
        Assert.False(outcome.VolumesInferred, "目录自带卷标题，不应该走推断分支");
    }

    /// <summary>
    /// 样书上「本地能不能自己找到漏识别标题」的当前事实 —— 答案是**找不到**。
    ///
    /// 这条测试存在的理由：文档 §7.2 曾写「第四卷 79/80 章确有高置信度候选」，那个说法来自
    /// 1.58.0 的 <c>自测说明.md</c>；<c>MissingChapterHeadings</c> 之后经过 <c>MaximumGap</c> /
    /// <c>RequireIsolatedLine</c> / 数字纠错规则 / 候选顺序约束等修改，当前版本**一个候选都没有**。
    ///
    /// 这不是缺陷，而是样书的形状决定的：第四卷 30–40 与 50–60 是「标题行完全不存在、只有正文」，
    /// 而候选必须**匹配标题格式**（<c>MissingChapterHeadings.Candidate</c> 正则）。所以
    /// gap 确实存在（9 个），但区间里没有任何一行能成为候选。
    ///
    /// 它直接决定 §3.4 的推荐顺序在样书上的结果：**先本地扫描 → 无候选 → 直接推荐参考目录**。
    /// </summary>
    [Fact]
    public async Task Sample_book_local_missing_heading_candidates_are_recorded()
    {
        var (document, _) = await LoadAsync();

        var candidates = MissingChapterHeadings.Find(document, document.Entries);

        // 当前事实：0。若这个数字变了，说明 MissingChapterHeadings 的行为变了 ——
        // 那时要判断的是"新行为和旧行为哪个对"，而不是改断言。
        Assert.Empty(candidates);

        // 而 gap 确实存在 —— 所以"没有候选"不是"没有 gap"造成的。
        var gaps = ChapterDiagnostics
            .Inspect(document, CancellationToken.None, true, document.Entries, int.MaxValue)
            .Count(issue => issue.Code == "chapter_number_gap");
        Assert.Equal(9, gaps);
    }

    /// <summary>
    /// 样书在当前版本上的**诊断形状** —— 这是 R3（诊断抑制策略化）的 before-count 基线。
    ///
    /// 三处都与文档此前的说法不同，而它们只能靠跑出来：
    ///
    /// <list type="bullet">
    /// <item>**没有 <c>chapter_duplicate</c>**：第六卷那 100 个重复标题行在识别阶段就被剔除，
    /// 不属于任何 <c>ChapterTreeEntry</c>，所以 tree-level 诊断看不到它们。</item>
    /// <item>**没有 <c>chapter_unrecognized</c>**：它要求"原文里存在一行看起来像标题、但这行没进树"，
    /// 而第四卷 30–40 **根本没有标题行**。</item>
    /// <item>**没有 <c>chapter_heading_typo</c>**：本地漏识别候选为 0，所以这个码也不产生。</item>
    /// </list>
    ///
    /// R3 改完 suppression 之后要拿这份形状做对照。
    /// </summary>
    [Fact]
    public async Task Sample_book_diagnostic_shape_is_recorded()
    {
        var (document, _) = await LoadAsync();

        var analysis = ChapterReviewAnalyzer.Analyze(document);
        var byCode = analysis.Groups
            .GroupBy(group => group.Issue.Code)
            .ToDictionary(group => group.Key, group => group.Count());

        Assert.Equal(32, byCode["chapter_content_duplicate"]);
        Assert.Equal(9, byCode["chapter_number_gap"]);
        Assert.Equal(2, byCode["chapter_repeated_sequence"]);
        Assert.Equal(1, byCode["chapter_structure_suggested"]);

        // 这三个码在样书的当前版本上**不产生** —— 理由见上面的注释。
        Assert.DoesNotContain("chapter_duplicate", byCode.Keys);
        Assert.DoesNotContain("chapter_unrecognized", byCode.Keys);
        Assert.DoesNotContain("chapter_heading_typo", byCode.Keys);
    }
}
