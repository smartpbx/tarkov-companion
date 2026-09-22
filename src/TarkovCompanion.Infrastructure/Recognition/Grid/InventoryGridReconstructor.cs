using System.Numerics;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>Turns bounded lattice and occupied-cell evidence into a safe V2 grid payload.</summary>
/// <remarks>
/// Candidate scoring belongs upstream and benchmark thresholds belong to #272. This type never
/// converts icon distance into probability or picks a near neighbour: an absent item value remains
/// ambiguous, with its original candidates and pixel region attached. It only proves geometry that
/// can coexist in one bounded grid and keeps everything else explicit as partial evidence.
/// </remarks>
public sealed class InventoryGridReconstructor
{
    public GridReconstructionResult Reconstruct(
        GridReconstructionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Surface == InventoryGridSurface.Unknown)
        {
            return NoChange(request, GridReconstructionIssueKind.SurfaceUnknown);
        }

        if (request.Surface == InventoryGridSurface.NonGrid)
        {
            return NoChange(request, GridReconstructionIssueKind.SurfaceIsNotGrid);
        }

        if (request.Lattice is not { } lattice)
        {
            return NoChange(request, GridReconstructionIssueKind.GeometryUnavailable);
        }

        var issues = new List<GridReconstructionIssue>();
        if (lattice.Status.Completeness == ResultCompleteness.Partial)
        {
            issues.Add(new(GridReconstructionIssueKind.GeometryPartial));
        }

        var cells = request.OccupiedCells
            .OrderBy(cell => cell.Anchor.Row)
            .ThenBy(cell => cell.Anchor.Column)
            .ThenBy(cell => cell.ObservationId, StringComparer.Ordinal)
            .Select(cell => new WorkingCell(cell))
            .ToArray();

        MarkDuplicateIds(cells, issues, cancellationToken);
        foreach (var cell in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePlacement(cell, lattice, issues);
            MarkEvidenceUncertainty(cell, issues);
        }

        ExcludeOverlaps(cells, lattice, issues, cancellationToken);

        var recognizedCells = cells
            .Where(cell => !cell.Excluded)
            .Select(cell => new GridCellRecognition(cell.Observation.Anchor, cell.Observation.Item))
            .ToArray();
        var recognition = new GridRecognition(BuildGeometry(lattice), recognizedCells);
        var unresolved = cells
            .Where(cell => cell.Unresolved)
            .Select(cell => cell.Observation)
            .ToArray();
        var outcome = issues.Count == 0
            ? GridReconstructionOutcome.Complete
            : GridReconstructionOutcome.Partial;

        return new GridReconstructionResult(
            outcome,
            request.Surface,
            recognition,
            unresolved,
            issues,
            request.VerticalScrollPosition);
    }

    private static GridReconstructionResult NoChange(
        GridReconstructionRequest request,
        GridReconstructionIssueKind issue) => new(
            GridReconstructionOutcome.NoChange,
            request.Surface,
            null,
            request.OccupiedCells,
            [new GridReconstructionIssue(issue)],
            request.VerticalScrollPosition);

    private static void MarkDuplicateIds(
        IReadOnlyList<WorkingCell> cells,
        List<GridReconstructionIssue> issues,
        CancellationToken cancellationToken)
    {
        foreach (var duplicate in cells
                     .GroupBy(cell => cell.Observation.ObservationId, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var duplicateCells = duplicate.ToArray();
            for (var index = 0; index < duplicateCells.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cell = duplicateCells[index];
                AddIssue(
                    cell,
                    issues,
                    GridReconstructionIssueKind.DuplicateObservationId,
                    duplicateCells[(index + 1) % duplicateCells.Length].Observation.ObservationId,
                    exclude: true);
            }
        }
    }

    private static void ValidatePlacement(
        WorkingCell cell,
        DetectedGridLattice lattice,
        List<GridReconstructionIssue> issues)
    {
        if (cell.Excluded)
        {
            return;
        }

        var observation = cell.Observation;
        if (observation.Anchor.Row >= lattice.Rows || observation.Anchor.Column >= lattice.Columns)
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.CellOutsideGrid, exclude: true);
            return;
        }

        if (!Contains(lattice.Bounds, observation.Item.Bounds!))
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.SourceRegionOutsideGrid, exclude: true);
            return;
        }

        if (!Intersects(lattice.CellBounds(observation.Anchor), observation.Item.Bounds!))
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.SourceRegionDoesNotMatchAnchor, exclude: true);
            return;
        }

        var remainingRows = lattice.Rows - observation.Anchor.Row;
        var remainingColumns = lattice.Columns - observation.Anchor.Column;
        if (FootprintClaims(observation.Item).Any(claim =>
                (claim.Width is { } width && width > remainingColumns) ||
                (claim.Height is { } height && height > remainingRows)))
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.FootprintOutsideGrid, exclude: true);
        }
    }

    private static void MarkEvidenceUncertainty(
        WorkingCell cell,
        List<GridReconstructionIssue> issues)
    {
        var field = cell.Observation.Item;
        if (field.Value is not { } item)
        {
            AddIssue(
                cell,
                issues,
                field.Candidates.Count == 0
                    ? GridReconstructionIssueKind.ItemUnresolved
                    : GridReconstructionIssueKind.ItemAmbiguous);
            return;
        }

        if (field.Status.Completeness != ResultCompleteness.Complete)
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.ItemPartial);
        }

        if (!IsComplete(item.CanonicalId) || !IsComplete(item.DisplayName))
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.ItemUnresolved);
        }

        if (!IsComplete(item.WidthCells) || !IsComplete(item.HeightCells))
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.FootprintUncertain);
        }

        if (!IsComplete(item.Rotated))
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.RotationUncertain);
        }

        if (!IsComplete(item.Quantity))
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.QuantityUncertain);
        }

        if (!IsComplete(item.FoundInRaid) || !IsComplete(item.Condition))
        {
            AddIssue(cell, issues, GridReconstructionIssueKind.AttributesUncertain);
        }
    }

    /// <summary>
    /// Claims use one 64-bit row mask because the shared grid contract has at most 64 columns.
    /// This keeps hostile maximum-size input to at most 256 mask checks per observation rather
    /// than multiplying every footprint by every other footprint. A conflicted owner's mask
    /// deliberately stays claimed: letting a later observation reuse it would make the safe
    /// subset depend on which of several mutually inconsistent observations happened to arrive
    /// last. The output excludes every conflicted owner while the mask remains conservative.
    /// </summary>
    private static void ExcludeOverlaps(
        IReadOnlyList<WorkingCell> cells,
        DetectedGridLattice lattice,
        List<GridReconstructionIssue> issues,
        CancellationToken cancellationToken)
    {
        var occupiedRows = new ulong[lattice.Rows];
        var owners = new int[lattice.Rows, lattice.Columns];

        for (var index = 0; index < cells.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cell = cells[index];
            if (cell.Excluded)
            {
                continue;
            }

            var (width, height) = CurrentFootprint(cell.Observation.Item);
            var mask = ColumnMask(cell.Observation.Anchor.Column, width);
            if (TryFindOwner(cell.Observation.Anchor.Row, height, mask, occupiedRows, owners, out var ownerIndex))
            {
                var owner = cells[ownerIndex];
                AddIssue(
                    cell,
                    issues,
                    GridReconstructionIssueKind.FootprintOverlap,
                    owner.Observation.ObservationId,
                    exclude: true);
                AddIssue(
                    owner,
                    issues,
                    GridReconstructionIssueKind.FootprintOverlap,
                    cell.Observation.ObservationId,
                    exclude: true);
                continue;
            }

            for (var row = cell.Observation.Anchor.Row; row < cell.Observation.Anchor.Row + height; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                occupiedRows[row] |= mask;
                for (var column = cell.Observation.Anchor.Column;
                     column < cell.Observation.Anchor.Column + width;
                     column++)
                {
                    owners[row, column] = index + 1;
                }
            }
        }
    }

    private static bool TryFindOwner(
        int firstRow,
        int height,
        ulong mask,
        IReadOnlyList<ulong> occupiedRows,
        int[,] owners,
        out int ownerIndex)
    {
        for (var row = firstRow; row < firstRow + height; row++)
        {
            var overlap = occupiedRows[row] & mask;
            if (overlap == 0)
            {
                continue;
            }

            var column = BitOperations.TrailingZeroCount(overlap);
            ownerIndex = owners[row, column] - 1;
            return true;
        }

        ownerIndex = -1;
        return false;
    }

    private static ulong ColumnMask(int firstColumn, int width)
    {
        var unshifted = width == GridGeometry.MaxColumns
            ? ulong.MaxValue
            : (1UL << width) - 1;
        return unshifted << firstColumn;
    }

    private static (int Width, int Height) CurrentFootprint(EvidencedValue<RecognizedItem> field) =>
        field.Value is { WidthCells.Value: { } width, HeightCells.Value: { } height }
            ? (width, height)
            : (1, 1);

    private static GridGeometry BuildGeometry(DetectedGridLattice lattice) => new(
        GeometryField("grid.rows", lattice.Rows, lattice),
        GeometryField("grid.columns", lattice.Columns, lattice),
        GeometryField("grid.cellWidthPixels", lattice.CellWidthPixels, lattice),
        GeometryField("grid.cellHeightPixels", lattice.CellHeightPixels, lattice));

    private static EvidencedValue<int?> GeometryField(
        string fieldId,
        int value,
        DetectedGridLattice lattice) => new(
            fieldId,
            value,
            lattice.Status,
            lattice.Provenance,
            lattice.Bounds);

    private static bool Contains(EvidenceRegion outer, EvidenceRegion inner) =>
        outer.CoordinateSpace == inner.CoordinateSpace &&
        inner.X >= outer.X &&
        inner.Y >= outer.Y &&
        (long)inner.X + inner.Width <= (long)outer.X + outer.Width &&
        (long)inner.Y + inner.Height <= (long)outer.Y + outer.Height;

    private static bool Intersects(EvidenceRegion first, EvidenceRegion second) =>
        first.CoordinateSpace == second.CoordinateSpace &&
        first.X < (long)second.X + second.Width &&
        (long)first.X + first.Width > second.X &&
        first.Y < (long)second.Y + second.Height &&
        (long)first.Y + first.Height > second.Y;

    private static IEnumerable<(int? Width, int? Height)> FootprintClaims(
        EvidencedValue<RecognizedItem> field)
    {
        if (field.Value is { } current)
        {
            foreach (var claim in FootprintClaims(current))
            {
                yield return claim;
            }
        }

        foreach (var candidate in field.Candidates)
        {
            foreach (var claim in FootprintClaims(candidate.Value))
            {
                yield return claim;
            }
        }
    }

    private static IEnumerable<(int? Width, int? Height)> FootprintClaims(RecognizedItem item)
    {
        foreach (var width in Values(item.WidthCells))
        {
            if (width is not null)
            {
                yield return (width, null);
            }
        }

        foreach (var height in Values(item.HeightCells))
        {
            if (height is not null)
            {
                yield return (null, height);
            }
        }
    }

    private static IEnumerable<int?> Values(EvidencedValue<int?> field)
    {
        yield return field.Value;
        foreach (var candidate in field.Candidates)
        {
            yield return candidate.Value;
        }

        foreach (var correction in field.Corrections)
        {
            yield return correction.OriginalValue;
            yield return correction.CorrectedValue;
        }
    }

    private static bool IsComplete<T>(EvidencedValue<T> field) =>
        field.Status.Completeness == ResultCompleteness.Complete && field.Value is not null;

    private static void AddIssue(
        WorkingCell cell,
        List<GridReconstructionIssue> issues,
        GridReconstructionIssueKind kind,
        string? relatedObservationId = null,
        bool exclude = false)
    {
        cell.Unresolved = true;
        cell.Excluded |= exclude;
        if (!cell.IssueKeys.Add((kind, relatedObservationId)))
        {
            return;
        }

        issues.Add(new GridReconstructionIssue(
            kind,
            cell.Observation.ObservationId,
            cell.Observation.Anchor,
            relatedObservationId));
    }

    private sealed class WorkingCell(GridCellObservation observation)
    {
        public GridCellObservation Observation { get; } = observation;

        public bool Excluded { get; set; }

        public bool Unresolved { get; set; }

        public HashSet<(GridReconstructionIssueKind Kind, string? Related)> IssueKeys { get; } = [];
    }
}
