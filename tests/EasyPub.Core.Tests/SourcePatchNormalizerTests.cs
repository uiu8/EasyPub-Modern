using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 物理改动补丁的规范化。
///
/// 这八条规则全部在**写盘之前**检查，违规即拒绝 —— 而不是留给执行器用次序去绕开。"先删再插"
/// 那种做法会把错误藏起来：两个动作对同一行有不同意见时，产出的文件同时不满足它们俩，而没有任何
/// 地方会说这件事。
/// </summary>
public class SourcePatchNormalizerTests
{
    private static Func<int, string?> Text(params string[] lines) =>
        line => line >= 1 && line <= lines.Length ? lines[line - 1] : null;

    private static DeleteOriginalLine Delete(int line, string text, string action = "A1") =>
        new(line, text) { OperationId = $"del-{line}", ActionId = action };

    private static ReplaceOriginalLine Replace(int line, string oldText, string newText, string action = "A1") =>
        new(line, oldText, newText) { OperationId = $"rep-{line}", ActionId = action };

    private static InsertLineAtAnchor Insert(SourceAnchor anchor, string text, string operationId = "ins-1",
        string action = "A1") =>
        new(anchor, text) { OperationId = operationId, ActionId = action };

    private static readonly string[] Book = ["第一章 开始", "正文一", "第二章 继续", "正文二", "第三章 收尾"];

    private static SourcePatchValidation Normalize(params SourceOperation[] operations) =>
        SourcePatchNormalizer.Normalize("actions", "SHA", operations, Book.Length, Text(Book));

    // ── 正常路径 ────────────────────────────────────────────────

    [Fact]
    public void A_patch_with_one_of_each_operation_is_accepted()
    {
        var result = Normalize(
            Delete(4, "正文二"),
            Replace(3, "第二章 继续", "第二章 续"),
            Insert(new AfterOriginalLine(1), "插入的一行"));

        Assert.True(result.IsValid, result.Describe());
        Assert.Equal(3, result.Patch!.Operations.Count);
    }

    /// <summary>渲染是单遍的，所以操作顺序不影响结果；次序只为可读。</summary>
    [Fact]
    public void The_order_operations_are_supplied_in_does_not_change_the_patch()
    {
        var first = Normalize(
            Delete(4, "正文二"),
            Replace(1, "第一章 开始", "第一章 起"),
            Insert(new AfterOriginalLine(2), "插"));
        var second = Normalize(
            Insert(new AfterOriginalLine(2), "插"),
            Replace(1, "第一章 开始", "第一章 起"),
            Delete(4, "正文二"));

        Assert.True(first.IsValid && second.IsValid);
        Assert.Equal(
            first.Patch!.Operations.Select(operation => operation.OperationId),
            second.Patch!.Operations.Select(operation => operation.OperationId));
    }

    // ── 规则 1、2、3、8：同一行的占用 ────────────────────────────

    [Fact]
    public void Deleting_and_replacing_the_same_line_is_refused()
    {
        var result = Normalize(Delete(3, "第二章 继续"), Replace(3, "第二章 继续", "第二章 续"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Rule == "同一行被多个操作占用");
    }

    [Fact]
    public void Two_replaces_on_one_line_are_refused()
    {
        var result = Normalize(
            Replace(3, "第二章 继续", "甲", action: "A1"),
            Replace(3, "第二章 继续", "乙", action: "A2"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Rule == "同一行被多个操作占用");
    }

    [Fact]
    public void Two_deletes_on_one_line_are_refused()
    {
        var result = Normalize(
            Delete(4, "正文二", action: "A1"),
            Delete(4, "正文二", action: "A2"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Rule == "同一行被多个操作占用");
    }

    /// <summary>被多个动作占用与同一动作重复要求，是两种不同的错，消息要能分开。</summary>
    [Fact]
    public void The_message_says_whether_two_actions_or_one_action_disagreed()
    {
        var twoActions = Normalize(Delete(4, "正文二", "A1"), Replace(4, "正文二", "改", "A2"));
        Assert.Contains("2 个动作", string.Join(" ", twoActions.Problems.Select(p => p.Detail)));

        var oneAction = Normalize(Delete(4, "正文二", "A1"), Delete(4, "正文二", "A1"));
        Assert.Contains("同一个动作", string.Join(" ", oneAction.Problems.Select(p => p.Detail)));
    }

    // ── 规则 5：前置条件 ────────────────────────────────────────

    /// <summary>
    /// 这一行已经不是当时的内容时必须拒绝，而不是覆盖它。
    /// 定位一旦出错，这是唯一能挡住"把别人的正文改掉"的东西。
    /// </summary>
    [Fact]
    public void A_line_whose_text_changed_is_refused_rather_than_overwritten()
    {
        var result = Normalize(Replace(3, "完全不同的一句话", "第二章 续"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Rule == "前置条件不符");
    }

    [Fact]
    public void A_delete_whose_text_changed_is_refused()
    {
        var result = Normalize(Delete(4, "不是这一行的内容"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Rule == "前置条件不符");
    }

    // ── 规则 6：行号范围 ────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    public void A_line_outside_the_file_is_refused(int line)
    {
        var result = Normalize(Delete(line, "随便什么"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Rule == "行号越界");
    }

    // ── 规则 7：锚点落在被删除的行上 ────────────────────────────

    /// <summary>
    /// 「把副本删掉，再往它原来的位置插一个标题」读起来通顺，实际上不成立：那个锚点不存在了。
    /// </summary>
    [Fact]
    public void An_anchor_on_a_deleted_line_is_refused()
    {
        var result = Normalize(
            Delete(4, "正文二"),
            Insert(new BeforeOriginalLine(4), "新标题"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Rule == "锚点落在被删除的行上");
    }

    [Fact]
    public void An_empty_insert_is_refused_because_nothing_accounts_for_a_blank_line()
    {
        var result = Normalize(Insert(new AfterOriginalLine(1), ""));

        Assert.False(result.IsValid);
        Assert.Contains(result.Problems, problem => problem.Rule == "插入内容为空");
    }

    // ── 规则 4：同锚点插入的稳定次序 ────────────────────────────

    /// <summary>
    /// 同一个锚点上的多个插入必须有确定次序，否则渲染出的字节取决于调用者凑巧按什么顺序建列表。
    /// </summary>
    [Fact]
    public void Inserts_sharing_an_anchor_come_out_in_a_fixed_order()
    {
        var first = Normalize(
            Insert(new AfterOriginalLine(1), "甲", "op-b"),
            Insert(new AfterOriginalLine(1), "乙", "op-a"));
        var second = Normalize(
            Insert(new AfterOriginalLine(1), "乙", "op-a"),
            Insert(new AfterOriginalLine(1), "甲", "op-b"));

        Assert.True(first.IsValid && second.IsValid);
        // 次序由 OperationId 决定，与传入顺序无关。
        Assert.Equal(["op-a", "op-b"], first.Patch!.Operations.Select(o => o.OperationId));
        Assert.Equal(["op-a", "op-b"], second.Patch!.Operations.Select(o => o.OperationId));
    }

    [Fact]
    public void An_ordinal_wins_over_the_operation_id()
    {
        // 「先」的 ordinal 是 0，「后」的是 1：ordinal 小的排在前面，尽管它的操作 id 更大。
        var result = Normalize(
            Insert(new AfterOriginalLine(1, 1), "后", "op-a"),
            Insert(new AfterOriginalLine(1, 0), "先", "op-z"));

        Assert.True(result.IsValid, result.Describe());
        Assert.Equal(["op-z", "op-a"], result.Patch!.Operations.Select(o => o.OperationId));
    }

    /// <summary>文件末尾的锚点不需要行号 —— 空文件也必须能表达。</summary>
    [Fact]
    public void An_end_of_file_anchor_works_on_an_empty_file()
    {
        var result = SourcePatchNormalizer.Normalize("actions", "SHA",
            [Insert(new EndOfFileAnchor(), "第一行")], 0, _ => null);

        Assert.True(result.IsValid, result.Describe());
        Assert.Single(result.Patch!.Operations);
    }

    /// <summary>多个问题一起报，而不是只报第一个 —— 修一个再发现下一个最浪费时间。</summary>
    [Fact]
    public void Every_problem_is_reported_not_just_the_first()
    {
        var result = Normalize(
            Delete(999, "越界"),
            Replace(3, "错的前置条件", "改"),
            Insert(new AfterOriginalLine(4), "") with { Anchor = new AfterOriginalLine(4) });

        Assert.False(result.IsValid);
        Assert.True(result.Problems.Count >= 2, "只报了一个问题：" + result.Describe());
    }
}
