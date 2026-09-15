using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class ReferenceOutlineTests
{
    [Fact]
    public void Catalog_keeps_volumes_and_chapter_ownership()
    {
        const string html = """
            <html><head><title>测试书 - 作者</title></head><body>
            <dl id="dir">
            <dt>序章</dt><dd><a href="/book/1/10.html" title="第一章 追捕（一）">第一章 追捕（一）</a></dd>
            <dt>第一篇 卷入</dt><dd><a href="/book/1/11.html" title="第一章 无妄之灾">第一章 无妄之灾</a></dd>
            <dd><a href="/book/1/12.html" title="第二章 脱狱">第二章 脱狱</a></dd>
            <dt>第二篇 人间世</dt><dd><a href="/book/1/13.html" title="第一章 我回来了">第一章 我回来了</a></dd>
            </dl></body></html>
            """;
        var catalog = ReferenceCatalogClient.Parse(new Uri("https://www.hetushu.com/book/1/index.html"), html);
        Assert.Equal(new[] { "序章", "第一篇 卷入", "第二篇 人间世" }, catalog.VolumeTitles);
        Assert.Equal(4, catalog.Titles.Count);
        Assert.Equal(2, catalog.ChaptersOf("第一篇 卷入").Count);
        Assert.Equal("序章", catalog.ChaptersOf("序章")[0].VolumeTitle);
    }

    [Fact]
    public void Key_matches_numbering_and_volume_prefix_variants()
    {
        // Volume names come from the reference directory; that is what lets an embedded volume
        // prefix ("第六篇 路") be stripped from a local title.
        var prefixes = new[] { "卷入", "人间世", "路" };
        Assert.Equal(ReferenceOutline.ParseKey("第六章 追忆杀戮时光（一）"), ReferenceOutline.ParseKey("第六章 追忆杀戮时光（1）"));
        Assert.Equal(ReferenceOutline.ParseKey("第十章 在路上"), ReferenceOutline.ParseKey("第六篇 路 第十章 在路上", prefixes));
        Assert.True(ReferenceOutline.MatchScore(ReferenceOutline.ParseKey("第十章 在路上"), ReferenceOutline.ParseKey("第六篇 第十章")) > 0);
        Assert.Equal(0, ReferenceOutline.MatchScore(ReferenceOutline.ParseKey("第一章 追捕"), ReferenceOutline.ParseKey("第一章 无妄之灾")));
    }

    [Fact]
    public async Task Planner_builds_volume_levels_without_adding_source_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        // Volume names sit on the same line as the chapter title, which is the hard case: the volume
        // must become a level of its own without inventing a heading line.
        var source = "第一篇 卷一 第一章 起始\n\n正文一。\n\n第二章 继续\n\n正文二。\n\n第二篇 卷二 第一章 新章\n\n正文三。";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var document = await ChapterTreeDocument.LoadAsync(path);
            var catalog = new ReferenceCatalog("test", "测试书",
            [
                new ReferenceNode("第一篇 卷一", ReferenceNodeKind.Volume, null, "第一篇 卷一"),
                new ReferenceNode("第一章 起始", ReferenceNodeKind.Chapter, null, "第一篇 卷一"),
                new ReferenceNode("第二章 继续", ReferenceNodeKind.Chapter, null, "第一篇 卷一"),
                new ReferenceNode("第二篇 卷二", ReferenceNodeKind.Volume, null, "第二篇 卷二"),
                new ReferenceNode("第一章 新章", ReferenceNodeKind.Chapter, null, "第二篇 卷二")
            ]);
            var plan = ReferencePlanner.Build(document, document.Entries, catalog);
            var rebuilt = ReferencePlanner.Apply(document, document.Entries, plan, plan.DefaultSelection.ToArray(), true, true);
            var volumes = rebuilt.Where(entry => entry.Level == 1 && !entry.IsFrontMatter).ToArray();
            Assert.Equal(2, volumes.Length);
            // Front matter is pinned to Level 2 by ValidatePlan, so it must not be counted as a chapter.
            Assert.Equal(3, rebuilt.Count(entry => entry.Level == 2 && !entry.IsFrontMatter));
            // A volume shares its first chapter's heading line and owns no content range of its own.
            Assert.All(volumes, volume => Assert.Empty(volume.ContentRanges));
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Planner_finds_duplicates_and_never_touches_source()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var source = "第一章 无妄之灾\n\n正文一。\n\n第二章 脱狱\n\n正文二。\n\n第二章 脱狱\n\n重复正文。\n\n第三章 通缉犯\n\n正文三。";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var document = await ChapterTreeDocument.LoadAsync(path);
            var catalog = new ReferenceCatalog("test", "测试书",
            [
                new ReferenceNode("第一章 无妄之灾", ReferenceNodeKind.Chapter, null, null),
                new ReferenceNode("第二章 脱狱", ReferenceNodeKind.Chapter, null, null),
                new ReferenceNode("第三章 通缉犯", ReferenceNodeKind.Chapter, null, null)
            ]);
            var plan = ReferencePlanner.Build(document, document.Entries, catalog);
            Assert.Equal(0, plan.AddCount);
            Assert.Equal(1, plan.DuplicateCount);
            var rebuilt = ReferencePlanner.Apply(document, document.Entries, plan, plan.Actions.Where(a => a.Kind != ReferenceActionKind.KeepExtra).ToArray(), true);
            Assert.Equal(3, rebuilt.Count(entry => !entry.IsFrontMatter));
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Repair_removes_duplicate_body_and_reports_removed_lines()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        // Line 9 repeats the heading of line 5, and line 11 repeats its body.
        var source = "第一章 甲\n\n正文一。\n\n第二章 乙\n\n正文二。\n\n第二章 乙\n\n重复正文。\n\n第三章 丙\n\n正文三。";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var document = await ChapterTreeDocument.LoadAsync(path);
            var catalog = new ReferenceCatalog("test", "测试书",
            [
                new ReferenceNode("第一章 甲", ReferenceNodeKind.Chapter, null, null),
                new ReferenceNode("第二章 乙", ReferenceNodeKind.Chapter, null, null),
                new ReferenceNode("第三章 丙", ReferenceNodeKind.Chapter, null, null)
            ]);
            var plan = ReferencePlanner.Build(document, document.Entries, catalog);
            var dropped = new List<int>();
            var rebuilt = ReferencePlanner.Apply(document, document.Entries, plan, plan.Actions.Where(a => a.Kind != ReferenceActionKind.KeepExtra).ToArray(), true, false, dropped);
            Assert.NotEmpty(dropped);
            var covered = rebuilt.SelectMany(entry => entry.ContentRanges
                .SelectMany(range => Enumerable.Range(range.StartLine, range.EndLine - range.StartLine + 1))).ToHashSet();
            // The repeated body must not be absorbed by the preceding chapter: removing a duplicate
            // has to actually remove it from the finished book.
            Assert.DoesNotContain(11, covered);
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }}
