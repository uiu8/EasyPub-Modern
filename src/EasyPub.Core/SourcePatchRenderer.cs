using System.Text;

namespace EasyPub.Core;

/// <summary>What rendering produced, and whether it did anything at all.</summary>
public sealed record SourceRenderResult(string Text, int LinesBefore, int LinesAfter, int Deleted, int Replaced, int Inserted)
{
    public bool Changed => Deleted > 0 || Replaced > 0 || Inserted > 0;
}

/// <summary>
/// Turns a patch into the text it describes.
///
/// <para>Rendering is a single pass over the original lines, keyed by <b>original</b> line numbers, so the
/// order the operations are stored in cannot change the result. That matters because the alternative —
/// applying edits one after another, each shifting the ones below — makes correctness depend on getting
/// the order right, and hides any mistake inside the result rather than reporting it.</para>
///
/// <para>Lines the patch does not mention are copied through <b>byte for byte</b>, including their own
/// terminators. A repair that changes one line therefore shows up as a one-line change, not a whole-file
/// rewrite.</para>
/// </summary>
public static class SourcePatchRenderer
{
    public static SourceRenderResult Render(SourceTextDocument document, SourcePatch patch)
    {
        var lineCount = document.Lines.Count;

        var before = new Dictionary<int, List<InsertLineAtAnchor>>();
        var after = new Dictionary<int, List<InsertLineAtAnchor>>();
        var atEnd = new List<InsertLineAtAnchor>();
        var deletes = new Dictionary<int, DeleteOriginalLine>();
        var replaces = new Dictionary<int, ReplaceOriginalLine>();

        foreach (var operation in patch.Operations)
        {
            switch (operation)
            {
                case DeleteOriginalLine delete:
                    deletes[delete.Line] = delete;
                    break;
                case ReplaceOriginalLine replace:
                    replaces[replace.Line] = replace;
                    break;
                case InsertLineAtAnchor insert:
                    switch (insert.Anchor)
                    {
                        case BeforeOriginalLine line:
                            Bucket(before, line.Line).Add(insert);
                            break;
                        case AfterOriginalLine line:
                            Bucket(after, line.Line).Add(insert);
                            break;
                        default:
                            atEnd.Add(insert);
                            break;
                    }
                    break;
            }
        }

        // Stable within a bucket: ordinal first, then operation id. The normaliser already ordered them,
        // but a patch that came from anywhere else must still render the same way.
        foreach (var bucket in before.Values) bucket.Sort(CompareInserts);
        foreach (var bucket in after.Values) bucket.Sort(CompareInserts);
        atEnd.Sort(CompareInserts);

        var builder = new StringBuilder();
        var inserted = 0;

        for (var line = 1; line <= lineCount; line++)
        {
            if (before.TryGetValue(line, out var leading))
                foreach (var insert in leading) AppendInsert(builder, document, insert, line, prepend: true, ref inserted);

            if (deletes.ContainsKey(line)) continue;

            if (replaces.TryGetValue(line, out var replace))
            {
                builder.Append(replace.NewText);
                // A replacement keeps the terminator the line already had; replacing the text of a line is
                // not a reason to change how it ends.
                if (document.Lines[line - 1].Terminator is { } terminator) builder.Append(terminator);
            }
            else
            {
                builder.Append(document.Lines[line - 1].Text);
                if (document.Lines[line - 1].Terminator is { } terminator) builder.Append(terminator);
            }

            if (after.TryGetValue(line, out var trailing))
                foreach (var insert in trailing) AppendInsert(builder, document, insert, line, prepend: false, ref inserted);
        }

        // End-of-file inserts. If the file did not end with a newline, one is added first: without it the
        // new content would run into the last line instead of following it.
        if (atEnd.Count > 0)
        {
            if (lineCount > 0 && document.Lines[^1].Terminator is null) builder.Append(document.DefaultNewLine);
            for (var index = 0; index < atEnd.Count; index++)
            {
                builder.Append(atEnd[index].Text);
                // The last inserted line inherits "no terminator at end of file", so the shape of the file
                // is preserved rather than gaining a trailing newline it never had.
                var isLast = index == atEnd.Count - 1;
                if (!isLast) builder.Append(document.DefaultNewLine);
            }
            inserted += atEnd.Count;
        }

        var after_ = lineCount - deletes.Count + inserted;
        return new SourceRenderResult(builder.ToString(), lineCount, after_, deletes.Count, replaces.Count, inserted);
    }

    /// <summary>
    /// An insert needs a terminator belonging to the line it was placed against. The line's own terminator
    /// is used when it has one, so an insert into a CRLF file gets CRLF even inside an otherwise LF file.
    /// </summary>
    private static void AppendInsert(StringBuilder builder, SourceTextDocument document,
        InsertLineAtAnchor insert, int anchorLine, bool prepend, ref int inserted)
    {
        builder.Append(insert.Text);
        var terminator = prepend
            ? document.NewLineForInsertBefore(anchorLine)
            : document.NewLineForInsertAfter(anchorLine);
        builder.Append(terminator);
        inserted++;
    }

    private static List<InsertLineAtAnchor> Bucket(Dictionary<int, List<InsertLineAtAnchor>> map, int line)
    {
        if (!map.TryGetValue(line, out var list)) map[line] = list = [];
        return list;
    }

    private static int CompareInserts(InsertLineAtAnchor left, InsertLineAtAnchor right)
    {
        var byOrdinal = SourceAnchor.OrdinalOf(left.Anchor).CompareTo(SourceAnchor.OrdinalOf(right.Anchor));
        return byOrdinal != 0 ? byOrdinal : string.CompareOrdinal(left.OperationId, right.OperationId);
    }
}

/// <summary>
/// The check that has to pass before any repair is allowed to write the TXT.
///
/// <para>It renders the patch against the original text and compares the result with the bytes on disk.
/// Rendering with <b>no</b> operations must reproduce the file exactly; if it does not, the encoding or
/// the line endings are not understood well enough to write, and the honest answer is to refuse rather
/// than to save a file that comes back subtly different from the one the user had.</para>
/// </summary>
public static class SourceRenderVerifier
{
    public static SourceRenderVerification Verify(SourceTextDocument document, string originalText)
    {
        var reproduced = document.Render();
        if (!string.Equals(reproduced, originalText, StringComparison.Ordinal))
        {
            var position = FirstDifference(reproduced, originalText);
            return new SourceRenderVerification(false,
                $"把原文按行拆开再拼回去得到了不同的内容（第 {position} 个字符处）。"
                + "为了避免把文件改成你没要求的样子，本次不会改动原文。");
        }
        return new SourceRenderVerification(true, "原文可以无损读入并还原。");
    }

    private static int FirstDifference(string left, string right)
    {
        var limit = Math.Min(left.Length, right.Length);
        for (var index = 0; index < limit; index++)
            if (left[index] != right[index]) return index + 1;
        return limit + 1;
    }
}

public sealed record SourceRenderVerification(bool IsLossless, string Message);
