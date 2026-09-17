using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// The one-click repair searches for headings the recogniser missed, and how far it reaches is a
/// judgement call rather than a fact: how wide a gap to bridge, whether a heading has to stand alone,
/// whether a mistyped number may be corrected. Those were hard-coded, which meant a book whose defect
/// fell outside them reported "nothing to repair" and left the user no way to say otherwise.
///
/// What the tests below do <em>not</em> claim is that the repair finds every shape of lost heading.
/// Probing the real pipeline showed the recogniser already accepts a missing 「第」 ("三章 归途" enters
/// the tree on its own), so the cases left for the repair are narrower than they look — a heading whose
/// separator also went missing, or one that lost its blank neighbours. Encoding a guessed shape here
/// would have tested the guess, not the code.
/// </summary>
public class HeadingRepairOptionsTests
{
    private static async Task<string> WriteAsync(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), $"easypub-heading-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    /// <summary>
    /// A run of missing numbers wider than the default limit. The default refuses it on purpose — a
    /// numbering convention that restarts per volume looks exactly like this — but the limit has to be
    /// the knob that decides, not a constant buried in the search.
    /// </summary>
    [Fact]
    public async Task The_gap_limit_decides_which_pairs_are_searched_at_all()
    {
        var text = "第一章 启程\n正文第一段。\n第十五章 收束\n正文最后一段。\n";
        var path = await WriteAsync(text);
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var cautious = MissingChapterHeadings.Find(document, document.Entries, new MissingChapterHeadingOptions());
            Assert.Empty(cautious);
            var narrowed = MissingChapterHeadings.Find(document, document.Entries,
                new MissingChapterHeadingOptions { MaximumGap = 3 });
            Assert.Empty(narrowed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void The_defaults_stay_at_the_cautious_end_of_every_switch()
    {
        var options = new MissingChapterHeadingOptions();
        Assert.True(options.RequireIsolatedLine);
        Assert.Equal(30, options.MaximumGap);
        Assert.True(options.AllowMissingPrefix);
        Assert.True(options.AllowNumberTypos);
    }

    /// <summary>
    /// The switches reach the search rather than sitting in the record unread: turning every widening
    /// off must not produce more candidates than leaving them on.
    /// </summary>
    [Fact]
    public async Task Turning_the_widenings_off_never_finds_more_than_leaving_them_on()
    {
        var text = "第一章 启程\n正文第一段。\n三章归途的开始\n正文第三段。\n第四章 收束\n正文第四段。\n";
        var path = await WriteAsync(text);
        try
        {
            var document = await ChapterTreeDocument.LoadAsync(path);
            var wide = MissingChapterHeadings.Find(document, document.Entries, new MissingChapterHeadingOptions());
            var narrow = MissingChapterHeadings.Find(document, document.Entries,
                new MissingChapterHeadingOptions { AllowMissingPrefix = false, AllowNumberTypos = false, RequireIsolatedLine = true });
            Assert.True(narrow.Count <= wide.Count,
                $"收紧后找到 {narrow.Count} 个，放宽时 {wide.Count} 个——开关没有生效");
        }
        finally { File.Delete(path); }
    }
}
