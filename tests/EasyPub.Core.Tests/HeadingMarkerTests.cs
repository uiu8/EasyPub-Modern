using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// 「章」and「回」may appear without 「第」 — many releases print "八百三十章 赌徒" and the chapter has
/// to be found. The rarer markers may not: with a bare 「节」, the prose "九节鞭、三节棍，随意变换的
/// 冰武…" parsed as 第9节 and the whole sentence became a chapter title, which is how a 46-character
/// body paragraph ended up in the table of contents.
/// </summary>
public class HeadingMarkerTests
{
    [Theory]
    [InlineData("八百三十章 赌徒", true)]              // no 「第」, still a chapter
    [InlineData("第830章 赌徒", true)]
    [InlineData("第一节 开始", true)]                  // real section keeps working
    [InlineData("第三卷 风起", true)]
    [InlineData("第二部 远方", true)]
    [InlineData("番外 第二章 混蛋兄妹", true)]
    [InlineData("序章", true)]
    public void Real_headings_are_still_recognised(string text, bool expected) =>
        Assert.Equal(expected, HeadingSyntax.IsHeading(text));

    [Theory]
    [InlineData("九节鞭、三节棍，随意变换的冰武，长短随心的冰矛，或许，我一直以来的运用冰霜武器的方式错误了。")]
    [InlineData("三节棍的用法")]
    [InlineData("细节决定成败")]
    [InlineData("这一集很好看")]
    [InlineData("三回头看")]
    [InlineData("前言不搭后语")]
    public void Prose_that_only_looks_numbered_is_not_a_heading(string text) =>
        Assert.False(HeadingSyntax.IsHeading(text));

    /// <summary>
    /// The guard lives in the recogniser, not in the number parser. ChapterNumber stays permissive
    /// about a bare 「节」 on purpose — narrowing it there resolved "第十卷" to the wrong number — so
    /// what keeps prose out of the book is that DefaultPattern never calls it a heading in the first
    /// place. This asserts the layer that actually protects the tree.
    /// </summary>
    [Fact]
    public void Prose_is_stopped_by_the_recogniser_not_by_the_number_parser()
    {
        const string sentence = "九节鞭、三节棍，随意变换的冰武，长短随心的冰矛，或许，我一直以来的运用冰霜武器的方式错误了。";

        Assert.False(HeadingSyntax.IsHeading(sentence));
    }
}
