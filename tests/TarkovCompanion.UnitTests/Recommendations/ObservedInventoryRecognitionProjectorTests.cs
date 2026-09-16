using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Infrastructure.Persistence.Inventory;
using TarkovCompanion.Infrastructure.Persistence.Repositories;
using TarkovCompanion.UnitTests.V2Contracts;

namespace TarkovCompanion.UnitTests.Recommendations;

public sealed class ObservedInventoryRecognitionProjectorTests
{
    [Fact]
    public void OverlappingCaptureOfTheSameCellIsCountedOnce()
    {
        var stored = StoredSnapshot(Region("r-1", 0, quantity: 3), Region("r-2", 1, quantity: 3));

        var result = new ObservedInventoryRecognitionProjector().Project(stored);

        Assert.Equal(ResultCompleteness.Complete, result.Status.Completeness);
        var item = Assert.Single(result.Items);
        Assert.Equal(3, item.TotalQuantity.Value);
        Assert.Equal(3, item.FoundInRaidQuantity.Value);
    }

    [Fact]
    public void ConflictingOverlapIsExcludedInsteadOfInflatingHoldings()
    {
        var stored = StoredSnapshot(Region("r-1", 0, quantity: 3), Region("r-2", 1, quantity: 4));

        var result = new ObservedInventoryRecognitionProjector().Project(stored);

        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
        Assert.Empty(result.Items);
        Assert.Equal(1, result.UnresolvedCells);
    }

    [Fact]
    public void UnplacedRegionDoesNotContributeACount()
    {
        var region = new StashCaptureRegion(
            "r-unplaced",
            "artifact-unplaced",
            0,
            "stash",
            V2ContractTestData.Unknown<GridCellAddress?>("region.origin"),
            V2ContractTestData.Grid(V2ContractTestData.Cell(0, 0, Item(7))));

        var result = new ObservedInventoryRecognitionProjector().Project(StoredSnapshot(region));

        Assert.Empty(result.Items);
        Assert.Equal(ResultCompleteness.Partial, result.Status.Completeness);
        Assert.Equal(1, result.UnresolvedCells);
    }

    private static ObservedInventorySnapshot StoredSnapshot(params StashCaptureRegion[] regions)
    {
        var stash = new StashRecognition(
            "data-snapshot-1",
            regions,
            [
                new StashContainerCoverage(
                    "stash",
                    V2ContractTestData.Complete<int?>("coverage.observed", 40),
                    V2ContractTestData.Complete<int?>("coverage.total", 40)),
            ],
            V2ContractTestData.Unknown<long?>("stash.value"),
            V2ContractTestData.Complete<int?>("stash.unresolved", 0));
        var envelope = new RecognitionResultEnvelope<StashRecognition>(
            V2ContractTestData.Header(RecognizedContext.Stash),
            V2ContractTestData.Complete("stash.result", stash));
        return new ObservedInventorySnapshot(
            Guid.Parse("75000000-0000-0000-0000-000000000001"),
            Guid.Parse("75000000-0000-0000-0000-000000000002"),
            "wipe-2026-09",
            "pvp",
            V2ContractTestData.ObservedUtc.AddMinutes(1),
            true,
            envelope);
    }

    private static StashCaptureRegion Region(string id, int ordinal, int quantity) => new(
        id,
        $"artifact-{id}",
        ordinal,
        "stash",
        V2ContractTestData.Complete<GridCellAddress?>("region.origin", new GridCellAddress(0, 0)),
        V2ContractTestData.Grid(V2ContractTestData.Cell(0, 0, Item(quantity))));

    private static RecognizedItem Item(int quantity) => new(
        V2ContractTestData.Complete("item.id", "item-a"),
        V2ContractTestData.Complete("item.name", "Item A"),
        V2ContractTestData.Complete<int?>("item.quantity", quantity),
        V2ContractTestData.Complete<int?>("item.width", 1),
        V2ContractTestData.Complete<int?>("item.height", 1),
        V2ContractTestData.Complete<bool?>("item.rotated", false),
        V2ContractTestData.Complete<bool?>("item.found-in-raid", true),
        V2ContractTestData.Complete("item.condition", ItemConditionReading.NotApplicable));
}
