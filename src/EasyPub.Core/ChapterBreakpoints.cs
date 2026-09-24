namespace EasyPub.Core;

/// <summary>Why two neighbouring chapters do not join up.</summary>
public enum ChapterBreakpointKind
{
    /// <summary>The chapter numbers themselves skip, so the source text is missing chapters.</summary>
    NumberGap,
    /// <summary>The reference directory lists these chapters and the tree does not contain them.</summary>
    ReferenceMissing,
    /// <summary>A stretch of numbers is absent, but a heading already in the tree reads as one of them.</summary>
    HeadingTypo,
}

/// <summary>
/// One break between two chapters, anchored to the chapter it follows. The anchor is an entry id
/// rather than a line number because merging, splitting and reordering move lines but keep identity.
/// </summary>
public sealed record ChapterBreakpoint(
    ChapterBreakpointKind Kind,
    string? AnchorId,
    string? NextId,
    int PreviousNumber,
    IReadOnlyList<int> MissingNumbers,
    IReadOnlyList<string> MissingTitles,
    string Detail);

/// <summary>
/// Read-only detection of the places where a chapter tree visibly fails to join up, so the workbench
/// can point at a specific row instead of printing one vague sentence per issue.
///
/// Two independent signals feed it. Neighbouring chapter numbers are compared in the same scope
/// <see cref="ChapterDiagnostics"/> uses (one parent, one level, one marker), and — when a reference
/// directory is available — the tree is aligned against it so a chapter the directory lists but the
/// text lacks is attached to the row it should have followed. Neither signal edits anything.
/// </summary>
public static class ChapterBreakpoints
{
    private const string ChineseNumberChars = "零〇一二两兩三四五六七八九十百千万萬壹贰貳叁參肆伍陆陸柒捌玖拾佰仟";

    /// <summary>
    /// The most consecutive chapter numbers that can be read as "this file lost these chapters". A real
    /// release sometimes drops a handful; a run of hundreds means the numbering itself is faulty (repeated
    /// or miswritten numbers), and claiming those chapters are missing would be a false alarm.
    /// </summary>
    private const int LargestPlausibleGap = 30;

    public static IReadOnlyList<ChapterBreakpoint> Between(
        ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry>? entries = null,
        ReferenceCatalog? reference = null,
        CancellationToken cancellationToken = default)
    {
        entries ??= document.Entries;
        var corrections = HeadingTypoRules.Parse(document.RecognitionOptions.HeadingNumberCorrections);
        // Only what the tree itself shows is reported: chapter numbers that skip, and chapters the reference
        // directory lists that the tree lacks. Nothing here guesses that a heading is *written* wrong — a
        // release legitimately prints "第335章" after "第1章" across volumes and restarts, and calling that a
        // miswritten number would bury the real gaps. Recognising headings is ChapterDiagnostics' job.
        var ordered = new List<(ChapterTreeEntry Entry, ChapterNumber Reading, int? Value, string Scope, string BaseScope)>();
        var parents = new List<ChapterTreeEntry>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsFrontMatter) { parents.Clear(); continue; }
            while (parents.Count > 0 && parents[^1].Level >= entry.Level) parents.RemoveAt(parents.Count - 1);
            // A book may switch from Chinese numerals to Arabic digits (or back) without starting a new
            // volume. Treat each written numbering family as its own local sequence. Comparing across the
            // switch turns a deliberate format change such as 第十七章 -> 第335章 into hundreds of fake gaps.
            var reading = Read(entry.Title, corrections);
            var baseScope = (parents.Count > 0 ? parents[^1].Id : "") + "|" + entry.Level + "|" + Unit(entry.Title);
            var scope = baseScope + "|" + NumberingFamily(entry.Title, corrections);
            parents.Add(entry);
            ordered.Add((entry, reading, reading.Number, scope, baseScope));
        }

        var result = new List<ChapterBreakpoint>();
        // The numbers this scope shows, and the span they cover. A gap is only believable when the missing
        // numbers sit inside that span: 择天记 prints 中文 chapters and 阿拉伯 chapters side by side
        // ("第十七章" then "第335章"), and comparing across two numbering systems invents hundreds of
        // "missing" chapters that belong to the other system.
        var span = new Dictionary<string, (int Low, int High)>();
        var scopes = ordered.GroupBy(item => item.BaseScope).ToDictionary(
            group => group.Key,
            group => group.Select(item => item.Value).OfType<int>().ToArray());
        var present = scopes.ToDictionary(pair => pair.Key, pair => pair.Value.ToHashSet());
        // Relaxed readings are indexed once. The old inner FirstOrDefault scanned the complete tree for
        // every candidate number, which made a long run of malformed headings unnecessarily quadratic.
        var relaxed = ordered.GroupBy(item => item.BaseScope).ToDictionary(
            group => group.Key,
            group => group.Where(item => item.Reading.Relaxed is int)
                .GroupBy(item => item.Reading.Relaxed!.Value)
                .ToDictionary(group => group.Key, group => group.First().Entry));
        foreach (var pair in scopes.Where(pair => pair.Value.Length > 0))
            span[pair.Key] = (pair.Value.Min(), pair.Value.Max());
        // The number the previous row showed, inside the scope being walked. A row that repeats a number is
        // a duplicate-number problem; it must be skipped, not used as the left end of a comparison.
        var seen = new Dictionary<string, HashSet<int>>();
        for (var index = 1; index < ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = ordered[index - 1];
            var current = ordered[index];
            if (previous.Value is int previousNumber)
            {
                if (!seen.TryGetValue(previous.Scope, out var previousSeen))
                    seen[previous.Scope] = previousSeen = [];
                previousSeen.Add(previousNumber);
            }
            if (previous.Scope != current.Scope) continue;
            if (previous.Value is not int before || current.Value is not int now) continue;
            if (!seen.TryGetValue(current.Scope, out var scopeSeen))
                seen[current.Scope] = scopeSeen = [];
            if (!scopeSeen.Add(now)) continue;
            if (now <= before + 1) continue;
            if ((long)now - before - 1 > LargestPlausibleGap)
            {
                result.Add(new(ChapterBreakpointKind.NumberGap, previous.Entry.Id, current.Entry.Id, before, [], [],
                    $"第 {before} 章之后是第 {now} 章；编号跨度较大，可能存在编号体系切换或版本差异，不能据此认定缺章。"));
                continue;
            }

            // The numbers that should be here are the ones this tree failed to read. A heading the tree put
            // later but whose other reading is exactly one of them is a miswritten number, not an absent
            // chapter — reporting it as missing would send the user hunting for text in plain sight.
            var missing = new List<int>();
            var typos = new List<(int Expected, string Title)>();
            var beyondScope = 0;
            var (low, high) = span.TryGetValue(current.BaseScope, out var range) ? range : (int.MinValue, int.MaxValue);
            for (var expected = before + 1; expected < now; expected++)
            {
                // Presence is intentionally broader than the comparison scope. A heading written as
                // 第257章 still proves that the source contains chapter 257 when its neighbours use
                // Chinese numerals; only the adjacent-number comparison needs the notation boundary.
                if (present[current.BaseScope].Contains(expected)) continue;
                if (expected < low || expected > high) { beyondScope++; continue; }
                if (!relaxed.TryGetValue(current.BaseScope, out var byNumber)
                    || !byNumber.TryGetValue(expected, out var match)) { missing.Add(expected); continue; }
                typos.Add((expected, match.Title));
            }
            if (missing.Count == 0 && typos.Count == 0 && beyondScope == 0) continue;
            if (typos.Count > 0)
                result.Add(new(ChapterBreakpointKind.HeadingTypo, previous.Entry.Id, current.Entry.Id, before, [], [],
                    "此处标题的编号写法有误：" + string.Join("；", typos.Select(t => $"「{t.Title}」按前后章号应为第 {t.Expected} 章")) + "。"));
            // Naming chapters is only honest while the numbers line up. When the range holds numbering from
            // another system (择天记 prints 中文 and 阿拉伯 chapters side by side) or covers hundreds of
            // numbers, say that the numbering breaks and let the user look at the row.
            if (missing.Count == 0 && beyondScope == 0) continue;
            var names = missing.Count is > 0 and <= LargestPlausibleGap;
            result.Add(new(ChapterBreakpointKind.NumberGap, previous.Entry.Id, current.Entry.Id, before,
                names ? missing : [], [],
                names
                    ? missing.Count == 1
                        ? $"第 {before} 章之后直接是第 {now} 章，当前目录未出现第 {missing[0]} 章，请对照原文核实。"
                        : $"第 {before} 章之后直接是第 {now} 章，当前目录未出现第 {missing[0]}–{missing[^1]} 章（共 {missing.Count} 章），请对照原文核实。"
                    : $"第 {before} 章与第 {now} 章之间的编号对不上（{Math.Max(missing.Count, beyondScope)} 个号码既不在本节范围内、也未出现），源文件这一段可能混用了另一套编号或存在重号，请核对。"));
        }
        if (reference is not null) AddReferenceMissing(entries, reference, corrections, result, cancellationToken);
        return result.OrderBy(item => item.PreviousNumber).ToArray();
    }

    /// <summary>
    /// Attaches every chapter the reference lists but the tree lacks to the row it should have
    /// followed, so "这里本来该有第四百一十章" can be shown between two real rows.
    /// </summary>
    private static void AddReferenceMissing(IReadOnlyList<ChapterTreeEntry> entries, ReferenceCatalog reference,
        IReadOnlyDictionary<char, char> corrections, List<ChapterBreakpoint> result, CancellationToken cancellationToken)
    {
        var local = entries.Where(entry => !entry.IsFrontMatter).ToArray();
        if (local.Length == 0) return;
        var alignment = ReferenceOutline.Align(entries, reference);
        var steps = alignment.Steps;
        for (var index = 0; index < steps.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (steps[index].Kind != AlignmentKind.ReferenceOnly) continue;
            var titles = new List<string>();
            var end = index;
            while (end < steps.Count && steps[end].Kind == AlignmentKind.ReferenceOnly)
            {
                var title = alignment.Reference[steps[end].ReferenceIndex].Title;
                // A chapter present under a slightly different title is a retitle, not a missing chapter.
                if (!Contains(local, title)) titles.Add(title);
                end++;
            }
            index = end - 1;
            if (titles.Count == 0) continue;
            var anchor = PreviousMatched(alignment, index);
            var next = NextMatched(alignment, index + 1);
            var before = anchor is null ? 0 : Read(anchor.Title, corrections).Number ?? 0;
            result.Add(new(ChapterBreakpointKind.ReferenceMissing, anchor?.Id, next?.Id, before, [], titles,
                titles.Count == 1
                    ? $"参考目录在此处有「{titles[0]}」，当前章节树未匹配，需对照原文核实。"
                    : $"参考目录在此处有 {titles.Count} 章未与当前章节树匹配：{string.Join("；", titles)}。需对照原文核实。"));
        }
    }

    private static ChapterTreeEntry? PreviousMatched(ReferenceAlignment alignment, int before)
    {
        for (var index = before; index >= 0; index--)
            if (alignment.Steps[index].Kind == AlignmentKind.Matched) return alignment.Entries[alignment.Steps[index].LocalIndex];
        return null;
    }

    private static ChapterTreeEntry? NextMatched(ReferenceAlignment alignment, int from)
    {
        for (var index = from; index < alignment.Steps.Count; index++)
            if (alignment.Steps[index].Kind == AlignmentKind.Matched) return alignment.Entries[alignment.Steps[index].LocalIndex];
        return null;
    }

    private static bool Contains(IReadOnlyList<ChapterTreeEntry> entries, string title)
    {
        var wanted = ReferenceOutline.ParseKey(title).Canonical;
        return entries.Any(entry => ReferenceOutline.ParseKey(entry.Title).Canonical == wanted);
    }

    private static string Unit(string title)
    {
        var match = System.Text.RegularExpressions.Regex.Match(title.Trim(), "[章回]",
            System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(200));
        return match.Success ? match.Value : "";
    }

    /// <summary>
    /// Identifies the notation used by the number before a chapter marker. This is deliberately a
    /// presentation-level boundary: it does not make an unrecognised heading valid and it never changes
    /// the number returned by <see cref="Read"/>. Its only purpose is to avoid comparing unrelated local
    /// sequences when a source changes from Chinese numerals to Arabic digits.
    /// </summary>
    private static string NumberingFamily(string title, IReadOnlyDictionary<char, char> corrections)
    {
        var digits = Extract(title, corrections);
        if (digits.Length == 0) return "unknown";
        var hasArabic = digits.Any(ch => (ch >= '0' && ch <= '9') || (ch >= '０' && ch <= '９'));
        var hasChinese = digits.Any(ch => ChineseNumberChars.Contains(ch)
            || corrections.TryGetValue(ch, out var corrected) && ChineseNumberChars.Contains(corrected));
        return (hasArabic, hasChinese) switch
        {
            (true, false) => "arabic",
            (false, true) => "chinese",
            (true, true) => "mixed",
            _ => "unknown",
        };
    }

    /// <summary>
    /// A chapter number read from a heading, in two readings. <paramref name="Number"/> is the heading
    /// after the book's own 编号纠错表 ("第六百八十久章" is 689); <paramref name="Relaxed"/> additionally
    /// reads a 零 in front of a unit as the "one" it was meant to be ("第四百零十" is 4110 literally and
    /// 410 as meant). Neither reading rewrites the heading; the second one exists only so that a heading
    /// which *is* present under a miswritten number is not counted among the missing.
    /// </summary>
    public readonly record struct ChapterNumber(int? Number, int? Relaxed, bool Readable);

    // A 零 immediately before a unit is the release's way of writing "one"; a 零 before a digit is the
    // ordinary placeholder of "一千零八". Only the first kind changes the number when it is fixed.
    private static readonly System.Text.RegularExpressions.Regex ZeroBeforeUnit = new("零+(?=[十百千万])",
        System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static ChapterNumber Read(string title, IReadOnlyDictionary<char, char> corrections)
    {
        var digits = Extract(title, corrections);
        if (digits.Length == 0) return new(null, null, false);
        var corrected = new string(digits.Select(ch => corrections.TryGetValue(ch, out var fix) ? fix : ch).ToArray());
        var literal = HeadingSyntax.ParseNumber(corrected);
        var unitFixed = ZeroBeforeUnit.IsMatch(corrected) ? ZeroBeforeUnit.Replace(corrected, "一") : null;
        var relaxed = unitFixed is null ? null : HeadingSyntax.ParseNumber(unitFixed);
        // "一千零八" keeps 1008 either way, so dropping a 零 proves nothing there. Only a reading that
        // actually changes the value exposes a 零 used as a digit.
        var suspicious = literal is not null && relaxed is not null && relaxed != literal;
        return new(literal, suspicious ? relaxed : null, literal is not null);
    }

    /// <summary>
    /// The chapter number written in a heading: the marker is located first, then the number is read
    /// back from it. That keeps a title ("…章 各怀鬼胎（二更）") from contributing digits while still
    /// allowing the characters the 编号纠错表 exists for — "第六百八十久章" is 689, not 680.
    /// </summary>
    private static string Extract(string title, IReadOnlyDictionary<char, char> corrections)
    {
        var text = title.Normalize(System.Text.NormalizationForm.FormKC);
        var marker = -1;
        for (var index = 0; index < text.Length; index++)
            if (text[index] is '章' or '回') { marker = index; break; }
        if (marker < 0) return "";
        var start = marker - 1;
        while (start >= 0 && IsNumberPart(text[start], corrections)) start--;
        // ParseNumber reads a bare number: the 第 is a marker, not a digit, and leaving it in makes the
        // whole reading null.
        return text[(start + 1)..marker].Trim().TrimStart('第').Trim();
    }

    private static bool IsNumberPart(char ch, IReadOnlyDictionary<char, char> corrections) =>
        HeadingSyntax.IsNumeral(ch) || ch is '第' or '十' or '百' or '千' or '万' || corrections.ContainsKey(ch);}
