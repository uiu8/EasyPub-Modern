using System.Text.Json;
using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// P3-3/P3-4 独立场景验收：先验证诊断分类，再验证可执行修复的边界。
/// 这些测试只读 samples，并把所有副本写入临时目录。
/// </summary>
public sealed class P33P34AcceptanceTests
{
    private static string Fixture(string scene)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "samples", "章节独立场景")))
            root = root.Parent;
        return Path.Combine(root!.FullName, "samples", "章节独立场景", scene);
    }

    private static async Task<ChapterTreeDocument> Load(string scene)
    {
        var fixture = Fixture(scene);
        var root = Path.Combine(Path.GetTempPath(), "easypub-p33-p34-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "工作副本.txt");
        File.Copy(Path.Combine(fixture, "缺陷样书.txt"), path);
        return await ChapterTreeDocument.LoadAsync(path, hierarchy: new TocHierarchyOptions
        {
            Enabled = true,
            HeadingNumberCorrections = HeadingTypoRules.Default,
        });
    }

    [Theory]
    [InlineData("S5")]
    [InlineData("S6")]
    public async Task P33_catalog_classifies_missing_heading_and_missing_body_separately(string scene)
    {
        var document = await Load(scene);
        var catalog = ReferenceCatalogInput.ParseText(await File.ReadAllTextAsync(Path.Combine(Fixture(scene), "参考目录.txt")))!;
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        var proposal = ChapterRepairApplier.ProposalFor(outcome, document)!;

        if (scene == "S5")
        {
            // 这 11 章只有正文，没有可以安全冒充标题的行；软件必须留下人工边界入口，
            // 不能把正文第一句自动升级成标题。
            Assert.DoesNotContain(proposal.Actions, action => action.Kind == ReferenceActionKind.AdoptHeading);
            Assert.DoesNotContain(proposal.Actions, action => action.Kind == ReferenceActionKind.AddChapter);
        }
        else
        {
            var missing = proposal.Actions.Where(a => a.Kind == ReferenceActionKind.MissingBody).ToArray();
            Assert.Equal(11, missing.Length);
            Assert.All(missing, action => Assert.False(action.Recommended));
            Assert.DoesNotContain(proposal.Actions, action => action.Kind == ReferenceActionKind.AddChapter);
        }
    }

    [Fact]
    public async Task P33_manual_split_copy_keeps_source_and_reopens_with_inserted_identity()
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-p33-split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "原稿.txt");
        await File.WriteAllTextAsync(source, "第一章 原章\r\n正文甲\r\n正文乙\r\n第二章 后章\r\n正文丙");
        var document = await ChapterTreeDocument.LoadAsync(source);
        var chapter = document.Entries.Single(e => e.Title == "第一章 原章");
        var original = await File.ReadAllBytesAsync(source);
        var prepared = ManualSplitCopy.Prepare(document, chapter.Id, 3, "第一章·后半");
        var result = await prepared.ExportAsync(new SourceBackupStore(Path.Combine(root, "backups")));
        Assert.True(result.Succeeded, result.Transaction.Message);
        Assert.Equal(original, await File.ReadAllBytesAsync(source));
        var project = await new EasyPubProjectStore(result.ProjectPath).LoadAsync();
        var plan = Assert.Single(project.Books).ChapterTree!;
        var reopened = await ChapterTreeDocument.LoadAsync(result.CopyPath, existingPlan: plan);
        Assert.Contains(reopened.Entries, e => e.Title == "第一章·后半" && e.RecognitionSource == "manual");
    }

    [Fact]
    public async Task P34_numeric_titles_are_candidates_and_prose_numbers_are_ignored()
    {
        var document = await Load("S8");
        var editing = await ChapterEditingDocument.LoadAsync(document.SourcePath);
        var candidates = editing.Candidates.Where(e => e.Kind == ChapterCandidateKind.NumericTitle).ToArray();
        Assert.Equal(6, candidates.Length);
        Assert.All(candidates, entry => Assert.True(entry.LineNumber > 0));
        Assert.False(ChapterTitleNormalizer.TryNormalizeNumericTitle("正文里有 001 个例子", out _));
    }

    [Fact]
    public async Task P34_typo_correction_is_reported_without_replacing_body_text()
    {
        var document = await Load("S9");
        var candidates = MissingChapterHeadings.Find(document);
        var typo = Assert.Single(candidates, candidate => candidate.Number == 119);
        Assert.Contains("第一百一十九章", typo.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Entries.SelectMany(entry => entry.ContentRanges)
            .SelectMany(range => Enumerable.Range(range.StartLine, range.EndLine - range.StartLine + 1))
            .Select(line => document.SourceLine(line)?.Text ?? ""), text => text.Contains("天长地九", StringComparison.Ordinal));
    }
}
