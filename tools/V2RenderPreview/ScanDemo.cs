using TarkovCompanion.App.ViewModels.V2.LootScan;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.LootScan;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Inventory;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Core.Domain.Recommendations;
using TarkovCompanion.Core.Domain.Stash;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// Render-only fixtures for package 17: a loot decision result and a stash snapshot, built from
/// the same contracts the real capture path produces and evaluated by the real
/// <see cref="LootScanDecisionService"/>, so a headless render shows a populated workspace without
/// a game, a screenshot, or any invented presentation state. Dev tool only; never shipped.
/// </summary>
internal static class ScanDemo
{
    private const string ContentSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 12, 0, 0, TimeSpan.Zero);
    private static readonly CaptureSessionId SessionId = new(Guid.Parse("20000000-0000-4000-8000-000000000282"));

    internal static LootScanResult LootResult(InventoryProfileScope scope, bool rigFallback = false)
    {
        if (rigFallback)
        {
            return RigFallbackResult(scope);
        }

        // A container of six read footprints over an 8x6 grid, and a backpack that is nearly full,
        // so the planner produces takes, a swap and a leave of its own accord.
        GridCellRecognition[] loot =
        [
            Cell(0, 0, "fuel-conditioner", "Fuel conditioner", 1, 2),
            Cell(0, 1, "virtex-processor", "Virtex programmable processor", 1, 1),
            Cell(0, 2, "electric-drill", "Electric drill", 2, 1),
            Cell(2, 0, "bolts", "Bolts", 1, 1, quantity: 3),
            Cell(2, 1, "crickent-lighter", "Crickent lighter", 1, 1),
            Cell(2, 2, "bearing", "Bearing", 1, 1),
        ];
        GridCellRecognition[] carried =
        [
            Cell(0, 0, "ifak", "IFAK", 1, 1),
            Cell(0, 1, "water-bottle", "Water bottle", 1, 2),
            Cell(0, 2, "pistol-rounds", "9x19 PS", 1, 1, quantity: 60),
            Cell(0, 3, "headset", "Peltor ComTac", 2, 2),
            Cell(1, 0, "gp-coin", "GP coin", 1, 1),
            Cell(1, 2, "wires", "Wires", 1, 1, quantity: 3),
            Cell(2, 0, "tushonka", "Beef stew", 1, 1, quantity: 2),
            Cell(2, 1, "cat-figurine", "Cat figurine", 1, 1),
            Cell(2, 2, "car-battery", "Car battery", 2, 2),
        ];

        var request = new LootScanRequest(
            "render-preview-loot",
            SessionId,
            new CaptureCorrelationId(Guid.Parse("20000000-0000-4000-8000-000000000283")),
            new CaptureContextMetadata(null, "PMC · Regular", "Customs", null, null, null, "desktop-primary"),
            "render-artifact",
            1,
            ContentSha256,
            ContentSha256,
            "desktop-primary",
            Now,
            new LootScanRecommendationContext(
                scope,
                "render-snapshot",
                new ObservedInventoryEvidenceSnapshot(
                    Guid.Parse("20000000-0000-4000-8000-000000000285"),
                    scope,
                    "render-snapshot",
                    new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "inventory.complete"),
                    new EvidenceCoverage(fraction: 1),
                    Screenshot("inventory"),
                    [],
                    unresolvedCells: 0),
                new RecommendationRaidContext(
                    Complete<RecommendationRaidPhase?>("raid.phase", RecommendationRaidPhase.Middle, Screenshot("raid")),
                    Complete<RecommendationRaidRisk?>("raid.risk", RecommendationRaidRisk.Low, Screenshot("raid")))),
            new GridReconstructionResult(GridReconstructionOutcome.Complete, InventoryGridSurface.VisibleLoot, Grid(6, 8, loot), [], []),
            new GridReconstructionResult(GridReconstructionOutcome.Complete, InventoryGridSurface.CarriedInventory, Grid(4, 5, carried), [], []),
            [
                Recommendation(scope, 0, 0, "fuel-conditioner", RecommendationReasonCategory.CurrentQuest, 136_000, 2),
                Recommendation(scope, 0, 1, "virtex-processor", RecommendationReasonCategory.FutureQuest, 91_000, 1),
                Recommendation(scope, 0, 2, "electric-drill", RecommendationReasonCategory.Hideout, 64_000, 2),
                Recommendation(scope, 2, 0, "bolts", RecommendationReasonCategory.Hideout, 28_000, 1),
                Recommendation(scope, 2, 1, "crickent-lighter", RecommendationReasonCategory.Economics, 9_000, 1),
                Recommendation(scope, 2, 2, "bearing", RecommendationReasonCategory.CraftOrBarter, 12_000, 1),
            ],
            [
                Droppable(1, 2, "wires", 12_400),
                Droppable(2, 1, "cat-figurine", 8_000),
                Droppable(1, 0, "gp-coin", 32_000),
            ]);

        return new LootScanDecisionService().Evaluate(request);
    }

    /// <summary>A full backpack beside an empty rig slot, evaluated by the real planner.</summary>
    private static LootScanResult RigFallbackResult(InventoryProfileScope scope)
    {
        var incoming = Cell(0, 0, "fuel-conditioner", "Fuel conditioner", 1, 1);
        var backpack = new GridReconstructionResult(
            GridReconstructionOutcome.Complete,
            InventoryGridSurface.CarriedInventory,
            Grid(1, 1, [Cell(0, 0, "ifak", "IFAK", 1, 1)]),
            [],
            []);
        var rig = new GridReconstructionResult(
            GridReconstructionOutcome.Complete,
            InventoryGridSurface.CarriedInventory,
            Grid(1, 2, []),
            [],
            []);
        var pockets = new GridReconstructionResult(
            GridReconstructionOutcome.Complete,
            InventoryGridSurface.CarriedInventory,
            Grid(1, 1, [Cell(0, 0, "bandage", "Army bandage", 1, 1)]),
            [],
            []);
        var request = new LootScanRequest(
            "render-preview-rig-fallback",
            SessionId,
            new CaptureCorrelationId(Guid.Parse("20000000-0000-4000-8000-000000000286")),
            new CaptureContextMetadata(null, "PMC · Regular", "Customs", null, null, null, "desktop-primary"),
            "render-artifact",
            1,
            ContentSha256,
            ContentSha256,
            "desktop-primary",
            Now,
            new LootScanRecommendationContext(
                scope,
                "render-snapshot",
                new ObservedInventoryEvidenceSnapshot(
                    Guid.Parse("20000000-0000-4000-8000-000000000285"),
                    scope,
                    "render-snapshot",
                    new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "inventory.complete"),
                    new EvidenceCoverage(fraction: 1),
                    Screenshot("inventory"),
                    [],
                    unresolvedCells: 0),
                new RecommendationRaidContext(
                    Complete<RecommendationRaidPhase?>("raid.phase", RecommendationRaidPhase.Middle, Screenshot("raid")),
                    Complete<RecommendationRaidRisk?>("raid.risk", RecommendationRaidRisk.Low, Screenshot("raid")))),
            new GridReconstructionResult(
                GridReconstructionOutcome.Complete,
                InventoryGridSurface.VisibleLoot,
                Grid(2, 2, [incoming]),
                [],
                []),
            [
                new(CarriedGridIdentity.PrimaryBackpack, backpack),
                new(new(CarriedGridKind.TacticalRig, 0), rig),
                new(new(CarriedGridKind.Pockets, 0), pockets),
            ],
            carriedCoverageComplete: true,
            [Recommendation(scope, 0, 0, "fuel-conditioner", RecommendationReasonCategory.CurrentQuest, 68_000, 1)],
            []);

        return new LootScanDecisionService().Evaluate(request);
    }

    /// <param name="resolveId">Turns a demo item's id and name into a catalog id, where there is a catalog.</param>
    internal static StashSnapshotRecord StashRecord(InventoryProfileScope scope, Func<string, string, string>? resolveId = null)
    {
        GridCellRecognition Cell(
            int row,
            int column,
            string itemId,
            string displayName,
            int width,
            int height,
            int quantity = 1,
            string? nested = null) =>
            ScanDemo.Cell(row, column, resolveId?.Invoke(itemId, displayName) ?? itemId, displayName, width, height, quantity, nested);

        var provenance = Screenshot("stash");
        var ordinal = 0;
        StashCaptureRegion Region(string path, int rows, int columns, params GridCellRecognition[] cells) =>
            new($"region-{path}", "render-artifact", ordinal++, path,
                Complete<GridCellAddress?>("origin", new GridCellAddress(0, 0), provenance),
                Grid(rows, columns, cells));

        var stash = Region("stash", 8, 10,
            Cell(0, 0, "ammo-case", "Ammo case", 2, 2, nested: "stash/ammo-case"),
            Cell(0, 2, "key-tool", "Key tool", 2, 2),
            Cell(0, 4, "docs-case", "Docs case", 2, 2),
            Cell(0, 6, "grizzly", "Grizzly", 2, 2),
            Cell(0, 8, "lab-access", "Labs access keycard", 1, 1),
            Cell(1, 8, "bitcoin", "Physical bitcoin", 1, 1, quantity: 3),
            Cell(2, 0, "sv98", "SV-98", 5, 2),
            Cell(2, 5, "6b43", "6B43 Zabralo", 3, 4),
            Cell(2, 8, "altyn", "Altyn helmet", 2, 2),
            Cell(4, 0, "m4a1", "M4A1", 5, 2),
            Cell(4, 8, "fast-mt", "FAST MT helmet", 2, 2),
            Cell(6, 0, "backpack", "Tri-Zip backpack", 4, 2),
            Cell(6, 4, "medcase", "Medicine case", 2, 2),
            Cell(6, 6, "fuel", "Metal fuel tank", 2, 2),
            Cell(6, 8, "gas-analyzer", "Gas analyzer", 1, 2, quantity: 2));

        var cases = Region("stash/ammo-case", 2, 4,
            Cell(0, 0, "m855a1", "5.56 M855A1", 1, 1, quantity: 320),
            Cell(0, 1, "bt", "7.62 BT", 1, 1, quantity: 180),
            Cell(0, 2, "ap-6.3", "9x19 AP 6.3", 1, 1, quantity: 240),
            Cell(0, 3, "igolnik", "5.45 Igolnik", 1, 1, quantity: 120),
            Cell(1, 0, "m61", "7.62 M61", 1, 1, quantity: 200),
            Cell(1, 1, "pp", "5.45 PP", 1, 1, quantity: 300),
            Cell(1, 2, "rip", "9x19 RIP", 1, 1, quantity: 90),
            Cell(1, 3, "ap20", "12/70 AP-20", 1, 1, quantity: 60));

        var recognition = new StashRecognition(
            "render-stash-snapshot",
            [stash, cases],
            [
                new StashContainerCoverage("stash",
                    Complete<int?>("coverage.observed", 74, provenance),
                    Complete<int?>("coverage.total", 80, provenance)),
                new StashContainerCoverage("stash/ammo-case",
                    Complete<int?>("coverage.observed", 8, provenance),
                    Complete<int?>("coverage.total", 8, provenance)),
            ],
            Complete<long?>("stash.value", 18_600_000L, provenance),
            Complete<int?>("stash.unresolved", 6, provenance));

        return new StashSnapshotRecord(
            Guid.Parse("20000000-0000-4000-8000-000000000370"),
            scope,
            "render-snapshot",
            Now,
            isCurrent: true,
            new RecognitionResultEnvelope<StashRecognition>(
                new RecognitionResultHeader(
                    "render-stash-result",
                    V2ContractVersion.Current,
                    SessionId,
                    "render-artifact",
                    Now.AddMinutes(-12),
                    ScanIntent.Stash,
                    Complete<RecognizedContext?>("context", RecognizedContext.Stash, provenance)),
                Complete("stash", recognition, provenance)));
    }

    private static GridRecognition Grid(int rows, int columns, IReadOnlyList<GridCellRecognition> cells) => new(
        new GridGeometry(
            Complete<int?>("grid.rows", rows),
            Complete<int?>("grid.columns", columns),
            Complete<int?>("grid.cell-width", 64),
            Complete<int?>("grid.cell-height", 64)),
        cells);

    private static GridCellRecognition Cell(
        int row,
        int column,
        string itemId,
        string displayName,
        int width,
        int height,
        int quantity = 1,
        string? nested = null) =>
        new(new GridCellAddress(row, column), Complete($"cell.{row}.{column}", new RecognizedItem(
            Complete("item.id", itemId),
            Complete("item.name", displayName),
            Complete<int?>("item.quantity", quantity),
            Complete<int?>("item.width", width),
            Complete<int?>("item.height", height),
            Complete<bool?>("item.rotated", false),
            Complete<bool?>("item.found-in-raid", true),
            Complete("item.condition", ItemConditionReading.NotApplicable))), nested);

    private static LootScanCandidateRecommendation Recommendation(
        InventoryProfileScope scope,
        int row,
        int column,
        string itemId,
        RecommendationReasonCategory category,
        long valueRoubles,
        int occupiedSquares)
    {
        var catalog = Catalog("profile");
        IReadOnlyList<RecommendationNeed> needs = category switch
        {
            RecommendationReasonCategory.CurrentQuest => [Need(RecommendationNeedPurpose.Quest, 0, catalog)],
            RecommendationReasonCategory.FutureQuest => [Need(RecommendationNeedPurpose.Quest, 1, catalog)],
            RecommendationReasonCategory.Hideout => [Need(RecommendationNeedPurpose.Hideout, 0, catalog)],
            RecommendationReasonCategory.CraftOrBarter => [Need(RecommendationNeedPurpose.CraftOrBarter, 0, catalog)],
            _ => [],
        };
        return new(
            Binding(row, column, itemId),
            $"recommendation-{row}-{column}",
            scope,
            "render-snapshot",
            new RecommendationProfileFacts(
                new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "profile.complete"),
                catalog,
                Complete<RecommendationExplicitActionState?>("profile.explicit-action", RecommendationExplicitActionState.None, catalog),
                Complete<bool?>("profile.protected", false, catalog),
                Complete<bool?>("profile.pinned", false, catalog),
                Complete<bool?>("profile.wishlist", false, catalog),
                new RecommendationEventStateFacts(null, Complete<EventItemState?>("profile.event-state", EventItemState.Unknown, catalog)),
                needs),
            new RecommendationEconomics(
                Complete<long?>("economics.flea-gross", valueRoubles + 1_000, Catalog("flea-gross")),
                Complete<long?>("economics.flea-fee", 1_000, Catalog("flea-fee")),
                Complete<long?>("economics.flea-net", valueRoubles, Catalog("flea-net")),
                Complete<long?>("economics.trader", Math.Max(0, valueRoubles - 6_000), Catalog("trader")),
                Complete<int?>("economics.squares", occupiedSquares, Screenshot("squares")),
                Complete<double?>("economics.condition", 1, Screenshot("condition"))),
            new RecommendationScarcityFacts(Complete<RecommendationObtainabilityBand?>(
                "scarcity.obtainability",
                RecommendationObtainabilityBand.Available,
                Catalog("scarcity"))));
    }

    private static RecommendationNeed Need(RecommendationNeedPurpose purpose, int stepsAhead, EvidenceProvenance provenance) => new(
        $"render-{purpose.ToString().ToLowerInvariant()}-{stepsAhead}",
        "Render need",
        purpose,
        stepsAhead,
        1,
        false,
        new ResultStatus(ResultCompleteness.Complete, FreshnessState.Current, "need.complete"),
        provenance);

    private static LootScanCarriedPolicy Droppable(int row, int column, string itemId, long replacementValueRoubles) => new(
        Binding(row, column, itemId),
        Complete<bool?>("carried.protected", false, Catalog("protected")),
        Complete<bool?>("carried.pinned", false, Catalog("pinned")),
        Complete<long?>("carried.replacement-value", replacementValueRoubles, Catalog("replacement-value")));

    private static LootScanEvidenceBinding Binding(int row, int column, string itemId) =>
        new(SessionId, "render-artifact", 1, ContentSha256, new GridCellAddress(row, column), itemId);

    private static EvidencedValue<T> Complete<T>(string fieldId, T value, EvidenceProvenance? provenance = null) => new(
        fieldId,
        value,
        new(ResultCompleteness.Complete, FreshnessState.Current),
        provenance ?? Screenshot("capture"));

    private static EvidenceProvenance Screenshot(string suffix) => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        $"render://scan/{suffix}",
        Now.AddMinutes(-1),
        EvidenceConfidence.Certain,
        new ProducerIdentity("v2-render-preview", "1"));

    private static EvidenceProvenance Catalog(string suffix) => new(
        EvidenceSourceClass.PublicStructuredData,
        $"render://catalog/{suffix}",
        Now.AddMinutes(-5),
        EvidenceConfidence.Certain,
        new ProducerIdentity("v2-render-preview", "1"));
}
