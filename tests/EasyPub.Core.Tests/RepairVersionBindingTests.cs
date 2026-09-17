using System.Reflection;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 计划与书稿状态的绑定。
///
/// 三条指纹各自回答一个问题：TXT 变了吗、树变了吗、识别规则变了吗。任何一条变了，旧计划描述的
/// 就是一本不存在的书，必须拒绝而不是"尽量套用" —— 半匹配的计划会产出用户从未预览过的树，而预览
/// 正是这件工具敢让人点"应用"的唯一理由。
/// </summary>
public class RepairVersionBindingTests
{
    private static ChapterTreeEntry Entry(string title, int level = 1, int? line = 1,
        params (int Start, int End)[] ranges) =>
        new(Guid.NewGuid().ToString("N"), title, level, true, line,
            ranges.Select(range => new ChapterSourceRange(range.Start, range.End)).ToArray());

    private static TocHierarchyOptions Recognition() => new() { Enabled = true };

    // ── 指纹本身 ────────────────────────────────────────────────

    [Fact]
    public void The_same_tree_fingerprints_the_same_way_twice()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 5)), Entry("第二章", 2, 6, (7, 9)) };
        Assert.Equal(ChapterTreeFingerprint.Compute(entries), ChapterTreeFingerprint.Compute(entries));
    }

    /// <summary>
    /// <c>Entry.Id</c> 每次重建都会换新的。把它算进指纹，就等于说"同一本书重建一次树之后旧计划
    /// 全部失效" —— 每次校验都失败，而计划其实完全有效。
    /// </summary>
    [Fact]
    public void A_regenerated_id_does_not_change_the_tree_fingerprint()
    {
        var first = new[] { Entry("第一章", 1, 1, (2, 5)) };
        var second = new[] { Entry("第一章", 1, 1, (2, 5)) };
        Assert.NotEqual(first[0].Id, second[0].Id);
        Assert.Equal(ChapterTreeFingerprint.Compute(first), ChapterTreeFingerprint.Compute(second));
    }

    /// <summary>每一个会改变修复结果的字段都必须影响指纹，逐个验证。</summary>
    [Fact]
    public void Every_field_that_changes_the_tree_changes_the_fingerprint()
    {
        var baseline = new[] { Entry("第一章", 1, 1, (2, 5)) };
        var expected = ChapterTreeFingerprint.Compute(baseline);

        var variants = new (string Why, ChapterTreeEntry[] Entries)[]
        {
            ("标题", new[] { Entry("第一章 改", 1, 1, (2, 5)) }),
            ("层级", new[] { Entry("第一章", 2, 1, (2, 5)) }),
            ("标题行号", new[] { Entry("第一章", 1, 9, (2, 5)) }),
            ("正文范围", new[] { Entry("第一章", 1, 1, (3, 5)) }),
            ("正文范围个数", new[] { Entry("第一章", 1, 1, (2, 3), (5, 5)) }),
        };
        foreach (var (why, entries) in variants)
            Assert.True(expected != ChapterTreeFingerprint.Compute(entries), $"{why}变了但指纹没变");

        var notInToc = new[] { Entry("第一章", 1, 1, (2, 5)) with { IncludeInToc = false } };
        Assert.NotEqual(expected, ChapterTreeFingerprint.Compute(notInToc));

        var frontMatter = new[] { Entry("第一章", 1, 1, (2, 5)) with { IsFrontMatter = true } };
        Assert.NotEqual(expected, ChapterTreeFingerprint.Compute(frontMatter));

        var noTitleLine = new[] { Entry("第一章", 1, null, (2, 5)) };
        Assert.NotEqual(expected, ChapterTreeFingerprint.Compute(noTitleLine));

        var source = new[] { Entry("第一章", 1, 1, (2, 5)) with { RecognitionSource = "manual" } };
        Assert.NotEqual(expected, ChapterTreeFingerprint.Compute(source));
    }

    /// <summary>
    /// 识别指纹必须覆盖 <see cref="TocHierarchyOptions"/> 的**全部**属性。
    ///
    /// 这条用反射而不是硬编码列表：将来有人给识别设置加一个新开关却忘了加进指纹，识别规则的变化
    /// 就会对版本校验隐形 —— 而这正是这层绑定要防的事。让它在这里红，而不是在生产里悄悄放过。
    /// </summary>
    [Fact]
    public void The_recognition_fingerprint_covers_every_recognition_setting()
    {
        var declared = typeof(TocHierarchyOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var covered = RecognitionFingerprint.CoveredOptionNames
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var missing = declared.Except(covered, StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0,
            "这些识别设置没有被指纹覆盖，改了它们计划不会失效：" + string.Join("、", missing));

        var stale = covered.Except(declared, StringComparer.Ordinal).ToArray();
        Assert.True(stale.Length == 0,
            "指纹声称覆盖了不存在的设置：" + string.Join("、", stale));
    }

    [Fact]
    public void Changing_any_recognition_setting_changes_the_recognition_fingerprint()
    {
        var baseline = Recognition();
        var expected = RecognitionFingerprint.Compute(baseline, null);
        Assert.Equal(expected, RecognitionFingerprint.Compute(baseline, null));

        var variants = new (string Why, TocHierarchyOptions Options, string? Pattern)[]
        {
            ("数字章节开关", baseline with { RecognizeNumericHeadings = true }, null),
            ("数字章节最少正文行数", baseline with { NumericHeadingMinimumBodyLines = 9 }, null),
            ("数字章节正则", baseline with { NumericHeadingPattern = @"^\d+$" }, null),
            // 注意不能写 "久=九"：那正是默认值，改了等于没改，这条变体会假通过。
            ("编号纠错表", baseline with { HeadingNumberCorrections = "久=九,己=已" }, null),
            ("层级开关", baseline with { Enabled = false }, null),
            ("一级正则", baseline with { Level1Pattern = "^卷" }, null),
            ("二级正则", baseline with { Level2Pattern = "^章" }, null),
            ("三级正则", baseline with { Level3Pattern = "^节" }, null),
            ("目录页开关", baseline with { IncludeHtmlTocPage = true }, null),
            ("章节导航开关", baseline with { IncludeChapterTopNavigation = true }, null),
            ("普通章节正则", baseline, "^第.+章"),
        };
        foreach (var (why, options, pattern) in variants)
        {
            var actual = RecognitionFingerprint.Compute(options, pattern);
            Assert.True(expected != actual,
                $"{why}变了但识别指纹没变：base={expected[..12]} variant={actual[..12]}"
                + $"（RecognizeNumericHeadings={options.RecognizeNumericHeadings}"
                + $" MinimumBodyLines={options.NumericHeadingMinimumBodyLines}"
                + $" Enabled={options.Enabled}）");
        }
    }

    /// <summary>
    /// 拼接字段时不做长度前缀，就能靠移动分隔符造出碰撞。指纹要能区分它们。
    /// </summary>
    [Fact]
    public void Field_boundaries_cannot_be_moved_to_forge_the_same_fingerprint()
    {
        var left = ChapterTreeFingerprint.Compute([Entry("a|b", 1, 1, (2, 5))]);
        var right = ChapterTreeFingerprint.Compute([Entry("a", 1, 1, (2, 5)) with { RecognitionSource = "b" }]);
        Assert.NotEqual(left, right);
    }

    // ── 校验器 ──────────────────────────────────────────────────

    private static RepairBaseVersion Capture(string sha, ChapterTreeEntry[] entries,
        TocHierarchyOptions? recognition = null, string? pattern = null) =>
        RepairBaseVersionFactory.Capture(sha, entries, recognition ?? Recognition(), pattern);

    [Fact]
    public void An_unchanged_book_keeps_its_plan_valid()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 5)) };
        var version = Capture("AAAA", entries);

        var check = RepairPlanValidator.Validate(version, "AAAA", entries, Recognition(), null);

        Assert.True(check.IsValid);
        Assert.Equal(RepairVersionDrift.None, check.Drift);
    }

    [Fact]
    public void A_changed_source_is_reported_as_a_source_change()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 5)) };
        var check = RepairPlanValidator.Validate(Capture("AAAA", entries), "BBBB", entries, Recognition(), null);

        Assert.False(check.IsValid);
        Assert.Equal(RepairVersionDrift.Source, check.Drift);
        Assert.Contains("TXT 的内容变了", check.Message);
    }

    [Fact]
    public void A_changed_tree_is_reported_as_a_tree_change()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 5)) };
        var edited = new[] { Entry("第一章 改", 1, 1, (2, 5)) };
        var check = RepairPlanValidator.Validate(Capture("AAAA", entries), "AAAA", edited, Recognition(), null);

        Assert.False(check.IsValid);
        Assert.Equal(RepairVersionDrift.Tree, check.Drift);
        Assert.Contains("章节树变了", check.Message);
    }

    [Fact]
    public void Changed_rules_are_reported_as_a_recognition_change()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 5)) };
        var check = RepairPlanValidator.Validate(Capture("AAAA", entries), "AAAA", entries,
            Recognition() with { RecognizeNumericHeadings = true }, null);

        Assert.False(check.IsValid);
        Assert.Equal(RepairVersionDrift.Recognition, check.Drift);
        Assert.Contains("识别规则变了", check.Message);
    }

    /// <summary>三样一起变时要全部说出来，只说一样会让人去修错的地方。</summary>
    [Fact]
    public void Several_changes_are_all_named()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 5)) };
        var check = RepairPlanValidator.Validate(Capture("AAAA", entries), "BBBB",
            new[] { Entry("第一章 改", 1, 1, (2, 5)) }, Recognition() with { Enabled = false }, null);

        Assert.Equal(RepairVersionDrift.Several, check.Drift);
        Assert.Contains("TXT 的内容变了", check.Message);
        Assert.Contains("章节树变了", check.Message);
        Assert.Contains("识别规则变了", check.Message);
    }

    /// <summary>没有任何"仍然继续"的路径：有效期只有一个答案。</summary>
    [Fact]
    public void There_is_no_way_to_carry_on_with_a_stale_plan()
    {
        var entries = new[] { Entry("第一章", 1, 1, (2, 5)) };
        var check = RepairPlanValidator.Validate(Capture("AAAA", entries), "BBBB", entries, Recognition(), null);
        Assert.False(check.IsValid);
        Assert.DoesNotContain("继续", check.Message);
    }
}
