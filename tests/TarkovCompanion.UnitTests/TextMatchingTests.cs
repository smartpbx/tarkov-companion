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
}
