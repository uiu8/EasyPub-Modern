using System.IO.Compression;

namespace EasyPub.Core;

public sealed record BookPreviewItem(string Title, string HtmlPath, bool IsChapter);

public sealed class BookPreviewPackage : IDisposable
{
    internal BookPreviewPackage(string workingDirectory, IReadOnlyList<BookPreviewItem> items)
    {
        WorkingDirectory = workingDirectory;
        Items = items;
    }

    public string WorkingDirectory { get; }
    public IReadOnlyList<BookPreviewItem> Items { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(WorkingDirectory)) Directory.Delete(WorkingDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

public sealed class BookPreviewService
{
    public async Task<BookPreviewPackage> BuildAsync(
        ConversionRequest request,
        IProgress<ConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var workingDirectory = Path.Combine(Path.GetTempPath(), "EasyPubModernPreview", Guid.NewGuid().ToString("N"));
        var epubPath = Path.Combine(workingDirectory, "preview.epub");
        var extractedPath = Path.Combine(workingDirectory, "book");
        Directory.CreateDirectory(workingDirectory);

        try
        {
            await new EasyPubConverter().ConvertAsync(
                request with { OutputPath = epubPath }, progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ZipFile.ExtractToDirectory(epubPath, extractedPath);
            var oebps = Path.Combine(extractedPath, "OEBPS");
            var chapters = await LegacyTextParser.ParseAsync(
                request.InputPath,
                request.Options ?? ConversionOptions.LegacyDefault,
                cancellationToken).ConfigureAwait(false);
            var items = new List<BookPreviewItem>
            {
                new("封面", Path.Combine(oebps, "cover.html"), false),
                new("目录", Path.Combine(oebps, "book-toc.html"), false),
            };
            items.AddRange(chapters.Select((chapter, index) =>
                new BookPreviewItem(chapter.Title, Path.Combine(oebps, $"chapter{index}.html"), true)));
            return new BookPreviewPackage(workingDirectory, items);
        }
        catch
        {
            try { Directory.Delete(workingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            throw;
        }
    }
}
