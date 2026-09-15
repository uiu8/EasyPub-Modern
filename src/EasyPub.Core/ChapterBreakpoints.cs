namespace EasyPub.Core;

/// <summary>Why two neighbouring chapters do not join up.</summary>
public enum ChapterBreakpointKind
{
    /// <summary>The chapter numbers themselves skip, so the source text is missing chapters.</summary>
    NumberGap,
    /// <summary>The reference directory lists chapters this tree does not contain.</summary>
    ReferenceMissing,
    /// <summary>A nearby heading reads as a chapter number it cannot actually be.</summary>
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
    public static IReadOnlyList<ChapterBreakpoint> Between(
        ChapterTreeDocument document,
        IReadOnlyList<ChapterTreeEntry>? entries = null,
        ReferenceCatalog? reference = null,
        CancellationToken cancellationToken = default)
    {
        entries ??= document.Entries;
        var corrections = HeadingTypoRules.Parse(document.RecognitionOptions.HeadingNumberCorrections);
        var ordered = new List<(ChapterTreeEntry Entry, ChapterNumber Reading, int? Value, bool Odd, string Scope)>();
        var parents = new List<ChapterTreeEntry>();
        var previousValue = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.IsFrontMatter) { parents.Clear(); previousValue = 0; continue; }
            while (parents.Count > 0 && parents[^1].Level >= entry.Level) parents.RemoveAt(parents.Count - 1);
            var scope = (parents.Count > 0 ? parents[^1].Id : "") + "|" + entry.Level + "|" + Unit(entry.Title);
            parents.Add(entry);
            var reading = Read(entry.Title, corrections);
            var value = reading.Number;
            // A heading whose digits are nowhere near the chapter before it ("第四百零十八章" reading 4080
            // where 418 belongs) is a miswritten number, not a chapter four thousand places away. The 零
            // reading is adopted only where it lands on the very next chapter, so an ordinary "第一千零八"
            // keeps its literal 1008 instead of becoming 1018.
            var odd = false;
            if (value.HasValue && Math.Abs((long)value.Value - (previousValue + 1)) > MisreadDistance)
            {
                var repaired = reading.Relaxed;
                if (repaired == previousValue + 1) value = repaired;
                else odd = true;
            }
            previousValue = value ?? previousValue;
            ordered.Add((entry, reading, value, odd, scope));
        }

        var result = new List<ChapterBreakpoint>();
        var used = new HashSet<int>();
        for (var index = 1; index < ordered.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = ordered[index - 1];
            var current = ordered[index];
            if (previous.Scope != current.Scope) { used.Clear(); if (previous.Value is int carried) used.Add(carried); continue; }
            if (previous.Value is not int before || current.Value is not int now) continue;
            // A number already used earlier in this scope means the tree is grappling with a repeated or
            // miswritten number ("第一五五章" where 第一五五五章 belongs repeats 155), not that chapters were
            // skipped. Reporting a fifteen-hundred-chapter gap there would bury the real ones.
            if (!used.Add(now)) continue;
            if (now <= before + 1) continue;

            // The number jumps somewhere it cannot plausibly belong. Saying so next to the row beats
            // reporting four thousand "missing" chapters in between.
            if (current.Odd)
            {
                result.Add(new(ChapterBreakpointKind.HeadingTypo, previous.Entry.Id, current.Entry.Id, before, [], [],
                    $"「{current.Entry.Title}」的章号与前一章（第 {before} 章）接不上，疑似编号写法有误；此处应紧接第 {before + 1} 章。"));
                continue;
            }

            // The numbers that should be here are the ones this tree failed to read. A heading the tree put
            // later but whose other reading is exactly one of them is a miswritten number, not an absent
            // chapter — reporting it as missing would send the user hunting for text in plain sight.
            var missing = new List<int>();
            var typos = new List<(int Expected, string Title)>();
            for (var expected = before + 1; expected < now; expected++)
            {
                var match = ordered.Skip(index).TakeWhile(item => item.Scope == current.Scope)
                    .FirstOrDefault(item => item.Reading.Relaxed == expected);
                if (match.Entry is null) { missing.Add(expected); continue; }
                typos.Add((expected, match.Entry.Title));
            }
            if (typos.Count > 0)
                result.Add(new(ChapterBreakpointKind.HeadingTypo, previous.Entry.Id, current.Entry.Id, before, [], [],
                    "此处标题的编号写法有误：" + string.Join("；", typos.Select(t => $"「{t.Title}」按前后章号应为第 {t.Expected} 章")) + "。"));
            if (missing.Count > 0)
                result.Add(new(ChapterBreakpointKind.NumberGap, previous.Entry.Id, current.Entry.Id, before, missing, [],
                    missing.Count == 1
                        ? $"第 {before} 章之后直接是第 {now} 章，源文件缺第 {missing[0]} 章。"
                        : $"第 {before} 章之后直接是第 {now} 章，源文件缺第 {missing[0]}–{missing[^1]} 章（共 {missing.Count} 章）。"));
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
                    ? $"参考目录在此处有「{titles[0]}」，源文件里找不到这一章。"
                    : $"参考目录在此处有 {titles.Count} 章在源文件里找不到：{string.Join("；", titles.Take(4))}{(titles.Count > 4 ? " …" : "")}。"));
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
    /// How far a heading number may sit from the chapter before it before the digits are treated as
    /// miswritten rather than as a chapter four thousand places away. Volumes restart numbering inside the
    /// same tree, but a restart changes scope, so it never reaches this comparison.
    /// </summary>
    private const int MisreadDistance = 50;

    /// <summary>
    /// A chapter number read from a heading, in two readings. <paramref name="Number"/> is the heading
    /// after the book's own 编号纠错表 ("第六百八十久章" is 689); <paramref name="Relaxed"/> additionally
    /// reads a 零 in front of a unit as the "one" it was meant to be ("第四百零十" is 4110 literally and
    /// 410 as meant). Neither reading rewrites the heading.
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
