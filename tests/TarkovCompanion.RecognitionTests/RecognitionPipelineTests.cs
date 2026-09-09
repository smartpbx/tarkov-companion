using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class RecognitionPipelineTests
{
    [Fact]
    public async Task SingleItemPipelineAutoSelectsNormalizedCanonicalItem()
    {
        var fixture = SyntheticFixtureLoader.LoadScenes().Single(scene => scene.ExpectedContext == "SingleItem");
        var engine = new FixtureOcrEngine([fixture.ToOcrScene()]);
        var service = new RecognitionService(
            new OcrCoordinator(engine, new ScanContextDetector()),
            new FuzzyCanonicalItemResolver(
            [
                new CanonicalItemReference("graphics-card", "Graphics Card", ["GPU"]),
                new CanonicalItemReference("power-cord", "Power Cord"),
            ]));

        var result = await service.RecognizeAsync(fixture.CreateImage(), CancellationToken.None);

        Assert.Equal(ScanContext.SingleItem, result.Context);
        Assert.Equal("graphics-card", result.Selected?.CanonicalId);
        Assert.Null(result.DiagnosticCode);
        Assert.Equal(fixture.CreateImage().CapturedUtc, result.ObservedUtc);
    }

    [Fact]
    public async Task UnknownSceneReturnsNoStateEvenWhenIconMatcherWouldMatch()
    {
        var fixture = SyntheticFixtureLoader.LoadScenes().Single(scene => scene.ExpectedContext == "Unknown");
        var engine = new FixtureOcrEngine([fixture.ToOcrScene()]);
        var service = new RecognitionService(
            new OcrCoordinator(engine, new ScanContextDetector()),
            new FuzzyCanonicalItemResolver([new CanonicalItemReference("item", "Item")]),
            new AlwaysMatchingIconMatcher());

        var result = await service.RecognizeAsync(fixture.CreateImage(), CancellationToken.None);

        Assert.Equal(ScanContext.Unknown, result.Context);
        Assert.Empty(result.Candidates);
        Assert.Null(result.Selected);
        Assert.Equal("context_unknown", result.DiagnosticCode);
    }

    [Fact]
    public async Task KnownSceneUsesIconFallbackWhenOcrHasNoViableItem()
    {
        var fixture = SyntheticFixtureLoader.LoadScenes().Single(scene => scene.ExpectedContext == "SingleItem");
        var engine = new FixtureOcrEngine([fixture.ToOcrScene()]);
        var service = new RecognitionService(
            new OcrCoordinator(engine, new ScanContextDetector()),
            new FuzzyCanonicalItemResolver([new CanonicalItemReference("fallback", "Fallback Item")]),
            new AlwaysMatchingIconMatcher());

        var result = await service.RecognizeAsync(fixture.CreateImage(), CancellationToken.None);

        Assert.Equal("icon-match", result.Selected?.CanonicalId);
        Assert.Null(result.DiagnosticCode);
    }

    private sealed class AlwaysMatchingIconMatcher : IIconMatcher
    {
        public Task<IReadOnlyList<RecognitionCandidate>> MatchAsync(
            CapturedImage image,
            int limit,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<RecognitionCandidate> candidates =
            [
                new RecognitionCandidate(
                    "icon-match",
                    "Icon Match",
                    Confidence.Certain,
                    "should-never-be-used"),
            ];
            return Task.FromResult(candidates);
        }
    }
}
