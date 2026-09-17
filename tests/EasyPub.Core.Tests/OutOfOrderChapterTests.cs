using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// A chapter whose located line sits before the chapter the directory puts ahead of it. The tree is
/// rebuilt in line order, so that chapter comes out first — which is exactly what happens when a source
/// file repeats a block or loses one. The order is not wrong to correct silently; it is wrong to fix
/// without saying so, because everything after it reads differently.
/// </summary>
public class OutOfOrderChapterTests
{
    private static async Task<string> WriteAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-order-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    private static ReferenceCatalog Catalog(params string[] titles)
    {
        var nodes = new List<ReferenceNode> { new("第一卷", ReferenceNodeKind.Volume, null, "第一卷") };
        nodes.AddRange(titles.Select(title => new ReferenceNode(title, ReferenceNodeKind.Chapter, null, "第一卷")));
        return new ReferenceCatalog("test", "测试", nodes);
    }

    [Fact]
    public async Task A_chapter_that_lands_before_its_neighbour_is_reported()
    {
        // The directory runs 1, 2, 3. The text prints 3 before 2, so chapter 2 cannot be placed after
        // chapter 1 without reaching past chapter 3 — it stays unplaced, and chapter 3 takes the line
        // that sits between them.
        var path = await WriteAsync(string.Join('\n',
        [
            "第一章 启程", "", "第一章的正文，写够一点长度好在章节树里看出范围。", "",
            "第三章 收束", "", "第三章的正文，也写够长度。", "",
            "第二章 归途", "", "第二章的正文，写够长度。", "",
        ]));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var catalog = Catalog("第一章 启程", "第二章 归途", "第三章 收束");
            var plan = ReferencePlanner.Build(document, document.Entries, catalog);

            // The directory order and the file order disagree here, so at least one chapter has to be
            // named as sitting before the one the directory puts ahead of it. Which one depends on how
            // the locator resolves the clash; what must not happen is resolving it silently.
            Assert.NotEmpty(plan.OutOfOrder);
            foreach (var (title, previous) in plan.OutOfOrder)
            {
                Assert.Contains(plan.Location.Chapters, c => c.Reference.Title == title);
                Assert.Contains(plan.Location.Chapters, c => c.Reference.Title == previous);
                // "Out of order" can only mean something if both chapters were actually placed.
                Assert.NotNull(plan.Location.Chapters.Single(c => c.Reference.Title == title).Line);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task An_ordinary_book_reports_no_order_problem()
    {
        var path = await WriteAsync(string.Join('\n',
        [
            "第一章 启程", "", "第一章的正文。", "",
            "第二章 归途", "", "第二章的正文。", "",
            "第三章 收束", "", "第三章的正文。", "",
        ]));
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var plan = ReferencePlanner.Build(document, document.Entries, Catalog("第一章 启程", "第二章 归途", "第三章 收束"));
            Assert.Empty(plan.OutOfOrder);
        }
        finally { File.Delete(path); }
    }
}
