using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.UnitTests.V2Contracts;

/// <summary>
/// The shapes #273, #282, #283, and #284 consume, frozen here so those worktrees do not fork
/// the recognition DTOs to add footprint, stash stitching, or flea condition.
/// </summary>
public sealed class DownstreamShapeContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

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
            V2ContractTestData.Complete<bool?>("item.foundInRaid", null),
            V2ContractTestData.Complete("item.condition", new ItemConditionReading(ItemConditionKind.Durability, 38, 50)));

        var json = JsonSerializer.Serialize(rotatedRifle, JsonOptions);
        var roundTrip = JsonSerializer.Deserialize<RecognizedItem>(json, JsonOptions)!;

        Assert.Equal(10, roundTrip.WidthCells.Value * roundTrip.HeightCells.Value);
        Assert.True(roundTrip.Rotated.Value);
        Assert.Equal(38, roundTrip.Condition.Value!.Current);
        Assert.Equal(50, roundTrip.Condition.Value.Maximum);
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(51, 50)]
    [InlineData(1, 0)]
    public void ConditionMustBeWithinItsVisibleMaximum(double current, double maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ItemConditionReading(ItemConditionKind.Durability, current, maximum));
    }

    [Fact]
    public void RecognizedFieldsCannotBeDropped()
    {
        var item = V2ContractTestData.Item();

        Assert.Throws<ArgumentNullException>(() => new RecognizedItem(
            item.CanonicalId,
            item.DisplayName,
            item.Quantity,
            null!,
            item.HeightCells,
            item.Rotated,
            item.FoundInRaid,
            item.Condition));
    }

    [Fact]
    public void StashRegionsKeepIdentityOrderMembershipOriginAndCoverage()
    {
        var first = Region("region-0", 0, 0);
        var second = Region("region-1", 1, 3);
        var stash = new StashRecognition(
            "snapshot-1",
            [first, second],
            [Coverage("stash", 70, 680)],
            V2ContractTestData.Complete<long?>("stash.total", 1_250_000),
            V2ContractTestData.Complete<int?>("stash.unresolved", 2));

        var roundTrip = JsonSerializer.Deserialize<StashRecognition>(
            JsonSerializer.Serialize(stash, JsonOptions),
            JsonOptions)!;

        Assert.Equal(["region-0", "region-1"], roundTrip.CapturedRegions.Select(region => region.RegionId));
        Assert.Equal(new GridCellAddress(3, 0), roundTrip.CapturedRegions[1].OriginInContainer.Value);
        Assert.Equal("stash", roundTrip.CapturedRegions[1].ContainerPath);
        Assert.Equal(680, roundTrip.Coverage.Single().TotalCells.Value);
    }

    [Fact]
    public void StashRejectsDuplicateCapturesAndUncoveredContainers()
    {
        Assert.Throws<ArgumentException>(() => new StashRecognition(
            "snapshot-1",
            [Region("region-0", 0, 0), Region("region-1", 0, 3)],
            [Coverage("stash", 70, 680)],
            V2ContractTestData.Complete<long?>("stash.total", null),
            V2ContractTestData.Complete<int?>("stash.unresolved", 0)));

        Assert.Throws<ArgumentException>(() => new StashRecognition(
            "snapshot-1",
            [Region("region-0", 0, 0, "stash/backpack-1")],
            [Coverage("stash", 70, 680)],
            V2ContractTestData.Complete<long?>("stash.total", null),
            V2ContractTestData.Complete<int?>("stash.unresolved", 0)));

        Assert.Throws<ArgumentOutOfRangeException>(() => Coverage("stash", 700, 680));
    }

    [Fact]
    public void UnstitchedRegionHasAnAbsentOrigin()
    {
        var unplaced = new EvidencedValue<GridCellAddress?>(
            "region.origin",
            null,
            new ResultStatus(ResultCompleteness.Unknown, FreshnessState.Current),
            V2ContractTestData.ScreenshotProvenance());

        var region = new StashCaptureRegion("region-2", "artifact-2", 2, "stash", unplaced, V2ContractTestData.Grid());

        Assert.Null(region.OriginInContainer.Value);
    }

    [Fact]
    public void FleaRowKeepsRowBoundsConditionAndRawLines()
    {
        var rowBounds = new EvidenceRegion(300, 410, 1100, 72, EvidenceCoordinateSpace.SourcePixels);
        var template = V2ContractTestData.Item("armor", 3, 3);
        var armor = new RecognizedItem(
            template.CanonicalId,
            template.DisplayName,
            template.Quantity,
            template.WidthCells,
            template.HeightCells,
            template.Rotated,
            template.FoundInRaid,
            V2ContractTestData.Complete("item.condition", new ItemConditionReading(ItemConditionKind.Durability, 41.5, 60)));
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
        var roundTrip = JsonSerializer.Deserialize<FleaRecognitionResult>(
            JsonSerializer.Serialize(result, JsonOptions),
            JsonOptions)!;

        var row = roundTrip.Recognition.Result.Value!.Listings.Single();
        Assert.Equal(rowBounds, row.RowBounds);
        Assert.Equal(41.5, row.Item.Value!.Condition.Value!.Current);
        Assert.Equal("41.5/60  84 000", roundTrip.Recognition.Result.Value.RawOcrLines.Single().Text.Value);
    }

    [Fact]
    public void ContractListsAreCopiedAtConstruction()
    {
        var cells = new List<GridCellRecognition>
        {
            new(new GridCellAddress(0, 0), V2ContractTestData.Complete("grid.0.0", V2ContractTestData.Item())),
        };
        var grid = new GridRecognition(V2ContractTestData.Grid().Geometry, cells);

        cells.Clear();

        Assert.Single(grid.Cells);
        Assert.Throws<ArgumentException>(() => new GridRecognition(grid.Geometry, [null!]));
    }

    private static StashCaptureRegion Region(string id, int order, int originRow, string container = "stash") => new(
        id,
        $"artifact-{order}",
        order,
        container,
        V2ContractTestData.Complete<GridCellAddress?>("region.origin", new GridCellAddress(originRow, 0)),
        V2ContractTestData.Grid());

    private static StashContainerCoverage Coverage(string container, int observed, int total) => new(
        container,
        V2ContractTestData.Complete<int?>("coverage.observed", observed),
        V2ContractTestData.Complete<int?>("coverage.total", total));
}
