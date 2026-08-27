namespace EasyPub.Desktop;

public sealed record LayoutPreviewPage(string? Title, string Body);

public static class LayoutPreviewPaginator
{
    public static IReadOnlyList<LayoutPreviewPage> Paginate(
        string title,
        IEnumerable<string> paragraphs,
        double availableWidth,
        double availableHeight,
        double fontSize,
        double lineHeight)
    {
        var normalized = paragraphs
            .Select(paragraph => paragraph.Replace("\r", string.Empty).Trim())
            .Where(paragraph => paragraph.Length > 0)
            .ToArray();
        if (normalized.Length == 0) normalized = ["当前章节没有可显示的正文。"];

        var charactersPerLine = Math.Max(8, (int)Math.Floor(Math.Max(120, availableWidth) / Math.Max(8, fontSize)));
        var fullPageLines = Math.Max(5, (int)Math.Floor(Math.Max(160, availableHeight) / Math.Max(fontSize, lineHeight)));
        var firstPageLines = Math.Max(3, fullPageLines - 4);
        var pages = new List<LayoutPreviewPage>();
        var remaining = new Queue<string>(normalized);
        var firstPage = true;

        while (remaining.Count > 0)
        {
            var capacityLines = firstPage ? firstPageLines : fullPageLines;
            var capacityCharacters = Math.Max(charactersPerLine, capacityLines * charactersPerLine);
            var body = new List<string>();
            var usedLines = 0;

            while (remaining.Count > 0)
            {
                var paragraph = remaining.Peek();
                var paragraphLines = Math.Max(1, (int)Math.Ceiling((double)paragraph.Length / charactersPerLine));
                var spacingLines = body.Count == 0 ? 0 : 1;
                if (usedLines + spacingLines + paragraphLines <= capacityLines)
                {
                    remaining.Dequeue();
                    body.Add(paragraph);
                    usedLines += spacingLines + paragraphLines;
                    continue;
                }

                if (body.Count > 0) break;
                remaining.Dequeue();
                var take = Math.Min(paragraph.Length, capacityCharacters);
                body.Add(paragraph[..take]);
                if (take < paragraph.Length) remaining.EnqueueFront(paragraph[take..]);
                break;
            }

            pages.Add(new LayoutPreviewPage(firstPage ? title : null, string.Join("\n\n", body)));
            firstPage = false;
        }

        return pages;
    }

    private static void EnqueueFront<T>(this Queue<T> queue, T item)
    {
        var tail = queue.ToArray();
        queue.Clear();
        queue.Enqueue(item);
        foreach (var value in tail) queue.Enqueue(value);
    }
}
