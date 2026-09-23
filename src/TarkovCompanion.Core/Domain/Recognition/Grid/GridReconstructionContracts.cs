using System.Collections.ObjectModel;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Domain.Recognition.Grid;

/// <summary>The inventory surface a measured lattice belongs to.</summary>
public enum InventoryGridSurface
{
    Unknown = 0,
    VisibleLoot,
    CarriedInventory,
    Stash,
    Container,
    NonGrid,
}

/// <summary>The carried section a separately framed Gear-screen grid belongs to.</summary>
public enum CarriedGridKind
{
    Backpack = 1,
    TacticalRig,
    Pockets,
}

/// <summary>
/// Stable identity for one separately framed carried grid. Several grids of the same kind are
/// common: a rig can publish one pouch per slot and a backpack can publish more than one grid.
/// </summary>
public readonly record struct CarriedGridIdentity
{
    public CarriedGridIdentity(CarriedGridKind kind, int index)
    {
        Kind = Enum.IsDefined(kind) ? kind : throw new ArgumentOutOfRangeException(nameof(kind));
        Index = index is >= 0 and < 64 ? index : throw new ArgumentOutOfRangeException(nameof(index));
    }

    public CarriedGridKind Kind { get; }

    public int Index { get; }

    public static CarriedGridIdentity PrimaryBackpack { get; } = new(CarriedGridKind.Backpack, 0);
}

/// <summary>One carried grid as read from pixels, before reconstruction.</summary>
public sealed record CarriedGridReconstructionRequest(
    CarriedGridIdentity Identity,
    GridReconstructionRequest Reconstruction)
{
    public GridReconstructionRequest Reconstruction { get; } = Reconstruction is { Surface: InventoryGridSurface.CarriedInventory }
        ? Reconstruction
        : throw new ArgumentException("A carried-grid request must describe carried inventory.", nameof(Reconstruction));
}

/// <summary>Whether a reconstruction can replace prior state.</summary>
public enum GridReconstructionOutcome
{
    NoChange = 1,
    Partial,
    Complete,
}

/// <summary>Why a reconstruction abstained from an exact result.</summary>
public enum GridReconstructionIssueKind
{
    SurfaceUnknown = 1,
    SurfaceIsNotGrid,
    GeometryUnavailable,
    GeometryPartial,
    DuplicateObservationId,
    CellOutsideGrid,
    SourceRegionOutsideGrid,
    SourceRegionDoesNotMatchAnchor,
    FootprintOutsideGrid,
    FootprintOverlap,
    ItemUnresolved,
    ItemAmbiguous,
    ItemPartial,
    FootprintUncertain,
    RotationUncertain,
    QuantityUncertain,
    AttributesUncertain,
}

/// <summary>A measured, axis-aligned inventory lattice and the pixels that support it.</summary>
/// <remarks>
/// The bounds are the exact cell rectangle, not an approximate panel crop. Requiring the measured
/// pitch to multiply back to those bounds prevents later cell arithmetic from accumulating rounding
/// error and walking outside a hostile capture region. Detection may move and scale the lattice per
/// frame; reconstruction never substitutes a global pitch.
/// </remarks>
public sealed record DetectedGridLattice
{
    public DetectedGridLattice(
        int rows,
        int columns,
        int cellWidthPixels,
        int cellHeightPixels,
        ResultStatus status,
        EvidenceProvenance provenance,
        EvidenceRegion bounds)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(bounds);

        if (rows is < 1 or > GridGeometry.MaxRows)
        {
            throw new ArgumentOutOfRangeException(nameof(rows));
        }

        if (columns is < 1 or > GridGeometry.MaxColumns)
        {
            throw new ArgumentOutOfRangeException(nameof(columns));
        }

        if (cellWidthPixels is < 1 or > GridGeometry.MaxCellPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(cellWidthPixels));
        }

        if (cellHeightPixels is < 1 or > GridGeometry.MaxCellPixels)
        {
            throw new ArgumentOutOfRangeException(nameof(cellHeightPixels));
        }

        if (status.Completeness is not (ResultCompleteness.Partial or ResultCompleteness.Complete))
        {
            throw new ArgumentException("A detected lattice must be partial or complete.", nameof(status));
        }

        if (bounds.CoordinateSpace == EvidenceCoordinateSpace.GridCellPixels)
        {
            throw new ArgumentException("A lattice must be located in source or capture-region pixels.", nameof(bounds));
        }

        var expectedWidth = (long)columns * cellWidthPixels;
        var expectedHeight = (long)rows * cellHeightPixels;
        if (bounds.Width != expectedWidth || bounds.Height != expectedHeight)
        {
            throw new ArgumentException("Lattice bounds must contain exactly the measured cells.", nameof(bounds));
        }

        if ((long)bounds.X + bounds.Width > int.MaxValue ||
            (long)bounds.Y + bounds.Height > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "Lattice coordinates must not overflow pixel space.");
        }

        Rows = rows;
        Columns = columns;
        CellWidthPixels = cellWidthPixels;
        CellHeightPixels = cellHeightPixels;
        Status = status;
        Provenance = provenance;
        Bounds = bounds;
    }

    public int Rows { get; }

    public int Columns { get; }

    public int CellWidthPixels { get; }

    public int CellHeightPixels { get; }

    public ResultStatus Status { get; }

    public EvidenceProvenance Provenance { get; }

    public EvidenceRegion Bounds { get; }

    public EvidenceRegion CellBounds(GridCellAddress address)
    {
        if (address.Row >= Rows || address.Column >= Columns)
        {
            throw new ArgumentOutOfRangeException(nameof(address), "The cell must be inside this lattice.");
        }

        return new EvidenceRegion(
            Bounds.X + (address.Column * CellWidthPixels),
            Bounds.Y + (address.Row * CellHeightPixels),
            CellWidthPixels,
            CellHeightPixels,
            Bounds.CoordinateSpace);
    }

    public bool TryLocateCell(int x, int y, out GridCellAddress address)
    {
        if (x < Bounds.X || y < Bounds.Y ||
            (long)x >= (long)Bounds.X + Bounds.Width ||
            (long)y >= (long)Bounds.Y + Bounds.Height)
        {
            address = default;
            return false;
        }

        address = new GridCellAddress(
            (y - Bounds.Y) / CellHeightPixels,
            (x - Bounds.X) / CellWidthPixels);
        return true;
    }
}

/// <summary>An occupied anchor and every item candidate supported by its source pixels.</summary>
/// <remarks>
/// The evidenced item is kept intact. A weak icon/OCR separation therefore remains an absent value
/// with candidates, and a correction retains the original nested values, candidates, provenance,
/// and source region instead of being flattened into a new recognition.
/// </remarks>
public sealed record GridCellObservation
{
    public const int MaxObservationIdLength = 128;

    public const int MaxCandidatesPerClaim = 32;

    public const int MaxCorrectionsPerClaim = 32;

    /// <summary>Alternatives and corrections across the whole occupied-cell observation.</summary>
    public const int MaxEvidenceEntries = 512;

    public GridCellObservation(
        string observationId,
        GridCellAddress anchor,
        EvidencedValue<RecognizedItem> item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);
        ArgumentNullException.ThrowIfNull(item);

        var normalizedId = observationId.Trim();
        if (normalizedId.Length > MaxObservationIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(observationId));
        }

        if (item.Bounds is null || item.Bounds.CoordinateSpace == EvidenceCoordinateSpace.GridCellPixels)
        {
            throw new ArgumentException(
                "An occupied-cell observation must retain a source or capture-region pixel region.",
                nameof(item));
        }

        var evidenceEntries = ValidateClaimBounds(item, nameof(item));
        if (item.Value is { } current)
        {
            evidenceEntries += ValidateItemClaims(current, nameof(item));
        }

        foreach (var candidate in item.Candidates)
        {
            evidenceEntries += ValidateItemClaims(candidate.Value, nameof(item));
        }

        if (evidenceEntries > MaxEvidenceEntries)
        {
            throw new ArgumentException(
                $"An occupied-cell observation cannot contain more than {MaxEvidenceEntries} evidence entries.",
                nameof(item));
        }

        ObservationId = normalizedId;
        Anchor = anchor;
        Item = item;
    }

    public string ObservationId { get; }

    public GridCellAddress Anchor { get; }

    public EvidencedValue<RecognizedItem> Item { get; }

    private static int ValidateItemClaims(RecognizedItem item, string parameterName)
    {
        return ValidateClaimBounds(item.CanonicalId, parameterName) +
               ValidateClaimBounds(item.DisplayName, parameterName) +
               ValidateClaimBounds(item.Quantity, parameterName) +
               ValidateClaimBounds(item.WidthCells, parameterName) +
               ValidateClaimBounds(item.HeightCells, parameterName) +
               ValidateClaimBounds(item.Rotated, parameterName) +
               ValidateClaimBounds(item.FoundInRaid, parameterName) +
               ValidateClaimBounds(item.Condition, parameterName);
    }

    private static int ValidateClaimBounds<T>(EvidencedValue<T> field, string parameterName)
    {
        if (field.Candidates.Count > MaxCandidatesPerClaim)
        {
            throw new ArgumentException(
                $"An evidence claim cannot contain more than {MaxCandidatesPerClaim} candidates.",
                parameterName);
        }

        if (field.Corrections.Count > MaxCorrectionsPerClaim)
        {
            throw new ArgumentException(
                $"An evidence claim cannot contain more than {MaxCorrectionsPerClaim} corrections.",
                parameterName);
        }

        return field.Candidates.Count + field.Corrections.Count;
    }
}

/// <summary>One bounded reconstruction request produced from a single capture.</summary>
public sealed record GridReconstructionRequest
{
    public const int MaxObservations = GridGeometry.MaxCells;

    public GridReconstructionRequest(
        InventoryGridSurface surface,
        DetectedGridLattice? lattice,
        IReadOnlyList<GridCellObservation> occupiedCells,
        double? verticalScrollPosition = null)
    {
        if (!Enum.IsDefined(surface))
        {
            throw new ArgumentOutOfRangeException(nameof(surface));
        }

        ArgumentNullException.ThrowIfNull(occupiedCells);
        var observationCount = occupiedCells.Count;
        if (observationCount is < 0 or > MaxObservations)
        {
            throw new ArgumentException(
                $"A reconstruction cannot contain more than {MaxObservations} occupied-cell observations.",
                nameof(occupiedCells));
        }

        Surface = surface;
        Lattice = lattice;
        OccupiedCells = Copy(occupiedCells, observationCount, nameof(occupiedCells));
        VerticalScrollPosition = ValidScrollPosition(verticalScrollPosition, nameof(verticalScrollPosition));
    }

    public InventoryGridSurface Surface { get; }

    public DetectedGridLattice? Lattice { get; }

    public IReadOnlyList<GridCellObservation> OccupiedCells { get; }

    /// <summary>Scrollbar thumb position from top (0) to bottom (1), when visible and readable.</summary>
    public double? VerticalScrollPosition { get; }

    private static double? ValidScrollPosition(double? value, string parameterName) =>
        value is null || double.IsFinite(value.Value) && value.Value is >= 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(parameterName);

    private static ReadOnlyCollection<GridCellObservation> Copy(
        IReadOnlyList<GridCellObservation> values,
        int count,
        string parameterName)
    {
        var copy = new GridCellObservation[count];
        for (var index = 0; index < count; index++)
        {
            copy[index] = values[index] ??
                throw new ArgumentException("An observation list cannot contain null entries.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }
}

public sealed record GridReconstructionIssue
{
    public GridReconstructionIssue(
        GridReconstructionIssueKind kind,
        string? observationId = null,
        GridCellAddress? anchor = null,
        string? relatedObservationId = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        Kind = kind;
        ObservationId = NormalizeId(observationId, nameof(observationId));
        Anchor = anchor;
        RelatedObservationId = NormalizeId(relatedObservationId, nameof(relatedObservationId));
    }

    public GridReconstructionIssueKind Kind { get; }

    public string? ObservationId { get; }

    public GridCellAddress? Anchor { get; }

    public string? RelatedObservationId { get; }

    private static string? NormalizeId(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= GridCellObservation.MaxObservationIdLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>A safe grid payload plus the evidence intentionally kept out of exact answers.</summary>
public sealed record GridReconstructionResult
{
    public const int MaxIssues = (GridReconstructionRequest.MaxObservations * 10) + 1;

    public GridReconstructionResult(
        GridReconstructionOutcome outcome,
        InventoryGridSurface surface,
        GridRecognition? recognition,
        IReadOnlyList<GridCellObservation> unresolvedCells,
        IReadOnlyList<GridReconstructionIssue> issues,
        double? verticalScrollPosition = null)
    {
        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        if (!Enum.IsDefined(surface))
        {
            throw new ArgumentOutOfRangeException(nameof(surface));
        }

        ArgumentNullException.ThrowIfNull(unresolvedCells);
        ArgumentNullException.ThrowIfNull(issues);
        var unresolvedCount = unresolvedCells.Count;
        var issueCount = issues.Count;
        if (unresolvedCount is < 0 or > GridReconstructionRequest.MaxObservations)
        {
            throw new ArgumentException("Unresolved cells exceed the reconstruction bounds.", nameof(unresolvedCells));
        }

        if (issueCount is < 0 or > MaxIssues)
        {
            throw new ArgumentException("Issues exceed the reconstruction bounds.", nameof(issues));
        }

        if (outcome == GridReconstructionOutcome.NoChange && recognition is not null)
        {
            throw new ArgumentException("A no-change result cannot carry replacement grid state.", nameof(recognition));
        }

        if (outcome != GridReconstructionOutcome.NoChange && recognition is null)
        {
            throw new ArgumentException("A reconstruction result must carry its safe grid state.", nameof(recognition));
        }

        if (outcome == GridReconstructionOutcome.Complete &&
            (unresolvedCount != 0 || issueCount != 0))
        {
            throw new ArgumentException("A complete reconstruction cannot carry unresolved evidence.");
        }

        Outcome = outcome;
        Surface = surface;
        Recognition = recognition;
        UnresolvedCells = Copy(unresolvedCells, unresolvedCount, nameof(unresolvedCells));
        Issues = Copy(issues, issueCount, nameof(issues));
        VerticalScrollPosition = verticalScrollPosition is null ||
                                 double.IsFinite(verticalScrollPosition.Value) && verticalScrollPosition.Value is >= 0 and <= 1
            ? verticalScrollPosition
            : throw new ArgumentOutOfRangeException(nameof(verticalScrollPosition));
    }

    public GridReconstructionOutcome Outcome { get; }

    public InventoryGridSurface Surface { get; }

    public GridRecognition? Recognition { get; }

    public IReadOnlyList<GridCellObservation> UnresolvedCells { get; }

    public IReadOnlyList<GridReconstructionIssue> Issues { get; }

    public double? VerticalScrollPosition { get; }

    private static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> values, int count, string parameterName)
    {
        var copy = new T[count];
        for (var index = 0; index < count; index++)
        {
            var value = values[index];
            if (value is null)
            {
                throw new ArgumentException("A result list cannot contain null entries.", parameterName);
            }

            copy[index] = value;
        }

        return Array.AsReadOnly(copy);
    }
}

/// <summary>One separately framed carried grid after safe reconstruction.</summary>
public sealed record CarriedGridReconstructionResult(
    CarriedGridIdentity Identity,
    GridReconstructionResult Reconstruction)
{
    public GridReconstructionResult Reconstruction { get; } = Reconstruction is { Surface: InventoryGridSurface.CarriedInventory }
        ? Reconstruction
        : throw new ArgumentException("A carried-grid result must describe carried inventory.", nameof(Reconstruction));
}

/// <summary>One carried grid retained with a Loot Scan result for review presentation.</summary>
public sealed record CarriedGridRecognition(CarriedGridIdentity Identity, GridRecognition Recognition)
{
    public GridRecognition Recognition { get; } = Recognition ?? throw new ArgumentNullException(nameof(Recognition));
}
