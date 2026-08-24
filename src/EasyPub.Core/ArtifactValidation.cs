using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace EasyPub.Core;

public enum ArtifactValidationSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ArtifactValidationIssue(
    ArtifactValidationSeverity Severity,
    string Code,
    string Message);

public sealed record ArtifactValidationReport(
    string OutputPath,
    string Format,
    IReadOnlyList<ArtifactValidationIssue> Issues,
    DateTimeOffset CheckedAt,
    bool RequiresKindleHardwareConfirmation,
    string? ReportPath = null)
{
    public bool StructurePassed => Issues.All(issue => issue.Severity != ArtifactValidationSeverity.Error);
    public int WarningCount => Issues.Count(issue => issue.Severity == ArtifactValidationSeverity.Warning);
    public string ResultLabel => StructurePassed
        ? WarningCount == 0 ? "结构通过" : $"结构通过，{WarningCount} 个提醒"
        : $"结构未通过，{Issues.Count(issue => issue.Severity == ArtifactValidationSeverity.Error)} 个错误";
}

public sealed class ArtifactValidationService
{
    public async Task<ArtifactValidationReport> ValidateAndSaveAsync(
        ConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var extension = Path.GetExtension(request.OutputPath).ToLowerInvariant();
        var report = extension switch
        {
            ".epub" => ValidateEpub(request.OutputPath),
            ".mobi" => await ValidateMobiAsync(request, cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException("只能验收 EPUB 或 MOBI 成品。"),
        };
        var reportPath = request.OutputPath + ".easypub-report.json";
        var saved = report with { ReportPath = Path.GetFullPath(reportPath) };
        try
        {
            await SaveReportAsync(saved, reportPath, cancellationToken).ConfigureAwait(false);
            return saved;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return report with
            {
                Issues = report.Issues.Concat([
                    new ArtifactValidationIssue(ArtifactValidationSeverity.Warning, "report_save_failed", $"成品已验收，但报告无法保存：{exception.Message}")
                ]).ToArray(),
            };
        }
    }

    public ArtifactValidationReport ValidateEpub(string outputPath)
    {
        var issues = new List<ArtifactValidationIssue>();
        if (!File.Exists(outputPath))
            return ErrorReport(outputPath, "EPUB", "output_missing", "找不到生成的 EPUB 文件。", false);

        try
        {
            using var archive = ZipFile.OpenRead(outputPath);
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .ToDictionary(entry => NormalizeZipPath(entry.FullName), StringComparer.OrdinalIgnoreCase);
            if (!entries.TryGetValue("mimetype", out var mimetype)
                || ReadText(mimetype).Trim() != "application/epub+zip")
                AddError(issues, "epub_mimetype", "mimetype 缺失或内容不正确。");
            if (!entries.TryGetValue("META-INF/container.xml", out var container))
            {
                AddError(issues, "epub_container", "缺少 META-INF/container.xml。");
                return Build(outputPath, "EPUB", issues, false);
            }

            var containerXml = XDocument.Parse(ReadText(container));
            var rootfile = containerXml.Descendants().FirstOrDefault(element => element.Name.LocalName == "rootfile")
                ?.Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(rootfile) || !entries.TryGetValue(NormalizeZipPath(rootfile), out var opfEntry))
            {
                AddError(issues, "epub_opf", "container.xml 指向的 OPF 文件不存在。");
                return Build(outputPath, "EPUB", issues, false);
            }

            var opf = XDocument.Parse(ReadText(opfEntry));
            var opfDirectory = ZipDirectory(rootfile);
            var metadata = opf.Descendants().Where(element => element.Parent?.Name.LocalName == "metadata").ToArray();
            if (!metadata.Any(element => element.Name.LocalName == "title" && !string.IsNullOrWhiteSpace(element.Value)))
                AddError(issues, "metadata_title", "书名元数据缺失。");
            if (!metadata.Any(element => element.Name.LocalName == "language" && !string.IsNullOrWhiteSpace(element.Value)))
                AddError(issues, "metadata_language", "语言元数据缺失。");

            var manifest = opf.Descendants().Where(element => element.Name.LocalName == "item")
                .Select(element => new
                {
                    Id = element.Attribute("id")?.Value,
                    Href = element.Attribute("href")?.Value,
                    MediaType = element.Attribute("media-type")?.Value,
                }).Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href)).ToArray();
            var manifestIds = manifest.Select(item => item.Id!).ToHashSet(StringComparer.Ordinal);
            foreach (var item in manifest)
            {
                var path = ResolveZipPath(opfDirectory, item.Href!);
                if (!entries.ContainsKey(path)) AddError(issues, "manifest_missing", $"清单资源不存在：{item.Href}");
            }
            foreach (var itemref in opf.Descendants().Where(element => element.Name.LocalName == "itemref"))
            {
                var idref = itemref.Attribute("idref")?.Value;
                if (!string.IsNullOrWhiteSpace(idref) && !manifestIds.Contains(idref))
                    AddError(issues, "spine_missing", $"阅读顺序引用了不存在的资源：{idref}");
            }

            var htmlItems = manifest.Where(item => item.MediaType is "application/xhtml+xml" or "text/html").ToArray();
            foreach (var item in htmlItems)
            {
                var path = ResolveZipPath(opfDirectory, item.Href!);
                if (!entries.TryGetValue(path, out var htmlEntry)) continue;
                ValidateDocumentLinks(entries, path, ReadText(htmlEntry), issues);
            }
            if (!manifest.Any(item => item.MediaType == "application/x-dtbncx+xml")
                && !manifest.Any(item => string.Equals(item.Id, "nav", StringComparison.OrdinalIgnoreCase)))
                AddWarning(issues, "toc_missing", "未发现 EPUB 目录资源。");
            if (manifest.Any(item => item.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true))
                issues.Add(new ArtifactValidationIssue(ArtifactValidationSeverity.Information, "images_ok", "图片清单及文件引用已检查。"));
            if (manifest.Any(item => item.MediaType?.Contains("font", StringComparison.OrdinalIgnoreCase) == true
                                  || item.Href!.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                                  || item.Href.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)))
                issues.Add(new ArtifactValidationIssue(ArtifactValidationSeverity.Information, "fonts_ok", "嵌入字体清单及文件引用已检查。"));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or System.Xml.XmlException)
        {
            AddError(issues, "epub_unreadable", $"EPUB 无法完整读取：{exception.Message}");
        }
        return Build(outputPath, "EPUB", issues, false);
    }

    private static async Task<ArtifactValidationReport> ValidateMobiAsync(
        ConversionRequest request,
        CancellationToken cancellationToken)
    {
        var issues = new List<ArtifactValidationIssue>();
        if (!File.Exists(request.OutputPath))
            return ErrorReport(request.OutputPath, "MOBI", "output_missing", "找不到生成的 MOBI 文件。", true);
        var bytes = await File.ReadAllBytesAsync(request.OutputPath, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < 92 || Encoding.ASCII.GetString(bytes, 60, Math.Min(8, bytes.Length - 60)) != "BOOKMOBI")
        {
            AddError(issues, "mobi_header", "文件不是有效的 BOOKMOBI 容器。");
            return Build(request.OutputPath, "MOBI", issues, true);
        }
        if (!LegacyMobiPostProcessor.HasValidJointStructure(bytes))
            AddError(issues, "mobi_joint", "联合 MOBI7 + KF8 结构或 KF8 边界无效。");
        else
            issues.Add(new ArtifactValidationIssue(ArtifactValidationSeverity.Information, "mobi_joint", "联合 MOBI7 + KF8 结构与 KF8 边界有效。"));

        var options = request.Options?.Mobi ?? new MobiOptions();
        var asin = ReadExth(bytes, 113);
        var contentType = ReadExth(bytes, 501);
        if (options.EnableReadingProgressSync)
        {
            if (string.IsNullOrWhiteSpace(asin)) AddError(issues, "asin_missing", "启用了阅读进度同步，但 EXTH 113 ASIN 缺失。");
            else if (!System.Text.RegularExpressions.Regex.IsMatch(asin, "^B00[A-Z0-9]{7}$")) AddWarning(issues, "asin_format", $"ASIN 格式非常规：{asin}");
            if (!string.Equals(contentType, "EBOK", StringComparison.Ordinal)) AddError(issues, "ebok_missing", "EXTH 501 未标记为 EBOK。");
        }
        if (options.StripSourceArchive && bytes.AsSpan().IndexOf("SRCS"u8) >= 0)
            AddWarning(issues, "srcs_present", "MOBI 中仍存在 KindleGen 源文件归档 SRCS。");

        var coverOffset = ReadExthUInt32(bytes, 201);
        if (!string.IsNullOrWhiteSpace(request.Options?.CoverImagePath) && coverOffset is null)
            AddError(issues, "cover_missing", "配置了封面，但 MOBI 元数据中没有封面记录。");
        else if (coverOffset is not null)
            issues.Add(new ArtifactValidationIssue(ArtifactValidationSeverity.Information, "cover_present", "MOBI 封面记录存在。"));

        issues.Add(new ArtifactValidationIssue(
            ArtifactValidationSeverity.Information,
            "hardware_unconfirmed",
            "结构检查不能代替 Kindle 真机确认；首次使用新设置时仍建议传入设备打开一次。"));
        return Build(request.OutputPath, "MOBI", issues, true);
    }

    private static void ValidateDocumentLinks(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string documentPath,
        string html,
        ICollection<ArtifactValidationIssue> issues)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                     html, "(?:href|src)\\s*=\\s*[\\\"'](?<path>[^\\\"'#]+)",
                     System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            var target = match.Groups["path"].Value;
            if (target.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || target.Contains("://", StringComparison.Ordinal)) continue;
            var resolved = ResolveZipPath(ZipDirectory(documentPath), target);
            if (!entries.ContainsKey(resolved)) AddError(issues, "link_missing", $"{documentPath} 引用了不存在的资源：{target}");
        }
    }

    private static string? ReadExth(byte[] bytes, uint requestedType)
    {
        var value = ReadExthBytes(bytes, requestedType);
        return value is null ? null : Encoding.UTF8.GetString(value).TrimEnd('\0');
    }

    private static uint? ReadExthUInt32(byte[] bytes, uint requestedType)
    {
        var value = ReadExthBytes(bytes, requestedType);
        return value is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32BigEndian(value) : null;
    }

    private static byte[]? ReadExthBytes(byte[] bytes, uint requestedType)
    {
        try
        {
            var record0 = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(78, 4)));
            var headerLength = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record0 + 20, 4)));
            var exth = record0 + 16 + headerLength;
            if (Encoding.ASCII.GetString(bytes, exth, 4) != "EXTH") return null;
            var count = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(exth + 8, 4)));
            var cursor = exth + 12;
            for (var index = 0; index < count; index++)
            {
                var type = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor, 4));
                var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + 4, 4)));
                if (length < 8 || cursor + length > bytes.Length) return null;
                if (type == requestedType) return bytes[(cursor + 8)..(cursor + length)];
                cursor += length;
            }
        }
        catch (Exception) when (bytes.Length > 0) { }
        return null;
    }

    private static async Task SaveReportAsync(ArtifactValidationReport report, string path, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    private static ArtifactValidationReport Build(string path, string format, IReadOnlyList<ArtifactValidationIssue> issues, bool hardware) =>
        new(Path.GetFullPath(path), format, issues.ToArray(), DateTimeOffset.Now, hardware);

    private static ArtifactValidationReport ErrorReport(string path, string format, string code, string message, bool hardware) =>
        Build(path, format, [new ArtifactValidationIssue(ArtifactValidationSeverity.Error, code, message)], hardware);

    private static void AddError(ICollection<ArtifactValidationIssue> issues, string code, string message) =>
        issues.Add(new ArtifactValidationIssue(ArtifactValidationSeverity.Error, code, message));
    private static void AddWarning(ICollection<ArtifactValidationIssue> issues, string code, string message) =>
        issues.Add(new ArtifactValidationIssue(ArtifactValidationSeverity.Warning, code, message));
    private static string ReadText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
    private static string NormalizeZipPath(string path) => path.Replace('\\', '/').TrimStart('/');
    private static string ZipDirectory(string path)
    {
        var normalized = NormalizeZipPath(path);
        var slash = normalized.LastIndexOf('/');
        return slash < 0 ? string.Empty : normalized[..slash];
    }
    private static string ResolveZipPath(string directory, string relative)
    {
        var stack = new List<string>();
        foreach (var part in (string.IsNullOrEmpty(directory) ? relative : directory + "/" + relative)
                     .Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(Uri.UnescapeDataString(part.Split('#')[0]));
        }
        return string.Join('/', stack);
    }
}
