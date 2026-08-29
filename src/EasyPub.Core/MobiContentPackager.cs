using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace EasyPub.Core;

internal sealed record MobiContentPackingResult(
    int LogicalChapterCount,
    int PhysicalDocumentCount,
    int MaximumChaptersPerDocument,
    long TargetDocumentBytes)
{
    public bool WasOptimized => PhysicalDocumentCount < LogicalChapterCount;
}

internal static partial class MobiContentPackager
{
    internal const int MaximumChaptersPerDocument = 10;
    internal const long TargetDocumentBytes = 192 * 1024;
    private static readonly UTF8Encoding Utf8WithBom = new(true);
    private static readonly XNamespace XhtmlNamespace = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace OpfNamespace = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace NcxNamespace = "http://www.daisy.org/z3986/2005/ncx/";

    public static MobiContentPackingResult Optimize(string oebpsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oebpsDirectory);
        var chapters = Directory.EnumerateFiles(oebpsDirectory, "chapter*.html")
            .Select(path => (Path: path, Match: ChapterFileName().Match(Path.GetFileName(path))))
            .Where(item => item.Match.Success && int.Parse(item.Match.Groups[1].Value, CultureInfo.InvariantCulture) > 0)
            .Select(item => new ChapterFile(
                int.Parse(item.Match.Groups[1].Value, CultureInfo.InvariantCulture),
                item.Path,
                new FileInfo(item.Path).Length))
            .OrderBy(item => item.Index)
            .ToArray();
        if (chapters.Length < 2)
            return new MobiContentPackingResult(chapters.Length, chapters.Length, MaximumChaptersPerDocument, TargetDocumentBytes);

        var groups = BuildGroups(chapters);
        if (groups.Count >= chapters.Length)
            return new MobiContentPackingResult(chapters.Length, chapters.Length, MaximumChaptersPerDocument, TargetDocumentBytes);

        var mappings = new Dictionary<string, string>(StringComparer.Ordinal);
        var groupFiles = new List<GroupFile>(groups.Count);
        for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            var group = groups[groupIndex];
            var fileName = $"chapter-pack-{groupIndex + 1:0000}.html";
            var outputPath = Path.Combine(oebpsDirectory, fileName);
            WriteGroup(outputPath, group);
            groupFiles.Add(new GroupFile(groupIndex + 1, fileName, group));
            foreach (var chapter in group)
                mappings[$"chapter{chapter.Index}.html"] = $"{fileName}#chapter{chapter.Index}";
        }

        RewriteNcx(Path.Combine(oebpsDirectory, "toc.ncx"), mappings);
        RewriteHtmlToc(Path.Combine(oebpsDirectory, "book-toc.html"), mappings);
        RewriteOpf(Path.Combine(oebpsDirectory, "content.opf"), chapters, groupFiles);
        foreach (var chapter in chapters) File.Delete(chapter.Path);

        return new MobiContentPackingResult(
            chapters.Length,
            groupFiles.Count,
            groups.Max(group => group.Count),
            TargetDocumentBytes);
    }

    private static IReadOnlyList<IReadOnlyList<ChapterFile>> BuildGroups(IReadOnlyList<ChapterFile> chapters)
    {
        var groups = new List<IReadOnlyList<ChapterFile>>();
        var current = new List<ChapterFile>(MaximumChaptersPerDocument);
        long currentBytes = 0;
        foreach (var chapter in chapters)
        {
            var exceedsTarget = current.Count > 0 && currentBytes + chapter.Bytes > TargetDocumentBytes;
            if (current.Count >= MaximumChaptersPerDocument || exceedsTarget)
            {
                groups.Add(current.ToArray());
                current.Clear();
                currentBytes = 0;
            }
            current.Add(chapter);
            currentBytes += chapter.Bytes;
        }
        if (current.Count > 0) groups.Add(current.ToArray());
        return groups;
    }

    private static void WriteGroup(string outputPath, IReadOnlyList<ChapterFile> chapters)
    {
        var output = LoadXml(chapters[0].Path);
        var body = output.Root?.Element(XhtmlNamespace + "body")
            ?? throw new InvalidDataException($"章节缺少 XHTML body：{chapters[0].Path}");
        body.RemoveNodes();
        body.Add(
            new XElement(XhtmlNamespace + "a", new XAttribute("id", "section")),
            new XElement(XhtmlNamespace + "a"),
            new XElement(XhtmlNamespace + "a", new XAttribute("id", "article")),
            new XElement(XhtmlNamespace + "a"));

        for (var chapterOffset = 0; chapterOffset < chapters.Count; chapterOffset++)
        {
            var chapter = chapters[chapterOffset];
            var source = LoadXml(chapter.Path);
            var sourceBody = source.Root?.Element(XhtmlNamespace + "body")
                ?? throw new InvalidDataException($"章节缺少 XHTML body：{chapter.Path}");
            var wrapper = new XElement(
                XhtmlNamespace + "div",
                new XAttribute("id", $"chapter{chapter.Index}"),
                new XAttribute("class", "easypub-packed-chapter"));
            if (chapterOffset > 0)
                wrapper.Add(new XAttribute("style", "page-break-before: always;"));
            foreach (var node in sourceBody.Nodes()) wrapper.Add(CloneNode(node));
            MakeIdentifiersUnique(wrapper, chapter.Index);
            body.Add(wrapper);
        }

        WriteXml(outputPath, output);
    }

    private static void MakeIdentifiersUnique(XElement wrapper, int chapterIndex)
    {
        var identifiers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in wrapper.Descendants())
        {
            var id = element.Attribute("id");
            if (id is null || string.IsNullOrEmpty(id.Value)) continue;
            var unique = $"chapter{chapterIndex}-{id.Value}";
            identifiers[id.Value] = unique;
            id.Value = unique;
        }
        foreach (var element in wrapper.Descendants())
        {
            var href = element.Attribute("href");
            if (href is null || !href.Value.StartsWith('#')) continue;
            var identifier = href.Value[1..];
            if (identifiers.TryGetValue(identifier, out var unique)) href.Value = "#" + unique;
        }
    }

    private static void RewriteNcx(string path, IReadOnlyDictionary<string, string> mappings)
    {
        var document = LoadXml(path);
        foreach (var content in document.Descendants(NcxNamespace + "content"))
        {
            var source = content.Attribute("src");
            if (source is not null && mappings.TryGetValue(source.Value, out var target)) source.Value = target;
        }
        WriteXml(path, document);
    }

    private static void RewriteHtmlToc(string path, IReadOnlyDictionary<string, string> mappings)
    {
        if (!File.Exists(path)) return;
        var document = LoadXml(path);
        foreach (var link in document.Descendants(XhtmlNamespace + "a"))
        {
            var href = link.Attribute("href");
            if (href is not null && mappings.TryGetValue(href.Value, out var target)) href.Value = target;
        }
        WriteXml(path, document);
    }

    private static void RewriteOpf(
        string path,
        IReadOnlyList<ChapterFile> chapters,
        IReadOnlyList<GroupFile> groups)
    {
        var document = LoadXml(path);
        var manifest = document.Root?.Element(OpfNamespace + "manifest")
            ?? throw new InvalidDataException("MOBI OPF 缺少 manifest。");
        var spine = document.Root?.Element(OpfNamespace + "spine")
            ?? throw new InvalidDataException("MOBI OPF 缺少 spine。");
        var chapterIds = chapters.Select(chapter => $"chapter{chapter.Index}").ToHashSet(StringComparer.Ordinal);

        manifest.Elements(OpfNamespace + "item")
            .Where(item => chapterIds.Contains((string?)item.Attribute("id") ?? string.Empty))
            .Remove();
        spine.Elements(OpfNamespace + "itemref")
            .Where(item => chapterIds.Contains((string?)item.Attribute("idref") ?? string.Empty))
            .Remove();
        foreach (var group in groups)
        {
            var id = $"chapter-pack-{group.Index:0000}";
            manifest.Add(new XElement(
                OpfNamespace + "item",
                new XAttribute("id", id),
                new XAttribute("href", group.FileName),
                new XAttribute("media-type", "application/xhtml+xml")));
            spine.Add(new XElement(OpfNamespace + "itemref", new XAttribute("idref", id), new XAttribute("linear", "yes")));
        }
        WriteXml(path, document);
    }

    private static XDocument LoadXml(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static void WriteXml(string path, XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Encoding = Utf8WithBom,
            Indent = false,
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
        };
        using var writer = XmlWriter.Create(path, settings);
        document.Save(writer);
    }

    private static XNode CloneNode(XNode node) => node switch
    {
        XElement element => new XElement(element),
        XCData cdata => new XCData(cdata.Value),
        XText text => new XText(text.Value),
        XComment comment => new XComment(comment.Value),
        XProcessingInstruction instruction => new XProcessingInstruction(instruction.Target, instruction.Data),
        _ => throw new NotSupportedException($"不支持的 XHTML 节点：{node.NodeType}"),
    };

    [GeneratedRegex(@"^chapter(\d+)\.html$", RegexOptions.CultureInvariant)]
    private static partial Regex ChapterFileName();

    private sealed record ChapterFile(int Index, string Path, long Bytes);
    private sealed record GroupFile(int Index, string FileName, IReadOnlyList<ChapterFile> Chapters);
}
