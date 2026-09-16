using System.Globalization;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using RecommendationResult = TarkovCompanion.Core.Abstractions.V2.RecommendationResult;
using RecommendationAction = TarkovCompanion.Core.Abstractions.V2.RecommendationAction;

namespace TarkovCompanion.UnitTests.LootScan;

public sealed class LootScanDecisionServiceTests
{
    private const string SourceContentSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ChangedContentSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly CaptureSessionId SessionId =
        new(Guid.Parse("20000000-0000-4000-8000-000000000282"));

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
            [Recommendation(incoming, "incoming", RecommendationReasonCategory.Economics, valueRoubles: 10_000, occupiedSquares: 2)],
            [Droppable(carriedLeft, "carried-left", 1_000), Droppable(carriedRight, "carried-right", 2_000)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Swap, decision.Verdict);
        Assert.Equal(3_000, decision.ReplacementCostRoubles);
        Assert.Equal(10_000, decision.Economics!.BestNetValueRoubles);
        Assert.Equal(5_000, decision.Economics.ValuePerSquareRoubles);
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
        Assert.Equal("economics.incomplete", Assert.Single(decision.Reasons).Code);
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
                RecommendationReasonCategory.Safety,
                valueRoubles: 1_000,
                reasonCode: "raid.risk.high")],
            [Droppable(anchor, "carried", 5_000)]);

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
            [new(evaluated.Binding, evaluated.Recommendation, economics)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("economics.incomplete", Assert.Single(decision.Reasons).Code);
        Assert.Equal("economics.model-review", decision.Economics!.Status.Code);
    }

    [Fact]
    public void NestedReasonEvidenceMustBeCurrentAndConfident()
    {
        var anchor = new GridCellAddress(0, 0);
        var invalid = new[]
        {
            CalculatedProvenance(
                "reason-with-unscored-input",
                Now.AddMinutes(-1),
                EvidenceConfidence.Certain,
                DirectProvenance("reason-unscored", Now.AddMinutes(-1), EvidenceConfidence.Unscored)),
            CalculatedProvenance(
                "reason-with-low-confidence-input",
                Now.AddMinutes(-1),
                EvidenceConfidence.Certain,
                DirectProvenance(
                    "reason-low-confidence",
                    Now.AddMinutes(-1),
                    new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.5))),
            CalculatedProvenance(
                "reason-with-stale-input",
                Now.AddMinutes(-1),
                EvidenceConfidence.Certain,
                DirectProvenance("reason-stale", Now.AddHours(-25), EvidenceConfidence.Certain)),
            DirectProvenance("reason-future", Now.AddMinutes(1), EvidenceConfidence.Certain),
        };

        foreach (var provenance in invalid)
        {
            var result = Evaluate(
                CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
                CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
                [RecommendationWithReasonEvidence(anchor, "loot", provenance)]);

            var decision = Assert.Single(result.Decisions);
            Assert.Equal(LootScanVerdict.Review, decision.Verdict);
            Assert.Contains(
                Assert.Single(decision.Reasons).Code,
                new[] { "recommendation.evidence-unreliable", "recommendation.expired" });
        }
    }

    [Fact]
    public void StaleOpportunityCostLineageCannotProduceDecisiveAdvice()
    {
        var anchor = new GridCellAddress(0, 0);
        var price = CatalogProvenance("stale-opportunity-price", Now.AddHours(-13));
        var footprint = ScreenshotProvenance("opportunity-footprint");
        var calculation = ScoredDerivedProvenance(price, footprint, Now);
        var opportunityCost = Complete<long?>("recommendation.opportunity-cost", 50_000, calculation);
        var recommendation = RecommendationWithReasonEvidence(
            anchor,
            "loot",
            CatalogProvenance("current-reason"),
            opportunityCost,
            new OpportunityCostLineage(price, footprint));

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [recommendation]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("recommendation.expired", Assert.Single(decision.Reasons).Code);
        Assert.Equal(FreshnessState.Stale, result.Status.Freshness);
    }

    [Fact]
    public void UnscoredLowConfidenceOrFutureOpportunityCostCannotProduceDecisiveAdvice()
    {
        var anchor = new GridCellAddress(0, 0);
        var footprint = ScreenshotProvenance("opportunity-footprint");
        var invalidPrices = new[]
        {
            DirectProvenance("unscored-opportunity-price", Now.AddMinutes(-1), EvidenceConfidence.Unscored),
            DirectProvenance(
                "low-confidence-opportunity-price",
                Now.AddMinutes(-1),
                new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.5)),
        };

        foreach (var price in invalidPrices)
        {
            var calculation = CalculatedProvenance(
                "cost-with-unreliable-price",
                Now.AddMinutes(-1),
                EvidenceConfidence.Certain,
                price,
                footprint);
            var recommendation = RecommendationWithReasonEvidence(
                anchor,
                "loot",
                CatalogProvenance("current-reason"),
                Complete<long?>("recommendation.opportunity-cost", 50_000, calculation),
                new OpportunityCostLineage(price, footprint));
            var result = Evaluate(
                CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
                CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
                [recommendation]);

            Assert.Equal(LootScanVerdict.Review, Assert.Single(result.Decisions).Verdict);
        }

        var currentPrice = CatalogProvenance("opportunity-price");
        var futureCalculation = CalculatedProvenance(
            "future-cost",
            Now.AddMinutes(1),
            EvidenceConfidence.Certain,
            currentPrice,
            footprint);
        var futureRecommendation = RecommendationWithReasonEvidence(
            anchor,
            "loot",
            CatalogProvenance("current-reason"),
            Complete<long?>("recommendation.opportunity-cost", 50_000, futureCalculation),
            new OpportunityCostLineage(currentPrice, footprint));
        var futureResult = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [futureRecommendation]);

        Assert.Equal(LootScanVerdict.Review, Assert.Single(futureResult.Decisions).Verdict);
    }

    [Fact]
    public void CallerPriorityCannotOverrideRulesetPlanningPrecedence()
    {
        var invalid = new GridCellAddress(0, 0);
        var valid = new GridCellAddress(0, 1);
        var result = Evaluate(
            CompleteGrid(
                InventoryGridSurface.VisibleLoot,
                1,
                2,
                Cell(invalid, "invalid-priority", 1, 1),
                Cell(valid, "valid-priority", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [
                Recommendation(invalid, "invalid-priority", priority: int.MaxValue),
                Recommendation(valid, "valid-priority", RecommendationReasonCategory.FutureQuest),
            ]);

        var rejected = result.Decisions.Single(item => item.SourceAnchor == invalid);
        Assert.Equal(LootScanVerdict.Review, rejected.Verdict);
        Assert.Equal("recommendation.precedence-mismatch", Assert.Single(rejected.Reasons).Code);
        Assert.Equal(LootScanVerdict.Take, result.Decisions.Single(item => item.SourceAnchor == valid).Verdict);
    }

    [Theory]
    [InlineData(RecommendationReasonCategory.CurrentQuest, "economics.flea-net.high")]
    [InlineData(RecommendationReasonCategory.Economics, "economics.price-missing")]
    public void ReasonCodeCannotMasqueradeAsAnotherRulesetRule(
        RecommendationReasonCategory category,
        string reasonCode)
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(
                anchor,
                "loot",
                category,
                priority: ExpectedPriority(category, DefaultReasonCode(category)),
                reasonCode: reasonCode)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("recommendation.precedence-mismatch", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void OversizedNestedRecommendationMetadataFailsClosedBeforePlanning()
    {
        var anchor = new GridCellAddress(0, 0);
        var provenance = CatalogProvenance("bounded-metadata");
        var reasons = Enumerable.Range(0, LootScanPlannerLimits.MaximumRecommendationReasons + 1)
            .Select(index => new TarkovCompanion.Core.Abstractions.V2.RecommendationReason(
                RecommendationReasonCategory.CurrentQuest,
                $"reason-{index:D3}",
                "Bounded reason",
                ExpectedPriority(RecommendationReasonCategory.CurrentQuest, "reason"),
                provenance))
            .ToArray();
        var recommendation = RecommendationWithDecision(
            anchor,
            "loot",
            new RecommendationDecision(
                RecommendationAction.Take,
                "Take this item.",
                reasons,
                AbsentOpportunityCost(provenance),
                null,
                []),
            provenance);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [recommendation]);

        Assert.Equal("recommendation.metadata-too-large", Assert.Single(Assert.Single(result.Decisions).Reasons).Code);
    }

    [Fact]
    public void OversizedRecommendationAlternativesFailClosedBeforePlanning()
    {
        var anchor = new GridCellAddress(0, 0);
        var provenance = CatalogProvenance("bounded-alternatives");
        var alternatives = Enumerable.Range(0, LootScanPlannerLimits.MaximumRecommendationSensitivities + 1)
            .Select(index => new RecommendationSensitivity(
                $"alternative-{index:D3}",
                "A bounded alternative.",
                RecommendationAction.Review))
            .ToArray();
        var decision = new RecommendationDecision(
            RecommendationAction.Take,
            "Take this item.",
            [new(
                RecommendationReasonCategory.CurrentQuest,
                "take",
                "The current profile supports taking this item.",
                ExpectedPriority(RecommendationReasonCategory.CurrentQuest, "take"),
                provenance)],
            AbsentOpportunityCost(provenance),
            null,
            alternatives);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [RecommendationWithDecision(anchor, "loot", decision, provenance)]);

        Assert.Equal("recommendation.metadata-too-large", Assert.Single(Assert.Single(result.Decisions).Reasons).Code);
    }

    [Fact]
    public void RecommendationValidationBudgetFailsClosed()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor, "loot")],
            maximumRecommendationWorkVisits: 1);

        Assert.Equal(
            "recommendation.validation-budget-exhausted",
            Assert.Single(Assert.Single(result.Decisions).Reasons).Code);
    }

    [Fact]
    public void RecommendationValidationHonorsCallerCancellation()
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
    public void ModelledFootprintDegradesEconomicProjectionWithoutThrowing()
    {
        var anchor = new GridCellAddress(0, 0);
        var evaluated = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 100_000);
        var modelledFootprint = ModelledProvenance(
            CatalogProvenance("modelled-footprint-input", Now.AddMinutes(-10)));
        var economics = EconomicsWith(
            valueRoubles: 100_000,
            footprintProvenance: modelledFootprint);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [new(evaluated.Binding, evaluated.Recommendation, economics)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("economics.model-review", decision.Economics!.Status.Code);
        var card = new LootScanDecisionViewModel(decision, Now, null, CultureInfo.InvariantCulture);
        Assert.Contains("ModelledEstimate", card.EconomicEvidenceLabel, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContractLimitPriceLineageDegradesEconomicProjectionWithoutThrowing(bool maximumDepth)
    {
        var anchor = new GridCellAddress(0, 0);
        var evaluated = Recommendation(
            anchor,
            "loot",
            RecommendationReasonCategory.Economics,
            valueRoubles: 100_000);
        var price = maximumDepth ? MaximumDepthProvenance() : MaximumCountProvenance();
        var economics = EconomicsWith(valueRoubles: 100_000, priceProvenance: price);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [new(evaluated.Binding, evaluated.Recommendation, economics)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("economics.lineage-too-complex", decision.Economics!.Status.Code);
    }

    [Fact]
    public void ChangedScreenshotInvalidatesEveryResolvedRecommendation()
    {
        var anchor = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 2, 2),
            [Recommendation(anchor, "loot")],
            reviewedContentSha256: ChangedContentSha256);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("capture.changed", Assert.Single(decision.Reasons).Code);
        Assert.Contains(result.Issues, issue => issue.Kind == LootScanIssueKind.CaptureChanged);
    }

    [Theory]
    [InlineData(ResultCompleteness.Complete, FreshnessState.Stale)]
    [InlineData(ResultCompleteness.Partial, FreshnessState.Current)]
    public void StaleOrIncompleteRecommendationCannotProduceDecisiveAdvice(
        ResultCompleteness completeness,
        FreshnessState freshness)
    {
        var anchor = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 2, 2),
            [Recommendation(anchor, "loot", decisionCompleteness: completeness, decisionFreshness: freshness)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("recommendation.incomplete", Assert.Single(decision.Reasons).Code);
    }

    [Theory]
    [InlineData(-25)]
    [InlineData(1)]
    public void ExpiredOrFutureRecommendationProvenanceCannotProduceDecisiveAdvice(int offsetHours)
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 2, 2),
            [Recommendation(anchor, "loot", decisionObservedUtc: Now.AddHours(offsetHours))]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Review, decision.Verdict);
        Assert.Equal("recommendation.expired", Assert.Single(decision.Reasons).Code);
        Assert.Equal(FreshnessState.Stale, result.Status.Freshness);
    }

    [Fact]
    public void RaidRecommendationUsesTheShortVolatileEvidenceWindow()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 2, 2),
            [Recommendation(
                anchor,
                "loot",
                RecommendationReasonCategory.Safety,
                reasonCode: "raid.risk.high",
                decisionObservedUtc: Now.AddMinutes(-15).AddTicks(-1))]);

        Assert.Equal(LootScanVerdict.Review, Assert.Single(result.Decisions).Verdict);
        Assert.Equal(FreshnessState.Stale, result.Status.Freshness);
    }

    [Fact]
    public void RecommendationAtItsAgeBoundaryRemainsCurrent()
    {
        var anchor = new GridCellAddress(0, 0);
        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 2, 2),
            [Recommendation(
                anchor,
                "loot",
                RecommendationReasonCategory.Safety,
                reasonCode: "raid.risk.high",
                decisionObservedUtc: Now.AddMinutes(-15))]);

        Assert.Equal(LootScanVerdict.Take, Assert.Single(result.Decisions).Verdict);
        Assert.Equal(FreshnessState.Current, result.Status.Freshness);
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
        Assert.Contains("CurrentQuest", Assert.Single(card.RecommendationReasonLabels), StringComparison.Ordinal);
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
                    valueRoubles: 1_000)],
                [Droppable(anchor, "carried", 5_000)]);

            Assert.Contains("5,000", Assert.Single(Assert.Single(result.Decisions).Reasons).Explanation, StringComparison.Ordinal);
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
        int maximumRecommendationWorkVisits = LootScanPlannerLimits.MaximumRecommendationWorkVisits)
    {
        var request = Request(
            visible,
            carried,
            recommendations,
            policies,
            reviewedContentSha256,
            initiatingDeviceId);
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
        string initiatingDeviceId = "desktop-primary") => new(
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
        int? priority = null,
        long? valueRoubles = null,
        int occupiedSquares = 1,
        DateTimeOffset? economicsObservedUtc = null,
        ResultCompleteness decisionCompleteness = ResultCompleteness.Complete,
        FreshnessState decisionFreshness = FreshnessState.Current,
        string? reasonCode = null,
        DateTimeOffset? decisionObservedUtc = null)
    {
        var effectiveReasonCode = reasonCode ?? DefaultReasonCode(category);
        EvidencedValue<long?> opportunityCost;
        OpportunityCostLineage? lineage;
        EvidenceProvenance decisionProvenance;
        if (valueRoubles is { } value && category != RecommendationReasonCategory.Economics)
        {
            var price = CatalogProvenance("price", decisionObservedUtc);
            var footprint = ScreenshotProvenance("footprint", decisionObservedUtc);
            decisionProvenance = ScoredDerivedProvenance(price, footprint, decisionObservedUtc ?? Now);
            opportunityCost = Complete<long?>("recommendation.opportunity-cost", value, decisionProvenance);
            lineage = new(price, footprint);
        }
        else
        {
            decisionProvenance = CatalogProvenance("recommendation", decisionObservedUtc);
            opportunityCost = new(
                "recommendation.opportunity-cost",
                null,
                new(ResultCompleteness.Unknown, FreshnessState.Current),
                decisionProvenance);
            lineage = null;
        }

        var decision = new RecommendationDecision(
            RecommendationAction.Take,
            "Take this item.",
            [new(
                category,
                effectiveReasonCode,
                "The current profile supports taking this item.",
                priority ?? ExpectedPriority(category, effectiveReasonCode),
                decisionProvenance)],
            opportunityCost,
            lineage,
            []);
        var evidencedDecision = new EvidencedValue<RecommendationDecision>(
            "recommendation.decision",
            decision,
            new(decisionCompleteness, decisionFreshness),
            decisionProvenance);
        return new(
            Binding(anchor, itemId),
            new RecommendationResult(
                $"recommendation-{anchor.Row}-{anchor.Column}",
                V2ContractVersion.Current,
                ExplainableRecommendationPolicy.CurrentRulesetVersion,
                SessionId,
                evidencedDecision),
            Economics(valueRoubles ?? 100_000, occupiedSquares, economicsObservedUtc));
    }

    private static LootScanCandidateRecommendation RecommendationWithReasonEvidence(
        GridCellAddress anchor,
        string itemId,
        EvidenceProvenance reasonProvenance,
        EvidencedValue<long?>? opportunityCost = null,
        OpportunityCostLineage? opportunityCostLineage = null)
    {
        var decisionProvenance = CatalogProvenance("decision-root");
        var decision = new RecommendationDecision(
            RecommendationAction.Take,
            "Take this item.",
            [new(
                RecommendationReasonCategory.CurrentQuest,
                "need.quest-current.fixture",
                "The current profile supports taking this item.",
                ExpectedPriority(RecommendationReasonCategory.CurrentQuest, "need.quest-current.fixture"),
                reasonProvenance)],
            opportunityCost ?? AbsentOpportunityCost(decisionProvenance),
            opportunityCostLineage,
            []);
        return RecommendationWithDecision(anchor, itemId, decision, decisionProvenance);
    }

    private static LootScanCandidateRecommendation RecommendationWithDecision(
        GridCellAddress anchor,
        string itemId,
        RecommendationDecision decision,
        EvidenceProvenance decisionProvenance) => new(
            Binding(anchor, itemId),
            new RecommendationResult(
                $"recommendation-{anchor.Row}-{anchor.Column}",
                V2ContractVersion.Current,
                ExplainableRecommendationPolicy.CurrentRulesetVersion,
                SessionId,
                new EvidencedValue<RecommendationDecision>(
                    "recommendation.decision",
                    decision,
                    new(ResultCompleteness.Complete, FreshnessState.Current),
                    decisionProvenance)),
            Economics(100_000, 1));

    private static EvidencedValue<long?> AbsentOpportunityCost(EvidenceProvenance provenance) => new(
        "recommendation.opportunity-cost",
        null,
        new(ResultCompleteness.Unknown, FreshnessState.Current),
        provenance);

    private static RecommendationEconomics EconomicsWith(
        long valueRoubles,
        EvidenceProvenance? priceProvenance = null,
        EvidenceProvenance? footprintProvenance = null)
    {
        var price = priceProvenance ?? CatalogProvenance("bounded-price");
        return new(
            Complete<long?>("economics.flea-gross", valueRoubles + 1_000, price),
            Complete<long?>("economics.flea-fee", 1_000, price),
            Complete<long?>("economics.flea-net", valueRoubles, price),
            Complete<long?>("economics.trader", valueRoubles - 1_000, CatalogProvenance("bounded-trader")),
            Complete<int?>("economics.squares", 1, footprintProvenance ?? ScreenshotProvenance("bounded-squares")),
            Complete<double?>("economics.condition", 1, ScreenshotProvenance("bounded-condition")));
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

    private static EvidenceProvenance MaximumCountProvenance()
    {
        var inputs = Enumerable.Range(0, EvidenceProvenance.MaxInputCount)
            .Select(index => CatalogProvenance($"count-{index}"))
            .ToArray();
        return new EvidenceProvenance(
            EvidenceSourceClass.DerivedCalculation,
            "fixture://loot-scan/count-limit",
            Now.AddMinutes(-1),
            new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
            new ProducerIdentity("loot-scan-tests", "1"),
            generatedUtc: Now.AddMinutes(-1),
            inputs: inputs);
    }

    private static EvidenceProvenance DirectProvenance(
        string suffix,
        DateTimeOffset observedUtc,
        EvidenceConfidence confidence) => new(
            EvidenceSourceClass.PublicStructuredData,
            $"fixture://loot-scan/{suffix}",
            observedUtc,
            confidence,
            new ProducerIdentity("loot-scan-tests", "1"));

    private static EvidenceProvenance CalculatedProvenance(
        string suffix,
        DateTimeOffset observedUtc,
        EvidenceConfidence confidence,
        params EvidenceProvenance[] inputs) => new(
            EvidenceSourceClass.DerivedCalculation,
            $"fixture://loot-scan/{suffix}",
            observedUtc,
            confidence,
            new ProducerIdentity("loot-scan-tests", "1"),
            generatedUtc: observedUtc,
            inputs: inputs);

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

    private static EvidencedValue<T> Complete<T>(
        string fieldId,
        T value,
        EvidenceProvenance? provenance = null) => new(
        fieldId,
        value,
        new(ResultCompleteness.Complete, FreshnessState.Current),
        provenance ?? ScreenshotProvenance());

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

    private static EvidenceProvenance ScoredDerivedProvenance(
        EvidenceProvenance first,
        EvidenceProvenance second,
        DateTimeOffset? observedUtc = null) => new(
        EvidenceSourceClass.DerivedCalculation,
        "fixture://loot-scan/derived-price",
        observedUtc ?? Now.AddMinutes(-5),
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
        new ProducerIdentity("loot-scan-tests", "1"),
        generatedUtc: observedUtc ?? Now.AddMinutes(-5),
        inputs: [first, second]);

    private static int ExpectedPriority(RecommendationReasonCategory category, string code)
    {
        var rule = category switch
        {
            RecommendationReasonCategory.ExplicitOverride => ExplainableRecommendationRule.ExplicitOverride,
            RecommendationReasonCategory.Safety when code == "event.allergic" => ExplainableRecommendationRule.EventAllergy,
            RecommendationReasonCategory.Safety when code == "item.protected" => ExplainableRecommendationRule.ProtectedItem,
            RecommendationReasonCategory.Safety when code == "event.untested" => ExplainableRecommendationRule.EventUntested,
            RecommendationReasonCategory.Safety when code == "event.safe" => ExplainableRecommendationRule.EventSafe,
            RecommendationReasonCategory.Safety when code.StartsWith("raid.", StringComparison.Ordinal) => ExplainableRecommendationRule.Economics,
            RecommendationReasonCategory.CurrentFoundInRaidQuest => ExplainableRecommendationRule.CurrentFoundInRaidQuest,
            RecommendationReasonCategory.CurrentQuest => ExplainableRecommendationRule.CurrentQuest,
            RecommendationReasonCategory.FutureQuest => ExplainableRecommendationRule.FutureQuest,
            RecommendationReasonCategory.Hideout => ExplainableRecommendationRule.Hideout,
            RecommendationReasonCategory.CraftOrBarter => ExplainableRecommendationRule.CraftOrBarter,
            RecommendationReasonCategory.SpecialistUtility => ExplainableRecommendationRule.SpecialistUtility,
            RecommendationReasonCategory.PinOrWishlist when code == "profile.pinned" => ExplainableRecommendationRule.Pin,
            RecommendationReasonCategory.PinOrWishlist when code == "profile.wishlist" => ExplainableRecommendationRule.Wishlist,
            RecommendationReasonCategory.ScarcityOrObtainability => ExplainableRecommendationRule.Scarcity,
            RecommendationReasonCategory.Economics => ExplainableRecommendationRule.Economics,
            RecommendationReasonCategory.EvidenceQuality => ExplainableRecommendationRule.EvidenceQuality,
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
        return ExplainableRecommendationPolicy.Default.PriorityOf(rule);
    }

    private static string DefaultReasonCode(RecommendationReasonCategory category) => category switch
    {
        RecommendationReasonCategory.ExplicitOverride => "override.explicit",
        RecommendationReasonCategory.Safety => "event.safe",
        RecommendationReasonCategory.CurrentFoundInRaidQuest => "need.quest-current-fir.fixture",
        RecommendationReasonCategory.CurrentQuest => "need.quest-current.fixture",
        RecommendationReasonCategory.FutureQuest => "need.quest-future.fixture",
        RecommendationReasonCategory.Hideout => "need.hideout.fixture",
        RecommendationReasonCategory.CraftOrBarter => "need.craft-barter.fixture",
        RecommendationReasonCategory.SpecialistUtility => "need.specialist.fixture",
        RecommendationReasonCategory.PinOrWishlist => "profile.pinned",
        RecommendationReasonCategory.ScarcityOrObtainability => "scarcity.fixture",
        RecommendationReasonCategory.Economics => "economics.flea-net.high",
        RecommendationReasonCategory.EvidenceQuality => "evidence.insufficient",
        _ => throw new ArgumentOutOfRangeException(nameof(category)),
    };
}
