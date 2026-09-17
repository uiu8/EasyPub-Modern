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

/// <summary>Which test accepted a line during the last-resort pass, strongest evidence first.</summary>
public enum LocateEvidence
{
    /// <summary>A chapter number that agrees with the reference title — the line is definitely this chapter.</summary>
    NumberAgrees,
    /// <summary>The title words appear intact but the chapter number is wrong.</summary>
    TitleWords,
    /// <summary>Character similarity: the heading itself is mis-typed or missing a character.</summary>
    Similarity
}

/// <summary>
/// What the last-resort pass actually did. It exists so the cost of that pass can be measured instead
/// of assumed: it is the only quadratic part of location, and when a directory does not match the text
/// at all it is the one place where the work is certain to be wasted.
/// <paramref name="ScannedLines"/> counts line visits (chapters considered × lines examined), and
/// <paramref name="SecondPassLocated"/> is how many chapters the cheap passes had already placed
/// before that search began.
/// </summary>
public sealed record ReferenceLocationStats(
    int CatalogChapters,
    int CandidateLines,
    int SecondPassLocated,
    int SecondPassMissing,
    int ScannedChapters,
    long ScannedLines,
    int NumberHits,
    int WordsHits,
    int SimilarityHits,
    int Unrescuable = 0,
    int OutOfWindow = 0)
{
    public int ThirdHits => NumberHits + WordsHits + SimilarityHits;
    public override string ToString() =>
        $"目录 {CatalogChapters} 章，标题候选行 {CandidateLines}；前两遍定位 {SecondPassLocated} 章、未定位 {SecondPassMissing} 章；" +
        $"兜底扫描 {ScannedChapters} 章 / {ScannedLines} 行，命中 {ThirdHits} 章（章号 {NumberHits} / 标题词 {WordsHits} / 相似度 {SimilarityHits}）" +
        (Unrescuable > 0 ? $"；另 {Unrescuable} 章标题无词可搜，未扫描" : "") +
        (OutOfWindow > 0 ? $"；另 {OutOfWindow} 章在前后邻章之间没有可搜索区间，原文中确无此章" : "");
}

/// <summary>Per-stage cost of reference location, split so a slow book can be attributed to one stage.</summary>
public record ReferenceLocateTiming(long CandidatesMs = 0, long ForwardMs = 0, long ThirdPassMs = 0, long TotalMs = 0)
{
    /// <summary>Sub-millisecond stages still matter here: the cheap passes are expected to be the fast ones.</summary>
    public override string ToString() =>
        $"候选行 {CandidatesMs} ms / 前两遍 {ForwardMs} ms / 兜底 {ThirdPassMs} ms / 合计 {TotalMs} ms";
}

/// <summary>
/// Reference-driven location of every chapter in the local text, independent of the normal
/// recognition pipeline. This matters because the recogniser requires a line to start with
/// "第X章", so lines like "第六篇 第十章" (volume prefix, no title words) are invisible to it.
/// The reference directory already knows those chapters exist, so it can be used to find them.
/// </summary>
public sealed record ReferenceLocation(IReadOnlyList<LocatedChapter> Chapters, ReferenceLocationStats? Stats = null)
{
    /// <summary>Chapters the last-resort pass never even searched, because their title carries no words.</summary>
    public int Unrescuable { get; init; }

    /// <summary>Per-stage cost of this location, when it was measured.</summary>
    public ReferenceLocateTiming? Timing { get; init; }

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
        // Three stages, timed separately. Which one dominates decides what to fix, and guessing it
        // from the source alone is how the wrong thing gets optimised.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
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
        var candidatesDone = System.Diagnostics.Stopwatch.GetTimestamp();
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
            // Strictly after the previous chapter when possible. A repeated block means a chapter's true
            // line can sit before the cursor — the copy is what `Lines` needs in order to be reported as
            // a duplicate — so the fallback stays. What it may no longer do is reach past the chapter
            // that follows: the gap bound in the third pass is where that is enforced.
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
        // Second pass: a chapter the cursor walked past is placed inside the gap its *placed*
        // neighbours bracket, which is the only place it can be. Before this it also accepted "the
        // nearest remaining candidate on either side", and that is how a chapter whose own text is
        // missing ended up sharing a line with a different volume's chapter of the same number.
        foreach (var index in pending)
        {
            var (previous, next) = NeighbourGap(located, index, lines.Count);
            var candidate = rankedByChapter[index]
                .Where(item => !used.Contains(item.Line) && item.Line > previous && item.Line < next)
                .OrderBy(item => item.Line)
                .FirstOrDefault();
            if (candidate is null) continue;
            used.Add(candidate.Line);
            located[index] = located[index] with { Line = candidate.Line, LocalTitle = candidate.Text };
        }
        // Third pass: a reference chapter still unplaced gets a search over the lines between the
        // chapters that *were* placed. Sites pad titles with marketing suffixes ("（六千大章补更）"),
        // which pushes the resemblance score below every threshold while the title itself is intact;
        // and releases also carry mis-typed headings, which only a character-level comparison can find.
        //
        // The neighbours are what makes the answer trustworthy. Searching the whole file let an
        // unplaced chapter match the first line whose number agreed — in a book that restarts its
        // numbering per volume that is a chapter from another volume, so it reported as located, the
        // volume it belonged to reported as aligned, and every following chapter came out reordered.
        // Missing is the honest answer when nothing between the neighbours matches.
        var forwardDone = System.Diagnostics.Stopwatch.GetTimestamp();
        var alreadyLocated = located.Count(chapter => chapter.Line is not null);
        TitleKey[]? lineKeys = null;
        var scanned = 0;
        var scannedLines = 0L;
        var byNumber = 0;
        var byWords = 0;
        var bySimilarity = 0;
        var unrescuable = 0;
        var outOfWindow = 0;
        for (var index = 0; index < located.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (located[index].Line is not null) continue;
            var key = ReferenceOutline.ParseKey(located[index].Reference.Title, volumePrefixes);
            var words = key.Words;
            // A title with fewer than two words carries no evidence this pass can use, so there is
            // nothing to search for. Counted rather than silently skipped, because "no title words at
            // all" and "searched and genuinely absent" are different answers for the user.
            if (words.Length < 2) { unrescuable++; continue; }
            // Two-character titles ("赌徒", "神陨") are common and used to be skipped outright, which
            // is why chapters whose heading lost its blank neighbours were reported missing even
            // though the text was right there. Such a word is far too generic for a bare substring
            // hit, so for these an agreeing chapter number is required before the line is accepted.
            var shortTitle = words.Length < 3;
            var (lower, upper) = NeighbourGap(located, index, lines.Count);
            // An empty gap means the text really does not contain this chapter — the answer the report
            // needs, rather than a line borrowed from elsewhere in the book.
            if (lower + 1 >= upper) { outOfWindow++; continue; }
            // Every line's key, parsed once and reused for every still-unplaced chapter. Parsing
            // inside this loop would re-run the number regex once per chapter per line.
            lineKeys ??= ParseLineKeys(lines, volumePrefixes);
            scanned++;
            // Search every line in the gap, not just the candidate set: a heading buried inside a long
            // chapter may have no blank neighbour at all, and that is exactly how "skipped chapters"
            // appear.
            var (evidence, found) = SearchEveryLine(lines, lineKeys, used, key, words, shortTitle, lower + 1, upper);
            scannedLines += upper - lower - 1;
            if (found < 0) continue;
            switch (evidence)
            {
                case LocateEvidence.NumberAgrees: byNumber++; break;
                case LocateEvidence.TitleWords: byWords++; break;
                default: bySimilarity++; break;
            }
            used.Add(found);
            located[index] = located[index] with { Line = found, LocalTitle = lines[found - 1].Trim() };
        }
        var thirdDone = System.Diagnostics.Stopwatch.GetTimestamp();
        var stats = new ReferenceLocationStats(
            catalog.Titles.Count,
            candidates.Count,
            alreadyLocated,
            located.Count(chapter => chapter.Line is null),
            scanned,
            scannedLines,
            byNumber,
            byWords,
            bySimilarity)
        { Unrescuable = unrescuable, OutOfWindow = outOfWindow };
        return new(located, stats)
        {
            Timing = new(
                Ms(started, candidatesDone),
                Ms(candidatesDone, forwardDone),
                Ms(forwardDone, thirdDone),
                Ms(started, thirdDone))
        };
    }

    /// <summary>Elapsed milliseconds between two <see cref="System.Diagnostics.Stopwatch"/> timestamps.</summary>
    private static long Ms(long from, long to) =>
        (long)System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    /// <summary>
    /// Per-chapter candidate lines and their grades, in the order the first pass considers them.
    /// Exists so a mislocation can be explained from evidence instead of reconstructed by reading the
    /// ranking rules and hoping the reading is right.
    /// </summary>
    public static IReadOnlyList<(string Title, string[] Candidates)> RankCandidates(
        IReadOnlyList<string> lines, ReferenceCatalog catalog)
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
            var text = lines[index].Trim();
            if (text.Length is 0 or > MaximumTitleLength) continue;
            var isolated = index == 0 || string.IsNullOrWhiteSpace(lines[index - 1])
                || index == lines.Count - 1 || string.IsNullOrWhiteSpace(lines[index + 1]);
            if (!isolated) continue;
            var key = ReferenceOutline.ParseKey(text, volumePrefixes);
            if (key.IsEmpty || !HeadingLike(text, key)) continue;
            candidates.Add(new(index + 1, key, text));
        }
        var result = new List<(string, string[])>();
        foreach (var node in catalog.Nodes.Where(n => n.Kind == ReferenceNodeKind.Chapter))
        {
            var key = ReferenceOutline.ParseKey(node.Title, volumePrefixes);
            var ranked = candidates
                .Select(candidate => (Candidate: candidate, Score: ReferenceOutline.MatchScore(key, candidate.Key)))
                .Where(pair => pair.Score > 0)
                .OrderByDescending(pair => NumberAgrees(key, pair.Candidate.Key))
                .ThenByDescending(pair => pair.Score)
                .ThenBy(pair => pair.Candidate.Line)
                .Select(pair => $"行{pair.Candidate.Line}「{pair.Candidate.Text}」score={pair.Score} number={NumberAgrees(key, pair.Candidate.Key)} 参考Key=({key.Number}|{key.Words}) 本地Key=({pair.Candidate.Key.Number}|{pair.Candidate.Key.Words})")
                .ToArray();
            result.Add((node.Title, ranked));
        }
        return result;
    }

    /// <summary>
    /// The open interval an unplaced chapter must be found in: strictly between the nearest chapters
    /// that *were* placed, before and after it in reference order. A line outside that gap belongs to
    /// some other part of the book, so accepting it would put the chapter in the wrong place; an empty
    /// gap means the text genuinely lacks the chapter.
    /// </summary>
    private static (int Lower, int Upper) NeighbourGap(IReadOnlyList<LocatedChapter> located, int index, int lineCount)
    {
        var lower = 0;
        var upper = lineCount + 1;
        for (var probe = index - 1; probe >= 0; probe--)
            if (located[probe].Line is int before) { lower = before; break; }
        for (var probe = index + 1; probe < located.Count; probe++)
            if (located[probe].Line is int after) { upper = after; break; }
        return (lower, upper);
    }

    /// <summary>
    /// Searches the lines between two already-placed chapters for one still-unplaced chapter,
    /// strongest evidence first, and reports which test accepted the line. Split out of
    /// <see cref="Locate"/> so the same search can be measured and, if ever needed, reused.
    /// <paramref name="lower"/> and <paramref name="upper"/> bound the search to the gap the chapter
    /// belongs in; a chapter that is not in that gap is genuinely absent and must stay missing.
    /// </summary>
    private static (LocateEvidence Evidence, int Line) SearchEveryLine(
        IReadOnlyList<string> lines, TitleKey[] lineKeys, HashSet<int> used, TitleKey key, string words, bool shortTitle,
        int lower, int upper)
    {
        var fallback = -1;
        var fuzzy = -1;
        var fuzzyScore = 0.0;
        var from = Math.Max(1, lower);
        var to = Math.Min(lines.Count, upper - 1);
        for (var line = from; line <= to; line++)
        {
            if (used.Contains(line)) continue;
            var text = lines[line - 1].Trim();
            if (text.Length is 0 or > MaximumScannedLineLength) continue;
            var candidate = lineKeys[line];
            if (candidate.Words.Length == 0) continue;
            if (NumberAgrees(key, candidate)) return (LocateEvidence.NumberAgrees, line);
            if (shortTitle) continue;
            if (text.Contains(words, StringComparison.Ordinal))
            {
                if (fallback < 0) fallback = line;
                continue;
            }
            // Below this point the evidence is resemblance, not identity, and prose resembles a title
            // easily ("他想起那个约定" against 「那个约定」). Sentence punctuation is what separates
            // them: a heading may carry 「，」 or 「：」 ("001：开始，然后呢"), but a line that ends a
            // clause is a paragraph, and a paragraph is not allowed to stand in for a chapter title.
            if (IsProseLine(text)) continue;
            // A title that merely grew or lost one character is still the same title. Anything
            // further apart is a different chapter and must stay missing rather than be guessed.
            if (Math.Abs(candidate.Words.Length - words.Length) > 1) continue;
            var score = ReferenceOutline.Similarity(words, candidate.Words);
            if (score >= FuzzyThreshold && score > fuzzyScore) { fuzzyScore = score; fuzzy = line; }
        }
        if (fallback >= 0) return (LocateEvidence.TitleWords, fallback);
        return (LocateEvidence.Similarity, fuzzy);
    }

    /// <summary>
    /// True when a line reads as a sentence rather than a heading. Deliberately narrow: only the
    /// punctuation that ends a clause counts, because 「，」 and 「：」 are ordinary inside a title
    /// ("001：开始，然后呢") while 「。」 「？」 「！」 「；」 are not.
    /// </summary>
    private static bool IsProseLine(string text) => text.IndexOfAny(['。', '！', '？', '；']) >= 0;

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
