namespace EasyPub.Core;

// Shared transactional exporter for prepared edits to independent working copies.
internal sealed class PreparedTextCopy(
    ChapterTreeDocument document, SourceTextDocument source, string rendered,
    SourcePatch patch, ChapterTreePlan plan, TreePatch treePatch, string operation)
{
    internal Action<RepairJournalState>? AfterStep { get; set; }
    internal Func<string, CancellationToken, Task>? PrepareArtifacts { get; init; }
    private readonly ChapterTreeDocument _document = document;
    private readonly SourceTextDocument _source = source;
    private readonly string _rendered = rendered;
    private readonly SourcePatch _patch = patch;
    private readonly ChapterTreePlan _plan = plan;
    private readonly TreePatch _treePatch = treePatch;
    private readonly string _operation = operation;
    public async Task<ManualSplitCopyResult> ExportAsync(SourceBackupStore store, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (await SourceFileHasher.HashOfAsync(_document.SourcePath, token) != _document.SourceSha256)
            throw new InvalidDataException("原稿已变化，副本未生成，请重新预览。");
        var copy = store.CreateWorkingCopy(_document.SourcePath);
        if (PrepareArtifacts is not null) await PrepareArtifacts(copy, token);
        var projectPath = copy + ".easypubproj";
        var backup = copy + ".before-" + _operation + ".bak";
        var transaction = new SourceEditTransaction(Path.Combine(store.DirectoryPath, "Transactions"), copy,
            afterReplace: async (_, ct) =>
            {
                // Opening this project preserves moves/merges; re-recognizing the TXT alone cannot.
                var project = new EasyPubProjectDocument(EasyPubProjectDocument.CurrentSchemaVersion,
                    projectPath, Path.GetDirectoryName(copy), ConversionProfile.Default,
                    [new EasyPubProjectBook(copy, Path.GetFileNameWithoutExtension(copy), null, null, []) { ChapterTree = _plan }],
                    DateTimeOffset.Now);
                await new EasyPubProjectStore(projectPath).SaveAsync(project, ct);
            }) { AfterStep = AfterStep };
        var version = RepairBaseVersionFactory.Capture(_document.SourceSha256, _document.Entries,
            _document.RecognitionOptions, _document.ChapterPattern);
        var manifest = new RepairExecutionManifest(RepairExecutionManifest.CurrentSchemaVersion, transaction.TransactionId,
            _patch.ActionSetId, version, [], _treePatch, _patch, _treePatch.ExpectedResult,
            _plan.SourceSha256, _document.SourceSha256, RepairLandingMode.EditSource, backup, _source.DefaultNewLine);
        var result = await transaction.ExecuteAsync(manifest, _source, _rendered, backup, token);
        result = result with { BackupPath = backup };
        if (!result.Succeeded && File.Exists(backup))
        {
            // Only restore our own new version. An external change is never overwritten.
            try
            {
                var hash = await SourceFileHasher.HashOfAsync(copy, CancellationToken.None);
                if (hash == _plan.SourceSha256
                    && await SourceFileHasher.HashOfAsync(backup, CancellationToken.None) == _document.SourceSha256)
                {
                    var failedCopy = copy + ".failed-" + Guid.NewGuid().ToString("N");
                    File.Copy(copy, failedCopy, false);
                    var bytes = await File.ReadAllBytesAsync(backup);
                    await AtomicOutputWriter.WriteAsync(copy,
                        (stream, ct) => stream.WriteAsync(bytes.AsMemory(), ct).AsTask(), CancellationToken.None);
                    if (File.Exists(projectPath)) File.Move(projectPath, failedCopy + ".easypubproj");
                    var journals = new RepairJournalStore(Path.Combine(store.DirectoryPath, "Transactions"));
                    var journal = journals.Load(transaction.TransactionId);
                    if (journal is not null) journals.Save(journal.WithState(RepairJournalState.RolledBack));
                    result = result with { Outcome = SourceEditOutcome.RolledBack, NewSourceSha256 = null,
                        Message = "副本导出未完成，已恢复修改前副本；失败版本保留于：" + failedCopy };
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                result = result with { Outcome = SourceEditOutcome.Failed,
                    Message = "副本导出及自动恢复未全部完成，请核对副本与备份。" + error.Message };
            }
        }
        return new(result.Succeeded, copy, projectPath, result);
    }
}
