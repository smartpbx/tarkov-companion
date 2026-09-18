using TarkovCompanion.Application.Services;

namespace TarkovCompanion.UnitTests;

public sealed class TextMatchingTests
{
    [Theory]
    [InlineData("Graphics Card", "graphics card")]
    [InlineData("5.45×39-mm", "5 45 39 mm")]
    [InlineData("Café key", "cafe key")]
    public void Normalize_RemovesPresentationDifferences(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void Similarity_RanksOcrTypoAboveUnrelatedItem()
    {
        var likely = FuzzyMatcher.Similarity("Graphics Card", "Graphlcs Card");
        var unrelated = FuzzyMatcher.Similarity("Graphics Card", "Bolts");

        Assert.True(likely > 0.85);
        Assert.True(likely > unrelated);
    }

    [Theory]
    [InlineData("Graphics card", "AP")]
    [InlineData("Salewa first aid kit", "M4")]
    public void ShortNameSimilarity_IgnoresATinyShortNameThatOnlyAppearsInsideTheQuery(string query, string shortName) =>
        Assert.Equal(0, FuzzyMatcher.ShortNameSimilarity(query, shortName));

    [Theory]
    [InlineData("Graphics card", "Car")]
    [InlineData("Salewa first aid kit", "Sal")]
    public void ShortNameSimilarity_DoesNotCountAShortNameThatIsOnlyPartOfAWordInTheQuery(string query, string shortName) =>
        Assert.True(FuzzyMatcher.ShortNameSimilarity(query, shortName) < 0.6);

    [Theory]
    [InlineData("gpu case", "GPU")]
    [InlineData("salewa first aid kit", "Salewa")]
    public void ShortNameSimilarity_CountsAShortNameThatIsAWholeWordOfTheQuery(string query, string shortName) =>
        Assert.True(FuzzyMatcher.ShortNameSimilarity(query, shortName) >= 0.82);

    [Fact]
    public void ShortNameSimilarity_StillMatchesATinyShortNameTypedInFull() =>
        Assert.Equal(1, FuzzyMatcher.ShortNameSimilarity("ap", "AP"));

    [Fact]
    public void ShortNameSimilarity_StillForgivesATypoOfAShortName() =>
        Assert.True(FuzzyMatcher.ShortNameSimilarity("salwa", "Salewa") > 0.8);

    [Fact]
    public void ShortNameSimilarity_LeavesLongerShortNamesToTheGeneralMatcher() =>
        Assert.Equal(
            FuzzyMatcher.Similarity("gpu case", "GPU"),
            FuzzyMatcher.ShortNameSimilarity("gpu case", "GPU"));
}
