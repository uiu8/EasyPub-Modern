using EasyPub.Core;
using System.Text.Json;

namespace EasyPub.Core.Tests;

public class RepairSelectionTests
{
    [Fact]
    public async Task Deselecting_a_proposed_demotion_keeps_the_heading()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n正文\n后记\n后记正文\n第二章 后续\n正文二");
        try
        {
            var doc = await ChapterTreeDocument.LoadAsync(path);
            ChapterTreeEntry[] entries = [new("a", "第一章 开始", 1, true, 1, [new(2, 2)]),
                new("note", "后记", 1, true, 3, [new(4, 4)]), new("b", "第二章 后续", 1, true, 5, [new(6, 6)])];
            var catalog = ReferenceCatalogInput.ParseText("第一章 开始\n第二章 后续")!;
            var plan = ReferencePlanner.Build(doc, entries, catalog);
            Assert.Contains(plan.DefaultSelection, a => a.Kind == ReferenceActionKind.DemoteExtra);
            var kept = ReferencePlanner.Apply(doc, entries, plan, [], true);
            Assert.Equal(entries, kept);
            var applied = ReferencePlanner.Apply(doc, entries, plan, plan.DefaultSelection.ToArray(), true);
            Assert.DoesNotContain(applied, e => e.Id == "note");
            RepairIntegrity.Verify(entries, applied, []);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Reference_repair_preserves_split_manual_entry_and_order()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        await File.WriteAllTextAsync(path, "第一章 开始\n正文一\n拆分正文\n第二章 后续\n正文二");
        try
        {
            var doc = await ChapterTreeDocument.LoadAsync(path);
            ChapterTreeEntry[] entries = [new("b", "第二章 后续", 1, true, 4, [new(5, 5)]) { RecognitionSource = "manual" },
                new("a", "第一章 开始（手改）", 1, true, 1, [new(2, 2)]) { RecognitionSource = "manual" },
                new("split", "自己拆出的章节", 2, false, null, [new(3, 3)]) { RecognitionSource = "manual" }];
            var catalog = ReferenceCatalogInput.ParseText("第一章 开始\n第二章 后续")!;
            var plan = ReferencePlanner.Build(doc, entries, catalog);
            Assert.DoesNotContain(plan.DefaultSelection, a => a.Kind == ReferenceActionKind.Retitle);
            var actual = ReferencePlanner.Apply(doc, entries, plan, plan.DefaultSelection.ToArray(), true, true);
            Assert.Equal(entries, actual);
            RepairIntegrity.Verify(entries, actual, []);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Project_plan_roundtrips_reference_provenance_and_review_decisions()
    {
        var catalog = new ReferenceCatalog("https://example.com/book", "原始来源页", ReferenceCatalogInput.ParseText("第一章 起点")!.Nodes);
        var plan = new ChapterTreePlan("hash", []) { ReferenceCatalog = catalog, ConfirmedReviews = new Dictionary<string, ChapterReviewGroup>() };
        var read = JsonSerializer.Deserialize<ChapterTreePlan>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(catalog.Source, read.ReferenceCatalog!.Source);
        Assert.Equal(catalog.PageTitle, read.ReferenceCatalog.PageTitle);
        Assert.NotNull(read.ConfirmedReviews);
    }

    [Fact]
    public async Task Cleanup_conversion_produces_backed_up_line_evidence_without_optional_validation()
    {
        var folder = Path.Combine(Path.GetTempPath(), "easypub-evidence-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var source = Path.Combine(folder, "source.txt");
        await File.WriteAllTextAsync(source, "第一章 起点\n正文\u200b内容\n");
        var bytes = await File.ReadAllBytesAsync(source);
        try
        {
            await new EasyPubConverter().ConvertAsync(new(source, Path.Combine(folder, "book.epub"), Options: new()
            { TextCleanup = new() { RemoveInvisibleCharacters = true } }));
            var report = Assert.Single(Directory.GetFiles(Path.Combine(folder, "EasyPub-变更记录"), "*.json"));
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(report));
            Assert.Equal("制作完成", json.RootElement.GetProperty("State").GetString());
            Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("OutputSha256").GetString()));
            var evidence = json.RootElement.GetProperty("Evidence");
            Assert.Equal(bytes, await File.ReadAllBytesAsync(evidence.GetProperty("BackupPath").GetString()!));
            Assert.True(evidence.GetProperty("CleanupChanges").GetArrayLength() > 0);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(source));
        }
        finally { Directory.Delete(folder, true); }
    }
}
