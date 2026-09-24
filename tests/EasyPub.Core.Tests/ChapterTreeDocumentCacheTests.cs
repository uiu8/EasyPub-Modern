using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public sealed class ChapterTreeDocumentCacheTests
{
    [Fact]
    public async Task Hand_edited_plan_with_same_count_does_not_reuse_old_document()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chapter-cache-plan-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "第一章 原题\n正文", Encoding.UTF8);
        try
        {
            var hierarchy = new TocHierarchyOptions { Enabled = false };
            var original = await ChapterTreeDocument.LoadAsync(path, hierarchy: hierarchy);
            var firstPlan = original.CreatePlan(original.Entries);
            var secondPlan = firstPlan with
            {
                Entries = firstPlan.Entries.Select(entry => entry with { Title = "第一章 手动标题" }).ToArray(),
            };
            var cache = new ChapterTreeDocumentCache();

            var first = await cache.GetOrLoadAsync(path, null, hierarchy, TextEncodingMode.Auto, firstPlan);
            var second = await cache.GetOrLoadAsync(path, null, hierarchy, TextEncodingMode.Auto, secondPlan);

            Assert.NotSame(first, second);
            Assert.Equal("第一章 原题", first.Entries.Single(entry => !entry.IsFrontMatter).Title);
            Assert.Equal("第一章 手动标题", second.Entries.Single(entry => !entry.IsFrontMatter).Title);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
