namespace EasyPub.Core;

/// <summary>
/// Where one reference chapter was found in the local text.
/// <paramref name="Line"/> is the position chosen by reference order (null when not found);
/// <paramref name="Lines"/> lists every equally-good position, which is how duplicates surface.
/// </summary>
public sealed record LocatedChapter(ReferenceNode Reference, string? Volume, int? Line, string LocalTitle, IReadOnlyList<int> Lines)
{
    public int Occurrences => Lines.Count;
    public bool IsDuplicate => Lines.Count > 1;
}

/// <summary>
/// Reference-driven location of every chapter in the local text, independent of the normal
/// recognition pipeline. This matters because the recogniser requires a line to start with
/// "第X章", so lines like "第六篇 第十章" (volume prefix, no title words) are invisible to it.
/// The reference directory already knows those chapters exist, so it can be used to find them.
/// </summary>
public sealed record ReferenceLocation(IReadOnlyList<LocatedChapter> Chapters)
{
    public int Found => Chapters.Count(c => c.Line is not null);
    public int Missing => Chapters.Count(c => c.Line is null);
    public IEnumerable<LocatedChapter> Ambiguous => Chapters.Where(c => c.IsDuplicate && c.Line is not null);
    public double Coverage => Chapters.Count == 0 ? 0 : (double)Found / Chapters.Count;

    public IReadOnlyList<LocatedChapter> InVolume(string volume) =>
        Chapters.Where(c => c.Volume == volume).ToArray();

    /// <summary>Consecutive runs of chapters that each appear more than once.</summary>
    public IEnumerable<IReadOnlyList<LocatedChapter>> DuplicateRuns()
    {
        var index = 0;
        while (index < Chapters.Count)
        {
            if (!Chapters[index].IsDuplicate) { index++; continue; }
            var start = index;
            while (index < Chapters.Count && Chapters[index].IsDuplicate) index++;
            yield return Chapters.Skip(start).Take(index - start).ToArray();
        }
    }
}

public static class ReferenceLocator
{
    private const int MaximumTitleLength = 60;

    /// <summary>
    /// How alike two titles must be before the last-resort pass accepts one for a missing chapter.
    /// Two-character titles are excluded from that pass entirely: "赌徒" and "赌鬼" score 0.5 against
    /// each other, which is far too easy to match by accident.
    /// </summary>
    private const double FuzzyThreshold = 0.67;

    /// <summary>Lines longer than this are prose, never a heading, so their key is never computed.</summary>
    private const int MaximumScannedLineLength = 100;
    private static readonly System.Text.RegularExpressions.Regex NumericStart = new(
        @"^\s*[0-9]{3,6}\s*[:：.、]", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    private static bool HeadingLike(string text, TitleKey key) => !HeadingSyntax.IsProsePrefix(text)
        && (HeadingSyntax.IsHeading(text) || NumericStart.IsMatch(text)
            || (key.Number.Length == 0 && text.IndexOfAny(['。', '；', '“', '”', '：']) < 0));

    /// <summary>
    /// Scans every line of the local text for reference chapter titles. A line is a candidate heading
    /// when it is short, carries a title signal, and is isolated by at least one blank neighbour.
    /// Candidates are consumed in reference order, preferring the nearest line after the previous match
    /// so that repeated titles stay anchored to their true position.
    /// </summary>
    public static ReferenceLocation Locate(IReadOnlyList<string> lines, ReferenceCatalog catalog, IReadOnlyDictionary<int, string>? knownHeadings = null, CancellationToken cancellationToken = default)
    {
        var volumePrefixes = catalog.VolumeTitles
            .Select(title => ReferenceOutline.ParseKey(title).Words)
            .Where(words => words.Length >= 2)
            .Distinct()
            .OrderByDescending(words => words.Length)
            .ToArray();
        var candidates = new List<Candidate>();
        for (var index = 0; index < lines.Count; index++)
        {
            if (index % 1024 == 0) cancellationToken.ThrowIfCancellationRequested();
            var known = knownHeadings is not null && knownHeadings.ContainsKey(index + 1);
            var text = known ? knownHeadings![index + 1] : lines[index].Trim();
            if (text.Length is 0 or > MaximumTitleLength) continue;
            var isolated = index == 0 || string.IsNullOrWhiteSpace(lines[index - 1])
                || index == lines.Count - 1 || string.IsNullOrWhiteSpace(lines[index + 1]);
            if (!isolated && !known) continue;
            var key = ReferenceOutline.ParseKey(text, volumePrefixes);
            if (key.IsEmpty) continue;
            if (!known && !HeadingLike(text, key)) continue;
            candidates.Add(new(index + 1, key, text));
        }
        var located = new List<LocatedChapter>();
        var used = new HashSet<int>();
        var cursor = 0;
        var pending = new List<int>();
        var rankedByChapter = new List<Candidate[]>();
        foreach (var node in catalog.Nodes.Where(n => n.Kind == ReferenceNodeKind.Chapter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ReferenceOutline.ParseKey(node.Title, volumePrefixes);
            var scored = candidates
                .Select(candidate => (Candidate: candidate, Score: ReferenceOutline.MatchScore(key, candidate.Key)))
                .Where(pair => pair.Score > 0)
                .ToArray();
            var matches = scored.Where(pair => pair.Candidate.Key.Canonical == key.Canonical)
                .Select(pair => pair.Candidate.Line).OrderBy(line => line).ToArray();
            // One source line per reference chapter, best still-unused candidate first. Top-scoring
            // only would leave the last chapters of a book with no line at all.
            // Similar titles are common across releases, so a resemblance match is still accepted; it
            // is graded below exact matches but must not be dropped, or genuine chapters go missing.
            var ranked = scored.Where(pair => !used.Contains(pair.Candidate.Line))
                .OrderByDescending(pair => NumberAgrees(key, pair.Candidate.Key))
                .ThenByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Candidate.Line)
                .Select(pair => pair.Candidate)
                .ToArray();
            rankedByChapter.Add(ranked);
            // Strictly after the previous chapter when possible. This book contains repeated blocks,
            // so a chapter's true line can sit before the cursor; falling back to the best remaining
            // candidate keeps every reference chapter in the tree.
            var agreeing = ranked.Where(candidate => NumberAgrees(key,candidate.Key)).ToArray();
            var exact = ranked.Where(candidate => candidate.Key.Canonical == key.Canonical).ToArray();
            var preferred = exact.Length > 0 ? exact : agreeing.Length > 0 ? agreeing : ranked;
            var chosen = preferred.FirstOrDefault(candidate => candidate.Line > cursor) ?? preferred.FirstOrDefault();
            if (chosen is null)
            {
                pending.Add(located.Count);
                located.Add(new(node, node.VolumeTitle, null, "", matches));
                continue;
            }
            used.Add(chosen.Line);
            cursor = chosen.Line;
            located.Add(new(node, node.VolumeTitle, chosen.Line, chosen.Text,
                chosen.Key.Canonical == key.Canonical ? matches : []));
        }
        // Second pass: a chapter that found no line after the cursor is placed between its located
        // neighbours, so the rebuilt tree stays monotonic instead of appending it at the end.
        foreach (var index in pending)
        {
            var previous = 0;
            for (var probe = index - 1; probe >= 0; probe--)
                if (located[probe].Line is { } before) { previous = before; break; }
            var next = int.MaxValue;
            for (var probe = index + 1; probe < located.Count; probe++)
                if (located[probe].Line is { } after) { next = after; break; }
            var available = rankedByChapter[index].Where(item => !used.Contains(item.Line)).ToArray();
            var candidate = available.FirstOrDefault(item => item.Line > previous && item.Line < next)
                ?? available.FirstOrDefault(item => item.Line > previous)
                ?? available.OrderBy(item => Math.Abs(item.Line - previous)).FirstOrDefault();
            if (candidate is null) continue;
            used.Add(candidate.Line);
            located[index] = located[index] with { Line = candidate.Line, LocalTitle = candidate.Text };
        }
        // Third pass: a reference chapter still unplaced gets a search over every remaining line.
        // Sites pad titles with marketing suffixes ("（六千大章补更）"), which pushes the resemblance
        // score below every threshold while the title itself is intact; and releases also carry
        // mis-typed headings, which only a character-level comparison can find.
        TitleKey[]? lineKeys = null;
        for (var index = 0; index < located.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (located[index].Line is not null) continue;
            var key = ReferenceOutline.ParseKey(located[index].Reference.Title, volumePrefixes);
            var words = key.Words;
            if (words.Length < 2) continue;
            // Two-character titles ("赌徒", "神陨") are common and used to be skipped outright, which
            // is why chapters whose heading lost its blank neighbours were reported missing even
            // though the text was right there. Such a word is far too generic for a bare substring
            // hit, so for these an agreeing chapter number is required before the line is accepted.
            var shortTitle = words.Length < 3;
            // Every line's key, parsed once and reused for every still-unplaced chapter. Parsing
            // inside this loop would re-run the number regex once per chapter per line.
            lineKeys ??= ParseLineKeys(lines, volumePrefixes);
            // Search every line, not just the candidate set: a heading buried inside a long chapter
            // may have no blank neighbour at all, and that is exactly how "skipped chapters" appear.
            // Three kinds of evidence, strongest first: an agreeing chapter number (the heading is
            // definitely this chapter), an exact substring of the title words (the heading is intact
            // but its number is wrong), then character similarity (the heading itself has a wrong or
            // missing character, which no substring test can ever match).
            var found = -1;
            var fallback = -1;
            var fuzzy = -1;
            var fuzzyScore = 0.0;
            for (var line = 1; line <= lines.Count; line++)
            {
                if (used.Contains(line)) continue;
                var text = lines[line - 1].Trim();
                if (text.Length is 0 or > MaximumScannedLineLength) continue;
                var candidate = lineKeys[line];
                if (candidate.Words.Length == 0) continue;
                if (NumberAgrees(key, candidate)) { found = line; break; }
                if (shortTitle) continue;
                if (text.Contains(words, StringComparison.Ordinal))
                {
                    if (fallback < 0) fallback = line;
                    continue;
                }
                // A title that merely grew or lost one character is still the same title. Anything
                // further apart is a different chapter and must stay missing rather than be guessed.
                if (Math.Abs(candidate.Words.Length - words.Length) > 1) continue;
                var score = ReferenceOutline.Similarity(words, candidate.Words);
                if (score >= FuzzyThreshold && score > fuzzyScore) { fuzzyScore = score; fuzzy = line; }
            }
            found = found >= 0 ? found : fallback >= 0 ? fallback : fuzzy;
            if (found < 0) continue;
            used.Add(found);
            located[index] = located[index] with { Line = found, LocalTitle = lines[found - 1].Trim() };
        }
        return new(located);
    }

    private sealed record Candidate(int Line, TitleKey Key, string Text);

    /// <summary>
    /// Parses the title key of every line once. The last-resort pass compares each still-unplaced
    /// chapter against all of them, so re-parsing inside that loop would run the number regex once
    /// per chapter per line — quadratic on a 60k-line book.
    /// </summary>
    private static TitleKey[] ParseLineKeys(IReadOnlyList<string> lines, IReadOnlyList<string> volumePrefixes)
    {
        var keys = new TitleKey[lines.Count + 1];
        for (var line = 1; line <= lines.Count; line++)
        {
            var text = lines[line - 1].Trim();
            var key = text.Length is 0 or > MaximumScannedLineLength
                ? new TitleKey("", "")
                : ReferenceOutline.ParseKey(text, volumePrefixes);
            keys[line] = HeadingLike(text, key) ? key : new TitleKey("", "");
        }
        return keys;
    }

    /// <summary>
    /// True when both keys carry a chapter number and they agree. An agreeing number is stronger
    /// evidence than agreeing title words, and it has to outrank them: with "第十六章 预谋" in the
    /// reference, a line reading "第二百五十七章 预谋" scores higher on words alone and drags the
    /// chapter thousands of lines away, which is what makes a rebuilt tree look scrambled.
    /// </summary>
    private static bool NumberAgrees(TitleKey left, TitleKey right) =>
        left.Number.Length > 0 && left.Number == right.Number;
}
