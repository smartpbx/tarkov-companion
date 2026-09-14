using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void GridFitIncludesItemCandidatesSpanCandidatesAndCorrectionHistory()
    {
        var anchor = new GridCellAddress(0, 9);
        var current = V2ContractTestData.Item(width: 1);

        Assert.Throws<ArgumentException>(() => new GridRecognition(
            V2ContractTestData.Grid().Geometry,
            [new GridCellRecognition(anchor, ItemWithCandidate(current, V2ContractTestData.Item(width: 2)))]));

        var candidateSpan = WithSpans(
            SpanWithCandidate(1, 2),
            V2ContractTestData.Complete<int?>("item.height", 1));
        Assert.Throws<ArgumentException>(() => new GridRecognition(
            V2ContractTestData.Grid().Geometry,
            [new GridCellRecognition(anchor, V2ContractTestData.Complete("grid.0.9", candidateSpan))]));

        var correctedSpan = WithSpans(
            CorrectedSpan(original: 2, corrected: 1),
            V2ContractTestData.Complete<int?>("item.height", 1));
        Assert.Throws<ArgumentException>(() => new GridRecognition(
            V2ContractTestData.Grid().Geometry,
            [new GridCellRecognition(anchor, V2ContractTestData.Complete("grid.0.9", correctedSpan))]));
    }

    [Fact]
    public void HostileGridJsonCannotHideAnEscapingItemOrSpanCandidate()
    {
        var json = JsonSerializer.Serialize(
            V2ContractTestData.Grid(V2ContractTestData.Cell(0, 9, V2ContractTestData.Item(width: 1))),
            JsonOptions);

        Assert.ThrowsAny<ArgumentException>(() => MutateGrid(json, grid =>
            grid["cells"]![0]!["item"]!["candidates"] = JsonSerializer.SerializeToNode(
                ItemWithCandidate(V2ContractTestData.Item(width: 1), V2ContractTestData.Item(width: 2)).Candidates,
                JsonOptions)));
        Assert.ThrowsAny<ArgumentException>(() => MutateGrid(json, grid =>
            grid["cells"]![0]!["item"]!["value"]!["widthCells"]!["candidates"] = JsonSerializer.SerializeToNode(
                SpanWithCandidate(1, 2).Candidates,
                JsonOptions)));
    }

    [Fact]
    public void AnchorsSitInsideAKnownGridEvenWithoutAnItemOrSize()
    {
        var unreadItem = new GridCellRecognition(new GridCellAddress(4, 0), V2ContractTestData.Unknown<RecognizedItem>("grid.4.0"));
        var unreadWidth = WithSpans(V2ContractTestData.Unknown<int?>("item.width"), V2ContractTestData.Complete<int?>("item.height", 1));
        var tooWideUnreadHeight = WithSpans(V2ContractTestData.Complete<int?>("item.width", 11), V2ContractTestData.Unknown<int?>("item.height"));

        Assert.Throws<ArgumentException>(() => V2ContractTestData.Grid(unreadItem));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Grid(V2ContractTestData.Cell(0, 10, unreadWidth)));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Grid(V2ContractTestData.Cell(0, 0, tooWideUnreadHeight)));
        Assert.Single(V2ContractTestData.Grid(V2ContractTestData.Cell(3, 9, unreadWidth)).Cells);
    }

    [Fact]
    public void GridCoordinatesSizesAndSpansHaveFiniteUpperBounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridCellAddress(GridGeometry.MaxRows, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GridCellAddress(0, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => V2ContractTestData.Item(width: GridGeometry.MaxColumns + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => V2ContractTestData.Item(height: int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => Geometry(V2ContractTestData.Complete<int?>("grid.rows", GridGeometry.MaxRows + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Geometry(new EvidencedValue<int?>(
            "grid.rows",
            null,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current),
            V2ContractTestData.ScreenshotProvenance(),
            candidates: [new EvidenceCandidate<int?>("rows", "2147483647", int.MaxValue, V2ContractTestData.ScreenshotProvenance())])));
        Assert.Throws<ArgumentOutOfRangeException>(() => Coverage("stash", 1, GridGeometry.MaxCells + 1));
    }

    [Fact]
    public void AnUnreadGridIsBoundedByTheContractCellSpace()
    {
        var unread = Geometry(V2ContractTestData.Unknown<int?>("grid.rows"), V2ContractTestData.Unknown<int?>("grid.columns"));
        var everyCell = V2ContractTestData.Item("everything", GridGeometry.MaxColumns, GridGeometry.MaxRows);

        // The largest footprint expands to exactly the bounded space, and one more anchor overlaps it.
        Assert.Single(new GridRecognition(unread, [V2ContractTestData.Cell(0, 0, everyCell)]).Cells);
        Assert.Throws<ArgumentException>(() => new GridRecognition(
            unread,
            [V2ContractTestData.Cell(0, 0, everyCell), V2ContractTestData.Cell(GridGeometry.MaxRows - 1, GridGeometry.MaxColumns - 1)]));
        Assert.Throws<ArgumentException>(() => new GridRecognition(
            unread,
            [V2ContractTestData.Cell(GridGeometry.MaxRows - 1, 0, V2ContractTestData.Item(height: 2))]));
        Assert.Throws<ArgumentException>(() => V2ContractTestData.Grid(
            Enumerable.Range(0, 41).Select(_ => V2ContractTestData.Cell(0, 0)).ToArray()));
    }

    [Fact]
    public void HostileGridJsonCannotOverflowTheFitOrAmplifyTheWalk()
    {
        var json = JsonSerializer.Serialize(V2ContractTestData.Grid(V2ContractTestData.Cell(0, 0)), JsonOptions);

        Assert.ThrowsAny<ArgumentException>(() => MutateGrid(json, grid => grid["cells"]![0]!["anchor"]!["row"] = int.MaxValue));
        Assert.ThrowsAny<ArgumentException>(() => MutateGrid(json, grid => grid["cells"]![0]!["anchor"]!["column"] = 10));
        Assert.ThrowsAny<ArgumentException>(() => MutateGrid(json, grid => grid["cells"]![0]!["item"]!["value"]!["widthCells"]!["value"] = 100_000));
        Assert.ThrowsAny<ArgumentException>(() => MutateGrid(json, grid => grid["geometry"]!["rows"]!["value"] = int.MaxValue));
        Assert.ThrowsAny<ArgumentException>(() => MutateGrid(json, grid =>
        {
            grid["geometry"]!["rows"] = JsonNode.Parse(JsonSerializer.Serialize(V2ContractTestData.Unknown<int?>("grid.rows"), JsonOptions));
            grid["cells"]![0]!["anchor"]!["row"] = GridGeometry.MaxRows - 1;
            grid["cells"]![0]!["item"]!["value"]!["heightCells"]!["value"] = 2;
        }));
    }

    [Fact]
    public void PlacedRegionStaysInsideTheContainerCellSpace()
    {
        Assert.Throws<ArgumentException>(() => Region("region-0", 0, GridGeometry.MaxRows - 2));
        Assert.Equal(
            new GridCellAddress(GridGeometry.MaxRows - 4, 0),
            Region("region-0", 0, GridGeometry.MaxRows - 4).OriginInContainer.Value);
    }

    [Fact]
    public void DeterminedCellsStayInsideTheContainerWhenRegionDimensionsAreUnread()
    {
        var rowOutside = UnreadGrid(V2ContractTestData.Cell(GridGeometry.MaxRows - 1, 0));
        var columnOutside = UnreadGrid(V2ContractTestData.Cell(0, GridGeometry.MaxColumns - 1));
        var footprintOutside = UnreadGrid(V2ContractTestData.Cell(
            GridGeometry.MaxRows - 2,
            0,
            V2ContractTestData.Item(height: 2)));
        var boundary = UnreadGrid(V2ContractTestData.Cell(
            GridGeometry.MaxRows - 2,
            GridGeometry.MaxColumns - 2));

        Assert.Throws<ArgumentException>(() => RegionAt(new GridCellAddress(1, 0), rowOutside));
        Assert.Throws<ArgumentException>(() => RegionAt(new GridCellAddress(0, 1), columnOutside));
        Assert.Throws<ArgumentException>(() => RegionAt(new GridCellAddress(1, 0), footprintOutside));
        Assert.Equal(
            new GridCellAddress(1, 1),
            RegionAt(new GridCellAddress(1, 1), boundary).OriginInContainer.Value);

        var candidateOrigin = new EvidencedValue<GridCellAddress?>(
            "region.origin",
            null,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current),
            V2ContractTestData.ScreenshotProvenance(),
            candidates:
            [
                new EvidenceCandidate<GridCellAddress?>(
                    "origin-1", "One row down", new GridCellAddress(1, 0),
                    V2ContractTestData.ScreenshotProvenance()),
            ]);
        Assert.Throws<ArgumentException>(() => new StashCaptureRegion(
            "region-0", "artifact-0", 0, "stash", candidateOrigin, rowOutside));
    }

    [Fact]
    public void AbsoluteRegionFitIncludesItemAndSpanCandidates()
    {
        var anchor = new GridCellAddress(0, GridGeometry.MaxColumns - 2);
        var origin = new GridCellAddress(0, 1);
        var current = V2ContractTestData.Item(width: 1);
        var candidateItemGrid = UnreadGrid(new GridCellRecognition(
            anchor,
            ItemWithCandidate(current, V2ContractTestData.Item(width: 2))));
        var candidateSpanGrid = UnreadGrid(new GridCellRecognition(
            anchor,
            V2ContractTestData.Complete(
                "grid.0.62",
                WithSpans(
                    SpanWithCandidate(1, 2),
                    V2ContractTestData.Complete<int?>("item.height", 1)))));

        Assert.Throws<ArgumentException>(() => RegionAt(origin, candidateItemGrid));
        Assert.Throws<ArgumentException>(() => RegionAt(origin, candidateSpanGrid));
    }

    [Fact]
    public void HostileRegionJsonCannotHideAnAbsolutelyEscapingItemOrSpanCandidate()
    {
        var region = RegionAt(
            new GridCellAddress(0, 1),
            UnreadGrid(V2ContractTestData.Cell(
                0,
                GridGeometry.MaxColumns - 2,
                V2ContractTestData.Item(width: 1))));
        var json = JsonSerializer.Serialize(region, JsonOptions);

        Assert.ThrowsAny<ArgumentException>(() => MutateRegion(json, node =>
            node["grid"]!["cells"]![0]!["item"]!["candidates"] = JsonSerializer.SerializeToNode(
                ItemWithCandidate(V2ContractTestData.Item(width: 1), V2ContractTestData.Item(width: 2)).Candidates,
                JsonOptions)));
        Assert.ThrowsAny<ArgumentException>(() => MutateRegion(json, node =>
            node["grid"]!["cells"]![0]!["item"]!["value"]!["widthCells"]!["candidates"] =
                JsonSerializer.SerializeToNode(SpanWithCandidate(1, 2).Candidates, JsonOptions)));
    }

    [Fact]
    public void HostileRegionJsonCannotMoveAnUnreadGridCellPastTheContainerBoundary()
    {
        var region = RegionAt(
            new GridCellAddress(0, 0),
            UnreadGrid(V2ContractTestData.Cell(0, 0)));
        var node = JsonSerializer.SerializeToNode(region, JsonOptions)!;
        node["originInContainer"]!["value"]!["row"] = 1;
        node["grid"]!["cells"]![0]!["anchor"]!["row"] = GridGeometry.MaxRows - 1;

        Assert.ThrowsAny<ArgumentException>(() =>
            JsonSerializer.Deserialize<StashCaptureRegion>(node.ToJsonString(), JsonOptions));
    }

    [Fact]
    public void OriginAndCellClaimsDoNotFormAHostileCartesianAmplifier()
    {
        var sharedItem = V2ContractTestData.Complete("grid.item", V2ContractTestData.Item());
        var cells = Enumerable.Range(0, GridGeometry.MaxCells)
            .Select(index => new GridCellRecognition(
                new GridCellAddress(index / GridGeometry.MaxColumns, index % GridGeometry.MaxColumns),
                sharedItem))
            .ToArray();
        var grid = UnreadGrid(cells);
        var sharedOrigin = new EvidenceCandidate<GridCellAddress?>(
            "origin", "Origin", new GridCellAddress(0, 0), V2ContractTestData.ScreenshotProvenance());
        var origins = new EvidencedValue<GridCellAddress?>(
            "region.origin",
            null,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Current),
            V2ContractTestData.ScreenshotProvenance(),
            candidates: Enumerable.Repeat(sharedOrigin, GridGeometry.MaxCells).ToArray());

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var region = new StashCaptureRegion("region-0", "artifact-0", 0, "stash", origins, grid);
        timer.Stop();

        Assert.Equal(GridGeometry.MaxCells, region.Grid.Cells.Count);
        Assert.Equal(GridGeometry.MaxCells, region.OriginInContainer.Candidates.Count);
        Assert.True(
            timer.Elapsed < TimeSpan.FromSeconds(10),
            $"Linear placement validation took {timer.Elapsed}; origin and cell claims may be multiplying again.");
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
    public void StashRegionOrdinalsAscendAndKeepTheGapOfAFailedCapture()
    {
        var stash = Stash([Region("region-0", 0, 0), Region("region-2", 2, 3)], [Coverage("stash", 70, 680)]);
        var json = JsonSerializer.Serialize(stash, JsonOptions);

        Assert.Equal([0, 2], JsonSerializer.Deserialize<StashRecognition>(json, JsonOptions)!.CapturedRegions.Select(region => region.CaptureOrdinal));
        Assert.Throws<ArgumentException>(() => Stash([Region("region-2", 2, 3), Region("region-0", 0, 0)], [Coverage("stash", 70, 680)]));
        Assert.ThrowsAny<ArgumentException>(() => MutateStash(json, regions => regions[1]!["captureOrdinal"] = 0));
        Assert.ThrowsAny<ArgumentException>(() => MutateStash(json, regions =>
        {
            regions[0]!["captureOrdinal"] = 2;
            regions[1]!["captureOrdinal"] = 0;
        }));
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

    private static StashCaptureRegion RegionAt(GridCellAddress origin, GridRecognition grid) => new(
        "region-0",
        "artifact-0",
        0,
        "stash",
        V2ContractTestData.Complete<GridCellAddress?>("region.origin", origin),
        grid);

    private static GridRecognition UnreadGrid(params GridCellRecognition[] cells) => new(
        Geometry(
            V2ContractTestData.Unknown<int?>("grid.rows"),
            V2ContractTestData.Unknown<int?>("grid.columns")),
        cells);

    private static StashContainerCoverage Coverage(string container, int observed, int total) => new(
        container,
        V2ContractTestData.Complete<int?>("coverage.observed", observed),
        V2ContractTestData.Complete<int?>("coverage.total", total));

    private static GridGeometry Geometry(EvidencedValue<int?> rows, EvidencedValue<int?>? columns = null) => new(
        rows,
        columns ?? V2ContractTestData.Complete<int?>("grid.columns", 10),
        V2ContractTestData.Complete<int?>("grid.cellWidth", 63),
        V2ContractTestData.Complete<int?>("grid.cellHeight", 63));

    private static RecognizedItem WithSpans(EvidencedValue<int?> width, EvidencedValue<int?> height)
    {
        var item = V2ContractTestData.Item();
        return new RecognizedItem(item.CanonicalId, item.DisplayName, item.Quantity, width, height, item.Rotated, item.FoundInRaid, item.Condition);
    }

    private static EvidencedValue<RecognizedItem> ItemWithCandidate(RecognizedItem current, RecognizedItem candidate) => new(
        "grid.item",
        current,
        V2ContractTestData.CompleteStatus,
        V2ContractTestData.ScreenshotProvenance(),
        candidates:
        [
            new EvidenceCandidate<RecognizedItem>(
                "wide", "Wide candidate", candidate, V2ContractTestData.ScreenshotProvenance()),
        ]);

    private static EvidencedValue<int?> SpanWithCandidate(int current, int candidate) => new(
        "item.width",
        current,
        V2ContractTestData.CompleteStatus,
        V2ContractTestData.ScreenshotProvenance(),
        candidates:
        [
            new EvidenceCandidate<int?>(
                "wide", "Wide candidate", candidate, V2ContractTestData.ScreenshotProvenance()),
        ]);

    private static EvidencedValue<int?> CorrectedSpan(int original, int corrected) =>
        V2ContractTestData.Complete<int?>(
            "item.width",
            corrected,
            corrections:
            [
                new EvidenceCorrection<int?>(
                    1, original, corrected, V2ContractTestData.ObservedUtc,
                    CorrectionOriginClass.User, "local-user"),
            ]);

    private static GridRecognition? MutateGrid(string json, Action<JsonNode> mutate)
    {
        var node = JsonNode.Parse(json)!;
        mutate(node);
        return JsonSerializer.Deserialize<GridRecognition>(node.ToJsonString(), JsonOptions);
    }

    private static StashCaptureRegion? MutateRegion(string json, Action<JsonNode> mutate)
    {
        var node = JsonNode.Parse(json)!;
        mutate(node);
        return JsonSerializer.Deserialize<StashCaptureRegion>(node.ToJsonString(), JsonOptions);
    }

    private static StashRecognition? MutateStash(string json, Action<JsonArray> mutate)
    {
        var node = JsonNode.Parse(json)!;
        mutate(node["capturedRegions"]!.AsArray());
        return JsonSerializer.Deserialize<StashRecognition>(node.ToJsonString(), JsonOptions);
    }
}
