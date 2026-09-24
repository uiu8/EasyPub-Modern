using EasyPub.Core;

namespace EasyPub.Core.Tests;

public class ChapterRepairSessionTests
{
    [Fact]
    public async Task Only_the_previewed_decisions_can_be_submitted_once()
    {
        var (session, document) = await Open();
        var proposal = ChapterRepairApplier.ProposalFor(session.Outcome, document)!;
        var choices = ChapterRepairApplier.DecisionsFor(proposal, RepairLandingMode.TreeOnly,
            proposal.Actions.Where(a => a.Kind == ReferenceActionKind.RemoveDuplicate).ToArray()).ToArray();
        Assert.Contains(choices, d => d.Selected);
        Assert.False((await session.ApplyAsync(RepairLandingMode.TreeOnly, choices)).Changed);
        Assert.True(session.Preview(RepairLandingMode.TreeOnly, choices).CanApply);
        var approved = choices.ToArray();
        choices[0] = choices[0] with { Selected = !choices[0].Selected };
        Assert.False((await session.ApplyAsync(RepairLandingMode.TreeOnly, choices)).Changed);
        Assert.False((await session.ApplyAsync(RepairLandingMode.EditSource, approved)).Changed);
        Assert.True((await session.ApplyAsync(RepairLandingMode.TreeOnly, approved)).Changed);
        Assert.False((await session.ApplyAsync(RepairLandingMode.TreeOnly, approved)).Changed);
    }

    [Fact]
    public async Task A_changed_source_blocks_a_previously_valid_preview()
    {
        var (session, document) = await Open();
        var proposal = ChapterRepairApplier.ProposalFor(session.Outcome, document)!;
        var choices = ChapterRepairApplier.DecisionsFor(proposal, RepairLandingMode.TreeOnly, proposal.Actions.ToArray());
        Assert.True(session.Preview(RepairLandingMode.TreeOnly, choices).CanApply);
        await File.AppendAllTextAsync(document.SourcePath, "外部新增正文\n");
        var expected = await File.ReadAllBytesAsync(document.SourcePath);
        Assert.False((await session.ApplyAsync(RepairLandingMode.TreeOnly, choices)).Changed);
        Assert.Equal(expected, await File.ReadAllBytesAsync(document.SourcePath));
    }

    [Fact]
    public async Task Local_basis_does_not_adopt_an_acquired_catalog()
    {
        var (session, _) = await Open();
        Assert.False(session.Outcome.CatalogFound);
        Assert.Null(session.Outcome.Catalog);
        Assert.Empty(session.Outcome.CatalogTimings);
    }

    private static async Task<(ChapterRepairSession, ChapterTreeDocument)> Open()
    {
        var root = Path.Combine(Path.GetTempPath(), "easypub-session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "book.txt");
        await File.WriteAllTextAsync(path, "第一章 起点\n正文保留。\n第一章 起点\n第二章 继续\n后续正文。\n");
        var document = await ChapterTreeDocument.LoadAsync(path);
        var ignored = ReferenceCatalogInput.ParseText("第一章 不应采用\n第二章 也不采用")!;
        var session = await ChapterRepairSession.AnalyzeAsync(document, new CurrentTreeHeuristicRequest(),
            new ChapterRepairApplier(Path.Combine(root, "backups")), acquisition: CatalogAcquisition.Given(ignored));
        return (session, document);
    }
}
