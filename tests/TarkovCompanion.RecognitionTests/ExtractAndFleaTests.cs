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

        Assert.Equal(2, result.Extracts.Count);
        Assert.Contains(result.Extracts, extract => extract.ExtractId == "road-to-customs");
        Assert.Contains(result.Extracts, extract => extract.ExtractId == "dorms-v-ex");
        Assert.All(result.Extracts, extract =>
        {
            Assert.True(extract.Confidence.Value >= 0.70);
            Assert.Contains("observedUtc=", extract.Source, StringComparison.Ordinal);
        });
        Assert.Empty(result.UnmatchedLines);
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
