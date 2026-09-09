using TarkovCompanion.Core.Common;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class TextResolutionTests
{
    private readonly OcrTextNormalizer _normalizer = new();

    [Fact]
    public void LookupNormalizationRepairsCommonAlphaNumericConfusions()
    {
        Assert.Equal("graphics card", _normalizer.NormalizeForLookup(" Gr@ph1cs-C@rd "));
        Assert.Equal("objectives", _normalizer.NormalizeForLookup("0BJECTlVES"));
        Assert.Equal("189990", _normalizer.NormalizeNumber("I89 99O ₽"));
    }

    [Fact]
    public void ResolverAutoSelectsOnlyAtNinetyPercentOrHigher()
    {
        var resolver = CreateResolver();

        var exact = resolver.Resolve("Gr@phics C@rd", new Confidence(0.95));
        var fuzzy = resolver.Resolve("Graphics Cr", new Confidence(0.90));
        var weak = resolver.Resolve("Graphic", new Confidence(0.90));

        Assert.Equal(RecognitionDecision.AutoSelected, exact.Decision);
        Assert.True(exact.Best?.Confidence.Value >= 0.90);
        Assert.Equal(RecognitionDecision.Ambiguous, fuzzy.Decision);
        Assert.InRange(fuzzy.Best!.Confidence.Value, 0.70, 0.899999);
        Assert.Equal(RecognitionDecision.Candidate, weak.Decision);
        Assert.InRange(weak.Best!.Confidence.Value, 0.45, 0.699999);
    }

    [Theory]
    [InlineData(0.90, RecognitionDecision.AutoSelected)]
    [InlineData(0.899999, RecognitionDecision.Ambiguous)]
    [InlineData(0.70, RecognitionDecision.Ambiguous)]
    [InlineData(0.699999, RecognitionDecision.Candidate)]
    [InlineData(0.45, RecognitionDecision.Candidate)]
    [InlineData(0.449999, RecognitionDecision.NoMatch)]
    public void PolicyClassifiesBoundaryValues(double value, RecognitionDecision expected)
    {
        Assert.Equal(expected, RecognitionPolicy.Classify(new Confidence(value)));
    }

    private static FuzzyCanonicalItemResolver CreateResolver() => new(
    [
        new CanonicalItemReference("graphics-card", "Graphics Card", ["GPU"]),
        new CanonicalItemReference("power-cord", "Power Cord"),
        new CanonicalItemReference("wires", "Wires"),
    ]);
}
