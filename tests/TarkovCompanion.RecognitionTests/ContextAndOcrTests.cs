using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class ContextAndOcrTests
{
    [Fact]
    public void HandTranscribedTaskFramesClassifyWithoutStealingNearbyScreens()
    {
        var fixtures = SyntheticFixtureLoader.LoadTaskContextScenes();
        var detector = new ScanContextDetector();

        Assert.Contains(fixtures, fixture => fixture.Name == "tasks-side-list");
        Assert.Contains(fixtures, fixture => fixture.Name == "tasks-story-chapter");
        Assert.Contains(fixtures, fixture => fixture.Name == "negative-flea");
        Assert.Contains(fixtures, fixture => fixture.Name == "negative-stash");
        Assert.Contains(fixtures, fixture => fixture.Name == "negative-inventory");
        Assert.Contains(fixtures, fixture => fixture.Name == "negative-health-character");

        foreach (var fixture in fixtures)
        {
            var result = detector.Detect(
                fixture.CreateImage(),
                new(fixture.ToOcrScene().Lines, TimeSpan.Zero, "hand-transcribed fixture"));

            Assert.Equal(Enum.Parse<ScanContext>(fixture.ExpectedContext), result.Context);
        }
    }

    [Fact]
    public async Task ScriptedPostOcrFixturesClassifyWithoutFabricatingUnknownState()
    {
        var fixtures = SyntheticFixtureLoader.LoadScenes();
        var engine = new FixtureOcrEngine(fixtures.Select(fixture => fixture.ToOcrScene()));
        var detector = new ScanContextDetector();

        Assert.Contains(fixtures, fixture => fixture.Height == 1080 && fixture.Scale == 1.0);
        Assert.Contains(fixtures, fixture => fixture.Height == 1440 && fixture.Scale == 1.25);
        Assert.Contains(fixtures, fixture => fixture.Height == 2160 && fixture.Scale == 1.5);
        Assert.All(fixtures, fixture => Assert.True(fixture.Noise > 0));

        foreach (var fixture in fixtures)
        {
            var image = fixture.CreateImage();
            var ocr = await engine.RecognizeAsync(
                image,
                new OcrRequest(ScanContext.Unknown),
                CancellationToken.None);
            var detection = detector.Detect(image, ocr);

            Assert.Equal(Enum.Parse<ScanContext>(fixture.ExpectedContext), detection.Context);
            Assert.InRange(detection.EstimatedUiScale, 0.50, 2.50);
            if (detection.Context != ScanContext.Unknown)
            {
                Assert.NotEmpty(detection.Anchors);
                Assert.All(detection.Anchors, anchor =>
                    Assert.Matches("live-(unvalidated|validated/)", anchor.Provenance));
            }
        }
    }

    [Fact]
    public async Task FixtureEngineIsDeterministicAndFiltersRequestedRegion()
    {
        var fixture = SyntheticFixtureLoader.LoadScenes().Single(scene => scene.ExpectedContext == "SingleItem");
        var image = fixture.CreateImage();
        var engine = new FixtureOcrEngine([fixture.ToOcrScene()]);
        var region = new PixelRect(650, 200, 400, 100);

        var first = await engine.RecognizeAsync(
            image,
            new OcrRequest(ScanContext.SingleItem, region),
            CancellationToken.None);
        var second = await engine.RecognizeAsync(
            image,
            new OcrRequest(ScanContext.SingleItem, region),
            CancellationToken.None);

        Assert.Equal(first.Engine, second.Engine);
        Assert.Equal(first.Duration, second.Duration);
        Assert.Equal(first.Lines, second.Lines);
        Assert.Collection(first.Lines, line => Assert.Equal("Gr@phics C@rd", line.Text));
        Assert.Equal(TimeSpan.Zero, first.Duration);
    }

    [Fact]
    public async Task CoordinatorDoesNotRunAContextualPassForUnknownScenes()
    {
        var image = CreateSmallImage("fixture://unknown");
        var engine = new RecordingOcrEngine(
            new OcrResult(
                [new OcrLine("CHARACTER", new PixelRect(10, 10, 100, 20), new Confidence(0.8))],
                TimeSpan.Zero,
                "recording"));
        var coordinator = new OcrCoordinator(engine, new ScanContextDetector());

        var result = await coordinator.RecognizeAsync(image, CancellationToken.None);

        Assert.Equal(ScanContext.Unknown, result.Detection.Context);
        Assert.Single(engine.Requests);
    }

    [Fact]
    public async Task CoordinatorUsesDetectedContextForSecondProviderNeutralPass()
    {
        var image = CreateSmallImage("fixture://single");
        var engine = new RecordingOcrEngine(
            new OcrResult(
            [
                new OcrLine("INSPECT", new PixelRect(40, 20, 80, 20), new Confidence(0.99)),
                new OcrLine("WEIGHT", new PixelRect(40, 90, 80, 20), new Confidence(0.95)),
            ], TimeSpan.Zero, "recording"));
        var coordinator = new OcrCoordinator(engine, new ScanContextDetector());

        var result = await coordinator.RecognizeAsync(image, CancellationToken.None);

        Assert.Equal(ScanContext.SingleItem, result.Detection.Context);
        Assert.Equal(2, engine.Requests.Count);
        Assert.Equal(ScanContext.SingleItem, engine.Requests[1].Context);
        Assert.NotNull(engine.Requests[1].Region);
    }

    [Fact]
    public async Task AnchorRelativeRegionKeepsFullFrameCandidatesOutsideDraggablePanelGuess()
    {
        var image = new CapturedImage(
            new byte[1000 * 600],
            1000,
            600,
            1000,
            PixelFormat.Gray8,
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
            "fixture://edge-panel");
        var engine = new FixtureOcrEngine(
        [
            new FixtureOcrScene(
                image.Source,
                [
                    new OcrLine("INSPECT", new PixelRect(890, 30, 90, 20), new Confidence(0.99)),
                    new OcrLine("WEIGHT", new PixelRect(900, 300, 80, 20), new Confidence(0.95)),
                    new OcrLine("Graphics Card", new PixelRect(40, 100, 180, 24), new Confidence(0.93)),
                ])
        ]);
        var coordinator = new OcrCoordinator(engine, new ScanContextDetector());

        var result = await coordinator.RecognizeAsync(image, CancellationToken.None);

        Assert.Equal(ScanContext.SingleItem, result.Detection.Context);
        Assert.True(result.UsedFullFrameSupplement);
        Assert.Contains(result.Candidates.Lines, line => line.Text == "Graphics Card");
        Assert.DoesNotContain(result.Contextual.Lines, line => line.Text == "Graphics Card");
    }

    private static CapturedImage CreateSmallImage(string source) => new(
        new byte[200 * 120],
        200,
        120,
        200,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
        source);

    private sealed class RecordingOcrEngine(OcrResult result) : IOcrEngine
    {
        public List<OcrRequest> Requests { get; } = [];

        public Task<OcrResult> RecognizeAsync(
            CapturedImage image,
            OcrRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(result);
        }
    }
}
