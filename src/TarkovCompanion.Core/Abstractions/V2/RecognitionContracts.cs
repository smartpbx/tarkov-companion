using System.Text.Json.Serialization;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Abstractions.V2;

/// <summary>What a frame was recognized as. An undetermined context is an absent value, never a member.</summary>
public enum RecognizedContext
{
    Item = 1,
    Grid,
    Loot,
    Stash,
    Ammo,
    Keys,
    QuestItems,
    ExtractsAndMap,
    HealthAndCharacter,
    Flea,
}

public sealed record RecognitionResultHeader
{
    public RecognitionResultHeader(
        string resultId,
        V2ContractVersion contractVersion,
        CaptureSessionId sessionId,
        string artifactId,
        DateTimeOffset capturedUtc,
        ScanIntent requestedIntent,
        EvidencedValue<RecognizedContext?> detectedContext)
    {
        ResultId = V2ContractGuard.Required(resultId, nameof(resultId));
        ContractVersion = V2ContractGuard.Defined(contractVersion, nameof(contractVersion));
        SessionId = V2ContractGuard.Defined(sessionId, nameof(sessionId));
        ArtifactId = V2ContractGuard.Required(artifactId, nameof(artifactId));
        CapturedUtc = V2ContractGuard.Utc(capturedUtc, nameof(capturedUtc));
        RequestedIntent = V2ContractGuard.Defined(requestedIntent, nameof(requestedIntent));
        DetectedContext = V2ContractGuard.Defined(detectedContext, nameof(detectedContext));
    }

    public string ResultId { get; }

    public V2ContractVersion ContractVersion { get; }

    public CaptureSessionId SessionId { get; }

    public string ArtifactId { get; }

    /// <summary>When the pixels were taken, which the provenance observed time may follow.</summary>
    public DateTimeOffset CapturedUtc { get; }

    public ScanIntent RequestedIntent { get; }

    /// <summary>The detected context with its own candidates, bounds, confidence, and corrections.</summary>
    public EvidencedValue<RecognizedContext?> DetectedContext { get; }
}

/// <summary>
/// The common result boundary. The payload is typed while its result-level evidence remains
/// as inspectable as any individual recognized field.
/// </summary>
public sealed record RecognitionResultEnvelope<T>
    where T : class, IRecognitionPayload
{
    public RecognitionResultEnvelope(RecognitionResultHeader header, EvidencedValue<T> result)
    {
        V2WirePayloads.Require(V2WirePayloads.Recognition, typeof(T), "recognition");
        Header = V2ContractGuard.NotNull(header, nameof(header));
        Result = V2ContractGuard.NotNull(result, nameof(result));

        if (result.Provenance.ObservedUtc < header.CapturedUtc ||
            header.DetectedContext.Provenance.ObservedUtc < header.CapturedUtc)
        {
            throw new ArgumentException("Recognition cannot be observed before its capture was taken.", nameof(result));
        }
    }

    public RecognitionResultHeader Header { get; }

    public EvidencedValue<T> Result { get; }
}

public enum ItemConditionKind
{
    NotApplicable = 1,
    Durability,
    Uses,
    Charges,
    Resource,
}

/// <summary>Visible durability, uses, charges, or resource, e.g. 38/50; never a guessed full value.</summary>
public sealed record ItemConditionReading
{
    public ItemConditionReading(ItemConditionKind kind, double? current, double? maximum)
    {
        Kind = V2ContractGuard.Defined(kind, nameof(kind));

        if (kind == ItemConditionKind.NotApplicable)
        {
            if (current is not null || maximum is not null)
            {
                throw new ArgumentException("An item without a condition carries no reading.", nameof(current));
            }
        }
        else if (current is not { } now || !double.IsFinite(now) || now < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(current));
        }

        if (maximum is { } max && (!double.IsFinite(max) || max <= 0 || current > max))
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        Current = current;
        Maximum = maximum;
    }

    public ItemConditionKind Kind { get; }

    public double? Current { get; }

    public double? Maximum { get; }

    public static ItemConditionReading NotApplicable { get; } = new(ItemConditionKind.NotApplicable, null, null);
}

/// <summary>
/// One recognized item. Width and height are the footprint as displayed in the grid, after
/// rotation, so fit and swap calculations never need the catalog orientation; occupied squares
/// are their product. An item with no condition reports <see cref="ItemConditionReading.NotApplicable"/>.
/// </summary>
public sealed record RecognizedItem(
    EvidencedValue<string> CanonicalId,
    EvidencedValue<string> DisplayName,
    EvidencedValue<int?> Quantity,
    EvidencedValue<int?> WidthCells,
    EvidencedValue<int?> HeightCells,
    EvidencedValue<bool?> Rotated,
    EvidencedValue<bool?> FoundInRaid,
    EvidencedValue<ItemConditionReading> Condition) : IRecognitionPayload
{
    public EvidencedValue<string> CanonicalId { get; } = V2ContractGuard.NotNull(CanonicalId, nameof(CanonicalId));

    public EvidencedValue<string> DisplayName { get; } = V2ContractGuard.NotNull(DisplayName, nameof(DisplayName));

    public EvidencedValue<int?> Quantity { get; } = V2ContractGuard.AtLeast(Quantity, 1, nameof(Quantity));

    public EvidencedValue<int?> WidthCells { get; } = V2ContractGuard.AtLeast(WidthCells, 1, nameof(WidthCells));

    public EvidencedValue<int?> HeightCells { get; } = V2ContractGuard.AtLeast(HeightCells, 1, nameof(HeightCells));

    public EvidencedValue<bool?> Rotated { get; } = V2ContractGuard.NotNull(Rotated, nameof(Rotated));

    public EvidencedValue<bool?> FoundInRaid { get; } = V2ContractGuard.NotNull(FoundInRaid, nameof(FoundInRaid));

    public EvidencedValue<ItemConditionReading> Condition { get; } = V2ContractGuard.NotNull(Condition, nameof(Condition));
}

public readonly record struct GridCellAddress
{
    // System.Text.Json builds a struct through its implicit parameterless constructor unless told
    // otherwise, which silently round-tripped ids to Guid.Empty and addresses to (0, 0).
    [JsonConstructor]
    public GridCellAddress(int row, int column)
    {
        if (row < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(row));
        }

        if (column < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }

        Row = row;
        Column = column;
    }

    public int Row { get; }

    public int Column { get; }
}

public sealed record GridGeometry(
    EvidencedValue<int?> Rows,
    EvidencedValue<int?> Columns,
    EvidencedValue<int?> CellWidthPixels,
    EvidencedValue<int?> CellHeightPixels)
{
    public EvidencedValue<int?> Rows { get; } = V2ContractGuard.AtLeast(Rows, 1, nameof(Rows));

    public EvidencedValue<int?> Columns { get; } = V2ContractGuard.AtLeast(Columns, 1, nameof(Columns));

    public EvidencedValue<int?> CellWidthPixels { get; } = V2ContractGuard.AtLeast(CellWidthPixels, 1, nameof(CellWidthPixels));

    public EvidencedValue<int?> CellHeightPixels { get; } = V2ContractGuard.AtLeast(CellHeightPixels, 1, nameof(CellHeightPixels));
}

/// <summary>
/// An occupied footprint anchored at its top-left cell and spanning the item's displayed width
/// and height. A container item whose contents were opened names the nested container's path.
/// </summary>
public sealed record GridCellRecognition(
    GridCellAddress Anchor,
    EvidencedValue<RecognizedItem> Item,
    string? NestedContainerPath = null)
{
    public GridCellAddress Anchor { get; } = Anchor;

    public EvidencedValue<RecognizedItem> Item { get; } = V2ContractGuard.NotNull(Item, nameof(Item));

    public string? NestedContainerPath { get; } = NestedContainerPath is null
        ? null
        : ContainerPaths.Validate(NestedContainerPath, nameof(NestedContainerPath));
}

public sealed record GridRecognition : IRecognitionPayload
{
    public GridRecognition(GridGeometry geometry, IReadOnlyList<GridCellRecognition> cells)
    {
        Geometry = V2ContractGuard.NotNull(geometry, nameof(geometry));
        Cells = V2ContractGuard.List(cells, nameof(cells));

        // Footprints are checked only where every dimension involved is known; an unknown span
        // is not assumed to be one cell, and an unknown grid is not assumed to be unbounded.
        var occupied = new HashSet<GridCellAddress>();
        foreach (var cell in Cells)
        {
            if (cell.Item.Value is not { } item)
            {
                if (!occupied.Add(cell.Anchor))
                {
                    throw new ArgumentException("Two footprints cannot share an anchor.", nameof(cells));
                }

                continue;
            }

            if (item.WidthCells.Value is not { } width || item.HeightCells.Value is not { } height)
            {
                if (!occupied.Add(cell.Anchor))
                {
                    throw new ArgumentException("Two footprints cannot share an anchor.", nameof(cells));
                }

                continue;
            }

            if (geometry.Rows.Value is { } rows && cell.Anchor.Row + height > rows ||
                geometry.Columns.Value is { } columns && cell.Anchor.Column + width > columns)
            {
                throw new ArgumentException("A footprint cannot extend past the recognized grid.", nameof(cells));
            }

            for (var row = cell.Anchor.Row; row < cell.Anchor.Row + height; row++)
            {
                for (var column = cell.Anchor.Column; column < cell.Anchor.Column + width; column++)
                {
                    if (!occupied.Add(new GridCellAddress(row, column)))
                    {
                        throw new ArgumentException("Recognized footprints cannot overlap.", nameof(cells));
                    }
                }
            }
        }
    }

    public GridGeometry Geometry { get; }

    public IReadOnlyList<GridCellRecognition> Cells { get; }
}

public sealed record LootRecognition(
    GridRecognition VisibleLoot,
    GridRecognition CarriedInventory) : IRecognitionPayload
{
    public GridRecognition VisibleLoot { get; } = V2ContractGuard.NotNull(VisibleLoot, nameof(VisibleLoot));

    public GridRecognition CarriedInventory { get; } = V2ContractGuard.NotNull(CarriedInventory, nameof(CarriedInventory));
}

/// <summary>
/// Container membership as a path: a root such as <c>stash</c>, then one segment per opened
/// container item, e.g. <c>stash/backpack-7f3a</c>.
/// </summary>
public static class ContainerPaths
{
    public const int MaxDepth = 8;

    public const int MaxLength = 256;

    public static string Validate(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var segments = path.Split('/');
        if (path.Length > MaxLength || segments.Length > MaxDepth ||
            segments.Any(segment => segment.Length == 0 || segment.Any(char.IsWhiteSpace)))
        {
            throw new ArgumentException("A container path is 1-8 non-empty segments without whitespace.", parameterName);
        }

        return path;
    }

    public static string? Parent(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator < 0 ? null : path[..separator];
    }
}

/// <summary>
/// One screenshot's grid placed in a container. The origin is where the region's top-left cell
/// sits in container coordinates; overlap between regions is the intersection of their placed
/// footprints. A region whose origin is undetermined cannot be stitched, so its items stay out
/// of exact totals rather than being counted twice.
/// </summary>
public sealed record StashCaptureRegion
{
    public StashCaptureRegion(
        string regionId,
        string artifactId,
        int captureOrdinal,
        string containerPath,
        EvidencedValue<GridCellAddress?> originInContainer,
        GridRecognition grid)
    {
        RegionId = V2ContractGuard.Required(regionId, nameof(regionId));
        ArtifactId = V2ContractGuard.Required(artifactId, nameof(artifactId));
        CaptureOrdinal = captureOrdinal >= 0 ? captureOrdinal : throw new ArgumentOutOfRangeException(nameof(captureOrdinal));
        ContainerPath = ContainerPaths.Validate(containerPath, nameof(containerPath));
        OriginInContainer = V2ContractGuard.NotNull(originInContainer, nameof(originInContainer));
        Grid = V2ContractGuard.NotNull(grid, nameof(grid));
    }

    public string RegionId { get; }

    public string ArtifactId { get; }

    /// <summary>The same ordinal the capture carries in its session progress.</summary>
    public int CaptureOrdinal { get; }

    public string ContainerPath { get; }

    public EvidencedValue<GridCellAddress?> OriginInContainer { get; }

    public GridRecognition Grid { get; }
}

/// <summary>How much of one container was observed; closed or unscrolled space is not invented.</summary>
public sealed record StashContainerCoverage
{
    public StashContainerCoverage(
        string containerPath,
        EvidencedValue<int?> observedCells,
        EvidencedValue<int?> totalCells)
    {
        ContainerPath = ContainerPaths.Validate(containerPath, nameof(containerPath));
        ObservedCells = V2ContractGuard.AtLeast(observedCells, 0, nameof(observedCells));
        TotalCells = V2ContractGuard.AtLeast(totalCells, 1, nameof(totalCells));

        if (observedCells.Value > totalCells.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(observedCells), "Observed cells must be within the container.");
        }
    }

    public string ContainerPath { get; }

    public EvidencedValue<int?> ObservedCells { get; }

    public EvidencedValue<int?> TotalCells { get; }
}

public sealed record StashRecognition : IRecognitionPayload
{
    public StashRecognition(
        string snapshotId,
        IReadOnlyList<StashCaptureRegion> capturedRegions,
        IReadOnlyList<StashContainerCoverage> coverage,
        EvidencedValue<long?> totalKnownValueRoubles,
        EvidencedValue<int?> unresolvedCells)
    {
        SnapshotId = V2ContractGuard.Required(snapshotId, nameof(snapshotId));
        CapturedRegions = V2ContractGuard.List(capturedRegions, nameof(capturedRegions));
        Coverage = V2ContractGuard.List(coverage, nameof(coverage));
        TotalKnownValueRoubles = V2ContractGuard.AtLeast(totalKnownValueRoubles, 0, nameof(totalKnownValueRoubles));
        UnresolvedCells = V2ContractGuard.AtLeast(unresolvedCells, 0, nameof(unresolvedCells));

        if (CapturedRegions.Select(region => region.RegionId).Distinct(StringComparer.Ordinal).Count() != CapturedRegions.Count ||
            CapturedRegions.Select(region => region.CaptureOrdinal).Distinct().Count() != CapturedRegions.Count)
        {
            throw new ArgumentException("Region ids and capture ordinals must be unique.", nameof(capturedRegions));
        }

        var covered = Coverage.Select(item => item.ContainerPath).ToHashSet(StringComparer.Ordinal);
        if (covered.Count != Coverage.Count ||
            CapturedRegions.Any(region => !covered.Contains(region.ContainerPath)))
        {
            throw new ArgumentException("Every captured container needs exactly one coverage entry.", nameof(coverage));
        }

        // A nested container is reached by opening an item in its parent, so its parent must be
        // covered too and the opening cell must sit in a region of that parent.
        var opened = CapturedRegions
            .SelectMany(region => region.Grid.Cells
                .Where(cell => cell.NestedContainerPath is not null)
                .Select(cell => (Parent: region.ContainerPath, Path: cell.NestedContainerPath!)))
            .ToArray();

        if (opened.Any(item => ContainerPaths.Parent(item.Path) != item.Parent))
        {
            throw new ArgumentException("A nested container path must extend the container it was opened from.", nameof(capturedRegions));
        }

        foreach (var path in covered)
        {
            if (ContainerPaths.Parent(path) is { } parent &&
                (!covered.Contains(parent) || !opened.Any(item => item.Path == path)))
            {
                throw new ArgumentException($"Nested container {path} has no covered parent cell that opened it.", nameof(coverage));
            }
        }
    }

    public string SnapshotId { get; }

    public IReadOnlyList<StashCaptureRegion> CapturedRegions { get; }

    public IReadOnlyList<StashContainerCoverage> Coverage { get; }

    public EvidencedValue<long?> TotalKnownValueRoubles { get; }

    public EvidencedValue<int?> UnresolvedCells { get; }
}

public sealed record AmmoRecognition(
    EvidencedValue<string> CanonicalRoundId,
    EvidencedValue<string> DisplayName,
    EvidencedValue<string> Caliber,
    EvidencedValue<int?> Quantity) : IRecognitionPayload
{
    public EvidencedValue<string> CanonicalRoundId { get; } = V2ContractGuard.NotNull(CanonicalRoundId, nameof(CanonicalRoundId));

    public EvidencedValue<string> DisplayName { get; } = V2ContractGuard.NotNull(DisplayName, nameof(DisplayName));

    public EvidencedValue<string> Caliber { get; } = V2ContractGuard.NotNull(Caliber, nameof(Caliber));

    public EvidencedValue<int?> Quantity { get; } = V2ContractGuard.AtLeast(Quantity, 0, nameof(Quantity));
}

public sealed record KeyRecognition(
    RecognizedItem Item,
    EvidencedValue<int?> UsesRemaining,
    EvidencedValue<string> Opens) : IRecognitionPayload
{
    public RecognizedItem Item { get; } = V2ContractGuard.NotNull(Item, nameof(Item));

    public EvidencedValue<int?> UsesRemaining { get; } = V2ContractGuard.AtLeast(UsesRemaining, 0, nameof(UsesRemaining));

    public EvidencedValue<string> Opens { get; } = V2ContractGuard.NotNull(Opens, nameof(Opens));
}

public sealed record QuestItemRecognition(
    RecognizedItem Item,
    EvidencedValue<string> QuestId,
    EvidencedValue<int?> OutstandingQuantity,
    EvidencedValue<bool?> RequiredFoundInRaid) : IRecognitionPayload
{
    public RecognizedItem Item { get; } = V2ContractGuard.NotNull(Item, nameof(Item));

    public EvidencedValue<string> QuestId { get; } = V2ContractGuard.NotNull(QuestId, nameof(QuestId));

    public EvidencedValue<int?> OutstandingQuantity { get; } = V2ContractGuard.AtLeast(OutstandingQuantity, 0, nameof(OutstandingQuantity));

    public EvidencedValue<bool?> RequiredFoundInRaid { get; } = V2ContractGuard.NotNull(RequiredFoundInRaid, nameof(RequiredFoundInRaid));
}

/// <summary>
/// One visible flea row. The row is joined by geometry, so its bounds are kept apart from the
/// item icon's; listing condition is the item's <see cref="RecognizedItem.Condition"/>.
/// </summary>
public sealed record FleaListingRecognition(
    EvidenceRegion RowBounds,
    EvidencedValue<RecognizedItem> Item,
    EvidencedValue<long?> PriceRoubles,
    EvidencedValue<int?> Quantity,
    EvidencedValue<long?> PricePerUnitRoubles)
{
    public EvidenceRegion RowBounds { get; } = V2ContractGuard.NotNull(RowBounds, nameof(RowBounds));

    public EvidencedValue<RecognizedItem> Item { get; } = V2ContractGuard.NotNull(Item, nameof(Item));

    public EvidencedValue<long?> PriceRoubles { get; } = V2ContractGuard.AtLeast(PriceRoubles, 0, nameof(PriceRoubles));

    public EvidencedValue<int?> Quantity { get; } = V2ContractGuard.AtLeast(Quantity, 1, nameof(Quantity));

    public EvidencedValue<long?> PricePerUnitRoubles { get; } = V2ContractGuard.AtLeast(PricePerUnitRoubles, 0, nameof(PricePerUnitRoubles));
}

/// <summary>The rows the user visibly opened, with every raw OCR line kept for review.</summary>
public sealed record FleaPageRecognition(
    IReadOnlyList<FleaListingRecognition> Listings,
    IReadOnlyList<RawOcrLine> RawOcrLines) : IRecognitionPayload
{
    public IReadOnlyList<FleaListingRecognition> Listings { get; } = V2ContractGuard.List(Listings, nameof(Listings));

    public IReadOnlyList<RawOcrLine> RawOcrLines { get; } = V2ContractGuard.List(RawOcrLines, nameof(RawOcrLines));
}

/// <summary>An exfil leaves the raid; a transit moves to another map and is absent from extract catalogs.</summary>
public enum ExtractKind
{
    Exfil = 1,
    Transit,
}

/// <summary>What the extract panel showed. An unread or illegible state is an absent value.</summary>
public enum ExtractAvailability
{
    Active = 1,
    Conditional,
    Pending,
    Closed,
}

public sealed record ExtractRecognition
{
    public ExtractRecognition(
        EvidencedValue<string> slotLabel,
        EvidencedValue<ExtractKind?> kind,
        EvidencedValue<string> canonicalId,
        EvidencedValue<string> displayName,
        EvidencedValue<string> destinationMapId,
        EvidencedValue<ExtractAvailability?> availability)
    {
        SlotLabel = V2ContractGuard.NotNull(slotLabel, nameof(slotLabel));
        Kind = V2ContractGuard.Defined(kind, nameof(kind));
        CanonicalId = V2ContractGuard.NotNull(canonicalId, nameof(canonicalId));
        DisplayName = V2ContractGuard.NotNull(displayName, nameof(displayName));
        DestinationMapId = V2ContractGuard.NotNull(destinationMapId, nameof(destinationMapId));
        Availability = V2ContractGuard.Defined(availability, nameof(availability));

        if (kind.Value == ExtractKind.Exfil && destinationMapId.Value is not null)
        {
            throw new ArgumentException("An exfil leaves the raid and has no destination map.", nameof(destinationMapId));
        }
    }

    /// <summary>The slot label as read, e.g. EXFIL01 or TRANSIT02, whose zero often OCRs as O or @.</summary>
    public EvidencedValue<string> SlotLabel { get; }

    public EvidencedValue<ExtractKind?> Kind { get; }

    public EvidencedValue<string> CanonicalId { get; }

    public EvidencedValue<string> DisplayName { get; }

    public EvidencedValue<string> DestinationMapId { get; }

    public EvidencedValue<ExtractAvailability?> Availability { get; }
}

public enum RaidClockBasis
{
    CountedFromRaidStart = 1,
    ObservedOnExtractScreen,
}

/// <summary>
/// The clock as it read at <see cref="AsOfUtc"/>. For an observed reading that instant is when the
/// screenshot was taken, not when this application acquired the file: #261 ages the clock from
/// capture time, and an import minutes later must not add those minutes to the raid. An unknown
/// clock is an absent reading, not a basis.
/// </summary>
public sealed record RaidClockReading
{
    public RaidClockReading(TimeSpan remaining, RaidClockBasis basis, DateTimeOffset asOfUtc)
    {
        Remaining = remaining >= TimeSpan.Zero
            ? remaining
            : throw new ArgumentOutOfRangeException(nameof(remaining), "A raid clock reading must be zero or more.");
        Basis = V2ContractGuard.Defined(basis, nameof(basis));
        AsOfUtc = V2ContractGuard.Utc(asOfUtc, nameof(asOfUtc));
    }

    public TimeSpan Remaining { get; }

    public RaidClockBasis Basis { get; }

    public DateTimeOffset AsOfUtc { get; }
}

/// <summary>One OCR line before headers, clocks, or extract matches are removed.</summary>
public sealed record RawOcrLine(EvidencedValue<string> Text)
{
    public EvidencedValue<string> Text { get; } = V2ContractGuard.NotNull(Text, nameof(Text));
}

public sealed record ExtractMapRecognition(
    EvidencedValue<string> MapId,
    IReadOnlyList<ExtractRecognition> Extracts,
    IReadOnlyList<RawOcrLine> RawOcrLines,
    EvidencedValue<RaidClockReading> RaidTimeRemaining) : IRecognitionPayload
{
    public EvidencedValue<string> MapId { get; } = V2ContractGuard.NotNull(MapId, nameof(MapId));

    public IReadOnlyList<ExtractRecognition> Extracts { get; } = V2ContractGuard.List(Extracts, nameof(Extracts));

    public IReadOnlyList<RawOcrLine> RawOcrLines { get; } = V2ContractGuard.List(RawOcrLines, nameof(RawOcrLines));

    public EvidencedValue<RaidClockReading> RaidTimeRemaining { get; } = ValidateClock(RaidTimeRemaining);

    private static EvidencedValue<RaidClockReading> ValidateClock(EvidencedValue<RaidClockReading> clock)
    {
        V2ContractGuard.NotNull(clock, nameof(RaidTimeRemaining));
        if (clock.Value is not { } reading)
        {
            return clock;
        }

        var source = clock.Provenance.SourceClass;
        var consistent = reading.Basis switch
        {
            RaidClockBasis.ObservedOnExtractScreen =>
                source is EvidenceSourceClass.GameWrittenScreenshot or EvidenceSourceClass.ExternalVisiblePixels &&
                reading.AsOfUtc <= clock.Provenance.ObservedUtc,
            RaidClockBasis.CountedFromRaidStart =>
                source is EvidenceSourceClass.GameWrittenLog or EvidenceSourceClass.UserEntered or EvidenceSourceClass.DerivedCalculation,
            _ => false,
        };

        if (!consistent)
        {
            throw new ArgumentException(
                $"A {reading.Basis} clock cannot come from {source} evidence or postdate its observation.",
                nameof(RaidTimeRemaining));
        }

        return clock;
    }
}

public enum CharacterRegion
{
    Head = 1,
    Thorax,
    Stomach,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg,
}

/// <summary>A displayed limb state. An absent or illegible display is an absent value, never Healthy.</summary>
public enum CharacterRegionState
{
    Healthy = 1,
    Injured,
    Critical,
    Destroyed,
}

public sealed record CharacterRegionReading(
    CharacterRegion Region,
    CharacterRegionState? State,
    double? VisibleFraction)
{
    public CharacterRegion Region { get; } = V2ContractGuard.Defined(Region, nameof(Region));

    public CharacterRegionState? State { get; } = V2ContractGuard.DefinedOptional(State, nameof(State));

    public double? VisibleFraction { get; } = VisibleFraction is null or (>= 0 and <= 1)
        ? VisibleFraction
        : throw new ArgumentOutOfRangeException(nameof(VisibleFraction));
}

public sealed record HealthCharacterRecognition(
    EvidencedValue<bool?> CharacterDisplayPresent,
    IReadOnlyList<EvidencedValue<CharacterRegionReading>> Regions,
    IReadOnlyList<EvidencedValue<string>> Conditions) : IRecognitionPayload
{
    public EvidencedValue<bool?> CharacterDisplayPresent { get; } = V2ContractGuard.NotNull(CharacterDisplayPresent, nameof(CharacterDisplayPresent));

    public IReadOnlyList<EvidencedValue<CharacterRegionReading>> Regions { get; } = V2ContractGuard.List(Regions, nameof(Regions));

    public IReadOnlyList<EvidencedValue<string>> Conditions { get; } = V2ContractGuard.List(Conditions, nameof(Conditions));
}

/// <summary>
/// What an Auto scan returns when it cannot say what the frame is. The header's detected context
/// is absent and carries the candidate contexts; the raw lines keep what was read for review.
/// </summary>
public sealed record UnresolvedContextRecognition(IReadOnlyList<RawOcrLine> RawOcrLines) : IRecognitionPayload
{
    public IReadOnlyList<RawOcrLine> RawOcrLines { get; } = V2ContractGuard.List(RawOcrLines, nameof(RawOcrLines));
}

public abstract record ContextualRecognitionResult<T>
    where T : class, IRecognitionPayload
{
    private protected ContextualRecognitionResult(
        RecognitionResultEnvelope<T> recognition,
        RecognizedContext? requiredContext)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        var detected = recognition.Header.DetectedContext;
        var matches = requiredContext is null
            ? detected.Value is null
            : detected.Value == requiredContext && detected.Status.Completeness == ResultCompleteness.Complete;

        if (!matches)
        {
            throw new ArgumentException(
                requiredContext is null
                    ? "An unresolved result must not claim a detected context."
                    : $"Recognition context must be a complete {requiredContext}.",
                nameof(recognition));
        }

        Recognition = recognition;
    }

    public RecognitionResultEnvelope<T> Recognition { get; }
}

public sealed record ItemRecognitionResult : ContextualRecognitionResult<RecognizedItem>
{
    public ItemRecognitionResult(RecognitionResultEnvelope<RecognizedItem> recognition)
        : base(recognition, RecognizedContext.Item)
    {
    }
}

public sealed record GridRecognitionResult : ContextualRecognitionResult<GridRecognition>
{
    public GridRecognitionResult(RecognitionResultEnvelope<GridRecognition> recognition)
        : base(recognition, RecognizedContext.Grid)
    {
    }
}

public sealed record LootRecognitionResult : ContextualRecognitionResult<LootRecognition>
{
    public LootRecognitionResult(RecognitionResultEnvelope<LootRecognition> recognition)
        : base(recognition, RecognizedContext.Loot)
    {
    }
}

public sealed record StashRecognitionResult : ContextualRecognitionResult<StashRecognition>
{
    public StashRecognitionResult(RecognitionResultEnvelope<StashRecognition> recognition)
        : base(recognition, RecognizedContext.Stash)
    {
    }
}

public sealed record AmmoRecognitionResult : ContextualRecognitionResult<AmmoRecognition>
{
    public AmmoRecognitionResult(RecognitionResultEnvelope<AmmoRecognition> recognition)
        : base(recognition, RecognizedContext.Ammo)
    {
    }
}

public sealed record KeyRecognitionResult : ContextualRecognitionResult<KeyRecognition>
{
    public KeyRecognitionResult(RecognitionResultEnvelope<KeyRecognition> recognition)
        : base(recognition, RecognizedContext.Keys)
    {
    }
}

public sealed record QuestItemRecognitionResult : ContextualRecognitionResult<QuestItemRecognition>
{
    public QuestItemRecognitionResult(RecognitionResultEnvelope<QuestItemRecognition> recognition)
        : base(recognition, RecognizedContext.QuestItems)
    {
    }
}

public sealed record FleaRecognitionResult : ContextualRecognitionResult<FleaPageRecognition>
{
    public FleaRecognitionResult(RecognitionResultEnvelope<FleaPageRecognition> recognition)
        : base(recognition, RecognizedContext.Flea)
    {
    }
}

/// <summary>An extract-screen result. An observed raid clock is as of the header's capture time.</summary>
public sealed record ExtractMapRecognitionResult : ContextualRecognitionResult<ExtractMapRecognition>
{
    public ExtractMapRecognitionResult(RecognitionResultEnvelope<ExtractMapRecognition> recognition)
        : base(recognition, RecognizedContext.ExtractsAndMap)
    {
        if (recognition.Result.Value?.RaidTimeRemaining.Value is { Basis: RaidClockBasis.ObservedOnExtractScreen } clock &&
            clock.AsOfUtc != recognition.Header.CapturedUtc)
        {
            throw new ArgumentException(
                "An observed raid clock is as of the screenshot's capture time.",
                nameof(recognition));
        }
    }
}

public sealed record HealthCharacterRecognitionResult : ContextualRecognitionResult<HealthCharacterRecognition>
{
    public HealthCharacterRecognitionResult(RecognitionResultEnvelope<HealthCharacterRecognition> recognition)
        : base(recognition, RecognizedContext.HealthAndCharacter)
    {
    }
}

public sealed record UnresolvedContextRecognitionResult : ContextualRecognitionResult<UnresolvedContextRecognition>
{
    public UnresolvedContextRecognitionResult(RecognitionResultEnvelope<UnresolvedContextRecognition> recognition)
        : base(recognition, requiredContext: null)
    {
    }
}
