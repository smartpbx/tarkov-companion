using System.Globalization;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.UnitTests.LootScan;

public sealed class LootScanDecisionServiceTests
{
    private const string SourceContentSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ChangedContentSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly CaptureSessionId SessionId =
        new(Guid.Parse("20000000-0000-4000-8000-000000000282"));
    private static readonly InventoryProfileScope ProfileScope = new(
        Guid.Parse("20000000-0000-4000-8000-000000000284"),
        "wipe-2026-09",
        "pvp");

    [Fact]
    public void VerifiedFreeSpaceProducesTakeWithAnExactPlacement()
    {
        var anchor = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 2, 2),
            [Recommendation(anchor, "loot")]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Take, decision.Verdict);
        Assert.Equal(new GridCellAddress(0, 0), decision.Placement!.Anchor);
        Assert.Equal((1, 1, false), (
            decision.Placement.WidthCells,
            decision.Placement.HeightCells,
            decision.Placement.RotateFromObserved));
        Assert.Equal("capacity.visible-fit", Assert.Single(decision.Reasons).Code);
        Assert.Equal(ResultCompleteness.Complete, result.Status.Completeness);
    }

    [Fact]
    public void RotationIsUsedOnlyWhenTheObservedOrientationCannotFit()
    {
        var anchor = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 2, Cell(anchor, "wide-loot", 2, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 2, 1),
            [Recommendation(anchor, "wide-loot", occupiedSquares: 2)]);

        var placement = Assert.Single(result.Decisions).Placement!;
        Assert.True(placement.RotateFromObserved);
        Assert.Equal((1, 2), (placement.WidthCells, placement.HeightCells));
    }

    [Fact]
    public void AcceptedPlacementsConsumeTheSharedCapacityBudget()
    {
        var preferred = new GridCellAddress(0, 0);
        var secondary = new GridCellAddress(0, 1);

        var result = Evaluate(
            CompleteGrid(
                InventoryGridSurface.VisibleLoot,
                1,
                2,
                Cell(preferred, "preferred", 1, 1),
                Cell(secondary, "secondary", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [
                Recommendation(preferred, "preferred", RecommendationReasonCategory.CurrentQuest),
                Recommendation(secondary, "secondary", RecommendationReasonCategory.FutureQuest),
            ]);

        Assert.Equal(LootScanVerdict.Take, result.Decisions.Single(item => item.SourceAnchor == preferred).Verdict);
        var deferred = result.Decisions.Single(item => item.SourceAnchor == secondary);
        Assert.Equal(LootScanVerdict.Leave, deferred.Verdict);
        Assert.Equal("capacity.no-supported-fit", Assert.Single(deferred.Reasons).Code);
    }

    [Fact]
    public void EconomicalBoundedSwapChoosesVerifiedDroppableItemsAndSumsTheirCost()
    {
        var incoming = new GridCellAddress(0, 0);
        var carriedLeft = new GridCellAddress(0, 0);
        var carriedRight = new GridCellAddress(0, 1);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 2, Cell(incoming, "incoming", 2, 1)),
            CompleteGrid(
                InventoryGridSurface.CarriedInventory,
                1,
                2,
                Cell(carriedLeft, "carried-left", 1, 1),
                Cell(carriedRight, "carried-right", 1, 1)),
            [Recommendation(incoming, "incoming", RecommendationReasonCategory.Economics, valueRoubles: 60_000, occupiedSquares: 2)],
            [Droppable(carriedLeft, "carried-left", 1_000), Droppable(carriedRight, "carried-right", 2_000)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Swap, decision.Verdict);
        Assert.Equal(3_000, decision.ReplacementCostRoubles);
        Assert.Equal(60_000, decision.Economics!.BestNetValueRoubles);
        Assert.Equal(30_000, decision.Economics.ValuePerSquareRoubles);
        Assert.Equal("flea-net", decision.Economics.SelectedPriceBasis);
        Assert.Equal(2, decision.Drops.Count);
        Assert.Equal(
            new[] { carriedLeft, carriedRight },
            decision.Drops.Select(item => item.Anchor).OrderBy(item => item.Column));
        Assert.Equal("capacity.bounded-swap", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void StaleEconomicInputsMakeAnOtherwisePossibleSwapReviewOnly()
    {
        var incoming = new GridCellAddress(0, 0);
        var occupied = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(incoming, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(occupied, "carried", 1, 1)),
            [Recommendation(
                incoming,
                "incoming",
                RecommendationReasonCategory.Economics,
                valueRoubles: 10_000,
                economicsObservedUtc: Now.AddHours(-13))],
            [Droppable(occupied, "carried", 1_000)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("recommendation.incomplete", Assert.Single(decision.Reasons).Code);
        Assert.Equal(ResultCompleteness.Partial, decision.Economics!.Status.Completeness);
        Assert.Equal(FreshnessState.Stale, decision.Economics.Status.Freshness);
        Assert.Equal(FreshnessState.Stale, result.Status.Freshness);
    }

    [Fact]
    public void AmbiguousProtectionEvidenceMakesAPotentialSwapReviewOnly()
    {
        var anchor = new GridCellAddress(0, 0);
        var provenance = CatalogProvenance("ambiguous-protection");
        var protectedItem = new EvidencedValue<bool?>(
            "carried.protected",
            false,
            new(ResultCompleteness.Complete, FreshnessState.Current),
            provenance,
            candidates: [new("protected", "Protected", true, provenance)]);
        var policy = new LootScanCarriedPolicy(
            Binding(anchor, "carried"),
            protectedItem,
            Complete<bool?>("carried.pinned", false, provenance),
            Complete<long?>("carried.replacement-value", 1_000, provenance));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(anchor, "carried", 1, 1)),
            [Recommendation(anchor, "incoming")],
            [policy]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("swap.evidence-incomplete", Assert.Single(decision.Reasons).Code);
        Assert.Empty(decision.Drops);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ProtectedOrPinnedCarriedItemsAreNeverOfferedForDropping(bool protectedItem, bool pinned)
    {
        var incoming = new GridCellAddress(0, 0);
        var occupied = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(incoming, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(occupied, "protected", 1, 1)),
            [Recommendation(incoming, "incoming")],
            [CarriedPolicy(occupied, "protected", protectedItem, pinned, 1)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Leave, decision.Verdict);
        Assert.Empty(decision.Drops);
        Assert.Equal("capacity.no-supported-fit", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void DurableCorrectedProtectionDoesNotExpireOrBecomeDroppable()
    {
        var anchor = new GridCellAddress(0, 0);
        var oldProtection = CatalogProvenance("durable-protection", Now.AddDays(-30));
        var protectedItem = new EvidencedValue<bool?>(
            "carried.protected",
            true,
            new(ResultCompleteness.Complete, FreshnessState.Current),
            oldProtection,
            corrections:
            [
                new EvidenceCorrection<bool?>(
                    1,
                    false,
                    true,
                    Now.AddDays(-20),
                    CorrectionOriginClass.User,
                    "local-user"),
            ]);
        var policy = new LootScanCarriedPolicy(
            Binding(anchor, "protected"),
            protectedItem,
            Complete<bool?>("carried.pinned", false, CatalogProvenance("durable-pin", Now.AddDays(-30))),
            Complete<long?>("carried.replacement-value", 1, CatalogProvenance("replacement-value")));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(anchor, "protected", 1, 1)),
            [Recommendation(anchor, "incoming")],
            [policy]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Leave, decision.Verdict);
        Assert.Empty(decision.Drops);
        Assert.Equal(FreshnessState.Current, result.Status.Freshness);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StaleStatusOrFutureCorrectionCannotAuthorizeACarriedPolicy(bool futureCorrection)
    {
        var anchor = new GridCellAddress(0, 0);
        var provenance = CatalogProvenance("untrusted-protection");
        IReadOnlyList<EvidenceCorrection<bool?>> corrections = futureCorrection
            ?
            [
                new EvidenceCorrection<bool?>(
                    1,
                    false,
                    true,
                    Now.AddMinutes(1),
                    CorrectionOriginClass.User,
                    "local-user"),
            ]
            : [];
        var protectedItem = new EvidencedValue<bool?>(
            "carried.protected",
            true,
            new(
                ResultCompleteness.Complete,
                futureCorrection ? FreshnessState.Current : FreshnessState.Stale),
            provenance,
            corrections: corrections);
        var policy = new LootScanCarriedPolicy(
            Binding(anchor, "carried"),
            protectedItem,
            Complete<bool?>("carried.pinned", false, provenance),
            Complete<long?>("carried.replacement-value", 1, provenance));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(anchor, "carried", 1, 1)),
            [Recommendation(anchor, "incoming")],
            [policy]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("swap.evidence-incomplete", Assert.Single(decision.Reasons).Code);
        Assert.Equal(FreshnessState.Stale, result.Status.Freshness);
    }

    [Fact]
    public void MissingCarriedPolicyMakesAPotentialSwapReviewOnly()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(anchor, "carried", 1, 1)),
            [Recommendation(anchor, "incoming")]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("swap.evidence-incomplete", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void ItemLargerThanEverySupportedOrientationIsLeft()
    {
        var anchor = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 2, 2, Cell(anchor, "oversized", 2, 2)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "oversized", occupiedSquares: 4)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Leave, decision.Verdict);
        Assert.Equal("capacity.no-supported-fit", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void PartialCarriedCoverageDegradesAnOtherwiseSupportedTakeToReview()
    {
        var anchor = new GridCellAddress(0, 0);
        var carried = PartialGrid(
            InventoryGridSurface.CarriedInventory,
            2,
            2,
            issues: [new(GridReconstructionIssueKind.GeometryPartial)]);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            carried,
            [Recommendation(anchor, "loot")]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("carried.capacity-incomplete", Assert.Single(decision.Reasons).Code);
        Assert.Contains(result.Issues, issue => issue.Kind == LootScanIssueKind.CarriedCoveragePartial);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void UnresolvedAndAmbiguousLootRemainSeparateReviewDecisions()
    {
        var unresolved = UnresolvedObservation("unresolved", new(0, 0));
        var ambiguous = UnresolvedObservation("ambiguous", new(0, 1), Item("candidate", 1, 1));
        var visible = PartialGrid(
            InventoryGridSurface.VisibleLoot,
            1,
            2,
            unresolved: [unresolved, ambiguous],
            issues:
            [
                new(GridReconstructionIssueKind.ItemUnresolved, unresolved.ObservationId, unresolved.Anchor),
                new(GridReconstructionIssueKind.ItemAmbiguous, ambiguous.ObservationId, ambiguous.Anchor),
            ]);

        var result = Evaluate(
            visible,
            CompleteGrid(InventoryGridSurface.CarriedInventory, 2, 2));

        Assert.Equal(2, result.Decisions.Count);
        Assert.All(result.Decisions, decision =>
        {
            Assert.Equal(LootScanVerdict.Review, decision.Verdict);
            Assert.Equal("item.evidence-incomplete", Assert.Single(decision.Reasons).Code);
        });
        Assert.Equal(new[] { unresolved.Anchor, ambiguous.Anchor }, result.Decisions.Select(item => item.SourceAnchor));
    }

    [Fact]
    public void PartialCellPresentInSafeAndUnresolvedSetsProducesOnlyReview()
    {
        var anchor = new GridCellAddress(0, 0);
        var unresolved = UnresolvedObservation("same-cell-review", anchor, Item("loot", 1, 1));
        var visible = PartialGrid(
            InventoryGridSurface.VisibleLoot,
            1,
            1,
            cells: [Cell(anchor, "loot", 1, 1)],
            unresolved: [unresolved],
            issues: [new(GridReconstructionIssueKind.ItemAmbiguous, unresolved.ObservationId, anchor)]);

        var result = Evaluate(
            visible,
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "loot")]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Null(decision.Placement);
    }

    [Fact]
    public void RecommendationBoundToAnotherItemAtTheSameCellIsRejected()
    {
        var anchor = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "current-item", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "prior-item")]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("recommendation.binding-mismatch", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void CompleteStatusWithAlternateItemEvidenceStillRequiresReview()
    {
        var anchor = new GridCellAddress(0, 0);
        var current = Item("current-item", 1, 1);
        var provenance = ScreenshotProvenance("ambiguous-item");
        var item = new EvidencedValue<RecognizedItem>(
            "cell.0.0",
            current,
            new(ResultCompleteness.Complete, FreshnessState.Current),
            provenance,
            candidates: [new("alternate", "Alternate", Item("alternate-item", 1, 1), provenance)]);

        var result = Evaluate(
            CompleteGrid(
                InventoryGridSurface.VisibleLoot,
                1,
                1,
                new GridCellRecognition(anchor, item)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "current-item")]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("item.evidence-incomplete", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void EconomicFootprintMustMatchTheObservedItem()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 2, Cell(anchor, "wide-item", 2, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 2),
            [Recommendation(
                anchor,
                "wide-item",
                RecommendationReasonCategory.Economics,
                valueRoubles: 100_000,
                occupiedSquares: 1)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("economics.incomplete", Assert.Single(decision.Reasons).Code);
        Assert.Equal("economics.footprint-mismatch", decision.Economics!.Status.Code);
    }

    [Fact]
    public void RaidAdjustedEconomicAdviceStillRequiresASwapGain()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(anchor, "carried", 1, 1)),
            [Recommendation(
                anchor,
                "incoming",
                RecommendationReasonCategory.Economics,
                valueRoubles: 60_000)],
            [Droppable(anchor, "carried", 70_000)],
            recommendationContext: SharedContext(
                RaidContext(
                    RecommendationRaidPhase.Middle,
                    RecommendationRaidRisk.High,
                    Now.AddMinutes(-1))));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Leave, decision.Verdict);
        Assert.Equal("swap.cost-exceeds-value", Assert.Single(decision.Reasons).Code);
        Assert.Empty(decision.Drops);
    }

    [Fact]
    public void DerivedPriceWithModelledLineageStaysReviewOnly()
    {
        var anchor = new GridCellAddress(0, 0);
        var evaluated = Recommendation(
            anchor,
            "incoming",
            RecommendationReasonCategory.Economics,
            valueRoubles: 100_000);
        var modelled = ModelledProvenance(CatalogProvenance("model-input", Now.AddMinutes(-10)));
        var projected = ModelledProvenance(modelled);
        var economics = new RecommendationEconomics(
            Complete<long?>("economics.flea-gross", 101_000, projected),
            Complete<long?>("economics.flea-fee", 1_000, projected),
            Complete<long?>("economics.flea-net", 100_000, projected),
            Complete<long?>("economics.trader", 90_000, CatalogProvenance("trader")),
            Complete<int?>("economics.squares", 1, ScreenshotProvenance("squares")),
            Complete<double?>("economics.condition", 1, ScreenshotProvenance("condition")));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(evaluated, economics)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("economics.incomplete", Assert.Single(decision.Reasons).Code);
        Assert.Equal("economics.model-review", decision.Economics!.Status.Code);
    }

    [Fact]
    public void EconomicProjectionRetainsBothComparedPricesAndTheFootprint()
    {
        var anchor = new GridCellAddress(0, 0);
        var flea = CatalogProvenance("compared-flea");
        var trader = CatalogProvenance("compared-trader");
        var footprint = ScreenshotProvenance("compared-footprint");
        var evaluated = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 100_000);
        var economics = new RecommendationEconomics(
            Complete<long?>("economics.flea-gross", 101_000, flea),
            Complete<long?>("economics.flea-fee", 1_000, flea),
            Complete<long?>("economics.flea-net", 100_000, flea),
            Complete<long?>("economics.trader", 90_000, trader),
            Complete<int?>("economics.squares", 1, footprint),
            Complete<double?>("economics.condition", 1, ScreenshotProvenance("condition")));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(evaluated, economics)]);

        var lineage = Assert.Single(result.Decisions).Economics!.CalculationProvenance!;
        Assert.Equal(
            new[]
            {
                "loot-scan://economic-price/flea-net/available",
                "loot-scan://economic-price/trader/available",
                "loot-scan://economic-footprint",
            },
            lineage.Inputs.Select(input => input.SourceIdentifier));
        Assert.Equal(flea.SourceIdentifier, Assert.Single(lineage.Inputs[0].Inputs).SourceIdentifier);
        Assert.Equal(trader.SourceIdentifier, Assert.Single(lineage.Inputs[1].Inputs).SourceIdentifier);
        Assert.Equal(footprint.SourceIdentifier, Assert.Single(lineage.Inputs[2].Inputs).SourceIdentifier);
    }

    [Fact]
    public void LosingComparedPriceCannotHideModelledLineage()
    {
        var anchor = new GridCellAddress(0, 0);
        var directFlea = CatalogProvenance("direct-winning-flea");
        var modelledTrader = ModelledProvenance(CatalogProvenance(
            "modelled-losing-trader-input",
            Now.AddMinutes(-10)));
        var evaluated = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 100_000);
        var economics = new RecommendationEconomics(
            Complete<long?>("economics.flea-gross", 101_000, directFlea),
            Complete<long?>("economics.flea-fee", 1_000, directFlea),
            Complete<long?>("economics.flea-net", 100_000, directFlea),
            Complete<long?>("economics.trader", 90_000, modelledTrader),
            Complete<int?>("economics.squares", 1, ScreenshotProvenance("squares")),
            Complete<double?>("economics.condition", 1, ScreenshotProvenance("condition")));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(evaluated, economics)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("economics.model-review", decision.Economics!.Status.Code);
    }

    [Fact]
    public void ARecentCorrectionDoesNotRenewExpiredPriceEvidence()
    {
        var anchor = new GridCellAddress(0, 0);
        var oldPrice = CatalogProvenance("expired-corrected-price", Now.AddHours(-13));
        var correctedFlea = new EvidencedValue<long?>(
            "economics.flea-net",
            100_000,
            new(ResultCompleteness.Complete, FreshnessState.Current),
            oldPrice,
            corrections:
            [
                new EvidenceCorrection<long?>(
                    1,
                    90_000,
                    100_000,
                    Now.AddMinutes(-1),
                    CorrectionOriginClass.User,
                    "local-user"),
            ]);
        var economics = new RecommendationEconomics(
            Complete<long?>("economics.flea-gross", 101_000, oldPrice),
            Complete<long?>("economics.flea-fee", 1_000, oldPrice),
            correctedFlea,
            Complete<long?>("economics.trader", 90_000, oldPrice),
            Complete<int?>("economics.squares", 1, ScreenshotProvenance("squares")),
            Complete<double?>("economics.condition", 1, ScreenshotProvenance("condition")));
        var recommendation = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 100_000);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(recommendation, economics)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("recommendation.incomplete", Assert.Single(decision.Reasons).Code);
        Assert.Equal(FreshnessState.Stale, decision.Economics!.Status.Freshness);
    }

    [Fact]
    public void CandidateContractCannotAcceptOrExposeAPrecomputedRecommendationResult()
    {
        var resultType = typeof(TarkovCompanion.Core.Abstractions.V2.RecommendationResult);

        Assert.DoesNotContain(
            typeof(LootScanCandidateRecommendation).GetConstructors().SelectMany(item => item.GetParameters()),
            parameter => parameter.ParameterType == resultType);
        Assert.DoesNotContain(
            typeof(LootScanCandidateRecommendation).GetProperties(),
            property => property.PropertyType == resultType);
    }

    [Fact]
    public void UnknownScreenshotProvenanceCannotAuthorizeAResolvedItem()
    {
        var anchor = new GridCellAddress(0, 0);
        var unknown = UnknownProvenance("recognized-item");
        var item = new RecognizedItem(
            Complete("item.id", "loot", unknown),
            Complete("item.name", "loot", unknown),
            Complete<int?>("item.quantity", 1, unknown),
            Complete<int?>("item.width", 1, unknown),
            Complete<int?>("item.height", 1, unknown),
            Complete<bool?>("item.rotated", false, unknown),
            Complete<bool?>("item.found-in-raid", true, unknown),
            Complete("item.condition", ItemConditionReading.NotApplicable, unknown));

        var result = Evaluate(
            CompleteGrid(
                InventoryGridSurface.VisibleLoot,
                1,
                1,
                new GridCellRecognition(anchor, Complete("cell.0.0", item, unknown))),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "loot")]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("item.evidence-incomplete", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void UnknownNestedPriceProvenanceCannotBeLaunderedIntoDecisiveAdvice()
    {
        var anchor = new GridCellAddress(0, 0);
        var nestedUnknown = new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "fixture://loot-scan/derived-over-unknown",
            Now.AddMinutes(-1),
            EvidenceConfidence.Certain,
            new ProducerIdentity("loot-scan-tests", "1"),
            generatedUtc: Now.AddMinutes(-1),
            inputs: [UnknownProvenance("nested-price")]);
        var candidate = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 60_000);
        var economics = EconomicsWithChannels(
            Complete<long?>("economics.flea-net", 60_000, nestedUnknown),
            Complete<long?>("economics.trader", 50_000, CatalogProvenance("trader")));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(candidate, economics)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal(ResultCompleteness.Partial, decision.Economics!.Status.Completeness);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("stale")]
    [InlineData("unknown")]
    [InlineData("low-confidence")]
    [InlineData("unknown-source")]
    public void UnresolvedAlternatePriceChannelCannotBeDiscarded(string alternateState)
    {
        var anchor = new GridCellAddress(0, 0);
        var ordinary = CatalogProvenance("alternate-trader");
        EvidencedValue<long?> alternate = alternateState switch
        {
            "partial" => new(
                "economics.trader",
                50_000,
                new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current),
                ordinary),
            "stale" => new(
                "economics.trader",
                50_000,
                new ResultStatus(ResultCompleteness.Complete, FreshnessState.Stale),
                ordinary),
            "unknown" => new(
                "economics.trader",
                null,
                new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Unknown),
                ordinary),
            "low-confidence" => Complete<long?>(
                "economics.trader",
                50_000,
                new EvidenceProvenance(
                    EvidenceSourceClass.PublicStructuredData,
                    "fixture://catalog/low-confidence-trader",
                    Now.AddMinutes(-1),
                    new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.5),
                    new ProducerIdentity("loot-scan-tests", "1"))),
            "unknown-source" => Complete<long?>(
                "economics.trader",
                50_000,
                UnknownProvenance("trader")),
            _ => throw new ArgumentOutOfRangeException(nameof(alternateState)),
        };
        var candidate = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 60_000);
        var economics = EconomicsWithChannels(
            Complete<long?>("economics.flea-net", 60_000, CatalogProvenance("flea")),
            alternate);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(candidate, economics)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal(ResultCompleteness.Partial, decision.Economics!.Status.Completeness);
    }

    [Fact]
    public void TrustedUnavailablePriceChannelPreservesItsNamedRole()
    {
        var anchor = new GridCellAddress(0, 0);
        var candidate = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 60_000);
        var economics = EconomicsWithChannels(
            Complete<long?>("economics.flea-net", 60_000, CatalogProvenance("flea")),
            Unavailable<long?>("economics.trader", CatalogProvenance("trader-unavailable")));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(candidate, economics)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Take, decision.Verdict);
        var lineage = decision.Economics!.CalculationProvenance!;
        Assert.Contains(
            lineage.Inputs,
            input => input.SourceIdentifier == "loot-scan://economic-price/trader/unavailable");
    }

    [Fact]
    public void SharedRawPriceSourceStillProducesTwoNamedPriceRoles()
    {
        var anchor = new GridCellAddress(0, 0);
        var shared = CatalogProvenance("shared-price-snapshot");
        var candidate = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 60_000);
        var economics = EconomicsWithChannels(
            Complete<long?>("economics.flea-net", 60_000, shared),
            Complete<long?>("economics.trader", 50_000, shared));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(candidate, economics)]);

        var inputs = Assert.Single(result.Decisions).Economics!.CalculationProvenance!.Inputs;
        Assert.Equal("loot-scan://economic-price/flea-net/available", inputs[0].SourceIdentifier);
        Assert.Equal("loot-scan://economic-price/trader/available", inputs[1].SourceIdentifier);
        Assert.Same(shared, Assert.Single(inputs[0].Inputs));
        Assert.Same(shared, Assert.Single(inputs[1].Inputs));
    }

    [Fact]
    public void PartialFootprintCannotProduceDecisiveEconomicAdvice()
    {
        var anchor = new GridCellAddress(0, 0);
        var candidate = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 60_000);
        var economics = EconomicsWithChannels(
            Complete<long?>("economics.flea-net", 60_000, CatalogProvenance("flea")),
            Complete<long?>("economics.trader", 50_000, CatalogProvenance("trader")),
            new EvidencedValue<int?>(
                "economics.squares",
                1,
                new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current),
                ScreenshotProvenance("partial-squares")));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(candidate, economics)]);

        Assert.Equal(LootScanVerdict.Review, Assert.Single(result.Decisions).Verdict);
    }

    [Fact]
    public void PartialReplacementValueCannotAuthorizeDroppingACarriedItem()
    {
        var anchor = new GridCellAddress(0, 0);
        var provenance = CatalogProvenance("partial-replacement");
        var policy = new LootScanCarriedPolicy(
            Binding(anchor, "carried"),
            Complete<bool?>("carried.protected", false, provenance),
            Complete<bool?>("carried.pinned", false, provenance),
            new EvidencedValue<long?>(
                "carried.replacement-value",
                1_000,
                new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current),
                provenance));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(anchor, "carried", 1, 1)),
            [Recommendation(anchor, "incoming")],
            [policy]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("swap.evidence-incomplete", Assert.Single(decision.Reasons).Code);
        Assert.Empty(decision.Drops);
    }

    [Fact]
    public void UnknownFalseCarriedPolicyCannotAuthorizeDroppingACarriedItem()
    {
        var anchor = new GridCellAddress(0, 0);
        var unknown = UnknownProvenance("carried-policy");
        var policy = new LootScanCarriedPolicy(
            Binding(anchor, "carried"),
            Complete<bool?>("carried.protected", false, unknown),
            Complete<bool?>("carried.pinned", false, unknown),
            Complete<long?>("carried.replacement-value", 1_000, unknown));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(anchor, "carried", 1, 1)),
            [Recommendation(anchor, "incoming")],
            [policy]);

        Assert.Equal(LootScanVerdict.Review, Assert.Single(result.Decisions).Verdict);
    }

    [Fact]
    public void NormalRaidContextIsCurrentAtTheBoundaryAndStaleOneTickLater()
    {
        var anchor = new GridCellAddress(0, 0);
        var visible = CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1));
        var carried = CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1);
        var recommendations =
            new[] { Recommendation(anchor, "loot", RecommendationReasonCategory.Economics, valueRoubles: 10_000) };

        var current = Evaluate(
            visible,
            carried,
            recommendations,
            recommendationContext: SharedContext(RaidContext(
                RecommendationRaidPhase.Middle,
                RecommendationRaidRisk.Low,
                Now - ExplainableRecommendationPolicy.Default.MaximumRaidContextAge)));
        var stale = Evaluate(
            visible,
            carried,
            recommendations,
            recommendationContext: SharedContext(RaidContext(
                RecommendationRaidPhase.Middle,
                RecommendationRaidRisk.Low,
                Now - ExplainableRecommendationPolicy.Default.MaximumRaidContextAge - TimeSpan.FromTicks(1))));

        Assert.Equal(LootScanVerdict.Take, Assert.Single(current.Decisions).Verdict);
        Assert.Equal(LootScanVerdict.Review, Assert.Single(stale.Decisions).Verdict);
        Assert.Equal(FreshnessState.Stale, stale.Status.Freshness);
    }

    [Theory]
    [InlineData(10_000, LootScanVerdict.Leave)]
    [InlineData(25_000, LootScanVerdict.Take)]
    public void ElevatedRaidThresholdUsesTheExactActiveReason(long valueRoubles, LootScanVerdict verdict)
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(
                anchor,
                "loot",
                RecommendationReasonCategory.Economics,
                valueRoubles: valueRoubles)],
            recommendationContext: SharedContext(RaidContext(
                RecommendationRaidPhase.Middle,
                RecommendationRaidRisk.Elevated,
                Now.AddMinutes(-1))));

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(verdict, decision.Verdict);
        Assert.Contains(
            decision.Recommendation!.Decision.Value!.Reasons,
            reason => reason.Code == "raid.risk.elevated");
    }

    [Fact]
    public void ScarcityUsesTheExactActiveObtainabilityCode()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(
                anchor,
                "loot",
                RecommendationReasonCategory.ScarcityOrObtainability,
                valueRoubles: 1)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Take, decision.Verdict);
        Assert.Contains(
            decision.Recommendation!.Decision.Value!.Reasons,
            reason => reason.Code == "scarcity.obtainability.scarce");
    }

    [Fact]
    public void OversizedRecommendationInputsFailClosedWithoutAbortingSiblingItems()
    {
        var oversizedAnchor = new GridCellAddress(0, 0);
        var validAnchor = new GridCellAddress(0, 1);
        var oversizedBase = Recommendation(oversizedAnchor, "oversized");
        var oversizedProvenance = new EvidenceProvenance(
            EvidenceSourceClass.PublicStructuredData,
            new string('x', 2_049),
            Now.AddMinutes(-1),
            EvidenceConfidence.Certain,
            new ProducerIdentity("loot-scan-tests", "1"));
        var oversized = new LootScanCandidateRecommendation(
            oversizedBase.Binding,
            oversizedBase.RecommendationId,
            oversizedBase.ProfileScope,
            oversizedBase.DataSnapshotId,
            Profile(RecommendationReasonCategory.CurrentQuest, oversizedProvenance),
            oversizedBase.Economics,
            oversizedBase.Scarcity);

        var result = Evaluate(
            CompleteGrid(
                InventoryGridSurface.VisibleLoot,
                1,
                2,
                Cell(oversizedAnchor, "oversized", 1, 1),
                Cell(validAnchor, "valid", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [oversized, Recommendation(validAnchor, "valid")]);

        var rejected = result.Decisions.Single(item => item.SourceAnchor == oversizedAnchor);
        Assert.Equal(LootScanVerdict.Review, rejected.Verdict);
        Assert.Equal("recommendation.input-too-large", Assert.Single(rejected.Reasons).Code);
        Assert.Equal(
            LootScanVerdict.Take,
            result.Decisions.Single(item => item.SourceAnchor == validAnchor).Verdict);
    }

    [Fact]
    public void ContractLimitPriceLineageBecomesReviewInsteadOfThrowing()
    {
        var anchor = new GridCellAddress(0, 0);
        var candidate = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 60_000);
        var economics = EconomicsWithChannels(
            Complete<long?>("economics.flea-net", 60_000, MaximumDepthProvenance()),
            Complete<long?>("economics.trader", 50_000, CatalogProvenance("trader")));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [WithEconomics(candidate, economics)]);

        Assert.Equal(LootScanVerdict.Review, Assert.Single(result.Decisions).Verdict);
    }

    [Fact]
    public void CandidateAndSharedContextContractsBoundExternalIdentifiers()
    {
        var anchor = new GridCellAddress(0, 0);
        var candidate = Recommendation(anchor, "loot");
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootScanCandidateRecommendation(
            candidate.Binding,
            new string('r', LootScanCandidateRecommendation.MaximumRecommendationIdLength + 1),
            candidate.ProfileScope,
            candidate.DataSnapshotId,
            candidate.Profile,
            candidate.Economics,
            candidate.Scarcity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootScanCandidateRecommendation(
            candidate.Binding,
            candidate.RecommendationId,
            candidate.ProfileScope,
            new string('d', LootScanCandidateRecommendation.MaximumDataSnapshotIdLength + 1),
            candidate.Profile,
            candidate.Economics,
            candidate.Scarcity));

        var oversizedProfileScope = new InventoryProfileScope(
            Guid.Parse("20000000-0000-4000-8000-000000000287"),
            new string('g', LootScanCandidateRecommendation.MaximumProfileDescriptorLength + 1),
            "pvp");
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootScanCandidateRecommendation(
            candidate.Binding,
            candidate.RecommendationId,
            oversizedProfileScope,
            candidate.DataSnapshotId,
            candidate.Profile,
            candidate.Economics,
            candidate.Scarcity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootScanRecommendationContext(
            oversizedProfileScope,
            candidate.DataSnapshotId,
            inventory: null,
            raidContext: null));

        var provenance = CatalogProvenance("oversized-need");
        RecommendationProfileFacts ProfileWithNeed(string needId, string displayName) => new(
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            provenance,
            Complete<RecommendationExplicitActionState?>(
                "profile.explicit-action",
                RecommendationExplicitActionState.None,
                provenance),
            Complete<bool?>("profile.protected", false, provenance),
            Complete<bool?>("profile.pinned", false, provenance),
            Complete<bool?>("profile.wishlist", false, provenance),
            new RecommendationEventStateFacts(
                null,
                Complete<EventItemState?>("profile.event-state", EventItemState.Unknown, provenance)),
            [new RecommendationNeed(
                needId,
                displayName,
                RecommendationNeedPurpose.Quest,
                0,
                1,
                false,
                new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
                provenance)]);

        var profile = ProfileWithNeed(
            new string('n', LootScanCandidateRecommendation.MaximumNeedIdLength + 1),
            "Need");

        Assert.Throws<ArgumentOutOfRangeException>(() => new LootScanCandidateRecommendation(
            candidate.Binding,
            candidate.RecommendationId,
            candidate.ProfileScope,
            candidate.DataSnapshotId,
            profile,
            candidate.Economics,
            candidate.Scarcity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LootScanCandidateRecommendation(
            candidate.Binding,
            candidate.RecommendationId,
            candidate.ProfileScope,
            candidate.DataSnapshotId,
            ProfileWithNeed(
                "need",
                new string('d', LootScanCandidateRecommendation.MaximumNeedDisplayNameLength + 1)),
            candidate.Economics,
            candidate.Scarcity));
    }

    [Fact]
    public void IncompatibleProfileBindingIsIsolatedToItsCandidate()
    {
        var invalidAnchor = new GridCellAddress(0, 0);
        var validAnchor = new GridCellAddress(0, 1);
        var otherProfile = new InventoryProfileScope(
            Guid.Parse("20000000-0000-4000-8000-000000000286"),
            "wipe-2026-09",
            "pvp");
        var scope = new RecommendationEventScope(otherProfile, "event", "1", "invalid");
        var provenance = CatalogProvenance("event-profile");
        var profile = new RecommendationProfileFacts(
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current),
            provenance,
            Complete<RecommendationExplicitActionState?>(
                "profile.explicit-action",
                RecommendationExplicitActionState.None,
                provenance),
            Complete<bool?>("profile.protected", false, provenance),
            Complete<bool?>("profile.pinned", false, provenance),
            Complete<bool?>("profile.wishlist", false, provenance),
            new RecommendationEventStateFacts(
                scope,
                Complete<EventItemState?>("profile.event-state", EventItemState.Safe, provenance)),
            []);
        var baseline = Recommendation(invalidAnchor, "invalid");
        var incompatible = new LootScanCandidateRecommendation(
            baseline.Binding,
            baseline.RecommendationId,
            otherProfile,
            baseline.DataSnapshotId,
            profile,
            baseline.Economics,
            baseline.Scarcity,
            scope);

        var result = Evaluate(
            CompleteGrid(
                InventoryGridSurface.VisibleLoot,
                1,
                2,
                Cell(invalidAnchor, "invalid", 1, 1),
                Cell(validAnchor, "valid", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [incompatible, Recommendation(validAnchor, "valid")]);

        var invalid = result.Decisions.Single(item => item.SourceAnchor == invalidAnchor);
        Assert.Equal(LootScanVerdict.Review, invalid.Verdict);
        Assert.Equal("recommendation.binding-mismatch", Assert.Single(invalid.Reasons).Code);
        Assert.Equal(
            LootScanVerdict.Take,
            result.Decisions.Single(item => item.SourceAnchor == validAnchor).Verdict);
    }

    [Fact]
    public void IncompatibleDataSnapshotBindingIsRejected()
    {
        var anchor = new GridCellAddress(0, 0);
        var baseline = Recommendation(anchor, "loot");
        var incompatible = new LootScanCandidateRecommendation(
            baseline.Binding,
            baseline.RecommendationId,
            baseline.ProfileScope,
            "snapshot-other",
            baseline.Profile,
            baseline.Economics,
            baseline.Scarcity);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [incompatible]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("recommendation.binding-mismatch", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void RecommendationWorkBudgetFailsClosed()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "loot")],
            maximumRecommendationWorkVisits: 1);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("recommendation.validation-budget-exhausted", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void RecommendationEvaluationHonorsCallerCancellation()
    {
        var anchor = new GridCellAddress(0, 0);
        var request = Request(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "loot")]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new LootScanDecisionService().Evaluate(request, cancellation.Token));
    }

    [Fact]
    public void PlacementWorkBudgetProducesReviewInsteadOfUnboundedSearch()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 2, Cell(anchor, "incoming", 2, 1)),
            CompleteGrid(
                InventoryGridSurface.CarriedInventory,
                1,
                2,
                Cell(new GridCellAddress(0, 0), "left", 1, 1),
                Cell(new GridCellAddress(0, 1), "right", 1, 1)),
            [Recommendation(anchor, "incoming", occupiedSquares: 2)],
            maximumPlacementCellVisits: 1);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("capacity.search-budget-exhausted", Assert.Single(decision.Reasons).Code);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
    }

    [Fact]
    public void ResultFocusReturnsToTheDeviceThatInitiatedTheScan()
    {
        const string initiatingDevice = "tablet-raid-review";
        var anchor = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "loot")],
            initiatingDeviceId: initiatingDevice);

        Assert.Equal(initiatingDevice, result.FocusDeviceId);
    }

    [Fact]
    public void ReviewViewModelKeepsActionEconomicsAndEvidenceScannable()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "rare-loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "rare-loot", valueRoubles: 120_000)]);

        var viewModel = new LootScanViewModel(result, culture: CultureInfo.InvariantCulture);
        var card = Assert.Single(viewModel.Decisions);

        Assert.Equal(1, viewModel.TakeCount);
        Assert.Equal("TAKE", card.VerdictLabel);
        Assert.Contains("120,000", card.ValueLabel, StringComparison.Ordinal);
        Assert.Contains("confidence", card.EvidenceLabel, StringComparison.Ordinal);
        Assert.True(card.HasPlacement);
        Assert.False(card.CanOpenEvidence);
        Assert.Equal("Evidence unavailable", card.EvidenceActionLabel);
        Assert.StartsWith("TAKE:", card.AutomationSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewViewModelSurfacesStaleEvidenceInsteadOfCallingItReady()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "loot", decisionObservedUtc: Now.AddHours(-25))]);
        var viewModel = new LootScanViewModel(result, culture: CultureInfo.InvariantCulture);

        Assert.Contains("expired", viewModel.StatusLabel, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsComplete);
        Assert.True(viewModel.IsPartial);
    }

    [Fact]
    public void ReviewViewModelDistinguishesAReadableEmptyGridFromAnUnavailableCapture()
    {
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1));
        var viewModel = new LootScanViewModel(result, culture: CultureInfo.InvariantCulture);

        Assert.Equal(ResultCompleteness.Complete, result.Status.Completeness);
        Assert.True(viewModel.HasNoVisibleLoot);
        Assert.False(viewModel.HasUnavailableResult);
    }

    [Fact]
    public void ReviewViewModelKeepsReadableEmptyLootWhenOnlyCarriedCoverageIsPartial()
    {
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1),
            PartialGrid(
                InventoryGridSurface.CarriedInventory,
                1,
                1,
                issues: [new(GridReconstructionIssueKind.GeometryPartial)]));
        var viewModel = new LootScanViewModel(result, culture: CultureInfo.InvariantCulture);

        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
        Assert.True(viewModel.HasNoVisibleLoot);
        Assert.False(viewModel.HasUnavailableResult);
    }

    [Fact]
    public void ReviewViewModelDoesNotCallAChangedEmptyCaptureCurrent()
    {
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            reviewedContentSha256: ChangedContentSha256);
        var viewModel = new LootScanViewModel(result, culture: CultureInfo.InvariantCulture);

        Assert.False(viewModel.HasNoVisibleLoot);
        Assert.True(viewModel.HasUnavailableResult);
    }

    [Fact]
    public void ReviewViewModelNamesSwapDropsAndShowsDecisionLineage()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(anchor, "carried-item", 1, 1)),
            [Recommendation(anchor, "incoming", valueRoubles: 100_000)],
            [Droppable(anchor, "carried-item", 1_000)]);
        var card = Assert.Single(new LootScanViewModel(result, culture: CultureInfo.InvariantCulture).Decisions);

        Assert.True(card.HasSwap);
        Assert.Contains("carried-item", Assert.Single(card.DropLabels), StringComparison.Ordinal);
        Assert.Contains("row 1, column 1", Assert.Single(card.DropLabels), StringComparison.Ordinal);
        Assert.Contains("RUB", card.SwapLabel, StringComparison.Ordinal);
        Assert.Contains(
            card.RecommendationReasonLabels,
            label => label.Contains("CurrentQuest", StringComparison.Ordinal));
        Assert.Contains("Opportunity cost", card.OpportunityCostLabel, StringComparison.Ordinal);
        Assert.Contains("RUB", card.OpportunityCostLabel, StringComparison.Ordinal);
        Assert.Contains("price", card.OpportunityCostLabel, StringComparison.Ordinal);
        Assert.Contains("footprint", card.OpportunityCostLabel, StringComparison.Ordinal);
        Assert.Contains("Flea net", card.EconomicEvidenceLabel, StringComparison.Ordinal);
        Assert.Contains("price", card.EconomicEvidenceLabel, StringComparison.Ordinal);
        Assert.Contains("footprint", card.EconomicEvidenceLabel, StringComparison.Ordinal);
        Assert.Contains("incoming", card.EvidenceDisclosureName, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateLabelIncludesCanonicalIdAlternativesNestedInsideTheRecognizedItem()
    {
        var anchor = new GridCellAddress(0, 0);
        var provenance = ScreenshotProvenance("nested-canonical-candidate");
        var canonicalId = new EvidencedValue<string>(
            "item.id",
            "current-item",
            new(ResultCompleteness.Complete, FreshnessState.Current),
            provenance,
            candidates: [new("alternate", "Alternate item", "alternate-item", provenance)]);
        var item = new RecognizedItem(
            canonicalId,
            Complete("item.name", "Current item", provenance),
            Complete<int?>("item.quantity", 1, provenance),
            Complete<int?>("item.width", 1, provenance),
            Complete<int?>("item.height", 1, provenance),
            Complete<bool?>("item.rotated", false, provenance),
            Complete<bool?>("item.found-in-raid", true, provenance),
            Complete("item.condition", ItemConditionReading.NotApplicable, provenance));
        var visible = CompleteGrid(
            InventoryGridSurface.VisibleLoot,
            1,
            1,
            new GridCellRecognition(anchor, Complete("cell", item, provenance)));

        var result = Evaluate(visible, CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1));
        var card = Assert.Single(new LootScanViewModel(result, culture: CultureInfo.InvariantCulture).Decisions);

        Assert.Equal("1 alternate identity", card.CandidateLabel);
    }

    [Fact]
    public void ApplicationReasonsUseInvariantNumbersRegardlessOfProcessCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var anchor = new GridCellAddress(0, 0);
            var result = Evaluate(
                CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "incoming", 1, 1)),
                CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1, Cell(anchor, "carried", 1, 1)),
                [Recommendation(
                    anchor,
                    "incoming",
                    RecommendationReasonCategory.Economics,
                    valueRoubles: 60_000)],
                [Droppable(anchor, "carried", 70_000)]);

            Assert.Contains("70,000", Assert.Single(Assert.Single(result.Decisions).Reasons).Explanation, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ReviewViewPagesLargeScansAndUsesTheRequestedNumberCulture()
    {
        const int itemCount = 25;
        var cells = Enumerable.Range(0, itemCount)
            .Select(index => new GridCellAddress(index / 5, index % 5))
            .Select(anchor => Cell(anchor, $"loot-{anchor.Row}-{anchor.Column}", 1, 1))
            .ToArray();
        var recommendations = cells
            .Select(cell => Recommendation(cell.Anchor, cell.Item.Value!.CanonicalId.Value!, valueRoubles: 120_000))
            .ToArray();
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 5, 5, cells),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 5, 5),
            recommendations);
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberGroupSeparator = "_";
        culture.NumberFormat.CurrencyGroupSeparator = "_";
        var viewModel = new LootScanViewModel(result, culture: culture);

        Assert.Equal(itemCount, viewModel.Decisions.Count);
        Assert.Equal(24, viewModel.VisibleDecisions.Count);
        Assert.True(viewModel.HasMultiplePages);
        Assert.False(viewModel.HasPreviousPage);
        Assert.True(viewModel.HasNextPage);
        Assert.Contains("RUB\u00A0120_000", viewModel.VisibleDecisions[0].ValueLabel, StringComparison.Ordinal);

        var pageNavigations = 0;
        viewModel.PageNavigated += (_, _) => pageNavigations++;

        viewModel.NextPageCommand.Execute(null);

        Assert.Single(viewModel.VisibleDecisions);
        Assert.True(viewModel.HasPreviousPage);
        Assert.False(viewModel.HasNextPage);
        Assert.Equal("Page 2 of 2 • 25 items", viewModel.PageSummary);
        Assert.Equal(1, pageNavigations);
    }

    [Fact]
    public void SwapContractRejectsTheSameDisplacedItemTwice()
    {
        var anchor = new GridCellAddress(0, 0);
        var item = Complete("item", Item("carried", 1, 1));
        var provenance = CatalogProvenance("replacement");
        var drop = new LootScanDropItem(anchor, item, 100, provenance);

        Assert.Throws<ArgumentException>(() => new LootScanDecision(
            anchor,
            Complete("incoming", Item("incoming", 1, 1)),
            LootScanVerdict.Swap,
            [new("swap", "Swap")],
            placement: new(anchor, 1, 1, false),
            drops: [drop, drop],
            replacementCostRoubles: 200));
    }

    [Fact]
    public void LootDecisionContractBoundsReasonsBeforeCopyingThem()
    {
        var reasons = Enumerable.Range(0, LootScanPlannerLimits.MaximumReasonsPerDecision + 1)
            .Select(index => new LootScanReason($"reason-{index}", "Bounded explanation"))
            .ToArray();

        Assert.Throws<ArgumentException>(() => new LootScanDecision(
            new GridCellAddress(0, 0),
            Complete("incoming", Item("incoming", 1, 1)),
            LootScanVerdict.Review,
            reasons));
    }

    [Fact]
    public void LootDecisionContractRejectsReplacementCostOverflow()
    {
        var provenance = CatalogProvenance("replacement-overflow");
        var first = new LootScanDropItem(
            new GridCellAddress(0, 0),
            Complete("first", Item("first", 1, 1)),
            long.MaxValue,
            provenance);
        var second = new LootScanDropItem(
            new GridCellAddress(0, 1),
            Complete("second", Item("second", 1, 1)),
            1,
            provenance);

        Assert.Throws<ArgumentException>(() => new LootScanDecision(
            new GridCellAddress(1, 0),
            Complete("incoming", Item("incoming", 2, 1)),
            LootScanVerdict.Swap,
            [new("swap", "Swap")],
            placement: new(new GridCellAddress(0, 0), 2, 1, false),
            drops: [first, second],
            replacementCostRoubles: long.MaxValue));
    }

    private static LootScanResult Evaluate(
        GridReconstructionResult visible,
        GridReconstructionResult carried,
        IReadOnlyList<LootScanCandidateRecommendation>? recommendations = null,
        IReadOnlyList<LootScanCarriedPolicy>? policies = null,
        string reviewedContentSha256 = SourceContentSha256,
        string initiatingDeviceId = "desktop-primary",
        int maximumPlacementCellVisits = LootScanPlannerLimits.MaximumPlacementCellVisits,
        int maximumRecommendationWorkVisits = LootScanPlannerLimits.MaximumRecommendationWorkVisits,
        LootScanRecommendationContext? recommendationContext = null)
    {
        var request = Request(
            visible,
            carried,
            recommendations,
            policies,
            reviewedContentSha256,
            initiatingDeviceId,
            recommendationContext);
        return new LootScanDecisionService(
            maximumPlacementCellVisits: maximumPlacementCellVisits,
            maximumRecommendationWorkVisits: maximumRecommendationWorkVisits).Evaluate(request);
    }

    private static LootScanRequest Request(
        GridReconstructionResult visible,
        GridReconstructionResult carried,
        IReadOnlyList<LootScanCandidateRecommendation>? recommendations = null,
        IReadOnlyList<LootScanCarriedPolicy>? policies = null,
        string reviewedContentSha256 = SourceContentSha256,
        string initiatingDeviceId = "desktop-primary",
        LootScanRecommendationContext? recommendationContext = null) => new(
            "loot-scan-282",
            SessionId,
            new CaptureCorrelationId(Guid.Parse("20000000-0000-4000-8000-000000000283")),
            new CaptureContextMetadata(null, null, null, null, null, null, initiatingDeviceId),
            "artifact-282",
            1,
            SourceContentSha256,
            reviewedContentSha256,
            initiatingDeviceId,
            Now,
            recommendationContext ?? SharedContext(),
            visible,
            carried,
            recommendations ?? [],
            policies ?? []);

    private static GridReconstructionResult CompleteGrid(
        InventoryGridSurface surface,
        int rows,
        int columns,
        params GridCellRecognition[] cells) => new(
        GridReconstructionOutcome.Complete,
        surface,
        Grid(rows, columns, cells),
        [],
        []);

    private static GridReconstructionResult PartialGrid(
        InventoryGridSurface surface,
        int rows,
        int columns,
        IReadOnlyList<GridCellRecognition>? cells = null,
        IReadOnlyList<GridCellObservation>? unresolved = null,
        IReadOnlyList<GridReconstructionIssue>? issues = null) => new(
        GridReconstructionOutcome.Partial,
        surface,
        Grid(rows, columns, cells ?? []),
        unresolved ?? [],
        issues ?? []);

    private static GridRecognition Grid(
        int rows,
        int columns,
        IReadOnlyList<GridCellRecognition> cells) => new(
        new GridGeometry(
            Complete<int?>("grid.rows", rows),
            Complete<int?>("grid.columns", columns),
            Complete<int?>("grid.cell-width", 64),
            Complete<int?>("grid.cell-height", 64)),
        cells);

    private static GridCellRecognition Cell(GridCellAddress anchor, string itemId, int width, int height) =>
        new(anchor, Complete($"cell.{anchor.Row}.{anchor.Column}", Item(itemId, width, height)));

    private static GridCellObservation UnresolvedObservation(
        string observationId,
        GridCellAddress anchor,
        RecognizedItem? candidate = null)
    {
        var provenance = ScreenshotProvenance();
        IReadOnlyList<EvidenceCandidate<RecognizedItem>> candidates = candidate is null
            ? []
            : [new EvidenceCandidate<RecognizedItem>("candidate-1", "Possible item", candidate, provenance)];
        var item = new EvidencedValue<RecognizedItem>(
            $"unresolved.{anchor.Row}.{anchor.Column}",
            null,
            new(candidate is null ? ResultCompleteness.Unknown : ResultCompleteness.Partial, FreshnessState.Current),
            provenance,
            new EvidenceRegion(anchor.Column * 10, anchor.Row * 10, 10, 10, EvidenceCoordinateSpace.SourcePixels),
            candidates);
        return new(observationId, anchor, item);
    }

    private static RecognizedItem Item(string itemId, int width, int height) => new(
        Complete("item.id", itemId),
        Complete("item.name", itemId),
        Complete<int?>("item.quantity", 1),
        Complete<int?>("item.width", width),
        Complete<int?>("item.height", height),
        Complete<bool?>("item.rotated", false),
        Complete<bool?>("item.found-in-raid", true),
        Complete("item.condition", ItemConditionReading.NotApplicable));

    private static LootScanCandidateRecommendation Recommendation(
        GridCellAddress anchor,
        string itemId,
        RecommendationReasonCategory category = RecommendationReasonCategory.CurrentQuest,
        long? valueRoubles = null,
        int occupiedSquares = 1,
        DateTimeOffset? economicsObservedUtc = null,
        DateTimeOffset? decisionObservedUtc = null)
    {
        var profileProvenance = CatalogProvenance("profile", decisionObservedUtc);
        return new(
            Binding(anchor, itemId),
            $"recommendation-{anchor.Row}-{anchor.Column}",
            ProfileScope,
            "snapshot-1",
            Profile(category, profileProvenance),
            Economics(valueRoubles ?? 100_000, occupiedSquares, economicsObservedUtc),
            new RecommendationScarcityFacts(Complete<RecommendationObtainabilityBand?>(
                "scarcity.obtainability",
                category == RecommendationReasonCategory.ScarcityOrObtainability
                    ? RecommendationObtainabilityBand.Scarce
                    : RecommendationObtainabilityBand.Available,
                CatalogProvenance("scarcity", decisionObservedUtc))));
    }

    private static LootScanCandidateRecommendation WithEconomics(
        LootScanCandidateRecommendation candidate,
        RecommendationEconomics economics) => new(
        candidate.Binding,
        candidate.RecommendationId,
        candidate.ProfileScope,
        candidate.DataSnapshotId,
        candidate.Profile,
        economics,
        candidate.Scarcity,
        candidate.EventScope);

    private static RecommendationProfileFacts Profile(
        RecommendationReasonCategory category,
        EvidenceProvenance provenance)
    {
        IReadOnlyList<RecommendationNeed> needs = category switch
        {
            RecommendationReasonCategory.CurrentFoundInRaidQuest =>
                [Need(RecommendationNeedPurpose.Quest, 0, true, provenance)],
            RecommendationReasonCategory.CurrentQuest =>
                [Need(RecommendationNeedPurpose.Quest, 0, false, provenance)],
            RecommendationReasonCategory.FutureQuest =>
                [Need(RecommendationNeedPurpose.Quest, 1, false, provenance)],
            RecommendationReasonCategory.Hideout =>
                [Need(RecommendationNeedPurpose.Hideout, 0, false, provenance)],
            RecommendationReasonCategory.CraftOrBarter =>
                [Need(RecommendationNeedPurpose.CraftOrBarter, 0, false, provenance)],
            RecommendationReasonCategory.SpecialistUtility =>
                [Need(RecommendationNeedPurpose.SpecialistUtility, 0, false, provenance)],
            _ => [],
        };
        return new RecommendationProfileFacts(
            new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "profile.complete"),
            provenance,
            Complete<RecommendationExplicitActionState?>(
                "profile.explicit-action",
                RecommendationExplicitActionState.None,
                provenance),
            Complete<bool?>(
                "profile.protected",
                category == RecommendationReasonCategory.Safety,
                provenance),
            Complete<bool?>(
                "profile.pinned",
                category == RecommendationReasonCategory.PinOrWishlist,
                provenance),
            Complete<bool?>("profile.wishlist", false, provenance),
            new RecommendationEventStateFacts(
                null,
                Complete<EventItemState?>("profile.event-state", EventItemState.Unknown, provenance)),
            needs);
    }

    private static RecommendationNeed Need(
        RecommendationNeedPurpose purpose,
        int stepsAhead,
        bool requiresFoundInRaid,
        EvidenceProvenance provenance) => new(
        $"fixture-{purpose.ToString().ToLowerInvariant()}-{stepsAhead}",
        "Fixture need",
        purpose,
        stepsAhead,
        1,
        requiresFoundInRaid,
        new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "need.complete"),
        provenance);

    private static LootScanRecommendationContext SharedContext(
        RecommendationRaidContext? raidContext = null,
        ObservedInventoryEvidenceSnapshot? inventory = null) => new(
        ProfileScope,
        "snapshot-1",
        inventory ?? CompleteInventory(),
        raidContext ?? RaidContext(
            RecommendationRaidPhase.Middle,
            RecommendationRaidRisk.Low,
            Now.AddMinutes(-1)));

    private static ObservedInventoryEvidenceSnapshot CompleteInventory(
        EvidenceProvenance? provenance = null) => new(
        Guid.Parse("20000000-0000-4000-8000-000000000285"),
        ProfileScope,
        "snapshot-1",
        new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "inventory.complete"),
        new EvidenceCoverage(fraction: 1),
        provenance ?? ScreenshotProvenance("inventory"),
        [],
        unresolvedCells: 0);

    private static RecommendationRaidContext RaidContext(
        RecommendationRaidPhase phase,
        RecommendationRaidRisk risk,
        DateTimeOffset observedUtc,
        EvidenceProvenance? provenance = null)
    {
        var source = provenance ?? ScreenshotProvenance("raid-context", observedUtc);
        return new(
            Complete<RecommendationRaidPhase?>("raid.phase", phase, source),
            Complete<RecommendationRaidRisk?>("raid.risk", risk, source));
    }

    private static EvidenceProvenance MaximumDepthProvenance()
    {
        EvidenceProvenance provenance = CatalogProvenance("depth-leaf");
        for (var depth = 2; depth <= EvidenceProvenance.MaxInputDepth; depth++)
        {
            provenance = new EvidenceProvenance(
                EvidenceSourceClass.DerivedCalculation,
                $"fixture://loot-scan/depth-{depth}",
                Now.AddMinutes(-1),
                new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
                new ProducerIdentity("loot-scan-tests", "1"),
                generatedUtc: Now.AddMinutes(-1),
                inputs: [provenance]);
        }

        return provenance;
    }

    private static LootScanCarriedPolicy Droppable(
        GridCellAddress anchor,
        string itemId,
        long replacementValueRoubles) =>
        CarriedPolicy(
            anchor,
            itemId,
            protectedItem: false,
            pinned: false,
            replacementValueRoubles: replacementValueRoubles);

    private static LootScanCarriedPolicy CarriedPolicy(
        GridCellAddress anchor,
        string itemId,
        bool protectedItem,
        bool pinned,
        long replacementValueRoubles) => new(
        Binding(anchor, itemId),
        Complete<bool?>("carried.protected", protectedItem, CatalogProvenance("protected")),
        Complete<bool?>("carried.pinned", pinned, CatalogProvenance("pinned")),
        Complete<long?>("carried.replacement-value", replacementValueRoubles, CatalogProvenance("replacement-value")));

    private static LootScanEvidenceBinding Binding(GridCellAddress anchor, string itemId) => new(
        SessionId,
        "artifact-282",
        1,
        SourceContentSha256,
        anchor,
        itemId);

    private static RecommendationEconomics Economics(
        long valueRoubles,
        int occupiedSquares,
        DateTimeOffset? observedUtc = null) => new(
        Complete<long?>("economics.flea-gross", valueRoubles + 1_000, CatalogProvenance("flea-gross", observedUtc)),
        Complete<long?>("economics.flea-fee", 1_000, CatalogProvenance("flea-fee", observedUtc)),
        Complete<long?>("economics.flea-net", valueRoubles, CatalogProvenance("flea-net", observedUtc)),
        Complete<long?>("economics.trader", Math.Max(0, valueRoubles - 1_000), CatalogProvenance("trader", observedUtc)),
        Complete<int?>("economics.squares", occupiedSquares, ScreenshotProvenance("squares", observedUtc)),
        Complete<double?>("economics.condition", 1, ScreenshotProvenance("condition")));

    private static RecommendationEconomics EconomicsWithChannels(
        EvidencedValue<long?> fleaNet,
        EvidencedValue<long?> trader,
        EvidencedValue<int?>? occupiedSquares = null) => new(
        Complete<long?>("economics.flea-gross", 61_000, CatalogProvenance("flea-gross")),
        Complete<long?>("economics.flea-fee", 1_000, CatalogProvenance("flea-fee")),
        fleaNet,
        trader,
        occupiedSquares ?? Complete<int?>("economics.squares", 1, ScreenshotProvenance("squares")),
        Complete<double?>("economics.condition", 1, ScreenshotProvenance("condition")));

    private static EvidencedValue<T> Complete<T>(
        string fieldId,
        T value,
        EvidenceProvenance? provenance = null) => new(
        fieldId,
        value,
        new(ResultCompleteness.Complete, FreshnessState.Current),
        provenance ?? ScreenshotProvenance());

    private static EvidencedValue<T> Unavailable<T>(
        string fieldId,
        EvidenceProvenance provenance) => new(
        fieldId,
        default,
        new(ResultCompleteness.Unavailable, FreshnessState.Current),
        provenance);

    private static EvidenceProvenance ScreenshotProvenance(
        string suffix = "capture",
        DateTimeOffset? observedUtc = null) => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        $"fixture://loot-scan/{suffix}",
        observedUtc ?? Now.AddMinutes(-1),
        EvidenceConfidence.Certain,
        new ProducerIdentity("loot-scan-tests", "1"));

    private static EvidenceProvenance CatalogProvenance(string suffix, DateTimeOffset? observedUtc = null) => new(
        EvidenceSourceClass.PublicStructuredData,
        $"fixture://catalog/{suffix}",
        observedUtc ?? Now.AddMinutes(-5),
        EvidenceConfidence.Certain,
        new ProducerIdentity("loot-scan-tests", "1"));

    private static EvidenceProvenance UnknownProvenance(string suffix) => new(
        EvidenceSourceClass.Unknown,
        $"fixture://unknown/{suffix}",
        Now.AddMinutes(-1),
        EvidenceConfidence.Certain,
        new ProducerIdentity("loot-scan-tests", "1"));

    private static EvidenceProvenance ModelledProvenance(params EvidenceProvenance[] inputs) => new(
        EvidenceSourceClass.ModelledEstimate,
        "fixture://loot-scan/modelled-price",
        Now.AddMinutes(-5),
        new EvidenceConfidence(EvidenceConfidenceKind.CalibratedEstimate, 0.9, "fixture-calibration"),
        new ProducerIdentity("loot-scan-tests", "1", "price-model-1"),
        dataThroughUtc: Now.AddMinutes(-10),
        generatedUtc: Now.AddMinutes(-5),
        coverage: new EvidenceCoverage(sampleSize: 100),
        inputs: inputs);

}
