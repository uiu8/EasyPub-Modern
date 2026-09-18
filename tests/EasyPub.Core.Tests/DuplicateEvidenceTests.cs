using System.IO;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 重复判断的五个证据夹具 —— **Step 0：只写测试，不改业务代码。**
///
/// 它们锁的是文档 §2.2.1 的四维模型：<c>Recommended</c> 只有在
/// 「身份达到自动门槛 ＋ keep target 已确定 ＋ 待删 occurrence 结构安全」三者同时成立时才该为 true。
///
/// 当前 <c>SelfRepairPlanner</c> 只做 <c>GroupBy(entry.Title)</c>，然后"保留第一份、其余全部
/// RemoveDuplicate 且 Recommended = true"。所以本文件里 **A / B / C / D1 预期失败，D2 预期通过**。
///
/// **正文长度不是随手写的**：<c>ChapterContentDuplicates.Compare</c> 对最短正文 &lt; 20 字符
/// 直接返回 <c>null</c>（那道门槛专门避免"很短的模板文字"被当成强重复证据）。夹具的正文必须
/// 越过它，否则测的是"太短无法比较"，而不是"正文不同"。
/// </summary>
public class DuplicateEvidenceTests
{
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
    /// 当前实现按标题分组，会把它们分到同一组并判为重复。
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
    /// 当前实现把"留第一份"（`FirstWins`）当成证据，所以这条会失败。
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
