using System.Globalization;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// #284: the flea rows a player photographed are priced against the catalog, and each says
/// whether buying it would pay. The fixture catalog's Graphics card averages 337,352 on the
/// flea, has a base price of 250,000 and sells to Therapist for 120,000.
/// </summary>
public sealed class FleaCaptureHandoffTests
{
    private static readonly DateTimeOffset Now = LootScanFactFixtures.Now;

    [Fact]
    public async Task EachRowStandsBesideTheTraderPriceAndTheAverageAfterItsFee()
    {
        var catalog = new LootScanFactFixtures.Catalog();
        var handoff = new FleaCaptureHandoff(catalog, catalog);
        FleaScanResult? read = null;
        handoff.ListingsRead += (_, scan) => read = scan;

        await handoff.AcceptAsync(
            Request(
                [new("gpu", "Graphics card", new Confidence(0.91), "Graphics card")],
                new(100_000, 1, new Confidence(0.9), "Graphics card 100 000"),
                new(290_000, 3, new Confidence(0.8), "Graphics card 290 000 (3)"),
                new(320_000, null, new Confidence(0.7), "Graphics card 320 000"),
                new(400_000, 1, new Confidence(0.9), "Graphics card 400 000")),
            CancellationToken.None);

        Assert.NotNull(read);
        var fee = FleaMarketFee.Calculate(250_000, 337_352, 1, LootScanFactFixtures.Rates);
        Assert.Equal("Graphics card", read.ItemName);
        Assert.Equal(120_000, read.TraderRoubles);
        Assert.Equal(337_352, read.Average24HourRoubles);
        Assert.Equal(fee, read.AverageFeeRoubles);

        var page = new FleaScanViewModel(read, CultureInfo.InvariantCulture);
        Assert.Equal("Offers for Graphics card", page.Heading);
        Assert.Equal("Therapist pays ₽120,000", page.TraderLabel);
        Assert.Contains("24 h average ₽337,352", page.AverageLabel, StringComparison.Ordinal);
        Assert.Contains($"after a ₽{fee.ToString("N0", CultureInfo.InvariantCulture)} fee", page.AverageLabel, StringComparison.Ordinal);

        // Cheaper than the trader pays: certain profit.
        Assert.Equal(FleaRowVerdict.ProfitToTrader, page.Rows[0].Verdict);
        Assert.Equal("Therapist pays ₽20,000 more than this", page.Rows[0].WhyLabel);
        // Under what the average returns after its fee: pays to resell. Three units, priced each.
        Assert.Equal(FleaRowVerdict.ProfitOnFlea, page.Rows[1].Verdict);
        Assert.Equal("₽290,000 each", page.Rows[1].PriceLabel);
        Assert.Equal("3 units · ₽870,000 for the lot", page.Rows[1].StackLabel);
        // Under the average, but the fee eats the difference.
        Assert.Equal(337_352 - fee < 320_000, page.Rows[2].Verdict == FleaRowVerdict.UnderAverage);
        Assert.Equal("count not read", page.Rows[2].StackLabel);
        Assert.Equal(FleaRowVerdict.OverAverage, page.Rows[3].Verdict);
        Assert.False(page.Rows[3].IsGoodBuy);
    }

    [Fact]
    public async Task RowsWhoseItemWasNotLegibleAreShownWithoutAComparison()
    {
        var catalog = new LootScanFactFixtures.Catalog();
        var read = await new FleaCaptureHandoff(catalog, catalog).BuildAsync(
            Request([], new CaptureFleaListing(52_000, 2, new Confidence(0.8), "52 000 (2)")),
            CancellationToken.None);

        var page = new FleaScanViewModel(read, CultureInfo.InvariantCulture);

        Assert.Null(read.ItemId);
        Assert.Equal("Offers you photographed", page.Heading);
        Assert.Equal(FleaRowVerdict.Unknown, Assert.Single(page.Rows).Verdict);
        Assert.Equal("No comparison", page.Rows[0].VerdictLabel);
    }

    [Fact]
    public async Task ACaptureWithNoLegibleRowsPublishesNothing()
    {
        var catalog = new LootScanFactFixtures.Catalog();
        var handoff = new FleaCaptureHandoff(catalog, catalog);
        var published = 0;
        handoff.ListingsRead += (_, _) => published++;

        await handoff.AcceptAsync(Request([new("gpu", "Graphics card", new Confidence(0.91), "line")]), CancellationToken.None);

        Assert.Equal(0, published);
    }

    [Theory]
    [InlineData(ScanIntent.Flea, ScanContext.Unknown, true)]
    [InlineData(ScanIntent.Auto, ScanContext.FleaListings, true)]
    [InlineData(ScanIntent.Auto, ScanContext.Container, false)]
    [InlineData(ScanIntent.Loot, ScanContext.FleaListings, false)]
    public void FleaRowsAreParsedForAnArmedFleaIntentOrADetectedFleaScreen(ScanIntent intent, ScanContext detected, bool expected)
    {
        Assert.Equal(expected, CaptureRecognitionPipeline.ReadsFleaRows(intent, detected));
    }

    private static CaptureHandoffRequest Request(CaptureIdentifiedItem[] identified, params CaptureFleaListing[] rows)
    {
        var provenance = new EvidenceProvenance(
            EvidenceSourceClass.GameWrittenScreenshot,
            "fixture://flea",
            Now,
            EvidenceConfidence.Unscored,
            new ProducerIdentity("fixture", "1"));
        return new(
            new CaptureSessionId(Guid.NewGuid()),
            "flea-frame",
            new CaptureAnalysis("result", RecognizedContext.Flea, false, true, null, new Confidence(0.9), Identified: identified, FleaListings: rows),
            CaptureContextMetadata.Empty,
            CaptureCorrelationId.New(),
            CaptureSourceKind.GameWrittenScreenshot,
            Now,
            Now,
            null,
            CaptureDeliveryKind.WatchedFile,
            provenance,
            0,
            CaptureReviewAction.UseDetected,
            ScanIntent.Flea,
            new CaptureCorrection(CaptureReviewAction.UseDetected, ScanIntent.Flea, RecognizedContext.Flea, 0, Now, "fixture"));
    }
}
