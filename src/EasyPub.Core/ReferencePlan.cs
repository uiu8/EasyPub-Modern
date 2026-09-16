namespace EasyPub.Core;

public enum ReferenceActionKind
{
    /// <summary>参考目录有、章节树没有，但原文中已定位到标题行。可直接收录。</summary>
    AddChapter,
    /// <summary>章节已存在，但标题与参考目录写法不同。</summary>
    Retitle,
    /// <summary>同一章在原文出现多次；参考目录只出现一次，可只保留与参考位置一致的一份。</summary>
    RemoveDuplicate,
    /// <summary>本地有、参考目录没有。只提示，永不自动处理。</summary>
    KeepExtra,
    /// <summary>参考目录的卷结构无法在原文中落地（卷名与章标题同行），仅作提示。</summary>
    VolumeNote,
    /// <summary>建议把目录外的疑似正文标题并回正文；必须明确选中。</summary>
    DemoteExtra
}

public sealed record ReferenceAction(
    ReferenceActionKind Kind,
    int Line,
    string Title,
    string Detail,
    string? EntryId,
    ReferenceNode? Reference,
    bool Recommended)
{
    /// <summary>
    /// Stable identity of one planned action, so that a change in the finished tree can say which
    /// action produced it. The three parts are what the report and the action list already agree on:
    /// the kind of action, the source line it is anchored to, and the title it names.
    /// </summary>
    public string Key => ReferenceAction.KeyOf(Kind, Line, Title);

    public static string KeyOf(ReferenceActionKind kind, int line, string title) =>
        string.Join('|', kind, line.ToString(System.Globalization.CultureInfo.InvariantCulture), title);
}

public sealed record ReferencePlan(
    ReferenceCatalog Catalog,
    ReferenceLocation Location,
    IReadOnlyList<ReferenceAction> Actions)
{
    public IReadOnlyList<ReferenceAction> OfKind(ReferenceActionKind kind) => Actions.Where(a => a.Kind == kind).ToArray();
    public int AddCount => OfKind(ReferenceActionKind.AddChapter).Count;
    public int RetitleCount => OfKind(ReferenceActionKind.Retitle).Count;
    public int DuplicateCount => OfKind(ReferenceActionKind.RemoveDuplicate).Count;
    public int ExtraCount => OfKind(ReferenceActionKind.KeepExtra).Count;
    public int DuplicatedLines => Location.Chapters.Where(c => c.IsDuplicate).Sum(c => c.Lines.Count - 1);

    /// <summary>Actions safe to pre-select: they only add structure the reference confirms.</summary>
    public IEnumerable<ReferenceAction> DefaultSelection => Actions.Where(a => a.Recommended && a.Kind is
        ReferenceActionKind.AddChapter or ReferenceActionKind.Retitle or ReferenceActionKind.RemoveDuplicate or ReferenceActionKind.DemoteExtra);
}

/// <summary>
/// Builds a reviewable plan that reconciles the local chapter tree with a reference directory,
/// and applies the parts the user selects. This is the general form of "adjust the chapter tree
/// from the official directory": it never rewrites the source text, never invents a chapter that
/// was not located in the text, and every change is a normal chapter-tree edit that can be undone.
/// </summary>
public static class ReferencePlanner
{
    /// <summary>
    /// Longest title a chapter outside the official directory may keep. Real headings are short —
    /// even "番外 第二章 混蛋兄妹" is thirteen characters — while body prose runs to whole sentences,
    /// so this separates the two without needing to understand the text.
    /// </summary>
    private const int MaximumExtraTitleLength = 40;

    public static ReferencePlan Build(ChapterTreeDocument document, IReadOnlyList<ChapterTreeEntry> entries, ReferenceCatalog catalog, CancellationToken cancellationToken = default)
    {
        var available = RepairIntegrity.Coverage(entries);
        var lines = Enumerable.Range(1, document.LineCount).Select(index => available.Contains(index) ? document.SourceLine(index)?.Text ?? "" : "").ToArray();
        var knownHeadings = entries.Where(e => !e.IsFrontMatter && e.TitleLineNumber is not null
                && e.RecognitionSource is not ("reference-volume" or "inferred-volume"))
            .GroupBy(e => e.TitleLineNumber!.Value).ToDictionary(g => g.Key, g => g.Last().Title);
        var location = ReferenceLocator.Locate(lines, catalog, knownHeadings, cancellationToken);
        var actions = new List<ReferenceAction>();
        var assignedLines = location.Chapters.Where(c => c.Line is not null).Select(c => c.Line!.Value).ToHashSet();
        var byLine = new Dictionary<int, ChapterTreeEntry>();
        foreach (var entry in entries.Where(e => !e.IsFrontMatter && e.TitleLineNumber is not null))
            byLine.TryAdd(entry.TitleLineNumber!.Value, entry);

        foreach (var chapter in location.Chapters)
        {
            var volume = chapter.Volume is null ? "" : $"[{chapter.Volume}] ";
            if (chapter.Line is null)
            {
                actions.Add(new(ReferenceActionKind.AddChapter, 0, chapter.Reference.Title,
                    $"{volume}参考目录有此章，但原文中未找到对应标题行，需要人工核对，不会自动插入。",
                    null, chapter.Reference, false));
                continue;
            }
            var line = chapter.Line.Value;
            if (byLine.TryGetValue(line, out var existing))
            {
                if (!string.Equals(existing.Title.Trim(), chapter.Reference.Title.Trim(), StringComparison.Ordinal))
                    actions.Add(new(ReferenceActionKind.Retitle, line, chapter.Reference.Title,
                        $"{volume}当前标题「{existing.Title}」与参考目录「{chapter.Reference.Title}」不同；勾选后使用参考标题。",
                        existing.Id, chapter.Reference, existing.RecognitionSource != "manual"));
            }
            else
            {
                actions.Add(new(ReferenceActionKind.AddChapter, line, chapter.Reference.Title,
                    $"{volume}原文第 {line} 行是「{chapter.LocalTitle}」，章节树未收录；勾选后按参考目录收录该章。",
                    null, chapter.Reference, true));
            }
            if (chapter.IsDuplicate)
            {
                var others = chapter.Lines.Where(value => value != line && !assignedLines.Contains(value) && byLine.ContainsKey(value)).ToArray();
                if (others.Length == 0) continue;
                var safe = others.All(other => byLine[other].RecognitionSource != "manual"
                    && !HasChildren(entries, byLine[other]) && SameBody(document, entries, line, other));
                actions.Add(new(ReferenceActionKind.RemoveDuplicate, line, chapter.Reference.Title,
                    $"{volume}「{chapter.Reference.Title}」在原文出现 {chapter.Occurrences} 次（第 {string.Join("、", chapter.Lines)} 行），" +
                    $"参考目录只出现一次。保留第 {line} 行（与参考位置一致），仅正文一致时默认移除，其余需对比后手动选择。",
                    byLine.TryGetValue(line, out var owner) ? owner.Id : null, chapter.Reference, safe));
            }
        }

        var locatedLines = location.Chapters.Where(c => c.Line is not null).Select(c => c.Line!.Value).ToHashSet();
        // Headings the user added by hand are not "extras to review": they are an explicit decision,
        // so they get no action and no alignment pass is allowed to take them away.
        foreach (var entry in entries.Where(e => !e.IsFrontMatter && e.TitleLineNumber is int line
                     && !locatedLines.Contains(line) && e.RecognitionSource != "manual"))
        {
            var demote = ReferenceOutline.ParseKey(entry.Title).Number.Length == 0
                || entry.Title.Trim().Length > MaximumExtraTitleLength;
            actions.Add(new(demote ? ReferenceActionKind.DemoteExtra : ReferenceActionKind.KeepExtra,
                entry.TitleLineNumber!.Value, entry.Title,
                $"「{entry.Title}」未列入参考目录。勾选后并回正文（保留全部文字）；不勾选则保留章节。", entry.Id, null, demote));
        }

        if (catalog.VolumeTitles.Count > 1)
        {
            var embedded = CountVolumesOnChapterLines(lines, catalog);
            var detail = embedded > 0
                ? $"参考目录含 {catalog.VolumeTitles.Count} 卷；原文中有 {embedded} 处卷名与章标题同行，可建立独立卷层级，不新增正文。"
                : $"参考目录含 {catalog.VolumeTitles.Count} 卷，原文中存在独立卷标题行，可另行建立卷层级。";
            actions.Add(new(ReferenceActionKind.VolumeNote, 0, string.Join("、", catalog.VolumeTitles), detail, null, null, false));
        }
        return new(catalog, location, actions);
    }

    /// <summary>
    /// Applies the selected actions and returns the rebuilt chapter tree. Titles come from the
    /// reference only when <paramref name="useReferenceTitles"/> is set; otherwise the original
    /// line text is kept, which preserves embedded volume prefixes.
    /// </summary>
    public static IReadOnlyList<ChapterTreeEntry> Apply(
        ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry> entries,
        ReferencePlan plan,
        IReadOnlyCollection<ReferenceAction> selected,
        bool useReferenceTitles,
        bool buildVolumeLevels = false,
        ICollection<int>? droppedLines = null)
    {
        var chosen = selected.ToHashSet();
        if (chosen.Count == 0 && !buildVolumeLevels) return entries;
        // A hand-edited tree owns its order, levels and body ranges. Reconcile locally instead
        // of reconstructing it from physical source positions.
        if (entries.Any(e => e.RecognitionSource == "manual" || (!e.IsFrontMatter && e.TitleLineNumber is null)))
            return ApplyPreservingEdits(document, entries, plan, chosen, useReferenceTitles, droppedLines);
        var allowed = RepairIntegrity.Coverage(entries);
        var keep = new SortedDictionary<int, ChapterTreeEntry>();
        foreach (var entry in entries.Where(e => !e.IsFrontMatter && e.TitleLineNumber is not null))
            keep[entry.TitleLineNumber!.Value] = entry;
        var reserved = plan.Location.Chapters.Where(c => c.Line is not null).Select(c => c.Line!.Value).ToHashSet();
        var dropped = new HashSet<int>();
        var originalBoundaries = keep.Keys.Concat(reserved).Distinct().Order().ToArray();
        foreach (var action in plan.Actions.Where(chosen.Contains))
        {
            switch (action.Kind)
            {
                case ReferenceActionKind.AddChapter when action.Line > 0 && allowed.Contains(action.Line):
                    keep.TryAdd(action.Line, new(Guid.NewGuid().ToString("N"),
                        useReferenceTitles ? action.Title : document.SourceLine(action.Line)!.Text.Trim(), 1, true, action.Line, []));
                    break;
                case ReferenceActionKind.Retitle when useReferenceTitles && keep.TryGetValue(action.Line, out var current):
                    keep[action.Line] = current with { Title = action.Title };
                    break;
                case ReferenceActionKind.RemoveDuplicate:
                    var located = plan.Location.Chapters.FirstOrDefault(c => ReferenceEquals(c.Reference, action.Reference));
                    if (located is null) break;
                    foreach (var line in located.Lines.Where(value => value != action.Line && !reserved.Contains(value)))
                    {
                        if (!keep.Remove(line)) continue;
                        var end = originalBoundaries.FirstOrDefault(value => value > line, document.LineCount + 1);
                        for (var row = line; row < end; row++) if (allowed.Contains(row)) dropped.Add(row);
                    }
                    break;
                case ReferenceActionKind.DemoteExtra when action.Line > 0:
                case ReferenceActionKind.KeepExtra when action.Line > 0:
                    // Selected means "this heading is not in the official directory, so it must not
                    // stand as a chapter". Only the heading boundary goes away — its lines fall into
                    // the preceding chapter's range instead of being dropped, so the text is kept
                    // whole and RepairIntegrity still balances. Left unselected, the entry survives,
                    // which is the deliberate default: the user decides, not the alignment.
                    keep.Remove(action.Line);
                    break;
            }
        }
        // Remove heading boundaries before allocating text: a demoted note remains ordinary body text.
        // The number test alone is not enough. ChapterNumber treats a bare "节" as a section marker,
        // so prose like "九节鞭、三节棍，随意变换的冰武…" parses as "第 9 节" and would keep its
        // chapter status. Anything outside the directory that runs past a plausible title length is
        // body text whatever its number looks like; user-added headings stay untouched either way.
        if (keep.Count == 0) return entries;
        allowed.ExceptWith(dropped);
        var ordered = keep.Keys.ToArray();
        var result = new List<ChapterTreeEntry>();
        var prefix = allowed.Where(line => line < ordered[0]).ToArray();
        if (prefix.Length > 0)
        {
            var front = entries.FirstOrDefault(e => e.IsFrontMatter)
                ?? new ChapterTreeEntry(Guid.NewGuid().ToString("N"), "前言", 2, false, null, []) { IsFrontMatter = true };
            result.Add(front with { TitleLineNumber = null, ContentRanges = RepairIntegrity.Ranges(prefix) });
        }
        var ranges = new Dictionary<int, IReadOnlyList<ChapterSourceRange>>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var end = i+1 < ordered.Length ? ordered[i+1] : document.LineCount+1;
            ranges[ordered[i]] = RepairIntegrity.Ranges(Enumerable.Range(ordered[i]+1, end-ordered[i]-1).Where(allowed.Contains));
        }
        var order = new Dictionary<int, double>();
        var volumeOfLine = new Dictionary<int, string>();
        foreach (var chapter in plan.Location.Chapters)
            if (chapter.Line is int line && keep.ContainsKey(line))
            {
                order.TryAdd(line, order.Count);
                if (buildVolumeLevels && chapter.Volume is { Length: > 0 } volume) volumeOfLine.TryAdd(line, volume);
            }
        // Extras remain beside their physical neighbours instead of being appended to the book's end.
        foreach (var line in ordered.Where(line => !order.ContainsKey(line)))
        {
            var previous = ordered.LastOrDefault(value => value < line && reserved.Contains(value));
            var next = ordered.FirstOrDefault(value => value > line && reserved.Contains(value));
            order[line] = previous > 0 && order.TryGetValue(previous, out var rank) ? rank + 0.5
                : next > 0 && order.TryGetValue(next, out rank) ? rank - 0.5 : double.MaxValue;
        }
        var anchors = buildVolumeLevels ? VolumeAnchors(plan) : [];
        var volumeOrder = plan.Catalog.VolumeTitles.Distinct().Select((title, index) => (title, index))
            .ToDictionary(pair => pair.title, pair => pair.index);
        string? VolumeFor(int line) => volumeOfLine.TryGetValue(line, out var owned) ? owned : VolumeAt(anchors, line);
        string? currentVolume = null;
        foreach (var line in ordered.OrderBy(line => VolumeFor(line) is string volume && volumeOrder.TryGetValue(volume, out var rank) ? rank : -1)
                     .ThenBy(line => order[line]).ThenBy(line => line))
        {
            var volume = VolumeFor(line);
            if (volume is not null && volume != currentVolume)
                result.Add(new ChapterTreeEntry(Guid.NewGuid().ToString("N"),volume,1,true,line,[]) { RecognitionSource = "reference-volume" });
            currentVolume = volume;
            result.Add(keep[line] with { Level = volume is null ? 1 : 2, ContentRanges = ranges[line],
                // A heading the user added by hand keeps its mark: the reports use it to leave those
                // entries out of "needs your decision", because the user already decided.
                RecognitionSource = keep[line].RecognitionSource == "manual" ? "manual"
                    : reserved.Contains(line) ? "reference" : keep[line].RecognitionSource });
        }
        RepairIntegrity.Verify(entries,result,dropped);
        document.CreatePlan(result);
        foreach (var line in dropped.Order()) droppedLines?.Add(line);
        return result;
    }

    private static IReadOnlyList<ChapterTreeEntry> ApplyPreservingEdits(ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry> entries, ReferencePlan plan, HashSet<ReferenceAction> chosen,
        bool useReferenceTitles, ICollection<int>? droppedLines)
    {
        var result = entries.ToList();
        foreach (var action in plan.Actions.Where(chosen.Contains))
        {
            var index = result.FindIndex(e => e.Id == action.EntryId);
            if (action.Kind == ReferenceActionKind.Retitle && useReferenceTitles && index >= 0)
                result[index] = result[index] with { Title = action.Title };
            else if (action.Kind == ReferenceActionKind.AddChapter && action.Line > 0
                && !result.Any(e => e.TitleLineNumber == action.Line))
            {
                index = result.FindIndex(e => e.ContentRanges.Any(r => r.StartLine <= action.Line && r.EndLine >= action.Line));
                if (index < 0 || result[index].RecognitionSource == "manual") continue;
                var owner = result[index];
                var body = RepairIntegrity.Coverage([owner]);
                if (owner.TitleLineNumber is int title) body.Remove(title);
                var tail = body.Where(line => line > action.Line).ToArray();
                result[index] = owner with { ContentRanges = RepairIntegrity.Ranges(body.Where(line => line < action.Line)) };
                result.Insert(index + 1, new ChapterTreeEntry(Guid.NewGuid().ToString("N"),
                    useReferenceTitles ? action.Title : document.SourceLine(action.Line)!.Text.Trim(),
                    owner.IsFrontMatter ? 1 : owner.Level, true, action.Line, RepairIntegrity.Ranges(tail)) { RecognitionSource = "reference" });
            }
            else if (action.Kind == ReferenceActionKind.RemoveDuplicate)
            {
                var located = plan.Location.Chapters.FirstOrDefault(c => ReferenceEquals(c.Reference, action.Reference));
                var reserved = plan.Location.Chapters.Where(c => c.Line is not null).Select(c => c.Line!.Value).ToHashSet();
                if (located is null) continue;
                result.RemoveAll(e => e.RecognitionSource != "manual" && e.TitleLineNumber is int line
                    && line != action.Line && !reserved.Contains(line) && located.Lines.Contains(line)
                    && !HasChildren(entries, e));
            }
            else if (action.Kind is ReferenceActionKind.KeepExtra or ReferenceActionKind.DemoteExtra
                && index > 0 && result[index].RecognitionSource != "manual" && !HasChildren(result, result[index])
                && result[index - 1].RecognitionSource != "manual")
            {
                var previous = result[index - 1];
                var merged = RepairIntegrity.Coverage([previous, result[index]]);
                if (previous.TitleLineNumber is int title) merged.Remove(title);
                result[index - 1] = previous with { ContentRanges = RepairIntegrity.Ranges(merged) };
                result.RemoveAt(index);
            }
        }
        var removed = RepairIntegrity.Coverage(entries);
        removed.ExceptWith(RepairIntegrity.Coverage(result));
        RepairIntegrity.Verify(entries, result, removed);
        document.CreatePlan(result);
        foreach (var line in removed.Order()) droppedLines?.Add(line);
        return result;
    }

    private static bool HasChildren(IReadOnlyList<ChapterTreeEntry> entries, ChapterTreeEntry entry)
    {
        var index = entries.ToList().FindIndex(e => e.Id == entry.Id);
        return index >= 0 && index + 1 < entries.Count && entries[index + 1].Level > entry.Level;
    }

    private static bool SameBody(ChapterTreeDocument document, IReadOnlyList<ChapterTreeEntry> entries, int first, int second)
    {
        string? Body(int line)
        {
            var entry = entries.LastOrDefault(e => e.TitleLineNumber == line);
            return entry is null ? null : string.Concat(entry.ContentRanges.SelectMany(r => Enumerable.Range(r.StartLine,r.EndLine-r.StartLine+1))
                .SelectMany(row => (document.SourceLine(row)?.Text ?? "").Where(c => !char.IsWhiteSpace(c))));
        }
        var body = Body(first);
        return body is not null && body == Body(second);
    }

    /// <summary>
    /// Anchor line of each reference volume: the located line of that volume's first chapter, in
    /// reference order, sorted by position. Ownership is decided positionally from these anchors, so
    /// a single mislocated chapter cannot make two volumes overlap and swallow each other.
    /// </summary>
    private static (string Volume, int Line)[] VolumeAnchors(ReferencePlan plan)
    {
        return plan.Location.Chapters
            .Where(chapter => chapter.Line is not null && chapter.Volume is { Length: > 0 })
            .GroupBy(chapter => chapter.Volume!, StringComparer.Ordinal)
            .Select(group => (Volume: group.Key, Line: group.First().Line!.Value))
            .OrderBy(anchor => anchor.Line)
            .ToArray();
    }

    /// <summary>Volume owning a source line: the last anchor at or before it.</summary>
    private static string? VolumeAt(IReadOnlyList<(string Volume, int Line)> anchors, int line)
    {
        if (anchors.Count == 0) return null;
        string? current = null;
        foreach (var anchor in anchors)
        {
            if (line < anchor.Line) break;
            current = anchor.Volume;
        }
        // A heading printed before the first volume's opening chapter — a preface, a stray promo
        // block — still belongs to that volume. Leaving it unowned floats it above the very volume
        // it sits inside, which reads as a scrambled tree.
        return current ?? anchors[0].Volume;
    }

    /// <summary>Counts chapter lines that also carry a volume name, i.e. volumes that cannot become their own level.</summary>
    private static int CountVolumesOnChapterLines(IReadOnlyList<string> lines, ReferenceCatalog catalog)
    {
        var prefixes = catalog.VolumeTitles
            .Select(title => ReferenceOutline.ParseKey(title).Words)
            .Where(words => words.Length >= 2)
            .ToArray();
        if (prefixes.Length == 0) return 0;
        var count = 0;
        foreach (var line in lines)
        {
            var text = line.Trim();
            if (text.Length is 0 or > 60) continue;
            var words = ReferenceOutline.ParseKey(text).Words;
            if (words.Length == 0) continue;
            if (prefixes.Any(prefix => words.StartsWith(prefix, StringComparison.Ordinal))) count++;
        }
        return count;
    }
}
