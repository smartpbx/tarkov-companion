using System.Globalization;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.Services.V2.Capture;
using TarkovCompanion.App.ViewModels.V2.Intel;
using TarkovCompanion.App.ViewModels.V2.StashScan;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>#290: the desktop's Stash scan and flea screen, as the tablet's review cards.</summary>
public sealed class TabletCaptureReviewBuilderTests
{
    private static readonly DateTimeOffset Now = LootScanFactFixtures.Now;

    [Fact]
    public async Task AFleaScreenIsReviewedInIntelsOwnOrderWordsAndConfidence()
    {
        var catalog = new LootScanFactFixtures.Catalog();
        var handoff = new FleaCaptureHandoff(catalog, catalog);
        FleaScanResult? read = null;
        handoff.ListingsRead += (_, scan) => read = scan;
        await handoff.AcceptAsync(
            Request(
                [new("gpu", "Graphics card", new Confidence(0.91), "Graphics card")],
                new(400_000, 1, new Confidence(0.9), "Graphics card 400 000"),
                new(100_000, 2, new Confidence(0.8), "Graphics card 100 000 (2)")),
            CancellationToken.None);
        var page = new FleaScanViewModel(read!, CultureInfo.InvariantCulture);

        var review = TabletCaptureReviewBuilder.FromFlea(page);

        Assert.Equal(TabletCaptureReviewBuilder.FleaKind, review.Kind);
        Assert.Equal(read!.ObservedUtc, review.SeenUtc);
        Assert.Equal("Offers for Graphics card", review.Heading);
        Assert.Equal(page.SummaryLabel, review.Summary);
        Assert.Contains(page.TraderLabel, review.Note, StringComparison.Ordinal);
        Assert.Equal(page.Rows.Select(row => row.VerdictLabel), review.Rows.Select(row => row.Tag));
        Assert.Equal(page.Rows.Select(row => row.ConfidenceLabel), review.Rows.Select(row => row.Confidence));
        Assert.StartsWith("#1 best buy · ", review.Rows[0].Name, StringComparison.Ordinal);
        Assert.Equal(
            page.Rows.Select(row => TabletCaptureReviewBuilder.FleaTagKind(row.Verdict)),
            review.Rows.Select(row => row.TagKind));
        Assert.Equal(0, review.HiddenRows);
    }

    [Fact]
    public void AStashScanKeepsThePagesGroupsCountsAndEvidence()
    {
        var review = TabletCaptureReviewBuilder.FromStash(
            Guid.Parse("6f3c9a51-0000-4000-8000-000000000001"),
            Now,
            "212 named · 3 unknown",
            "₽18.6M",
            [
                new StashPlanTileViewModel(StashPlanGroup.Keep, "Keep", 1, true),
                new StashPlanTileViewModel(StashPlanGroup.Sell, "Sell", 0, true),
                new StashPlanTileViewModel(StashPlanGroup.Review, "Review", 1, true),
            ],
            [
                new StashItemRowViewModel("k1", "Graphics card", "stash", "x2", "Screenshot · 93%", StashPlanGroup.Keep) { WhyLabel = "Hideout" },
                new StashItemRowViewModel("k2", "Bolts", "stash", "x5", "Screenshot · 88%", StashPlanGroup.Sell) { IsIgnored = true },
                new StashItemRowViewModel("k3", "Unknown item", "stash", "x1", "GameWrittenScreenshot · unscored", StashPlanGroup.Review),
            ]);

        Assert.Equal("stash", review.Kind);
        Assert.Equal("6f3c9a51000040008000000000000001", review.Id);
        Assert.Equal("212 named · 3 unknown · ₽18.6M known", review.Summary);
        Assert.Equal("Keep 1 · Review 1", review.Note);
        Assert.Equal(["Keep", "Ignored", "Review"], review.Rows.Select(row => row.Tag));
        Assert.Equal(["Good", "Neutral", "Review"], review.Rows.Select(row => row.TagKind));
        Assert.Equal("confidence not scored", review.Rows[2].Confidence);
        Assert.Equal(("x2", "read 93% sure", "Hideout"), (review.Rows[0].Value, review.Rows[0].Confidence, review.Rows[0].Detail));
    }

    [Fact]
    public void AReviewIsBoundedInRowsAndInText()
    {
        var rows = Enumerable.Range(1, 45)
            .Select(index => new TabletReviewRow(new string('n', 400), "Keep", "Good", "x1", "93%", new string('d', 1000)))
            .ToArray();

        var review = TabletCaptureReview.Bounded("stash", "id", Now, new string('h', 300), "summary", null, rows);

        Assert.Equal(TabletCaptureReview.MaximumRows, review.Rows.Count);
        Assert.Equal(45 - TabletCaptureReview.MaximumRows, review.HiddenRows);
        Assert.All(review.Rows, row => Assert.True(row.Name.Length <= TabletCaptureReview.MaximumText && row.Detail.Length <= TabletCaptureReview.MaximumText));
        Assert.EndsWith("…", review.Heading, StringComparison.Ordinal);
        // Thirty worst-case rows stay far inside the relay's one-megabyte surface bound.
        var surfaceBytes = TabletMapSurfaceJson.Serialize(new TabletMapSurface(
            1, "customs", "Customs", "default", "v1", null, null, [], [], [], [],
            new TabletMapView(null, 0, 0, 1, null, null), null, null, Now, Stash: review, Flea: review)).Length;
        Assert.InRange(surfaceBytes, 1, 64 * 1024);
    }

    private static CaptureHandoffRequest Request(CaptureIdentifiedItem[] identified, params CaptureFleaListing[] rows) =>
        RequestAt(identified, Now, rows);

    private static CaptureHandoffRequest RequestAt(CaptureIdentifiedItem[] identified, DateTimeOffset now, params CaptureFleaListing[] rows)
    {
        var Now = now;
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
