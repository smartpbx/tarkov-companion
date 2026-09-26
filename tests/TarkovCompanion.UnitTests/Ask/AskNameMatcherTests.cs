using TarkovCompanion.Application.Services.Ask;

namespace TarkovCompanion.UnitTests.Ask;

public sealed class AskNameMatcherTests
{
    // The 2026-09-14 catalog's Gunsmith quests, as named there.
    private static readonly string[] Gunsmith =
    [
        "Gunsmith - AK-105", "Gunsmith - HK MP5", "Gunsmith - M4A1", "Gunsmith Master - Part 1",
        "Gunsmith Master - Part 5", "Gunsmith Master - Part 10", "Gunsmith Master - Part 11", "Gunsmith Master - Part 12",
    ];

    [Fact]
    public void Gunsmith_5_is_Gunsmith_Master_Part_5()
    {
        var ranked = AskNameMatcher.Rank("gunsmith 5", Gunsmith, name => name);

        Assert.Equal("Gunsmith Master - Part 5", ranked[0].Name);
        Assert.True(ranked[0].Score >= AskNameMatcher.Accept);
        Assert.True(ranked[1].Score < AskNameMatcher.Accept);
    }

    [Fact]
    public void A_number_matches_only_itself_so_1_is_not_10_11_or_12()
    {
        var ranked = AskNameMatcher.Rank("gunsmith 1", Gunsmith, name => name);

        Assert.Equal("Gunsmith Master - Part 1", ranked[0].Name);
        Assert.All(ranked.Skip(1), match => Assert.True(match.Score < AskNameMatcher.Accept, match.Name));
    }

    [Theory]
    [InlineData("gunsmth 5")]
    [InlineData("gunsmiths 5")]
    [InlineData("gun 5")]
    public void Misspelt_plural_or_prefix_words_still_match(string typed)
    {
        Assert.Equal("Gunsmith Master - Part 5", AskNameMatcher.Rank(typed, Gunsmith, name => name)[0].Name);
    }

    [Fact]
    public void A_station_matches_without_its_level()
    {
        string[] stations = ["Lavatory", "Workbench", "Water Collector", "Medstation"];

        Assert.Equal("Lavatory", AskNameMatcher.Rank("lavatory", stations, name => name)[0].Name);
        Assert.Equal("Water Collector", AskNameMatcher.Rank("water collector", stations, name => name)[0].Name);
    }

    [Fact]
    public void A_name_missing_a_typed_word_is_never_accepted()
    {
        Assert.True(AskNameMatcher.Score("gunsmith 99", "Gunsmith Master - Part 5") < AskNameMatcher.Accept);
        Assert.True(AskNameMatcher.Score("delivery from the past", "Gunsmith Master - Part 5") < AskNameMatcher.Accept);
    }
}
