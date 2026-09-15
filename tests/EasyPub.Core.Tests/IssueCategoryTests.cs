using EasyPub.Core;
namespace EasyPub.Core.Tests;
public sealed class IssueCategoryTests
{
    [Theory]
    [InlineData("chapter_number_gap", ReviewCategories.Missing)]
    [InlineData("chapter_heading_typo", ReviewCategories.Missing)]
    [InlineData("chapter_structure_suggested", ReviewCategories.Structure)]
    [InlineData("chapter_duplicate", ReviewCategories.Duplicate)]
    [InlineData("chapter_number_order", ReviewCategories.Structure)]
    public void Existing_diagnostics_keep_independent_entry_points(string code, string category)
    {
        var issue = new ConversionPreflightIssue("book.txt", PreflightSeverity.Warning, code, "原始依据", PreflightTargetKind.Chapters, 42);
        Assert.Equal(category, IssueCategory.For(issue));
        Assert.Equal(42, issue.LineNumber);
        Assert.Equal("原始依据", issue.Message);
    }
}
