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
                new(400_000, 1, new Confidence(0.9), "Graphics card 400 000"),
                new(290_000, 3, new Confidence(0.8), "Graphics card 290 000 (3)"),
                new(100_000, 1, new Confidence(0.9), "Graphics card 100 000"),
                new(320_000, null, new Confidence(0.7), "Graphics card 320 000"),
                new(400_000, 1, new Confidence(0.9), "Graphics card 400 000 duplicate")),
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
        Assert.Equal(["#1 best buy", "#2", "#3", "#4", "#5"], page.Rows.Select(row => row.RankLabel));
        Assert.Equal(["₽100,000 each", "₽290,000 each", "₽400,000 each", "₽400,000 each", "₽320,000 each"], page.Rows.Select(row => row.PriceLabel));

        // The engine chooses the stronger resale path rather than stopping at the first profitable one.
        Assert.Equal(FleaRowVerdict.ProfitOnFlea, page.Rows[0].Verdict);
        Assert.Contains("roubles more than it costs", page.Rows[0].WhyLabel, StringComparison.Ordinal);
        // Under what the average returns after its fee: pays to resell. Three units, priced each.
        Assert.Equal(FleaRowVerdict.ProfitOnFlea, page.Rows[1].Verdict);
        Assert.Equal("₽290,000 each", page.Rows[1].PriceLabel);
        Assert.Equal("3 units · ₽870,000 for the lot", page.Rows[1].StackLabel);
        Assert.Equal(FleaRowVerdict.OverAverage, page.Rows[2].Verdict);
        Assert.False(page.Rows[2].IsGoodBuy);
        // The 70%-sure row is below the shared policy's confidence floor and is ranked for review.
        Assert.Equal(FleaRowVerdict.Unknown, page.Rows[4].Verdict);
        Assert.Contains("ambiguous", page.Rows[4].WhyLabel, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("count not read", page.Rows[4].StackLabel);
        Assert.All(read.Rows, row => Assert.Equal("recommendation-274.2", row.Recommendation.RulesetVersion));
    }

    [Fact]
    public async Task OfflineOrOldComparisonPricesAreSaidBesideTheRows()
    {
        var catalog = new LootScanFactFixtures.Catalog();
        var read = await new FleaCaptureHandoff(catalog, catalog).BuildAsync(
            Request(
                [new("gpu", "Graphics card", new Confidence(0.91), "Graphics card")],
                new CaptureFleaListing(100_000, 1, new Confidence(0.9), "row")),
            CancellationToken.None);
        read = read with { PriceUpdatedUtc = Now.AddDays(-2) };

        var offline = new FleaScanViewModel(read, CultureInfo.InvariantCulture, offline: true, timeProvider: new FixedClock(Now));
        var online = new FleaScanViewModel(read, CultureInfo.InvariantCulture, offline: false, timeProvider: new FixedClock(Now));

        Assert.StartsWith("Offline · comparison prices are from", offline.MarketDataNote, StringComparison.Ordinal);
        Assert.StartsWith("Price comparison is over a day old", online.MarketDataNote, StringComparison.Ordinal);
        Assert.True(offline.HasMarketDataNote);
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
    public async Task ConditionCurrencyAndAlternativeIdentitiesReachTheEngineResult()
    {
        var catalog = new LootScanFactFixtures.Catalog();
        var condition = new ItemConditionReading(ItemConditionKind.Uses, 3, 5);
        var read = await new FleaCaptureHandoff(catalog, catalog).BuildAsync(
            Request(
                [
                    new("gpu", "Graphics card", new Confidence(0.91), "Graphics card"),
                    new("bolts", "Bolts", new Confidence(0.52), "Bolts"),
                ],
                new CaptureFleaListing(
                    99_900,
                    1,
                    new Confidence(0.93),
                    "uses 3/5 | 450 EUR",
                    "EUR",
                    450,
                    222,
                    new DataProvenance("fixture currency", Now.AddMinutes(-5)),
                    condition)),
            CancellationToken.None);

        var row = Assert.Single(read.Rows);
        Assert.Equal(condition, row.Condition);
        Assert.Equal("EUR", row.CurrencyCode);
        Assert.Equal(450, row.OriginalPrice);
        Assert.Equal(222, row.CurrencyRateRoubles);
        Assert.Equal(RecommendationAction.Take, row.Recommendation.Decision.Value!.Action);
        Assert.Contains("60 % condition", row.Recommendation.Decision.Value.Reasons.Single(
            reason => reason.Category == RecommendationReasonCategory.Economics).Explanation, StringComparison.Ordinal);
        var alternative = Assert.Single(row.Alternatives);
        Assert.Equal("bolts", alternative.ItemId);
        Assert.Equal("recommendation-274.2", alternative.Recommendation.RulesetVersion);
        Assert.Equal(RecommendationAction.Review, alternative.Recommendation.Decision.Value!.Action);

        var page = new FleaScanViewModel(read, CultureInfo.InvariantCulture);
        Assert.Equal("€450 · ₽99,900 each", Assert.Single(page.Rows).PriceLabel);
        Assert.Equal("Uses 3/5", page.Rows[0].ConditionLabel);
        Assert.Contains("Bolts — review", page.Rows[0].AlternativesLabel, StringComparison.Ordinal);
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

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
