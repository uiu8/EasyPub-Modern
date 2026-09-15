using System.IO;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// A chapter the official directory does not list is the user's call, not the alignment's.
/// It must be recorded with its exact position so the user can see where it is, kept by default,
/// and — when the user does decide to remove it — lose only its heading boundary so the text
/// survives inside the preceding chapter.
/// </summary>
public class ExtraChapterTests
{
    private static async Task<string> WriteAsync(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-extra-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, body);
        return path;
    }

    private static ReferenceCatalog CatalogWithFirstChapter() =>
        new("test", "测试", [
            new ReferenceNode("第一章 开始", ReferenceNodeKind.Chapter, "https://example.com/1", null),
        ]);

    [Fact]
    public async Task An_unlisted_numbered_chapter_is_recorded_with_its_position_and_left_alone()
    {
        var path = await WriteAsync(string.Join("\n", new[]
        {
            "第一章 开始", "", "正文一", "",
            "第九十九章 番外篇", "", "番外正文", "",
        }));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var plan = ReferencePlanner.Build(document, document.Entries, CatalogWithFirstChapter());

            var extra = Assert.Single(plan.Actions.Where(action => action.Kind == ReferenceActionKind.KeepExtra));
            Assert.Equal(5, extra.Line);
            Assert.Contains("第九十九章 番外篇", extra.Title);
            Assert.False(extra.Recommended);

            var kept = ReferencePlanner.Apply(
                document, document.Entries, plan, plan.DefaultSelection.ToArray(), true, false);

            Assert.Contains(kept, entry => entry.Title.Contains("第九十九章"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// The point of the action: choosing it removes the phantom chapter without losing a single
    /// line. RepairIntegrity.Verify runs inside Apply, so an unbalanced rebuild would throw here
    /// instead of silently dropping the text.
    /// </summary>
    [Fact]
    public async Task Choosing_the_extra_gives_its_text_to_the_previous_chapter_without_losing_a_line()
    {
        var path = await WriteAsync(string.Join("\n", new[]
        {
            "第一章 开始", "", "正文一", "",
            "第九十九章 番外篇", "", "番外正文", "",
        }));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var plan = ReferencePlanner.Build(document, document.Entries, CatalogWithFirstChapter());
            var extra = plan.Actions.Single(action => action.Kind == ReferenceActionKind.KeepExtra);

            var selected = plan.DefaultSelection.Append(extra).ToArray();
            var rebuilt = ReferencePlanner.Apply(document, document.Entries, plan, selected, true, false);

            Assert.DoesNotContain(rebuilt, entry => entry.Title.Contains("第九十九章"));

            // Every source line still belongs to some entry: the heading became ordinary text.
            var covered = rebuilt.SelectMany(entry => entry.ContentRanges)
                .SelectMany(range => Enumerable.Range(range.StartLine, range.EndLine - range.StartLine + 1))
                .ToHashSet();
            foreach (var line in new[] { 5, 7 }) Assert.Contains(line, covered);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    /// <summary>
    /// A heading the user added by hand is not an "extra to review" — it is an explicit decision.
    /// It gets no action at all, and no alignment pass may remove it.
    /// </summary>
    [Fact]
    public async Task A_manually_added_heading_gets_no_action_and_is_never_taken_away()
    {
        var path = await WriteAsync(string.Join("\n", new[]
        {
            "第一章 开始", "", "正文一", "",
            "第九十九章 番外篇", "", "番外正文", "",
        }));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var manual = document.Entries
                .Select(entry => entry.Title.Contains("第九十九章")
                    ? entry with { RecognitionSource = "manual" }
                    : entry)
                .ToArray();

            var plan = ReferencePlanner.Build(document, manual, CatalogWithFirstChapter());

            Assert.DoesNotContain(plan.Actions, action => action.Kind == ReferenceActionKind.KeepExtra);

            var rebuilt = ReferencePlanner.Apply(
                document, manual, plan, plan.DefaultSelection.ToArray(), true, false);

            Assert.Contains(rebuilt, entry => entry.Title.Contains("第九十九章"));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
