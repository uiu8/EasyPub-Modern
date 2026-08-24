using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EasyPub.Core;

public sealed record EpubInspectionResult(
    string? Title,
    string? Author,
    string? Language,
    int SpineDocumentCount,
    bool IsFixedLayout,
    bool HasUnsupportedEncryption);

public static class EpubInspectionService
{
    public static EpubInspectionResult Inspect(string epubPath)
    {
        using var package = EpubPackage.Open(epubPath);
        return new EpubInspectionResult(
            package.Title,
            package.Author,
            package.Metadata.Language,
            package.Spine.Count,
            package.IsFixedLayout,
            package.HasUnsupportedEncryption);
    }
}

internal sealed class ImportedEpubBook : IDisposable
{
    public required string WorkingDirectory { get; init; }
    public required string TextPath { get; init; }
    public required string? Title { get; init; }
    public required string? Author { get; init; }
    public required PublicationMetadata Metadata { get; init; }
    public required string? CoverImagePath { get; init; }
    public required IReadOnlyList<BookIllustration> Illustrations { get; init; }
    public required ChapterTreePlan ChapterTree { get; init; }

    public void Dispose()
    {
        try { if (Directory.Exists(WorkingDirectory)) Directory.Delete(WorkingDirectory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal static class EpubCompatibilityImporter
{
    private static readonly HashSet<string> TextBlockNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "li", "blockquote", "pre", "h1", "h2", "h3", "h4", "h5", "h6",
    };

    public static async Task<ImportedEpubBook> ImportAsync(
        string epubPath,
        CancellationToken cancellationToken)
    {
        using var package = EpubPackage.Open(epubPath);
        if (package.HasUnsupportedEncryption)
            throw new InvalidDataException("该 EPUB 含 DRM 或不支持的加密资源，无法转换。");
        if (package.IsFixedLayout)
            throw new NotSupportedException("该 EPUB 是固定版式电子书。请改用“保留原 EPUB 版式”模式。");

        var workingDirectory = Path.Combine(Path.GetTempPath(), "EasyPubModernEpubImport", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var bodyLines = new List<string>();
            var chapters = new List<ChapterTreeEntry>();
            var illustrations = new List<BookIllustration>();
            var imageMarkers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var spineItem in package.Spine)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (spineItem.IsNavigation || spineItem.IsCoverDocument) continue;
                var document = package.LoadXml(spineItem.Path);
                var body = document.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
                if (body is null) continue;

                var title = package.NavigationTitles.TryGetValue(spineItem.Path, out var navigationTitle)
                    ? navigationTitle.Title
                    : FindDocumentTitle(document, spineItem.Path);
                var level = package.NavigationTitles.TryGetValue(spineItem.Path, out navigationTitle)
                    ? navigationTitle.Level
                    : 2;
                var startLine = bodyLines.Count + 1;
                var skippedFirstMatchingHeading = false;

                foreach (var element in body.Descendants().Where(element => TextBlockNames.Contains(element.Name.LocalName)))
                {
                    if (element.Ancestors().Any(ancestor => TextBlockNames.Contains(ancestor.Name.LocalName))) continue;
                    foreach (var image in element.DescendantsAndSelf().Where(node =>
                                 node.Name.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase)))
                    {
                        var marker = ExtractImage(package, spineItem.Path, image, workingDirectory, imageMarkers, illustrations);
                        if (marker is not null) bodyLines.Add($"[[插图:{marker}]]");
                    }

                    var text = NormalizeText(string.Concat(element.DescendantNodes().OfType<XText>().Select(node => node.Value)));
                    if (text.Length == 0) continue;
                    var isHeading = element.Name.LocalName.StartsWith('h') && element.Name.LocalName.Length == 2;
                    if (!skippedFirstMatchingHeading && isHeading && string.Equals(text, title, StringComparison.OrdinalIgnoreCase))
                    {
                        skippedFirstMatchingHeading = true;
                        continue;
                    }
                    bodyLines.Add(text);
                }

                foreach (var image in body.Descendants().Where(node =>
                             node.Name.LocalName.Equals("img", StringComparison.OrdinalIgnoreCase)
                             && !node.Ancestors().Any(ancestor => TextBlockNames.Contains(ancestor.Name.LocalName))))
                {
                    var marker = ExtractImage(package, spineItem.Path, image, workingDirectory, imageMarkers, illustrations);
                    if (marker is not null) bodyLines.Add($"[[插图:{marker}]]");
                }

                var endLine = bodyLines.Count;
                chapters.Add(new ChapterTreeEntry(
                    Guid.NewGuid().ToString("N"),
                    title,
                    Math.Clamp(level, 1, 4),
                    true,
                    null,
                    startLine <= endLine ? [new ChapterSourceRange(startLine, endLine)] : [])
                {
                    HeadingLevel = Math.Clamp(level, 1, 4),
                });
            }

            if (chapters.Count == 0)
                throw new InvalidDataException("EPUB 的阅读顺序中没有可导入的正文页面。");

            var textPath = Path.Combine(workingDirectory, "imported.txt");
            var textBytes = new UTF8Encoding(false).GetBytes(string.Join("\n", bodyLines));
            await File.WriteAllBytesAsync(textPath, textBytes, cancellationToken).ConfigureAwait(false);
            var plan = new ChapterTreePlan(
                Convert.ToHexString(SHA256.HashData(textBytes)),
                ChapterTreeDocument.NormalizeHierarchyLevels(chapters));
            ChapterTreeDocument.ValidatePlan(plan, bodyLines.Count);

            string? coverPath = null;
            if (package.CoverImagePath is not null)
            {
                var extension = ExtensionFor(package.CoverImagePath, package.GetMediaType(package.CoverImagePath));
                coverPath = Path.Combine(workingDirectory, "cover" + extension);
                await File.WriteAllBytesAsync(coverPath, package.ReadBytes(package.CoverImagePath), cancellationToken).ConfigureAwait(false);
            }

            return new ImportedEpubBook
            {
                WorkingDirectory = workingDirectory,
                TextPath = textPath,
                Title = package.Title,
                Author = package.Author,
                Metadata = package.Metadata,
                CoverImagePath = coverPath,
                Illustrations = illustrations,
                ChapterTree = plan,
            };
        }
        catch
        {
            try { Directory.Delete(workingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static string? ExtractImage(
        EpubPackage package,
        string documentPath,
        XElement image,
        string workingDirectory,
        IDictionary<string, string> imageMarkers,
        ICollection<BookIllustration> illustrations)
    {
        var source = image.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("src", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(source) || source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var imagePath = EpubPackage.ResolvePath(documentPath, source);
        if (!package.Exists(imagePath)) return null;
        var mediaType = package.GetMediaType(imagePath);
        if (mediaType is not ("image/jpeg" or "image/png" or "image/webp" or "image/gif")) return null;
        if (imageMarkers.TryGetValue(imagePath, out var existingMarker)) return existingMarker;

        var marker = $"epub-import-{illustrations.Count + 1:000}";
        var extractedPath = Path.Combine(workingDirectory, marker + ExtensionFor(imagePath, mediaType));
        File.WriteAllBytes(extractedPath, package.ReadBytes(imagePath));
        var alt = image.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals("alt", StringComparison.OrdinalIgnoreCase))?.Value;
        illustrations.Add(new BookIllustration(marker, extractedPath, string.IsNullOrWhiteSpace(alt) ? marker : alt.Trim()));
        imageMarkers.Add(imagePath, marker);
        return marker;
    }

    private static string FindDocumentTitle(XDocument document, string path)
    {
        var heading = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName is "h1" or "h2" or "h3" or "h4" or "h5" or "h6");
        var value = heading is null ? null : NormalizeText(heading.Value);
        if (string.IsNullOrWhiteSpace(value))
            value = NormalizeText(document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty);
        return string.IsNullOrWhiteSpace(value) ? Path.GetFileNameWithoutExtension(path) : value;
    }

    private static string NormalizeText(string value) => Regex.Replace(value, @"\s+", " ").Trim();

    private static string ExtensionFor(string path, string? mediaType)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif") return extension;
        return mediaType switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".jpg",
        };
    }
}

internal sealed class EpubPackage : IDisposable
{
    private static readonly HashSet<string> AllowedEncryptionAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://www.idpf.org/2008/embedding",
        "http://ns.adobe.com/pdf/enc#RC",
    };

    private readonly FileStream _stream;
    private readonly ZipArchive _archive;
    private readonly IReadOnlyDictionary<string, ZipArchiveEntry> _entries;
    private readonly IReadOnlyDictionary<string, string> _mediaTypes;

    private EpubPackage(
        FileStream stream,
        ZipArchive archive,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, string> mediaTypes)
    {
        _stream = stream;
        _archive = archive;
        _entries = entries;
        _mediaTypes = mediaTypes;
    }

    public string? Title { get; private init; }
    public string? Author { get; private init; }
    public PublicationMetadata Metadata { get; private init; } = new();
    public IReadOnlyList<EpubSpineItem> Spine { get; private init; } = [];
    public IReadOnlyDictionary<string, EpubNavigationTitle> NavigationTitles { get; private init; } =
        new Dictionary<string, EpubNavigationTitle>();
    public string? CoverImagePath { get; private init; }
    public bool IsFixedLayout { get; private init; }
    public bool HasUnsupportedEncryption { get; private init; }

    public static EpubPackage Open(string epubPath)
    {
        var fullPath = Path.GetFullPath(epubPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到 EPUB 文件。", fullPath);
        var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var entries = archive.Entries
                .Where(entry => !string.IsNullOrEmpty(entry.Name))
                .ToDictionary(entry => NormalizeArchivePath(entry.FullName), StringComparer.OrdinalIgnoreCase);
            if (!entries.TryGetValue("META-INF/container.xml", out var containerEntry))
                throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");
            var container = LoadXml(containerEntry);
            var opfPath = container.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("rootfile", StringComparison.OrdinalIgnoreCase))?
                .Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "full-path")?.Value;
            if (string.IsNullOrWhiteSpace(opfPath)) throw new InvalidDataException("EPUB 未声明 OPF 包文件。");
            opfPath = NormalizeArchivePath(opfPath);
            if (!entries.TryGetValue(opfPath, out var opfEntry)) throw new InvalidDataException("EPUB 的 OPF 包文件不存在。");
            var opf = LoadXml(opfEntry);
            var opfDirectory = GetDirectory(opfPath);

            var manifestById = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
            var mediaTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in opf.Descendants().Where(element => element.Name.LocalName == "item"))
            {
                var id = Attribute(item, "id");
                var href = Attribute(item, "href");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href)) continue;
                var path = ResolvePath(opfPath, href);
                var manifest = new ManifestItem(id, path, Attribute(item, "media-type") ?? string.Empty, Attribute(item, "properties") ?? string.Empty);
                manifestById[id] = manifest;
                mediaTypes[path] = manifest.MediaType;
            }

            var spineElement = opf.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine");
            var spine = new List<EpubSpineItem>();
            if (spineElement is not null)
            {
                foreach (var itemRef in spineElement.Elements().Where(element => element.Name.LocalName == "itemref"))
                {
                    var idref = Attribute(itemRef, "idref");
                    if (idref is null || !manifestById.TryGetValue(idref, out var item)) continue;
                    spine.Add(new EpubSpineItem(
                        item.Path,
                        item.MediaType,
                        item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav", StringComparer.OrdinalIgnoreCase),
                        false));
                }
            }

            var coverDocumentPaths = opf.Descendants().Where(element => element.Name.LocalName == "reference")
                .Where(element => string.Equals(Attribute(element, "type"), "cover", StringComparison.OrdinalIgnoreCase))
                .Select(element => Attribute(element, "href"))
                .Where(href => !string.IsNullOrWhiteSpace(href))
                .Select(href => ResolvePath(opfPath, href!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var navigationDocumentPaths = opf.Descendants().Where(element => element.Name.LocalName == "reference")
                .Where(element => string.Equals(Attribute(element, "type"), "toc", StringComparison.OrdinalIgnoreCase))
                .Select(element => Attribute(element, "href"))
                .Where(href => !string.IsNullOrWhiteSpace(href))
                .Select(href => ResolvePath(opfPath, href!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            spine = spine.Select(item => item with
            {
                IsCoverDocument = coverDocumentPaths.Contains(item.Path),
                IsNavigation = item.IsNavigation || navigationDocumentPaths.Contains(item.Path),
            }).ToList();

            var navItem = manifestById.Values.FirstOrDefault(item =>
                item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav", StringComparer.OrdinalIgnoreCase));
            var navigation = navItem is null
                ? ReadNcxNavigation(entries, manifestById, spineElement, opfPath)
                : ReadHtmlNavigation(entries, navItem.Path);

            var coverImage = manifestById.Values.FirstOrDefault(item =>
                item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("cover-image", StringComparer.OrdinalIgnoreCase));
            if (coverImage is null)
            {
                var coverId = opf.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName == "meta"
                    && string.Equals(Attribute(element, "name"), "cover", StringComparison.OrdinalIgnoreCase));
                var content = coverId is null ? null : Attribute(coverId, "content");
                if (content is not null) manifestById.TryGetValue(content, out coverImage);
            }

            var metadataElement = opf.Descendants().FirstOrDefault(element => element.Name.LocalName == "metadata");
            string? MetadataValue(string name) => metadataElement?.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();
            var dateValue = MetadataValue("date");
            DateOnly? date = null;
            if (!string.IsNullOrWhiteSpace(dateValue) && dateValue.Length >= 10
                && DateOnly.TryParse(dateValue[..10], out var parsedDate))
                date = parsedDate;
            var identifiers = metadataElement?.Descendants().Where(element => element.Name.LocalName == "identifier").ToArray() ?? [];
            var isbn = identifiers.FirstOrDefault(element =>
                element.Value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().All(char.IsDigit)
                && element.Value.Replace("-", string.Empty, StringComparison.Ordinal).Trim().Length is 10 or 13)?.Value.Trim();
            var layout = opf.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "meta" && Attribute(element, "property") == "rendition:layout")?.Value.Trim();
            var unsupportedEncryption = false;
            if (entries.TryGetValue("META-INF/encryption.xml", out var encryptionEntry))
            {
                var encryption = LoadXml(encryptionEntry);
                unsupportedEncryption = encryption.Descendants().Where(element => element.Name.LocalName == "EncryptionMethod")
                    .Select(element => Attribute(element, "Algorithm"))
                    .Any(algorithm => !string.IsNullOrWhiteSpace(algorithm) && !AllowedEncryptionAlgorithms.Contains(algorithm));
            }

            return new EpubPackage(stream, archive, entries, mediaTypes)
            {
                Title = MetadataValue("title"),
                Author = MetadataValue("creator"),
                Metadata = new PublicationMetadata
                {
                    Isbn = isbn,
                    PublicationDate = date,
                    Publisher = MetadataValue("publisher"),
                    Category = MetadataValue("subject"),
                    Language = MetadataValue("language") ?? "zh-CN",
                    Description = MetadataValue("description"),
                },
                Spine = spine,
                NavigationTitles = navigation,
                CoverImagePath = coverImage?.Path,
                IsFixedLayout = string.Equals(layout, "pre-paginated", StringComparison.OrdinalIgnoreCase),
                HasUnsupportedEncryption = unsupportedEncryption,
            };
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public XDocument LoadXml(string path) => LoadXml(GetEntry(path));
    public byte[] ReadBytes(string path)
    {
        using var input = GetEntry(path).Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
    public bool Exists(string path) => _entries.ContainsKey(NormalizeArchivePath(path));
    public string? GetMediaType(string path) => _mediaTypes.TryGetValue(NormalizeArchivePath(path), out var value) ? value : null;

    public static string ResolvePath(string baseDocumentPath, string href)
    {
        var cleanHref = Uri.UnescapeDataString(href.Split('#', 2)[0].Split('?', 2)[0]).Replace('\\', '/');
        var combined = cleanHref.StartsWith('/')
            ? cleanHref.TrimStart('/')
            : GetDirectory(baseDocumentPath) + cleanHref;
        return NormalizeArchivePath(combined);
    }

    public void Dispose()
    {
        _archive.Dispose();
        _stream.Dispose();
    }

    private ZipArchiveEntry GetEntry(string path) => _entries.TryGetValue(NormalizeArchivePath(path), out var entry)
        ? entry
        : throw new InvalidDataException($"EPUB 资源不存在：{path}");

    private static IReadOnlyDictionary<string, EpubNavigationTitle> ReadHtmlNavigation(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string navPath)
    {
        var result = new Dictionary<string, EpubNavigationTitle>(StringComparer.OrdinalIgnoreCase);
        if (!entries.TryGetValue(navPath, out var entry)) return result;
        var document = LoadXml(entry);
        var nav = document.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "nav" && element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "type" && attribute.Value.Split(' ').Contains("toc")));
        nav ??= document.Descendants().FirstOrDefault(element => element.Name.LocalName == "nav");
        if (nav is null) return result;
        var rootList = nav.Elements().FirstOrDefault(element => element.Name.LocalName is "ol" or "ul");
        if (rootList is not null) AddHtmlNavigationLevel(rootList, navPath, 1, result);
        return result;
    }

    private static void AddHtmlNavigationLevel(
        XElement list,
        string navPath,
        int level,
        IDictionary<string, EpubNavigationTitle> result)
    {
        foreach (var item in list.Elements().Where(element => element.Name.LocalName == "li"))
        {
            var link = item.Descendants().FirstOrDefault(element => element.Name.LocalName == "a");
            var href = link is null ? null : Attribute(link, "href");
            if (!string.IsNullOrWhiteSpace(href))
            {
                var path = ResolvePath(navPath, href);
                result.TryAdd(path, new EpubNavigationTitle(Regex.Replace(link!.Value, @"\s+", " ").Trim(), Math.Clamp(level, 1, 4)));
            }
            var childList = item.Elements().FirstOrDefault(element => element.Name.LocalName is "ol" or "ul");
            if (childList is not null) AddHtmlNavigationLevel(childList, navPath, level + 1, result);
        }
    }

    private static IReadOnlyDictionary<string, EpubNavigationTitle> ReadNcxNavigation(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        IReadOnlyDictionary<string, ManifestItem> manifest,
        XElement? spine,
        string opfPath)
    {
        var result = new Dictionary<string, EpubNavigationTitle>(StringComparer.OrdinalIgnoreCase);
        var tocId = spine is null ? null : Attribute(spine, "toc");
        var ncx = tocId is not null && manifest.TryGetValue(tocId, out var byId)
            ? byId
            : manifest.Values.FirstOrDefault(item => item.MediaType == "application/x-dtbncx+xml");
        if (ncx is null || !entries.TryGetValue(ncx.Path, out var entry)) return result;
        var document = LoadXml(entry);
        foreach (var node in document.Descendants().Where(element => element.Name.LocalName == "navPoint"))
        {
            var content = node.Elements().FirstOrDefault(element => element.Name.LocalName == "content");
            var source = content is null ? null : Attribute(content, "src");
            if (string.IsNullOrWhiteSpace(source)) continue;
            var title = node.Descendants().FirstOrDefault(element => element.Name.LocalName == "text")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;
            var level = node.Ancestors().Count(element => element.Name.LocalName == "navPoint") + 1;
            result.TryAdd(ResolvePath(ncx.Path, source), new EpubNavigationTitle(title, Math.Clamp(level, 1, 4)));
        }
        return result;
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string NormalizeArchivePath(string path)
    {
        var parts = new List<string>();
        foreach (var part in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) throw new InvalidDataException("EPUB 资源路径越过了压缩包根目录。");
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private static string GetDirectory(string path)
    {
        var normalized = path.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..(separator + 1)];
    }

    private static string? Attribute(XElement element, string name) => element.Attributes()
        .FirstOrDefault(attribute => attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private sealed record ManifestItem(string Id, string Path, string MediaType, string Properties);
}

internal sealed record EpubSpineItem(string Path, string MediaType, bool IsNavigation, bool IsCoverDocument);
internal sealed record EpubNavigationTitle(string Title, int Level);
