namespace EasyPub.Core;

/// <summary>One entry inside a report group: where it is and what was done to it.</summary>
public sealed record RepairReportItem(int Line, string Title, string Detail);

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
    public int NeedsReview { get; init; }
    public string? CatalogSource { get; init; }
    /// <summary>Categorised account of what the repair changed, ready to display.</summary>
    public IReadOnlyList<RepairReportGroup> Report { get; init; } = [];
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
    /// falls back to the built-in order instead of blocking the repair.
    /// </summary>
    private static async Task<IReadOnlyList<string>> PreferredSourcesAsync(string path)
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
        var document = currentDocument ?? await ChapterTreeDocument.LoadAsync(path, cancellationToken: token).ConfigureAwait(false);
        var bookName = ExtractBookName(path);
        string? error = null;
        if (reference is null)
        {
            progress?.Report("正在查找本书已保存的目录与来源…");
            var preferred = await PreferredSourcesAsync(path).ConfigureAwait(false);
            var saved = ReferenceCatalogInput.Load(document.SourceSha256);
            reference = string.IsNullOrWhiteSpace(saved?.Text) ? null : ReferenceCatalogInput.ParseText(saved.Text);
            if (reference is not null && !string.IsNullOrWhiteSpace(saved?.Url)) reference = reference with { Source = saved.Url + "（本书已保存目录）" };
            if (reference is null)
            {
                var query = !string.IsNullOrWhiteSpace(saved?.Url) ? saved.Url
                    : await Task.Run(() => ReferenceCatalogInput.FindLocalBookUrl(path), token).ConfigureAwait(false)
                    ?? (!string.IsNullOrWhiteSpace(saved?.Query) ? saved.Query : bookName);
                progress?.Report("正在获取参考目录，可随时取消…");
                try
                {
                    var catalogs = await new ReferenceCatalogClient().DiscoverAsync(query,token,preferred).ConfigureAwait(false);
                    var most = catalogs.Count == 0 ? 0 : catalogs.Max(c=>c.Titles.Count);
                    reference = catalogs.FirstOrDefault(c=>c.Titles.Count > 0 && c.Titles.Count >= most*0.9);
                }
                catch (Exception e) when (!token.IsCancellationRequested && e is HttpRequestException or OperationCanceledException or InvalidOperationException)
                { error=e.Message; }
            }
        }
        token.ThrowIfCancellationRequested();
        progress?.Report("正在核对章节、分配正文并检查行完整性…");
        return await Task.Run(() => Prepare(document,reference,error,token),token).ConfigureAwait(false);
    }

    public static AutoRepairOutcome Prepare(ChapterTreeDocument document, ReferenceCatalog? catalog,
        string? discoveryError = null, CancellationToken token = default)
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
                    : "目录获取失败："+discoveryError+"。可在「目录辅助修复」导入目录后重试。",Entries:entries);
        var plan=ReferencePlanner.Build(document,entries,catalog,token);
        token.ThrowIfCancellationRequested();
        var dropped=new List<int>();
        var rebuilt=ReferencePlanner.Apply(document,entries,plan,plan.DefaultSelection.ToArray(),true,catalog.VolumeTitles.Count>0,dropped);
        var inferred=false;
        if(catalog.VolumeTitles.Count==0 && restarts.Count>0)
        {
            rebuilt=WithInferredVolumes(document,rebuilt,restarts);
            inferred=true;
        }
        RepairIntegrity.Verify(entries,rebuilt,dropped);
        var chapters=rebuilt.Where(e=>!e.IsFrontMatter && e.RecognitionSource is not ("reference-volume" or "inferred-volume")).ToArray();
        // Count by distinct located source line, not a set of title strings: repeated titles in other
        // volumes cannot make an absent chapter appear present.
        var foundLines=chapters.Where(e=>e.TitleLineNumber is not null).Select(e=>e.TitleLineNumber!.Value).ToHashSet();
        var missing=plan.Location.Chapters.Where(c=>c.Line is null || !foundLines.Remove(c.Line.Value))
            .Select(c=>(c.Volume is null ? "" : c.Volume+" · ")+c.Reference.Title).ToArray();
        var volumes=rebuilt.Count(e=>e.RecognitionSource is "reference-volume" or "inferred-volume");
        var review=plan.Actions.Count(a=>a.Kind==ReferenceActionKind.RemoveDuplicate && !a.Recommended);
        var verdict=$"已对齐 {catalog.Titles.Count-missing.Length}/{catalog.Titles.Count} 章；未定位 {missing.Length} 章；保留目录外 {foundLines.Count} 章；待核对重复 {review} 项"+
            (inferred ? $"；{volumes} 卷为章号重启推断，请核对" : $"；{volumes} 卷");
        var report = BuildReport(plan, rebuilt, missing, foundLines);
        return new(document.SourcePath,ExtractBookName(document.SourcePath),true,catalog.VolumeTitles.Count,catalog.Titles.Count,
            volumes,chapters.Length,missing.Length,foundLines.Count,restarts.Count,inferred,verdict,
            RemovedLines:dropped.Count,Entries:rebuilt)
        { RemovedSourceLines=dropped,MissingTitles=missing,NeedsReview=review,CatalogSource=catalog.Source,Report=report };
    }

    /// <summary>
    /// Groups the plan's actions into the handful of categories a reader can act on. A long book
    /// produces hundreds of actions and listing them is noise, so each category states how many and
    /// carries the one example — or the volume ranges — that make it concrete. The per-item list stays
    /// available underneath for anyone who wants to check a specific line.
    /// </summary>
    private static IReadOnlyList<RepairReportGroup> BuildReport(
        ReferencePlan plan, IReadOnlyList<ChapterTreeEntry> rebuilt, IReadOnlyList<string> missing,
        IReadOnlyCollection<int> extraLines)
    {
        var groups = new List<RepairReportGroup>();
        void Add(string label, ReferenceActionKind kind, string unit, Func<ReferenceAction[], string> summary)
        {
            var items = plan.OfKind(kind).Where(action => action.Line > 0).OrderBy(action => action.Line).ToArray();
            if (items.Length == 0) return;
            groups.Add(new(label, items.Length, unit, summary(items),
                items.Select(action => new RepairReportItem(action.Line, action.Title, action.Detail)).ToArray()));
        }

        Add("补齐漏识别", ReferenceActionKind.AddChapter, "章",
            items => $"已在原文中定位并收录；例：第 {items[0].Line} 行「{items[0].Title}」");
        Add("标题按目录改写", ReferenceActionKind.Retitle, "章",
            items => $"以参考目录写法为准；例：第 {items[0].Line} 行「{items[0].Title}」");
        Add("移除重复正文", ReferenceActionKind.RemoveDuplicate, "处",
            items => $"参考目录只出现一次，多余的那份已从成品移除；例：第 {items[0].Line} 行「{items[0].Title}」");

        // Counted from the rebuilt tree, not from the plan's actions: the plan sees the tree as it was
        // before headings without a chapter number were folded back into the body, so it reports more
        // extras than the user will actually find. One number, one source — the tree they get.
        var extras = rebuilt
            .Where(entry => !entry.IsFrontMatter && entry.TitleLineNumber is int line && extraLines.Contains(line)
                && entry.RecognitionSource != "manual")
            .Select(entry => new RepairReportItem(entry.TitleLineNumber!.Value, entry.Title,
                "参考目录未列出；确认不需要可在方案里勾选，内容会并回上一章。"))
            .ToArray();
        if (extras.Length > 0)
            groups.Add(new("目录外章节（需你决定）", extras.Length, "处",
                $"参考目录未列出，已按原位置保留；例：第 {extras[0].Line} 行「{Truncate(extras[0].Title)}」", extras));

        var spans = VolumeSpans(rebuilt);
        if (spans.Count > 0)
            groups.Add(new("建立卷层级", spans.Count, "卷",
                string.Join(" · ", spans.Select(span => $"{span.Title} {span.First}–{span.Last}")), []));

        if (missing.Count > 0)
            groups.Add(new("目录里有、源文本找不到", missing.Count, "章",
                "这些章节在源 TXT 里没有对应标题，需要换来源或人工补齐——软件不会凭空造出正文。",
                missing.Select(title => new RepairReportItem(0, title, "")).ToArray()));

        return groups;
    }

    /// <summary>Keeps a pasted paragraph from swallowing the whole report line.</summary>
    private static string Truncate(string text) => text.Length <= 40 ? text : text[..40] + "…";

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
