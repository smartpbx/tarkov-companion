using System.Text.Json;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

/// <summary>
/// The shapes #273, #282, #283, and #284 consume, frozen here so those worktrees do not fork
/// the recognition DTOs to add footprint, stash stitching, nesting, or flea condition.
/// </summary>
public sealed class DownstreamShapeContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = V2ContractJson.Options;

    [Fact]
    public void ItemCarriesDisplayedFootprintRotationAndCondition()
    {
        var rotatedRifle = new RecognizedItem(
            V2ContractTestData.Complete("item.id", "rifle"),
            V2ContractTestData.Complete("item.name", "Rifle"),
            V2ContractTestData.Complete<int?>("item.quantity", 1),
            V2ContractTestData.Complete<int?>("item.width", 2),
            V2ContractTestData.Complete<int?>("item.height", 5),
            V2ContractTestData.Complete<bool?>("item.rotated", true),
            V2ContractTestData.Unknown<bool?>("item.foundInRaid"),
            V2ContractTestData.Complete("item.condition", new ItemConditionReading(ItemConditionKind.Durability, 38, 50)));

        var roundTrip = JsonSerializer.Deserialize<RecognizedItem>(JsonSerializer.Serialize(rotatedRifle, JsonOptions), JsonOptions)!;

        Assert.Equal(10, roundTrip.WidthCells.Value * roundTrip.HeightCells.Value);
        Assert.True(roundTrip.Rotated.Value);
        Assert.Null(roundTrip.FoundInRaid.Value);
        Assert.Equal(38, roundTrip.Condition.Value!.Current);
        Assert.Equal(50, roundTrip.Condition.Value.Maximum);
    }

    [Fact]
    public void ConditionIsVisibleOrExplicitlyNotApplicable()
    {
        Assert.Null(ItemConditionReading.NotApplicable.Current);
        Assert.Equal(3, new ItemConditionReading(ItemConditionKind.Charges, 3, 4).Current);
        Assert.Throws<ArgumentException>(() => new ItemConditionReading(ItemConditionKind.NotApplicable, 1, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ItemConditionReading(ItemConditionKind.Durability, null, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ItemConditionReading(ItemConditionKind.Durability, -1, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ItemConditionReading(ItemConditionKind.Durability, 51, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ItemConditionReading(ItemConditionKind.Uses, 1, 0));
    }

    [Fact]
    public void RecognizedFieldsCannotBeDroppedOrNegative()
    {
        var item = V2ContractTestData.Item();
        var negativeCandidate = new EvidencedValue<int?>(
            "item.quantity",
            null,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current),
            V2ContractTestData.ScreenshotProvenance(),
            candidates: [new EvidenceCandidate<int?>("q", "-2", -2, V2ContractTestData.ScreenshotProvenance())]);

        Assert.Throws<ArgumentNullException>(() => new RecognizedItem(
            item.CanonicalId, item.DisplayName, item.Quantity, null!, item.HeightCells, item.Rotated, item.FoundInRaid, item.Condition));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RecognizedItem(
            item.CanonicalId, item.DisplayName, negativeCandidate, item.WidthCells, item.HeightCells, item.Rotated, item.FoundInRaid, item.Condition));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FleaListingRecognition(
            new EvidenceRegion(0, 0, 10, 10, EvidenceCoordinateSpace.SourcePixels),
            V2ContractTestData.Complete("row.item", item),
            V2ContractTestData.Complete<long?>("row.price", -1),
            V2ContractTestData.Complete<int?>("row.quantity", 1),
            V2ContractTestData.Complete<long?>("row.unitPrice", 1)));
    }

    [Fact]
    public void FootprintsStayInsideTheGridAndDoNotOverlap()
    {
        var wide = V2ContractTestData.Item("rifle", width: 5, height: 2);

        Assert.Equal(2, V2ContractTestData.Grid(V2ContractTestData.Cell(0, 0, wide), V2ContractTestData.Cell(0, 5, wide)).Cells.Count);
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Grid(V2ContractTestData.Cell(0, 6, wide)));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Grid(V2ContractTestData.Cell(3, 0, wide)));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Grid(V2ContractTestData.Cell(0, 0, wide), V2ContractTestData.Cell(1, 4)));
    }

    [Fact]
    public void StashRegionsKeepIdentityOrderMembershipOriginAndCoverage()
    {
        var stash = new StashRecognition(
            "snapshot-1",
            [
                Region("region-0", 0, 0, V2ContractTestData.Grid(V2ContractTestData.Cell(0, 0, nested: "stash/backpack-1"))),
                Region("region-1", 1, 3),
                Region("region-2", 2, 0, container: "stash/backpack-1"),
            ],
            [Coverage("stash", 70, 680), Coverage("stash/backpack-1", 20, 20)],
            V2ContractTestData.Complete<long?>("stash.total", 1_250_000),
            V2ContractTestData.Complete<int?>("stash.unresolved", 2));

        var roundTrip = JsonSerializer.Deserialize<StashRecognition>(JsonSerializer.Serialize(stash, JsonOptions), JsonOptions)!;

        Assert.Equal(["region-0", "region-1", "region-2"], roundTrip.CapturedRegions.Select(region => region.RegionId));
        Assert.Equal(new GridCellAddress(3, 0), roundTrip.CapturedRegions[1].OriginInContainer.Value);
        Assert.Equal("stash/backpack-1", roundTrip.CapturedRegions[2].ContainerPath);
        Assert.Equal("stash/backpack-1", roundTrip.CapturedRegions[0].Grid.Cells.Single().NestedContainerPath);
        Assert.Equal(680, roundTrip.Coverage[0].TotalCells.Value);
    }

    [Fact]
    public void StashRejectsDuplicatesUncoveredContainersAndInventedNesting()
    {
        Assert.Throws<ArgumentException>(() => Stash([Region("region-0", 0, 0), Region("region-1", 0, 3)], [Coverage("stash", 70, 680)]));
        Assert.Throws<ArgumentException>(() => Stash([Region("region-0", 0, 0, container: "stash/backpack-1")], [Coverage("stash", 70, 680)]));
        Assert.Throws<ArgumentException>(() => Stash(
            [Region("region-0", 0, 0), Region("region-1", 1, 0, container: "stash/backpack-1")],
            [Coverage("stash", 70, 680), Coverage("stash/backpack-1", 20, 20)]));
        Assert.Throws<ArgumentException>(() => Stash(
            [Region("region-0", 0, 0, V2ContractTestData.Grid(V2ContractTestData.Cell(0, 0, nested: "other/backpack-1")))],
            [Coverage("stash", 70, 680)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Coverage("stash", 700, 680));
        Assert.Throws<ArgumentException>(() => Coverage("stash//x", 1, 2));
    }

    [Fact]
    public void UnstitchedRegionHasAnAbsentOrigin()
    {
        var region = new StashCaptureRegion(
            "region-2", "artifact-2", 2, "stash",
            V2ContractTestData.Unknown<GridCellAddress?>("region.origin"),
            V2ContractTestData.Grid());

        Assert.Null(region.OriginInContainer.Value);
    }

    [Fact]
    public void FleaRowKeepsRowBoundsConditionAndRawLines()
    {
        var rowBounds = new EvidenceRegion(300, 410, 1100, 72, EvidenceCoordinateSpace.SourcePixels);
        var armor = V2ContractTestData.Item("armor", 3, 3, new ItemConditionReading(ItemConditionKind.Durability, 41.5, 60));
        var page = new FleaPageRecognition(
            [
                new FleaListingRecognition(
                    rowBounds,
                    V2ContractTestData.Complete("row.item", armor),
                    V2ContractTestData.Complete<long?>("row.price", 84_000),
                    V2ContractTestData.Complete<int?>("row.quantity", 1),
                    V2ContractTestData.Complete<long?>("row.unitPrice", 84_000)),
            ],
            [new RawOcrLine(V2ContractTestData.Complete("raw.0", "41.5/60  84 000"))]);

        var result = new FleaRecognitionResult(new RecognitionResultEnvelope<FleaPageRecognition>(
            V2ContractTestData.Header(RecognizedContext.Flea),
            V2ContractTestData.Complete("result.flea", page)));
        var roundTrip = JsonSerializer.Deserialize<FleaRecognitionResult>(JsonSerializer.Serialize(result, JsonOptions), JsonOptions)!;

        var row = roundTrip.Recognition.Result.Value!.Listings.Single();
        Assert.Equal(rowBounds, row.RowBounds);
        Assert.Equal(41.5, row.Item.Value!.Condition.Value!.Current);
        Assert.Equal("41.5/60  84 000", roundTrip.Recognition.Result.Value.RawOcrLines.Single().Text.Value);
    }

    [Fact]
    public void ContractListsAreCopiedAndImmutable()
    {
        var cells = new List<GridCellRecognition> { V2ContractTestData.Cell(0, 0) };
        var grid = new GridRecognition(V2ContractTestData.Grid().Geometry, cells);

        cells.Clear();

        Assert.Single(grid.Cells);
        Assert.False(grid.Cells is GridCellRecognition[]);
        Assert.Throws<ArgumentException>(() => new GridRecognition(grid.Geometry, [null!]));
    }

    private static StashRecognition Stash(IReadOnlyList<StashCaptureRegion> regions, IReadOnlyList<StashContainerCoverage> coverage) => new(
        "snapshot-1",
        regions,
        coverage,
        V2ContractTestData.Unknown<long?>("stash.total"),
        V2ContractTestData.Complete<int?>("stash.unresolved", 0));

    private static StashCaptureRegion Region(string id, int ordinal, int originRow, GridRecognition? grid = null, string container = "stash") => new(
        id,
        $"artifact-{ordinal}",
        ordinal,
        container,
        V2ContractTestData.Complete<GridCellAddress?>("region.origin", new GridCellAddress(originRow, 0)),
        grid ?? V2ContractTestData.Grid());

    private static StashContainerCoverage Coverage(string container, int observed, int total) => new(
        container,
        V2ContractTestData.Complete<int?>("coverage.observed", observed),
        V2ContractTestData.Complete<int?>("coverage.total", total));
}
