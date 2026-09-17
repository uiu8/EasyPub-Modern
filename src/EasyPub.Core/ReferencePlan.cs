namespace EasyPub.Core;

public enum ReferenceActionKind
{
    /// <summary>参考目录有、章节树没有，但原文中已定位到标题行。可直接收录。</summary>
    AddChapter,
    /// <summary>章节已存在，但标题与参考目录写法不同。</summary>
    Retitle,
    /// <summary>
    /// 参考目录有、章节树没有，而且这一段原文里根本没有标题行——只有一段正文。正文确实在，
    /// 缺的只是"哪一行算标题"。默认不勾选：把正文里的某一行立成标题会改变成品的章节划分，
    /// 必须由人看过再决定。可以收录，因为"被并进邻章的正文"和"真的没有正文"必须分开报。
    /// </summary>
    AdoptHeading,
    /// <summary>
    /// 参考目录有，但原文里这一章的位置既没有标题、也没有正文。软件不会补造正文，只如实报告。
    /// 仅作提示，永远不可勾选。
    /// </summary>
    MissingBody,
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
        // Every source line goes to the locator, including the ones the chapter tree already covers.
        // Blanking them out was meant to stop a known heading from being matched a second time, and it
        // did that — but it also made the *body* of a stretch whose headings were never recognised
        // invisible: a run of paragraphs between two chapters reads as "no content here" to the
        // locator, which then reaches for a heading far away and drags the whole tail of the book out
        // of order. knownHeadings already says which lines are headings, so the locator can have the
        // text as well.
        var lines = Enumerable.Range(1, document.LineCount).Select(index => document.SourceLine(index)?.Text ?? "").ToArray();
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
                // A chapter the locator could not pin down is one of three quite different things, and
                // lumping them together is what made "not located" useless: the body may be sitting in
                // the text with its heading lost (recoverable), or the chapter may genuinely be absent
                // (not recoverable). Both used to be reported the same way, and the second kind used to
                // be reported not at all.
                var gap = NeighbourGapOf(location, chapter);
                var heading = FindHeadingCandidate(document, entries, gap);
                var occupied = GapHasOccupiedLines(entries, gap);
                actions.Add(heading is { } found
                    ? new(ReferenceActionKind.AdoptHeading, found.Line, chapter.Reference.Title,
                        $"{volume}这一段原文没有标题行；第 {found.Line} 行「{TruncateTitle(found.Text)}」最像标题。" +
                        "勾选后以该行为章节标题，正文保持原样、一行不动；不勾选则原文保持不动。",
                        null, chapter.Reference, false)
                    : occupied
                        ? new(ReferenceActionKind.MissingBody, 0, chapter.Reference.Title,
                            $"{volume}参考目录有此章，但它该在的位置被别的内容占着（原文这一段已归属其他章节）。" +
                            "多半是源文件重复拼接或版本差异，需要人工核对哪一份才是这一章；软件不会自动挪动正文。",
                            null, chapter.Reference, false)
                        : new(ReferenceActionKind.MissingBody, 0, chapter.Reference.Title,
                            $"{volume}参考目录有此章，但原文这一段里既没有标题行、也没有对应正文。软件不会补造正文，请核对来源与文件完整性。",
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
        // One chapter, one action. A chapter can produce both an AddChapter (a line agreeing on the
        // number) and an AdoptHeading (a heading-looking line elsewhere in the gap), and offering both
        // would put two rows in the change list for the same directory entry — a row the user can tick
        // twice, producing two entries for one chapter. The located line wins: it was confirmed, the
        // adopted one was guessed.
        var located = actions.Where(action => action.Kind == ReferenceActionKind.AddChapter)
            .Select(action => action.Title).ToHashSet(StringComparer.Ordinal);
        if (located.Count > 0)
            actions.RemoveAll(action => action.Kind == ReferenceActionKind.AdoptHeading && located.Contains(action.Title));
        return new(catalog, location, actions);
    }

    /// <summary>
    /// The lines a chapter could be hiding in: strictly between the chapters the locator did place,
    /// before and after it in reference order.
    /// </summary>
    private static (int Lower, int Upper) NeighbourGapOf(ReferenceLocation location, LocatedChapter chapter)
    {
        var index = location.Chapters.ToList().FindIndex(candidate => ReferenceEquals(candidate, chapter));
        var lower = 0;
        var upper = int.MaxValue;
        for (var probe = index - 1; probe >= 0; probe--)
            if (location.Chapters[probe].Line is int before) { lower = before; break; }
        for (var probe = index + 1; probe < location.Chapters.Count; probe++)
            if (location.Chapters[probe].Line is int after) { upper = after; break; }
        return (lower, upper);
    }

    /// <summary>
    /// Finds the line inside a gap that could stand as the missing chapter's heading. Deliberately
    /// strict — the line must be short and must not read as a sentence — because the result is offered
    /// to the user as "this could be the title", and a wrong offer costs more than a missing one: the
    /// body would be cut in the wrong place.
    ///
    /// A line already covered by another chapter's body is still a candidate. That is the normal shape
    /// of this defect: when a heading goes unrecognised, the paragraph that carried it is simply
    /// absorbed into the chapter above, so insisting on "no owner" would find nothing in exactly the
    /// case the check exists for. Adopting it cuts the line back out of that chapter at apply time.
    /// </summary>
    private static (int Line, string Text)? FindHeadingCandidate(ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry> entries, (int Lower, int Upper) gap)
    {
        // A heading the tree already has is not a candidate: recommending "第六十九章 …" for a missing
        // 第七十七章 would put the same line in two chapters. Only lines that are body text today can
        // be adopted as a heading.
        var headings = entries.Where(e => !e.IsFrontMatter && e.TitleLineNumber is not null)
            .Select(e => e.TitleLineNumber!.Value).ToHashSet();
        var from = Math.Max(1, gap.Lower + 1);
        var to = Math.Min(document.LineCount, gap.Upper - 1);
        for (var line = from; line <= to; line++)
        {
            if (headings.Contains(line)) continue;
            var text = document.SourceLine(line)?.Text.Trim() ?? "";
            if (text.Length is < 2 or > 40) continue;
            if (text.IndexOfAny(['。', '！', '？', '；']) >= 0) continue;
            if (text.All(c => char.IsDigit(c) || char.IsWhiteSpace(c))) continue;
            // The line has to look like a chapter heading to be worth offering as one. Aggregator
            // releases put leave notices between chapters ("请假一天", "今天的更新也会晚一些") and those
            // are short standalone lines too — recommending one as a title would invent a chapter out
            // of an announcement. A number, or a 章/回 marker, is what a real heading has.
            if (!text.Any(char.IsDigit) && text.IndexOfAny(['章', '回', '节']) < 0) continue;
            return (line, text);
        }
        return null;
    }

    /// <summary>
    /// True when the gap a missing chapter should occupy is already owned by other chapters. That is a
    /// different finding from an empty stretch: the text is there, it just belongs somewhere else —
    /// which is what a duplicated block in the source looks like from the directory's point of view.
    /// Telling the two apart is the difference between "your file is incomplete" and "your file has a
    /// repeated section", and only one of those is worth re-downloading the book for.
    /// </summary>
    private static bool GapHasOccupiedLines(IReadOnlyList<ChapterTreeEntry> entries, (int Lower, int Upper) gap)
    {
        // A gap can come out reversed when the placed neighbours themselves are out of order — the
        // locator guarantees each chapter a line, not that the lines ascend. There is no stretch to
        // inspect in that case, so it is not reported as occupied.
        if (gap.Upper <= gap.Lower) return false;
        var owned = RepairIntegrity.Coverage(entries);
        for (var line = gap.Lower + 1; line < gap.Upper; line++)
            if (owned.Contains(line)) return true;
        return false;
    }

    /// <summary>Keeps a pasted paragraph from swallowing the detail line.</summary>
    private static string TruncateTitle(string text) => text.Length <= 30 ? text : text[..30] + "…";

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
                // A heading adopted from the body behaves exactly like a located one from here on: the
                // line becomes a chapter boundary and the rebuilt pass hands it the text below it. The
                // difference is only in how the line was chosen — the directory could not confirm it.
                case ReferenceActionKind.AdoptHeading when action.Line > 0 && allowed.Contains(action.Line):
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
            else if (action.Kind is ReferenceActionKind.AddChapter or ReferenceActionKind.AdoptHeading
                && action.Line > 0 && !result.Any(e => e.TitleLineNumber == action.Line))
            {
                // Both kinds insert a chapter at an existing source line. They differ only in where the
                // line may come from: an AddChapter line was located, so it can be a heading that lies
                // between two chapters and belongs to neither; an AdoptHeading line was guessed from
                // the body, so it must be cut out of the chapter that currently carries it.
                var owner = result.FindIndex(e => e.ContentRanges.Any(r => r.StartLine <= action.Line && r.EndLine >= action.Line));
                if (owner < 0 && action.Kind == ReferenceActionKind.AdoptHeading) continue;
                if (owner >= 0 && result[owner].RecognitionSource == "manual") continue;
                var insertAt = owner + 1;
                var level = 1;
                if (owner >= 0)
                {
                    var carrying = result[owner];
                    var body = RepairIntegrity.Coverage([carrying]);
                    if (carrying.TitleLineNumber is int ownTitle) body.Remove(ownTitle);
                    var tail = body.Where(line => line > action.Line).ToArray();
                    result[owner] = carrying with { ContentRanges = RepairIntegrity.Ranges(body.Where(line => line < action.Line)) };
                    level = carrying.IsFrontMatter ? 1 : carrying.Level;
                    result.Insert(insertAt, new ChapterTreeEntry(Guid.NewGuid().ToString("N"),
                        useReferenceTitles ? action.Title : document.SourceLine(action.Line)!.Text.Trim(),
                        level, true, action.Line, RepairIntegrity.Ranges(tail)) { RecognitionSource = "reference" });
                }
                else
                {
                    // The located heading sits in no chapter's range, so it becomes one on its own; the
                    // rebuilt pass hands it the text below it.
                    result.Insert(Math.Min(insertAt, result.Count), new ChapterTreeEntry(Guid.NewGuid().ToString("N"),
                        useReferenceTitles ? action.Title : document.SourceLine(action.Line)!.Text.Trim(),
                        level, true, action.Line, []) { RecognitionSource = "reference" });
                }
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
