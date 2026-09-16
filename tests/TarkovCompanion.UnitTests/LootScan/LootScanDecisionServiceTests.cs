using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recognition.Grid;

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
            [Recommendation(anchor)]);

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
            [Recommendation(anchor)]);

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
            [Recommendation(preferred, priority: 20), Recommendation(secondary, priority: 10)]);

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
            [Recommendation(incoming, RecommendationReasonCategory.Economics, valueRoubles: 10_000)],
            [Droppable(carriedLeft, 1_000), Droppable(carriedRight, 2_000)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Swap, decision.Verdict);
        Assert.Equal(3_000, decision.ReplacementCostRoubles);
        Assert.Equal(2, decision.Drops.Count);
        Assert.Equal(
            new[] { carriedLeft, carriedRight },
            decision.Drops.Select(item => item.Anchor).OrderBy(item => item.Column));
        Assert.Equal("capacity.bounded-swap", Assert.Single(decision.Reasons).Code);
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
            [Recommendation(incoming)],
            [CarriedPolicy(occupied, protectedItem, pinned, 1)]);

        var decision = Assert.Single(result.Decisions);
        Assert.Equal(LootScanVerdict.Leave, decision.Verdict);
        Assert.Empty(decision.Drops);
        Assert.Equal("capacity.no-supported-fit", Assert.Single(decision.Reasons).Code);
    }

    [Fact]
    public void ItemLargerThanEverySupportedOrientationIsLeft()
    {
        var anchor = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 2, 2, Cell(anchor, "oversized", 2, 2)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 1, 1),
            [Recommendation(anchor)]);

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
            [Recommendation(anchor)]);

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
    public void ChangedScreenshotInvalidatesEveryResolvedRecommendation()
    {
        var anchor = new GridCellAddress(0, 0);

        var result = Evaluate(
            CompleteGrid(InventoryGridSurface.VisibleLoot, 1, 1, Cell(anchor, "loot", 1, 1)),
            CompleteGrid(InventoryGridSurface.CarriedInventory, 2, 2),
            [Recommendation(anchor)],
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
            [Recommendation(anchor, decisionCompleteness: completeness, decisionFreshness: freshness)]);

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
            [Recommendation(anchor)],
            initiatingDeviceId: initiatingDevice);

        Assert.Equal(initiatingDevice, result.FocusDeviceId);
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
        RecommendationReasonCategory category = RecommendationReasonCategory.CurrentQuest,
        int priority = 10,
        long? valueRoubles = null,
        ResultCompleteness decisionCompleteness = ResultCompleteness.Complete,
        FreshnessState decisionFreshness = FreshnessState.Current)
    {
        EvidencedValue<long?> opportunityCost;
        OpportunityCostLineage? lineage;
        EvidenceProvenance decisionProvenance;
        if (valueRoubles is { } value)
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
            [new(category, "take", "The current profile supports taking this item.", priority, decisionProvenance)],
            opportunityCost,
            lineage,
            []);
        var evidencedDecision = new EvidencedValue<RecommendationDecision>(
            "recommendation.decision",
            decision,
            new(decisionCompleteness, decisionFreshness),
            decisionProvenance);
        return new(
            anchor,
            new RecommendationResult(
                $"recommendation-{anchor.Row}-{anchor.Column}",
                V2ContractVersion.Current,
                "loot-scan-tests-v1",
                SessionId,
                evidencedDecision));
    }

    private static LootScanCarriedPolicy Droppable(GridCellAddress anchor, long replacementValueRoubles) =>
        CarriedPolicy(
            anchor,
            protectedItem: false,
            pinned: false,
            replacementValueRoubles: replacementValueRoubles);

    private static LootScanCarriedPolicy CarriedPolicy(
        GridCellAddress anchor,
        bool protectedItem,
        bool pinned,
        long replacementValueRoubles) => new(
        anchor,
        Complete<bool?>("carried.protected", protectedItem, CatalogProvenance("protected")),
        Complete<bool?>("carried.pinned", pinned, CatalogProvenance("pinned")),
        Complete<long?>("carried.replacement-value", replacementValueRoubles, CatalogProvenance("replacement-value")));

    private static EvidencedValue<T> Complete<T>(
        string fieldId,
        T value,
        EvidenceProvenance? provenance = null) => new(
        fieldId,
        value,
        new(ResultCompleteness.Complete, FreshnessState.Current),
        provenance ?? ScreenshotProvenance());

    private static EvidenceProvenance ScreenshotProvenance(string suffix = "capture") => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        $"fixture://loot-scan/{suffix}",
        Now.AddMinutes(-1),
        EvidenceConfidence.Certain,
        new ProducerIdentity("loot-scan-tests", "1"));

    private static EvidenceProvenance CatalogProvenance(string suffix) => new(
        EvidenceSourceClass.PublicStructuredData,
        $"fixture://catalog/{suffix}",
        Now.AddMinutes(-5),
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
}
