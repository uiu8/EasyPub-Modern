namespace EasyPub.Core;

public enum PreflightSeverity
{
    Information,
    Warning,
    Error,
}

public enum PreflightTargetKind
{
    General,
    InputBook,
    Chapters,
    Output,
    Cover,
    Illustrations,
    BookInformation,
    Mobi,
    Font,
    TextCleanup,
}

public sealed record ConversionPreflightIssue(
    string? InputPath,
    PreflightSeverity Severity,
    string Code,
    string Message,
    PreflightTargetKind Target = PreflightTargetKind.General,
    int? LineNumber = null,
    string? RelatedValue = null);

public sealed record ConversionPreflightBook(
    string InputPath,
    int ChapterCandidateCount);

public sealed record ConversionPreflightReport(
    IReadOnlyList<ConversionPreflightBook> Books,
    IReadOnlyList<ConversionPreflightIssue> Issues)
{
    public bool HasErrors => Issues.Any(issue => issue.Severity == PreflightSeverity.Error);
    public int WarningCount => Issues.Count(issue => issue.Severity == PreflightSeverity.Warning);
}

public sealed class ConversionPreflightInspector
{
    public async Task<ConversionPreflightReport> InspectAsync(
        IEnumerable<ConversionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var jobs = requests.ToArray();
        var issuesByJob = new IReadOnlyList<ConversionPreflightIssue>[jobs.Length];
        var booksByJob = new IReadOnlyList<ConversionPreflightBook>[jobs.Length];
        var parallelism = Math.Min(8, ConversionConcurrencyPolicy.Resolve(ConversionConcurrencyPolicy.Auto, jobs));

        await Parallel.ForEachAsync(
            Enumerable.Range(0, jobs.Length),
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (jobIndex, token) =>
        {
            var request = jobs[jobIndex];
            bool Check(PreflightTargetKind target) => request.AutomaticChecks is null || request.AutomaticChecks.Targets.Contains(target);
            var issues = new List<ConversionPreflightIssue>();
            var books = new List<ConversionPreflightBook>();
            try
            {
                token.ThrowIfCancellationRequested();
                if (request.AutomaticChecks is { Enabled: false }) return;
                if (!File.Exists(request.InputPath))
                {
                    issues.Add(new ConversionPreflightIssue(
                        request.InputPath,
                        PreflightSeverity.Error,
                        "input_missing",
                        $"找不到输入文件：{request.InputPath}",
                        PreflightTargetKind.InputBook));
                    return;
                }

                var options = request.Options ?? ConversionOptions.LegacyDefault;
                int? inputLineCount = null;
                var inputExtension = Path.GetExtension(request.InputPath);
                if (string.Equals(inputExtension, ".epub", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(Path.GetExtension(request.OutputPath), ".mobi", StringComparison.OrdinalIgnoreCase))
                    {
                        issues.Add(new ConversionPreflightIssue(
                            request.InputPath,
                            PreflightSeverity.Error,
                            "epub_output_unsupported",
                            "EPUB 输入只能转换为 MOBI。",
                            PreflightTargetKind.Output));
                    }
                    try
                    {
                        var inspection = EpubInspectionService.Inspect(request.InputPath);
                        books.Add(new ConversionPreflightBook(request.InputPath, inspection.SpineDocumentCount));
                        if (inspection.HasUnsupportedEncryption)
                            issues.Add(new ConversionPreflightIssue(request.InputPath, PreflightSeverity.Error, "epub_drm", "EPUB 含 DRM 或不支持的加密资源。", PreflightTargetKind.InputBook));
                        if (inspection.IsFixedLayout && options.Mobi.EpubInputMode == EpubInputMode.EasyPubCompatible)
                            issues.Add(new ConversionPreflightIssue(request.InputPath, PreflightSeverity.Error, "epub_fixed_layout_reflow", "固定版式 EPUB 不能兼容重排，请选择“保留原 EPUB 版式”。", PreflightTargetKind.Mobi));
                        else if (options.Mobi.EpubInputMode == EpubInputMode.EasyPubCompatible)
                        {
                            try { EpubInspectionService.ValidateCompatibleReflow(request.InputPath); }
                            catch (InvalidDataException exception)
                            {
                                issues.Add(new ConversionPreflightIssue(request.InputPath, PreflightSeverity.Error,
                                    "epub_reflow_unsafe", exception.Message, PreflightTargetKind.Mobi));
                            }
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        issues.Add(new ConversionPreflightIssue(request.InputPath, PreflightSeverity.Error, "epub_unreadable", $"无法读取 EPUB：{exception.Message}", PreflightTargetKind.InputBook));
                    }
                }
                else
                {
                    try
                    {
                        var document = await ChapterTreeDocument.LoadAsync(
                            request.InputPath,
                            options.ChapterPattern,
                            options.TocHierarchy,
                            options.TextEncoding,
                            request.ChapterTree,
                            token);
                        inputLineCount = document.LineCount;
                        var sourceText = string.Join("\n", document.SourceLines.Select(line => line.Text));
                        TextCleanupPreview? plannedCleanup = null;
                        try
                        {
                            if (options.TextCleanup.Enabled)
                            {
                                plannedCleanup = TextCleanupPipeline.Apply(sourceText, options.TextCleanup, token);
                                plannedCleanup.EnsureSourcePositionsAreSafe(request.ChapterTree is not null || options.Illustrations.Any(image => image.InsertAfterLine.HasValue));
                            }
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            issues.Add(new ConversionPreflightIssue(request.InputPath, PreflightSeverity.Error,
                                "cleanup_plan_invalid", $"转换清理规则无法安全应用：{exception.Message}", PreflightTargetKind.TextCleanup));
                        }
                        if (request.AutomaticChecks is { } checks)
                        {
                            if (checks.Cleanup.Enabled)
                            {
                                try
                                {
                                    var preview = TextCleanupPipeline.Apply(sourceText, checks.Cleanup, token);
                                    var planned = (plannedCleanup?.Changes ?? []).Where(change => change.IsApplied)
                                        .Select(change => (change.Key, change.After)).ToHashSet();
                                    var excluded = options.TextCleanup.ExcludedChangeKeys.ToHashSet(StringComparer.Ordinal);
                                    foreach (var group in preview.Changes.Where(change => change.IsApplied).GroupBy(change => new
                                    {
                                        change.Rule,
                                        State = excluded.Contains(change.Key) ? "已确认保留" : planned.Contains((change.Key, change.After)) ? "已计划清理" : "待确认"
                                    }))
                                        issues.Add(new ConversionPreflightIssue(request.InputPath,
                                            group.Key.State == "待确认" ? PreflightSeverity.Warning : PreflightSeverity.Information,
                                            "cleanup_check", $"{group.Key.Rule}：{group.Count()} 处{group.Key.State}，首处原文第 {group.First().LineNumber} 行。",
                                            PreflightTargetKind.TextCleanup, group.First().LineNumber));
                                }
                                catch (Exception exception) when (exception is not OperationCanceledException)
                                {
                                    issues.Add(new ConversionPreflightIssue(request.InputPath, PreflightSeverity.Error,
                                        "cleanup_rule_invalid", $"自动检查的清理规则无效：{exception.Message}", PreflightTargetKind.TextCleanup));
                                }
                            }
                        }
                        else
                        {
                        var notices = 0;
                        int? firstNoticeLine = null;
                        var exclusions = new HashSet<string>(options.TextCleanup.ExcludedChangeKeys, StringComparer.Ordinal);
                        foreach (var line in document.SourceLines)
                        {
                            if ((line.LineNumber & 1023) == 0) token.ThrowIfCancellationRequested();
                            if (!TextCleanupPipeline.IsSiteNotice(line.Text)) continue;
                            if (exclusions.Contains(TextCleanupPipeline.CreateChangeKey(line.LineNumber, "清理网站广告/下载说明", line.Text))) continue;
                            notices++;
                            firstNoticeLine ??= line.LineNumber;
                        }
                        if (notices > 0)
                        {
                            var enabled = options.TextCleanup.RemoveSiteNotices;
                            issues.Add(new ConversionPreflightIssue(request.InputPath,
                                enabled ? PreflightSeverity.Information : PreflightSeverity.Warning,
                                "site_notices",
                                $"发现 {notices} 行疑似网站广告/下载说明（首处原文第 {firstNoticeLine} 行）；"
                                    + (enabled ? "已开启广告清理，转换时将按规则处理，可预览确认。" : "建议打开文本清理预览并确认。"),
                                PreflightTargetKind.TextCleanup, firstNoticeLine));
                        }
                        }
                        var candidateCount = document.Entries.Count(entry => entry.TitleLineNumber.HasValue);
                        books.Add(new ConversionPreflightBook(request.InputPath, candidateCount));
                        if (candidateCount == 0)
                        {
                            issues.Add(new ConversionPreflightIssue(
                                request.InputPath,
                                PreflightSeverity.Warning,
                                "chapter_not_found",
                                "没有识别到章节标题，将作为单章电子书转换。",
                                PreflightTargetKind.Chapters));
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        issues.Add(new ConversionPreflightIssue(
                            request.InputPath,
                            PreflightSeverity.Error,
                            "input_unreadable",
                            $"无法读取或解析 TXT：{exception.Message}",
                            PreflightTargetKind.InputBook));
                    }
                }

                if (Check(PreflightTargetKind.Output) && File.Exists(request.OutputPath))
                {
                    issues.Add(new ConversionPreflightIssue(
                        request.InputPath,
                        PreflightSeverity.Warning,
                        "output_exists",
                        $"输出文件已存在，将被覆盖：{request.OutputPath}",
                        PreflightTargetKind.Output));
                }

                if (Check(PreflightTargetKind.Cover) && !string.IsNullOrWhiteSpace(options.CoverImagePath)
                    && !File.Exists(options.CoverImagePath))
                {
                    issues.Add(new ConversionPreflightIssue(
                        request.InputPath,
                        PreflightSeverity.Error,
                        "cover_missing",
                        $"找不到封面文件：{options.CoverImagePath}",
                        PreflightTargetKind.Cover));
                }
                else if (Check(PreflightTargetKind.Cover) && !string.IsNullOrWhiteSpace(options.CoverImagePath))
                {
                    try
                    {
                        await CoverImageConverter.PrepareJpegAsync(options.CoverImagePath, token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        issues.Add(new ConversionPreflightIssue(
                            request.InputPath,
                            PreflightSeverity.Error,
                            "cover_unreadable",
                            $"封面无法读取：{exception.Message}",
                            PreflightTargetKind.Cover));
                    }
                }

                foreach (var illustration in Check(PreflightTargetKind.Illustrations) ? options.Illustrations : [])
                {
                    if (illustration.InsertAfterLine is int insertAfterLine &&
                        (insertAfterLine < 1 || inputLineCount is not null && insertAfterLine > inputLineCount))
                    {
                        issues.Add(new ConversionPreflightIssue(
                            request.InputPath,
                            PreflightSeverity.Error,
                            "illustration_position_out_of_range",
                            $"正文插图“{illustration.Marker}”选择的第 {insertAfterLine} 行已超出当前 TXT 范围。",
                            PreflightTargetKind.Illustrations,
                            insertAfterLine,
                            illustration.Marker));
                    }

                    if (!File.Exists(illustration.ImagePath))
                    {
                        issues.Add(new ConversionPreflightIssue(
                            request.InputPath,
                            PreflightSeverity.Error,
                            "illustration_missing",
                            $"找不到正文插图“{illustration.Marker}”：{illustration.ImagePath}",
                            PreflightTargetKind.Illustrations,
                            illustration.InsertAfterLine,
                            illustration.Marker));
                        continue;
                    }

                    try
                    {
                        await CoverImageConverter.PrepareJpegAsync(illustration.ImagePath, token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        issues.Add(new ConversionPreflightIssue(
                            request.InputPath,
                            PreflightSeverity.Error,
                            "illustration_unreadable",
                            $"正文插图“{illustration.Marker}”无法读取：{exception.Message}",
                            PreflightTargetKind.Illustrations,
                            illustration.InsertAfterLine,
                            illustration.Marker));
                    }
                }

                var metadata = options.Metadata ?? new PublicationMetadata();
                if (!string.IsNullOrWhiteSpace(metadata.Isbn) && !IsValidIsbn(metadata.Isbn))
                {
                    issues.Add(new ConversionPreflightIssue(
                        request.InputPath,
                        PreflightSeverity.Warning,
                        "isbn_invalid",
                        "ISBN 校验未通过；仍可继续转换，但建议检查数字是否正确。",
                        PreflightTargetKind.BookInformation));
                }
                if (!string.IsNullOrWhiteSpace(metadata.Language) && !IsValidLanguageTag(metadata.Language))
                {
                    issues.Add(new ConversionPreflightIssue(
                        request.InputPath,
                        PreflightSeverity.Warning,
                        "language_invalid",
                        "语言代码格式不规范，建议使用 zh-CN、zh-TW、en、ja 等 BCP 47 标签。",
                        PreflightTargetKind.BookInformation));
                }

                if (Check(PreflightTargetKind.Font) && options.Font.Enabled)
                {
                    if (string.IsNullOrWhiteSpace(options.Font.FontPath) || !File.Exists(options.Font.FontPath))
                    {
                        issues.Add(new ConversionPreflightIssue(
                            request.InputPath,
                            PreflightSeverity.Error,
                            "font_missing",
                            "已启用字体嵌入，但找不到字体文件。",
                            PreflightTargetKind.Font));
                    }
                    else
                    {
                        try
                        {
                            var info = FontEmbeddingService.Inspect(options.Font.FontPath);
                            if (!info.CanEmbed)
                            {
                                issues.Add(new ConversionPreflightIssue(
                                    request.InputPath,
                                    PreflightSeverity.Error,
                                    "font_embedding_forbidden",
                                    "字体许可禁止嵌入电子书，请更换字体。",
                                    PreflightTargetKind.Font));
                            }
                            else if (options.Font.Subset && !info.CanSubset)
                            {
                                issues.Add(new ConversionPreflightIssue(
                                    request.InputPath,
                                    PreflightSeverity.Warning,
                                    "font_subsetting_forbidden",
                                    "字体许可不允许子集化，将完整嵌入该字体。",
                                    PreflightTargetKind.Font));
                            }
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            issues.Add(new ConversionPreflightIssue(
                                request.InputPath,
                                PreflightSeverity.Error,
                                "font_unsupported",
                                $"字体无法嵌入：{exception.Message}",
                                PreflightTargetKind.Font));
                        }
                    }
                }

                if (string.Equals(Path.GetExtension(request.OutputPath), ".mobi", StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrWhiteSpace(options.Mobi.KindleGenPath)
                        || !File.Exists(options.Mobi.KindleGenPath)))
                {
                    issues.Add(new ConversionPreflightIssue(
                        request.InputPath,
                        PreflightSeverity.Error,
                        "kindlegen_missing",
                        "MOBI 转换需要可用的 kindlegen_v2.9.exe。",
                        PreflightTargetKind.Mobi));
                }
            }
            finally
            {
                issuesByJob[jobIndex] = issues;
                booksByJob[jobIndex] = books;
            }
        });

        var issues = issuesByJob.SelectMany(items => items ?? []).ToList();
        var books = booksByJob.SelectMany(items => items ?? []).ToList();

        foreach (var duplicate in jobs
                     .GroupBy(job => Path.GetFullPath(job.OutputPath), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            issues.Add(new ConversionPreflightIssue(
                null,
                PreflightSeverity.Error,
                "duplicate_output",
                $"多本小说将写入同一个输出文件：{duplicate.Key}",
                PreflightTargetKind.Output));
        }

        return new ConversionPreflightReport(books, issues);
    }

    private static bool IsValidLanguageTag(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            value.Trim(),
            "^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsValidIsbn(string value)
    {
        var normalized = new string(value.Where(character => character is not ('-' or ' ')).ToArray())
            .ToUpperInvariant();
        if (normalized.Length == 10)
        {
            var sum = 0;
            for (var index = 0; index < 10; index++)
            {
                var digit = index == 9 && normalized[index] == 'X'
                    ? 10
                    : normalized[index] is >= '0' and <= '9'
                        ? normalized[index] - '0'
                        : -1;
                if (digit < 0) return false;
                sum += (10 - index) * digit;
            }
            return sum % 11 == 0;
        }
        if (normalized.Length == 13 && normalized.All(char.IsDigit))
        {
            var sum = 0;
            for (var index = 0; index < 13; index++)
                sum += (normalized[index] - '0') * (index % 2 == 0 ? 1 : 3);
            return sum % 10 == 0;
        }
        return false;
    }
}
