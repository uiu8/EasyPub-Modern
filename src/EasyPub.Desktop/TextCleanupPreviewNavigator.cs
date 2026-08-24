using EasyPub.Core;

namespace EasyPub.Desktop;

public sealed record TextCleanupPreviewView(
    string Text,
    int SelectionStart,
    int SelectionLength,
    string LocationText,
    bool IsWindowed);

public static class TextCleanupPreviewNavigator
{
    public const int DefaultMaximumCharacters = 120_000;

    public static TextCleanupPreviewView Create(
        TextCleanupPreview preview,
        TextCleanupChange? selectedChange = null,
        int maximumCharacters = DefaultMaximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (maximumCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        var renderedLines = new List<RenderedLine>();
        var text = new System.Text.StringBuilder();
        for (var sourceIndex = 0; sourceIndex < preview.Lines.Count; sourceIndex++)
        {
            var line = preview.Lines[sourceIndex];
            if (TextCleanupPipeline.IsRemovedLine(line)) continue;
            if (renderedLines.Count > 0) text.Append(Environment.NewLine);
            var start = text.Length;
            text.Append(line);
            renderedLines.Add(new RenderedLine(sourceIndex + 1, start, line.Length));
        }

        var completeText = text.ToString();
        if (selectedChange is null)
        {
            if (completeText.Length <= maximumCharacters)
                return new TextCleanupPreviewView(completeText, 0, 0, "点击左侧修改记录，可定位并高亮对应正文", false);

            var initialEnd = AlignWindowEnd(completeText, maximumCharacters);
            return new TextCleanupPreviewView(
                completeText[..initialEnd],
                0,
                0,
                "正文较长，当前显示开头；点击左侧记录可跳到任意修改处",
                true);
        }

        var exactLine = renderedLines.FirstOrDefault(line => line.SourceLineNumber == selectedChange.LineNumber);
        var targetLine = exactLine ?? FindNearestRenderedLine(renderedLines, selectedChange.LineNumber);
        if (targetLine is null)
        {
            return new TextCleanupPreviewView(
                completeText,
                0,
                0,
                $"原文第 {selectedChange.LineNumber} 行已删除；处理后正文为空",
                false);
        }

        var isRemoved = exactLine is null;
        var locationText = isRemoved
            ? $"原文第 {selectedChange.LineNumber} 行已删除，已定位到相邻正文第 {targetLine.SourceLineNumber} 行"
            : $"已定位：原文第 {selectedChange.LineNumber} 行 · {selectedChange.Rule}";

        if (completeText.Length <= maximumCharacters)
        {
            return new TextCleanupPreviewView(
                completeText,
                targetLine.Start,
                Math.Max(1, targetLine.Length),
                locationText,
                false);
        }

        var desiredStart = Math.Max(0, targetLine.Start - maximumCharacters / 2);
        var windowStart = AlignWindowStart(completeText, desiredStart);
        var windowEnd = AlignWindowEnd(completeText, Math.Min(completeText.Length, windowStart + maximumCharacters));
        if (targetLine.Start + targetLine.Length > windowEnd)
        {
            windowEnd = Math.Min(completeText.Length, targetLine.Start + targetLine.Length);
            windowStart = AlignWindowStart(completeText, Math.Max(0, windowEnd - maximumCharacters));
        }

        return new TextCleanupPreviewView(
            completeText[windowStart..windowEnd],
            targetLine.Start - windowStart,
            Math.Max(1, targetLine.Length),
            locationText + "（显示当前位置附近正文）",
            true);
    }

    private static RenderedLine? FindNearestRenderedLine(IReadOnlyList<RenderedLine> lines, int sourceLineNumber)
    {
        if (lines.Count == 0) return null;
        return lines
            .OrderBy(line => Math.Abs(line.SourceLineNumber - sourceLineNumber))
            .ThenBy(line => line.SourceLineNumber < sourceLineNumber ? 1 : 0)
            .First();
    }

    private static int AlignWindowStart(string text, int position)
    {
        if (position <= 0) return 0;
        var lineBreak = text.LastIndexOf(Environment.NewLine, position, StringComparison.Ordinal);
        return lineBreak < 0 ? 0 : lineBreak + Environment.NewLine.Length;
    }

    private static int AlignWindowEnd(string text, int position)
    {
        if (position >= text.Length) return text.Length;
        var lineBreak = text.LastIndexOf(Environment.NewLine, position, StringComparison.Ordinal);
        return lineBreak <= 0 ? position : lineBreak;
    }

    private sealed record RenderedLine(int SourceLineNumber, int Start, int Length);
}
