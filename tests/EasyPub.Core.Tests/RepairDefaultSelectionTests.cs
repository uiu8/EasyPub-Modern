using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// This is meant to be a one-click repair: the user looks at what the software will do and approves
/// it, rather than ticking dozens of boxes. So the preselection covers everything safe, and the three
/// cases that are not safe stay unselected on purpose:
///
///  * a chapter the user edited by hand is never touched automatically,
///  * text that leaves the finished book (a duplicate whose body differs) is never removed for them,
///  * a chapter that is simply missing from the directory is kept unless they say otherwise — a
///    directory is frequently incomplete, and demoting a real chapter would damage the book.
/// </summary>
public class RepairDefaultSelectionTests
{
    private static async Task<ChapterTreeDocument> DocumentAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-default-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text);
        try { return await ChapterTreeDocument.LoadAsync(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Every_safe_action_is_preselected_so_one_click_is_enough()
    {
        // A catalog with volumes, a chapter to add, a title to align, and content to fold back.
        var document = await DocumentAsync(
            "第一章 起点\n这是第一章的正文。\n" +
            "他忽然想起那年的雨\n这是正文，只是恰好单独成行。\n" +
            "第二章 继续\n这是第二章的正文。\n" +
            "第三章 收网\n这是第三章的正文。\n");
        var catalog = ReferenceCatalogInput.ParseText("第一章 起点\n第二章 继续\n第三章 收网")!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        var plan = outcome.Plan!;
        var chosen = plan.DefaultSelection.ToArray();

        // Adding a chapter that was located in the text is safe: it invents nothing.
        var addable = plan.Actions.Where(action => action.Kind == ReferenceActionKind.AddChapter && action.Recommended).ToArray();
        Assert.All(addable, action => Assert.Contains(action, chosen));
        // Folding back a heading the directory does not list is safe too: the text is kept whole.
        var demotable = plan.Actions.Where(action => action.Kind == ReferenceActionKind.DemoteExtra && action.Recommended).ToArray();
        Assert.All(demotable, action => Assert.Contains(action, chosen));
    }

    [Fact]
    public async Task A_safe_duplicate_is_preselected_because_its_text_is_identical()
    {
        // The other half of "select everything safe": a duplicate whose body matches word for word is
        // something the software can decide, unlike the differing-body case below.
        var document = await DocumentAsync(
            "第一章 开始\n这是第一章的正文。\n\n第二章 继续\n完全相同的正文\n\n第二章 继续\n完全相同的正文\n\n第三章 结束\n尾声\n");
        var plan = ReferencePlanner.Build(document, document.Entries,
            ReferenceCatalogInput.ParseText("第一章 开始\n第二章 继续\n第三章 结束")!);

        var duplicates = plan.Actions.Where(action => action.Kind == ReferenceActionKind.RemoveDuplicate).ToArray();
        Assert.NotEmpty(duplicates);
        var safe = duplicates.Where(action => action.Recommended).ToArray();
        Assert.NotEmpty(safe);
        Assert.All(safe, action => Assert.Contains(action, plan.DefaultSelection));
    }

    [Fact]
    public async Task A_chapter_the_user_edited_by_hand_is_never_preselected()
    {
        var document = await DocumentAsync("第一章 起点\n这是第一章的正文。\n第二章 继续\n这是第二章的正文。\n");
        ChapterTreeEntry[] entries =
        [
            new("a", "第一章 起点（我自己改的）", 1, true, 1, [new(2, 2)]) { RecognitionSource = "manual" },
            new("b", "第二章 继续", 1, true, 3, [new(4, 4)]),
        ];
        var plan = ReferencePlanner.Build(document, entries, ReferenceCatalogInput.ParseText("第一章 起点\n第二章 继续")!);

        Assert.DoesNotContain(plan.DefaultSelection, action => action.EntryId == "a");
        Assert.DoesNotContain(plan.DefaultSelection, action => action.Kind == ReferenceActionKind.Retitle && !action.Recommended);
    }

    [Fact]
    public async Task Removing_a_duplicate_whose_body_differs_is_never_preselected()
    {
        var document = await DocumentAsync(
            "第一章 开始\n这是第一章的正文。\n\n第二章 继续\n前半段剧情各不相同\n\n第二章 继续\n后半段剧情也不一样\n");
        var plan = ReferencePlanner.Build(document, document.Entries, ReferenceCatalogInput.ParseText("第一章 开始\n第二章 继续")!);

        // The duplicate is listed so the user can look at it, but the software does not decide which
        // copy of different text is the real one.
        var duplicates = plan.Actions.Where(action => action.Kind == ReferenceActionKind.RemoveDuplicate).ToArray();
        Assert.NotEmpty(duplicates);
        Assert.All(duplicates.Where(action => !action.Recommended),
            action => Assert.DoesNotContain(action, plan.DefaultSelection));
    }

    [Fact]
    public async Task A_chapter_missing_from_the_directory_never_joins_the_selection_by_itself()
    {
        var document = await DocumentAsync(
            "第一章 开始\n这是第一章的正文。\n第二章 继续\n这是第二章的正文。\n");
        // The tree gained a chapter the directory does not know about — the case the window must
        // leave alone, because a directory is frequently incomplete.
        ChapterTreeEntry[] entries =
        [
            new("a", "第一章 开始", 1, true, 1, [new(2, 2)]),
            new("extra", "角色设定", 1, true, null, [new(3, 3)]),
            new("b", "第二章 继续", 1, true, 3, [new(5, 5)]),
        ];
        var plan = ReferencePlanner.Build(document, entries, ReferenceCatalogInput.ParseText("第一章 开始\n第二章 继续")!);

        var outside = plan.Actions.Where(action =>
            action.Kind is ReferenceActionKind.KeepExtra or ReferenceActionKind.DemoteExtra).ToArray();
        Assert.All(outside, action => Assert.DoesNotContain(action, plan.DefaultSelection));

        // Whatever the planner proposes for a hand-added heading, the window's row must not arrive
        // preselected either: the two must agree.
        Assert.All(plan.Actions.Where(action => action.EntryId == "extra"),
            action => Assert.False(action.Recommended));
    }
}
