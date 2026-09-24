using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class P31AcceptanceTests
{
    [Theory]
    [InlineData("S1", 1)] [InlineData("S1", 2)] [InlineData("S1", 3)] [InlineData("S1", 4)]
    [InlineData("S2", 1)] [InlineData("S2", 2)] [InlineData("S2", 3)] [InlineData("S2", 4)]
    [InlineData("S4", 1)] [InlineData("S4", 2)] [InlineData("S4", 3)] [InlineData("S4", 4)]
    public Task Twelve_cells_preserve_volume_body_and_output(string scene, int modeNumber) => Verify(scene, modeNumber);

    internal static async Task Verify(string scene, int modeNumber)
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo != null && !Directory.Exists(Path.Combine(repo.FullName, "samples", "章节独立场景"))) repo = repo.Parent;
        Assert.NotNull(repo);
        var fixture = Path.Combine(repo.FullName, "samples", "章节独立场景", scene);
        var root = Path.Combine(repo.FullName, "work", scene == "S3" ? "p3-2-acceptance" : "p3-1-acceptance", scene + "-M" + modeNumber + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var watch = Stopwatch.StartNew();
        var original = await File.ReadAllBytesAsync(Path.Combine(fixture, "缺陷样书.txt"));
        using var truth = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "真值清单.json")));
        var expected = truth.RootElement.GetProperty("chapters").EnumerateArray().ToArray();
        foreach (var pair in truth.RootElement.GetProperty("hashes").EnumerateObject())
            Assert.Equal(pair.Value.GetString(), Hash(await File.ReadAllBytesAsync(Path.Combine(fixture, pair.Name))));
        var path = Path.Combine(root, "工作副本.txt");
        await File.WriteAllBytesAsync(path, original);
        var doc = await ChapterTreeDocument.LoadAsync(path, hierarchy: new TocHierarchyOptions { Enabled = true });
        var reference = modeNumber % 2 == 0;
        var mode = modeNumber >= 3 ? RepairLandingMode.EditSource : RepairLandingMode.TreeOnly;
        IReadOnlyList<ChapterTreeEntry> entries;
        RepairApplicationResult? applied = null;
        if (scene == "S3" && !reference)
        {
            // Simulate a user's explicit selection and destination, never infer these from chapter numbers.
            var selected = Enumerable.Range(20, 21).Select(n => doc.Entries.Single(e => e.Title == $"第{n}章 卷2的旅程{n}").Id).ToArray();
            var target = doc.Entries.Single(e => e.Title == "第41章 卷2的旅程41");
            var preview = ChapterTreeRelocation.Before(doc, selected, target.Id);
            Assert.Equal(21, preview.MovedEntryIds.Count);
            entries = preview.Entries;
            await Save("preview.json", preview);
        }
        else if (scene == "S1" && !reference)
        {
            // User explicitly accepts the number-group preview; inferred names never enter TXT.
            var recovery = ChapterStructureRecovery.Analyze(doc);
            Assert.Equal(6, recovery.Groups);
            entries = recovery.Entries;
            await Save("preview.json", recovery);
        }
        else
        {
            var catalog = reference ? ReferenceCatalogInput.ParseText(await File.ReadAllTextAsync(Path.Combine(fixture, "参考目录.txt"))) : null;
            var outcome = ChapterAutoRepair.Prepare(doc, catalog);
            var proposal = ChapterRepairApplier.ProposalFor(outcome, doc)!;
            if (reference && scene != "S1")
            {
                Assert.DoesNotContain(proposal.Actions, a => a.Kind == ReferenceActionKind.DemoteExtra && a.Recommended);
                var defaults = ReferencePlanner.Apply(doc, doc.Entries, proposal.Plan,
                    proposal.Plan.DefaultSelection.ToArray(), true, true);
                Assert.Equal(6, defaults.Count(e => e.Level == 1 && !e.IsFrontMatter));
                Assert.Equal(690, defaults.Count(e => e.Level == 2 && !e.IsFrontMatter));
            }
            // Full body deletion is an explicit reviewed choice, never a recommendation by number alone.
            var decisions = proposal.Actions.Select(a => new RepairDecision(RepairActionIdentity.Compute(a),
                scene == "S1" ? proposal.Plan.DefaultSelection.Contains(a) : a.Kind == ReferenceActionKind.RemoveDuplicate,
                SourceEffectPolicies.Decide(a.Kind, mode, deleteDuplicateFromSource: true))).ToArray();
            await Save("proposal.json", proposal);
            await Save("decisions.json", decisions);
            var applier = new ChapterRepairApplier(Path.Combine(root, "backups"));
            var preview = applier.Preview(proposal, doc, decisions, mode, buildVolumeLevels: reference);
            await Save("preview.json", preview);
            Assert.True(preview.CanApply, preview.Message);
            applied = await applier.ApplyAsync(proposal, doc, decisions, mode, buildVolumeLevels: reference);
            await Save("application.json", new { applied.Changed, applied.Message, applied.NeedsAttention, applied.Transaction, removedLines = applied.Compilation?.RemovedLines });
            Assert.False(applied.NeedsAttention, applied.Message);
            entries = applied.Changed ? applied.Transaction == null ? applied.Compilation!.RebuiltEntries : applied.State!.Plan!.Entries : doc.Entries;
            if (scene != "S1") Assert.Equal(scene == "S3" ? 0 : 11, decisions.Count(d => d.Selected && proposal.Actions.Single(a => RepairActionIdentity.Compute(a) == d.ActionId).Kind == ReferenceActionKind.RemoveDuplicate));
        }
        var after = ChapterTreeDocument.Load(path, await File.ReadAllBytesAsync(path)).WithEntries(entries);
        var projectPath = Path.Combine(root, "修复项目.easypubproj");
        var store = new EasyPubProjectStore(projectPath);
        await store.SaveAsync(new(EasyPubProjectDocument.CurrentSchemaVersion, projectPath, root, ConversionProfile.Default,
            [new EasyPubProjectBook(path, "P3-1验收", null, null, []) { ChapterTree = after.CreatePlan(entries) }], DateTimeOffset.Now));
        var saved = Assert.Single((await store.LoadAsync()).Books).ChapterTree!;
        var reopened = await ChapterTreeDocument.LoadAsync(path, existingPlan: saved);
        Assert.Equal(entries.Select(e => e.Id), reopened.Entries.Select(e => e.Id));
        var chapters = reopened.Entries.Where(e => e.Level == 2 && !e.IsFrontMatter).ToArray();
        Assert.Equal(690, chapters.Length);
        var volumes = reopened.Entries.Where(e => e.Level == 1).ToArray();
        Assert.Equal(6, volumes.Length);
        var actualVolumes = new List<int>();
        var volume = 0;
        foreach (var entry in reopened.Entries.Where(e => !e.IsFrontMatter))
            if (entry.Level == 1) volume++; else actualVolumes.Add(volume);
        Assert.Equal(expected.Select(c => c.GetProperty("volume").GetInt32()), actualVolumes);
        if (scene == "S1" && !reference)
            Assert.All(volumes, v => Assert.Contains("卷名待核对", v.Title));
        else Assert.Equal(Enumerable.Range(1, 6).Select(n => $"第{n}卷 旅程{n}"), volumes.Select(v => v.Title));
        var bodyHashes = new List<string>();
        for (var i = 0; i < chapters.Length; i++)
        {
            Assert.Equal(expected[i].GetProperty("title").GetString(), chapters[i].Title);
            var body = reopened.GetSourceLines(chapters[i]).Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t));
            var hash = Hash(Encoding.UTF8.GetBytes(string.Join('\n', body)));
            Assert.Equal(expected[i].GetProperty("body_sha256").GetString(), hash);
            bodyHashes.Add(hash);
        }
        Assert.Equal(690, bodyHashes.Distinct().Count());
        var output = Path.Combine(root, "成品.epub");
        await new EasyPubConverter().ConvertAsync(new(path, output, Options: ConversionOptions.LegacyDefault with
        { ChapterPattern = "^不应重新识别$", AddFullWidthIndent = false }) { ChapterTree = saved });
        using (var zip = ZipFile.OpenRead(output))
        {
            var outputBodies = new List<string>();
            foreach (var item in zip.Entries.Where(e => e.FullName.StartsWith("OEBPS/chapter") && e.FullName.EndsWith(".html"))
                .OrderBy(e => int.Parse(Path.GetFileNameWithoutExtension(e.FullName)[7..])))
            {
                using var stream = item.Open();
                using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Ignore });
                var html = XDocument.Load(reader);
                var lines = html.Descendants().Where(e => e.Name.LocalName == "p" && (string?)e.Attribute("class") == "a")
                    .Select(e => e.Value.Trim()).Where(t => t.Length > 0).ToArray();
                if (lines.Length > 0) outputBodies.Add(Hash(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
            }
            Assert.Equal(bodyHashes, outputBodies);
        }
        await VerifyKindlingAsync(path, saved, root, chapters.Length);
        if (mode == RepairLandingMode.TreeOnly || scene == "S1") Assert.Equal(original, await File.ReadAllBytesAsync(path));
        else
        {
            Assert.Equal(SourceEditOutcome.Committed, applied!.Transaction!.Outcome);
            Assert.Equal(original, await File.ReadAllBytesAsync(applied.Transaction.BackupPath!));
            Assert.NotEmpty(applied.Compilation!.RemovedLines);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(root, "backups"), RepairExecutionManifestStore.FileName, SearchOption.AllDirectories));
            await Save("deleted-lines.json", applied.Compilation.RemovedLines.Select(n => new { line = n, text = doc.SourceLine(n)!.Text }));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(Path.Combine(fixture, "缺陷样书.txt")));
        await Save("accepted.json", new { scene, modeNumber, reference, mode, doc.RecognitionOptions, doc.ChapterPattern,
            originalSha = Hash(original), afterSha = Hash(await File.ReadAllBytesAsync(path)), chapters = chapters.Length,
            volumes = volumes.Select(v => v.Title), bodyHashes, elapsedMs = watch.ElapsedMilliseconds,
            limitation = "Core production path and project/EPUB verified; UI evidence tracked separately. S1 volume nodes are tree-only in all modes." });
        async Task Save(string name, object value) => await File.WriteAllTextAsync(Path.Combine(root, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task VerifyKindlingAsync(string inputPath, ChapterTreePlan plan, string root, int expectedChapters)
    {
        var engine = Environment.GetEnvironmentVariable("EASYPUB_TEST_KINDLING");
        if (string.IsNullOrWhiteSpace(engine)) return;
        Assert.True(File.Exists(engine), "EASYPUB_TEST_KINDLING 指向的 Kindling 不存在：" + engine);
        var output = Path.Combine(root, "成品-kindling.mobi");
        var options = ConversionOptions.LegacyDefault with
        {
            ChapterPattern = "^不应重新识别$",
            AddFullWidthIndent = false,
            Mobi = new MobiOptions
            {
                Engine = KindleConversionEngine.Kindling,
                KindlingPath = engine,
                StripSourceArchive = true,
                EpubInputMode = EpubInputMode.PreserveOriginal,
            },
        };
        var result = await new EasyPubConverter().ConvertAsync(new(inputPath, output, Options: options) { ChapterTree = plan });
        // The MOBI progress count includes saved volume/front-matter entries. The caller separately
        // asserts the 690 body chapters; here we verify that the exact persisted tree reached Kindling.
        Assert.Equal(plan.Entries.Count, result.ChapterCount);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.True(bytes.Length > 1024);
        Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(bytes, 60, 8));
        Assert.NotEmpty(Directory.GetFiles(root, "*.kindling-*.log"));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}


