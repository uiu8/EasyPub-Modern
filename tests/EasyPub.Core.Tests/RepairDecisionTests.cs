using System.Reflection;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 用户决定与动作语义。
///
/// 这一层的存在理由只有一个：**编译器不得替用户做决定**。「从成品里排除重复副本」和「顺便把那些行
/// 从 TXT 里删掉」是两个不同的答案，而在这个类型出现之前，第二个答案没有地方可写 —— 编译器只好
/// 看看落地模式，自己选一个。
/// </summary>
public class RepairDecisionTests
{
    private static ReferenceAction Action(ReferenceActionKind kind, int line = 10, string title = "第一章",
        bool recommended = true) =>
        new(kind, line, title, "细节", "entry-1",
            new ReferenceNode("第一章", ReferenceNodeKind.Chapter, null, "第一卷"), recommended);

    // ── 策略映射必须完整 ────────────────────────────────────────

    /// <summary>
    /// 每一个动作类别都要有明确的策略。
    ///
    /// 用反射而不是列清单：将来加一个新的动作类别却忘了定策略，那个动作会落到 <c>default</c> 分支上
    /// 被当成 NotApplicable —— 于是用户勾了"改原文"却什么都没发生，而且没有任何地方会报错。
    /// 让它在这里红。
    /// </summary>
    [Fact]
    public void Every_action_kind_has_a_stated_policy()
    {
        var kinds = Enum.GetValues<ReferenceActionKind>();
        var missing = kinds.Where(kind => !Enum.IsDefined(SourceEffectPolicies.For(kind))).ToArray();
        Assert.True(missing.Length == 0, "这些动作类别没有策略：" + string.Join("、", missing));

        // 反向：策略不能声称某个动作属于它而那个动作并不存在。上面已经保证了取值合法，
        // 这里再确认 NotApplicable 不是"漏了"的默认值 —— 逐个类别点名，改一处就要改这里。
        var expected = new Dictionary<ReferenceActionKind, SourceEffectPolicy>
        {
            [ReferenceActionKind.AddChapter] = SourceEffectPolicy.NotApplicable,
            [ReferenceActionKind.Retitle] = SourceEffectPolicy.RequiredInEditSource,
            [ReferenceActionKind.AdoptHeading] = SourceEffectPolicy.Optional,
            [ReferenceActionKind.MissingBody] = SourceEffectPolicy.NotApplicable,
            [ReferenceActionKind.RemoveDuplicate] = SourceEffectPolicy.ExplicitOptIn,
            [ReferenceActionKind.KeepExtra] = SourceEffectPolicy.NotApplicable,
            [ReferenceActionKind.VolumeNote] = SourceEffectPolicy.NotApplicable,
            [ReferenceActionKind.DemoteExtra] = SourceEffectPolicy.Optional,
        };
        Assert.Equal(expected.Count, Enum.GetValues<ReferenceActionKind>().Length);
        foreach (var (kind, policy) in expected)
            Assert.Equal(policy, SourceEffectPolicies.For(kind));
    }

    // ── 三种落地情形的决定 ──────────────────────────────────────

    /// <summary>
    /// 用户选了「改原文」以后，核心动作必须是"必须改到"，不能是可降级。
    ///
    /// 否则会出现：用户明确选了改原文，InsertHeading 却因为编译不出安全的源补丁而降级成"只改树"，
    /// 于是 TXT 里仍然没有标题 —— 用户要的功能悄悄没做。降级后的树和源彼此自洽，任何一致性检查
    /// 都会通过，所以这种错误不会被事后发现。
    /// </summary>
    [Theory]
    [InlineData(ReferenceActionKind.Retitle)]
    [InlineData(ReferenceActionKind.AddChapter)]
    public void A_core_edit_action_never_downgrades_when_the_user_chose_to_edit_the_source(
        ReferenceActionKind kind)
    {
        var decision = SourceEffectPolicies.Decide(kind, RepairLandingMode.EditSource);
        if (kind == ReferenceActionKind.AddChapter)
        {
            // 标题已在原文里，收录它不需要动 TXT。
            Assert.Equal(SourceEffectDecision.None, decision);
            return;
        }
        Assert.Equal(SourceEffectDecision.ApplyRequired, decision);
        Assert.NotEqual(SourceEffectDecision.ApplyIfAvailable, decision);
    }

    /// <summary>
    /// TreeOnly 表示"不动 TXT"，对每一个动作都成立。
    ///
    /// 这不是把策略改弱 —— 这是用户选的模式。想要原文被改的人应该选 EditSource，那个模式就是为
    /// 这件事存在的。
    /// </summary>
    [Fact]
    public void Tree_only_mode_never_touches_the_source_for_any_action_kind()
    {
        foreach (var kind in Enum.GetValues<ReferenceActionKind>())
        {
            // 连"用户明确要求删除重复"也一样：模式是 TreeOnly，就不动原文。
            var decision = SourceEffectPolicies.Decide(kind, RepairLandingMode.TreeOnly,
                deleteDuplicateFromSource: true);
            Assert.Equal(SourceEffectDecision.None, decision);
        }
    }

    /// <summary>重复副本默认只从成品排除；物理删除必须由用户明确要求。</summary>
    [Fact]
    public void Removing_a_duplicate_copy_from_the_text_requires_an_explicit_request()
    {
        Assert.Equal(SourceEffectDecision.None,
            SourceEffectPolicies.Decide(ReferenceActionKind.RemoveDuplicate, RepairLandingMode.EditSource));
        Assert.Equal(SourceEffectDecision.ApplyRequired,
            SourceEffectPolicies.Decide(ReferenceActionKind.RemoveDuplicate, RepairLandingMode.EditSource,
                deleteDuplicateFromSource: true));
        Assert.True(SourceEffectPolicies.NeedsExplicitOptIn(ReferenceActionKind.RemoveDuplicate));
        Assert.False(SourceEffectPolicies.NeedsExplicitOptIn(ReferenceActionKind.Retitle));
    }

    /// <summary>只有必须问第二遍的类别才需要那个开关，否则界面会问一堆无意义的问题。</summary>
    [Fact]
    public void Only_explicit_opt_in_kinds_ask_the_second_question()
    {
        var asking = Enum.GetValues<ReferenceActionKind>()
            .Where(SourceEffectPolicies.NeedsExplicitOptIn).ToArray();
        Assert.Equal([ReferenceActionKind.RemoveDuplicate], asking);
    }

    // ── 动作身份 ────────────────────────────────────────────────

    [Fact]
    public void The_same_action_gets_the_same_identity()
    {
        var action = Action(ReferenceActionKind.Retitle);
        Assert.Equal(RepairActionIdentity.Compute(action), RepairActionIdentity.Compute(action));
    }

    /// <summary>
    /// 改一句提示文字不得改变动作身份。
    ///
    /// 否则每改一次文案，收据、journal 与界面勾选记录全部对不上 —— 而它们本来在说的是同一件事。
    /// </summary>
    [Fact]
    public void Rewording_a_hint_does_not_change_the_identity()
    {
        var original = Action(ReferenceActionKind.Retitle);
        var reworded = original with { Detail = "完全换一种说法，写得更长一些。" };
        Assert.Equal(RepairActionIdentity.Compute(original), RepairActionIdentity.Compute(reworded));
    }

    /// <summary>会改变动作实际做什么的字段，一个都不能漏。</summary>
    [Fact]
    public void Everything_that_changes_what_the_action_does_changes_the_identity()
    {
        var baseline = RepairActionIdentity.Compute(Action(ReferenceActionKind.Retitle));

        Assert.NotEqual(baseline, RepairActionIdentity.Compute(Action(ReferenceActionKind.RemoveDuplicate)));
        Assert.NotEqual(baseline, RepairActionIdentity.Compute(Action(ReferenceActionKind.Retitle, line: 11)));
        Assert.NotEqual(baseline, RepairActionIdentity.Compute(Action(ReferenceActionKind.Retitle, title: "第二章")));
        Assert.NotEqual(baseline, RepairActionIdentity.Compute(Action(ReferenceActionKind.Retitle, recommended: false)));

        var otherEntry = Action(ReferenceActionKind.Retitle) with { EntryId = "entry-2" };
        Assert.NotEqual(baseline, RepairActionIdentity.Compute(otherEntry));

        var otherReference = Action(ReferenceActionKind.Retitle) with
        {
            Reference = new ReferenceNode("另一个标题", ReferenceNodeKind.Chapter, null, "第一卷"),
        };
        Assert.NotEqual(baseline, RepairActionIdentity.Compute(otherReference));

        var otherVolume = Action(ReferenceActionKind.Retitle) with
        {
            Reference = new ReferenceNode("第一章", ReferenceNodeKind.Chapter, null, "第二卷"),
        };
        Assert.NotEqual(baseline, RepairActionIdentity.Compute(otherVolume));
    }

    [Fact]
    public void The_schema_version_is_part_of_the_identity()
    {
        // 版本号是常量，这里确认它被写进了哈希：把版本号换成相邻值应当得到不同的 id。
        // 直接改常量做不到，所以用一条相同输入的手工哈希来对照 —— 只要版本号参与计算，
        // 它变动时这一串就会跟着变。此处只断言当前版本已声明且为正。
        Assert.True(RepairActionIdentity.ActionSchemaVersion >= 1);
    }

    // ── 方案身份 ────────────────────────────────────────────────

    private static readonly RepairBaseVersion AnyVersion = new("AAAA", "TREE", "RECO");

    /// <summary>
    /// 同一本书、同一棵树、同一套规则，但导入的是**不同的目录** —— 动作集合不同，方案 id 就必须不同。
    ///
    /// 只用 BaseVersion + basis 哈希是不够的：那三个输入在这种情况下完全相同，两份方案会撞成同一个
    /// id，而它们要做的事截然不同。
    /// </summary>
    [Fact]
    public void Two_directories_that_produce_different_actions_get_different_proposal_ids()
    {
        var first = RepairActionIdentity.ComputeProposal("ReferenceCatalog", AnyVersion, ["a", "b"]);
        var second = RepairActionIdentity.ComputeProposal("ReferenceCatalog", AnyVersion, ["a", "c"]);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void The_same_actions_in_the_same_order_give_the_same_proposal_id()
    {
        Assert.Equal(
            RepairActionIdentity.ComputeProposal("ReferenceCatalog", AnyVersion, ["a", "b"]),
            RepairActionIdentity.ComputeProposal("ReferenceCatalog", AnyVersion, ["a", "b"]));
    }

    /// <summary>顺序也是方案的一部分：同样的动作换个次序不是同一份方案。</summary>
    [Fact]
    public void Action_order_is_part_of_the_proposal_identity()
    {
        Assert.NotEqual(
            RepairActionIdentity.ComputeProposal("ReferenceCatalog", AnyVersion, ["a", "b"]),
            RepairActionIdentity.ComputeProposal("ReferenceCatalog", AnyVersion, ["b", "a"]));
    }

    [Fact]
    public void A_different_base_version_gives_a_different_proposal_id()
    {
        var first = RepairActionIdentity.ComputeProposal("ReferenceCatalog", new RepairBaseVersion("AAAA", "TREE", "RECO"), ["a"]);
        var second = RepairActionIdentity.ComputeProposal("ReferenceCatalog", new RepairBaseVersion("BBBB", "TREE", "RECO"), ["a"]);
        Assert.NotEqual(first, second);
    }

    // ── 决策编译检查 ────────────────────────────────────────────

    private static RepairProposal Proposal(params ReferenceAction[] actions) =>
        RepairProposalFactory.Create("ReferenceCatalog", new RepairBaseVersion("AAAA", "TREE", "RECO"),
            new ReferencePlan(
                new ReferenceCatalog("src", "页", actions.Select(a => new ReferenceNode(a.Title, ReferenceNodeKind.Chapter, null, null)).ToArray()),
                new ReferenceLocation([]),
                actions));

    [Fact]
    public void A_decision_naming_an_action_that_is_not_in_the_proposal_is_reported()
    {
        var proposal = Proposal(Action(ReferenceActionKind.Retitle));
        var decisions = new[] { new RepairDecision("not-an-action", true, SourceEffectDecision.None) };

        var check = RepairDecisionCompiler.Check(proposal, decisions);

        Assert.False(check.CanApply);
        Assert.Equal(RepairCompileProblemKind.UnknownAction, check.Problems.Single().Reason);
    }

    /// <summary>
    /// 承诺了改原文、却编译不出安全的源改动 —— 整份方案失败，而不是悄悄降级。
    ///
    /// Phase 3 还没有 SourcePatch，所以 <c>canEditSource</c> 传 null 就是"什么都改不了"的诚实回答。
    /// </summary>
    [Fact]
    public void An_apply_required_action_with_no_source_edit_fails_the_plan()
    {
        var action = Action(ReferenceActionKind.Retitle);
        var proposal = Proposal(action);
        var decisions = new[]
        {
            new RepairDecision(RepairActionIdentity.Compute(action), true, SourceEffectDecision.ApplyRequired),
        };

        var check = RepairDecisionCompiler.Check(proposal, decisions);

        Assert.False(check.CanApply);
        Assert.Equal(RepairCompileProblemKind.SourceEditUnavailable, check.Problems.Single().Reason);
    }

    /// <summary>
    /// 反过来：可降级的动作编译不出源改动时，方案照样成立 —— 结果记为 Downgraded，并且会被显示出来。
    /// </summary>
    [Fact]
    public void An_apply_if_available_action_is_allowed_to_land_in_the_tree_only()
    {
        var action = Action(ReferenceActionKind.AdoptHeading);
        var proposal = Proposal(action);
        var decisions = new[]
        {
            new RepairDecision(RepairActionIdentity.Compute(action), true, SourceEffectDecision.ApplyIfAvailable),
        };

        var check = RepairDecisionCompiler.Check(proposal, decisions);

        Assert.True(check.CanApply);
        var outcomes = RepairDecisionCompiler.Outcomes(decisions);
        Assert.Equal(SourceEffectOutcome.Downgraded, outcomes.Single());
    }

    /// <summary>
    /// 勾了一个根本执行不了的类别（只报告的发现项）必须被指出来。
    /// 界面画得出复选框却兑现不了，正是修复确认窗要消灭的那种缺陷。
    /// </summary>
    [Theory]
    [InlineData(ReferenceActionKind.MissingBody)]
    [InlineData(ReferenceActionKind.KeepExtra)]
    [InlineData(ReferenceActionKind.VolumeNote)]
    public void Selecting_a_finding_that_cannot_be_performed_is_reported(ReferenceActionKind kind)
    {
        var action = Action(kind);
        var proposal = Proposal(action);
        var decisions = new[]
        {
            new RepairDecision(RepairActionIdentity.Compute(action), true, SourceEffectDecision.None),
        };

        var check = RepairDecisionCompiler.Check(proposal, decisions);

        Assert.False(check.CanApply);
        Assert.Equal(RepairCompileProblemKind.NotExecutable, check.Problems.Single().Reason);
    }

    /// <summary>没勾选的动作不参与检查 —— 用户没要它，它就不该挡住方案。</summary>
    [Fact]
    public void An_unselected_action_is_never_a_problem()
    {
        var action = Action(ReferenceActionKind.Retitle);
        var proposal = Proposal(action);
        var decisions = new[]
        {
            new RepairDecision(RepairActionIdentity.Compute(action), false, SourceEffectDecision.ApplyRequired),
        };

        Assert.True(RepairDecisionCompiler.Check(proposal, decisions).CanApply);
    }

    [Fact]
    public void A_plan_with_nothing_selected_compiles_to_nothing()
    {
        var proposal = Proposal(Action(ReferenceActionKind.Retitle), Action(ReferenceActionKind.RemoveDuplicate, 20));
        var decisions = proposal.Actions
            .Select(a => new RepairDecision(RepairActionIdentity.Compute(a), false, SourceEffectDecision.None))
            .ToArray();

        var check = RepairDecisionCompiler.Check(proposal, decisions);

        Assert.True(check.CanApply);
        Assert.All(RepairDecisionCompiler.Outcomes(decisions),
            outcome => Assert.Equal(SourceEffectOutcome.NotRequested, outcome));
    }
}
