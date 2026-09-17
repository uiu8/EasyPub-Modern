using System.Text;

namespace EasyPub.Core;

/// <summary>
/// One line of a source file, with the exact terminator that followed it.
///
/// <para><see cref="Terminator"/> is <c>null</c> for the last line when the file does not end with a
/// newline. That case is real and worth carrying: a file whose last line has no terminator is not the
/// same file as one that has it, and a renderer that "normalises" it changes bytes the user never asked
/// to change.</para>
/// </summary>
public sealed record SourceTextLine(string Text, string? Terminator)
{
    public bool EndsLine => Terminator is not null;
}

/// <summary>
/// A source file split into lines without losing anything.
///
/// <para>Losslessness is a hard requirement, not tidiness. Before a repair may edit the TXT it renders the
/// plan against the original bytes and compares: if rendering with no changes does not reproduce the file
/// exactly, the encoding or the line endings are not understood well enough to write, and the edit is
/// refused rather than applied to a file that will come back subtly different.</para>
///
/// <para>The old model kept one newline string for the whole file. That is enough for a file that is
/// consistent and wrong for one that is not — mixed files exist, and re-writing every line with the
/// majority terminator turns a one-line change into a whole-file diff.</para>
/// </summary>
public sealed record SourceTextDocument(
    IReadOnlyList<SourceTextLine> Lines,
    Encoding Encoding,
    byte[] Preamble,
    string DefaultNewLine)
{
    /// <summary>
    /// Splits the text, keeping each line's own terminator.
    ///
    /// <para>CRLF is tested before CR so a CRLF file is not read as a CR file with stray LFs, and the
    /// final line is allowed to have no terminator at all.</para>
    ///
    /// <para>The default encoding is a UTF-8 that emits <b>no</b> byte-order mark, not
    /// <see cref="Encoding.UTF8"/>. That property returns an instance whose <c>GetPreamble</c> is the BOM,
    /// so a renderer that asks the encoding what to write in front of the text would add three bytes the
    /// file never had — and the resulting hash would not match the one computed from the text alone. A
    /// real replace was caught by exactly that mismatch.</para>
    /// </summary>
    public static SourceTextDocument Parse(string text, Encoding? encoding = null, byte[]? preamble = null)
    {
        var lines = new List<SourceTextLine>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                lines.Add(new SourceTextLine(text[start..index], "\r\n"));
                index++;
                start = index + 1;
            }
            else if (character is '\r' or '\n')
            {
                lines.Add(new SourceTextLine(text[start..index], character.ToString()));
                start = index + 1;
            }
        }
        // A trailing terminator produced an empty tail that is not a line of its own.
        if (start < text.Length) lines.Add(new SourceTextLine(text[start..], null));
        else if (lines.Count == 0) lines.Add(new SourceTextLine("", null));

        return new SourceTextDocument(lines, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            preamble ?? [], DetermineDefaultNewLine(lines));
    }

    /// <summary>
    /// The terminator new lines should use: the first one the file actually has.
    ///
    /// <para>First rather than most common, so the answer does not depend on scanning the whole file, and
    /// so two runs over the same bytes always agree. A single-line file has none, and CRLF is the
    /// conventional answer for a Windows-oriented tool.</para>
    /// </summary>
    private static string DetermineDefaultNewLine(IReadOnlyList<SourceTextLine> lines) =>
        lines.Select(line => line.Terminator).FirstOrDefault(terminator => terminator is not null) ?? "\r\n";

    /// <summary>
    /// Reads a file the same way the chapter tree reads it.
    ///
    /// <para>There used to be two readers: the tree went through <see cref="TextFileDecoder"/> — BOM
    /// detection, strict UTF-8, GBK fallback — and this document came from <c>File.ReadAllText</c>, which
    /// always assumes UTF-8. On a GBK book the two disagreed about the characters, and therefore about
    /// where the lines are, which is exactly the disagreement a coordinate map cannot survive.</para>
    /// </summary>
    public static SourceTextDocument Decode(byte[] bytes, TextEncodingMode mode = TextEncodingMode.Auto)
    {
        var decoded = TextFileDecoder.Decode(bytes, mode);
        return Parse(decoded.Text, decoded.Encoding, decoded.Preamble);
    }

    public static SourceTextDocument Load(string path, TextEncodingMode mode = TextEncodingMode.Auto) =>
        Decode(File.ReadAllBytes(path), mode);

    public static async Task<SourceTextDocument> LoadAsync(string path, TextEncodingMode mode = TextEncodingMode.Auto,
        CancellationToken token = default) =>
        Decode(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false), mode);

    /// <summary>Rebuilds the text exactly as it was parsed. The identity check relies on this.</summary>
    public string Render()
    {
        var builder = new StringBuilder();
        foreach (var line in Lines)
        {
            builder.Append(line.Text);
            if (line.Terminator is not null) builder.Append(line.Terminator);
        }
        return builder.ToString();
    }

    /// <summary>
    /// The newline to use for a line inserted before original line <paramref name="line"/> (1-based).
    ///
    /// <para>The rules are stated rather than left to judgement, because "nearby dominant terminator" has
    /// at least three reasonable readings — take the previous line, take the next, take the majority of
    /// ten — and each produces a different expected hash for the same plan. An expected hash that depends
    /// on which reading the implementer chose cannot be used to verify anything.</para>
    /// </summary>
    public string NewLineForInsertBefore(int line)
    {
        if (line > 1 && line - 1 <= Lines.Count && Lines[line - 2].Terminator is { } previous)
            return previous;
        if (line >= 1 && line <= Lines.Count && Lines[line - 1].Terminator is { } current)
            return current;
        return DefaultNewLine;
    }

    /// <summary>The newline to use for a line inserted after original line <paramref name="line"/>.</summary>
    public string NewLineForInsertAfter(int line)
    {
        if (line >= 1 && line <= Lines.Count && Lines[line - 1].Terminator is { } terminator)
            return terminator;
        return DefaultNewLine;
    }
}
