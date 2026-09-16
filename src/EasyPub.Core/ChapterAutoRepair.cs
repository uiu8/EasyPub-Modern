namespace EasyPub.Core;

/// <summary>
/// One entry inside a report group: where it is and what was done to it. <paramref name="Kind"/> is
/// what lets the confirmation window turn a row's checkbox back into the action it selects, and
/// <paramref name="Recommended"/> is its default state, so the window never has to re-derive either
/// from the label text. Both are null/false for rows that are not selectable — a reference entry that
/// was never located, or a volume that was only described.
/// </summary>
public sealed record RepairReportItem(int Line, string Title, string Detail,
    ReferenceActionKind? Kind = null, bool Recommended = false);

/// <summary>
/// One reference entry the repair did not locate in the text, carried with the reason it is worth a
/// second look. Aggregator directories collect the author's leave notices ("请假一天", "今天的更新
/// 也会晚一些") beside real chapters, so of 264 entries reported as "not located" the large majority
/// are not missing prose at all. <paramref name="HasChapterNumber"/> is that distinction: it reuses the
/// number test already used to tell a section heading from body prose, so a directory entry without a
/// chapter number is what the window calls a suspected announcement rather than a missing chapter.
/// </summary>
public sealed record UnmatchedChapter(string Title, string? Volume, bool HasChapterNumber)
{
    /// <summary>Volume-qualified title, as the reference printed it.</summary>
    public string Display => Volume is { Length: > 0 } volume ? $"{volume} · {Title}" : Title;
}

/// <summary>
/// One category of change, summarised. A repair touches hundreds of chapters and listing each one is
/// noise; grouping them keeps the outcome readable while every item stays inspectable on demand.
/// <paramref name="Summary"/> is written to be read on its own — it carries the one example or the
/// volume ranges that make the category concrete.
/// </summary>
public sealed record RepairReportGroup(
    string Label,
    int Count,
    string Unit,
    string Summary,
    IReadOnlyList<RepairReportItem> Items)
{
    public string Headline => $"{Label}　{Count} {Unit}";
    public bool HasItems => Items.Count > 0;
}

/// <summary>Where one repair spent its time. Kept because the intuitive suspect is often not the real one.</summary>
public sealed record AutoRepairTiming(
    long CatalogMs = 0,
    long PlanMs = 0,
    long ApplyMs = 0,
    long PrepareMs = 0,
    long TotalMs = 0,
    ReferenceLocationStats? Location = null,
    ReferenceLocateTiming? Locate = null)
{
    public string Describe() =>
        $"目录获取 {CatalogMs} ms；对齐 {PlanMs} ms；重建章节树 {ApplyMs} ms；核对 {PrepareMs - PlanMs - ApplyMs} ms；合计 {TotalMs} ms";
}

/// <summary>Outcome of one automatic repair pass, including the comparison against the reference.</summary>
public sealed record AutoRepairOutcome(
    string Path,
    string BookName,
    bool CatalogFound,
    int ReferenceVolumes,
    int ReferenceChapters,
    int RebuiltVolumes,
    int RebuiltChapters,
    int Missing,
    int Extra,
    int VolumeRestarts,
    bool VolumesInferred,
    string Verdict,
    string? BackupPath = null,
    string? RemovedPath = null,
    int RemovedLines = 0,
    IReadOnlyList<ChapterTreeEntry>? Entries = null)
{
    public IReadOnlyList<int> RemovedSourceLines { get; init; } = [];
    public IReadOnlyList<string> MissingTitles { get; init; } = [];
    /// <summary>
    /// Every reference entry that was not located, split by whether the directory numbered it. The
    /// window reads the four figures it shows straight from these instead of parsing the verdict
    /// sentence, so the headline can never disagree with the detail underneath it.
    /// </summary>
    public IReadOnlyList<UnmatchedChapter> Unmatched { get; init; } = [];
    public int AlignedCount => Math.Max(0, ReferenceChapters - Unmatched.Count);
    /// <summary>Not located and numbered: the entries a reader actually has to check by hand.</summary>
    public int UnmatchedWithNumber => Unmatched.Count(item => item.HasChapterNumber);
    /// <summary>Not located and unnumbered: leave notices and similar, usually no action needed.</summary>
    public int UnmatchedNoNumber => Unmatched.Count(item => !item.HasChapterNumber);
    public int NeedsReview { get; init; }
    public string? CatalogSource { get; init; }
    public ReferenceCatalog? Catalog { get; init; }
    /// <summary>
    /// The plan the report was built from. The confirmation window needs the actions themselves, not
    /// just their descriptions: applying a per-item selection means handing the chosen actions back to
    /// <see cref="ReferencePlanner.Apply"/>, and <see cref="ReferencePlan.DefaultSelection"/> is what
    /// preselects the checkboxes.
    /// </summary>
    public ReferencePlan? Plan { get; init; }
    /// <summary>
    /// Indexes into the local chapter list where numbering restarts at 1. Carried so
    /// <see cref="ChapterAutoRepair.RebuildWithSelection"/> can repeat the volume-inference pass the
    /// preview ran; recomputing it from the rebuilt tree would answer a different question. Distinct
    /// from <see cref="VolumeRestarts"/>, which is how many restarts there were.
    /// </summary>
    public IReadOnlyList<int> VolumeRestartIndexes { get; init; } = [];
    /// <summary>Categorised account of what the repair changed, ready to display.</summary>
    public IReadOnlyList<RepairReportGroup> Report { get; init; } = [];
    /// <summary>Measured cost of each stage of this pass.</summary>
    public AutoRepairTiming? Timing { get; init; }
    /// <summary>Per-source cost of directory lookup, slowest first; empty when a saved directory was used.</summary>
    public IReadOnlyList<ReferenceCatalogClient.SourceTiming> CatalogTimings { get; init; } = [];
}

/// <summary>
/// One-click repair: find the official directory, reconcile the chapter tree against it, and then
/// prove the result by comparing chapter by chapter. Nothing is reported as fixed unless the rebuilt
/// tree actually accounts for every reference chapter.
/// </summary>
public static class ChapterAutoRepair
{
    /// <summary>
    /// Positions where chapter numbering restarts at 1 after a long run. Aggregator sites drop volume
    /// headings, so "…第 80 章 / 第 1 章 / …第 60 章 / 第 1 章…" is often the only surviving evidence that
    /// the book is divided into volumes. Each restart is a volume boundary the source no longer shows.
    /// </summary>
    public static IReadOnlyList<int> FindVolumeRestarts(IReadOnlyList<string> titles)
    {
        var restarts = new List<int>();
        var previous = 0;
        for (var index = 0; index < titles.Count; index++)
        {
            if (!int.TryParse(ReferenceOutline.ParseKey(titles[index]).Number, out var number)) continue;
            if (number == 1 && previous >= 10) restarts.Add(index);
            previous = number;
        }
        return restarts;
    }

    /// <summary>
    /// The saved source list plus the folder rule's source preference. Both are optional: any failure
    /// falls back to the built-in order instead of blocking the repair. Shared with the workbench's own
    /// "fetch the directory" action so a directory found there is the same one a repair would find.
    /// </summary>
    public static async Task<IReadOnlyList<string>> LoadPreferredSourcesAsync(string path)
    {
        try
        {
            ReferenceCatalogClient.Sources = await BookSourceStore.CreateDefault().LoadAsync();
            var rules = await MetadataMappingStore.CreateDefault().LoadAsync();
            return MetadataMappingResolver.Match(path, rules)?.Sources ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    public static async Task<AutoRepairOutcome> RepairAsync(
        string path, CancellationToken token = default, IProgress<string>? progress = null,
        ChapterTreeDocument? currentDocument = null, ReferenceCatalog? reference = null)
    {
        // Timed from the first statement: "the network is only a second or two" is an assumption worth
        // checking, and on a book whose directory is already cached it is simply wrong.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var document = currentDocument ?? await ChapterTreeDocument.LoadAsync(path, cancellationToken: token).ConfigureAwait(false);
        var bookName = ExtractBookName(path);
        string? error = null;
        IReadOnlyList<ReferenceCatalogClient.SourceTiming> timings = [];
        if (reference is null)
        {
            progress?.Report("正在查找本书已保存的目录与来源…");
            var preferred = await LoadPreferredSourcesAsync(path).ConfigureAwait(false);
            var saved = ReferenceCatalogInput.Load(document.SourceSha256);
            reference = ReferenceCatalogInput.ReadCatalog(saved);
            if (reference is not null && !string.IsNullOrWhiteSpace(saved?.Url)) reference = reference with { Source = saved.Url + "（本书已保存目录）" };
            if (reference is null)
            {
                var query = !string.IsNullOrWhiteSpace(saved?.Url) ? saved.Url
                    : await Task.Run(() => ReferenceCatalogInput.FindLocalBookUrl(path), token).ConfigureAwait(false)
                    ?? (!string.IsNullOrWhiteSpace(saved?.Query) ? saved.Query : bookName);
                progress?.Report("正在获取参考目录，可随时取消…");
                try
                {
                    var client = new ReferenceCatalogClient();
                    var catalogs = await client.DiscoverAsync(query,token,preferred).ConfigureAwait(false);
                    timings = client.LastTimings;
                    reference = ReferenceCatalogInput.Pick(catalogs);
                }
                catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or OperationCanceledException or InvalidOperationException)
                { error=e.Message; }
            }
        }
        token.ThrowIfCancellationRequested();
        var catalogMs = watch.ElapsedMilliseconds;
        progress?.Report("正在核对章节、分配正文并检查行完整性…");
        var outcome = await Task.Run(() => Prepare(document,reference,error,token,catalogMs),token).ConfigureAwait(false);
        return outcome with { Catalog = reference, CatalogTimings = timings };
    }

    /// <summary>Set only while a repair pass is running, so <see cref="Prepare"/> can report its own cost.</summary>
    [ThreadStatic] private static System.Diagnostics.Stopwatch? _pass;

    public static AutoRepairOutcome Prepare(ChapterTreeDocument document, ReferenceCatalog? catalog,
        string? discoveryError = null, CancellationToken token = default, long catalogMs = 0)
    {
        _pass = System.Diagnostics.Stopwatch.StartNew();
        try { return Build(document, catalog, discoveryError, token, catalogMs); }
        finally { _pass = null; }
    }

    private static AutoRepairOutcome Build(ChapterTreeDocument document, ReferenceCatalog? catalog,
        string? discoveryError, CancellationToken token, long catalogMs)
    {
        token.ThrowIfCancellationRequested();
        var entries=document.Entries;
        var localEntries=entries.Where(e=>!e.IsFrontMatter && e.RecognitionSource is not ("reference-volume" or "inferred-volume")).ToArray();
        var restarts=FindVolumeRestarts(localEntries.Select(e=>e.Title).ToArray());
        if(catalog is null)
            return new(document.SourcePath,ExtractBookName(document.SourcePath),false,0,0,
                entries.Count(e=>e.RecognitionSource is "reference-volume" or "inferred-volume"),localEntries.Length,0,0,
                restarts.Count,false,discoveryError is null
                    ? "未取得参考目录，无法判断完整性。请在「目录辅助修复」粘贴书籍网址或导入目录；当前章节树保持原样。"
                    : "目录获取失败："+discoveryError+"。可在「目录辅助修复」导入目录后重试。",Entries:entries)
            { Timing = Finish(catalogMs, 0, 0, null) };
        var plan=ReferencePlanner.Build(document,entries,catalog,token);
        var planMs = _pass?.ElapsedMilliseconds ?? 0;
        token.ThrowIfCancellationRequested();
        var dropped=new List<int>();
        var rebuilt=ReferencePlanner.Apply(document,entries,plan,plan.DefaultSelection.ToArray(),true,catalog.VolumeTitles.Count>0,dropped);
        var applyMs = _pass?.ElapsedMilliseconds ?? 0;
        var inferred=false;
        if(catalog.VolumeTitles.Count==0 && restarts.Count>0 && !entries.Any(e => e.RecognitionSource == "manual"))
        {
            rebuilt=WithInferredVolumes(document,rebuilt,restarts);
            inferred=true;
        }
        RepairIntegrity.Verify(entries,rebuilt,dropped);
        var chapters=rebuilt.Where(e=>!e.IsFrontMatter && e.RecognitionSource is not ("reference-volume" or "inferred-volume")).ToArray();
        // Count by distinct located source line, not a set of title strings: repeated titles in other
        // volumes cannot make an absent chapter appear present.
        var foundLines=chapters.Where(e=>e.TitleLineNumber is not null).Select(e=>e.TitleLineNumber!.Value).ToHashSet();
        // Whether the reference numbered an entry is what separates a chapter somebody has to look for
        // from a leave notice the site collected by mistake. Numbered entries keep the volume-qualified
        // title the report already printed; unnumbered ones need no prefix, they have no volume.
        var unmatched = plan.Location.Chapters.Where(c=>c.Line is null || !foundLines.Remove(c.Line.Value))
            .Select(c=>new UnmatchedChapter(c.Reference.Title,c.Volume,
                ReferenceOutline.ParseKey(c.Reference.Title).Number.Length>0)).ToArray();
        var missing=unmatched.Select(c=>c.Display).ToArray();
        var volumes=rebuilt.Count(e=>e.RecognitionSource is "reference-volume" or "inferred-volume");
        var review=plan.Actions.Count(a=>a.Kind==ReferenceActionKind.RemoveDuplicate && !a.Recommended);
        var aligned=catalog.Titles.Count-missing.Length;
        var verdict=$"已对齐 {aligned}/{catalog.Titles.Count} 章；未定位 {missing.Length} 章；保留目录外 {foundLines.Count} 章；待核对重复 {review} 项"+
            (inferred ? $"；{volumes} 卷为章号重启推断，请核对" : $"；{volumes} 卷");
        var report = BuildReport(plan, rebuilt, unmatched, foundLines);
        // Shown first, because it changes what every other line means.
        if (DescribeCatalogMismatch(catalog, localEntries) is { } mismatch)
            report = new[] { new RepairReportGroup("来源与文件对不上", 1, "项", mismatch, []) }
                .Concat(report).ToArray();
        return new(document.SourcePath,ExtractBookName(document.SourcePath),true,catalog.VolumeTitles.Count,catalog.Titles.Count,
            volumes,chapters.Length,missing.Length,foundLines.Count,restarts.Count,inferred,verdict,
            RemovedLines:dropped.Count,Entries:rebuilt)
        { RemovedSourceLines=dropped,MissingTitles=missing,Unmatched=unmatched,NeedsReview=review,CatalogSource=catalog.Source,Report=report,Plan=plan,VolumeRestartIndexes=restarts,
          Timing=Finish(catalogMs, planMs, applyMs, plan.Location) };
    }

    /// <summary>
    /// Assembles the timing of one pass. <paramref name="planMs"/> and <paramref name="applyMs"/> are
    /// read from the running stopwatch, so they are cumulative — subtracting gives each stage its own
    /// cost without threading a second stopwatch through every call.
    /// </summary>
    private static AutoRepairTiming Finish(long catalogMs, long planMs, long applyMs, ReferenceLocation? location) =>
        new(Math.Max(0, catalogMs), Math.Max(0, planMs), Math.Max(0, applyMs - planMs),
            _pass?.ElapsedMilliseconds ?? 0, catalogMs + (_pass?.ElapsedMilliseconds ?? 0),
            location?.Stats, location?.Timing);

    /// <summary>
    /// Groups the plan's actions into the handful of categories a reader can act on. A long book
    /// produces hundreds of actions and listing them is noise, so each category states how many and
    /// carries the one example — or the volume ranges — that make it concrete. The per-item list stays
    /// available underneath for anyone who wants to check a specific line.
    /// </summary>
    /// <summary>
    /// Rebuilds the tree from a selection the user made by hand in the confirmation window.
    ///
    /// The preview already applied <see cref="ReferencePlan.DefaultSelection"/> once — that is where
    /// the report's counts and "what actually changed" come from — so applying that default again and
    /// then the user's own choice would verify the same tree against itself. This is the one place that
    /// knows both the selection and the volume-inference pass, so the window asks for it rather than
    /// reaching for <see cref="ReferencePlanner.Apply"/> directly; the checks it runs are the same, so
    /// nothing is applied that the preview would have refused.
    /// </summary>
    public static IReadOnlyList<ChapterTreeEntry> RebuildWithSelection(ChapterTreeDocument document,
        AutoRepairOutcome outcome, IReadOnlyCollection<ReferenceAction> selected, CancellationToken token = default)
    {
        if (outcome.Entries is not { } preview || outcome.Plan is not { } plan) return [];
        token.ThrowIfCancellationRequested();
        var dropped = new List<int>();
        var rebuilt = ReferencePlanner.Apply(document, document.Entries, plan, selected, true,
            outcome.Catalog?.VolumeTitles.Count > 0, dropped);
        // Same condition the preview used: volumes only get inferred when the reference did not carry
        // them and the numbering really does restart.
        if (outcome.VolumeRestarts > 0 && outcome.VolumeRestartIndexes.Count > 0
            && outcome.Catalog?.VolumeTitles.Count == 0
            && !document.Entries.Any(entry => entry.RecognitionSource == "manual"))
            rebuilt = WithInferredVolumes(document, rebuilt, outcome.VolumeRestartIndexes);
        RepairIntegrity.Verify(document.Entries, rebuilt, dropped);
        return rebuilt;
    }

    private static IReadOnlyList<RepairReportGroup> BuildReport(
        ReferencePlan plan, IReadOnlyList<ChapterTreeEntry> rebuilt, IReadOnlyList<UnmatchedChapter> unmatched,
        IReadOnlyCollection<int> extraLines)
    {
        var groups = new List<RepairReportGroup>();
        void Add(string label, ReferenceActionKind kind, string unit, Func<ReferenceAction[], string> summary)
        {
            // Every planned action is listed, not only the preselected ones. A row the user cannot see
            // is a row the user cannot cancel, and the window's whole promise is that each change can be
            // struck out; Recommended carries the default state instead of the filter.
            var items = plan.OfKind(kind).Where(action => action.Line > 0)
                .Where(action => kind != ReferenceActionKind.Retitle || rebuilt.Any(e => e.Id == action.EntryId && e.Title == action.Title))
                .Where(action => kind != ReferenceActionKind.AddChapter || rebuilt.Any(e => e.TitleLineNumber == action.Line))
                .Where(action => kind != ReferenceActionKind.DemoteExtra || !rebuilt.Any(e => e.Id == action.EntryId))
                .OrderBy(action => action.Line).ToArray();
            if (items.Length == 0) return;
            // The example has to be one the window will actually tick: now that unselectable-by-default
            // rows are listed too, items[0] can be the one action that will not run.
            var shown = items.FirstOrDefault(action => action.Recommended) ?? items[0];
            groups.Add(new(label, items.Length, unit, summary([shown]),
                items.Select(action => new RepairReportItem(action.Line, action.Title, action.Detail, kind, action.Recommended)).ToArray()));
        }

        // The extras group below has to name the action each heading actually carries, and the plan is
        // the only place that knows: a heading with no chapter number is demoted by default, one that
        // is merely absent from the directory is kept until the user says otherwise.
        var kindOfLine = new Dictionary<int, ReferenceActionKind>();
        foreach (var action in plan.Actions.Where(action => action.Line > 0))
            kindOfLine.TryAdd(action.Line, action.Kind);

        Add("补齐漏识别", ReferenceActionKind.AddChapter, "章",
            items => $"已在原文中定位并收录；例：第 {items[0].Line} 行「{items[0].Title}」");
        Add("标题按目录改写", ReferenceActionKind.Retitle, "章",
            items => $"以参考目录写法为准；例：第 {items[0].Line} 行「{items[0].Title}」");
        Add("标题并回正文", ReferenceActionKind.DemoteExtra, "处", items => "移除所选标题边界，全部文字保留在正文中。");
        Add("移除重复正文", ReferenceActionKind.RemoveDuplicate, "处",
            items => $"参考目录只出现一次，多余的那份已从成品移除；例：第 {items[0].Line} 行「{items[0].Title}」");

        // Counted from the rebuilt tree, not from the plan's actions: the plan sees the tree as it was
        // before headings without a chapter number were folded back into the body, so it reports more
        // extras than the user will actually find. One number, one source — the tree they get.
        var ordered = rebuilt
            .Where(entry => !entry.IsFrontMatter && entry.TitleLineNumber is int line && extraLines.Contains(line)
                && entry.RecognitionSource != "manual")
            .Select(entry =>
            {
                // This group mixes two actions: a heading that does not look like a title at all is
                // demoted by default, while one that merely is not listed keeps its chapter status
                // until the user says otherwise. Read the kind from the plan rather than re-deciding it.
                var kind = kindOfLine.TryGetValue(entry.TitleLineNumber!.Value, out var found)
                    ? found : ReferenceActionKind.KeepExtra;
                return new RepairReportItem(entry.TitleLineNumber!.Value, entry.Title,
                    "参考目录未列出；确认不需要可在方案里勾选，内容会并回上一章。", kind,
                    kind == ReferenceActionKind.DemoteExtra
                        && plan.Actions.Any(a => a.Kind == kind && a.Line == entry.TitleLineNumber!.Value && a.Recommended));
            })
            .OrderBy(item => item.Line)
            .ToArray();
        var extras = ordered;
        if (extras.Length > 0)
            groups.Add(new("目录外章节（需你决定）", extras.Length, "处",
                $"参考目录未列出，已按原位置保留；例：第 {extras[0].Line} 行「{Truncate(extras[0].Title)}」", extras));

        var spans = VolumeSpans(rebuilt);
        if (spans.Count > 0)
            groups.Add(new("建立卷层级", spans.Count, "卷",
                string.Join(" · ", spans.Select(span => $"{span.Title} {span.First}–{span.Last}")), []));

        // Two groups, not one. An entry the directory numbered but the text does not show is a chapter
        // worth looking for; an entry it never numbered is usually a leave notice the aggregator
        // collected, and mixing 13 of the first with 251 of the second is what buried the 13. The
        // numbered ones come first for the same reason: they are the only ones the user has to check.
        var numbered = unmatched.Where(item => item.HasChapterNumber).ToArray();
        if (numbered.Length > 0)
            groups.Add(new("参考目录未匹配", numbered.Length, "章",
                "目录里有章号、原文中未定位到。也可能是版本或写法差异，请先核对来源与原文；软件不会补造正文。",
                numbered.Select(item => new RepairReportItem(0, item.Display, "")).ToArray()));

        var unnumbered = unmatched.Where(item => !item.HasChapterNumber).ToArray();
        if (unnumbered.Length > 0)
            groups.Add(new("疑似公告", unnumbered.Length, "章",
                "参考目录里这些条目没有章号，多为请假、休更一类公告，通常无需处理。",
                unnumbered.Select(item => new RepairReportItem(0, item.Display, "")).ToArray()));

        return groups;
    }

    /// <summary>Keeps a pasted paragraph from swallowing the whole report line.</summary>
    private static string Truncate(string text) => text.Length <= 40 ? text : text[..40] + "…";

    /// <summary>
    /// Names the case where the directory is not for this file at all.
    ///
    /// Without it the user is told "1378 chapters not located", which reads as "your book is
    /// incomplete" — and the reasonable response to that is to download the same book again, which
    /// fixes nothing. The directory in that case belongs to a different edition (a sequel volume,
    /// another release), and the only useful advice is to look up a different source.
    ///
    /// Two conditions together: far more chapters than the file has headings, and a first chapter
    /// whose title does not match. Size alone would merely mean an incomplete file; a title mismatch
    /// means it is not the same book.
    /// </summary>
    private static string? DescribeCatalogMismatch(ReferenceCatalog catalog, IReadOnlyList<ChapterTreeEntry> local)
    {
        if (local.Count == 0 || catalog.Titles.Count <= local.Count * 3) return null;
        var firstReference = catalog.Titles.FirstOrDefault();
        var firstLocal = local.FirstOrDefault()?.Title;
        if (firstReference is null || firstLocal is null) return null;
        if (ReferenceOutline.ParseKey(firstReference).Words == ReferenceOutline.ParseKey(firstLocal).Words) return null;
        return $"参考目录有 {catalog.Titles.Count} 章，首章「{firstReference}」；" +
            $"你的文件识别到 {local.Count} 章，首章「{firstLocal}」。章数相差约 " +
            $"{catalog.Titles.Count / Math.Max(1, local.Count)} 倍，首章标题也不同——这个目录很可能属于" +
            "另一个版本或另一本书，并不是你的文件缺了这些章。建议换一个来源重新获取目录；" +
            "确认文件本身完整之后再考虑补书稿。";
    }

    /// <summary>Each volume with the chapter range it covers, numbered across the whole book.</summary>
    private static IReadOnlyList<(string Title, int First, int Last)> VolumeSpans(IReadOnlyList<ChapterTreeEntry> entries)
    {
        var spans = new List<(string Title, int First, int Last)>();
        string? current = null;
        var index = 0;
        var first = 0;
        var last = 0;
        foreach (var entry in entries)
        {
            if (entry.RecognitionSource == "reference-volume")
            {
                if (current is not null) spans.Add((current, first, last));
                current = entry.Title;
                first = last = 0;
                continue;
            }
            if (entry.IsFrontMatter) continue;
            index++;
            if (current is null) continue;
            if (first == 0) first = index;
            last = index;
        }
        if (current is not null) spans.Add((current, first, last));
        return spans;
    }

    /// <summary>
    /// Inserts a volume node before each restart point, so a book whose source dropped its volume
    /// headings still comes out divided the way the author wrote it. Chapters become level 2.
    /// </summary>
    private static IReadOnlyList<ChapterTreeEntry> WithInferredVolumes(
        ChapterTreeDocument document, IReadOnlyList<ChapterTreeEntry> entries, IReadOnlyList<int> restarts)
    {
        var localEntries = document.Entries.Where(entry => !entry.IsFrontMatter && entry.RecognitionSource is not ("reference-volume" or "inferred-volume")).ToArray();
        var boundaries = restarts
            .Select(index => index < localEntries.Length ? localEntries[index].TitleLineNumber : null)
            .Where(line => line is not null)
            .Select(line => line!.Value)
            .OrderBy(line => line)
            .ToArray();

        var ordered = entries.Where(entry => !entry.IsFrontMatter)
            .OrderBy(entry => entry.TitleLineNumber ?? int.MaxValue)
            .ToArray();
        var result = entries.Where(entry => entry.IsFrontMatter).ToList();
        var boundaryIndex = 0;
        var volume = 1;
        var announced = false;
        foreach (var entry in ordered)
        {
            var line = entry.TitleLineNumber ?? int.MaxValue;
            while (boundaryIndex < boundaries.Length && line >= boundaries[boundaryIndex])
            {
                volume++;
                boundaryIndex++;
                announced = false;
            }
            if (!announced)
            {
                result.Add(new ChapterTreeEntry(Guid.NewGuid().ToString("N"), $"第{volume}卷", 1, true, line, [])
                {
                    RecognitionSource = "inferred-volume"
                });
                announced = true;
            }
            result.Add(entry with { Level = 2 });
        }
        document.CreatePlan(result);
        return result;
    }

    /// <summary>Book title from the file name, ignoring the "(author)" suffix the downloaders append.</summary>
    public static string ExtractBookName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var cut = name.IndexOfAny(['(', '（']);
        return (cut > 0 ? name[..cut] : name).Trim();
    }
}
