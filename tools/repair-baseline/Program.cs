using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EasyPub.Core;

// Deterministic baseline, not an automatic repair acceptance test. Never edits the fixture.
var repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var fixtureOption = Array.IndexOf(args, "--fixture");
var fixture = fixtureOption >= 0
    ? Path.GetFullPath(args[fixtureOption + 1], repo)
    : Path.Combine(repo, "samples", "六卷690章修复验收");
var plainCatalog = args.Contains("--plain-catalog", StringComparer.Ordinal);
var manualSplits = args.Contains("--manual-splits", StringComparer.Ordinal);
var output = Path.Combine(repo, "work", "baseline", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("EASYPUB_REFERENCE_CATALOGS_PATH", Path.Combine(output, "catalog-store"));
Environment.SetEnvironmentVariable("EASYPUB_SOURCE_BACKUPS_PATH", Path.Combine(output, "backup-store"));
Environment.SetEnvironmentVariable("EASYPUB_APP_SETTINGS_PATH", Path.Combine(output, "settings.json"));
using var truth = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture, "真值清单.json")));
var chapters = truth.RootElement.GetProperty("chapters").EnumerateArray().Select((e, rank) => new Truth(
    e.GetProperty("id").GetString()!, e.GetProperty("title").GetString()!, e.GetProperty("body_sha256").GetString()!,
    e.GetProperty("missing_body").GetBoolean(), rank)).ToArray();
var byHash = chapters.ToDictionary(c => c.Hash);
var byTitle = chapters.ToDictionary(c => c.Title);
var originalBytes = File.ReadAllBytes(Path.Combine(fixture, "缺陷样书.txt"));
var originalSha = Hash(originalBytes);
foreach (var hash in truth.RootElement.GetProperty("hashes").EnumerateObject())
    if (Hash(File.ReadAllBytes(Path.Combine(fixture, hash.Name))) != hash.Value.GetString())
        throw new InvalidDataException("Fixture hash differs: " + hash.Name);
// Independently validate the fixture's body truth before using it as an oracle.
var fixtureLines = Encoding.UTF8.GetString(originalBytes).Split('\n');
foreach (var instance in truth.RootElement.GetProperty("instances").EnumerateArray())
{
    var start = instance.GetProperty("body_start_line").GetInt32();
    var end = instance.GetProperty("body_end_line").GetInt32();
    var id = instance.GetProperty("id").GetString();
    if (Hash(Encoding.UTF8.GetBytes(string.Join('\n', fixtureLines[(start - 1)..end]))) != chapters.Single(c => c.Id == id).Hash)
        throw new InvalidDataException("Body truth differs: " + id);
}
var results = new List<object>();
foreach (var (label, reference, mode) in new[] {
    ("MIX-M1", false, RepairLandingMode.TreeOnly), ("MIX-M2", true, RepairLandingMode.TreeOnly),
    ("MIX-M3", false, RepairLandingMode.EditSource), ("MIX-M4", true, RepairLandingMode.EditSource) })
{
    if (manualSplits && !reference) continue;
    var folder = Path.Combine(output, label);
    Directory.CreateDirectory(folder);
    var path = Path.Combine(folder, "book.txt");
    File.WriteAllBytes(path, originalBytes);
    var watch = Stopwatch.StartNew();
    object result;
    try
    {
        var document = await ChapterTreeDocument.LoadAsync(path);
        var inputAudit = Audit(document.Entries, document);
        var loadedMs = watch.Elapsed.TotalMilliseconds;
        var catalogText = File.ReadAllText(Path.Combine(fixture, "参考目录.txt"));
        // The fixture uses Markdown volume markers, which ParseText does not support.
        // Keep raw and explicitly adapted runs separate; never silently fix the fixture.
        if (plainCatalog) catalogText = string.Join('\n', catalogText.Split('\n').Select(line => line.StartsWith("# ", StringComparison.Ordinal) ? line[2..] : line));
        var catalog = reference ? ReferenceCatalogInput.ParseText(catalogText) : null;
        if (reference) File.WriteAllText(Path.Combine(folder, "catalog-input.txt"), catalogText);
        if (reference && plainCatalog && (catalog!.VolumeTitles.Count != 6 || catalog.Titles.Count != 690))
            throw new InvalidDataException("Adapted catalog must contain six volumes and 690 chapters.");
        // Prepare receives the basis explicitly: no acquisition/cache/network path.
        var outcome = ChapterAutoRepair.Prepare(document, catalog);
        var proposal = ChapterRepairApplier.ProposalFor(outcome, document)!;
        var decisions = ChapterRepairApplier.DecisionsFor(proposal, mode, outcome.Plan!.DefaultSelection.ToArray());
        var preparedMs = watch.Elapsed.TotalMilliseconds;
        var applier = new ChapterRepairApplier(Path.Combine(folder, "backups"));
        var preview = applier.Preview(proposal, document, decisions, mode, buildVolumeLevels: reference);
        var previewedMs = watch.Elapsed.TotalMilliseconds;
        var applied = await applier.ApplyAsync(proposal, document, decisions, mode, buildVolumeLevels: reference);
        var appliedMs = watch.Elapsed.TotalMilliseconds;
        var afterDocument = ChapterTreeDocument.Load(path, File.ReadAllBytes(path));
        var entries = applied.Changed
            ? applied.Transaction is null ? applied.Compilation!.RebuiltEntries : applied.State?.Plan?.Entries
            : document.Entries;
        if (entries is null) throw new InvalidDataException("Changed result has no final tree.");
        var audit = Audit(entries, afterDocument);
        var manualAudit = manualSplits
            ? await VerifyManualSplits(afterDocument.WithEntries(entries), folder, mode == RepairLandingMode.EditSource)
            : null;
        await Write(Path.Combine(folder, "tree.json"), entries);
        await Write(Path.Combine(folder, "decisions.json"), decisions);
        await Write(Path.Combine(folder, "proposal.json"), new { proposal.Actions, outcome.Report });
        result = new {
            label, reference, mode = mode.ToString(), plainCatalog,
            catalogVolumes = catalog?.VolumeTitles.Count, catalogChapters = catalog?.Titles.Count,
            selection = "Plan.DefaultSelection; source deletion NOT authorized; no manual movement/splitting",
            rules = document.RecognitionOptions, inputEntries = document.Entries.Count, inputAudit,
            actions = proposal.Actions.Count, selected = decisions.Count(d => d.Selected), preview,
            applied.Changed, applied.Message, needsAttention = applied.NeedsAttention,
            transaction = applied.Transaction?.Outcome.ToString(), backup = applied.Transaction?.BackupPath,
            manualAudit, removedLines = applied.Compilation?.RemovedLines, sourceShaBefore = originalSha,
            sourceShaAfter = Hash(File.ReadAllBytes(path)), audit,
            timingMs = new { load = loadedMs, prepare = preparedMs - loadedMs, preview = previewedMs - preparedMs, apply = appliedMs - previewedMs },
            limitation = manualSplits
                ? "manualAudit uses independent truth to simulate confirmed boundaries and verifies save/reopen. No physical UI, EPUB/MOBI or reader acceptance."
                : "Automatic default-selection baseline only. No UI, manual choices, save/reopen, EPUB/MOBI or reader acceptance."
        };
    }
    catch (Exception error) { result = new { label, error = error.ToString(), elapsedMs = watch.Elapsed.TotalMilliseconds, sourceShaAfter = Hash(File.ReadAllBytes(path)) }; }
    await Write(Path.Combine(folder, "result.json"), result);
    results.Add(result);
    Console.WriteLine(label + " recorded");
}
if (Hash(File.ReadAllBytes(Path.Combine(fixture, "缺陷样书.txt"))) != originalSha)
    throw new InvalidDataException("Original fixture was modified.");
await Write(Path.Combine(output, "baseline.json"), new { createdUtc = DateTime.UtcNow, fixture, originalSha,
    expectedChapters = chapters.Length, expectedPresentBodies = chapters.Count(c => !c.Missing), results });
Console.WriteLine("REPORT=" + output);
if (results.Any(r => JsonSerializer.SerializeToElement(r).TryGetProperty("error", out _))) Environment.ExitCode = 1;

async Task<object> VerifyManualSplits(ChapterTreeDocument document, string folder, bool exportCopies)
{
    var startingPath = document.SourcePath;
    var startingHash = Hash(File.ReadAllBytes(startingPath));
    var steps = new List<object>();
    var missingHeadings = truth.RootElement.GetProperty("chapters").EnumerateArray()
        .Where(c => c.GetProperty("missing_heading").GetBoolean() && !c.GetProperty("missing_body").GetBoolean()).ToArray();
    if (missingHeadings.Length != 11) throw new InvalidDataException("Expected exactly 11 manual boundaries.");
    foreach (var chapter in missingHeadings)
    {
        var id = chapter.GetProperty("id").GetString()!;
        var title = chapter.GetProperty("title").GetString()!;
        var instance = truth.RootElement.GetProperty("instances").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);
        var bodyStart = instance.GetProperty("body_start_line").GetInt32();
        var bodyEnd = instance.GetProperty("body_end_line").GetInt32();
        var body = fixtureLines[(bodyStart - 1)..bodyEnd];
        var starts = Enumerable.Range(1, document.LineCount - body.Length + 1)
            .Where(n => document.SourceLine(n)!.Text == body[0]
                && body.Select((text, offset) => document.SourceLine(n + offset)?.Text == text).All(match => match)).ToArray();
        if (starts.Length != 1) throw new InvalidDataException("Truth boundary not unique: " + id);
        var line = starts[0];
        var owner = document.Entries.Single(e => e.ContentRanges.Any(r => line >= r.StartLine && line <= r.EndLine));
        var before = BodyLines(document).ToArray();
        var priorPath = document.SourcePath;
        var priorHash = Hash(File.ReadAllBytes(priorPath));
        string? copyProject = null;
        if (exportCopies)
        {
            var result = await ManualSplitCopy.Prepare(document, owner.Id, line, title)
                .ExportAsync(new SourceBackupStore(Path.Combine(folder, "manual-copy-store")));
            if (!result.Succeeded) throw new InvalidDataException(result.Transaction.Message);
            copyProject = result.ProjectPath;
            var project = await new EasyPubProjectStore(copyProject).LoadAsync();
            var book = project.Books.Single();
            document = await ChapterTreeDocument.LoadAsync(book.InputPath, existingPlan: book.ChapterTree);
        }
        else
        {
            var split = ChapterContentSplit.At(owner.ContentRanges, line) ?? throw new InvalidDataException("Cannot split " + id);
            var list = document.Entries.ToList();
            var index = list.FindIndex(e => e.Id == owner.Id);
            list[index] = owner with { ContentRanges = split.Before };
            list.Insert(index + 1, owner with { Id = Guid.NewGuid().ToString("N"), Title = title,
                TitleLineNumber = null, ContentRanges = split.After, RecognitionSource = "manual", IncludeInToc = true });
            RepairIntegrity.Verify(document.Entries, list, []);
            document = document.WithEntries(list);
        }
        if (Hash(File.ReadAllBytes(priorPath)) != priorHash) throw new InvalidDataException("Previous source changed: " + id);
        if (!before.SequenceEqual(BodyLines(document)))
            throw new InvalidDataException("Body sequence changed: " + id);
        steps.Add(new { id, title, originalTruthLine = bodyStart, selectedLine = line, owner = owner.Title, copyProject });
    }
    var projectPath = Path.Combine(folder, "manual-final.easypubproj");
    var finalPlan = document.CreatePlan(document.Entries);
    await new EasyPubProjectStore(projectPath).SaveAsync(new EasyPubProjectDocument(
        EasyPubProjectDocument.CurrentSchemaVersion, projectPath, folder, ConversionProfile.Default,
        [new(document.SourcePath, "六卷人工边界验收", null, null, []) { ChapterTree = finalPlan }], DateTimeOffset.Now));
    var saved = (await new EasyPubProjectStore(projectPath).LoadAsync()).Books.Single();
    var reopened = await ChapterTreeDocument.LoadAsync(saved.InputPath, existingPlan: saved.ChapterTree);
    await Write(Path.Combine(folder, "manual-before-reopen.json"), document.Entries);
    await Write(Path.Combine(folder, "manual-after-reopen.json"), reopened.Entries);
    // Persisted legacy zero means "use the node level"; reopening materializes that default.
    var effectiveBefore = document.Entries.Select(e => e with
        { HeadingLevel = e.HeadingLevel is >= 1 and <= 4 ? e.HeadingLevel : e.Level });
    if (ChapterTreeFingerprint.Compute(reopened.Entries) != ChapterTreeFingerprint.Compute(effectiveBefore))
        throw new InvalidDataException("Save/reopen changed structure.");
    var final = Audit(reopened.Entries, reopened);
    var check = JsonSerializer.SerializeToElement(final);
    foreach (var key in new[] { "missingPresentBodies", "unexpectedMissingBodiesFound", "ownerMismatches", "invalidLines", "orderRegressions" })
        if (check.GetProperty(key).GetArrayLength() != 0) throw new InvalidDataException("Manual audit failed: " + key);
    if (check.GetProperty("distinctBodies").GetInt32() != 679 || check.GetProperty("bodyOccurrences").GetInt32() != 679)
        throw new InvalidDataException("Expected 679 unique body occurrences.");
    var v4Titles = chapters.Where(c => c.Id.StartsWith("v4-", StringComparison.Ordinal) && !c.Missing).Select(c => c.Title).ToHashSet();
    var v4Count = reopened.Entries.Count(e => v4Titles.Contains(e.Title));
    if (v4Count != 89 || chapters.Where(c => c.Missing).Any(c => reopened.Entries.Any(e => e.Title == c.Title)))
        throw new InvalidDataException("Fourth volume must have 89 chapters without missing-body placeholders.");
    if (Hash(File.ReadAllBytes(startingPath)) != startingHash) throw new InvalidDataException("Manual workflow changed starting source.");
    var resultReport = new { exportCopies, boundarySource = "Independent fixture truth; simulates user-confirmed boundaries, not automatic detection",
        steps, v4Count, final, projectPath, startingSourceUnchanged = true };
    await Write(Path.Combine(folder, "manual-splits.json"), resultReport);
    return resultReport;
}

static IEnumerable<string> BodyLines(ChapterTreeDocument document) => document.Entries.SelectMany(e => e.ContentRanges)
    .SelectMany(r => Enumerable.Range(r.StartLine, r.EndLine - r.StartLine + 1))
    .Select(n => document.SourceLine(n)?.Text ?? throw new InvalidDataException("Missing body line."));

object Audit(IReadOnlyList<ChapterTreeEntry> entries, ChapterTreeDocument document)
{
    var counts = new Dictionary<string, int>();
    var order = new List<string>();
    var ownerMismatches = new List<object>();
    var invalidLines = new List<int>();
    foreach (var entry in entries)
    {
        var lines = new List<string>();
        foreach (var range in entry.ContentRanges)
            for (var line = range.StartLine; line <= range.EndLine; line++)
            {
                if (line < 1 || line > document.LineCount) { invalidLines.Add(line); continue; }
                var text = document.SourceLine(line)?.Text ?? "";
                if (text.Length > 0) lines.Add(text);
            }
        for (var i = 0; i + 2 < lines.Count; i++)
        {
            var hash = Hash(Encoding.UTF8.GetBytes(string.Join('\n', lines.Skip(i).Take(3))));
            if (!byHash.TryGetValue(hash, out var chapter)) continue;
            counts[chapter.Id] = counts.GetValueOrDefault(chapter.Id) + 1;
            order.Add(chapter.Id);
            if (!byTitle.TryGetValue(entry.Title, out var owner) || owner.Id != chapter.Id)
                ownerMismatches.Add(new { chapter.Id, ownerTitle = entry.Title, entry.TitleLineNumber });
        }
    }
    var ranks = chapters.ToDictionary(c => c.Id, c => c.Rank);
    var seen = new HashSet<string>();
    var unique = order.Where(seen.Add).ToArray();
    var regressions = unique.Zip(unique.Skip(1)).Where(pair => ranks[pair.Second] < ranks[pair.First])
        .Select(pair => new { previous = pair.First, next = pair.Second }).ToArray();
    return new { entries = entries.Count, distinctBodies = counts.Count, bodyOccurrences = counts.Values.Sum(),
        missingPresentBodies = chapters.Where(c => !c.Missing && !counts.ContainsKey(c.Id)).Select(c => c.Id).ToArray(),
        unexpectedMissingBodiesFound = chapters.Where(c => c.Missing && counts.ContainsKey(c.Id)).Select(c => c.Id).ToArray(),
        duplicateBodies = counts.Where(p => p.Value > 1).ToDictionary(p => p.Key, p => p.Value),
        ownerMismatches, invalidLines, orderRegressions = regressions, bodyOrder = order };
}
static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
static Task Write(string path, object value) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
record Truth(string Id, string Title, string Hash, bool Missing, int Rank);
