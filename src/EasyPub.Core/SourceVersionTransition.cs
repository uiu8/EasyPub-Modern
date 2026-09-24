namespace EasyPub.Core;

/// <summary>What happened to one saved chapter identity while the source moved to a new version.</summary>
public enum SourceRebindStatus
{
    /// <summary>The identity survived and now points at the new numbering.</summary>
    Rebound,
    /// <summary>The identity survived because it never depended on a line number (a title-keyed entry).</summary>
    TitleKeyed,
    /// <summary>The heading line is gone, so the identity is gone with it.</summary>
    LostLineDeleted,
    /// <summary>The heading line was rewritten by this repair, so the old identity no longer describes it.</summary>
    LostLineReplaced,
    /// <summary>The chapter has no line and no body left, so there is nothing to keep.</summary>
    LostNoBody,
}

/// <summary>One saved chapter identity, before and after the source changed.</summary>
public sealed record SourceEntryRebind(string OldStableKey, string? NewStableKey, string Title, SourceRebindStatus Status)
{
    public bool Survived => Status is SourceRebindStatus.Rebound or SourceRebindStatus.TitleKeyed;
}

/// <summary>
/// Everything a running workbench holds that is written in the coordinates of one version of the TXT.
///
/// <para>Kept as one value rather than as fields on a window, so that migrating it is a function with a
/// result instead of a sequence of assignments whose halfway point is a state nobody designed.</para>
/// </summary>
public sealed record SourceTransitionState(
    string SourcePath,
    SourceTextDocument Source,
    ChapterTreeDocument? RecognitionTree,
    ChapterTreePlan? Plan,
    ReferenceCatalog? Catalog,
    IReadOnlyDictionary<string, ChapterReviewGroup>? ConfirmedReviews,
    IReadOnlyList<ChapterBreakpoint>? Breakpoints)
{
    public string SourceSha256 => RecognitionTree?.SourceSha256 ?? "";

    /// <summary>
    /// What this document would occupy on disk, hashed the way the writer hashes it. Computed once when the
    /// state is built rather than on each read: it hashes the whole book, and it is read by several steps.
    /// </summary>
    public string RenderedHash { get; init; } = "";

    /// <summary>
    /// The hash the step that read this text found on disk, so a later check can tell whether the file has
    /// moved on since. Empty when the state was assembled by hand rather than read.
    /// </summary>
    public string LoadedHash { get; init; } = "";
    public string? ReloadChapterPattern { get; init; }
    public TocHierarchyOptions? ReloadRecognitionOptions { get; init; }

    /// <summary>
    /// The text that was replaced, kept for the length of the migration.
    ///
    /// <para>The coordinate map bridges two versions, so checking it needs both, and after
    /// <see cref="AdoptSourceTextStep"/> has run the state only holds the new one. Carrying the old text here
    /// is what lets the very next step re-derive the map from the two texts it actually has, instead of
    /// trusting the map it was handed.</para>
    /// </summary>
    public SourceTextDocument? ReplacedText { get; init; }

    /// <summary>Reads the book from disk with no saved plan: whatever the rules recognise in this text.</summary>
    public static async Task<SourceTransitionState> LoadFromAsync(string sourcePath,
        TextEncodingMode encodingMode = TextEncodingMode.Auto, CatalogPreferences? catalog = null,
        CancellationToken token = default)
    {
        var document = await ChapterTreeDocument.LoadAsync(sourcePath, encodingMode: encodingMode,
            cancellationToken: token).ConfigureAwait(false);
        var source = await SourceTextDocument.LoadAsync(sourcePath, encodingMode, token).ConfigureAwait(false);
        return new SourceTransitionState(
            sourcePath,
            source,
            document,
            document.CreatePlan(document.Entries),
            ReferenceCatalogInput.ReadCatalog(catalog),
            null,
            null)
        {
            LoadedHash = SourceFileHasher.HashOfRendered(source, source.Render()),
            RenderedHash = SourceFileHasher.HashOfRendered(source, source.Render()),
        };
    }

    /// <summary>
    /// The tree the user arranged, without the recognition pass that produced it — for a caller that wants
    /// the migration to start from the saved plan rather than from a fresh recognition.
    ///
    /// <para>The plan stays; only the recognition document is dropped, and dropping it is a statement that
    /// this state has no parse of the current file rather than a claim that there is nothing to parse.</para>
    /// </summary>
    public SourceTransitionState WithoutRecognitionTree() => this with
    {
        ReloadChapterPattern = RecognitionTree is not null ? RecognitionTree.ChapterPattern : ReloadChapterPattern,
        ReloadRecognitionOptions = RecognitionTree?.RecognitionOptions ?? ReloadRecognitionOptions,
        RecognitionTree = null,
    };
}

/// <summary>
/// One step of the migration. Each step answers three questions about itself, which is what makes the whole
/// migration idempotent and checkable rather than hopeful:
///
/// <list type="number">
/// <item><see cref="Applies"/> — is there anything left to do? If not the step is skipped, so running the
/// migration twice does nothing the second time.</item>
/// <item><see cref="RunAsync"/> — the work, returning a <b>new</b> state. Nothing is mutated in place, so a
/// step that fails its own postcondition cannot leave half of itself behind.</item>
/// <item><see cref="Verify"/> — the postcondition, checked against the state the step just produced.</item>
/// </list>
///
/// <para>The old code did this with three statements in a window method and one boolean, and the boolean said
/// only "something changed" — not what survived and what did not. When a heading line was rewritten, the
/// saved chapter kept claiming a line that no longer held its title, and the tree showed a chapter the book
/// did not have.</para>
/// </summary>
public abstract class SourceTransitionStep
{
    public abstract string Name { get; }

    /// <summary>False when this step's work is already done, which is what makes a second run a no-op.</summary>
    public abstract bool Applies(SourceTransitionState state);

    public abstract Task<SourceTransitionState> RunAsync(SourceTransitionContext context, SourceTransitionState state,
        CancellationToken token);

    /// <summary>The postcondition in words, used both for checking and for the report.</summary>
    public abstract string Postcondition { get; }

    public abstract bool Verify(SourceTransitionState state);

    /// <summary>What this step decided about saved chapters, when it decided anything.</summary>
    public virtual IReadOnlyList<SourceEntryRebind> Rebinds => [];
}

/// <summary>What a step is allowed to read: the patch that was applied, and the map from old lines to new.</summary>
public sealed record SourceTransitionContext(
    string OldSourceSha256,
    string NewSourceSha256,
    SourcePatch Patch,
    SourceCoordinateMap Coordinates,
    string SourcePath,
    TextEncodingMode EncodingMode = TextEncodingMode.Auto)
{
    /// <summary>
    /// The cache the workbench loaded the tree through. A property of the window rather than of the
    /// migration, so it is handed in rather than owned here.
    /// </summary>
    public ChapterTreeDocumentCache? DocumentCache { get; init; }

    /// <summary>Where the saved directory for the old version lives, when the caller knows it was loaded.</summary>
    public string? OldCatalogPath { get; init; }

    public void InvalidateCache() => DocumentCache?.Invalidate(SourcePath);
}

/// <summary>
/// Moves every in-memory model from the old version of the TXT to the new one.
///
/// <para><b>Adopt</b> — the text and the cached parse — comes first, because the other steps read it to
/// decide whether the file they are about to describe is the file that exists.</para>
///
/// <para><b>Coordinates</b> is next because everything after it depends on where each old line went, and the
/// map is checked against the new bytes before anything uses it.</para>
///
/// <para><b>Reload</b> re-reads the file. The saved plan <b>cannot</b> be handed to this read: it carries the
/// old hash, and <see cref="ChapterTreeDocument.LoadAsync"/> refuses a plan whose hash does not match — which
/// is the correct refusal, because that plan describes a book that no longer exists on disk.</para>
///
/// <para><b>Rebind</b> answers the question the old boolean could not: which saved chapters still exist. A
/// chapter keyed by a heading line survives when that line survives; a title-keyed one survives regardless;
/// and one whose heading line was <b>rewritten</b> is dropped, because a replaced line is different text and
/// keeping the identity would silently fuse two different chapters into one.</para>
///
/// <para><b>Retire</b> throws away what cannot be renumbered at all. A review was confirmed on a set of line
/// numbers; after the deletion of 160 lines those numbers mean other lines. Inventing a new set would be
/// presenting a decision the user never made, so the honest answer is that they have to look again.</para>
/// </summary>
public sealed class SourceVersionTransition
{
    private readonly List<SourceTransitionStep> _steps = [];

    public SourceVersionTransition Add(SourceTransitionStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        _steps.Add(step);
        return this;
    }

    public IReadOnlyList<SourceTransitionStep> Steps => _steps;

    /// <summary>
    /// The migration a repair needs, in the order it has to happen.
    ///
    /// <para><c>reloadRecognizedTree</c> is false when the caller already holds the tree for the new version,
    /// which is the case when the transaction itself re-read the file. It is true when the file changed
    /// underneath the program, which is the other way a source version changes.</para>
    /// </summary>
    public static SourceVersionTransition Standard(bool reloadRecognizedTree = true) =>
        new SourceVersionTransition()
            .Add(new AdoptSourceTextStep())
            .Add(new BuildCoordinatesStep())
            .Add(new ReloadRecognitionTreeStep(reloadRecognizedTree))
            .Add(new RebindSavedPlanStep())
            .Add(new RetireReviewsAndBreakpointsStep())
            .Add(new RehashSavedCatalogStep());
    public async Task<SourceTransitionResult> RunAsync(SourceTransitionContext context, SourceTransitionState state,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(state);

        var report = new List<SourceTransitionStepReport>();
        var rebinds = new List<SourceEntryRebind>();
        var fallback = false;
        var current = state;

        foreach (var step in _steps)
        {
            token.ThrowIfCancellationRequested();

            if (!step.Applies(current))
            {
                report.Add(new SourceTransitionStepReport(step.Name, SourceTransitionStepOutcome.AlreadySatisfied,
                    step.Postcondition));
                continue;
            }

            SourceTransitionState next;
            try
            {
                next = await step.RunAsync(context, current, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception error)
            {
                report.Add(new SourceTransitionStepReport(step.Name, SourceTransitionStepOutcome.Failed,
                    $"{step.Postcondition}（执行时出错：{error.Message}）"));
                return new SourceTransitionResult(false, report, current,
                    $"迁移在第 {report.Count} 步中断：{step.Name} — {error.Message}");
            }

            if (!step.Verify(next))
            {
                // A step that can say what it disagreed about should say it: "执行后未满足" tells a person
                // nothing about which of two hundred line numbers was wrong.
                var detail = step switch
                {
                    BuildCoordinatesStep coordinates => coordinates.DescribeMismatch(),
                    _ => step.Postcondition,
                };
                report.Add(new SourceTransitionStepReport(step.Name, SourceTransitionStepOutcome.Failed,
                    $"{detail}（执行后未满足）"));
                return new SourceTransitionResult(false, report, current,
                    $"迁移在第 {report.Count} 步中断：{step.Name} — {detail}");
            }

            report.Add(new SourceTransitionStepReport(step.Name, SourceTransitionStepOutcome.Completed, step.Postcondition));
            rebinds.AddRange(step.Rebinds);
            if (step is RebindSavedPlanStep { FellBackToRecognition: true }) fallback = true;
            current = next;
        }

        return new SourceTransitionResult(true, report, current, "原文版本迁移已完成。")
        {
            Rebinds = rebinds,
            FellBackToRecognition = fallback,
        };
    }

    /// <summary>
    /// The whole migration for the ordinary case: a repair replaced the file, and here are the bytes it wrote.
    ///
    /// <para>The coordinate map is verified before any step runs. Every later step is a rebinding, and a
    /// rebinding against a map that is off by one line is worse than refusing — it produces a tree that looks
    /// consistent and points at the wrong text.</para>
    /// </summary>
    public static async Task<SourceTransitionResult> AfterReplaceAsync(
        SourceTransitionState before,
        SourcePatch patch,
        string newText,
        string newSourcePath,
        ChapterTreeDocumentCache? cache = null,
        TextEncodingMode encodingMode = TextEncodingMode.Auto,
        string? oldCatalogPath = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(patch);

        // 状态已经是新版本时，这次迁移是空操作。
        //
        // 这不是"补丁与文本不一致"，而是"根本没有旧版本可搬"——程序重启后从磁盘读到的就是这个形状，
        // 补丁的替换在那份文本上确实无事可做。按失败处理会让重启后的第一次操作莫名其妙地报错；
        // 按成功处理它，就是"已经在新版本上，无需迁移"。
        if (IsAlreadyAt(before, newText))
            return new SourceTransitionResult(true,
                [new SourceTransitionStepReport("核对版本", SourceTransitionStepOutcome.AlreadySatisfied,
                    "这次迁移要到达的版本就是当前版本")],
                before, "原文已经是这次修复后的版本，无需迁移。");

        var coordinates = SourceCoordinateMap.Build(before.Source, patch, newText);
        var verification = coordinates.Verify(before.Source, patch, newText);
        if (!verification.IsSound)
            return new SourceTransitionResult(false,
                [new SourceTransitionStepReport("核对坐标表与补丁", SourceTransitionStepOutcome.Failed, verification.Message)],
                before, "坐标表与补丁或新文本不符，已停止迁移：" + verification.Message);

        // Computed from the bytes the transaction writes rather than taken from the caller: a hash passed in
        // from elsewhere is a second opinion about a file this method is already holding.
        var newSha = SourceFileHasher.HashOfRendered(before.Source, newText);
        var context = new SourceTransitionContext(
            before.RecognitionTree?.SourceSha256 ?? "",
            newSha,
            patch,
            coordinates,
            newSourcePath,
            encodingMode)
        {
            DocumentCache = cache,
            OldCatalogPath = oldCatalogPath,
        };

        return await Standard().RunAsync(context, before, token).ConfigureAwait(false);
    }

    /// <summary>
    /// The whole migration for the <b>other</b> way a source version changes: the file was edited outside
    /// this program, and the program still holds models written against the old text.
    ///
    /// <para>There is no patch in hand for this case — nobody recorded what the person typed — so it is
    /// derived by comparing the two texts line by line, exactly as a restore does. Deriving it here rather
    /// than in the window is the point: the window would have to read the file, guess at the encoding and
    /// work out the operations, and every one of those is a thing this class already does for the repair
    /// path.</para>
    ///
    /// <para>An unchanged file is <b>not</b> an error. The check that calls this runs whenever the window is
    /// activated, and most activations happen after the file did not change; reporting a failure there would
    /// train the user to ignore the message that matters.</para>
    /// </summary>
    public static async Task<SourceTransitionResult> AfterExternalEditAsync(
        SourceTransitionState before,
        string sourcePath,
        ChapterTreeDocumentCache? cache = null,
        TextEncodingMode encodingMode = TextEncodingMode.Auto,
        string? oldCatalogPath = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var after = await SourceTextDocument.LoadAsync(sourcePath, encodingMode, token).ConfigureAwait(false);
        var newText = after.Render();
        if (string.Equals(before.Source.Render(), newText, StringComparison.Ordinal))
            return new SourceTransitionResult(true,
                [new SourceTransitionStepReport("核对版本", SourceTransitionStepOutcome.AlreadySatisfied,
                    "磁盘上的原文与内存中的文本一致，无需迁移")],
                before, "原文内容未变，无需迁移。");

        return await AfterReplaceAsync(before, SourceDiff.Patch(before.Source, after), newText, sourcePath,
            cache, encodingMode, oldCatalogPath, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether the state already describes the text the patch produces.
    ///
    /// <para>Compared by rendering the state's own text, not by asking the caller for a hash: the caller's
    /// hash would be one more claim about a text this method can read for itself.</para>
    /// </summary>
    private static bool IsAlreadyAt(SourceTransitionState before, string newText) =>
        string.Equals(before.Source.Render(), newText, StringComparison.Ordinal);
}

public enum SourceTransitionStepOutcome
{
    Completed,
    /// <summary>Nothing to do — this is what a second run of the same migration looks like.</summary>
    AlreadySatisfied,
    Failed,
}

public sealed record SourceTransitionStepReport(string Step, SourceTransitionStepOutcome Outcome, string Detail);

/// <summary>
/// The migration's result, including <b>which saved chapters survived</b>.
///
/// <para>The per-entry list is the part a person needs. "原文已变化" says nothing about whether the chapter
/// they spent an hour numbering is still there; this says it chapter by chapter.</para>
/// </summary>
public sealed record SourceTransitionResult(
    bool Succeeded,
    IReadOnlyList<SourceTransitionStepReport> Steps,
    SourceTransitionState State,
    string Message)
{
    public IReadOnlyList<SourceEntryRebind> Rebinds { get; init; } = [];

    /// <summary>
    /// True when the saved tree could not be moved onto the new numbering as a whole and was replaced by a
    /// fresh recognition. It is not an error — it is the honest answer — but the user has to be told, because
    /// it means their manual edits are gone.
    /// </summary>
    public bool FellBackToRecognition { get; init; }

    public IReadOnlyList<SourceEntryRebind> Lost => Rebinds.Where(rebind => !rebind.Survived).ToArray();

    public string Describe()
    {
        if (!Succeeded) return Message;
        var survived = Rebinds.Count(rebind => rebind.Survived);
        var lost = Lost;
        return $"{Message} 章节身份：保留 {survived} 个"
            + (lost.Count == 0
                ? "，无失效"
                : $"，失效 {lost.Count} 个（{string.Join("、", lost.Take(3).Select(rebind => rebind.Title))}"
                    + (lost.Count > 3 ? " 等" : "") + "）")
            + (FellBackToRecognition ? " 注意：迁移后的章节树未通过校验，已回退为按新原文重新识别的结果。" : "");
    }
}

// ---------------------------------------------------------------------------------------------------------
// The six steps, in order.
// ---------------------------------------------------------------------------------------------------------

/// <summary>Step 1: hold the text this run will describe, and let go of the cached parse of the old file.</summary>
public sealed class AdoptSourceTextStep : SourceTransitionStep
{
    public override string Name => "采用新文本并作废旧解析";

    public override bool Applies(SourceTransitionState state) => true;

    public override async Task<SourceTransitionState> RunAsync(SourceTransitionContext context,
        SourceTransitionState state, CancellationToken token)
    {
        // Read through the same decoder the chapter tree uses. There used to be two readers — the tree through
        // TextFileDecoder, this document through File.ReadAllText — and on a GBK book they disagreed about the
        // characters, and therefore about where the lines are. A coordinate map cannot survive that.
        var source = await SourceTextDocument.LoadAsync(context.SourcePath, context.EncodingMode, token)
            .ConfigureAwait(false);

        // The cache is keyed by path, length, write time and the recognition settings — everything except the
        // bytes. A replacement of the same length inside the file system's timestamp granularity would hit
        // the old key, so the entry is removed rather than left for the key to notice.
        context.InvalidateCache();

        return state with
        {
            SourcePath = context.SourcePath,
            Source = source,
            ReloadChapterPattern = state.RecognitionTree is not null ? state.RecognitionTree.ChapterPattern : state.ReloadChapterPattern,
            ReloadRecognitionOptions = state.RecognitionTree?.RecognitionOptions ?? state.ReloadRecognitionOptions,
            RecognitionTree = null,
            LoadedHash = SourceFileHasher.HashOfRendered(source, source.Render()),
            RenderedHash = SourceFileHasher.HashOfRendered(source, source.Render()),
            ReplacedText = context.Coordinates.OriginalLineCount == state.Source.Lines.Count ? state.Source : null,
        };
    }

    public override string Postcondition => "内存中的文本与磁盘上的原文一致，且旧解析结果不再被复用";

    /// <summary>
    /// Checked against the file, not against the string that was read. The failure this rules out is exactly
    /// "the text in memory is not the text on disk": a decoder that answered with a different code page than
    /// the chapter tree used produces a document whose line numbers are shifted relative to the tree's, and
    /// every rebinding after that is off by the same amount.
    /// </summary>
    public override bool Verify(SourceTransitionState state) =>
        state.RecognitionTree is null
        && state.LoadedHash.Length > 0
        && File.Exists(state.SourcePath)
        && string.Equals(SourceFileHasher.HashOf(state.SourcePath), state.LoadedHash, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Step 2: re-derive where every old line went, from the two texts, and refuse to continue if the map
/// the caller supplied does not match.</summary>
public sealed class BuildCoordinatesStep : SourceTransitionStep
{
    private bool _mapIsSound;
    private string _failure = "";

    public override string Name => "核对坐标表与新文本";

    public override bool Applies(SourceTransitionState state) => true;

    public override Task<SourceTransitionState> RunAsync(SourceTransitionContext context, SourceTransitionState state,
        CancellationToken token)
    {
        // The pair (patch, new text) has to be self-consistent before anything is rebuilt from it: a patch
        // whose replacements do not match the text it is said to produce would otherwise be used to derive a
        // coordinate map, and that derived map would agree with the fabricated patch perfectly. Checking the
        // pair first is what makes the check mean something — it asks the text whether the patch is true.
        var verification = context.Coordinates.Verify(state.ReplacedText!, context.Patch, state.Source.Render());
        if (!verification.IsSound)
        {
            _mapIsSound = false;
            _failure = verification.Message;
            return Task.FromResult(state);
        }

        // And the map the caller supplied has to be that same map: a caller that computed it against another
        // version, or that lost a deletion on the way, has to be told rather than quietly ignored.
        var rebuilt = SourceCoordinateMap.Build(state.ReplacedText!, context.Patch, state.Source.Render());
        var supplied = context.Coordinates;
        var mismatch = !rebuilt.DeletedLines.SetEquals(supplied.DeletedLines)
            || !rebuilt.ReplacedLines.SetEquals(supplied.ReplacedLines)
            || !rebuilt.InsertedLines.SetEquals(supplied.InsertedLines)
            || rebuilt.NewLineCount != supplied.NewLineCount
            || rebuilt.NewLineOfOriginal.Any(pair =>
                !supplied.NewLineOfOriginal.TryGetValue(pair.Key, out var other) || other != pair.Value);

        _mapIsSound = !mismatch;
        _failure = mismatch
            ? $"调用方给的坐标表记着 删{supplied.DeletedLines.Count}/改{supplied.ReplacedLines.Count}"
                + $"/插{supplied.InsertedLines.Count} 行，按补丁与新文本重新推出的却是 "
                + $"删{rebuilt.DeletedLines.Count}/改{rebuilt.ReplacedLines.Count}/插{rebuilt.InsertedLines.Count} 行。"
            : "";
        return Task.FromResult(state);
    }

    public override string Postcondition => "新文本逐行满足补丁的声明，且调用方的坐标表与之一致";

    public override bool Verify(SourceTransitionState state) => _mapIsSound;

    public string DescribeMismatch() => _failure.Length > 0 ? _failure : Postcondition;
}

/// <summary>
/// Step 3: read the file again, without the saved plan.
///
/// <para>The saved plan carries the old hash and is refused by the loader on purpose. Passing it here would
/// mean either weakening that refusal or catching it as an expected error, and both turn "this plan belongs to
/// another file" from a guarantee into a habit.</para>
/// </summary>
public sealed class ReloadRecognitionTreeStep : SourceTransitionStep
{
    private readonly bool _reload;
    private string _loadedSha256 = "";

    public ReloadRecognitionTreeStep(bool reload = true) => _reload = reload;

    public override string Name => "按新原文重新识别章节树";

    public override bool Applies(SourceTransitionState state) =>
        _reload && (state.RecognitionTree is null
            || !string.Equals(state.RecognitionTree.SourceSha256, state.RenderedHash, StringComparison.OrdinalIgnoreCase));

    public override async Task<SourceTransitionState> RunAsync(SourceTransitionContext context,
        SourceTransitionState state, CancellationToken token)
    {
        var tree = await ChapterTreeDocument.LoadAsync(context.SourcePath, encodingMode: context.EncodingMode,
            chapterPattern: state.RecognitionTree is not null ? state.RecognitionTree.ChapterPattern : state.ReloadChapterPattern,
            hierarchy: state.RecognitionTree?.RecognitionOptions ?? state.ReloadRecognitionOptions,
            cancellationToken: token).ConfigureAwait(false);
        _loadedSha256 = tree.SourceSha256;

        // Only the recognition result is replaced. The plan — the tree the user arranged — is deliberately
        // left alone: it still carries the old version's hash, and the next step is what moves it onto the
        // new numbering. Overwriting it here would throw away every manual edit at the exact moment the user
        // asked for their text to be repaired, and the migration would then report "0 identities to move"
        // while quietly replacing their work with a fresh recognition.
        return state with { RecognitionTree = tree };
    }

    /// <summary>
    /// The postcondition is about <b>which version</b> the tree describes, not about how many lines it holds.
    ///
    /// <para>Line counts are not comparable across the two models: the recognition document splits the text on
    /// newlines and therefore counts one extra empty element for a file that ends with one, while
    /// <see cref="SourceTextDocument"/> does not treat the tail as a line. Comparing them made this step fail
    /// on the sample book with 2503 against 2502 — a difference that says nothing about the tree. The hash is
    /// the thing that actually has to match.</para>
    /// </summary>
    public override string Postcondition => "识别树描述的是新版本的字节";

    public override bool Verify(SourceTransitionState state) =>
        state.RecognitionTree is not null
        && _loadedSha256.Length > 0
        && string.Equals(state.RecognitionTree.SourceSha256, _loadedSha256, StringComparison.OrdinalIgnoreCase)
        && string.Equals(state.RecognitionTree.SourceSha256, state.RenderedHash, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Step 4: move the saved chapters onto the new numbering, and say which ones did not survive.
///
/// <para>The rules are per entry and deliberate:</para>
/// <list type="bullet">
/// <item>A heading line that survived keeps the chapter, with the line renumbered.</item>
/// <item>A heading line that was deleted leaves a chapter whose title no longer appears anywhere — the
/// identity becomes title-keyed, which is exactly the state a to-be-inserted heading is already in.</item>
/// <item>A heading line that was <b>rewritten</b> cannot be kept under its old identity. The line now holds
/// different text, so "L812" no longer means this chapter; keeping it would attach the chapter to whatever
/// the repair wrote there.</item>
/// <item>A chapter with no line and no body left is dropped: an empty chapter is not a chapter.</item>
/// </list>
/// </summary>
public sealed class RebindSavedPlanStep : SourceTransitionStep
{
    private IReadOnlyList<SourceEntryRebind> _rebinds = [];

    internal bool FellBackToRecognition { get; private set; }

    public override string Name => "把已保存的章节树移到新行号";

    public override bool Applies(SourceTransitionState state) =>
        state.Plan is not null && state.RecognitionTree is not null
        && !string.Equals(state.Plan.SourceSha256, state.SourceSha256, StringComparison.OrdinalIgnoreCase);

    public override Task<SourceTransitionState> RunAsync(SourceTransitionContext context, SourceTransitionState state,
        CancellationToken token)
    {
        var map = context.Coordinates;
        var patched = state.Source;
        var kept = new List<ChapterTreeEntry>();
        var rebinds = new List<SourceEntryRebind>();

        var parents = new HashSet<string>();
        for (var i = 0; i + 1 < state.Plan!.Entries.Count; i++)
            if (!state.Plan.Entries[i].IsFrontMatter && state.Plan.Entries[i + 1].Level > state.Plan.Entries[i].Level)
                parents.Add(state.Plan.Entries[i].Id);
        foreach (var entry in state.Plan.Entries)
        {
            var oldKey = entry.StableKey;
            var titleLine = entry.TitleLineNumber;

            if (titleLine is null)
            {
                // Never depended on a line number; its identity is its title.
                kept.Add(RebindRanges(entry, map, patched));
                rebinds.Add(new SourceEntryRebind(oldKey, entry.StableKey, entry.Title, SourceRebindStatus.TitleKeyed));
                continue;
            }

            if (map.IsReplaced(titleLine.Value))
            {
                rebinds.Add(new SourceEntryRebind(oldKey, null, entry.Title, SourceRebindStatus.LostLineReplaced));
                continue;
            }

            var mappedLine = map.TryMap(titleLine.Value);
            if (mappedLine is null)
            {
                // The heading line is gone. The chapter survives on its title only if it still has a body.
                var withoutLine = RebindRanges(entry with { TitleLineNumber = null }, map, patched);
                if (withoutLine.ContentRanges.Count == 0)
                {
                    rebinds.Add(new SourceEntryRebind(oldKey, null, entry.Title, SourceRebindStatus.LostNoBody));
                    continue;
                }
                kept.Add(withoutLine);
                rebinds.Add(new SourceEntryRebind(oldKey, withoutLine.StableKey, entry.Title,
                    SourceRebindStatus.LostLineDeleted));
                continue;
            }

            var rebound = RebindRanges(entry with { TitleLineNumber = mappedLine }, map, patched);
            if (rebound.ContentRanges.Count == 0 && !rebound.IsFrontMatter && !parents.Contains(entry.Id))
            {
                rebinds.Add(new SourceEntryRebind(oldKey, null, entry.Title, SourceRebindStatus.LostNoBody));
                continue;
            }
            kept.Add(rebound);
            rebinds.Add(new SourceEntryRebind(oldKey, rebound.StableKey, entry.Title, SourceRebindStatus.Rebound));
        }

        _rebinds = rebinds;
        FellBackToRecognition = false;

        var plan = BuildPlan(state, context, kept);
        if (plan is null)
        {
            // The rebound tree did not survive validation as a whole. Every individual entry was rebound
            // correctly, but the result as a set of ranges is not a tree the rest of the program can use,
            // so the whole thing is replaced by a fresh recognition and the fact is recorded. Half-applying
            // it would be worse than either answer.
            FellBackToRecognition = true;
            return Task.FromResult(state with
            {
                Plan = state.RecognitionTree!.CreatePlan(state.RecognitionTree.Entries),
                ConfirmedReviews = null,
                Breakpoints = null,
            });
        }

        return Task.FromResult(state with { Plan = plan, ConfirmedReviews = null, Breakpoints = null });
    }

    private static ChapterTreePlan? BuildPlan(SourceTransitionState state, SourceTransitionContext context,
        List<ChapterTreeEntry> kept)
    {
        if (kept.Count == 0) return null;

        var plan = new ChapterTreePlan(context.NewSourceSha256, kept)
        {
            NumericHeadingRecognition = state.Plan!.NumericHeadingRecognition,
            NumericHeadingMinimumBodyLines = state.Plan.NumericHeadingMinimumBodyLines,
            NumericHeadingPattern = state.Plan.NumericHeadingPattern,
            HeadingNumberCorrections = state.Plan.HeadingNumberCorrections,
            ReferenceCatalog = state.Catalog,
            // The reviews were confirmed against the old line numbers and cannot be carried over. Leaving
            // this null rather than renumbering them is the point.
            ConfirmedReviews = null,
            // **必须显式复制 provenance。** 这里是逐字段重建 plan，漏掉一个字段 = 那个字段在原文
            // 迁移之后**静默消失**。这正是「事务算法不改，但迁移必须把新状态带过去」那个窄例外。
            Provenance = state.Plan.Provenance,
            RecognitionOptions = state.Plan.RecognitionOptions,
            ChapterPattern = state.Plan.ChapterPattern,
        };

        try
        {
            ChapterTreeDocument.ValidatePlan(plan, state.RecognitionTree!.LineCount);
            return plan;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// A range that lost a middle stretch becomes two, and a range that lost every line is dropped.
    ///
    /// <para>This is where the sixth-volume defect would come back if the map were ignored: duplicate heading
    /// lines that a repair deletes sit <b>inside</b> a chapter's body, so that body really does become two
    /// ranges. Ranges are read out of the map instead of being recomputed from heading positions, so no line
    /// can end up owned twice or not at all.</para>
    ///
    /// <para><b>Adjacent ranges are not merged merely because they are adjacent.</b> Two ranges can become
    /// neighbours in the new numbering without ever having been one range: the lines that used to separate
    /// them were deleted, so the gap closed. Merging on adjacency then hands one chapter the whole of the
    /// chapter that follows it — measured on the sample book, chapter 7 of volume 6 swallowed the heading and
    /// body of chapter 8 because 100 deleted lines had pulled the two ranges together. They are merged only
    /// when every line between them is a line the patch deleted, because that is the one case where the gap
    /// was this chapter's own text and is now genuinely gone.</para>
    /// </summary>
    private static ChapterTreeEntry RebindRanges(ChapterTreeEntry entry, SourceCoordinateMap map,
        SourceTextDocument patched)
    {
        var ranges = new List<ChapterSourceRange>();
        foreach (var range in entry.ContentRanges ?? [])
        {
            int? start = null;
            var previous = 0;
            for (var line = range.StartLine; line <= range.EndLine; line++)
            {
                var mapped = map.TryMap(line);
                if (mapped is null)
                {
                    if (start is not null) ranges.Add(new ChapterSourceRange(start.Value, previous));
                    start = null;
                    continue;
                }
                start ??= mapped.Value;
                previous = mapped.Value;
            }
            if (start is not null) ranges.Add(new ChapterSourceRange(start.Value, previous));
        }

        var ordered = ranges.OrderBy(range => range.StartLine).ToList();
        var merged = new List<ChapterSourceRange>();
        foreach (var range in ordered)
        {
            if (merged.Count > 0 && range.StartLine <= merged[^1].EndLine + 1
                && GapIsDeletedText(merged[^1].EndLine + 1, range.StartLine - 1, map, patched))
            {
                var last = merged[^1];
                merged[^1] = new ChapterSourceRange(last.StartLine, Math.Max(last.EndLine, range.EndLine));
                continue;
            }
            merged.Add(range);
        }

        return entry with { ContentRanges = merged };
    }

    /// <summary>
    /// True when every line in <c>[from, to]</c> of the new text is a deleted line's empty place, or carries
    /// only whitespace.
    ///
    /// <para>The direction matters: the question is not where those new line numbers came from — a deleted
    /// line has no new number at all — but whether anything real moved into the gap. A heading that follows
    /// this chapter sits <b>after</b> the deleted run, so it lands in the gap whenever the run is shorter than
    /// the distance between the two ranges, and a line with real text there means the ranges were never one
    /// range.</para>
    /// </summary>
    private static bool GapIsDeletedText(int from, int to, SourceCoordinateMap map, SourceTextDocument patched)
    {
        if (from > to) return true;
        if (to > patched.Lines.Count) return false;
        for (var line = from; line <= to; line++)
        {
            if (map.SourceOf(line) is not null) return false;
            if (patched.Lines[line - 1].Text.Trim().Length > 0) return false;
        }
        return true;
    }

    public override string Postcondition => "每个正文区间都落在新文本范围内，且没有任何行被两章同时占用";

    public override bool Verify(SourceTransitionState state)
    {
        if (state.Plan is null) return true;
        try
        {
            ChapterTreeDocument.ValidatePlan(state.Plan, state.Source.Lines.Count);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    public override IReadOnlyList<SourceEntryRebind> Rebinds => _rebinds;
}

/// <summary>
/// Step 5: retire the reviews and breakpoints, which are line numbers and entry ids and nothing else.
///
/// <para>A confirmed review records that the user looked at lines 120 and 121 and agreed. After a repair that
/// deleted 160 lines, those numbers are other lines. There is no honest way to renumber a decision, so the
/// decision is retired and the review is presented again.</para>
///
/// <para>Breakpoints are anchored to entry ids, and the entries they point at belong to a tree that is being
/// replaced. Keeping them would leave rows accented for reasons that no longer exist.</para>
/// </summary>
public sealed class RetireReviewsAndBreakpointsStep : SourceTransitionStep
{
    public override string Name => "作废按旧行号记录的复核与断点";

    public override bool Applies(SourceTransitionState state) =>
        state.ConfirmedReviews is { Count: > 0 } || state.Breakpoints is { Count: > 0 };

    public override Task<SourceTransitionState> RunAsync(SourceTransitionContext context, SourceTransitionState state,
        CancellationToken token) => Task.FromResult(state with { ConfirmedReviews = null, Breakpoints = null });

    public override string Postcondition => "不再保留任何按旧行号记录的复核结论";

    public override bool Verify(SourceTransitionState state) =>
        state.ConfirmedReviews is null && state.Breakpoints is null;
}

/// <summary>
/// Step 6: let the saved directory follow the book.
///
/// <para>The directory is stored under the source hash, so a repair writes a new hash and the next run finds
/// nothing — which reads to the user as "the program forgot the directory I gave it". Nothing about the
/// directory was invalidated by editing the text: the two files describe the same book. The copy goes through
/// a temporary file and a rename, like every other write here, so an interrupted copy cannot leave a
/// half-written directory where the real one was.</para>
/// </summary>
public sealed class RehashSavedCatalogStep : SourceTransitionStep
{
    private bool _copyIsReadable = true;

    public override string Name => "把已保存目录挂到新版本";

    /// <summary>
    /// This step cannot tell from the state alone whether there is a file to copy, and asking the file system
    /// here would turn a pure question into I/O on every run. It decides inside <see cref="RunAsync"/> and
    /// does nothing when there is nothing to do — which is also what makes a second run a no-op.
    /// </summary>
    public override bool Applies(SourceTransitionState state) => true;

    public override Task<SourceTransitionState> RunAsync(SourceTransitionContext context, SourceTransitionState state,
        CancellationToken token)
    {
        _copyIsReadable = true;
        if (string.IsNullOrWhiteSpace(context.OldSourceSha256)
            || string.Equals(context.OldSourceSha256, context.NewSourceSha256, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(state);

        var target = ReferenceCatalogInput.SettingsPath(context.NewSourceSha256);
        // Never overwrite: a directory already saved under the new hash was saved by somebody who had the new
        // version, and it is newer information than the copy of the old one.
        if (File.Exists(target)) return Task.FromResult(state);

        var source = context.OldCatalogPath ?? ReferenceCatalogInput.SettingsPath(context.OldSourceSha256);
        if (!File.Exists(source)) return Task.FromResult(state);

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        File.Copy(source, temp, overwrite: true);
        File.Move(temp, target, overwrite: true);

        // Checked by reading the copy back, not by trusting the copy call. The whole reason this step exists is
        // that a directory keyed by the wrong hash is invisible, and an invisible failure is the one worth
        // paying a read to rule out.
        _copyIsReadable = CopyMatches(context.OldSourceSha256, context.NewSourceSha256);
        return Task.FromResult(state);
    }

    public override string Postcondition => "新版本哈希下能读到与旧版本相同的目录记录";

    public override bool Verify(SourceTransitionState state) => _copyIsReadable;

    private static bool CopyMatches(string oldSha256, string newSha256)
    {
        var original = ReferenceCatalogInput.Load(oldSha256);
        if (original is null) return true;
        var copied = ReferenceCatalogInput.Load(newSha256);
        return copied is not null
            && string.Equals(copied.Text, original.Text, StringComparison.Ordinal)
            && string.Equals(copied.Query, original.Query, StringComparison.Ordinal);
    }
}
