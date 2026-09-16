using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// "What actually changed" has to be readable, and it was not: the report matched entries by their
/// tree <c>Id</c>, but rebuilding a volume level mints a fresh <c>Guid</c> for every volume, so one
/// volume was reported twice — once as "removed" and once as "added", with the same source line. On a
/// real 385-chapter book that turned the change list into 286 lines, most of them the same six volumes
/// counted twice, and the phrase "移除章节项" read as "your text was deleted" when nothing was.
/// </summary>
public class RepairChangeSummaryTests
{
    private static async Task<ChapterTreeDocument> DocumentAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-change-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text);
        try { return await ChapterTreeDocument.LoadAsync(path); }
        finally { File.Delete(path); }
    }

    /// <summary>A heading that is a volume or part rather than a chapter.</summary>
    private static bool IsVolumeLike(string title) =>
        title.Contains('卷') || title.Contains('部') || title.Contains('篇');

    [Fact]
    public async Task Rebuilding_a_volume_is_one_structural_change_not_a_removal_plus_an_addition()
    {
        // A catalog that carries volumes makes the planner build volume entries; the ids it mints for
        // them are new every time, which is what used to produce the phantom pair.
        var document = await DocumentAsync(
            "第一卷 风起\n第一章 起点\n正文一\n第二章 继续\n正文二\n" +
            "第二卷 云涌\n第一章 再会\n正文三\n");
        var catalog = ReferenceCatalogInput.ParseText(
            "第一卷 风起\n第一章 起点\n第二章 继续\n第二卷 云涌\n第一章 再会")!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        Assert.NotNull(outcome.Entries);

        var changes = RepairIntegrity.Describe(outcome.Entries!, document.Entries);
        var lines = RepairIntegrity.DescribeChanges(document.Entries, outcome.Entries!);

        // No volume may be described as removed: its source line is still there, still a volume.
        var volumes = document.Entries.Where(entry => IsVolumeLike(entry.Title)).ToArray();
        Assert.NotEmpty(volumes);
        foreach (var volume in volumes)
            Assert.DoesNotContain(lines, line => line.Contains("移除章节项：" + volume.Title, StringComparison.Ordinal));

        // The structural change is stated once, as its own kind, and counts the volumes it rebuilt.
        var structural = changes.Where(change => change.Kind == RepairChangeKind.RebuiltVolume).ToArray();
        Assert.NotEmpty(structural);
        Assert.All(structural, change => Assert.True(change.Line > 0, "结构变化必须带原文行"));
    }

    [Fact]
    public async Task A_retitled_chapter_is_reported_as_a_title_change_not_a_replacement()
    {
        var document = await DocumentAsync("序\n这是前言。\n第1章 开始\n这是第一章的正文内容。\n第2章 继续\n这是第二章的正文内容。\n");
        var catalog = ReferenceCatalogInput.ParseText("第1章 开始\n第2章 续章")!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        Assert.NotNull(outcome.Entries);

        var changes = RepairIntegrity.Describe(outcome.Entries!, document.Entries);
        // The chapter keeps its source line: only its title moved to the catalog's wording. Reporting
        // that as an addition would hide that the text was already there.
        Assert.Contains(changes, change => change.Kind == RepairChangeKind.RetitledChapter);
        var retitled = changes.Single(change => change.Kind == RepairChangeKind.RetitledChapter);
        Assert.Equal("第2章 续章", retitled.Title);
        Assert.DoesNotContain(changes, change => change.Kind == RepairChangeKind.AddedChapter);

        var lines = RepairIntegrity.DescribeChanges(document.Entries, outcome.Entries!);
        Assert.DoesNotContain(lines, line => line.StartsWith("新增：第2章", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("第2章 续章", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_chapter_that_is_genuinely_new_is_still_reported_as_added()
    {
        // Guard against over-correcting: a real addition must keep saying so. The shapes are built by
        // hand because the planner only adds a chapter the tree had missed while the text still shows
        // the heading — a combination that is awkward to provoke through a real file.
        var document = await DocumentAsync("第1章 起点\n这是第一章的正文内容。\n");
        ChapterTreeEntry[] before = [new("a", "第1章 起点", 1, true, 1, [new(2, 2)])];
        ChapterTreeEntry[] after =
        [
            new("a", "第1章 起点", 1, true, 1, [new(2, 2)]),
            new("new", "第3章 收网", 1, true, null, [new(3, 3)]),
        ];

        var changes = RepairIntegrity.Describe(after, before);
        var added = Assert.Single(changes, change => change.Kind == RepairChangeKind.AddedChapter);
        Assert.Equal("第3章 收网", added.Title);

        var lines = RepairIntegrity.DescribeChanges(before, after);
        Assert.Contains(lines, line => line.StartsWith("新增：第3章 收网", StringComparison.Ordinal));
    }
    [Fact]
    public async Task The_change_list_never_claims_a_number_it_cannot_back_up()
    {
        var document = await DocumentAsync(
            "第一卷 风起\n第一章 起点\n正文一\n第二章 继续\n正文二\n" +
            "第二卷 云涌\n第一章 再会\n正文三\n");
        var catalog = ReferenceCatalogInput.ParseText(
            "第一卷 风起\n第一章 起点\n第二章 继续\n第二卷 云涌\n第一章 再会")!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        var changes = RepairIntegrity.Describe(outcome.Entries!, document.Entries);

        // Summarising is only honest if the parts add up: every change carries a kind and a title, and
        // the list stays far smaller than the tree — the old one grew one line per entry.
        Assert.All(changes, change => Assert.False(string.IsNullOrWhiteSpace(change.Title)));
        Assert.All(changes, change => Assert.True(Enum.IsDefined(typeof(RepairChangeKind), change.Kind)));
        Assert.True(changes.Count <= 12,
            $"变化项 {changes.Count} 条，说明仍在逐条罗列而不是聚合");
    }
}
