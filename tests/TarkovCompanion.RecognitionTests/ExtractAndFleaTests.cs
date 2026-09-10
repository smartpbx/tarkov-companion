using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

public sealed class ExtractAndFleaTests
{
    [Fact]
    public async Task ExtractMatcherUsesOnlyCurrentMapCanonicalExtracts()
    {
        var fixture = SyntheticFixtureLoader.LoadScenes().Single(scene => scene.ExpectedContext == "ExtractList");
        var engine = new FixtureOcrEngine([fixture.ToOcrScene()]);
        var service = new ExtractRecognitionService(engine);
        var provenance = new DataProvenance("fixture", fixture.CreateImage().CapturedUtc);
        var map = new MapDefinition(
            "customs",
            "Customs",
            null,
            null,
            [],
            [
                new MapExtract("road-to-customs", "customs", "Road to Customs", null, null, provenance),
                new MapExtract("dorms-v-ex", "customs", "Dorms V-EX", null, null, provenance),
            ],
            null,
            provenance);

        var result = await service.RecognizeAsync(fixture.CreateImage(), map, CancellationToken.None);

        Assert.True(result.ProviderAvailable);
        Assert.Single(result.Extracts);
        Assert.Contains(result.Extracts, extract => extract.ExtractId == "road-to-customs");
        Assert.DoesNotContain(result.Extracts, extract => extract.ExtractId == "dorms-v-ex");
        Assert.Contains(result.Observations, extract =>
            extract.ExtractId == "dorms-v-ex" && extract.Status == ExtractStatus.Closed);
        Assert.All(result.Extracts, extract =>
        {
            Assert.True(extract.Confidence.Value >= 0.70);
            Assert.Contains("observedUtc=", extract.Source, StringComparison.Ordinal);
        });
        Assert.Empty(result.UnmatchedLines);
        Assert.Empty(result.AmbiguousLines);
    }

    [Fact]
    public async Task NearTieExtractNamesRemainAmbiguous()
    {
        var image = CreateImage(800, 600) with { Source = "fixture://extract-near-tie" };
        var engine = new FixtureOcrEngine(
        [
            new FixtureOcrScene(
                image.Source,
                [new OcrLine("ZB-101", new PixelRect(400, 100, 120, 24), new Confidence(0.96))])
        ]);
        var provenance = new DataProvenance("fixture", image.CapturedUtc);
        var map = new MapDefinition(
            "customs",
            "Customs",
            null,
            null,
            [],
            [
                new MapExtract("zb-1011", "customs", "ZB-1011", null, null, provenance),
                new MapExtract("zb-1012", "customs", "ZB-1012", null, null, provenance),
            ],
            null,
            provenance);

        var result = await new ExtractRecognitionService(engine)
            .RecognizeAsync(image, map, CancellationToken.None);

        Assert.Empty(result.Extracts);
        Assert.Empty(result.Observations);
        Assert.Equal(["ZB-101"], result.AmbiguousLines);
    }

    [Fact]
    public async Task ExtractStatusModelPreservesActiveClosedPendingAndUnknown()
    {
        var image = CreateImage(1000, 700) with { Source = "fixture://extract-statuses" };
        var lines = new[]
        {
            new OcrLine("North ACTIVE", new(500, 100, 150, 20), new Confidence(0.96)),
            new OcrLine("South CLOSED", new(500, 150, 150, 20), new Confidence(0.96)),
            new OcrLine("East PENDING", new(500, 200, 150, 20), new Confidence(0.96)),
            new OcrLine("West UNKNOWN", new(500, 250, 150, 20), new Confidence(0.96)),
        };
        var engine = new FixtureOcrEngine([new(image.Source, lines)]);
        var provenance = new DataProvenance("fixture", image.CapturedUtc);
        var map = new MapDefinition(
            "test",
            "Test",
            null,
            null,
            [],
            new[] { "North", "South", "East", "West" }
                .Select(name => new MapExtract(name.ToLowerInvariant(), "test", name, null, null, provenance))
                .ToArray(),
            null,
            provenance);

        var result = await new ExtractRecognitionService(engine)
            .RecognizeAsync(image, map, CancellationToken.None);

        Assert.Equal(["north"], result.Extracts.Select(extract => extract.ExtractId));
        Assert.Contains(result.Observations, value => value.ExtractId == "north" && value.Status == ExtractStatus.Active);
        Assert.Contains(result.Observations, value => value.ExtractId == "south" && value.Status == ExtractStatus.Closed);
        Assert.Contains(result.Observations, value => value.ExtractId == "east" && value.Status == ExtractStatus.Pending);
        Assert.Contains(result.Observations, value => value.ExtractId == "west" && value.Status == ExtractStatus.Unknown);
    }

    [Fact]
    public void FleaParserReturnsOnlyVisibleRoubleRows()
    {
        var image = CreateImage(1920, 1080);
        var ocr = new OcrResult(
        [
            new OcrLine("189 999 ₽ x3", new PixelRect(1200, 300, 250, 24), new Confidence(0.92)),
            new OcrLine("75,500 RUB", new PixelRect(1200, 370, 220, 24), new Confidence(0.86)),
            new OcrLine("not a listing", new PixelRect(1200, 440, 220, 24), new Confidence(0.99)),
            new OcrLine("12 000 ₽", new PixelRect(2100, 500, 200, 24), new Confidence(0.99)),
        ], TimeSpan.Zero, "fixture-ocr");

        var listings = new FleaListingParser().ParseVisible(ocr, image);

        Assert.Collection(
            listings,
            listing =>
            {
                Assert.Equal(189_999, listing.PriceRoubles);
                Assert.Equal(3, listing.Quantity);
            },
            listing =>
            {
                Assert.Equal(75_500, listing.PriceRoubles);
                Assert.Null(listing.Quantity);
            });
    }

    private static CapturedImage CreateImage(int width, int height) => new(
        new byte[checked(width * height)],
        width,
        height,
        width,
        PixelFormat.Gray8,
        new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
        "fixture://flea");
}
