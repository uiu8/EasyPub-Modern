using EasyPub.Core;

namespace EasyPub.Core.Tests;

/// <summary>
/// The breakpoint list is what the chapter workbench draws between two rows, so these tests pin down
/// exactly what a user is told: a real gap in the source text, a heading whose number is written wrong,
/// or a chapter the official directory has and the text does not.
/// </summary>
public class ChapterBreakpointsTests
{
    private static ChapterTreeDocument Document(params string[] titles)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".txt");
        File.WriteAllText(path, string.Join("\n", titles.SelectMany((title, index) => new[] { title, $"正文{index}" })));
        return ChapterTreeDocument.LoadAsync(path).GetAwaiter().GetResult();
    }

    private static ChapterTreeEntry Entry(string id, string title, int line) =>
        new(id, title, 1, true, line, [new ChapterSourceRange(line + 1, line + 1)]);

    /// <summary>Chinese chapter numbering, so the catalogue titles read like a real one.</summary>
    private static string Chinese(int number)
    {
        string[] digits = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
        string[] units = ["", "十", "百", "千"];
        var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = new System.Text.StringBuilder();
        for (var index = 0; index < text.Length; index++)
        {
            var digit = text[index] - '0';
            if (digit == 0) { result.Append('零'); continue; }
            result.Append(digits[digit]).Append(units[text.Length - index - 1]);
        }
        return result.ToString().TrimEnd('零');
    }

    [Fact]
    public void Consecutive_chapters_produce_no_breakpoint()
    {
        var document = Document("第一章 甲", "第二章 乙", "第三章 丙");
        Assert.Empty(ChapterBreakpoints.Between(document));
    }

    [Fact]
    public void A_skipped_number_reports_the_missing_chapters()
    {
        var document = Document("第四百零一章 甲", "第四百十二章 生意");
        var breakpoint = Assert.Single(ChapterBreakpoints.Between(document));
        Assert.Equal(ChapterBreakpointKind.NumberGap, breakpoint.Kind);
        Assert.Equal(401, breakpoint.PreviousNumber);
        Assert.Equal(Enumerable.Range(402, 10), breakpoint.MissingNumbers);
        Assert.Contains("402", breakpoint.Detail);
        Assert.Contains("411", breakpoint.Detail);
    }

    [Fact]
    public void A_zero_in_front_of_a_unit_is_read_as_the_one_it_means()
    {
        // This release prints 第四百零十章 for 第四百一十章. Reading the 零 as a coefficient makes it 400,
        // which turns a chapter that is present into a four-hundred-chapter hole in the tree.
        Assert.Equal(410, HeadingSyntax.ParseNumber("四百零十"));
        Assert.Equal(1010, HeadingSyntax.ParseNumber("一千零十"));
        // A 零 in front of a digit is the ordinary placeholder and must keep its value.
        Assert.Equal(1008, HeadingSyntax.ParseNumber("一千零八"));
        Assert.Equal(1080, HeadingSyntax.ParseNumber("一千零八十"));
        var document = Document("第四百零九章 甲", "第四百零十章 龙城轶事", "第四百十一章 乙");
        Assert.Empty(ChapterBreakpoints.Between(document));
    }

    [Fact]
    public void A_real_gap_on_either_side_of_the_miswritten_heading_is_still_reported()
    {
        // 401 … the miswritten 410 … 412: chapters 402–409 and 411 really are absent, and the chapter that
        // is present under a miswritten number must not be counted among them.
        var document = Document("第四百零一章 甲", "第四百零十章 龙城轶事", "第四百十二章 乙");
        var breaks = ChapterBreakpoints.Between(document);
        Assert.All(breaks, item => Assert.Equal(ChapterBreakpointKind.NumberGap, item.Kind));
        var missing = breaks.SelectMany(item => item.MissingNumbers).ToArray();
        Assert.Equal(Enumerable.Range(402, 8).Append(411), missing);
        Assert.DoesNotContain(410, missing);
    }

    [Fact]
    public void A_real_placeholder_zero_is_not_mistaken_for_a_typo()
    {
        // "一千零八" is correct Chinese for 1008: fixing its 零 would invent chapter 1018.
        var document = Document("第一千零七章 甲", "第一千零八章 乙");
        Assert.Empty(ChapterBreakpoints.Between(document));
        Assert.Equal(1008, ChapterBreakpoints.Read("第一千零八章 乙", new Dictionary<char, char>()).Number);
    }

    [Fact]
    public void A_heading_the_tree_could_not_recognise_is_not_a_gap_the_breakpoints_invent()
    {
        // "第六百八十久章 奥秘" is not a heading the built-in recognition accepts — 久 is not a numeral — so it
        // never enters the tree. The breakpoint list reports the gap the tree actually has rather than
        // silently inventing a chapter from the 编号纠错表, which only applies to headings already recognised.
        var document = Document("第六百八十八章 甲", "第六百八十久章 奥秘", "第六百九十章 乙");
        Assert.Equal(2, document.Entries.Count(entry => !entry.IsFrontMatter));
        var gap = Assert.Single(ChapterBreakpoints.Between(document));
        Assert.Equal(ChapterBreakpointKind.NumberGap, gap.Kind);
        Assert.Equal([689], gap.MissingNumbers);
    }

    [Fact]
    public void The_books_own_number_correction_table_applies_to_a_heading_it_did_recognise()
    {
        // 零 is a numeral, so this heading is recognised; the 编号纠错表 is then what decides its number.
        var document = Document("第五百零七章 甲", "第五百零八章 乙");
        Assert.Empty(ChapterBreakpoints.Between(document));
        var reading = ChapterBreakpoints.Read("第五百零八章 乙",
            HeadingTypoRules.Parse(HeadingTypoRules.Default));
        Assert.Equal(508, reading.Number);
    }

    [Fact]
    public void The_reference_names_the_chapters_the_text_lacks()
    {
        var entries = new[]
        {
            Entry("a", "第四百零一章 标题401", 10),
            Entry("b", "第四百十二章 标题412", 20),
        };
        var document = Document("占位").WithEntries(entries);
        var catalog = new ReferenceCatalog("test", "test", Enumerable.Range(401, 12)
            .Select(number => new ReferenceNode($"第{Chinese(number)}章 标题{number}", ReferenceNodeKind.Chapter, null, null))
            .ToArray());
        var missing = ChapterBreakpoints.Between(document, entries, catalog)
            .Where(item => item.Kind == ChapterBreakpointKind.ReferenceMissing).ToArray();
        Assert.NotEmpty(missing);
        var titles = string.Join("；", missing.SelectMany(item => item.MissingTitles));
        Assert.Contains("标题402", titles);
        Assert.Contains("标题411", titles);
        Assert.Equal("a", missing[0].AnchorId);
        Assert.Equal("b", missing[0].NextId);
    }

    [Fact]
    public void A_repeated_number_does_not_turn_the_rest_of_the_book_into_a_gap()
    {
        // 牧神记 prints 第一五五章 where 第一五五五章 belongs, so 155 appears inside a run of 1553…1556.
        // Whatever the tree makes of that, it must never claim that a whole volume's worth of chapters —
        // which the tree clearly does contain — went missing.
        var document = Document("第一五五三章 甲", "第一五五章 神话的破灭", "第一五五五章 乙", "第一五五六章 丙");
        foreach (var gap in ChapterBreakpoints.Between(document).Where(item => item.Kind == ChapterBreakpointKind.NumberGap))
        {
            Assert.True(gap.MissingNumbers.Count <= 30, $"缺口被夸大：{gap.Detail}");
            Assert.Contains("编号", gap.Detail);
        }
    }

    [Fact]
    public void Volume_boundaries_do_not_report_a_breakpoint()
    {
        var document = Document("第一卷 起", "第一章 甲", "第二卷 承", "第一章 乙");
        Assert.Empty(ChapterBreakpoints.Between(document));
    }
}
