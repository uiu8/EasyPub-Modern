using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class ChapterContentDuplicateTests
{
    [Fact]
    public async Task Repeated_sequence_is_explained_in_structure_preview_without_removing_text()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var section = string.Join("\n", Enumerable.Range(25, 4).Select(i => $"第{i}章 标题{i}\n第{i}天，旅人沿着河流继续前进，终于发现远方的城堡。"));
        try
        {
            await File.WriteAllTextAsync(path, section + "\n第29章 间隔\n别的正文\n" + section);
            var doc = await ChapterTreeDocument.LoadAsync(path);
            Assert.Contains(ChapterReviewAnalyzer.Analyze(doc).Groups, g => g.Issue.Code == "chapter_repeated_sequence");
            Assert.Contains(ChapterStructureRecovery.Analyze(doc).Notes, n => n.Contains("连续重复区段"));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public async Task Deletion_persists_and_converter_keeps_only_the_chosen_body()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        const string body = "旅人沿着河流继续前进，终于发现远方城堡的灯光，夜幕渐渐降临。";
        var source = $"第一章 转折\n{body}\n第一章 转折\n{body}\n第二章 后续\n必须保留的下一章。";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var doc = await ChapterTreeDocument.LoadAsync(path);
            var copies = doc.Entries.Where(e => e.Title == "第一章 转折").ToArray();
            foreach (var removeFirst in new[] { true, false })
            {
                var plan = doc.CreatePlan(ChapterContentDuplicates.RemoveCopy(doc, doc.Entries, copies[removeFirst ? 0 : 1].Id, copies[removeFirst ? 1 : 0].Id));
                var reloaded = await ChapterTreeDocument.LoadAsync(path, existingPlan: plan);
                var output = await LegacyTextParser.ParseAsync(path, new(), plan, default);
                Assert.Single(output, c => c.Title == "第一章 转折");
                Assert.Single(output.SelectMany(c => c.Paragraphs), p => p == body);
                Assert.Contains(output.SelectMany(c => c.Paragraphs), p => p == "必须保留的下一章。");
                Assert.DoesNotContain(ChapterReviewAnalyzer.Analyze(reloaded, detectUnrecognized: false).Groups, g => g.Issue.Code == "chapter_content_duplicate");
            }
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("正文", "正文")]
    [InlineData("甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲甲", "乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙乙")]
    public async Task Same_title_and_line_count_are_not_enough(string first, string second)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        try
        {
            await File.WriteAllTextAsync(path, $"第一章 甲\n{first}\n第一章 甲\n{second}");
            var doc = await ChapterTreeDocument.LoadAsync(path);
            Assert.Empty(ChapterContentDuplicates.Find(doc, doc.Entries));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repeated_body_is_a_separate_actionable_group(bool near)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var body = string.Join("\n", Enumerable.Range(1, 50).Select(n => $"第{n}天，旅人沿着河流继续前进，终于发现远方的城堡。"));
        try
        {
            await File.WriteAllTextAsync(path, "第三十四章 转折\n" + body + "\n第三十四章 转折\n" + (near ? body.Replace("第25天", "第二十五天") : body));
            var doc = await ChapterTreeDocument.LoadAsync(path);
            var group = Assert.Single(ChapterReviewAnalyzer.Analyze(doc).Groups.Where(g => g.Issue.Code == "chapter_content_duplicate"));
            Assert.Equal(2, group.NodeIds.Count);
            Assert.Equal(new[] { 1, 52 }, group.Lines);
            Assert.Contains(near ? "相似" : "完全相同", group.Issue.Message);
        }
        finally { File.Delete(path); }
    }
}
