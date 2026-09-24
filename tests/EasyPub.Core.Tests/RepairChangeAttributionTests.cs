using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// Each change in the finished tree has to name the planned action that caused it.
///
/// The confirmation window used to work this out in the view by matching a change back to an action on
/// the same source line, and the match silently failed on exactly the rows the user had to decide
/// about: a heading the tree folds back into the body is reported on the heading's line, while the
/// action that folds it sits on the same line under a different title. Those rows therefore carried no
/// checkbox, so the change list could not reach them and ticking the list did not reach every change.
/// The attribution has to come from the side that knows the answer.
/// </summary>
public class RepairChangeAttributionTests
{
    private static async Task<ChapterTreeDocument> DocumentAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-attr-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text);
        try { return await ChapterTreeDocument.LoadAsync(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void An_action_is_identified_by_its_kind_line_and_title()
    {
        var action = new ReferenceAction(ReferenceActionKind.Retitle, 42, "第2章 续章", "目录写法", null, null, true);
        Assert.Equal(action.Key, new ReferenceAction(ReferenceActionKind.Retitle, 42, "第2章 续章", "别的说明", "e", null, false).Key);
        Assert.NotEqual(action.Key, (action with { Line = 43 }).Key);
        Assert.NotEqual(action.Key, (action with { Kind = ReferenceActionKind.AddChapter }).Key);
    }

    [Fact]
    public async Task A_chapter_the_directory_added_names_the_action_that_added_it()
    {
        // Built from a tree diff and a plan by hand, because the two real paths to this change are hard
        // to provoke through a file: when the locator cannot pin the directory's title to a line it
        // reports the action with line 0 and the repair inserts nothing, which is the whole point of the
        // "never invent a chapter" rule. What is under test here is the attribution, not the location.
        var document = await DocumentAsync("第1章 起点\n这是第一章的正文内容。\n");
        ChapterTreeEntry[] before = [new("a", "第1章 起点", 1, true, 1, [new(2, 2)])];
        ChapterTreeEntry[] after =
        [
            new("a", "第1章 起点", 1, true, 1, [new(2, 2)]),
            new("new", "第3章 收网", 1, true, 5, [new(6, 6)]),
        ];
        var action = new ReferenceAction(ReferenceActionKind.AddChapter, 5, "第3章 收网",
            "原文第 5 行是「第3章 收网」，章节树未收录", null, null, true);
        var plan = new ReferencePlan(ReferenceCatalogInput.ParseText("第1章 起点\n第3章 收网")!, new ReferenceLocation([]), [action]);

        var changes = RepairIntegrity.DescribeWithActions(plan, after, before);

        var added = Assert.Single(changes, change => change.Kind == RepairChangeKind.AddedChapter);
        Assert.Equal("第3章 收网", added.Title);
        Assert.Equal(action.Key, added.ActionKey);
        Assert.Equal(added.ActionKey, plan.Actions.Single(candidate => candidate.Key == added.ActionKey).Key);
    }

    [Fact]
    public async Task A_duplicate_removal_names_the_action_that_removed_it()
    {
        var document = await DocumentAsync("第1章 起点\n这是第一章的正文内容。\n");
        ChapterTreeEntry[] before =
        [
            new("a", "第1章 起点", 1, true, 1, [new(2, 2)]),
            new("b", "第1章 起点", 1, true, 9, [new(10, 10)]),
        ];
        ChapterTreeEntry[] after = [new("a", "第1章 起点", 1, true, 1, [new(2, 2)])];
        var action = new ReferenceAction(ReferenceActionKind.RemoveDuplicate, 1, "第1章 起点",
            "「第1章 起点」在原文出现 2 次（第 1、9 行）", "a", null, true);
        var plan = new ReferencePlan(ReferenceCatalogInput.ParseText("第1章 起点")!, new ReferenceLocation([]), [action]);

        var changes = RepairIntegrity.DescribeWithActions(plan, after, before);

        var removed = Assert.Single(changes, change => change.Kind == RepairChangeKind.RemovedEntry);
        Assert.Equal("第1章 起点", removed.Title);
        Assert.Equal(action.Key, removed.ActionKey);
    }

    [Fact]
    public async Task A_retitled_chapter_names_the_retitle_action()
    {
        var document = await DocumentAsync("第1章 开始\n这是第一章的正文内容。\n第2章 继续\n这是第二章的正文内容。\n");
        var catalog = ReferenceCatalogInput.ParseText("第1章 开始\n第2章 续章")!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        Assert.NotNull(outcome.Entries);

        var changes = RepairIntegrity.DescribeWithActions(outcome.Plan!, outcome.Entries!, document.Entries);

        var retitled = Assert.Single(changes, change => change.Kind == RepairChangeKind.RetitledChapter);
        var action = Assert.Single(outcome.Plan!.Actions, candidate => candidate.Kind == ReferenceActionKind.Retitle);
        Assert.Equal("第2章 续章", retitled.Title);
        Assert.Equal(action.Key, retitled.ActionKey);
        // The action the key names really is the one that carries this title, which is what lets the
        // window tick a change and mean it.
        Assert.Equal(retitled.Title, outcome.Plan.Actions.Single(candidate => candidate.Key == retitled.ActionKey).Title);
    }

    [Fact]
    public async Task Changes_the_alignment_makes_by_itself_carry_no_action()
    {
        // Chapters acquire catalog levels under existing volumes without a selectable edit action:
        // the level is built as part of aligning. Saying otherwise would show the user a choice that
        // does not exist.
        var document = await DocumentAsync(
            "第一卷 风起\n第一章 起点\n正文一\n第二章 继续\n正文二\n" +
            "第二卷 云涌\n第一章 再会\n正文三\n");
        var catalog = ReferenceCatalogInput.ParseText(
            "第一卷 风起\n第一章 起点\n第二章 继续\n第二卷 云涌\n第一章 再会")!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        Assert.NotNull(outcome.Entries);

        var changes = RepairIntegrity.DescribeWithActions(outcome.Plan!, outcome.Entries!, document.Entries);

        var volumes = changes.Where(change => change.Kind == RepairChangeKind.ChangedLevel).ToArray();
        Assert.NotEmpty(volumes);
        Assert.All(volumes, change => Assert.Null(change.ActionKey));
        // And the same list is still what Describe alone says: attribution adds a key, never a change.
        Assert.Equal(
            RepairIntegrity.Describe(outcome.Entries!, document.Entries).Select(change => (change.Kind, change.Line, change.Title)),
            changes.Select(change => (change.Kind, change.Line, change.Title)));
    }

    [Fact]
    public async Task Every_change_a_selectable_action_can_reach_is_attributed_to_an_action_in_the_plan()
    {
        // The invariant the window relies on: a key it is handed always resolves inside the plan it was
        // handed with. A key that resolves to nothing means a change the user cannot act on.
        var document = await DocumentAsync(
            "序\n这是前言。\n第1章 开始\n这是第一章的正文内容。\n第2章 继续\n这是第二章的正文内容。\n" +
            "第3章 收网\n这是第三章的正文内容。\n");
        var catalog = ReferenceCatalogInput.ParseText("第1章 开始\n第2章 续章\n第3章 收网")!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        Assert.NotNull(outcome.Entries);

        var changes = RepairIntegrity.DescribeWithActions(outcome.Plan!, outcome.Entries!, document.Entries);
        var byKey = outcome.Plan!.Actions.ToDictionary(action => action.Key, StringComparer.Ordinal);

        foreach (var change in changes.Where(change => change.ActionKey is not null))
        {
            Assert.True(byKey.ContainsKey(change.ActionKey!), $"变化「{change.Title}」的动作键无法在计划中解析：{change.ActionKey}");
            // A change the window offers to cancel must belong to an action that can be cancelled:
            // KeepExtra is a notice, never applied.
            Assert.NotEqual(ReferenceActionKind.KeepExtra, byKey[change.ActionKey!].Kind);
        }
        Assert.Contains(changes, change => change.ActionKey is not null);
    }

    [Fact]
    public async Task A_heading_folded_back_into_the_body_names_the_action_that_folded_it()
    {
        // 番外 人物卡 is outside the directory and short enough to be a heading, so the plan offers to
        // demote it; applying that removes the entry from the tree while keeping every line of its body.
        var document = await DocumentAsync(
            "第1章 起点\n这是第一章的正文内容。\n第2章 继续\n这是第二章的正文内容。\n番外 人物卡\n这是番外的正文内容。\n");
        var catalog = ReferenceCatalogInput.ParseText("第1章 起点\n第2章 继续")!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        Assert.NotNull(outcome.Entries);

        var changes = RepairIntegrity.DescribeWithActions(outcome.Plan!, outcome.Entries!, document.Entries);
        var demoted = Assert.Single(changes, change =>
            change.Kind is RepairChangeKind.RemovedEntry or RepairChangeKind.FoldedIntoBody);

        var action = Assert.Single(outcome.Plan!.Actions, candidate => candidate.Title == "番外 人物卡");
        Assert.Equal(action.Key, demoted.ActionKey);
    }
}
