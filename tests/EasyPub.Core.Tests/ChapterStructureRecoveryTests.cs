using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class ChapterStructureRecoveryTests
{
    [Fact]
    public async Task Recovery_preserves_source_order_and_groups_resets_without_fake_volumes()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        var text = "介绍\n序章\n正文\n第十一章 前文\n正文\n第二卷结束之后，我想休息。\n说明\n序章\n正文\n第一章 起点\n正文\n第二章 后续\n\n第559章后续\n正文\n第三章 结束\n正文\n第三章 结束\n不同正文";
        try
        {
            await File.WriteAllTextAsync(path, text);
            var doc = await ChapterTreeDocument.LoadAsync(path);
            var result = ChapterStructureRecovery.Analyze(doc);
            Assert.Equal(2, result.Groups);
            Assert.DoesNotContain(result.Entries, e => e.Title.StartsWith("第二卷") || e.Title.StartsWith("第559"));
            Assert.Equal(2, result.Entries.Count(e => e.Title == "第三章 结束"));
            var lines = result.Entries.SelectMany(e => (e.TitleLineNumber is int l ? new[] { l } : Array.Empty<int>()).Concat(e.ContentRanges.SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1))));
            Assert.Equal(Enumerable.Range(1, doc.LineCount), lines);
            var reloaded = await ChapterTreeDocument.LoadAsync(path, existingPlan: doc.CreatePlan(result.Entries));
            Assert.Equal(result.Entries.Select(e => (e.Title, e.Level)), reloaded.Entries.Select(e => (e.Title, e.Level)));
            Assert.Equal(text, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }
}
