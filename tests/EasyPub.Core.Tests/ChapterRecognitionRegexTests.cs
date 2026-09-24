using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ChapterRecognitionRegexTests
{
    [Fact]
    public async Task Invalid_chapter_rule_names_the_rule_instead_of_looking_like_empty_book()
    {
        var path = Path.Combine(Path.GetTempPath(), $"invalid-chapter-rule-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n正文", Encoding.UTF8);
        try
        {
            var error = Assert.Throws<ChapterRecognitionRuleException>(() =>
                ChapterTreeDocument.Load(path, File.ReadAllBytes(path), chapterPattern: "["));
            Assert.Equal("普通章节标题", error.RuleName);
            Assert.Equal(ChapterRecognitionRuleFailure.InvalidPattern, error.Failure);
            Assert.Contains("不是有效的正则表达式", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Matching_timeout_reports_rule_and_line()
    {
        var regex = ChapterRecognitionRegex.Compile("^(a+)+$", "^$", "测试章节规则");
        var input = new string('a', 200_000) + "!";

        var error = Assert.Throws<ChapterRecognitionRuleException>(() =>
            ChapterRecognitionRegex.IsMatch(regex, input, "测试章节规则", lineNumber: 37));

        Assert.Equal(ChapterRecognitionRuleFailure.Timeout, error.Failure);
        Assert.Equal("测试章节规则", error.RuleName);
        Assert.Contains("第 37 行", error.Message);
    }

    [Fact]
    public async Task Saved_tree_does_not_recompile_current_invalid_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"saved-rule-reopen-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n正文", Encoding.UTF8);
        try
        {
            var hierarchy = new TocHierarchyOptions { Enabled = false };
            var original = ChapterTreeDocument.Load(path, File.ReadAllBytes(path), hierarchy: hierarchy);
            var plan = original.CreatePlan(original.Entries);

            var reopened = ChapterTreeDocument.Load(
                path,
                File.ReadAllBytes(path),
                chapterPattern: "[",
                hierarchy: new TocHierarchyOptions { Level1Pattern = "[" },
                existingPlan: plan);

            Assert.Equal(plan.Entries.Select(entry => entry.Title), reopened.Entries.Select(entry => entry.Title));
            Assert.Equal(original.ChapterPattern, reopened.ChapterPattern);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
