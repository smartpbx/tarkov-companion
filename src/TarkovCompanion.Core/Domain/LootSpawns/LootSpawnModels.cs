using System.Collections.ObjectModel;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Core.Domain.LootSpawns;

public enum LootSpawnPrecision
{
    ExactPoint = 1,
    BoundedArea,
    RoomOrRegion,
    MapOnly,
}

public enum LootSpawnPoolKind
{
    SingleKnownItem = 1,
    UnweightedCandidates,
}

public enum LootSpawnValueBasis
{
    FleaGross = 1,
    FleaNet,
    BestTrader,
    BestNet,
    ValuePerSquare,
    ProfileUtility,
}

public enum LootSpawnValueTier
{
    Unknown = 0,
    BelowThreshold,
    ProfileRelevant,
    Moderate,
    High,
    Exceptional,
}

/// <summary>Why a possible item matters to the active profile, independent of market value.</summary>
public enum LootSpawnProfileNeedKind
{
    CurrentQuest = 1,
    FutureQuest,
    Hideout,
    CraftOrBarter,
    Ammo,
    Key,
    Loadout,
    Event,
    Wishlist,
    ProtectedItem,
    UserPin,
}

public sealed record LootSpawnProfileNeed
{
    public LootSpawnProfileNeed(
        LootSpawnProfileNeedKind kind,
        string code,
        string explanation,
        ResultStatus status,
        EvidenceProvenance provenance)
    {
        Kind = Defined(kind, nameof(kind));
        Code = Required(code, nameof(code), 128);
        Explanation = Required(explanation, nameof(explanation), 512);
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public LootSpawnProfileNeedKind Kind { get; }

    public string Code { get; }

    public string Explanation { get; }

    public ResultStatus Status { get; }

    public EvidenceProvenance Provenance { get; }

    internal static T Defined<T>(T value, string parameterName)
        where T : struct, Enum => Enum.IsDefined(value)
        ? value
        : throw new ArgumentOutOfRangeException(parameterName);

    internal static string Required(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        return normalized.Length <= maximumLength
            ? normalized
            : throw new ArgumentOutOfRangeException(parameterName);
    }
}

/// <summary>One candidate in a single-item or explicitly unweighted spawn pool.</summary>
public sealed record LootSpawnCandidate
{
    public const int MaximumProfileNeeds = 32;

    public LootSpawnCandidate(
        string itemId,
        string displayName,
        string category,
        EvidencedValue<long?> fleaGrossRoubles,
        EvidencedValue<long?> fleaNetRoubles,
        EvidencedValue<long?> bestTraderRoubles,
        EvidencedValue<int?> occupiedSquares,
        IReadOnlyList<LootSpawnProfileNeed>? profileNeeds = null)
    {
        ItemId = LootSpawnProfileNeed.Required(itemId, nameof(itemId), 256);
        DisplayName = LootSpawnProfileNeed.Required(displayName, nameof(displayName), 256);
        Category = LootSpawnProfileNeed.Required(category, nameof(category), 128);
        FleaGrossRoubles = fleaGrossRoubles ?? throw new ArgumentNullException(nameof(fleaGrossRoubles));
        FleaNetRoubles = fleaNetRoubles ?? throw new ArgumentNullException(nameof(fleaNetRoubles));
        BestTraderRoubles = bestTraderRoubles ?? throw new ArgumentNullException(nameof(bestTraderRoubles));
        OccupiedSquares = occupiedSquares ?? throw new ArgumentNullException(nameof(occupiedSquares));
        ValidateNonNegative(fleaGrossRoubles, nameof(fleaGrossRoubles));
        ValidateNonNegative(fleaNetRoubles, nameof(fleaNetRoubles));
        ValidateNonNegative(bestTraderRoubles, nameof(bestTraderRoubles));
        ValidatePositive(occupiedSquares, nameof(occupiedSquares));

        var needs = (profileNeeds ?? [])
            .Select(need => need ?? throw new ArgumentException("Profile needs cannot contain null.", nameof(profileNeeds)))
            .ToArray();
        if (needs.Length > MaximumProfileNeeds)
        {
            throw new ArgumentException($"An item cannot carry more than {MaximumProfileNeeds} profile needs.", nameof(profileNeeds));
        }

        if (needs.Select(need => need.Code).Distinct(StringComparer.Ordinal).Count() != needs.Length)
        {
            throw new ArgumentException("Profile-need codes must be unique per candidate.", nameof(profileNeeds));
        }

        ProfileNeeds = Array.AsReadOnly(needs);
    }

    public string ItemId { get; }

    public string DisplayName { get; }

    public string Category { get; }

    public EvidencedValue<long?> FleaGrossRoubles { get; }

    public EvidencedValue<long?> FleaNetRoubles { get; }

    public EvidencedValue<long?> BestTraderRoubles { get; }

    public EvidencedValue<int?> OccupiedSquares { get; }

    public IReadOnlyList<LootSpawnProfileNeed> ProfileNeeds { get; }

    private static void ValidateNonNegative(EvidencedValue<long?> field, string parameterName)
    {
        var values = new[] { field.Value }
            .Concat(field.Candidates.Select(candidate => candidate.Value))
            .Concat(field.Corrections.SelectMany(correction =>
                new[] { correction.OriginalValue, correction.CorrectedValue }));
        if (values.Any(value => value < 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Money cannot be negative.");
        }
    }

    private static void ValidatePositive(EvidencedValue<int?> field, string parameterName)
    {
        var values = new[] { field.Value }
            .Concat(field.Candidates.Select(candidate => candidate.Value))
            .Concat(field.Corrections.SelectMany(correction =>
                new[] { correction.OriginalValue, correction.CorrectedValue }));
        if (values.Any(value => value is <= 0))
        {
            throw new ArgumentOutOfRangeException(parameterName, "Occupied squares must be positive when known.");
        }
    }
}

/// <summary>A source-honest location. Map-only knowledge deliberately has no geometry.</summary>
public sealed record LootSpawnLocation
{
    public const int MaximumFloors = 32;

    public LootSpawnLocation(
        LootSpawnPrecision precision,
        MapSceneGeometry? geometry,
        IReadOnlyList<string>? floorIds = null)
    {
        Precision = LootSpawnProfileNeed.Defined(precision, nameof(precision));
        var expectedGeometry = precision switch
        {
            LootSpawnPrecision.ExactPoint => MapSceneGeometryKind.Point,
            LootSpawnPrecision.BoundedArea => MapSceneGeometryKind.Area,
            LootSpawnPrecision.RoomOrRegion => MapSceneGeometryKind.Region,
            LootSpawnPrecision.MapOnly => (MapSceneGeometryKind?)null,
            _ => throw new ArgumentOutOfRangeException(nameof(precision)),
        };
        var expectsNoGeometry = expectedGeometry is null;
        if (expectsNoGeometry != (geometry is null) ||
            expectedGeometry is { } expected && geometry?.Kind != expected)
        {
            throw new ArgumentException("Location precision and geometry must agree without inventing a point.", nameof(geometry));
        }

        var floors = (floorIds ?? [])
            .Select(floor => LootSpawnProfileNeed.Required(floor, nameof(floorIds), 96))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (floors.Length > MaximumFloors)
        {
            throw new ArgumentException($"A spawn cannot name more than {MaximumFloors} floors.", nameof(floorIds));
        }

        Geometry = geometry;
        FloorIds = Array.AsReadOnly(floors);
    }

    public LootSpawnPrecision Precision { get; }

    public MapSceneGeometry? Geometry { get; }

    /// <summary>An empty list means the source did not resolve a floor.</summary>
    public IReadOnlyList<string> FloorIds { get; }
}

public sealed record LootSpawnRecord
{
    public const int MaximumCandidates = 256;

    public LootSpawnRecord(
        string spawnId,
        string mapId,
        string label,
        LootSpawnLocation location,
        LootSpawnPoolKind poolKind,
        IReadOnlyList<LootSpawnCandidate> candidates,
        EvidencedValue<double?> spawnProbability,
        EvidencedValue<string?> respawnBehavior,
        string datasetVersion,
        string transformVersion,
        ResultStatus status,
        EvidenceProvenance provenance,
        string? accessNote = null)
    {
        SpawnId = LootSpawnProfileNeed.Required(spawnId, nameof(spawnId), 160);
        MapId = LootSpawnProfileNeed.Required(mapId, nameof(mapId), 128);
        Label = LootSpawnProfileNeed.Required(label, nameof(label), 256);
        Location = location ?? throw new ArgumentNullException(nameof(location));
        PoolKind = LootSpawnProfileNeed.Defined(poolKind, nameof(poolKind));
        SpawnProbability = spawnProbability ?? throw new ArgumentNullException(nameof(spawnProbability));
        RespawnBehavior = respawnBehavior ?? throw new ArgumentNullException(nameof(respawnBehavior));
        DatasetVersion = LootSpawnProfileNeed.Required(datasetVersion, nameof(datasetVersion), 128);
        TransformVersion = LootSpawnProfileNeed.Required(transformVersion, nameof(transformVersion), 128);
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
        AccessNote = string.IsNullOrWhiteSpace(accessNote)
            ? null
            : LootSpawnProfileNeed.Required(accessNote, nameof(accessNote), 1024);

        var copied = (candidates ?? throw new ArgumentNullException(nameof(candidates)))
            .Select(candidate => candidate ?? throw new ArgumentException("Candidate pools cannot contain null.", nameof(candidates)))
            .ToArray();
        if (copied.Length is < 1 or > MaximumCandidates)
        {
            throw new ArgumentException($"A spawn pool must contain between 1 and {MaximumCandidates} candidates.", nameof(candidates));
        }

        if (copied.Select(candidate => candidate.ItemId).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("Candidate item IDs must be unique within a spawn pool.", nameof(candidates));
        }

        if (poolKind == LootSpawnPoolKind.SingleKnownItem != (copied.Length == 1))
        {
            throw new ArgumentException("A single-known-item pool has exactly one candidate; larger pools must be unweighted.", nameof(candidates));
        }

        ValidateProbability(spawnProbability);
        Candidates = Array.AsReadOnly(copied);
    }

    public string SpawnId { get; }

    public string MapId { get; }

    public string Label { get; }

    public LootSpawnLocation Location { get; }

    public LootSpawnPoolKind PoolKind { get; }

    public IReadOnlyList<LootSpawnCandidate> Candidates { get; }

    public EvidencedValue<double?> SpawnProbability { get; }

    public EvidencedValue<string?> RespawnBehavior { get; }

    public string DatasetVersion { get; }

    public string TransformVersion { get; }

    public ResultStatus Status { get; }

    public EvidenceProvenance Provenance { get; }

    public string? AccessNote { get; }

    private static void ValidateProbability(EvidencedValue<double?> field)
    {
        var values = new[] { field.Value }
            .Concat(field.Candidates.Select(candidate => candidate.Value))
            .Concat(field.Corrections.SelectMany(correction =>
                new[] { correction.OriginalValue, correction.CorrectedValue }));
        if (values.Any(value => value is { } present && (!double.IsFinite(present) || present is < 0 or > 1)))
        {
            throw new ArgumentOutOfRangeException(nameof(field), "Spawn probability must be between zero and one when known.");
        }
    }
}

public sealed record LootSpawnCoverage
{
    public LootSpawnCoverage(int published, int positioned, int floorResolved, int unresolved)
    {
        if (published < 0 || positioned < 0 || floorResolved < 0 || unresolved < 0 ||
            positioned > published || floorResolved > positioned || unresolved > published)
        {
            throw new ArgumentOutOfRangeException(nameof(published), "Loot-spawn coverage counts do not reconcile.");
        }

        Published = published;
        Positioned = positioned;
        FloorResolved = floorResolved;
        Unresolved = unresolved;
    }

    public int Published { get; }

    public int Positioned { get; }

    public int FloorResolved { get; }

    public int Unresolved { get; }
}

public sealed record LootSpawnSnapshot
{
    public const int MaximumRecords = 4096;

    public LootSpawnSnapshot(
        string snapshotId,
        string datasetVersion,
        string mapId,
        string transformVersion,
        DateTimeOffset generatedUtc,
        ResultStatus status,
        LootSpawnCoverage coverage,
        EvidenceProvenance provenance,
        IReadOnlyList<LootSpawnRecord> records)
    {
        SnapshotId = LootSpawnProfileNeed.Required(snapshotId, nameof(snapshotId), 160);
        DatasetVersion = LootSpawnProfileNeed.Required(datasetVersion, nameof(datasetVersion), 128);
        MapId = LootSpawnProfileNeed.Required(mapId, nameof(mapId), 128);
        TransformVersion = LootSpawnProfileNeed.Required(transformVersion, nameof(transformVersion), 128);
        if (generatedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Snapshot generation time must be UTC.", nameof(generatedUtc));
        }

        GeneratedUtc = generatedUtc;
        Status = status ?? throw new ArgumentNullException(nameof(status));
        Coverage = coverage ?? throw new ArgumentNullException(nameof(coverage));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));

        var copied = (records ?? throw new ArgumentNullException(nameof(records)))
            .Select(record => record ?? throw new ArgumentException("Snapshots cannot contain null records.", nameof(records)))
            .ToArray();
        if (copied.Length > MaximumRecords)
        {
            throw new ArgumentException($"A map snapshot cannot contain more than {MaximumRecords} records.", nameof(records));
        }

        if (copied.Select(record => record.SpawnId).Distinct(StringComparer.Ordinal).Count() != copied.Length)
        {
            throw new ArgumentException("Spawn IDs must be unique within a snapshot.", nameof(records));
        }

        if (copied.Any(record => !string.Equals(record.MapId, MapId, StringComparison.Ordinal) ||
                                 !string.Equals(record.DatasetVersion, DatasetVersion, StringComparison.Ordinal) ||
                                 !string.Equals(record.TransformVersion, TransformVersion, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Every record must match its snapshot map, dataset, and transform.", nameof(records));
        }

        if (coverage.Published != copied.Length)
        {
            throw new ArgumentException("Published coverage must equal the bounded snapshot record count.", nameof(coverage));
        }

        var positioned = copied.Count(record => record.Location.Geometry is not null);
        var floorResolved = copied.Count(record =>
            record.Location.Geometry is not null && record.Location.FloorIds.Count > 0);
        if (coverage.Positioned != positioned || coverage.FloorResolved != floorResolved ||
            coverage.Unresolved != copied.Length - positioned)
        {
            throw new ArgumentException(
                "Coverage counts must be measured from the published snapshot records.",
                nameof(coverage));
        }

        Records = Array.AsReadOnly(copied);
    }

    public string SnapshotId { get; }

    public string DatasetVersion { get; }

    public string MapId { get; }

    public string TransformVersion { get; }

    public DateTimeOffset GeneratedUtc { get; }

    public ResultStatus Status { get; }

    public LootSpawnCoverage Coverage { get; }

    public EvidenceProvenance Provenance { get; }

    public IReadOnlyList<LootSpawnRecord> Records { get; }
}

public sealed record LootSpawnValueThresholds
{
    public LootSpawnValueThresholds(long minimum, long moderate, long high, long exceptional)
    {
        if (minimum < 0 || moderate < minimum || high <= moderate || exceptional <= high)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "Value thresholds must be ordered and non-negative.");
        }

        Minimum = minimum;
        Moderate = moderate;
        High = high;
        Exceptional = exceptional;
    }

    public long Minimum { get; }

    public long Moderate { get; }

    public long High { get; }

    public long Exceptional { get; }

    public LootSpawnValueTier Classify(long value) => value switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(value)),
        var amount when amount >= Exceptional => LootSpawnValueTier.Exceptional,
        var amount when amount >= High => LootSpawnValueTier.High,
        var amount when amount >= Moderate => LootSpawnValueTier.Moderate,
        _ => LootSpawnValueTier.BelowThreshold,
    };

    public static LootSpawnValueThresholds Default { get; } = new(50_000, 75_000, 150_000, 500_000);
}

public sealed record HighValueLootFilter
{
    public const int MaximumFilterValues = 256;

    public HighValueLootFilter(
        LootSpawnValueBasis valueBasis,
        LootSpawnValueThresholds thresholds,
        TimeSpan maximumPriceAge,
        TimeSpan maximumSourceAge,
        double minimumConfidence,
        bool includeProfileRelevant = true,
        string? floorId = null,
        IReadOnlyList<string>? itemIds = null,
        IReadOnlyList<string>? categories = null)
    {
        ValueBasis = LootSpawnProfileNeed.Defined(valueBasis, nameof(valueBasis));
        Thresholds = thresholds ?? throw new ArgumentNullException(nameof(thresholds));
        if (maximumPriceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumPriceAge));
        }

        if (maximumSourceAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSourceAge));
        }

        if (!double.IsFinite(minimumConfidence) || minimumConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumConfidence));
        }

        MaximumPriceAge = maximumPriceAge;
        MaximumSourceAge = maximumSourceAge;
        MinimumConfidence = minimumConfidence;
        IncludeProfileRelevant = includeProfileRelevant;
        FloorId = string.IsNullOrWhiteSpace(floorId)
            ? null
            : LootSpawnProfileNeed.Required(floorId, nameof(floorId), 96);
        ItemIds = CopyFilter(itemIds, nameof(itemIds));
        Categories = CopyFilter(categories, nameof(categories));
    }

    public LootSpawnValueBasis ValueBasis { get; }

    public LootSpawnValueThresholds Thresholds { get; }

    public TimeSpan MaximumPriceAge { get; }

    public TimeSpan MaximumSourceAge { get; }

    public double MinimumConfidence { get; }

    public bool IncludeProfileRelevant { get; }

    public string? FloorId { get; }

    public IReadOnlyList<string> ItemIds { get; }

    public IReadOnlyList<string> Categories { get; }

    public static HighValueLootFilter Default { get; } = new(
        LootSpawnValueBasis.BestNet,
        LootSpawnValueThresholds.Default,
        TimeSpan.FromMinutes(30),
        TimeSpan.FromDays(90),
        0.50);

    private static ReadOnlyCollection<string> CopyFilter(IReadOnlyList<string>? values, string parameterName)
    {
        var copied = (values ?? [])
            .Select(value => LootSpawnProfileNeed.Required(value, parameterName, 256))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (copied.Length > MaximumFilterValues)
        {
            throw new ArgumentException($"A filter cannot contain more than {MaximumFilterValues} values.", parameterName);
        }

        return Array.AsReadOnly(copied);
    }
}
