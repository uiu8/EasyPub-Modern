using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 诊断抑制策略 —— 文档 §8.2.2 的 R3。
///
/// 它替掉了原来的一个全局 <c>bool referenceAligned</c>。那个 bool 的问题是：只要有一个手工改过的
/// 节点，它整本书就变回 false，于是六类本地提醒**全部**重新打开。
///
/// 这里钉住三件事：
/// <list type="number">
/// <item><b>目录证据推翻不了正文内容类 finding</b> —— 章节身份正确，不蕴含正文没有重复。</item>
/// <item><b>目录证据能推翻身份类 finding</b> —— 它回答的就是同一个问题。</item>
/// <item><b>只有完全覆盖才抑制</b> —— 一个手工节点不该让相邻的已验证区域跟着复活，
/// 反过来未被覆盖的那一处也不能被吞掉。</item>
/// </list>
/// </summary>
public class FindingSuppressionTests
{
    private static ReferenceVerificationIndex Index(params string[] nodeIds) =>
        new(nodeIds.ToHashSet(StringComparer.Ordinal), new HashSet<int>());

    [Fact]
    public void A_body_content_finding_survives_reference_verification()
    {
        // 参考目录明确有第十章、第十一章，两章都成功 verified —— 但源 TXT 因抓取错误，
        // 二者正文完全一样。章节身份被目录验证，**不能**否定"正文被错误复制"。
        var decision = FindingSuppressionPolicy.Decide(
            "chapter_content_duplicate", new FindingScope(["a", "b"], []), Index("a", "b"));

        Assert.False(decision.IsSuppressed);
        Assert.Equal(SuppressionDecisionKind.ReportWithReferenceContext, decision.Kind);
    }

    [Fact]
    public void A_repeated_sequence_finding_survives_reference_verification()
    {
        // 目录反而**可能帮助证明**"这段正文在源文件里重复拼接了"，所以它同样不可被推翻。
        var decision = FindingSuppressionPolicy.Decide(
            "chapter_repeated_sequence", new FindingScope(["a"], []), Index("a"));

        Assert.False(decision.IsSuppressed);
    }

    [Fact]
    public void A_cross_title_duplicate_finding_survives_reference_verification()
    {
        var decision = FindingSuppressionPolicy.Decide(
            "chapter_cross_title_duplicate", new FindingScope(["a", "b"], []), Index("a", "b"));

        Assert.False(decision.IsSuppressed);
    }

    [Fact]
    public void An_identity_finding_is_superseded_when_the_directory_covers_it()
    {
        // 「这一段到底是什么章」是目录回答的同一个问题，所以本地猜测没有存在意义。
        foreach (var code in new[]
                 {
                     "chapter_unrecognized", "chapter_heading_typo",
                     "chapter_number_order", "chapter_duplicate", "chapter_unnumbered",
                 })
        {
            var decision = FindingSuppressionPolicy.Decide(code, new FindingScope(["a"], []), Index("a"));
            Assert.True(decision.IsSuppressed, code);
        }
    }

    [Fact]
    public void A_partially_covered_scope_is_still_reported()
    {
        // 一个手工节点**不该**让整个 scope 被当成已验证；反过来，它也不该让别的已验证区域复活。
        var decision = FindingSuppressionPolicy.Decide(
            "chapter_unrecognized", new FindingScope(["a", "manual"], []), Index("a"));

        Assert.False(decision.IsSuppressed);
        Assert.Contains("没有覆盖", decision.Reason);
    }

    [Fact]
    public void Without_a_directory_nothing_is_suppressed()
    {
        foreach (var code in new[] { "chapter_unrecognized", "chapter_duplicate", "chapter_content_duplicate" })
        {
            var decision = FindingSuppressionPolicy.Decide(
                code, new FindingScope(["a"], [10]), ReferenceVerificationIndex.None);
            Assert.False(decision.IsSuppressed, code);
        }
    }

    [Fact]
    public void The_index_only_counts_entries_the_directory_actually_verified()
    {
        // 手工改过的节点**不是**已验证的 —— 这正是"目录校验后 + 手工修改"这棵树的状态。
        // 旧实现要求"所有非卷条目都已验证"才算对齐，于是这一章会让整本书的抑制全部失效。
        var entries = new[]
        {
            Entry("a", "reference", 10),
            Entry("b", "reference-volume", 20),
            Entry("c", "manual", 30),
            Entry("d", "pattern", 40),
        };

        var index = ReferenceVerificationIndex.Build(entries);

        Assert.Contains("a", index.VerifiedNodeIds);
        Assert.DoesNotContain("c", index.VerifiedNodeIds);
        Assert.DoesNotContain("d", index.VerifiedNodeIds);
        // 卷边界条目（reference-volume）不算"已验证的章"，与旧实现的定义保持一致。
        Assert.DoesNotContain("b", index.VerifiedNodeIds);
    }

    /// <summary>造一个只填了抑制判断所需字段的条目。</summary>
    private static ChapterTreeEntry Entry(string id, string source, int titleLine) =>
        new(id, $"第{titleLine}章 标题", 1, IncludeInToc: true, titleLine, []) { RecognitionSource = source };

    /// <summary>
    /// 集成层面：把样书**整棵树**的条目都标成"目录已验证"，看哪些 finding 该活下来。
    ///
    /// 这是 §8.5 要的 before/after 对照。before 是没有目录的
    /// <c>Sample_book_diagnostic_shape_is_recorded</c>（四码分布），after 就是这里 ——
    /// 而结论是**两者完全相同**。
    ///
    /// 原因是样书里的每一类 finding 都落在"目录证据推翻不了"的那一侧：
    /// <list type="bullet">
    /// <item><c>chapter_content_duplicate</c>（32）—— 正文内容类，目录证明不了正文没被错抄；</item>
    /// <item><c>chapter_repeated_sequence</c>（2）—— 同上；</item>
    /// <item><c>chapter_number_gap</c>（9）—— 故意不抑制，有目录时它恰恰是最该说的那件事；</item>
    /// <item><c>chapter_structure_suggested</c>（1）—— 不在受影响的 detector 之列。</item>
    /// </list>
    ///
    /// <b>差值为 0 才是正确结果。</b>如果 R3 之后这个数字变小了，说明抑制把真实的源文件损坏藏了起来。
    /// </summary>
    [Fact]
    public async Task Reference_verification_does_not_hide_body_content_findings()
    {
        var root = SampleRoot();
        var document = await ChapterTreeDocument.LoadAsync(Path.Combine(root, "缺陷样书.txt"));

        var before = ChapterReviewAnalyzer.Analyze(document);
        var beforeByCode = before.Groups.GroupBy(g => g.Issue.Code).ToDictionary(g => g.Key, g => g.Count());

        var allVerified = document.Entries.Select(e => e with { RecognitionSource = "reference" }).ToArray();
        var after = ChapterReviewAnalyzer.Analyze(document, allVerified);
        var afterByCode = after.Groups.GroupBy(g => g.Issue.Code).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(beforeByCode, afterByCode);
        Assert.Equal(32, afterByCode["chapter_content_duplicate"]);
        Assert.Equal(9, afterByCode["chapter_number_gap"]);
    }

    private static string SampleRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", "六卷缺陷样书");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("找不到 samples/六卷缺陷样书。");
    }
}
