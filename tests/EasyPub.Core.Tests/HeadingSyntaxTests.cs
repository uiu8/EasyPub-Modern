using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class HeadingSyntaxTests
{
    [Theory]
    [InlineData("110", 110)]
    [InlineData("一一〇", 110)]
    [InlineData("壹佰壹拾", 110)]
    [InlineData("１１０", 110)]
    [InlineData("2万3千", 23000)]
    [InlineData("一千零八", 1008)]
    public void Reference_numbers_use_the_same_value(string number, int expected)
    {
        Assert.Equal(expected, HeadingSyntax.ParseNumber(number));
        Assert.Equal(expected.ToString(), ReferenceOutline.ParseKey($"第{number}章 归来").Number);
        Assert.Equal(ReferenceOutline.ParseKey($"第{expected}章 归来").Canonical,
            ReferenceOutline.ParseKey($"第{number}章 归来").Canonical);
    }

    [Theory]
    [InlineData("三回头看", false)]
    [InlineData("三回到家里", false)]
    [InlineData("前言不搭后语", false)]
    [InlineData("正文开始了", false)]
    [InlineData("他提到第三章，然后离开。", false)]
    [InlineData("第一一〇章 归来", true)]
    [InlineData("壹佰壹拾章 归来", true)]
    [InlineData("第１１０章 归来", true)]
    [InlineData("八百三十章 赌徒", true)]
    [InlineData("【第三回 风波】", true)]
    [InlineData("序章", true)]
    [InlineData("番外篇：归来", true)]
    [InlineData("前言 书的缘起", true)]
    public async Task Tree_and_direct_export_agree_and_keep_prose(string line, bool heading)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, line + "\n正文保留\n");
            foreach (var enabled in new[] { false, true })
            {
                var options = new TocHierarchyOptions { Enabled = enabled };
                var document = await ChapterTreeDocument.LoadAsync(path, hierarchy: options);
                var chapters = await LegacyTextParser.ParseAsync(path, new ConversionOptions { TocHierarchy = options }, null, default);
                Assert.Equal(heading, document.Entries.Any(e => !e.IsFrontMatter && e.Title == line));
                Assert.Equal(heading, chapters.Any(e => e.Title == line));
                Assert.Contains("正文保留", chapters.SelectMany(c => c.Paragraphs));
                if (!heading) Assert.Contains(line, chapters.SelectMany(c => c.Paragraphs));
            }
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("三回头看")]
    [InlineData("前言不搭后语")]
    [InlineData("正文开始了")]
    public void Reference_locator_does_not_promote_prose(string text)
    {
        var catalog = new ReferenceCatalog("test", "test", [new(text, ReferenceNodeKind.Chapter, null, null)]);
        Assert.Equal(1, ReferenceLocator.Locate(["", text, ""], catalog).Missing);
    }

    [Fact]
    public async Task Explicit_expression_still_overrides_builtins()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, "第一章 标题\n自订 开始\n正文");
            var doc = await ChapterTreeDocument.LoadAsync(path, "^自订");
            Assert.Equal("自订 开始", Assert.Single(doc.Entries, e => !e.IsFrontMatter).Title);
        }
        finally { File.Delete(path); }
    }
}
