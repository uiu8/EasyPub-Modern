using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 重复判断的证据回归与短正文反例。
///
/// 它们锁的是文档 §2.2.1 的四维模型：<c>Recommended</c> 只有在
/// 「身份达到自动门槛 ＋ keep target 已确定 ＋ 待删 occurrence 结构安全」三者同时成立时才该为 true。
///
/// 保护现有正文证据及结构安全规则，避免回退到按同名直接删除的旧行为。
///
/// **正文长度不是随手写的**：<c>ChapterContentDuplicates.Compare</c> 对最短正文 &lt; 20 字符
/// 直接返回 <c>null</c>（那道门槛专门避免"很短的模板文字"被当成强重复证据）。夹具的正文必须
/// 越过它，否则测的是"太短无法比较"，而不是"正文不同"。
/// </summary>
public class DuplicateEvidenceTests
{
    [Theory]
    [InlineData("春眠不觉晓，处处闻啼鸟。", "夜来风雨声，花落知多少。")]
    [InlineData("未完待续", "未完待续")]
    [InlineData("他在纸上写下：第一章 重逢，然后离开。", "她走进院子，看见树下放着昨日的那封信。")]
    public async Task Short_poems_templates_and_title_mentions_are_not_empty_duplicates(string first, string second)
    {
        var document = await BookAsync($"第一章 重逢\n{first}\n第一章 重逢\n{second}\n");
        document = document.WithEntries([
            new("first", "第一章 重逢", 1, true, 1, [new(2, 2)]),
            new("second", "第一章 重逢", 1, true, 3, [new(4, 4)])]);
        Assert.DoesNotContain(SelfRepairPlanner.Build(document).Actions,
            a => a.Kind == ReferenceActionKind.RemoveDuplicate);
    }

    [Fact]
    public async Task Confirmed_full_copy_is_offered_once_and_only_removed_after_selection()
    {
        var document = await BookAsync($"第一章 重逢\n{BodyA}\n第一章 重逢\n{BodyA}\n第二章 后续\n{BodyB}\n");
        var plan = SelfRepairPlanner.Build(document);
        var action = Assert.Single(plan.Actions, a => a.Kind == ReferenceActionKind.RemoveDuplicate);
        Assert.False(action.Recommended);
        var kept = ReferencePlanner.Apply(document, document.Entries, plan, [action], true, false, new List<int>());
        Assert.Single(kept.SelectMany(document.GetSourceLines), l => l.Text == BodyA);
        Assert.Single(kept.SelectMany(document.GetSourceLines), l => l.Text == BodyB);
    }
    [Theory]
    [InlineData("\n")]
    [InlineData("\n\n")]
    [InlineData("\n　 \t\n")]
    public async Task Blank_lines_between_repeated_titles_do_not_count_as_body(string separator)
    {
        var document = await BookAsync($"第一章 重逢{separator}第一章 重逢\n{BodyA}\n第二章 后续\n{BodyB}\n");
        // Custom recognition rules can retain both adjacent headings; exercise that input tree too.
        var second = 1 + separator.Count(c => c == '\n');
        document = document.WithEntries([
            new("empty", "第一章 重逢", 1, true, 1, second > 2 ? [new(2, second - 1)] : []),
            new("body", "第一章 重逢", 1, true, second, [new(second + 1, second + 1)]),
            new("next", "第二章 后续", 1, true, second + 2, [new(second + 3, document.LineCount)])]);
        var plan = SelfRepairPlanner.Build(document);
        var action = Assert.Single(plan.Actions, a => a.Kind == ReferenceActionKind.RemoveDuplicate);
        Assert.True(action.Recommended);
        var located = Assert.Single(plan.Location.Chapters, c => ReferenceEquals(c.Reference, action.Reference));
        Assert.True(located.Line > 1);
        var dropped = new List<int>();
        var rebuilt = ReferencePlanner.Apply(document, document.Entries, plan, [action], true, false, dropped);
        Assert.Contains(rebuilt.SelectMany(document.GetSourceLines), line => line.Text == BodyA);
        Assert.Contains(rebuilt.SelectMany(document.GetSourceLines), line => line.Text == BodyB);
        Assert.All(dropped, line => Assert.True(line == 1 || string.IsNullOrWhiteSpace(document.SourceLine(line)?.Text)));
    }

    [Fact]
    public async Task An_identical_pair_does_not_make_a_third_different_body_removable()
    {
        var duplicates = await DuplicatesAsync($"第一章 重逢\n{BodyA}\n第一章 重逢\n{BodyA}\n第一章 重逢\n{BodyB}\n");
        Assert.Empty(duplicates);
    }

    [Fact]
    public async Task Same_number_with_different_titles_and_bodies_is_not_a_duplicate_action()
    {
        var duplicates = await DuplicatesAsync($"第一章 起点\n{BodyA}\n第一章 归途\n{BodyB}\n");
        Assert.Empty(duplicates);
    }

    /// <summary>够长的正文，让比较器愿意真正做相似度判断。</summary>
    private const string BodyA =
        "这一段正文刻意写得足够长，长到比较器愿意为它计算相似度，而不是因为太短就直接跳过。";

    /// <summary>同样够长，但与 <see cref="BodyA"/> 毫无重叠的第二段正文。</summary>
    private const string BodyB =
        "另一段同样够长的正文，可是内容与前者完全不同，因此构不成任何可以据以判定的重复证据。";

    private static async Task<ChapterTreeDocument> BookAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-dupe-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        return await ChapterTreeDocument.LoadAsync(path);
    }

    private static async Task<ReferenceAction[]> DuplicatesAsync(string text)
    {
        var document = await BookAsync(text);
        var plan = SelfRepairPlanner.Build(document);
        return plan.Actions.Where(action => action.Kind == ReferenceActionKind.RemoveDuplicate).ToArray();
    }

    /// <summary>
    /// 夹具 A —— 两个**显式不同的卷**里各有一个同名章，正文也不一样。
    ///
    /// 「第一卷 第一章 缘起」与「第二卷 第一章 缘起」是两章，不是副本。
    /// 不允许跨显式卷按同名合并。
    /// </summary>
    [Fact]
    public async Task A_same_title_in_two_explicit_volumes_is_not_auto_removed()
    {
        var duplicates = await DuplicatesAsync(
            $"第一卷 甲卷\n第一章 缘起\n{BodyA}\n" +
            $"第二卷 乙卷\n第一章 缘起\n{BodyB}\n");

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// 夹具 B —— **没有卷标题行、只有章号重启**，两个同名章，正文不同。
    ///
    /// 这是六卷样书的形状：原文里一个卷标题都没有。所以把 Volume / Parent 加进分组键
    /// **保护不了这种书** —— 它会退化成 `Level + Title`，跨卷同名照样被合并。
    /// 能拦住它的是**正文证据**，不是结构键。
    /// </summary>
    [Fact]
    public async Task B_same_title_across_a_numbering_restart_is_not_auto_removed()
    {
        var duplicates = await DuplicatesAsync(
            $"第一章 缘起\n{BodyA}\n第二章 别的\n{BodyB}\n" +
            $"第一章 缘起\n{BodyB}\n第二章 又别的\n{BodyA}\n");

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// 夹具 C —— 同一层级、同标题，但**正文不同**。
    ///
    /// 现实小说里合法存在（重复的「序章」「幕间」）。可以有一个 finding 让用户看一眼，
    /// 但**不得**默认推荐删除。
    /// </summary>
    [Fact]
    public async Task C_same_title_with_different_bodies_is_not_auto_recommended()
    {
        var duplicates = await DuplicatesAsync(
            $"第一章 重逢\n{BodyA}\n第一章 重逢\n{BodyB}\n");

        Assert.DoesNotContain(duplicates, action => action.Recommended);
    }

    /// <summary>
    /// 夹具 D1 —— 同标题、**正文完全相同**，但**没有可靠的 keep-target 证据**。
    ///
    /// 这一条是本组里最重要的：它证明"**正文完全相同**"**不足以**推荐删除。
    /// 两份内容等价，却没有任何证据说明该留哪一份 —— 所以 <c>Recommended</c> 必须是 false，
    /// 由用户在对比界面里自己决定。
    ///
    /// 不能把"留第一份"（`FirstWins`）当成自动删除的证据。
    /// </summary>
    [Fact]
    public async Task D1_same_title_with_identical_bodies_but_no_keep_target_is_not_auto_recommended()
    {
        var duplicates = await DuplicatesAsync(
            $"第一章 重逢\n{BodyA}\n第一章 重逢\n{BodyA}\n");

        Assert.DoesNotContain(duplicates, action => action.Recommended);
    }

    /// <summary>
    /// 夹具 D2 —— 同名组里**恰好一份有独立正文**，另一份是空章。
    ///
    /// 这是唯一一个**现在就应该是绿的**夹具，也是本轮唯一被承认的 keep-target 证据：
    /// 留哪一份不是偏好，而是**结构事实** —— 空章本来就没有内容可留。
    ///
    /// 若它在改完 0-A 之后变红，说明改动过度了。
    /// </summary>
    [Fact]
    public async Task D2_same_title_with_a_clear_keep_target_is_auto_recommended()
    {
        var duplicates = await DuplicatesAsync(
            $"第一章 重逢\n{BodyA}\n第一章 重逢\n第二章 继续\n{BodyB}\n");

        Assert.Contains(duplicates, action => action.Recommended);
    }
}
