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

    private static double Score(string line, string catalogName) => ExtractLineMatcher.Score(
        Normalizer.NormalizeForLookup(ExtractLineMatcher.StripTrailingMeasure(line)),
        Normalizer.NormalizeForLookup(catalogName),
        Normalizer.NormalizeForLookup(ExtractLineMatcher.WithoutQualifier(catalogName)));
}
