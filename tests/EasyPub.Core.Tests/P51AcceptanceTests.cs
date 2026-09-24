using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// P5-1 的 S5/S6 端到端验收：人工边界必须逐项确认，真缺正文不能被目录伪造。
/// 转换出口在设置 EASYPUB_TEST_KINDLING 时统一走实际 Kindling；未设置时仍保留结构链路，
/// 便于普通 CI 在没有外部引擎的机器上运行核心测试。
/// </summary>
public sealed class P51AcceptanceTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public Task S5_manual_boundaries_roundtrip_and_convert(int modeNumber) => RunS5(modeNumber);

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public Task S6_missing_body_stays_uncreated_after_reopen_and_convert(int modeNumber) => RunS6(modeNumber);

    [Theory]
    [InlineData("S7", 1)] [InlineData("S7", 2)] [InlineData("S7", 3)] [InlineData("S7", 4)]
    [InlineData("S8", 1)] [InlineData("S8", 2)] [InlineData("S8", 3)] [InlineData("S8", 4)]
    [InlineData("S9", 1)] [InlineData("S9", 2)] [InlineData("S9", 3)] [InlineData("S9", 4)]
    public Task S7_to_S9_confirmed_candidates_roundtrip_and_convert(string scene, int modeNumber) =>
        RunCandidate(scene, modeNumber);

    private static async Task RunS5(int modeNumber)
    {
        var (root, fixture, source, original, truth) = await Open("S5");
        var reference = modeNumber is 2 or 4;
        var editSource = modeNumber is 3 or 4;
        var document = await ChapterTreeDocument.LoadAsync(source, hierarchy: Hierarchy());
        if (reference) document = ApplyReferenceDefaults(document, fixture);

        var initialSource = File.ReadAllBytes(source);
        var missing = truth.RootElement.GetProperty("chapters").EnumerateArray()
            .Where(c => c.GetProperty("missing_heading").GetBoolean())
            .ToArray();
        Assert.Equal(11, missing.Length);
        foreach (var chapter in missing)
        {
            var id = chapter.GetProperty("id").GetString()!;
            var title = chapter.GetProperty("title").GetString()!;
            var instance = truth.RootElement.GetProperty("instances").EnumerateArray()
                .Single(i => i.GetProperty("id").GetString() == id);
            var body = TruthBody(fixture, instance);
            var line = FindBodyStart(document, body);
            var owner = document.Entries.Single(e => e.ContentRanges.Any(r => line >= r.StartLine && line <= r.EndLine));
            if (editSource)
            {
                var result = await ManualSplitCopy.Prepare(document, owner.Id, line, title)
                    .ExportAsync(new SourceBackupStore(Path.Combine(root, "backups")));
                Assert.True(result.Succeeded, result.Transaction.Message);
                var project = await new EasyPubProjectStore(result.ProjectPath).LoadAsync();
                var book = Assert.Single(project.Books);
                document = await ChapterTreeDocument.LoadAsync(book.InputPath, existingPlan: book.ChapterTree);
            }
            else
            {
                var split = ChapterContentSplit.At(owner.ContentRanges, line);
                Assert.NotNull(split);
                var entries = document.Entries.ToList();
                var index = entries.FindIndex(e => e.Id == owner.Id);
                entries[index] = owner with { ContentRanges = split!.Before };
                entries.Insert(index + 1, owner with
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Title = title,
                    TitleLineNumber = null,
                    ContentRanges = split.After,
                    RecognitionSource = "manual",
                    IncludeInToc = true,
                });
                RepairIntegrity.Verify(document.Entries, entries, []);
                document = document.WithEntries(entries);
            }
        }

        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.Equal(initialSource, File.ReadAllBytes(source));
        var reopened = await SaveAndReopen(document, root, "S5");
        var chapters = reopened.Entries.Where(e => !e.IsFrontMatter && e.Level == 2).ToArray();
        Assert.Equal(690, chapters.Length);
        AssertTruthSequence(reopened, truth, missingBody: false);
        await ConvertWithKindlingIfConfigured(reopened, root);
    }

    private static async Task RunS6(int modeNumber)
    {
        var (root, fixture, source, original, truth) = await Open("S6");
        var reference = modeNumber is 2 or 4;
        var document = await ChapterTreeDocument.LoadAsync(source, hierarchy: Hierarchy());
        if (reference) document = ApplyReferenceDefaults(document, fixture);

        var catalog = ReferenceCatalogInput.ParseText(PlainCatalog(File.ReadAllText(Path.Combine(fixture, "参考目录.txt"))));
        var proposal = ChapterRepairApplier.ProposalFor(ChapterAutoRepair.Prepare(document, catalog), document);
        Assert.NotNull(proposal);
        Assert.Equal(11, proposal!.Actions.Count(a => a.Kind == ReferenceActionKind.MissingBody));
        Assert.All(proposal.Actions.Where(a => a.Kind == ReferenceActionKind.MissingBody), a => Assert.False(a.Recommended));
        Assert.DoesNotContain(proposal.Actions, a => a.Kind == ReferenceActionKind.AddChapter);

        var reopened = await SaveAndReopen(document, root, "S6");
        var chapters = reopened.Entries.Where(e => !e.IsFrontMatter && e.Level == 2).ToArray();
        Assert.Equal(679, chapters.Length);
        Assert.All(chapters, chapter => Assert.Contains(reopened.GetSourceLines(chapter), line => !string.IsNullOrWhiteSpace(line.Text)));
        AssertTruthSequence(reopened, truth, missingBody: true);
        Assert.Equal(original, File.ReadAllBytes(source));
        await ConvertWithKindlingIfConfigured(reopened, root);
    }

    private static async Task RunCandidate(string scene, int modeNumber)
    {
        var (root, fixture, source, original, truth) = await Open(scene);
        var reference = modeNumber is 2 or 4;
        var editSource = modeNumber is 3 or 4;
        var document = await ChapterTreeDocument.LoadAsync(source, hierarchy: Hierarchy());
        if (reference)
        {
            var catalog = ReferenceCatalogInput.ParseText(PlainCatalog(File.ReadAllText(Path.Combine(fixture, "参考目录.txt"))))!;
            var outcome = ChapterAutoRepair.Prepare(document, catalog);
            var proposal = ChapterRepairApplier.ProposalFor(outcome, document);
            Assert.NotNull(proposal);
            var candidates = proposal!.Actions.Where(a => a.Kind != ReferenceActionKind.VolumeNote && a.Recommended).ToArray();
            Assert.Equal(scene switch { "S7" => 6, "S8" => 6, "S9" => 1, _ => 0 }, candidates.Length);
            var decisions = ChapterRepairApplier.DecisionsFor(proposal,
                editSource ? RepairLandingMode.EditSource : RepairLandingMode.TreeOnly, candidates);
            var applier = new ChapterRepairApplier(Path.Combine(root, "backups"));
            var preview = applier.Preview(proposal, document, decisions,
                editSource ? RepairLandingMode.EditSource : RepairLandingMode.TreeOnly, buildVolumeLevels: true);
            Assert.True(preview.CanApply, preview.Message);
            var applied = await applier.ApplyAsync(proposal, document, decisions,
                editSource ? RepairLandingMode.EditSource : RepairLandingMode.TreeOnly, buildVolumeLevels: true);
            Assert.False(applied.NeedsAttention, applied.Message);
            if (applied.Changed)
            {
                document = applied.State?.Plan is { } plan
                    ? await ChapterTreeDocument.LoadAsync(source, existingPlan: plan)
                    : document.WithEntries(applied.Compilation!.RebuiltEntries);
            }
            Assert.Equal(original, File.ReadAllBytes(fixture + "\\缺陷样书.txt"));
        }

        var reopened = await SaveAndReopen(document, root, scene);
        var chapters = reopened.Entries.Where(e => !e.IsFrontMatter && e.Level == 2).ToArray();
        if (reference)
        {
            Assert.Equal(690, chapters.Length);
            Assert.Equal(truth.RootElement.GetProperty("chapters").EnumerateArray().Select(c => c.GetProperty("title").GetString()),
                chapters.Select(c => c.Title));
        }
        AssertTruthBodiesPresent(reopened, truth);
        Assert.Equal(original, File.ReadAllBytes(fixture + "\\缺陷样书.txt"));
        await ConvertWithKindlingIfConfigured(reopened, root);
    }

    private static TocHierarchyOptions Hierarchy() => new() { Enabled = true };

    private static ChapterTreeDocument ApplyReferenceDefaults(ChapterTreeDocument document, string fixture)
    {
        var catalog = ReferenceCatalogInput.ParseText(PlainCatalog(File.ReadAllText(Path.Combine(fixture, "参考目录.txt"))))!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        var proposal = ChapterRepairApplier.ProposalFor(outcome, document)!;
        var entries = ReferencePlanner.Apply(document, document.Entries, proposal.Plan,
            proposal.Plan.DefaultSelection.ToArray(), useReferenceTitles: true, buildVolumeLevels: true);
        return document.WithEntries(entries);
    }

    private static async Task<ChapterTreeDocument> SaveAndReopen(ChapterTreeDocument document, string root, string scene)
    {
        var projectPath = Path.Combine(root, scene + "-验收.easypubproj");
        var plan = document.CreatePlan(document.Entries);
        await new EasyPubProjectStore(projectPath).SaveAsync(new EasyPubProjectDocument(
            EasyPubProjectDocument.CurrentSchemaVersion, projectPath, root, ConversionProfile.Default,
            [new EasyPubProjectBook(document.SourcePath, scene + "验收", null, null, []) { ChapterTree = plan }], DateTimeOffset.Now));
        var saved = Assert.Single((await new EasyPubProjectStore(projectPath).LoadAsync()).Books);
        return await ChapterTreeDocument.LoadAsync(saved.InputPath, existingPlan: saved.ChapterTree);
    }

    private static void AssertTruthSequence(ChapterTreeDocument document, JsonDocument truth, bool missingBody)
    {
        var expected = truth.RootElement.GetProperty("chapters").EnumerateArray()
            .Where(c => !missingBody || !c.GetProperty("missing_body").GetBoolean())
            .ToArray();
        var actual = document.Entries.Where(e => !e.IsFrontMatter && e.Level == 2).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].GetProperty("title").GetString(), actual[i].Title);
            var body = string.Join('\n', document.GetSourceLines(actual[i]).Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
            Assert.Equal(expected[i].GetProperty("body_sha256").GetString(), Hash(Encoding.UTF8.GetBytes(body)));
        }
    }

    private static void AssertTruthBodiesPresent(ChapterTreeDocument document, JsonDocument truth)
    {
        var lines = document.Entries.SelectMany(document.GetSourceLines)
            .Select(line => line.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        var expected = truth.RootElement.GetProperty("chapters").EnumerateArray()
            .Select(chapter => chapter.GetProperty("body_sha256").GetString()!).ToArray();
        foreach (var chapter in expected)
        {
            var found = 0;
            for (var i = 0; i + 2 < lines.Length; i++)
            {
                var hash = Hash(Encoding.UTF8.GetBytes(string.Join('\n', lines.Skip(i).Take(3))));
                if (hash == chapter) found++;
            }
            Assert.Equal(1, found);
        }
    }

    private static async Task ConvertWithKindlingIfConfigured(ChapterTreeDocument document, string root)
    {
        var engine = Environment.GetEnvironmentVariable("EASYPUB_TEST_KINDLING");
        if (string.IsNullOrWhiteSpace(engine)) return;
        Assert.True(File.Exists(engine), "EASYPUB_TEST_KINDLING 指向的 Kindling 不存在：" + engine);
        var output = Path.Combine(root, "验收-kindling.mobi");
        var options = ConversionOptions.LegacyDefault with
        {
            ChapterPattern = "^不应重新识别$",
            AddFullWidthIndent = false,
            Mobi = new MobiOptions { Engine = KindleConversionEngine.Kindling, KindlingPath = engine },
        };
        var result = await new EasyPubConverter().ConvertAsync(new(document.SourcePath, output, Options: options)
        { ChapterTree = document.CreatePlan(document.Entries) });
        // The converter deliberately counts volume nodes in its output progress. The separate
        // assertions above count actual Level-2 body chapters (690 for S5, 679 for S6).
        Assert.Equal(document.Entries.Count, result.ChapterCount);
        var bytes = await File.ReadAllBytesAsync(output);
        Assert.Equal("BOOKMOBI", Encoding.ASCII.GetString(bytes, 60, 8));
        Assert.True(bytes.Length > 1024);
        Assert.NotEmpty(Directory.GetFiles(root, "*.kindling-*.log"));
    }

    private static async Task<(string Root, string Fixture, string Source, byte[] Original, JsonDocument Truth)> Open(string scene)
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-p51-" + scene + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = Fixture(scene);
        var source = Path.Combine(root, "工作副本.txt");
        File.Copy(Path.Combine(fixture, "缺陷样书.txt"), source);
        return (root, fixture, source, await File.ReadAllBytesAsync(source),
            JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fixture, "真值清单.json"))));
    }

    private static string Fixture(string scene)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "samples", "章节独立场景"))) root = root.Parent;
        return Path.Combine(root!.FullName, "samples", "章节独立场景", scene);
    }

    private static string PlainCatalog(string text) => string.Join('\n', text.Split('\n')
        .Select(line => line.StartsWith("# ", StringComparison.Ordinal) ? line[2..] : line));

    private static string[] TruthBody(string fixture, JsonElement instance)
    {
        var lines = File.ReadAllLines(Path.Combine(fixture, "缺陷样书.txt"));
        var start = instance.GetProperty("body_start_line").GetInt32();
        var end = instance.GetProperty("body_end_line").GetInt32();
        return lines[(start - 1)..end];
    }

    private static int FindBodyStart(ChapterTreeDocument document, IReadOnlyList<string> body)
    {
        for (var line = 1; line <= document.LineCount - body.Count + 1; line++)
            if (body.Select((text, offset) => document.SourceLine(line + offset)?.Text == text).All(match => match)) return line;
        throw new InvalidDataException("验收真值中的正文边界在当前副本中找不到。");
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
