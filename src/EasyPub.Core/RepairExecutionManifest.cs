using System.Text.Json;
using System.Text.Json.Serialization;

namespace EasyPub.Core;

/// <summary>
/// One action's decision, stored so recovery never has to ask the planner again.
///
/// <para>Self-contained on purpose. A recovery runs after a crash, when the proposal that produced these
/// decisions is gone; making it re-derive them would mean re-running the planner against a file that has
/// already changed, and that is exactly the thing the transaction boundary exists to prevent.</para>
/// </summary>
public sealed record RepairDecisionSnapshot(
    string ActionId,
    ReferenceActionKind Kind,
    string Title,
    bool Selected,
    SourceEffectDecision SourceEffect,
    SourceEffectOutcome Outcome,
    int? SourceLine,
    string? DesiredTitle);

/// <summary>
/// What a transaction is going to do, frozen before it does any of it.
///
/// <para>This is the file that makes an interrupted transaction recoverable. Without it a crash after the
/// source was replaced leaves the program holding a patch, a candidate id and two hashes — but no idea
/// what the chapter tree was supposed to become. The patch says which lines changed; nothing there says
/// which chapters were meant to exist afterwards.</para>
///
/// <para>It also cannot be re-derived. Re-running the planner would work from the file as it is now —
/// already replaced, and possibly under different recognition rules than when the user agreed — and the
/// result is not guaranteed to be what they accepted. So the intent is written down once, before anything
/// is touched, and read back rather than recomputed.</para>
///
/// <para><b>Written and flushed before the source file is replaced, and never edited afterwards.</b> The
/// expected hash is in here rather than in the journal so that the one file recovery trusts is the one
/// that cannot drift.</para>
/// </summary>
public sealed record RepairExecutionManifest(
    int SchemaVersion,
    string TransactionId,
    string ProposalId,
    RepairBaseVersion BaseVersion,
    IReadOnlyList<RepairDecisionSnapshot> Decisions,
    TreePatch TreePatch,
    SourcePatch? SourcePatch,
    CanonicalChapterModel ExpectedCanonicalResult,
    string ExpectedNewSha256,
    string BaseSnapshotSha256,
    RepairLandingMode LandingMode,
    string? SourceBackupSnapshotPath,
    string? DefaultNewLine)
{
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// Reads and writes the execution manifest.
///
/// <para>The format is written out by hand rather than left to the serializer. These are abstract records
/// — a source operation is one of three shapes, a tree mutation one of five — and a serializer left to its
/// own devices would produce something whose meaning depends on type names, which then become part of the
/// file format by accident. Writing it out makes the format the thing under control.</para>
/// </summary>
public static class RepairExecutionManifestStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public const string FileName = "execution-manifest.json";

    public static string PathFor(string transactionDirectory) =>
        Path.Combine(transactionDirectory, FileName);

    /// <summary>
    /// Writes the manifest and makes sure it is on disk before returning.
    ///
    /// <para>The flush is the point of the method. The source file must not be replaced on the strength of
    /// a manifest that is still in a cache, so this does not return until the bytes have been handed to
    /// the operating system to write, and then reads them back to check the write was not damaged.</para>
    /// </summary>
    public static void Write(string transactionDirectory, RepairExecutionManifest manifest)
    {
        Directory.CreateDirectory(transactionDirectory);
        var target = PathFor(transactionDirectory);
        var payload = JsonSerializer.Serialize(ToDto(manifest), Json);

        using (var stream = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        // Read-back integrity check — not a durability proof. It shows the write produced the bytes it was
        // given and did not land on the wrong file; durability is the flush's job.
        var readBack = File.ReadAllText(target);
        if (!string.Equals(readBack, payload, StringComparison.Ordinal))
            throw new InvalidDataException("事务清单写入后读回的内容不一致，已停止后续步骤。");
    }

    public static RepairExecutionManifest? Read(string transactionDirectory)
    {
        var path = PathFor(transactionDirectory);
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var dto = JsonSerializer.Deserialize<ManifestDto>(stream, Json);
            return dto is null ? null : FromDto(dto);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ── 格式 ────────────────────────────────────────────────────

    private sealed record ManifestDto
    {
        public int SchemaVersion { get; init; }
        public string TransactionId { get; init; } = "";
        public string ProposalId { get; init; } = "";
        public string BaseSourceSha256 { get; init; } = "";
        public string BaseTreeFingerprint { get; init; } = "";
        public string RecognitionFingerprint { get; init; } = "";
        public List<DecisionDto> Decisions { get; init; } = [];
        public List<MutationDto> Mutations { get; init; } = [];
        public List<ChapterDto> ExpectedChapters { get; init; } = [];
        public List<OperationDto>? Operations { get; init; }
        public string ExpectedNewSha256 { get; init; } = "";
        public string BaseSnapshotSha256 { get; init; } = "";
        public string LandingMode { get; init; } = "";
        public string? SourceBackupSnapshotPath { get; init; }
        public string? DefaultNewLine { get; init; }
    }

    private sealed record DecisionDto
    {
        public string ActionId { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Title { get; init; } = "";
        public bool Selected { get; init; }
        public string SourceEffect { get; init; } = "";
        public string Outcome { get; init; } = "";
        public int? SourceLine { get; init; }
        public string? DesiredTitle { get; init; }
    }

    /// <summary>
    /// A mutation flattened to fields rather than nested objects. A reader of this file — a person during
    /// an incident, or a future version — can see what changed without knowing the type hierarchy.
    /// </summary>
    private sealed record MutationDto
    {
        public string Kind { get; init; } = "";
        public string SemanticId { get; init; } = "";
        public string ActionId { get; init; } = "";
        public string Binding { get; init; } = "";
        public int? Line { get; init; }
        public string? Title { get; init; }
        public int? Level { get; init; }
        public bool? IncludeInToc { get; init; }
        public bool? TextStaysInBody { get; init; }
        public string? Body { get; init; }
    }

    private sealed record OperationDto
    {
        public string Kind { get; init; } = "";
        public string OperationId { get; init; } = "";
        public string ActionId { get; init; } = "";
        public int? Line { get; init; }
        public string? ExpectedOldText { get; init; }
        public string? NewText { get; init; }
        public string? Anchor { get; init; }
        public int? AnchorLine { get; init; }
        public int? AnchorOrdinal { get; init; }
        public string? Text { get; init; }
    }

    /// <summary>
    /// One expected chapter, stored field by field rather than as a sentence.
    ///
    /// <para>A readable one-line summary was the first attempt and it was a mistake: the hash count went in
    /// but the hashes themselves did not, so a recovery could compare the shape of the expected tree and
    /// not its content. The whole point of carrying the expected result is to be able to prove the tree that
    /// was built is the tree that was agreed.</para>
    /// </summary>
    private sealed record ChapterDto
    {
        public string Title { get; init; } = "";
        public int Level { get; init; }
        public bool IncludeInToc { get; init; }
        public string Origin { get; init; } = "";
        public string Kind { get; init; } = "";
        public List<int> ParentPath { get; init; } = [];
        public List<string> BodyContentHashes { get; init; } = [];
    }

    private static ManifestDto ToDto(RepairExecutionManifest manifest) => new()
    {
        SchemaVersion = manifest.SchemaVersion,
        TransactionId = manifest.TransactionId,
        ProposalId = manifest.ProposalId,
        BaseSourceSha256 = manifest.BaseVersion.BaseSourceSha256,
        BaseTreeFingerprint = manifest.BaseVersion.BaseTreeFingerprint,
        RecognitionFingerprint = manifest.BaseVersion.RecognitionFingerprint,
        Decisions = manifest.Decisions.Select(decision => new DecisionDto
        {
            ActionId = decision.ActionId,
            Kind = decision.Kind.ToString(),
            Title = decision.Title,
            Selected = decision.Selected,
            SourceEffect = decision.SourceEffect.ToString(),
            Outcome = decision.Outcome.ToString(),
            SourceLine = decision.SourceLine,
            DesiredTitle = decision.DesiredTitle,
        }).ToList(),
        Mutations = manifest.TreePatch.Ordered().Select(ToDto).ToList(),
        ExpectedChapters = manifest.ExpectedCanonicalResult.Chapters.Select(ToDto).ToList(),
        Operations = manifest.SourcePatch?.Operations.Select(ToDto).ToList(),
        ExpectedNewSha256 = manifest.ExpectedNewSha256,
        BaseSnapshotSha256 = manifest.BaseSnapshotSha256,
        LandingMode = manifest.LandingMode.ToString(),
        SourceBackupSnapshotPath = manifest.SourceBackupSnapshotPath,
        DefaultNewLine = manifest.DefaultNewLine,
    };

    /// <summary>A chapter stored field by field, so it can be compared on recovery rather than just counted.</summary>
    private static ChapterDto ToDto(CanonicalChapter chapter) => new()
    {
        Title = chapter.Title,
        Level = chapter.Level,
        IncludeInToc = chapter.IncludeInToc,
        Origin = chapter.HeadingOrigin.ToString(),
        Kind = chapter.Kind.ToString(),
        ParentPath = chapter.ParentPath.ToList(),
        BodyContentHashes = chapter.BodyContentHashes.ToList(),
    };

    private static CanonicalChapter FromDto(ChapterDto dto) => new(
        dto.Title,
        dto.Level,
        dto.IncludeInToc,
        Enum.TryParse<CanonicalHeadingOrigin>(dto.Origin, out var origin)
            ? origin : CanonicalHeadingOrigin.ExistingSourceHeading,
        Enum.TryParse<CanonicalNodeKind>(dto.Kind, out var kind) ? kind : CanonicalNodeKind.Chapter,
        dto.ParentPath,
        dto.BodyContentHashes);

    private static MutationDto ToDto(ChapterTreeMutation mutation) => mutation switch
    {
        InsertEntry insert => new MutationDto
        {
            Kind = "InsertEntry", SemanticId = insert.SemanticId, ActionId = insert.ActionId,
            Binding = Describe(insert.Binding), Title = insert.Title, Level = insert.Level,
            IncludeInToc = insert.IncludeInToc, Body = Describe(insert.Body),
        },
        RetitleEntry retitle => new MutationDto
        {
            Kind = "RetitleEntry", SemanticId = retitle.SemanticId, ActionId = retitle.ActionId,
            Binding = Describe(retitle.Binding), Title = retitle.NewTitle,
        },
        RemoveEntry remove => new MutationDto
        {
            Kind = "RemoveEntry", SemanticId = remove.SemanticId, ActionId = remove.ActionId,
            Binding = Describe(remove.Binding), TextStaysInBody = remove.TextStaysInBody,
        },
        ChangeEntryLevel level => new MutationDto
        {
            Kind = "ChangeEntryLevel", SemanticId = level.SemanticId, ActionId = level.ActionId,
            Binding = Describe(level.Binding), Level = level.NewLevel,
        },
        ReassignEntryBody body => new MutationDto
        {
            Kind = "ReassignEntryBody", SemanticId = body.SemanticId, ActionId = body.ActionId,
            Binding = Describe(body.Binding), Body = Describe(body.Body),
        },
        _ => new MutationDto { Kind = "Unknown", ActionId = mutation.ActionId },
    };

    private static OperationDto ToDto(SourceOperation operation) => operation switch
    {
        DeleteOriginalLine delete => new OperationDto
        {
            Kind = "DeleteOriginalLine", OperationId = delete.OperationId, ActionId = delete.ActionId,
            Line = delete.Line, ExpectedOldText = delete.ExpectedOldText,
        },
        ReplaceOriginalLine replace => new OperationDto
        {
            Kind = "ReplaceOriginalLine", OperationId = replace.OperationId, ActionId = replace.ActionId,
            Line = replace.Line, ExpectedOldText = replace.ExpectedOldText, NewText = replace.NewText,
        },
        InsertLineAtAnchor insert => new OperationDto
        {
            Kind = "InsertLineAtAnchor", OperationId = insert.OperationId, ActionId = insert.ActionId,
            Anchor = AnchorKind(insert.Anchor), AnchorLine = AnchorLine(insert.Anchor),
            AnchorOrdinal = SourceAnchor.OrdinalOf(insert.Anchor), Text = insert.Text,
        },
        _ => new OperationDto { Kind = "Unknown", ActionId = operation.ActionId },
    };

    private static string Describe(SourceBinding binding) => binding switch
    {
        OriginalLineBinding line => "original:" + line.Line,
        InsertedLineBinding inserted => "inserted:" + inserted.OperationId,
        _ => "none",
    };

    private static string Describe(BodyAssignment body) => string.Join(',',
        body.Spans.Select(span => span.Start + "-" + span.End));

    private static string AnchorKind(SourceAnchor anchor) => anchor switch
    {
        BeforeOriginalLine => "before",
        AfterOriginalLine => "after",
        _ => "end",
    };

    private static int? AnchorLine(SourceAnchor anchor) => anchor switch
    {
        BeforeOriginalLine before => before.Line,
        AfterOriginalLine after => after.Line,
        _ => null,
    };

    private static RepairExecutionManifest FromDto(ManifestDto dto) => new(
        dto.SchemaVersion,
        dto.TransactionId,
        dto.ProposalId,
        new RepairBaseVersion(dto.BaseSourceSha256, dto.BaseTreeFingerprint, dto.RecognitionFingerprint),
        dto.Decisions.Select(decision => new RepairDecisionSnapshot(
            decision.ActionId,
            Enum.TryParse<ReferenceActionKind>(decision.Kind, out var kind) ? kind : ReferenceActionKind.KeepExtra,
            decision.Title,
            decision.Selected,
            Enum.TryParse<SourceEffectDecision>(decision.SourceEffect, out var effect) ? effect : SourceEffectDecision.None,
            Enum.TryParse<SourceEffectOutcome>(decision.Outcome, out var outcome) ? outcome : SourceEffectOutcome.NotRequested,
            decision.SourceLine,
            decision.DesiredTitle)).ToArray(),
        new TreePatch(dto.ProposalId, dto.Mutations.Select(FromDto).ToArray(),
            new CanonicalChapterModel(dto.ExpectedChapters.Select(FromDto).ToArray())),
        dto.Operations is null ? null : new SourcePatch(dto.ProposalId, dto.BaseSourceSha256,
            dto.Operations.Select(FromDto).ToArray()),
        new CanonicalChapterModel(dto.ExpectedChapters.Select(FromDto).ToArray()),
        dto.ExpectedNewSha256,
        dto.BaseSnapshotSha256,
        Enum.TryParse<RepairLandingMode>(dto.LandingMode, out var mode) ? mode : RepairLandingMode.TreeOnly,
        dto.SourceBackupSnapshotPath,
        dto.DefaultNewLine);

    private static ChapterTreeMutation FromDto(MutationDto dto) => dto.Kind switch
    {
        "InsertEntry" => new InsertEntry(dto.SemanticId, dto.Title ?? "", dto.Level ?? 1,
            dto.IncludeInToc ?? true, ParseBody(dto.Body))
        { ActionId = dto.ActionId, Binding = ParseBinding(dto.Binding) },
        "RetitleEntry" => new RetitleEntry(dto.SemanticId, dto.Title ?? "")
        { ActionId = dto.ActionId, Binding = ParseBinding(dto.Binding) },
        "RemoveEntry" => new RemoveEntry(dto.SemanticId, dto.TextStaysInBody ?? true)
        { ActionId = dto.ActionId, Binding = ParseBinding(dto.Binding) },
        "ChangeEntryLevel" => new ChangeEntryLevel(dto.SemanticId, dto.Level ?? 1)
        { ActionId = dto.ActionId, Binding = ParseBinding(dto.Binding) },
        "ReassignEntryBody" => new ReassignEntryBody(dto.SemanticId, ParseBody(dto.Body))
        { ActionId = dto.ActionId, Binding = ParseBinding(dto.Binding) },
        _ => throw new InvalidDataException($"事务清单里有无法识别的树变化：{dto.Kind}"),
    };

    private static SourceOperation FromDto(OperationDto dto) => dto.Kind switch
    {
        "DeleteOriginalLine" => new DeleteOriginalLine(dto.Line ?? 0, dto.ExpectedOldText ?? "")
        { OperationId = dto.OperationId, ActionId = dto.ActionId },
        "ReplaceOriginalLine" => new ReplaceOriginalLine(dto.Line ?? 0, dto.ExpectedOldText ?? "", dto.NewText ?? "")
        { OperationId = dto.OperationId, ActionId = dto.ActionId },
        "InsertLineAtAnchor" => new InsertLineAtAnchor(
            dto.Anchor switch
            {
                "before" => new BeforeOriginalLine(dto.AnchorLine ?? 1, dto.AnchorOrdinal ?? 0),
                "after" => new AfterOriginalLine(dto.AnchorLine ?? 1, dto.AnchorOrdinal ?? 0),
                _ => new EndOfFileAnchor(dto.AnchorOrdinal ?? 0),
            },
            dto.Text ?? "")
        { OperationId = dto.OperationId, ActionId = dto.ActionId },
        _ => throw new InvalidDataException($"事务清单里有无法识别的文本操作：{dto.Kind}"),
    };

    private static SourceBinding ParseBinding(string text) => text switch
    {
        _ when text.StartsWith("original:", StringComparison.Ordinal)
            && int.TryParse(text["original:".Length..], out var line) => new OriginalLineBinding(line),
        _ when text.StartsWith("inserted:", StringComparison.Ordinal)
            => new InsertedLineBinding(text["inserted:".Length..]),
        _ => new NoSourceBinding(),
    };

    private static BodyAssignment ParseBody(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return BodyAssignment.Empty;
        var spans = new List<SourceLineSpan>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var halves = part.Split('-');
            if (halves.Length == 2
                && int.TryParse(halves[0], out var start) && int.TryParse(halves[1], out var end))
                spans.Add(new SourceLineSpan(start, end));
        }
        return new BodyAssignment(spans);
    }
}
