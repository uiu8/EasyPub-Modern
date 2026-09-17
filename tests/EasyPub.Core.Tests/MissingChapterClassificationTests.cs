using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// "The directory lists a chapter the tree does not have" covers three situations that call for three
/// different answers, and the report has to say which one it found:
///
/// 1. the heading is there and only the recogniser missed it — recoverable without a decision;
/// 2. the body is there but the heading is not — recoverable, but adopting a line as the heading
///    changes how the chapter is cut, so a person has to look;
/// 3. the body is gone as well — not recoverable, and saying so is the only honest answer.
///
/// Reporting all three as "not located" is what let a book with eleven chapters of missing prose be
/// described as "aligned 478/480".
/// </summary>
public class MissingChapterClassificationTests
{
    private static async Task<string> WriteAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-classify-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    private static ReferenceCatalog Catalog(params string[] chapterTitles)
    {
        var nodes = new List<ReferenceNode>
        {
            new("第一卷", ReferenceNodeKind.Volume, null, "第一卷"),
        };
        nodes.AddRange(chapterTitles.Select(title => new ReferenceNode(title, ReferenceNodeKind.Chapter, null, "第一卷")));
        return new ReferenceCatalog("test", "测试", nodes);
    }

    [Fact]
    public async Task A_chapter_whose_headings_body_survives_is_offered_a_line_to_adopt()
    {
        // Chapter two's heading line is simply gone; its paragraphs were absorbed by the chapter above.
        // Nothing in the text says "第二章" — which is exactly why the directory is the only thing that
        // can tell the reader a chapter is missing here, and why the answer has to be "this line could
        // be its title" rather than "chapter not found". The surviving line carries no sentence-ending
        // punctuation, which is what lets it pass as a heading candidate at all.
        // Chapter two is titled with a single character, so the locator's last-resort pass cannot search
        // for it at all ("a title with fewer than two words carries no evidence this pass can use").
        // What survives in the text is one line that looks like a heading and belongs to no chapter —
        // which is what this classification exists to hand to the reader instead of "chapter not found".
        var path = await WriteAsync(string.Join('\n',
        [
            "第一章 启程", "", "第一章的正文。", "",
            "归章", "", "归途正文的第一段。", "",
            "第三章 收束", "", "第三章的正文。", "",
        ]));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var plan = ReferencePlanner.Build(document, document.Entries, Catalog("第一章 启程", "第二章 归", "第三章 收束"));

            var adopt = plan.OfKind(ReferenceActionKind.AdoptHeading).SingleOrDefault();
            Assert.NotNull(adopt);
            Assert.Equal("第二章 归", adopt!.Title);
            Assert.Equal("归章", document.SourceLine(adopt.Line)!.Text.Trim());
            Assert.False(adopt.Recommended);
            // The text is there, so this is not a missing-body finding.
            Assert.Empty(plan.OfKind(ReferenceActionKind.MissingBody));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_chapter_whose_position_holds_other_text_is_reported_as_occupied()
    {
        var path = await WriteAsync(string.Join('\n',
        [
            "第一章 启程", "", "第一章的正文。", "",
            "第三章 收束", "", "第三章的正文。", "",
        ]));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var plan = ReferencePlanner.Build(document, document.Entries, Catalog("第一章 启程", "第二章 归途", "第三章 收束"));

            var missing = Assert.Single(plan.OfKind(ReferenceActionKind.MissingBody));
            Assert.Equal("第二章 归途", missing.Title);
            Assert.False(missing.Recommended);
            // The position between the two chapters is owned by the first chapter's body, so this is a
            // repeated-or-reordered source, not an incomplete one. The detail has to say which.
            Assert.Contains("占着", missing.Detail, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task The_report_separates_the_two_reasons_for_a_missing_chapter()
    {
        var path = await WriteAsync(string.Join('\n',
        [
            "第一章 启程", "", "第一章的正文。", "",
            "第三章 收束", "", "第三章的正文。", "",
        ]));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var outcome = ChapterAutoRepair.Prepare(document, Catalog("第一章 启程", "第二章 归途", "第三章 收束"));

            var group = Assert.Single(outcome.Report, item => item.Label == "正文缺失（需人工核对）");
            Assert.Equal(1, group.Count);
            Assert.Contains("占着", group.Summary, StringComparison.Ordinal);
            // Nothing in this group may be ticked: there is no body to adopt and none to insert. The
            // kind still names the finding so the row can be grouped and explained.
            Assert.All(group.Items, item => Assert.False(item.Selectable));
            Assert.All(group.Items, item => Assert.Equal(ReferenceActionKind.MissingBody, item.Kind));
        }
        finally { File.Delete(path); }
    }
}
