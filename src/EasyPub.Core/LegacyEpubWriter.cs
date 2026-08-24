using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace EasyPub.Core;

internal static class LegacyEpubWriter
{
    private const string Generator = "EasyPub v1.50";
    private static readonly UTF8Encoding Utf8WithBom = new(true);
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    public static async Task<(int ChapterCount, long OutputBytes)> WriteAsync(
        ConversionRequest request,
        CancellationToken cancellationToken,
        IProgress<ConversionProgress>? progress = null)
    {
        var inputPath = Path.GetFullPath(request.InputPath);
        var outputPath = Path.GetFullPath(request.OutputPath);
        if (!File.Exists(inputPath)) throw new FileNotFoundException("Input file does not exist.", inputPath);
        if (!string.Equals(Path.GetExtension(inputPath), ".txt", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("EPUB 输出目前只接受 TXT 输入；EPUB 输入请选择 MOBI 输出。");
        if (!string.Equals(Path.GetExtension(outputPath), ".epub", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The compatibility core currently supports EPUB output only.");

        var title = string.IsNullOrWhiteSpace(request.Title)
            ? Path.GetFileNameWithoutExtension(inputPath)
            : request.Title.Trim();
        var author = request.Author?.Trim() ?? string.Empty;
        var options = request.Options ?? ConversionOptions.LegacyDefault;
        ValidateOptions(options);
        var metadata = options.Metadata;
        progress?.Report(new ConversionProgress(inputPath, 0.03, "正在读取并分析 TXT"));
        var chapters = await LegacyTextParser.ParseAsync(inputPath, options, request.ChapterTree, cancellationToken);
        progress?.Report(new ConversionProgress(inputPath, 0.14, $"已识别 {chapters.Count} 个章节"));
        var bookId = CreateLegacyBookId(title, author);
        var cover = string.IsNullOrWhiteSpace(options.CoverImagePath)
            ? null
            : await CoverImageConverter.PrepareJpegAsync(options.CoverImagePath, cancellationToken);
        var illustrations = await PrepareIllustrationsAsync(options.Illustrations, cancellationToken);
        var font = options.Font.Enabled
            ? await FontEmbeddingService.PrepareAsync(
                options.Font,
                string.Join("\n", chapters.SelectMany(chapter => chapter.Paragraphs.Prepend(chapter.Title))),
                cancellationToken)
            : null;

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, Utf8WithoutBom))
                {
                    var timestamp = DateTimeOffset.Now;
                    AddEntry(archive, "mimetype", Utf8WithoutBom.GetBytes("application/epub+zip"), CompressionLevel.NoCompression, timestamp);
                    AddEntry(archive, "META-INF/", [], CompressionLevel.NoCompression, timestamp);
                    AddEntry(archive, "META-INF/container.xml", Utf8WithoutBom.GetBytes(LegacyTemplates.Container), CompressionLevel.Optimal, timestamp);
                    AddEntry(archive, "OEBPS/", [], CompressionLevel.NoCompression, timestamp);
                    if (cover is not null)
                        AddEntry(archive, "OEBPS/cover.jpg", cover.JpegBytes, CompressionLevel.Optimal, timestamp);
                    foreach (var illustration in illustrations)
                        AddEntry(archive, "OEBPS/" + illustration.RelativePath, illustration.JpegBytes, CompressionLevel.Optimal, timestamp);
                    if (font is not null)
                        AddEntry(archive, "OEBPS/fonts/book.ttf", font.Bytes, CompressionLevel.Optimal, timestamp);
                    AddTextEntry(archive, "OEBPS/book-toc.html", BuildHtmlToc(chapters), timestamp, withBom: true);
                    for (var index = 0; index < chapters.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        AddTextEntry(archive, $"OEBPS/chapter{index}.html", BuildChapter(chapters[index], index, options, illustrations), timestamp, withBom: true);
                        progress?.Report(new ConversionProgress(
                            inputPath,
                            0.22 + 0.62 * (index + 1d) / Math.Max(1, chapters.Count),
                            $"正在生成章节 {index + 1}/{chapters.Count}"));
                    }
                    AddTextEntry(archive, "OEBPS/content.opf", BuildOpf(title, author, bookId, chapters.Count, cover is not null, illustrations, font is not null, metadata), timestamp, withBom: true);
                    AddTextEntry(archive, "OEBPS/cover.html", BuildCover(title, author, cover is not null), timestamp, withBom: true);
                    AddTextEntry(archive, "OEBPS/style.css", LegacyTemplates.CreateStyleCss(options), timestamp, withBom: false);
                    AddTextEntry(archive, "OEBPS/toc.ncx", BuildNcx(title, author, bookId, chapters), timestamp, withBom: true);
                }
                progress?.Report(new ConversionProgress(inputPath, 0.93, "正在写入电子书文件"));
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
            var bytes = new FileInfo(outputPath).Length;
            progress?.Report(new ConversionProgress(inputPath, 1, "转换完成"));
            return (chapters.Count, bytes);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void AddTextEntry(ZipArchive archive, string path, string content, DateTimeOffset timestamp, bool withBom)
    {
        var encoding = withBom ? Utf8WithBom : Utf8WithoutBom;
        var contentBytes = encoding.GetBytes(content);
        var preamble = encoding.GetPreamble();
        var body = new byte[preamble.Length + contentBytes.Length];
        preamble.CopyTo(body, 0);
        contentBytes.CopyTo(body, preamble.Length);
        AddEntry(archive, path, body, CompressionLevel.Optimal, timestamp);
    }

    private static void AddEntry(
        ZipArchive archive,
        string path,
        byte[] content,
        CompressionLevel compression,
        DateTimeOffset timestamp)
    {
        var entry = archive.CreateEntry(path, compression);
        entry.LastWriteTime = timestamp;
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static string BuildCover(string title, string author, bool hasImageCover)
    {
        var lines = LegacyTemplates.XhtmlHeader("Cover");
        lines.Add(hasImageCover ? "<div class=\"centeredimage\">" : "<div>");
        if (hasImageCover)
        {
            lines.Add($"<img class=\"attpic\" src=\"cover.jpg\" alt=\"{Html(title)}\"/>");
        }
        else
        {
            lines.Add($"<h1 class=\"booktitle\">{Html(title)}</h1>");
            if (!string.IsNullOrEmpty(author)) lines.Add($"<h3 class=\"bookauthor\">{Html(author)}</h3>");
        }
        lines.Add("</div>");
        lines.Add("</body>");
        lines.Add("</html>");
        return JoinLines(lines);
    }

    private static string BuildHtmlToc(IReadOnlyList<LegacyChapter> chapters)
    {
        var lines = LegacyTemplates.XhtmlHeader("Table Of Contents");
        lines.Add("<h2 class=\"titletoc\">");
        lines.Add("目录");
        lines.Add("</h2>");
        lines.Add("<div class=\"toc\">");
        lines.Add("<dl>");
        for (var index = 0; index < chapters.Count; index++)
        {
            if (!chapters[index].IncludeInToc) continue;
            var level = Math.Clamp(chapters[index].TocLevel, 1, 4);
            lines.Add($"<dt class=\"tocl{level}\"><a href=\"chapter{index}.html\">{Html(chapters[index].Title)}</a></dt>");
        }
        lines.Add("</dl>");
        lines.Add("</div>");
        lines.Add("</body>");
        lines.Add("</html>");
        return JoinLines(lines);
    }

    private static string BuildChapter(
        LegacyChapter chapter,
        int index,
        ConversionOptions options,
        IReadOnlyList<PreparedIllustration> illustrations)
    {
        var lines = LegacyTemplates.XhtmlHeader($"chapter {index} - 0");
        var level = Math.Clamp(chapter.HeadingLevel ?? chapter.TocLevel, 1, 4);
        var titleClass = chapter.Paragraphs.Count == 0 ? $"titlel{level}single" : $"titlel{level}std";
        lines.Add($"<h{level} id=\"title\" class=\"{titleClass}\">{Html(chapter.Title)}</h{level}>");
        var prefix = options.AddFullWidthIndent ? "　　" : string.Empty;
        var illustrationByMarker = illustrations.ToDictionary(
            illustration => illustration.Marker,
            StringComparer.OrdinalIgnoreCase);
        foreach (var paragraph in chapter.Paragraphs)
        {
            if (TryGetPositionedIllustrationMarker(paragraph, out var positionedMarker) &&
                illustrationByMarker.TryGetValue(positionedMarker, out var positionedIllustration))
            {
                lines.Add($"<div class=\"illustration\"><img class=\"body-illustration\" src=\"{positionedIllustration.RelativePath}\" alt=\"{Html(positionedIllustration.AltText)}\"/></div>");
                continue;
            }

            if (TryGetIllustrationMarker(paragraph, out var marker) &&
                illustrationByMarker.TryGetValue(marker, out var illustration))
            {
                if (illustration.InsertAfterLine is null)
                    lines.Add($"<div class=\"illustration\"><img class=\"body-illustration\" src=\"{illustration.RelativePath}\" alt=\"{Html(illustration.AltText)}\"/></div>");
                continue;
            }

            lines.Add(paragraph.Length == 0
                ? "<p class=\"a\"><br /></p>"
                : $"<p class=\"a\">{prefix}{Html(paragraph)}</p>");
        }
        lines.Add("</body>");
        lines.Add("</html>");
        return JoinLines(lines);
    }

    private static string BuildOpf(
        string title,
        string author,
        string bookId,
        int chapterCount,
        bool hasImageCover,
        IReadOnlyList<PreparedIllustration> illustrations,
        bool hasEmbeddedFont,
        PublicationMetadata metadata)
    {
        var lines = new List<string>
        {
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"no\"?>", "",
            "<package version=\"2.0\" xmlns=\"http://www.idpf.org/2007/opf\" unique-identifier=\"bookid\">",
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:opf=\"http://www.idpf.org/2007/opf\">",
            $"<dc:identifier id=\"bookid\">{bookId}</dc:identifier>",
            $"<dc:title>{Html(title)}</dc:title>",
            $"<dc:creator opf:role=\"aut\">{Html(author)}</dc:creator>",
            $"<dc:date>{(metadata.PublicationDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? DateTime.Now.Year.ToString(System.Globalization.CultureInfo.InvariantCulture))}</dc:date>",
            $"<dc:rights>Created with {Generator}</dc:rights>",
            $"<dc:language>{Html(string.IsNullOrWhiteSpace(metadata.Language) ? "zh-CN" : metadata.Language.Trim())}</dc:language>",
        };
        if (!string.IsNullOrWhiteSpace(metadata.Translator))
            lines.Add($"<dc:contributor opf:role=\"trl\">{Html(metadata.Translator.Trim())}</dc:contributor>");
        if (!string.IsNullOrWhiteSpace(metadata.Isbn))
            lines.Add($"<dc:identifier opf:scheme=\"ISBN\">{Html(metadata.Isbn.Trim())}</dc:identifier>");
        if (!string.IsNullOrWhiteSpace(metadata.Publisher))
            lines.Add($"<dc:publisher>{Html(metadata.Publisher.Trim())}</dc:publisher>");
        if (!string.IsNullOrWhiteSpace(metadata.Category))
            lines.Add($"<dc:subject>{Html(metadata.Category.Trim())}</dc:subject>");
        if (!string.IsNullOrWhiteSpace(metadata.Description))
            lines.Add($"<dc:description>{Html(metadata.Description.Trim())}</dc:description>");
        if (hasImageCover)
            lines.Add("<meta name=\"cover\" content=\"cover-image\"/>");
        lines.AddRange([
            "</metadata>", "<manifest>",
            "<item id=\"ncxtoc\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>",
            "<item id=\"htmltoc\"  href=\"book-toc.html\" media-type=\"application/xhtml+xml\"/>",
            "<item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>",
            "<item id=\"cover\" href=\"cover.html\" media-type=\"application/xhtml+xml\"/>",
        ]);
        if (hasImageCover)
            lines.Add("<item id=\"cover-image\" href=\"cover.jpg\" media-type=\"image/jpeg\"/>");
        foreach (var illustration in illustrations)
            lines.Add($"<item id=\"{illustration.Id}\" href=\"{illustration.RelativePath}\" media-type=\"image/jpeg\"/>");
        if (hasEmbeddedFont)
            lines.Add("<item id=\"embedded-font\" href=\"fonts/book.ttf\" media-type=\"application/vnd.ms-opentype\"/>");
        for (var index = 0; index < chapterCount; index++)
            lines.Add($"<item id=\"chapter{index}\" href=\"chapter{index}.html\" media-type=\"application/xhtml+xml\"/>");
        lines.Add("</manifest>");
        lines.Add("<spine toc=\"ncxtoc\">");
        lines.Add("<itemref idref=\"cover\" linear=\"no\"/>");
        lines.Add("<itemref idref=\"htmltoc\" linear=\"yes\"/>");
        for (var index = 0; index < chapterCount; index++)
            lines.Add($"<itemref idref=\"chapter{index}\" linear=\"yes\"/>");
        lines.Add("</spine>");
        lines.Add("<guide>");
        lines.Add("<reference href=\"cover.html\" type=\"cover\" title=\"Cover\"/>");
        lines.Add("<reference href=\"book-toc.html\" type=\"toc\" title=\"Table Of Contents\"/>");
        lines.Add("<reference href=\"chapter0.html\" type=\"text\" title=\"Beginning\"/>");
        lines.Add("</guide>");
        lines.Add("</package>");
        return JoinLines(lines);
    }

    private static string BuildNcx(string title, string author, string bookId, IReadOnlyList<LegacyChapter> chapters)
    {
        var tocTree = BuildTocTree(chapters);
        var lines = new List<string>
        {
            "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"no\"?>",
            "<!DOCTYPE ncx PUBLIC \"-//NISO//DTD ncx 2005-1//EN\" \"http://www.daisy.org/z3986/2005/ncx-2005-1.dtd\">",
            "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">", "<head>",
            "<meta name=\"cover\" content=\"cover\"/>",
            $"<meta name=\"dtb:uid\" content=\"{bookId}\" />",
            $"<meta name=\"dtb:depth\" content=\"{CalculateTocDepth(tocTree)}\"/>",
            $"<meta name=\"dtb:generator\" content=\"{Generator}\"/>",
            "<meta name=\"dtb:totalPageCount\" content=\"0\"/>",
            "<meta name=\"dtb:maxPageNumber\" content=\"0\"/>", "</head>", "",
            "<docTitle>", $"<text>{Html(title)}</text>", "</docTitle>",
            "<docAuthor>", $"<text>{Html(author)}</text>", "</docAuthor>", "", "<navMap>",
            "<navPoint id=\"cover\" playOrder=\"1\">", "<navLabel><text>封面</text></navLabel>",
            "<content src=\"cover.html\"/>", "</navPoint>", "",
            "<navPoint id=\"htmltoc\" playOrder=\"2\">", "<navLabel><text>目录</text></navLabel>",
            "<content src=\"book-toc.html\"/>", "</navPoint>", "",
        };
        foreach (var root in tocTree) AddNcxNode(lines, root, chapters);
        lines.Add("</navMap>");
        lines.Add("</ncx>");
        return JoinLines(lines);
    }

    private static IReadOnlyList<TocNode> BuildTocTree(IReadOnlyList<LegacyChapter> chapters)
    {
        var roots = new List<TocNode>();
        var stack = new Stack<TocNode>();
        for (var index = 0; index < chapters.Count; index++)
        {
            if (!chapters[index].IncludeInToc) continue;
            var node = new TocNode(index, Math.Clamp(chapters[index].TocLevel, 1, 4));
            while (stack.Count > 0 && stack.Peek().Level >= node.Level) stack.Pop();
            if (stack.Count == 0) roots.Add(node);
            else stack.Peek().Children.Add(node);
            stack.Push(node);
        }
        return roots;
    }

    private static int CalculateTocDepth(IReadOnlyList<TocNode> roots) =>
        roots.Count == 0 ? 1 : roots.Max(CalculateTocDepth);

    private static int CalculateTocDepth(TocNode node) =>
        node.Children.Count == 0 ? 1 : 1 + node.Children.Max(CalculateTocDepth);

    private static void AddNcxNode(
        List<string> lines,
        TocNode node,
        IReadOnlyList<LegacyChapter> chapters)
    {
        lines.Add($"<navPoint id=\"chapter{node.ChapterIndex}\" playOrder=\"{node.ChapterIndex + 3}\">");
        lines.Add($"<navLabel><text>{Html(chapters[node.ChapterIndex].Title)}</text></navLabel>");
        lines.Add($"<content src=\"chapter{node.ChapterIndex}.html\"/>");
        foreach (var child in node.Children) AddNcxNode(lines, child, chapters);
        lines.Add("</navPoint>");
        lines.Add("");
    }

    private sealed record TocNode(int ChapterIndex, int Level)
    {
        public List<TocNode> Children { get; } = [];
    }

    private static string CreateLegacyBookId(string title, string author)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(936).GetBytes($"{title}-{author}");
        var hash = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        return $"easypub-{hash.Substring(8, 8)}";
    }

    private static string Html(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("'", "&#39;", StringComparison.Ordinal);
    private static string JoinLines(IEnumerable<string> lines) => string.Join("\r\n", lines) + "\r\n";

    private static async Task<IReadOnlyList<PreparedIllustration>> PrepareIllustrationsAsync(
        IReadOnlyList<BookIllustration> definitions,
        CancellationToken cancellationToken)
    {
        if (definitions.Count == 0) return [];

        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PreparedIllustration>(definitions.Count);
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            var marker = definition.Marker?.Trim();
            if (string.IsNullOrWhiteSpace(marker))
                throw new ArgumentException("插图标记不能为空。", nameof(definitions));
            if (!markers.Add(marker))
                throw new ArgumentException($"插图标记重复：{marker}", nameof(definitions));
            if (string.IsNullOrWhiteSpace(definition.ImagePath))
                throw new ArgumentException($"插图“{marker}”没有图片路径。", nameof(definitions));

            var prepared = await CoverImageConverter.PrepareJpegAsync(definition.ImagePath, cancellationToken);
            var number = index + 1;
            result.Add(new PreparedIllustration(
                marker,
                $"illustration-{number:000}",
                $"illustrations/illustration-{number:000}.jpg",
                string.IsNullOrWhiteSpace(definition.AltText) ? marker : definition.AltText.Trim(),
                definition.InsertAfterLine,
                prepared.JpegBytes));
        }
        return result;
    }

    private static bool TryGetIllustrationMarker(string paragraph, out string marker)
    {
        var value = paragraph.Trim();
        const string prefix = "[[插图:";
        if (value.StartsWith(prefix, StringComparison.Ordinal) &&
            value.EndsWith("]]", StringComparison.Ordinal) &&
            value.Length > prefix.Length + 2)
        {
            marker = value[prefix.Length..^2].Trim();
            return marker.Length > 0;
        }
        marker = string.Empty;
        return false;
    }

    private static bool TryGetPositionedIllustrationMarker(string paragraph, out string marker)
    {
        if (paragraph.StartsWith(LegacyTextParser.PositionedIllustrationPrefix, StringComparison.Ordinal))
        {
            marker = paragraph[LegacyTextParser.PositionedIllustrationPrefix.Length..].Trim();
            return marker.Length > 0;
        }
        marker = string.Empty;
        return false;
    }

    private sealed record PreparedIllustration(
        string Marker,
        string Id,
        string RelativePath,
        string AltText,
        int? InsertAfterLine,
        byte[] JpegBytes);

    private static void ValidateOptions(ConversionOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.FontSizePercent, 20);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.FontSizePercent, 500);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.LineHeightPercent, 50);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.LineHeightPercent, 500);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ParagraphSpacingEm);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ParagraphIndentEm);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PageMarginTopPx);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PageMarginBottomPx);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PageMarginLeftPx);
        ArgumentOutOfRangeException.ThrowIfNegative(options.PageMarginRightPx);
        ArgumentNullException.ThrowIfNull(options.Illustrations);
        ArgumentNullException.ThrowIfNull(options.Metadata);
        ArgumentNullException.ThrowIfNull(options.Font);
    }
}
