using EasyPub.Core;

namespace EasyPub.Desktop.Tests;

/// <summary>
/// The preflight overview folds same-category reminders of one book into a single row. Grouping used
/// to key on the raw file name, so the same book arriving through paths that differed only by
/// extension or directory formed one group per issue — the report then listed a book's reminders one
/// per line, which is exactly the wall of near-identical rows the folding exists to prevent.
/// </summary>
public class PreflightCollapseTests
{
    private static PreflightIssueRow Row(string book, string code, string message) =>
        PreflightIssueRow.From(new ConversionPreflightIssue(
            book, PreflightSeverity.Warning, code, message, PreflightTargetKind.Chapters));

    [Fact]
    public void Reminders_of_one_book_fold_into_a_single_row()
    {
        var rows = new[]
        {
            Row(@"C:\b\疯巫妖.txt", "chapter_number_gap", "章节编号从 401 跳到 412"),
            Row(@"C:\b\疯巫妖.txt", "chapter_number_gap", "章节编号从 657 跳到 660"),
            Row(@"C:\b\疯巫妖.txt", "chapter_number_gap", "章节编号从 688 跳到 690"),
        };

        var collapsed = PreflightWindow.Collapse(rows);

        var row = Assert.Single(collapsed);
        Assert.Contains("共 3 条同类提醒", row.Message);
    }

    /// <summary>The real failure: identical book, paths differing only by extension and directory.</summary>
    [Fact]
    public void Paths_that_differ_only_by_extension_or_folder_still_fold()
    {
        var rows = new[]
        {
            Row(@"C:\b\疯巫妖的实验日志(愤怒的松鼠).txt", "chapter_number_gap", "章节编号从 401 跳到 412"),
            Row(@"D:\other\疯巫妖的实验日志(愤怒的松鼠).txt", "chapter_number_gap", "章节编号从 657 跳到 660"),
        };

        var collapsed = PreflightWindow.Collapse(rows);

        Assert.Single(collapsed);
        Assert.Contains("共 2 条同类提醒", collapsed[0].Message);
    }

    [Fact]
    public void Different_categories_of_one_book_stay_separate() =>
        Assert.Equal(2, PreflightWindow.Collapse(new[]
        {
            Row(@"C:\b\疯巫妖.txt", "chapter_number_gap", "缺章"),
            Row(@"C:\b\疯巫妖.txt", "chapter_headline_typography", "广告"),
        }).Length);

    [Fact]
    public void Different_books_never_merge() =>
        Assert.Equal(2, PreflightWindow.Collapse(new[]
        {
            Row(@"C:\b\甲.txt", "chapter_number_gap", "缺章"),
            Row(@"C:\b\乙.txt", "chapter_number_gap", "缺章"),
        }).Length);

    [Fact]
    public void A_single_reminder_is_left_exactly_as_it_was()
    {
        var rows = new[] { Row(@"C:\b\甲.txt", "chapter_number_gap", "章节编号从 401 跳到 412") };

        var collapsed = PreflightWindow.Collapse(rows);

        Assert.Same(rows[0], collapsed[0]);
    }

    [Theory]
    [InlineData(@"C:\b\书.txt", "书")]
    [InlineData(@"C:\b\书", "书")]
    [InlineData("批量任务", "批量任务")]
    public void Book_key_ignores_extension_and_folder(string input, string expected) =>
        Assert.Equal(expected, PreflightWindow.BookKey(input));
}
