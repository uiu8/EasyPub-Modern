using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class DuplicateOutputRoundtripTests
{
    private const string FirstBody = "旅人沿着河流继续前进，终于发现远方城堡的灯光，夜幕渐渐降临。";
    private const string NextBody = "第二章独有的正文，灯火已经熄灭，归来的人将行囊放在门边。";

    [Theory]
    [InlineData(false, false)] // Full copy, keep first.
    [InlineData(false, true)]  // Full copy, keep second.
    [InlineData(true, false)]  // Empty heading, local evidence.
    [InlineData(true, true)]   // Empty heading, reference evidence.
    public async Task Repair_project_reopen_and_epub_preserve_exact_body_sequence(bool emptyHeading, bool alternative)
    {
        var root = Path.Combine(Environment.GetEnvironmentVariable("EASYPUB_DUPLICATE_OUTPUT_EVIDENCE") ?? Path.GetTempPath(),
            $"duplicate-output-{emptyHeading}-{alternative}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "原稿.txt");
        var text = $"第一章 转折\n{(emptyHeading ? "　 " : FirstBody)}\n第一章 转折\n{FirstBody}\n第二章 后续\n{NextBody}";
        await File.WriteAllTextAsync(input, text);
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(input));
        var document = await ChapterTreeDocument.LoadAsync(input);
        // Explicit recognition result: both title occurrences were recognized.
        document = document.WithEntries([
            new("a", "第一章 转折", 1, true, 1, [new(2, 2)]),
            new("b", "第一章 转折", 1, true, 3, [new(4, 4)]),
            new("c", "第二章 后续", 1, true, 5, [new(6, 6)])]);
        IReadOnlyList<ChapterTreeEntry> repaired;
        if (emptyHeading)
        {
            var proposal = alternative
                ? ReferencePlanner.Build(document, document.Entries, ReferenceCatalogInput.ParseText("第一章 转折\n第二章 后续")!)
                : SelfRepairPlanner.Build(document);
            var actions = proposal.Actions.Where(a => a.Kind == ReferenceActionKind.RemoveDuplicate).ToArray();
            Assert.NotEmpty(actions);
            Assert.All(actions, a => Assert.True(a.Recommended));
            var dropped = new List<int>();
            repaired = ReferencePlanner.Apply(document, document.Entries, proposal, actions, true, false, dropped);
            Assert.All(dropped, line => Assert.True(line == 1 || string.IsNullOrWhiteSpace(document.SourceLine(line)?.Text)));
        }
        else
        {
            repaired = ChapterContentDuplicates.RemoveCopy(document, document.Entries,
                alternative ? "a" : "b", alternative ? "b" : "a");
        }
        Assert.Equal(new[] { "第一章 转折", "第二章 后续" }, repaired.Select(e => e.Title));
        var plan = document.CreatePlan(repaired);
        var projectPath = Path.Combine(root, "修复项目.easypubproj");
        var project = new EasyPubProjectDocument(EasyPubProjectDocument.CurrentSchemaVersion, projectPath, root,
            ConversionProfile.Default, [new EasyPubProjectBook(input, "验收书", null, null, []) { ChapterTree = plan }], DateTimeOffset.Now);
        var store = new EasyPubProjectStore(projectPath);
        await store.SaveAsync(project);
        var saved = Assert.Single((await store.LoadAsync()).Books).ChapterTree!;
        var reopened = await ChapterTreeDocument.LoadAsync(input, existingPlan: saved);
        Assert.Equal(repaired.Select(e => e.Id), reopened.Entries.Select(e => e.Id));
        Assert.Equal(new[] { FirstBody, NextBody }, reopened.Entries.SelectMany(reopened.GetSourceLines)
            .Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)));
        var output = Path.Combine(root, "成品.epub");
        var result = await new EasyPubConverter().ConvertAsync(new(input, output, Options: ConversionOptions.LegacyDefault with
        { ChapterPattern = "^不应重新识别原稿$", AddFullWidthIndent = false }) { ChapterTree = saved });
        Assert.Equal(2, result.ChapterCount);
        using var zip = ZipFile.OpenRead(output);
        var paragraphs = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            using var stream = zip.GetEntry($"OEBPS/chapter{i}.html")!.Open();
            using var reader = XmlReader.Create(stream, new() { DtdProcessing = DtdProcessing.Ignore });
            var html = XDocument.Load(reader);
            Assert.Equal(repaired[i].Title, html.Descendants().Single(e => (string?)e.Attribute("id") == "title").Value.Trim());
            paragraphs.AddRange(html.Descendants().Where(e => e.Name.LocalName == "p" && (string?)e.Attribute("class") == "a")
                .Select(e => e.Value.Trim()).Where(t => t.Length > 0));
        }
        Assert.Equal(new[] { FirstBody, NextBody }, paragraphs);
        Assert.Equal(originalHash, SHA256.HashData(await File.ReadAllBytesAsync(input)));
    }
}
