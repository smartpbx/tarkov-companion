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
        ResultId = Required(resultId, nameof(resultId));
        ContractVersion = contractVersion;
        SessionId = sessionId;
        ArtifactId = Required(artifactId, nameof(artifactId));
        RequestedIntent = requestedIntent;
        RecognizedContext = recognizedContext;
    }

    public string ResultId { get; }

    public V2ContractVersion ContractVersion { get; }

    public CaptureSessionId SessionId { get; }

    public string ArtifactId { get; }

    public ScanIntent RequestedIntent { get; }

    public RecognizedContext RecognizedContext { get; }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
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

public sealed record RecognizedItem(
    EvidencedValue<string> CanonicalId,
    EvidencedValue<string> DisplayName,
    EvidencedValue<int?> Quantity,
    EvidencedValue<int?> OccupiedSlots,
    EvidencedValue<bool?> FoundInRaid);

public readonly record struct GridCellAddress
{
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
    EvidencedValue<int?> CellHeightPixels);

public sealed record GridCellRecognition(
    GridCellAddress Address,
    EvidencedValue<RecognizedItem> Item);

public sealed record GridRecognition(
    GridGeometry Geometry,
    IReadOnlyList<GridCellRecognition> Cells);

public sealed record LootRecognition(
    GridRecognition VisibleLoot,
    GridRecognition CarriedInventory);

public sealed record StashRecognition(
    string SnapshotId,
    IReadOnlyList<GridRecognition> CapturedRegions,
    EvidencedValue<long?> TotalKnownValueRoubles,
    EvidencedValue<int?> UnresolvedCells);

public sealed record AmmoRecognition(
    EvidencedValue<string> CanonicalRoundId,
    EvidencedValue<string> DisplayName,
    EvidencedValue<string> Caliber,
    EvidencedValue<int?> Quantity);

public sealed record KeyRecognition(
    RecognizedItem Item,
    EvidencedValue<int?> UsesRemaining,
    EvidencedValue<string> Opens);

public sealed record QuestItemRecognition(
    RecognizedItem Item,
    EvidencedValue<string> QuestId,
    EvidencedValue<int?> OutstandingQuantity,
    EvidencedValue<bool?> RequiredFoundInRaid);

public sealed record FleaListingRecognition(
    EvidencedValue<RecognizedItem> Item,
    EvidencedValue<long?> PriceRoubles,
    EvidencedValue<int?> Quantity,
    EvidencedValue<long?> PricePerUnitRoubles);

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
    EvidencedValue<ExtractAvailability> Availability);

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
public sealed record RawOcrLine(EvidencedValue<string> Text);

public sealed record ExtractMapRecognition(
    EvidencedValue<string> MapId,
    IReadOnlyList<ExtractRecognition> Extracts,
    IReadOnlyList<RawOcrLine> RawOcrLines,
    EvidencedValue<RaidClockReading> RaidTimeRemaining);

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
    IReadOnlyList<EvidencedValue<string>> Conditions);

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
    : ContextualRecognitionResult<IReadOnlyList<FleaListingRecognition>>
{
    public FleaRecognitionResult(RecognitionResultEnvelope<IReadOnlyList<FleaListingRecognition>> recognition)
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
