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

        if (selectedChange is null)
            return CreateInitialWindow(preview.Lines, maximumCharacters);

        var requestedIndex = Math.Clamp(selectedChange.LineNumber - 1, 0, Math.Max(0, preview.Lines.Count - 1));
        var exact = preview.Lines.Count > 0 && !TextCleanupPipeline.IsRemovedLine(preview.Lines[requestedIndex]);
        var targetIndex = exact ? requestedIndex : FindNearestRenderedLine(preview.Lines, requestedIndex);
        if (targetIndex < 0)
        {
            return new TextCleanupPreviewView(
                string.Empty,
                0,
                0,
                $"原文第 {selectedChange.LineNumber} 行已删除；处理后正文为空",
                false);
        }

        var locationText = !exact
            ? $"原文第 {selectedChange.LineNumber} 行已删除，已定位到相邻正文第 {targetIndex + 1} 行"
            : $"已定位：原文第 {selectedChange.LineNumber} 行 · {selectedChange.Rule}";
        return CreateTargetWindow(preview.Lines, targetIndex, maximumCharacters, locationText);
    }

    private static TextCleanupPreviewView CreateInitialWindow(IReadOnlyList<string> lines, int maximumCharacters)
    {
        var text = new System.Text.StringBuilder(Math.Min(maximumCharacters, 16_384));
        var lastRenderedIndex = -1;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (TextCleanupPipeline.IsRemovedLine(line)) continue;
            var separatorLength = text.Length == 0 ? 0 : Environment.NewLine.Length;
            if (text.Length + separatorLength + line.Length > maximumCharacters)
            {
                if (text.Length == 0) text.Append(line.AsSpan(0, Math.Min(line.Length, maximumCharacters)));
                return new TextCleanupPreviewView(
                    text.ToString(), 0, 0,
                    "正文较长，当前显示开头；点击左侧记录可跳到任意修改处", true);
            }
            if (separatorLength > 0) text.Append(Environment.NewLine);
            text.Append(line);
            lastRenderedIndex = index;
        }

        return new TextCleanupPreviewView(
            text.ToString(), 0, 0,
            "点击左侧修改记录，可定位并高亮对应正文",
            lastRenderedIndex + 1 < lines.Count && HasRenderedLine(lines, lastRenderedIndex + 1, lines.Count));
    }

    private static TextCleanupPreviewView CreateTargetWindow(
        IReadOnlyList<string> lines,
        int targetIndex,
        int maximumCharacters,
        string locationText)
    {
        var startIndex = targetIndex;
        var leadingBudget = maximumCharacters / 2;
        var leadingLength = 0;
        for (var index = targetIndex - 1; index >= 0; index--)
        {
            if (TextCleanupPipeline.IsRemovedLine(lines[index])) continue;
            var cost = lines[index].Length + Environment.NewLine.Length;
            if (leadingLength + cost > leadingBudget) break;
            leadingLength += cost;
            startIndex = index;
        }

        var text = new System.Text.StringBuilder(Math.Min(maximumCharacters, 16_384));
        var selectionStart = 0;
        var selectionLength = 0;
        var lastRenderedIndex = startIndex - 1;
        for (var index = startIndex; index < lines.Count; index++)
        {
            var line = lines[index];
            if (TextCleanupPipeline.IsRemovedLine(line)) continue;
            var separatorLength = text.Length == 0 ? 0 : Environment.NewLine.Length;
            if (index != targetIndex && text.Length + separatorLength + line.Length > maximumCharacters)
                break;
            if (separatorLength > 0) text.Append(Environment.NewLine);
            if (index == targetIndex) selectionStart = text.Length;
            text.Append(line);
            if (index == targetIndex) selectionLength = Math.Max(1, line.Length);
            lastRenderedIndex = index;
        }

        var isWindowed = HasRenderedLine(lines, 0, startIndex)
            || HasRenderedLine(lines, lastRenderedIndex + 1, lines.Count);
        return new TextCleanupPreviewView(
            text.ToString(), selectionStart, selectionLength,
            isWindowed ? locationText + "（显示当前位置附近正文）" : locationText,
            isWindowed);
    }

    private static int FindNearestRenderedLine(IReadOnlyList<string> lines, int sourceIndex)
    {
        for (var distance = 1; distance < lines.Count; distance++)
        {
            var after = sourceIndex + distance;
            if (after < lines.Count && !TextCleanupPipeline.IsRemovedLine(lines[after])) return after;
            var before = sourceIndex - distance;
            if (before >= 0 && !TextCleanupPipeline.IsRemovedLine(lines[before])) return before;
        }
        return -1;
    }

    private static bool HasRenderedLine(IReadOnlyList<string> lines, int start, int end)
    {
        for (var index = Math.Max(0, start); index < Math.Min(end, lines.Count); index++)
            if (!TextCleanupPipeline.IsRemovedLine(lines[index])) return true;
        return false;
    }
}
