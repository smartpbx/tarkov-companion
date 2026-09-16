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
            [Recommendation(preferred, "preferred", priority: 20), Recommendation(secondary, "secondary", priority: 10)]);

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
        var modelled = ModelledProvenance(CatalogProvenance("model-input"));
        var derived = ScoredDerivedProvenance(modelled);
        var economics = new RecommendationEconomics(
            Complete<long?>("economics.flea-gross", 101_000, derived),
            Complete<long?>("economics.flea-fee", 1_000, derived),
            Complete<long?>("economics.flea-net", 100_000, derived),
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

        var viewModel = new LootScanViewModel(result);
        var card = Assert.Single(viewModel.Decisions);

        Assert.Equal(1, viewModel.TakeCount);
        Assert.Equal("TAKE", card.VerdictLabel);
        Assert.Contains("120,000", card.ValueLabel, StringComparison.Ordinal);
        Assert.Contains("confidence", card.EvidenceLabel, StringComparison.Ordinal);
        Assert.True(card.HasPlacement);
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

    private static LootScanResult Evaluate(
        GridReconstructionResult visible,
        GridReconstructionResult carried,
        IReadOnlyList<LootScanCandidateRecommendation>? recommendations = null,
        IReadOnlyList<LootScanCarriedPolicy>? policies = null,
        string reviewedContentSha256 = SourceContentSha256,
        string initiatingDeviceId = "desktop-primary")
    {
        var request = new LootScanRequest(
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
        return new LootScanDecisionService().Evaluate(request);
    }

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
        int priority = 10,
        long? valueRoubles = null,
        int occupiedSquares = 1,
        DateTimeOffset? economicsObservedUtc = null,
        ResultCompleteness decisionCompleteness = ResultCompleteness.Complete,
        FreshnessState decisionFreshness = FreshnessState.Current,
        string reasonCode = "take")
    {
        EvidencedValue<long?> opportunityCost;
        OpportunityCostLineage? lineage;
        EvidenceProvenance decisionProvenance;
        if (valueRoubles is { } value && category != RecommendationReasonCategory.Economics)
        {
            var price = CatalogProvenance("price");
            var footprint = ScreenshotProvenance("footprint");
            decisionProvenance = DerivedProvenance(price, footprint);
            opportunityCost = Complete<long?>("recommendation.opportunity-cost", value, decisionProvenance);
            lineage = new(price, footprint);
        }
        else
        {
            decisionProvenance = CatalogProvenance("recommendation");
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
            [new(category, reasonCode, "The current profile supports taking this item.", priority, decisionProvenance)],
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

    private static EvidenceProvenance DerivedProvenance(params EvidenceProvenance[] inputs) => new(
        EvidenceSourceClass.DerivedCalculation,
        "fixture://loot-scan/opportunity-cost",
        Now,
        EvidenceConfidence.Unscored,
        new ProducerIdentity("loot-scan-tests", "1"),
        generatedUtc: Now,
        inputs: inputs);

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

    private static EvidenceProvenance ScoredDerivedProvenance(params EvidenceProvenance[] inputs) => new(
        EvidenceSourceClass.DerivedCalculation,
        "fixture://loot-scan/derived-price",
        Now.AddMinutes(-5),
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.9),
        new ProducerIdentity("loot-scan-tests", "1"),
        generatedUtc: Now.AddMinutes(-5),
        inputs: inputs);
}
