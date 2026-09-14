using System.Text.Json.Serialization;
using TarkovCompanion.Core.Domain.Evidence;

namespace TarkovCompanion.Core.Abstractions.V2;

public enum RecognizedContext
{
    Unknown,
    Item,
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
        ScanIntent requestedIntent,
        RecognizedContext recognizedContext)
    {
        ResultId = V2ContractGuard.Required(resultId, nameof(resultId));
        ContractVersion = V2ContractGuard.Defined(contractVersion, nameof(contractVersion));
        SessionId = V2ContractGuard.Defined(sessionId, nameof(sessionId));
        ArtifactId = V2ContractGuard.Required(artifactId, nameof(artifactId));
        RequestedIntent = requestedIntent;
        RecognizedContext = recognizedContext;
    }

    public string ResultId { get; }

    public V2ContractVersion ContractVersion { get; }

    public CaptureSessionId SessionId { get; }

    public string ArtifactId { get; }

    public ScanIntent RequestedIntent { get; }

    public RecognizedContext RecognizedContext { get; }
}

/// <summary>
/// The common result boundary. The payload is typed while its result-level evidence remains
/// as inspectable as any individual recognized field.
/// </summary>
public sealed record RecognitionResultEnvelope<T>
{
    public RecognitionResultEnvelope(RecognitionResultHeader header, EvidencedValue<T> result)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(result);
        Header = header;
        Result = result;
    }

    public RecognitionResultHeader Header { get; }

    public EvidencedValue<T> Result { get; }
}

public enum ItemConditionKind
{
    Durability,
    Uses,
    Resource,
}

/// <summary>Visible durability, uses, or resource, e.g. 38/50 or 3/4; never a guessed full value.</summary>
public sealed record ItemConditionReading
{
    public ItemConditionReading(ItemConditionKind kind, double current, double? maximum)
    {
        if (!double.IsFinite(current) || current < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(current));
        }

        if (maximum is { } max && (!double.IsFinite(max) || max <= 0 || current > max))
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        Kind = kind;
        Current = current;
        Maximum = maximum;
    }

    public ItemConditionKind Kind { get; }

    public double Current { get; }

    public double? Maximum { get; }
}

/// <summary>
/// One recognized item. Width and height are the footprint as displayed in the grid, after
/// rotation, so fit and swap calculations never need the catalog orientation; occupied squares
/// are their product. Condition is null with a complete status when the item has none.
/// </summary>
public sealed record RecognizedItem(
    EvidencedValue<string> CanonicalId,
    EvidencedValue<string> DisplayName,
    EvidencedValue<int?> Quantity,
    EvidencedValue<int?> WidthCells,
    EvidencedValue<int?> HeightCells,
    EvidencedValue<bool?> Rotated,
    EvidencedValue<bool?> FoundInRaid,
    EvidencedValue<ItemConditionReading> Condition)
{
    public EvidencedValue<string> CanonicalId { get; } = V2ContractGuard.NotNull(CanonicalId, nameof(CanonicalId));

    public EvidencedValue<string> DisplayName { get; } = V2ContractGuard.NotNull(DisplayName, nameof(DisplayName));

    public EvidencedValue<int?> Quantity { get; } = V2ContractGuard.NotNull(Quantity, nameof(Quantity));

    public EvidencedValue<int?> WidthCells { get; } = V2ContractGuard.NotNull(WidthCells, nameof(WidthCells));

    public EvidencedValue<int?> HeightCells { get; } = V2ContractGuard.NotNull(HeightCells, nameof(HeightCells));

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
    public EvidencedValue<int?> Rows { get; } = V2ContractGuard.NotNull(Rows, nameof(Rows));

    public EvidencedValue<int?> Columns { get; } = V2ContractGuard.NotNull(Columns, nameof(Columns));

    public EvidencedValue<int?> CellWidthPixels { get; } = V2ContractGuard.NotNull(CellWidthPixels, nameof(CellWidthPixels));

    public EvidencedValue<int?> CellHeightPixels { get; } = V2ContractGuard.NotNull(CellHeightPixels, nameof(CellHeightPixels));
}

/// <summary>An occupied footprint whose address is its top-left cell.</summary>
public sealed record GridCellRecognition(
    GridCellAddress Address,
    EvidencedValue<RecognizedItem> Item)
{
    public EvidencedValue<RecognizedItem> Item { get; } = V2ContractGuard.NotNull(Item, nameof(Item));
}

public sealed record GridRecognition(
    GridGeometry Geometry,
    IReadOnlyList<GridCellRecognition> Cells)
{
    public GridGeometry Geometry { get; } = V2ContractGuard.NotNull(Geometry, nameof(Geometry));

    public IReadOnlyList<GridCellRecognition> Cells { get; } = V2ContractGuard.List(Cells, nameof(Cells));
}

public sealed record LootRecognition(
    GridRecognition VisibleLoot,
    GridRecognition CarriedInventory)
{
    public GridRecognition VisibleLoot { get; } = V2ContractGuard.NotNull(VisibleLoot, nameof(VisibleLoot));

    public GridRecognition CarriedInventory { get; } = V2ContractGuard.NotNull(CarriedInventory, nameof(CarriedInventory));
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
        int captureOrder,
        string containerPath,
        EvidencedValue<GridCellAddress?> originInContainer,
        GridRecognition grid)
    {
        if (captureOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(captureOrder));
        }

        RegionId = V2ContractGuard.Required(regionId, nameof(regionId));
        ArtifactId = V2ContractGuard.Required(artifactId, nameof(artifactId));
        CaptureOrder = captureOrder;
        ContainerPath = V2ContractGuard.Required(containerPath, nameof(containerPath));
        OriginInContainer = V2ContractGuard.NotNull(originInContainer, nameof(originInContainer));
        Grid = V2ContractGuard.NotNull(grid, nameof(grid));
    }

    public string RegionId { get; }

    public string ArtifactId { get; }

    public int CaptureOrder { get; }

    /// <summary>Membership: the stash root, or the path of the nested container that was opened.</summary>
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
        ContainerPath = V2ContractGuard.Required(containerPath, nameof(containerPath));
        ObservedCells = V2ContractGuard.NotNull(observedCells, nameof(observedCells));
        TotalCells = V2ContractGuard.NotNull(totalCells, nameof(totalCells));

        if (observedCells.Value is < 0 || totalCells.Value is < 0 ||
            observedCells.Value > totalCells.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(observedCells), "Observed cells must be within the container.");
        }
    }

    public string ContainerPath { get; }

    public EvidencedValue<int?> ObservedCells { get; }

    public EvidencedValue<int?> TotalCells { get; }
}

public sealed record StashRecognition
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
        TotalKnownValueRoubles = V2ContractGuard.NotNull(totalKnownValueRoubles, nameof(totalKnownValueRoubles));
        UnresolvedCells = V2ContractGuard.NotNull(unresolvedCells, nameof(unresolvedCells));

        if (CapturedRegions.Select(region => region.RegionId).Distinct(StringComparer.Ordinal).Count() != CapturedRegions.Count ||
            CapturedRegions.Select(region => region.CaptureOrder).Distinct().Count() != CapturedRegions.Count)
        {
            throw new ArgumentException("Region ids and capture orders must be unique.", nameof(capturedRegions));
        }

        var covered = Coverage.Select(item => item.ContainerPath).ToHashSet(StringComparer.Ordinal);
        if (covered.Count != Coverage.Count ||
            CapturedRegions.Any(region => !covered.Contains(region.ContainerPath)))
        {
            throw new ArgumentException("Every captured container needs exactly one coverage entry.", nameof(coverage));
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
    EvidencedValue<int?> Quantity)
{
    public EvidencedValue<string> CanonicalRoundId { get; } = V2ContractGuard.NotNull(CanonicalRoundId, nameof(CanonicalRoundId));

    public EvidencedValue<string> DisplayName { get; } = V2ContractGuard.NotNull(DisplayName, nameof(DisplayName));

    public EvidencedValue<string> Caliber { get; } = V2ContractGuard.NotNull(Caliber, nameof(Caliber));

    public EvidencedValue<int?> Quantity { get; } = V2ContractGuard.NotNull(Quantity, nameof(Quantity));
}

public sealed record KeyRecognition(
    RecognizedItem Item,
    EvidencedValue<int?> UsesRemaining,
    EvidencedValue<string> Opens)
{
    public RecognizedItem Item { get; } = V2ContractGuard.NotNull(Item, nameof(Item));

    public EvidencedValue<int?> UsesRemaining { get; } = V2ContractGuard.NotNull(UsesRemaining, nameof(UsesRemaining));

    public EvidencedValue<string> Opens { get; } = V2ContractGuard.NotNull(Opens, nameof(Opens));
}

public sealed record QuestItemRecognition(
    RecognizedItem Item,
    EvidencedValue<string> QuestId,
    EvidencedValue<int?> OutstandingQuantity,
    EvidencedValue<bool?> RequiredFoundInRaid)
{
    public RecognizedItem Item { get; } = V2ContractGuard.NotNull(Item, nameof(Item));

    public EvidencedValue<string> QuestId { get; } = V2ContractGuard.NotNull(QuestId, nameof(QuestId));

    public EvidencedValue<int?> OutstandingQuantity { get; } = V2ContractGuard.NotNull(OutstandingQuantity, nameof(OutstandingQuantity));

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

    public EvidencedValue<long?> PriceRoubles { get; } = V2ContractGuard.NotNull(PriceRoubles, nameof(PriceRoubles));

    public EvidencedValue<int?> Quantity { get; } = V2ContractGuard.NotNull(Quantity, nameof(Quantity));

    public EvidencedValue<long?> PricePerUnitRoubles { get; } = V2ContractGuard.NotNull(PricePerUnitRoubles, nameof(PricePerUnitRoubles));
}

/// <summary>The rows the user visibly opened, with every raw OCR line kept for review.</summary>
public sealed record FleaPageRecognition(
    IReadOnlyList<FleaListingRecognition> Listings,
    IReadOnlyList<RawOcrLine> RawOcrLines)
{
    public IReadOnlyList<FleaListingRecognition> Listings { get; } = V2ContractGuard.List(Listings, nameof(Listings));

    public IReadOnlyList<RawOcrLine> RawOcrLines { get; } = V2ContractGuard.List(RawOcrLines, nameof(RawOcrLines));
}

public enum ExtractAvailability
{
    Unknown,
    Available,
    Conditional,
    Closed,
}

public sealed record ExtractRecognition(
    EvidencedValue<string> CanonicalId,
    EvidencedValue<string> DisplayName,
    EvidencedValue<ExtractAvailability?> Availability)
{
    public EvidencedValue<string> CanonicalId { get; } = V2ContractGuard.NotNull(CanonicalId, nameof(CanonicalId));

    public EvidencedValue<string> DisplayName { get; } = V2ContractGuard.NotNull(DisplayName, nameof(DisplayName));

    public EvidencedValue<ExtractAvailability?> Availability { get; } = V2ContractGuard.NotNull(Availability, nameof(Availability));
}

public enum RaidClockBasis
{
    Unknown,
    CountedFromRaidStart,
    ObservedOnExtractScreen,
}

/// <summary>
/// The clock as it read at <see cref="AsOfUtc"/>. For an observed reading that instant is when the
/// screenshot was taken, not when this application acquired the file: #261 ages the clock from
/// capture time, and an import minutes later must not add those minutes to the raid.
/// </summary>
public sealed record RaidClockReading
{
    public RaidClockReading(TimeSpan? remaining, RaidClockBasis basis, DateTimeOffset? asOfUtc)
    {
        if (basis == RaidClockBasis.Unknown)
        {
            if (remaining is not null || asOfUtc is not null)
            {
                throw new ArgumentException("An unknown raid clock carries no reading.", nameof(basis));
            }
        }
        else
        {
            if (remaining is not { } value || value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(remaining), "A raid clock reading must be zero or more.");
            }

            if (asOfUtc is not { } asOf || asOf == default)
            {
                throw new ArgumentException("A raid clock reading must say when it was true.", nameof(asOfUtc));
            }
        }

        Remaining = remaining;
        Basis = basis;
        AsOfUtc = asOfUtc?.ToUniversalTime();
    }

    public TimeSpan? Remaining { get; }

    public RaidClockBasis Basis { get; }

    public DateTimeOffset? AsOfUtc { get; }
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
    EvidencedValue<RaidClockReading> RaidTimeRemaining)
{
    public EvidencedValue<string> MapId { get; } = V2ContractGuard.NotNull(MapId, nameof(MapId));

    public IReadOnlyList<ExtractRecognition> Extracts { get; } = V2ContractGuard.List(Extracts, nameof(Extracts));

    public IReadOnlyList<RawOcrLine> RawOcrLines { get; } = V2ContractGuard.List(RawOcrLines, nameof(RawOcrLines));

    public EvidencedValue<RaidClockReading> RaidTimeRemaining { get; } = V2ContractGuard.NotNull(RaidTimeRemaining, nameof(RaidTimeRemaining));
}

public enum CharacterRegion
{
    Unknown,
    Head,
    Thorax,
    Stomach,
    LeftArm,
    RightArm,
    LeftLeg,
    RightLeg,
}

public enum CharacterRegionState
{
    Unknown,
    Healthy,
    Injured,
    Critical,
    Destroyed,
}

public sealed record CharacterRegionReading(
    CharacterRegion Region,
    CharacterRegionState State,
    double? VisibleFraction)
{
    public double? VisibleFraction { get; } = VisibleFraction is null or (>= 0 and <= 1)
        ? VisibleFraction
        : throw new ArgumentOutOfRangeException(nameof(VisibleFraction));
}

public sealed record HealthCharacterRecognition(
    EvidencedValue<bool?> CharacterDisplayPresent,
    IReadOnlyList<EvidencedValue<CharacterRegionReading>> Regions,
    IReadOnlyList<EvidencedValue<string>> Conditions)
{
    public EvidencedValue<bool?> CharacterDisplayPresent { get; } = V2ContractGuard.NotNull(CharacterDisplayPresent, nameof(CharacterDisplayPresent));

    public IReadOnlyList<EvidencedValue<CharacterRegionReading>> Regions { get; } = V2ContractGuard.List(Regions, nameof(Regions));

    public IReadOnlyList<EvidencedValue<string>> Conditions { get; } = V2ContractGuard.List(Conditions, nameof(Conditions));
}

public sealed record ItemRecognitionResult
    : ContextualRecognitionResult<RecognizedItem>
{
    public ItemRecognitionResult(RecognitionResultEnvelope<RecognizedItem> recognition)
        : base(recognition, RecognizedContext.Item)
    {
    }
}

public sealed record GridRecognitionResult
    : ContextualRecognitionResult<GridRecognition>
{
    public GridRecognitionResult(RecognitionResultEnvelope<GridRecognition> recognition)
        : base(recognition, RecognizedContext.Grid)
    {
    }
}

public sealed record LootRecognitionResult
    : ContextualRecognitionResult<LootRecognition>
{
    public LootRecognitionResult(RecognitionResultEnvelope<LootRecognition> recognition)
        : base(recognition, RecognizedContext.Loot)
    {
    }
}

public sealed record StashRecognitionResult
    : ContextualRecognitionResult<StashRecognition>
{
    public StashRecognitionResult(RecognitionResultEnvelope<StashRecognition> recognition)
        : base(recognition, RecognizedContext.Stash)
    {
    }
}

public sealed record AmmoRecognitionResult
    : ContextualRecognitionResult<AmmoRecognition>
{
    public AmmoRecognitionResult(RecognitionResultEnvelope<AmmoRecognition> recognition)
        : base(recognition, RecognizedContext.Ammo)
    {
    }
}

public sealed record KeyRecognitionResult
    : ContextualRecognitionResult<KeyRecognition>
{
    public KeyRecognitionResult(RecognitionResultEnvelope<KeyRecognition> recognition)
        : base(recognition, RecognizedContext.Keys)
    {
    }
}

public sealed record QuestItemRecognitionResult
    : ContextualRecognitionResult<QuestItemRecognition>
{
    public QuestItemRecognitionResult(RecognitionResultEnvelope<QuestItemRecognition> recognition)
        : base(recognition, RecognizedContext.QuestItems)
    {
    }
}

public sealed record FleaRecognitionResult
    : ContextualRecognitionResult<FleaPageRecognition>
{
    public FleaRecognitionResult(RecognitionResultEnvelope<FleaPageRecognition> recognition)
        : base(recognition, RecognizedContext.Flea)
    {
    }
}

public sealed record ExtractMapRecognitionResult
    : ContextualRecognitionResult<ExtractMapRecognition>
{
    public ExtractMapRecognitionResult(RecognitionResultEnvelope<ExtractMapRecognition> recognition)
        : base(recognition, RecognizedContext.ExtractsAndMap)
    {
    }
}

public sealed record HealthCharacterRecognitionResult
    : ContextualRecognitionResult<HealthCharacterRecognition>
{
    public HealthCharacterRecognitionResult(RecognitionResultEnvelope<HealthCharacterRecognition> recognition)
        : base(recognition, RecognizedContext.HealthAndCharacter)
    {
    }
}

public abstract record ContextualRecognitionResult<T>
{
    protected ContextualRecognitionResult(
        RecognitionResultEnvelope<T> recognition,
        RecognizedContext requiredContext)
    {
        ArgumentNullException.ThrowIfNull(recognition);
        if (recognition.Header.RecognizedContext != requiredContext)
        {
            throw new ArgumentException(
                $"Recognition context must be {requiredContext}.",
                nameof(recognition));
        }

        Recognition = recognition;
    }

    public RecognitionResultEnvelope<T> Recognition { get; }
}
