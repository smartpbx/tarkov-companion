using System.Collections;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition.Grid;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.RecognitionTests.Grid;

public sealed class InventoryGridReconstructorTests
{
    private static readonly DateTimeOffset ObservedUtc =
        new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    private static readonly ResultStatus CompleteStatus =
        new(ResultCompleteness.Complete, FreshnessState.Current);

    private static readonly ResultStatus PartialStatus =
        new(ResultCompleteness.Partial, FreshnessState.Current);

    [Theory]
    [InlineData(1920, 1080, 1100, 360, 63, InventoryGridSurface.VisibleLoot)]
    [InlineData(2560, 1440, 1500, 500, 84, InventoryGridSurface.CarriedInventory)]
    [InlineData(3840, 2160, 2400, 800, 126, InventoryGridSurface.Stash)]
    [InlineData(3840, 1080, 2800, 400, 63, InventoryGridSurface.Container)]
    [InlineData(1920, 1080, 1000, 420, 78, InventoryGridSurface.VisibleLoot)]
    public void ReconstructsMeasuredOriginsAndPitchesAcrossFrameShapes(
        int frameWidth,
        int frameHeight,
        int originX,
        int originY,
        int pitch,
        InventoryGridSurface surface)
    {
        var lattice = Lattice(originX, originY, rows: 4, columns: 6, pitch: pitch);
        var anchor = new GridCellAddress(2, 3);
        var observation = Observation(lattice, "cell-scaled", anchor, Item());

        var result = new InventoryGridReconstructor().Reconstruct(
            new GridReconstructionRequest(surface, lattice, [observation]));

        Assert.True(lattice.Bounds.X + lattice.Bounds.Width <= frameWidth);
        Assert.True(lattice.Bounds.Y + lattice.Bounds.Height <= frameHeight);
        Assert.Equal(GridReconstructionOutcome.Complete, result.Outcome);
        Assert.Equal(surface, result.Surface);
        var recognition = Assert.IsType<GridRecognition>(result.Recognition);
        Assert.Equal(pitch, recognition.Geometry.CellWidthPixels.Value);
        Assert.Equal(pitch, recognition.Geometry.CellHeightPixels.Value);
        Assert.Equal(lattice.CellBounds(anchor), recognition.Cells.Single().Item.Bounds);
        Assert.Same(observation.Item, recognition.Cells.Single().Item);
        var cellBounds = lattice.CellBounds(anchor);
        Assert.True(lattice.TryLocateCell(cellBounds.X + 1, cellBounds.Y + 1, out var located));
        Assert.Equal(anchor, located);
        Assert.False(lattice.TryLocateCell(
            lattice.Bounds.X + lattice.Bounds.Width,
            cellBounds.Y,
            out _));
    }

    [Fact]
    public void NearNeighbourCandidatesStayAmbiguousWithTheirEvidenceAndRegion()
    {
        var lattice = Lattice(300, 200, rows: 3, columns: 4, pitch: 70);
        var anchor = new GridCellAddress(1, 1);
        var bounds = lattice.CellBounds(anchor);
        var first = Item("round-a");
        var second = Item("round-b");
        EvidenceCandidate<RecognizedItem>[] candidates =
        [
            new("icon-neighbour-a", "Round A", first, Provenance("fixture://icon/a"), bounds),
            new("ocr-neighbour-b", "Round B", second, Provenance("fixture://ocr/b"), bounds),
        ];
        var itemEvidence = new EvidencedValue<RecognizedItem>(
            "grid.1.1.item",
            null,
            PartialStatus,
            Provenance(),
            bounds,
            candidates);
        var observation = new GridCellObservation("ambiguous-round", anchor, itemEvidence);

        var result = new InventoryGridReconstructor().Reconstruct(
            new GridReconstructionRequest(InventoryGridSurface.VisibleLoot, lattice, [observation]));

        Assert.Equal(GridReconstructionOutcome.Partial, result.Outcome);
        var cell = Assert.Single(result.Recognition!.Cells);
        Assert.Null(cell.Item.Value);
        Assert.Same(itemEvidence, cell.Item);
        Assert.Collection(
            cell.Item.Candidates,
            candidate => Assert.Same(candidates[0], candidate),
            candidate => Assert.Same(candidates[1], candidate));
        Assert.Equal(bounds, cell.Item.Bounds);
        Assert.Same(observation, Assert.Single(result.UnresolvedCells));
        Assert.Contains(result.Issues, issue => issue.Kind == GridReconstructionIssueKind.ItemAmbiguous);
    }

    [Fact]
    public void OccupiedCellWithoutCacheCandidatesCannotEnterExactItemTotals()
    {
        var lattice = Lattice(200, 160, rows: 2, columns: 3, pitch: 68);
        var anchor = new GridCellAddress(0, 2);
        var itemEvidence = new EvidencedValue<RecognizedItem>(
            "grid.0.2.item",
            null,
            PartialStatus,
            Provenance("fixture://missing-icon-cache"),
            lattice.CellBounds(anchor));
        var observation = new GridCellObservation("missing-cache", anchor, itemEvidence);

        var result = new InventoryGridReconstructor().Reconstruct(
            new GridReconstructionRequest(InventoryGridSurface.VisibleLoot, lattice, [observation]));

        Assert.Equal(GridReconstructionOutcome.Partial, result.Outcome);
        var cell = Assert.Single(result.Recognition!.Cells);
        Assert.Null(cell.Item.Value);
        Assert.Empty(cell.Item.Candidates);
        Assert.Same(observation, Assert.Single(result.UnresolvedCells));
        Assert.Contains(result.Issues, issue => issue.Kind == GridReconstructionIssueKind.ItemUnresolved);
    }

    [Fact]
    public void RotationAndQuantityUncertaintySurviveAlongsideCorrectionHistory()
    {
        var lattice = Lattice(240, 180, rows: 4, columns: 5, pitch: 72);
        var anchor = new GridCellAddress(1, 1);
        var bounds = lattice.CellBounds(anchor);
        var correctedId = Complete(
            "item.id",
            "wires",
            corrections:
            [
                new EvidenceCorrection<string>(
                    1,
                    "wire?",
                    "wires",
                    ObservedUtc.AddSeconds(1),
                    CorrectionOriginClass.User,
                    "fixture-reviewer"),
            ]);
        var quantity = Partial<int?>(
            "item.quantity",
            null,
            candidates:
            [
                Candidate<int?>("quantity-one", "One", 1),
                Candidate<int?>("quantity-two", "Two", 2),
            ]);
        var rotated = Partial<bool?>(
            "item.rotated",
            null,
            candidates:
            [
                Candidate<bool?>("rotation-normal", "Not rotated", false),
                Candidate<bool?>("rotation-turned", "Rotated", true),
            ]);
        var item = Item(
            "wires",
            width: 2,
            height: 1,
            canonicalId: correctedId,
            quantity: quantity,
            rotated: rotated);
        var itemEvidence = Complete("grid.1.1.item", item, bounds: bounds);
        var observation = new GridCellObservation("uncertain-badges", anchor, itemEvidence);

        var result = new InventoryGridReconstructor().Reconstruct(
            new GridReconstructionRequest(InventoryGridSurface.CarriedInventory, lattice, [observation]));

        Assert.Equal(GridReconstructionOutcome.Partial, result.Outcome);
        var recognition = Assert.IsType<GridRecognition>(result.Recognition);
        var recognized = Assert.Single(recognition.Cells).Item.Value!;
        Assert.Equal("wire?", recognized.CanonicalId.RecognizedValue);
        Assert.Equal("wires", recognized.CanonicalId.Value);
        Assert.Equal(2, recognized.Quantity.Candidates.Count);
        Assert.Equal(2, recognized.Rotated.Candidates.Count);
        Assert.Equal(bounds, Assert.Single(recognition.Cells).Item.Bounds);
        Assert.Contains(result.Issues, issue => issue.Kind == GridReconstructionIssueKind.QuantityUncertain);
        Assert.Contains(result.Issues, issue => issue.Kind == GridReconstructionIssueKind.RotationUncertain);
    }

    [Fact]
    public void OverlappingFootprintsAreExcludedWithoutDiscardingEitherObservation()
    {
        var lattice = Lattice(100, 100, rows: 4, columns: 4, pitch: 64);
        var first = Observation(lattice, "large-a", new GridCellAddress(0, 0), Item("large-a", 2, 2));
        var second = Observation(lattice, "large-b", new GridCellAddress(1, 1), Item("large-b", 2, 2));
        var safe = Observation(lattice, "safe", new GridCellAddress(3, 3), Item("safe"));

        var result = new InventoryGridReconstructor().Reconstruct(
            new GridReconstructionRequest(
                InventoryGridSurface.VisibleLoot,
                lattice,
                [second, safe, first]));

        Assert.Equal(GridReconstructionOutcome.Partial, result.Outcome);
        Assert.Equal("safe", Assert.Single(result.Recognition!.Cells).Item.Value!.CanonicalId.Value);
        Assert.Equal(2, result.UnresolvedCells.Count);
        Assert.Contains(result.UnresolvedCells, cell => ReferenceEquals(cell, first));
        Assert.Contains(result.UnresolvedCells, cell => ReferenceEquals(cell, second));
        Assert.Contains(result.Issues, issue =>
            issue is
            {
                Kind: GridReconstructionIssueKind.FootprintOverlap,
                ObservationId: "large-a",
                RelatedObservationId: "large-b",
            });
        Assert.Contains(result.Issues, issue =>
            issue is
            {
                Kind: GridReconstructionIssueKind.FootprintOverlap,
                ObservationId: "large-b",
                RelatedObservationId: "large-a",
            });
    }

    [Fact]
    public void EscapingCorrectionBecomesPartialInsteadOfThrowingDuringAssembly()
    {
        var lattice = Lattice(0, 0, rows: 2, columns: 2, pitch: 50);
        var anchor = new GridCellAddress(0, 1);
        var correctedWidth = Complete<int?>(
            "item.width",
            1,
            corrections:
            [
                new EvidenceCorrection<int?>(
                    1,
                    2,
                    1,
                    ObservedUtc.AddSeconds(1),
                    CorrectionOriginClass.User,
                    "fixture-reviewer"),
            ]);
        var item = Item("corrected-width", width: 1, widthField: correctedWidth);
        var observation = Observation(lattice, "edge", anchor, item);

        var result = new InventoryGridReconstructor().Reconstruct(
            new GridReconstructionRequest(InventoryGridSurface.Stash, lattice, [observation]));

        Assert.Equal(GridReconstructionOutcome.Partial, result.Outcome);
        Assert.Empty(result.Recognition!.Cells);
        Assert.Same(observation, Assert.Single(result.UnresolvedCells));
        Assert.Contains(result.Issues, issue => issue.Kind == GridReconstructionIssueKind.FootprintOutsideGrid);
    }

    [Theory]
    [InlineData(InventoryGridSurface.Unknown, GridReconstructionIssueKind.SurfaceUnknown)]
    [InlineData(InventoryGridSurface.NonGrid, GridReconstructionIssueKind.SurfaceIsNotGrid)]
    [InlineData(InventoryGridSurface.VisibleLoot, GridReconstructionIssueKind.GeometryUnavailable)]
    public void UnclassifiedNonGridAndMissingGeometryAreExplicitNoChangeResults(
        InventoryGridSurface surface,
        GridReconstructionIssueKind expectedIssue)
    {
        var lattice = Lattice(0, 0, rows: 2, columns: 2, pitch: 50);
        var observation = Observation(lattice, "unapplied", new GridCellAddress(0, 0), Item());

        var result = new InventoryGridReconstructor().Reconstruct(
            new GridReconstructionRequest(surface, null, [observation]));

        Assert.Equal(GridReconstructionOutcome.NoChange, result.Outcome);
        Assert.Null(result.Recognition);
        Assert.Same(observation, Assert.Single(result.UnresolvedCells));
        Assert.Equal(expectedIssue, Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void PartialGeometryNeverMasqueradesAsACompleteGrid()
    {
        var lattice = Lattice(40, 40, rows: 2, columns: 2, pitch: 48, status: PartialStatus);

        var result = new InventoryGridReconstructor().Reconstruct(
            new GridReconstructionRequest(InventoryGridSurface.VisibleLoot, lattice, []));

        Assert.Equal(GridReconstructionOutcome.Partial, result.Outcome);
        Assert.Empty(result.Recognition!.Cells);
        Assert.Contains(result.Issues, issue => issue.Kind == GridReconstructionIssueKind.GeometryPartial);
    }

    [Fact]
    public void HostileGeometryAndCandidateAmplificationAreRejectedAtTheBoundary()
    {
        var provenance = Provenance();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DetectedGridLattice(
            GridGeometry.MaxRows + 1,
            1,
            1,
            1,
            CompleteStatus,
            provenance,
            new EvidenceRegion(0, 0, 1, GridGeometry.MaxRows + 1, EvidenceCoordinateSpace.SourcePixels)));
        Assert.Throws<ArgumentException>(() => new DetectedGridLattice(
            2,
            2,
            50,
            50,
            CompleteStatus,
            provenance,
            new EvidenceRegion(0, 0, 99, 100, EvidenceCoordinateSpace.SourcePixels)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DetectedGridLattice(
            1,
            1,
            12,
            1,
            CompleteStatus,
            provenance,
            new EvidenceRegion(int.MaxValue - 10, 0, 12, 1, EvidenceCoordinateSpace.SourcePixels)));

        var lattice = Lattice(0, 0, rows: 2, columns: 2, pitch: 50);
        var bounds = lattice.CellBounds(new GridCellAddress(0, 0));
        var candidates = Enumerable.Range(0, GridCellObservation.MaxCandidatesPerClaim + 1)
            .Select(index => new EvidenceCandidate<RecognizedItem>(
                $"candidate-{index}",
                $"Candidate {index}",
                Item($"item-{index}"),
                provenance,
                bounds))
            .ToArray();
        var amplified = new EvidencedValue<RecognizedItem>(
            "grid.0.0.item",
            null,
            PartialStatus,
            provenance,
            bounds,
            candidates);
        var missingBounds = Complete("grid.0.0.item", Item());

        Assert.Throws<ArgumentException>(() =>
            new GridCellObservation("amplified", new GridCellAddress(0, 0), amplified));
        Assert.Throws<ArgumentException>(() =>
            new GridCellObservation("missing-bounds", new GridCellAddress(0, 0), missingBounds));
        Assert.Throws<ArgumentException>(() => new GridReconstructionRequest(
            InventoryGridSurface.VisibleLoot,
            lattice,
            new OversizedObservationList()));
    }

    [Fact]
    public void DamagedSourceRegionIsPartialAndCancellationIsPropagated()
    {
        var lattice = Lattice(100, 100, rows: 2, columns: 2, pitch: 50);
        var itemEvidence = Complete(
            "grid.0.0.item",
            Item(),
            bounds: new EvidenceRegion(90, 100, 20, 20, EvidenceCoordinateSpace.SourcePixels));
        var observation = new GridCellObservation("damaged", new GridCellAddress(0, 0), itemEvidence);
        var reconstructor = new InventoryGridReconstructor();

        var result = reconstructor.Reconstruct(
            new GridReconstructionRequest(InventoryGridSurface.VisibleLoot, lattice, [observation]));

        Assert.Equal(GridReconstructionOutcome.Partial, result.Outcome);
        Assert.Empty(result.Recognition!.Cells);
        Assert.Contains(result.Issues, issue => issue.Kind == GridReconstructionIssueKind.SourceRegionOutsideGrid);

        var mismatchedEvidence = Complete(
            "grid.0.0.item",
            Item("mismatched"),
            bounds: lattice.CellBounds(new GridCellAddress(0, 1)));
        var mismatched = new GridCellObservation("mismatched", new GridCellAddress(0, 0), mismatchedEvidence);
        var mismatchedResult = reconstructor.Reconstruct(
            new GridReconstructionRequest(InventoryGridSurface.VisibleLoot, lattice, [mismatched]));
        Assert.Empty(mismatchedResult.Recognition!.Cells);
        Assert.Contains(mismatchedResult.Issues, issue =>
            issue.Kind == GridReconstructionIssueKind.SourceRegionDoesNotMatchAnchor);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => reconstructor.Reconstruct(
            new GridReconstructionRequest(InventoryGridSurface.VisibleLoot, lattice, [observation]),
            cancelled.Token));
    }

    private static DetectedGridLattice Lattice(
        int x,
        int y,
        int rows,
        int columns,
        int pitch,
        ResultStatus? status = null) => new(
            rows,
            columns,
            pitch,
            pitch,
            status ?? CompleteStatus,
            Provenance("fixture://grid-geometry"),
            new EvidenceRegion(
                x,
                y,
                checked(columns * pitch),
                checked(rows * pitch),
                EvidenceCoordinateSpace.SourcePixels));

    private static GridCellObservation Observation(
        DetectedGridLattice lattice,
        string observationId,
        GridCellAddress anchor,
        RecognizedItem item) => new(
            observationId,
            anchor,
            Complete($"grid.{anchor.Row}.{anchor.Column}.item", item, bounds: lattice.CellBounds(anchor)));

    private static RecognizedItem Item(
        string id = "item-a",
        int width = 1,
        int height = 1,
        EvidencedValue<string>? canonicalId = null,
        EvidencedValue<int?>? quantity = null,
        EvidencedValue<int?>? widthField = null,
        EvidencedValue<bool?>? rotated = null) => new(
            canonicalId ?? Complete("item.id", id),
            Complete("item.name", id),
            quantity ?? Complete<int?>("item.quantity", 1),
            widthField ?? Complete<int?>("item.width", width),
            Complete<int?>("item.height", height),
            rotated ?? Complete<bool?>("item.rotated", false),
            Complete<bool?>("item.foundInRaid", true),
            Complete("item.condition", ItemConditionReading.NotApplicable));

    private static EvidenceCandidate<T> Candidate<T>(string id, string displayName, T value) =>
        new(id, displayName, value, Provenance());

    private static EvidencedValue<T> Complete<T>(
        string fieldId,
        T value,
        EvidenceRegion? bounds = null,
        IReadOnlyList<EvidenceCandidate<T>>? candidates = null,
        IReadOnlyList<EvidenceCorrection<T>>? corrections = null) => new(
            fieldId,
            value,
            CompleteStatus,
            Provenance(),
            bounds,
            candidates,
            corrections);

    private static EvidencedValue<T> Partial<T>(
        string fieldId,
        T? value,
        EvidenceRegion? bounds = null,
        IReadOnlyList<EvidenceCandidate<T>>? candidates = null) => new(
            fieldId,
            value,
            PartialStatus,
            Provenance(),
            bounds,
            candidates);

    private static EvidenceProvenance Provenance(string source = "fixture://inventory-grid") => new(
        EvidenceSourceClass.GameWrittenScreenshot,
        source,
        ObservedUtc,
        new EvidenceConfidence(EvidenceConfidenceKind.ProviderScore, 0.82),
        new ProducerIdentity("fixture-grid-recognizer", "1.0"));

    /// <summary>Count alone must stop allocation; touching the indexer would amplify hostile input.</summary>
    private sealed class OversizedObservationList : IReadOnlyList<GridCellObservation>
    {
        public int Count => GridReconstructionRequest.MaxObservations + 1;

        public GridCellObservation this[int index] => throw new InvalidOperationException("Indexer must not be read.");

        public IEnumerator<GridCellObservation> GetEnumerator() =>
            throw new InvalidOperationException("Enumerator must not be read.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
