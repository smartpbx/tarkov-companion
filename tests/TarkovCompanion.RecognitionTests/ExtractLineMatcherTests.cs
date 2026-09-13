using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

/// <summary>
/// Matching one line off the extract screen to one exit in the catalog.
/// </summary>
/// <remarks>
/// A real raid on Woods matched one exit out of a screen full of them, which is what these are
/// about. Every rule here adds a comparison rather than lowering a threshold, so a line that
/// matched before still matches.
/// </remarks>
public sealed class ExtractLineMatcherTests
{
    private static readonly OcrTextNormalizer Normalizer = new();

    [Fact]
    public void Matches_a_name_the_catalog_qualifies_in_brackets()
    {
        // "Power Line Passage (Flare)" in the catalog; the screen prints the name alone.
        Assert.True(Score("Power line passage", "Power Line Passage (Flare)") >= 0.86);
    }

    [Fact]
    public void Matches_a_name_the_screen_prints_a_countdown_after()
    {
        Assert.Equal("Northern UN Roadblock", ExtractLineMatcher.StripTrailingMeasure("Northern UN Roadblock 00:35"));
        Assert.Equal("Old Station", ExtractLineMatcher.StripTrailingMeasure("Old Station 1:24:00"));
        Assert.Equal("Factory Gate", ExtractLineMatcher.StripTrailingMeasure("Factory Gate 240 m"));
        Assert.Equal("Outskirts", ExtractLineMatcher.StripTrailingMeasure("Outskirts   45s"));
    }

    [Fact]
    public void Keeps_a_number_that_is_part_of_the_name()
    {
        // Woods has ZB-014 and ZB-016, which differ only in their last digit. Stripping the
        // number would make them the same string and neither would ever be matched again.
        Assert.Equal("ZB-014", ExtractLineMatcher.StripTrailingMeasure("ZB-014"));
        Assert.Equal("ZB-016", ExtractLineMatcher.StripTrailingMeasure("ZB-016"));
        Assert.Equal("RUAF Roadblock", ExtractLineMatcher.StripTrailingMeasure("RUAF Roadblock"));
    }

    [Fact]
    public void Keeps_a_line_that_is_only_a_clock()
    {
        // The raid timer is read out of the lines that did not match an exit, so a line that
        // is nothing but a clock has to survive intact to get there.
        Assert.Equal("00:34:12", ExtractLineMatcher.StripTrailingMeasure("00:34:12"));
    }

    [Fact]
    public void Matches_a_name_the_screen_abbreviates()
    {
        // Every word of the shorter name, in the longer, in order.
        Assert.True(Score("UN Roadblock", "Northern UN Roadblock") >= 0.86);
        Assert.True(Score("Dead Mans Place", "Dead Man's Place") >= 0.86);
    }

    [Fact]
    public void Refuses_a_fragment_that_would_fit_more_than_one_exit()
    {
        // "Gate" is in Factory Gate and East Gate and half the maps have one of each. A
        // four-character fragment matching by containment sends somebody across the map.
        Assert.False(ExtractLineMatcher.Contains("gate", "factory gate"));
        Assert.False(ExtractLineMatcher.Contains("un", "northern un roadblock"));
    }

    [Fact]
    public void Refuses_words_in_the_wrong_order()
    {
        // An exit read backwards is a misreading. Treating it as a hit would be inventing a
        // match out of two words in common.
        Assert.False(ExtractLineMatcher.Contains("gate factory", "factory gate exit"));
        Assert.True(ExtractLineMatcher.Contains("factory gate", "factory gate exit"));
    }

    [Fact]
    public void Still_refuses_a_line_that_is_nothing_like_the_exit()
    {
        Assert.True(Score("Sniper Rifle SVD", "Northern UN Roadblock") < 0.65);
        Assert.True(Score("00:34:12", "Power Line Passage (Flare)") < 0.65);
    }

    [Fact]
    public void Scores_an_exact_reading_above_an_abbreviated_one()
    {
        var exact = Score("Northern UN Roadblock", "Northern UN Roadblock");
        var abbreviated = Score("UN Roadblock", "Northern UN Roadblock");

        Assert.Equal(1, exact);
        Assert.True(abbreviated < exact);
    }

    [Fact]
    public void Leaves_a_name_with_no_bracket_alone()
    {
        Assert.Null(ExtractLineMatcher.WithoutQualifier("Old Station"));
        Assert.Equal("Power Line Passage", ExtractLineMatcher.WithoutQualifier("Power Line Passage (Flare)"));
        // A name that is entirely a bracket has nothing to fall back to.
        Assert.Null(ExtractLineMatcher.WithoutQualifier("(Flare)"));
    }

    [Theory]
    // Verbatim from a real screen. The zero of the slot number reads as a letter O or an
    // at-sign far more often than as a digit, which is why the pattern accepts all three.
    [InlineData("EXFILO1 Friendship Bridge (Co-Op)", "Friendship Bridge (Co-Op)")]
    [InlineData("EXFIL@2 ZB-014", "ZB-014")]
    [InlineData("EXFIL@3 Bridge V-Ex", "Bridge V-Ex")]
    [InlineData("EXFIL05 Power Line Passage (Flare)", "Power Line Passage (Flare)")]
    [InlineData("EXFILO2 Outskirts", "Outskirts")]
    public void Takes_the_slot_label_off_the_front_of_an_exit(string line, string expected)
    {
        var (text, kind) = ExtractLineMatcher.StripRowPrefix(line);

        Assert.Equal(ExtractLineMatcher.RowKind.Extract, kind);
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("TRANSIT01 Transit to Factory", "Transit to Factory")]
    [InlineData("TRANSITO3 Transit to Lighthouse", "Transit to Lighthouse")]
    public void Knows_a_transit_from_an_exit(string line, string expected)
    {
        // A way to another map, which no extract catalog contains and never will.
        var (text, kind) = ExtractLineMatcher.StripRowPrefix(line);

        Assert.Equal(ExtractLineMatcher.RowKind.Transit, kind);
        Assert.Equal(expected, text);
    }

    [Theory]
    [InlineData("Find an extraction point")]
    [InlineData("0:12:28")]
    [InlineData("Power Line Passage (Flare)")]
    public void Leaves_a_line_with_no_slot_label_alone(string line)
    {
        var (text, kind) = ExtractLineMatcher.StripRowPrefix(line);

        Assert.Equal(ExtractLineMatcher.RowKind.Unlabelled, kind);
        Assert.Equal(line, text);
    }

    [Fact]
    public void Reports_a_row_whose_name_did_not_read_as_a_row_all_the_same()
    {
        // "The screen had a fifth exit and it did not read" is a different fact from "there
        // were four", and the panel is entitled to say which.
        var (text, kind) = ExtractLineMatcher.StripRowPrefix("EXFIL@2 ");

        Assert.Equal(ExtractLineMatcher.RowKind.Extract, kind);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Matches_the_rows_that_the_slot_label_was_sinking()
    {
        // Every one of these was on the screen of the raid that matched one exit out of five.
        // The prefix is eight characters against a name of six to eleven, so it dominated the
        // edit distance and only the longest name on the screen survived.
        Assert.True(Prefixed("EXFIL@3 Bridge V-Ex", "Bridge V-Ex") >= 0.65);
        Assert.True(Prefixed("EXFILO2 Outskirts", "Outskirts") >= 0.65);
        Assert.True(Prefixed("EXFILO1 Friendship Bridge (Co-Op)", "Friendship Bridge (Co-Op)") >= 0.65);

        // And the one that already worked still does.
        Assert.True(Prefixed("EXFIL05 Power Line Passage (Flare)", "Power Line Passage (Flare)") >= 0.65);
    }

    [Fact]
    public void Is_still_beaten_by_a_name_the_engine_corrupted()
    {
        // "ZB-014" read as "ZB-214" on the same screen. Six characters with one wrong is not
        // something a threshold can rescue, and pretending otherwise would match it to
        // whatever else is nearest.
        Assert.True(Prefixed("EXFIL@2 ZB-214", "ZB-014") < 0.86);
    }

    private static double Prefixed(string line, string catalogName) =>
        Score(ExtractLineMatcher.StripRowPrefix(line).Text, catalogName);

    private static double Score(string line, string catalogName) => ExtractLineMatcher.Score(
        Normalizer.NormalizeForLookup(ExtractLineMatcher.StripTrailingMeasure(line)),
        Normalizer.NormalizeForLookup(catalogName),
        Normalizer.NormalizeForLookup(ExtractLineMatcher.WithoutQualifier(catalogName)));
}
