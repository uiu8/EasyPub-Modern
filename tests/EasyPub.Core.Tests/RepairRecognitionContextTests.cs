using System.IO.Compression;
using System.Text;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class RepairRecognitionContextTests
{
    private const string Pattern = "^第[一二]章.*$";
    private const string Text = "第一章 起点\n正文一。\n第二章 中途\n正文二。\n第三章 不识别\n正文三。\n";

    [Fact]
    public async Task Rule_only_drift_is_detected_even_when_the_tree_is_identical()
    {
        var document = await Load();
        var snapshot = document.WithEntries(document.Entries);
        Assert.Equal(Pattern, snapshot.ChapterPattern);
        var outcome = ChapterAutoRepair.Prepare(snapshot, ReferenceCatalogInput.ParseText("第一章 新起点\n第二章 中途"));
        var proposal = ChapterRepairApplier.ProposalFor(outcome, snapshot)!;
        Assert.True(RepairPlanValidator.Validate(proposal.BaseVersion, snapshot, snapshot.Entries).IsValid);
        var changed = ChapterTreeDocument.Load(document.SourcePath, await File.ReadAllBytesAsync(document.SourcePath),
            chapterPattern: "^(?:第[一二]章).*$");
        Assert.Equal(RepairVersionDrift.Recognition,
            RepairPlanValidator.Validate(proposal.BaseVersion, changed, changed.Entries).Drift);
    }

    [Fact]
    public async Task Returning_to_default_pattern_does_not_revive_previous_custom_rule()
    {
        var custom = await Load();
        var document = await ChapterTreeDocument.LoadAsync(custom.SourcePath);
        var state = new SourceTransitionState(document.SourcePath, SourceTextDocument.Load(document.SourcePath),
            document, document.CreatePlan(document.Entries), null, null, null)
            { ReloadChapterPattern = Pattern };
        Assert.Null(state.WithoutRecognitionTree().ReloadChapterPattern);
        await File.AppendAllTextAsync(document.SourcePath, "新增正文。\n");
        var result = await SourceVersionTransition.AfterExternalEditAsync(state, document.SourcePath);
        Assert.True(result.Succeeded, result.Message);
        Assert.Null(result.State.RecognitionTree!.ChapterPattern);
        Assert.Contains(result.State.RecognitionTree.Entries, e => e.Title == "第三章 不识别");
    }

    [Fact]
    public async Task Saved_plan_reopens_with_its_rules_over_current_defaults()
    {
        var document = await Load();
        var plan = document.CreatePlan(document.Entries);
        var reloaded = ChapterTreeDocument.Load(
            document.SourcePath,
            await File.ReadAllBytesAsync(document.SourcePath),
            chapterPattern: "^完全不同$",
            hierarchy: new TocHierarchyOptions { Enabled = false, NumericHeadingMinimumBodyLines = 1 },
            existingPlan: plan);

        Assert.Equal(Pattern, reloaded.ChapterPattern);
        Assert.Equal(document.RecognitionOptions, reloaded.RecognitionOptions);
        Assert.Equal(document.Entries.Select(entry => entry.Title), reloaded.Entries.Select(entry => entry.Title));
    }

    [Fact]
    public async Task Conversion_request_uses_each_book_snapshot_instead_of_global_profile()
    {
        var document = await Load();
        var plan = document.CreatePlan(document.Entries);
        var requests = BatchConversionRequestFactory.Create(
            [new BookConversionSource("book.txt", ChapterTree: plan)],
            "out",
            "epub",
            null,
            new ConversionOptions
            {
                ChapterPattern = "^全局规则$",
                TocHierarchy = new TocHierarchyOptions { Enabled = false },
            });

        var request = Assert.Single(requests);
        Assert.Equal(Pattern, request.RequiredOptions.ChapterPattern);
        Assert.Equal(document.RecognitionOptions, request.RequiredOptions.TocHierarchy);
    }

    [Fact]
    public async Task Converted_structure_follows_the_saved_tree_when_global_rules_disagree()
    {
        var document = await Load();
        var entries = document.Entries.ToArray();
        entries[1] = entries[1] with { Title = "第一章 成品标题" };
        var plan = document.CreatePlan(entries);
        var outputDirectory = Path.Combine(Path.GetTempPath(), "easypub-tree-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var request = Assert.Single(BatchConversionRequestFactory.Create(
                [new BookConversionSource(document.SourcePath, ChapterTree: plan)],
                outputDirectory,
                "epub",
                null,
                new ConversionOptions { ChapterPattern = "^全局规则$" }));

            var result = await new EasyPubConverter().ConvertAsync(request);
            Assert.Equal(plan.Entries.Count, result.ChapterCount);
            using var archive = ZipFile.OpenRead(request.OutputPath);
            Assert.Contains("第一章 成品标题", ReadText(archive, "OEBPS/chapter1.html"));
            Assert.Contains("正文一", ReadText(archive, "OEBPS/chapter1.html"));
            Assert.Contains("第二章 中途", ReadText(archive, "OEBPS/chapter2.html"));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task External_reload_preserves_custom_pattern_and_hierarchy_options()
    {
        var document = await Load();
        Assert.DoesNotContain(document.Entries, e => e.Title == "第三章 不识别");
        var before = new SourceTransitionState(document.SourcePath, SourceTextDocument.Load(document.SourcePath),
            document, document.CreatePlan(document.Entries), null, null, null)
            { LoadedHash = document.SourceSha256, RenderedHash = document.SourceSha256 };
        await File.AppendAllTextAsync(document.SourcePath, "新增正文。\n");
        var result = await SourceVersionTransition.AfterExternalEditAsync(before, document.SourcePath);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(Pattern, result.State.RecognitionTree!.ChapterPattern);
        Assert.Equal(document.RecognitionOptions, result.State.RecognitionTree.RecognitionOptions);
        Assert.DoesNotContain(result.State.RecognitionTree.Entries, e => e.Title == "第三章 不识别");
    }

    private static async Task<ChapterTreeDocument> Load()
    {
        var path = Path.Combine(Path.GetTempPath(), "easypub-recognition-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, Text);
        return await ChapterTreeDocument.LoadAsync(path, chapterPattern: Pattern,
            hierarchy: new TocHierarchyOptions { NumericHeadingMinimumBodyLines = 9, HeadingNumberCorrections = "久=九" });
    }

    private static string ReadText(ZipArchive archive, string path)
    {
        using var reader = new StreamReader(archive.GetEntry(path)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
