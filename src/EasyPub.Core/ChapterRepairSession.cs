namespace EasyPub.Core;

/// <summary>
/// One review of one book snapshot. Acquisition is explicit and stays outside this class.
/// A submission must match the last successful preview, including source-delete permission.
/// Caller serializes review interactions; the interlocked gate prevents duplicate submission.
/// </summary>
public sealed class ChapterRepairSession
{
    private readonly ChapterTreeDocument _document;
    private readonly ChapterRepairApplier _applier;
    private readonly RepairProposal? _proposal;
    private readonly bool _volumeLevels;
    private RepairDecision[]? _previewedDecisions;
    private RepairApplicationPreview? _preview;
    private int _submitted;

    private ChapterRepairSession(ChapterTreeDocument document, AutoRepairOutcome outcome, ChapterRepairApplier applier)
    {
        _document = document;
        Outcome = outcome;
        _applier = applier;
        _proposal = ChapterRepairApplier.ProposalFor(outcome, document);
        _volumeLevels = outcome.Catalog?.VolumeTitles.Count > 0;
    }

    public AutoRepairOutcome Outcome { get; }

    public static async Task<ChapterRepairSession> AnalyzeAsync(ChapterTreeDocument document,
        RepairRequest request, ChapterRepairApplier applier, CancellationToken token = default,
        IProgress<string>? progress = null, CatalogAcquisition? acquisition = null)
    {
        // Detach collection containers before asynchronous analysis begins.
        var snapshot = document.WithEntries(document.Entries.Select(e => e with
            { ContentRanges = e.ContentRanges.ToArray() }).ToArray());
        var basis = request is ReferenceRepairRequest reference
            ? new ReferenceRepairRequest(reference.Catalog with { Nodes = reference.Catalog.Nodes.ToArray() })
            : request;
        var outcome = await ChapterAutoRepair.RepairAsync(snapshot.SourcePath, basis, token, progress,
            snapshot, basis is CurrentTreeHeuristicRequest ? CatalogAcquisition.NotRequested : acquisition);
        return new ChapterRepairSession(snapshot, outcome, applier);
    }

    public RepairApplicationPreview Preview(RepairLandingMode mode, IReadOnlyList<RepairDecision> decisions)
    {
        if (Volatile.Read(ref _submitted) != 0) throw new InvalidOperationException("本次方案已提交，请重新检查。");
        _previewedDecisions = null;
        _preview = null;
        var copy = decisions.ToArray();
        var preview = _applier.Preview(_proposal ?? throw new InvalidOperationException("没有可预览的方案。"),
            _document, copy, mode, buildVolumeLevels: _volumeLevels);
        _preview = preview;
        if (preview.CanApply) _previewedDecisions = copy;
        return preview;
    }

    public async Task<RepairApplicationResult> ApplyAsync(RepairLandingMode mode,
        IReadOnlyList<RepairDecision> decisions, CancellationToken token = default)
    {
        var approved = _previewedDecisions;
        if (approved is null || _preview?.Mode != mode || !approved.SequenceEqual(decisions))
            return new(false, mode, "选择或应用范围已变化，请重新预览后再应用。");
        if (Interlocked.CompareExchange(ref _submitted, 1, 0) != 0)
            return new(false, mode, "本次方案已提交，请重新检查后再应用。");
        // Applier rechecks the source file on disk immediately before committing.
        return await _applier.ApplyAsync(_proposal!, _document, approved, mode,
            buildVolumeLevels: _volumeLevels, token: token);
    }
}
