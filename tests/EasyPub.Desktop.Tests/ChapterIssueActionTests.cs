using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// 问题卡是"一条提醒"变成"一个动作"的唯一地方，所以每一步都要落在能真正解决这条问题的动作上。
///
/// <para>触发这些测试的原案：一条跳章提醒被给了「编辑成品标题…」——一个看起来合理、
/// 却不可能把缺掉的章节变回来的按钮。这些测试以前只锁 <c>ForGeneric</c> 拼出来的字符串；
/// 现在它们锁**整条链路**（<see cref="IssueResolutionPolicy"/> 算动作 → surface 说自己的话），
/// 因为错配恰恰发生在"文案"与"执行"分头判断的地方。</para>
/// </summary>
public class ChapterIssueActionTests
{
    private static ConversionPreflightIssue Issue(string code) =>
        new("/tmp/book.txt", PreflightSeverity.Warning, code, "message", PreflightTargetKind.Chapters, 1);

    private static ChapterReviewGroup Group(string code, params int[] lines) =>
        new(Issue(code), IssueCategory.ForCode(code) ?? ReviewCategories.Other, [], lines, []);

    private static ResolutionAction? Action(
        string code,
        IssueResolutionFacts? facts = null,
        ChapterRepairContextSnapshot? context = null) =>
        IssueResolutionPolicy.Evaluate(Group(code), context ?? ChapterRepairContextSnapshot.Unknown, facts).Recommended;

    private static string WorkbenchCta(string code, IssueResolutionFacts? facts = null) =>
        ChapterIssueAction.CtaFor(Action(code, facts), "第三百章 归来");

    [Fact]
    public void A_missing_chapter_never_offers_a_title_edit()
    {
        // 漏识别只有一条出路：把原文里的一行变成章节。
        Assert.Equal("从选中行建立章节", WorkbenchCta("chapter_unrecognized", new IssueResolutionFacts(CanSplitSelectedLine: true)));
        // 没有选中行时不能承诺"建立"，也不能退化成"编辑成品标题"。
        var withoutRow = WorkbenchCta("chapter_unrecognized");
        Assert.DoesNotContain("建立", withoutRow);
        Assert.DoesNotContain("编辑", withoutRow);
    }

    [Fact]
    public void A_numbering_problem_without_a_correction_offers_the_check_that_fits_it()
    {
        // 没有相邻章节可以推断编号时，唯一诚实的出路是层级处理菜单。
        foreach (var code in new[] { "chapter_number_order", "volume_number_duplicate", "chapter_level_gap" })
            Assert.Equal("核对层级处理…", WorkbenchCta(code));
    }

    [Fact]
    public void A_wrong_title_still_offers_the_corrected_one()
    {
        // 417 → 第四百零十八章 → 419 有确定答案，它必须压过通用的"核对层级"。
        Assert.Equal("修改为：第三百章 归来",
            WorkbenchCta("chapter_number_order", new IssueResolutionFacts(HasSuggestedTitle: true)));
    }

    /// <summary>
    /// 报告窗口只能跳转（点一下打开工作台并定位），所以它的每条文案都以「在工作台」起头，
    /// 承诺的是"那边有这个入口"，不是"点这里就会发生这件事"。
    ///
    /// <para>原来的文案没有这层区分：同一个重复正文的问题，报告窗口写「对比并删除重复正文」，
    /// 工作台写「对比正文，选择保留…」—— <b>对"要不要删"给出了相反的预期</b>。</para>
    /// </summary>
    [Fact]
    public void The_report_window_never_promises_an_effect_it_cannot_produce()
    {
        foreach (var code in new[] { "chapter_content_duplicate", "chapter_cross_title_duplicate", "chapter_duplicate" })
        {
            var cta = PreflightCta.For(Issue(code));
            Assert.StartsWith("在工作台", cta);
            Assert.DoesNotContain("删除", cta);
        }
        // 工作台就地执行，它可以更进一步说"清理"，但那是用户勾选后的事。
        Assert.Equal("检查并清理本组…", ChapterIssueAction.CtaFor(Action("chapter_duplicate")));
    }

    [Fact]
    public void Both_surfaces_agree_on_what_the_action_is()
    {
        // 措辞可以不同（§5.4），但必须是同一件事。这里锁的是"两边都非空且互相对应"：
        // 只要有一边退回通用兜底，就说明那个码没有被策略覆盖。
        foreach (var definition in IssueResolutionPolicy.All)
        {
            var advice = IssueResolutionPolicy.Evaluate(Group(definition.IssueCode), ChapterRepairContextSnapshot.Unknown);
            Assert.True(advice.HasChapterActions, $"{definition.IssueCode} 没有可执行的动作");
            Assert.NotEqual(ResolutionIntent.Unknown, advice.RecommendedIntent);
            Assert.False(string.IsNullOrWhiteSpace(ChapterIssueAction.CtaFor(advice.Recommended)));
            Assert.False(string.IsNullOrWhiteSpace(PreflightCta.For(Issue(definition.IssueCode))));
        }
    }

    /// <summary>
    /// 原文在程序之外被改过时，**所有基于行号的动作都要让路** —— 这是每一条提醒的共同前提，
    /// 所以它在策略的最前面，不分问题码。以前这个判断散在工作台各处的 if 里，
    /// 每加一个按钮就要记得再加一次。
    /// </summary>
    [Fact]
    public void A_source_changed_outside_the_program_makes_every_action_wait()
    {
        var context = ChapterRepairContextSnapshot.Unknown with { SourceChanged = true };
        foreach (var definition in IssueResolutionPolicy.All)
        {
            var advice = IssueResolutionPolicy.Evaluate(Group(definition.IssueCode), context);
            Assert.Equal(ResolutionIntent.ReRecognizeAsNumeric, advice.RecommendedIntent);
        }
    }

    /// <summary>
    /// 跳章这一类的推荐必须**随本地证据变**：本地找到了候选就先核对候选（不需要任何书外知识），
    /// 找不到才推参考目录。这正是设计文档 §8.6 状态组合测试的第 6、7 条。
    ///
    /// <para>第三种情况同样重要：<b>报告窗口没有文档，它无从知道本地有没有候选。</b>
    /// 此时两个出口都不预设，推荐"到原文核对" —— 它对跳章永远成立，也不暗示必须先弄一份目录。
    /// 把"没查过"当成"查过且没有"，报告窗口就会对每一条跳章都说"去获取参考目录"，
    /// 而其中不少其实是本地漏识别。</para>
    /// </summary>
    [Fact]
    public void A_gap_recommends_the_local_check_only_when_there_is_something_local_to_check()
    {
        Assert.Equal(ResolutionIntent.NavigateToSource, Action("chapter_number_gap")!.Intent);
        Assert.Equal(ResolutionIntent.RepairMissingHeadings,
            Action("chapter_number_gap", new IssueResolutionFacts(HasLocalHeadingCandidate: true))!.Intent);
        Assert.Equal(ResolutionIntent.AcquireCatalog,
            Action("chapter_number_gap", new IssueResolutionFacts(HasLocalHeadingCandidate: false))!.Intent);
    }

    /// <summary>
    /// 目录可用性改变的是**推荐哪个动作**，不只是措辞：已经选用目录时，"获取目录"不再是
    /// 用户要做的事，该做的是按它补建。这是 <see cref="ChapterRepairContextSnapshot"/>
    /// 除了显示三行事实之外的第一个真实用途。
    /// </summary>
    [Fact]
    public void An_already_selected_catalog_stops_asking_the_user_to_get_one()
    {
        var selected = ChapterRepairContextSnapshot.Unknown with
        {
            CatalogAvailability = CatalogAvailability.Selected,
            CatalogSource = "local",
            CatalogChapterCount = 480,
        };
        // 跳章：本地查过、没有候选时，推荐的动作从"去获取目录"变成"按这份目录补建"。
        Assert.Equal(ResolutionIntent.AdoptCatalogChapters,
            Action("chapter_number_gap", new IssueResolutionFacts(HasLocalHeadingCandidate: false), selected)!.Intent);
        // 无编号标题：推荐仍是专项工具，但第二条路已经不是"去获取目录"了。
        var unnumbered = IssueResolutionPolicy.Evaluate(Group("chapter_unnumbered"), selected);
        Assert.Equal(ResolutionIntent.RepairUnnumberedHeadings, unnumbered.RecommendedIntent);
        Assert.Contains(unnumbered.AvailableActions, a => a.Intent == ResolutionIntent.AdoptCatalogChapters);
        Assert.DoesNotContain(unnumbered.AvailableActions, a => a.Intent == ResolutionIntent.AcquireCatalog);
    }

    /// <summary>
    /// 推荐必须指向一个**真的在列表里**的动作。
    ///
    /// <para>这条测试来自一个真实缺陷：跳章那一条原本把推荐写死成"获取目录"，而已经选用目录时
    /// 该动作已经换成了"按目录补建" —— 于是 <see cref="ResolutionAdvice.Recommended"/> 返回 null，
    /// 界面随即悄悄退化成「处理章节…」兜底。<b>那正是这次重构要消灭的东西</b>，
    /// 所以现在它会抛异常，这条测试把每种组合都过一遍。</para>
    /// </summary>
    [Fact]
    public void Every_recommendation_points_at_an_action_that_exists()
    {
        var contexts = new[]
        {
            ChapterRepairContextSnapshot.Unknown,
            ChapterRepairContextSnapshot.Unknown with { SourceChanged = true },
            ChapterRepairContextSnapshot.Unknown with { CatalogAvailability = CatalogAvailability.Saved },
            ChapterRepairContextSnapshot.Unknown with { CatalogAvailability = CatalogAvailability.Selected },
        };
        var factSets = new[]
        {
            IssueResolutionFacts.None,
            new IssueResolutionFacts(HasLocalHeadingCandidate: true),
            new IssueResolutionFacts(HasSuggestedTitle: true),
            new IssueResolutionFacts(CanSplitSelectedLine: true),
            new IssueResolutionFacts(true, true, true),
        };
        foreach (var definition in IssueResolutionPolicy.All)
        foreach (var context in contexts)
        foreach (var facts in factSets)
        {
            // Recommended 在推荐与动作不一致时会抛 —— 不 catch，让它把出错的那一组直接指出来。
            var advice = IssueResolutionPolicy.Evaluate(Group(definition.IssueCode), context, facts);
            Assert.NotNull(advice.Recommended);
        }
    }

    /// <summary>
    /// 有几张卡片带自己的<b>专属措辞</b>（问题卡在 <c>UpdateReviewCard</c> 里提前返回，按钮文字比
    /// <see cref="ChapterIssueAction.CtaFor"/> 的通用说法更贴合）。它们<b>仍然必须跟着策略走</b> ——
    /// 这条测试把"策略推荐的动作"钉住：一旦策略改了其中任何一个，这里会红，
    /// 提醒去同步那张卡片，而不是让它悄悄说一件已经不成立的事。
    /// </summary>
    [Theory]
    [InlineData("chapter_unnumbered", ResolutionIntent.RepairUnnumberedHeadings)]
    [InlineData("chapter_repeated_sequence", ResolutionIntent.NavigateToSource)]
    [InlineData("chapter_content_duplicate", ResolutionIntent.CompareDuplicateBodies)]
    [InlineData("chapter_structure_suggested", ResolutionIntent.RecoverStructure)]
    [InlineData("numeric_chapters_suspected", ResolutionIntent.ReRecognizeAsNumeric)]
    [InlineData("chapter_heading_typo", ResolutionIntent.RepairMissingHeadings)]
    [InlineData("chapter_duplicate", ResolutionIntent.CleanDuplicateTitles)]
    [InlineData("numeric_body_group", ResolutionIntent.RestoreAsBody)]
    [InlineData("chapter_cross_title_duplicate", ResolutionIntent.CompareDuplicateBodies)]
    [InlineData("reference_chapter_absent", ResolutionIntent.AdoptCatalogChapters)]
    public void The_cards_that_have_their_own_wording_still_follow_the_policy(string code, ResolutionIntent expected) =>
        Assert.Equal(expected, Action(code)!.Intent);

    [Fact]
    public void Every_category_reachable_in_the_workbench_gets_a_label()
    {
        foreach (var category in new[]
                 {
                     ReviewCategories.Missing, ReviewCategories.Misrecognized,
                     ReviewCategories.Duplicate, ReviewCategories.Structure, ReviewCategories.Other,
                 })
        {
            var group = new ChapterReviewGroup(
                new ConversionPreflightIssue("/tmp/book.txt", PreflightSeverity.Warning, "whatever", "message",
                    PreflightTargetKind.Chapters, 1),
                category, [], [], []);
            var advice = IssueResolutionPolicy.Evaluate(group, ChapterRepairContextSnapshot.Unknown);
            // 未登记的码没有章节入口，但卡片仍要说一句人能读懂的话。
            Assert.False(string.IsNullOrWhiteSpace(ChapterIssueAction.CtaFor(advice.Recommended)));
            Assert.False(string.IsNullOrWhiteSpace(PreflightCta.For(advice.Recommended)));
        }
    }

    [Fact]
    public void The_suppressed_after_alignment_codes_stay_in_step_with_the_analyzer()
    {
        // The workbench groups by IssueCategory, so a code added to the analyzer without a category would
        // silently land in 其他提醒. This pins the ones the chapter surfaces actually show.
        var expected = new Dictionary<string, string>
        {
            ["chapter_unrecognized"] = ReviewCategories.Missing,
            ["chapter_heading_typo"] = ReviewCategories.Missing,
            ["chapter_unnumbered"] = ReviewCategories.Missing,
            ["numeric_chapters_suspected"] = ReviewCategories.Missing,
            ["chapter_number_gap"] = ReviewCategories.Missing,
            ["reference_chapter_absent"] = ReviewCategories.Missing,
            ["numeric_body_group"] = ReviewCategories.Misrecognized,
            ["chapter_repeated_sequence"] = ReviewCategories.Duplicate,
            ["chapter_content_duplicate"] = ReviewCategories.Duplicate,
            ["chapter_cross_title_duplicate"] = ReviewCategories.Duplicate,
            ["chapter_duplicate"] = ReviewCategories.Duplicate,
            ["chapter_number_order"] = ReviewCategories.Structure,
            ["volume_number_duplicate"] = ReviewCategories.Structure,
            ["chapter_level_gap"] = ReviewCategories.Structure,
            ["chapter_structure_suggested"] = ReviewCategories.Structure,
            ["chapter_numbering_variants"] = ReviewCategories.Structure,
        };
        foreach (var (code, category) in expected)
            Assert.Equal(category, IssueCategory.For(Issue(code)));
        // 每一个都必须在策略表里，否则它会退回"其他提醒"而不报错。
        Assert.Equal(expected.Count, IssueResolutionPolicy.All.Count);
        foreach (var definition in IssueResolutionPolicy.All)
        {
            Assert.True(expected.ContainsKey(definition.IssueCode), $"{definition.IssueCode} 未登记在归类表里");
            Assert.False(string.IsNullOrWhiteSpace(definition.Detector));
        }
    }
}
