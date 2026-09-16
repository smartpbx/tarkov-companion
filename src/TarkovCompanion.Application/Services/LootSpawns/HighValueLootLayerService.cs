using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Application.Services.LootSpawns;

public enum HighValueLootDiagnosticKind
{
    SnapshotUnavailable = 1,
    SnapshotMismatch,
    RecordUnavailable,
    InvalidGeometry,
    SourceTooOld,
    ConfidenceBelowFilter,
    FloorUnknown,
    FilteredOut,
    ValueUnavailable,
}

public sealed record HighValueLootDiagnostic
{
    public HighValueLootDiagnostic(
        HighValueLootDiagnosticKind kind,
        string code,
        string explanation,
        string? spawnId = null)
    {
        Kind = HighValueLootGuard.Defined(kind, nameof(kind));
        Code = HighValueLootGuard.Required(code, nameof(code), 128);
        Explanation = HighValueLootGuard.Required(explanation, nameof(explanation), 1024);
        SpawnId = string.IsNullOrWhiteSpace(spawnId)
            ? null
            : HighValueLootGuard.Required(spawnId, nameof(spawnId), 160);
    }

    public HighValueLootDiagnosticKind Kind { get; }

    public string Code { get; }

    public string Explanation { get; }

    public string? SpawnId { get; }

    public bool AffectsCompleteness => Kind is
        HighValueLootDiagnosticKind.SnapshotUnavailable or
        HighValueLootDiagnosticKind.SnapshotMismatch or
        HighValueLootDiagnosticKind.RecordUnavailable or
        HighValueLootDiagnosticKind.InvalidGeometry or
        HighValueLootDiagnosticKind.ValueUnavailable;
}

public sealed record HighValueLootLayerRequest
{
    public HighValueLootLayerRequest(
        string mapId,
        string transformVersion,
        MapSceneBounds mapBounds,
        DateTimeOffset evaluatedUtc,
        HighValueLootFilter filter,
        LootSpawnSnapshot? snapshot)
    {
        MapId = HighValueLootGuard.Required(mapId, nameof(mapId), 128);
        TransformVersion = HighValueLootGuard.Required(transformVersion, nameof(transformVersion), 128);
        if (evaluatedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Layer evaluation time must be UTC.", nameof(evaluatedUtc));
        }

        MapBounds = mapBounds;
        EvaluatedUtc = evaluatedUtc;
        Filter = filter ?? throw new ArgumentNullException(nameof(filter));
        Snapshot = snapshot;
    }

    public string MapId { get; }

    public string TransformVersion { get; }

    public MapSceneBounds MapBounds { get; }

    public DateTimeOffset EvaluatedUtc { get; }

    public HighValueLootFilter Filter { get; }

    public LootSpawnSnapshot? Snapshot { get; }
}

/// <summary>A list/table row backed by the same projection as the scene object.</summary>
public sealed record HighValueLootEntry
{
    public HighValueLootEntry(
        LootSpawnRecord spawn,
        LootSpawnValueTier tier,
        long? minimumValue,
        long? maximumValue,
        long? minimumValuePerSquare,
        long? maximumValuePerSquare,
        int valuedCandidateCount,
        int highValueCandidateCount,
        string valueBasis,
        string summary,
        IReadOnlyList<LootSpawnProfileNeed> profileNeeds,
        IReadOnlyList<string> missingFacts,
        MapSceneObjectId? sceneObjectId)
    {
        Spawn = spawn ?? throw new ArgumentNullException(nameof(spawn));
        Tier = HighValueLootGuard.Defined(tier, nameof(tier));
        if (minimumValue < 0 || maximumValue < 0 || minimumValuePerSquare < 0 || maximumValuePerSquare < 0 ||
            minimumValue > maximumValue || minimumValuePerSquare > maximumValuePerSquare ||
            valuedCandidateCount < 0 || valuedCandidateCount > spawn.Candidates.Count ||
            highValueCandidateCount < 0 || highValueCandidateCount > valuedCandidateCount)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumValue), "Projected value ranges or counts are invalid.");
        }

        MinimumValue = minimumValue;
        MaximumValue = maximumValue;
        MinimumValuePerSquare = minimumValuePerSquare;
        MaximumValuePerSquare = maximumValuePerSquare;
        ValuedCandidateCount = valuedCandidateCount;
        HighValueCandidateCount = highValueCandidateCount;
        ValueBasis = HighValueLootGuard.Required(valueBasis, nameof(valueBasis), 64);
        Summary = HighValueLootGuard.Required(summary, nameof(summary), 1024);
        ProfileNeeds = Copy(profileNeeds, 512, nameof(profileNeeds));
        MissingFacts = CopyStrings(missingFacts, nameof(missingFacts));
        SceneObjectId = sceneObjectId;
    }

    public LootSpawnRecord Spawn { get; }

    public LootSpawnValueTier Tier { get; }

    public long? MinimumValue { get; }

    /// <summary>A ceiling for an unweighted pool, never an expected or likely value.</summary>
    public long? MaximumValue { get; }

    public long? MinimumValuePerSquare { get; }

    public long? MaximumValuePerSquare { get; }

    public int ValuedCandidateCount { get; }

    public int HighValueCandidateCount { get; }

    /// <summary>Expected value stays absent because this slice never invents candidate weights.</summary>
    public long? ExpectedValueRoubles => null;

    public string ValueBasis { get; }

    public string Summary { get; }

    public IReadOnlyList<LootSpawnProfileNeed> ProfileNeeds { get; }

    public IReadOnlyList<string> MissingFacts { get; }

    public MapSceneObjectId? SceneObjectId { get; }

    private static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> values, int maximum, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum)
        {
            throw new ArgumentException($"A projection cannot contain more than {maximum} entries.", parameterName);
        }

        return Array.AsReadOnly(values
            .Select(value => value ?? throw new ArgumentException("Projection lists cannot contain null.", parameterName))
            .ToArray());
    }

    private static ReadOnlyCollection<string> CopyStrings(IReadOnlyList<string> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > 64)
        {
            throw new ArgumentException("A projection cannot contain more than 64 missing-fact entries.", parameterName);
        }

        return Array.AsReadOnly(values
            .Select(value => HighValueLootGuard.Required(value, parameterName, 256))
            .Distinct(StringComparer.Ordinal)
            .ToArray());
    }
}

public sealed record HighValueLootLayerResult
{
    public HighValueLootLayerResult(
        MapSceneLayer layer,
        ResultStatus status,
        string compactLegend,
        DateTimeOffset? dataThroughUtc,
        LootSpawnCoverage? coverage,
        IReadOnlyList<MapSceneObject> objects,
        IReadOnlyList<HighValueLootEntry> entries,
        IReadOnlyList<HighValueLootDiagnostic> diagnostics)
    {
        Layer = layer ?? throw new ArgumentNullException(nameof(layer));
        Status = status ?? throw new ArgumentNullException(nameof(status));
        CompactLegend = HighValueLootGuard.Required(compactLegend, nameof(compactLegend), 256);
        if (dataThroughUtc?.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Data-through time must be UTC.", nameof(dataThroughUtc));
        }

        DataThroughUtc = dataThroughUtc;
        Coverage = coverage;
        Objects = Copy(objects, LootSpawnSnapshot.MaximumRecords, nameof(objects));
        Entries = Copy(entries, LootSpawnSnapshot.MaximumRecords, nameof(entries));
        Diagnostics = Copy(diagnostics, LootSpawnSnapshot.MaximumRecords * 2, nameof(diagnostics));
        if (objects.Select(item => item.Id).Distinct().Count() != objects.Count)
        {
            throw new ArgumentException("Layer scene-object IDs must be unique.", nameof(objects));
        }
    }

    public MapSceneLayer Layer { get; }

    public ResultStatus Status { get; }

    public string CompactLegend { get; }

    public DateTimeOffset? DataThroughUtc { get; }

    public LootSpawnCoverage? Coverage { get; }

    public IReadOnlyList<MapSceneObject> Objects { get; }

    public IReadOnlyList<HighValueLootEntry> Entries { get; }

    public IReadOnlyList<HighValueLootDiagnostic> Diagnostics { get; }

    private static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T> values, int maximum, string parameterName)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > maximum)
        {
            throw new ArgumentException($"A layer result cannot contain more than {maximum} {parameterName}.", parameterName);
        }

        return Array.AsReadOnly(values
            .Select(value => value ?? throw new ArgumentException("Layer result lists cannot contain null.", parameterName))
            .ToArray());
    }
}

/// <summary>
/// Projects versioned potential-spawn knowledge into the scene shared by desktop and tablet.
/// It never claims that a candidate exists in the current raid and never computes expected value
/// for an unweighted pool.
/// </summary>
public sealed class HighValueLootLayerService
{
    public static readonly MapSceneLayerId LayerId = new("high-value-loot-spawns");

    public static readonly MapSceneLayer Layer = new(LayerId, "High-value loot", 340, false);

    public HighValueLootLayerResult Build(
        HighValueLootLayerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = request.Snapshot;
        if (snapshot is null)
        {
            return Unavailable(
                HighValueLootDiagnosticKind.SnapshotUnavailable,
                "Potential spawns · Data unavailable",
                "snapshot.missing",
                "No last-known-good loot-spawn snapshot is available.");
        }

        if (!string.Equals(snapshot.MapId, request.MapId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.TransformVersion, request.TransformVersion, StringComparison.Ordinal))
        {
            return Unavailable(
                HighValueLootDiagnosticKind.SnapshotMismatch,
                "Potential spawns · Transform mismatch",
                "snapshot.map-transform-mismatch",
                "The loot-spawn snapshot belongs to a different map or transform and was not drawn.",
                snapshot.Coverage,
                snapshot.Provenance.EvidenceThroughUtc);
        }

        if (snapshot.Status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable)
        {
            return Unavailable(
                HighValueLootDiagnosticKind.SnapshotUnavailable,
                "Potential spawns · Data unavailable",
                "snapshot.unavailable",
                "The last-known loot-spawn snapshot is unavailable.",
                snapshot.Coverage,
                snapshot.Provenance.EvidenceThroughUtc);
        }

        var entries = new List<HighValueLootEntry>(snapshot.Records.Count);
        var objects = new List<MapSceneObject>(snapshot.Records.Count);
        var diagnostics = new List<HighValueLootDiagnostic>();
        foreach (var spawn in snapshot.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (spawn.Status.Completeness is ResultCompleteness.Unknown or ResultCompleteness.Unavailable)
            {
                diagnostics.Add(new(
                    HighValueLootDiagnosticKind.RecordUnavailable,
                    "spawn.unavailable",
                    "This spawn record has no usable facts.",
                    spawn.SpawnId));
                continue;
            }

            if (!SourcePasses(spawn.Provenance, request, out var sourceDiagnostic))
            {
                diagnostics.Add(new(
                    sourceDiagnostic!.Kind,
                    sourceDiagnostic.Code,
                    sourceDiagnostic.Explanation,
                    spawn.SpawnId));
                continue;
            }

            if (!FloorPasses(spawn, request.Filter, out var floorDiagnostic))
            {
                diagnostics.Add(new(
                    floorDiagnostic!.Kind,
                    floorDiagnostic.Code,
                    floorDiagnostic.Explanation,
                    spawn.SpawnId));
                continue;
            }

            var candidates = ApplyCandidateFilters(spawn.Candidates, request.Filter);
            if (candidates.Count == 0)
            {
                diagnostics.Add(new(
                    HighValueLootDiagnosticKind.FilteredOut,
                    "spawn.candidate-filtered",
                    "No candidate in this pool matches the active item or category filter.",
                    spawn.SpawnId));
                continue;
            }

            var projection = Project(spawn, candidates, request);
            if (!projection.Include)
            {
                diagnostics.Add(new(
                    projection.Values.Count == 0
                        ? HighValueLootDiagnosticKind.ValueUnavailable
                        : HighValueLootDiagnosticKind.FilteredOut,
                    projection.Values.Count == 0 ? "spawn.value-unavailable" : "spawn.below-threshold",
                    projection.Values.Count == 0
                        ? "No current trustworthy value supports this high-value filter."
                        : "Every valued candidate is below the active threshold.",
                    spawn.SpawnId));
                continue;
            }

            MapSceneObjectId? objectId = null;
            if (spawn.Location.Geometry is { } geometry)
            {
                if (geometry.Points.Any(point => !request.MapBounds.Contains(point)))
                {
                    diagnostics.Add(new(
                        HighValueLootDiagnosticKind.InvalidGeometry,
                        "spawn.outside-map-bounds",
                        "The spawn geometry is outside the reviewed map bounds and was quarantined instead of clamped.",
                        spawn.SpawnId));
                    continue;
                }

                objectId = StableObjectId(snapshot, spawn);
                objects.Add(new(
                    objectId.Value,
                    LayerId,
                    MapSceneObjectKind.LootSpawn,
                    MapSceneTruthKind.PotentialSpawn,
                    spawn.Label,
                    projection.Summary,
                    geometry,
                    spawn.Location.FloorIds,
                    SceneProvenance(spawn.Provenance)));
            }

            entries.Add(new(
                spawn,
                projection.Tier,
                projection.Values.Count == 0 ? null : projection.Values.Min(),
                projection.Values.Count == 0 ? null : projection.Values.Max(),
                projection.ValuesPerSquare.Count == 0 ? null : projection.ValuesPerSquare.Min(),
                projection.ValuesPerSquare.Count == 0 ? null : projection.ValuesPerSquare.Max(),
                projection.Values.Count,
                projection.HighValueCandidateCount,
                BasisLabel(request.Filter.ValueBasis),
                projection.Summary,
                projection.ProfileNeeds,
                projection.MissingFacts,
                objectId));
        }

        var freshness = snapshot.Status.Freshness == FreshnessState.Stale ||
                        entries.Any(entry => entry.Spawn.Status.Freshness == FreshnessState.Stale)
            ? FreshnessState.Stale
            : snapshot.Status.Freshness;
        var incomplete = diagnostics.Any(diagnostic => diagnostic.AffectsCompleteness) ||
                         snapshot.Status.Completeness == ResultCompleteness.Partial ||
                         request.Filter.ValueBasis != LootSpawnValueBasis.ProfileUtility &&
                         entries.Any(entry => entry.ValuedCandidateCount == 0);
        var status = new ResultStatus(
            incomplete ? ResultCompleteness.Partial : ResultCompleteness.Complete,
            freshness,
            incomplete ? "loot-spawns.partial" : "loot-spawns.ready");
        var through = snapshot.Provenance.EvidenceThroughUtc;
        var legend = $"Potential spawns · Updated {through:yyyy-MM-dd}" +
                     (freshness == FreshnessState.Stale ? " · Stale" : string.Empty);
        return new(Layer, status, legend, through, snapshot.Coverage, objects, entries, diagnostics);
    }

    private static Projection Project(
        LootSpawnRecord spawn,
        IReadOnlyList<LootSpawnCandidate> candidates,
        HighValueLootLayerRequest request)
    {
        var values = new List<long>(candidates.Count);
        var valuesPerSquare = new List<long>(candidates.Count);
        var highValueCandidateCount = 0;
        var missing = new HashSet<string>(StringComparer.Ordinal);
        var suppliedNeeds = candidates
            .SelectMany(candidate => candidate.ProfileNeeds)
            .DistinctBy(need => need.Code, StringComparer.Ordinal)
            .ToArray();
        var needs = suppliedNeeds
            .Where(need => NeedIsReliable(need, request))
            .OrderBy(need => NeedPriority(need.Kind))
            .ThenBy(need => need.Code, StringComparer.Ordinal)
            .ToArray();
        if (needs.Length != suppliedNeeds.Length)
        {
            missing.Add("Some profile relevance is stale, incomplete, or below the confidence filter.");
        }

        foreach (var candidate in candidates)
        {
            var value = ValueFor(candidate, request, missing);
            if (value is { } amount)
            {
                values.Add(amount);
                if (amount >= request.Filter.Thresholds.Minimum)
                {
                    highValueCandidateCount++;
                }
            }

            var bestNet = BestNet(candidate, request, missing);
            var squares = Reliable(candidate.OccupiedSquares, request, request.Filter.MaximumPriceAge);
            if (bestNet is { } net && squares is { } occupied)
            {
                valuesPerSquare.Add(net / occupied);
            }
            else if (request.Filter.ValueBasis == LootSpawnValueBasis.ValuePerSquare)
            {
                missing.Add("Current net value or occupied-square evidence is unavailable.");
            }
        }

        var profileRelevant = request.Filter.IncludeProfileRelevant && needs.Length > 0;
        var include = request.Filter.ValueBasis == LootSpawnValueBasis.ProfileUtility
            ? profileRelevant
            : values.Any(value => value >= request.Filter.Thresholds.Minimum) || profileRelevant;
        var maximum = values.Count == 0 ? (long?)null : values.Max();
        var tier = maximum is { } ceiling && ceiling >= request.Filter.Thresholds.Minimum
            ? request.Filter.Thresholds.Classify(ceiling)
            : profileRelevant
                ? LootSpawnValueTier.ProfileRelevant
                : maximum is null
                    ? LootSpawnValueTier.Unknown
                    : LootSpawnValueTier.BelowThreshold;
        if (spawn.SpawnProbability.Value is null)
        {
            missing.Add("Spawn probability is unknown; expected value is not calculated.");
        }

        if (string.IsNullOrWhiteSpace(spawn.RespawnBehavior.Value))
        {
            missing.Add("Respawn behavior is unknown.");
        }

        var summary = Summary(spawn, candidates, request.Filter.ValueBasis, values, highValueCandidateCount, needs);
        return new(
            include,
            tier,
            values,
            valuesPerSquare,
            highValueCandidateCount,
            needs,
            missing.Order(StringComparer.Ordinal).ToArray(),
            summary);
    }

    private static long? ValueFor(
        LootSpawnCandidate candidate,
        HighValueLootLayerRequest request,
        ISet<string> missing) => request.Filter.ValueBasis switch
    {
        LootSpawnValueBasis.FleaGross => RequiredValue(candidate.FleaGrossRoubles, "Current flea gross is unavailable.", request, missing),
        LootSpawnValueBasis.FleaNet => RequiredValue(candidate.FleaNetRoubles, "Current flea net is unavailable.", request, missing),
        LootSpawnValueBasis.BestTrader => RequiredValue(candidate.BestTraderRoubles, "Current trader value is unavailable.", request, missing),
        LootSpawnValueBasis.BestNet => BestNet(candidate, request, missing),
        LootSpawnValueBasis.ValuePerSquare => PerSquare(candidate, request, missing),
        LootSpawnValueBasis.ProfileUtility => null,
        _ => throw new ArgumentOutOfRangeException(nameof(request)),
    };

    private static long? BestNet(
        LootSpawnCandidate candidate,
        HighValueLootLayerRequest request,
        ISet<string> missing)
    {
        var flea = Reliable(candidate.FleaNetRoubles, request, request.Filter.MaximumPriceAge);
        var trader = Reliable(candidate.BestTraderRoubles, request, request.Filter.MaximumPriceAge);
        if (flea is null && trader is null)
        {
            missing.Add("Current flea-net and trader values are unavailable.");
            return null;
        }

        return flea is null ? trader : trader is null ? flea : Math.Max(flea.Value, trader.Value);
    }

    private static long? PerSquare(
        LootSpawnCandidate candidate,
        HighValueLootLayerRequest request,
        ISet<string> missing)
    {
        var value = BestNet(candidate, request, missing);
        var squares = Reliable(candidate.OccupiedSquares, request, request.Filter.MaximumPriceAge);
        if (squares is null)
        {
            missing.Add("Occupied-square evidence is unavailable.");
        }

        return value is { } amount && squares is { } occupied ? amount / occupied : null;
    }

    private static long? RequiredValue(
        EvidencedValue<long?> field,
        string missingFact,
        HighValueLootLayerRequest request,
        ISet<string> missing)
    {
        var value = Reliable(field, request, request.Filter.MaximumPriceAge);
        if (value is null)
        {
            missing.Add(missingFact);
        }

        return value;
    }

    private static T? Reliable<T>(
        EvidencedValue<T?> field,
        HighValueLootLayerRequest request,
        TimeSpan maximumAge)
        where T : struct =>
        field.Value is { } value &&
        field.Status.Completeness == ResultCompleteness.Complete &&
        field.Status.Freshness == FreshnessState.Current &&
        field.Provenance.EvidenceThroughUtc <= request.EvaluatedUtc &&
        request.EvaluatedUtc - field.Provenance.EvidenceThroughUtc <= maximumAge &&
        ConfidencePasses(field.Provenance, request.Filter.MinimumConfidence)
            ? value
            : null;

    private static IReadOnlyList<LootSpawnCandidate> ApplyCandidateFilters(
        IReadOnlyList<LootSpawnCandidate> candidates,
        HighValueLootFilter filter) => candidates
        .Where(candidate => filter.ItemIds.Count == 0 ||
                            filter.ItemIds.Contains(candidate.ItemId, StringComparer.OrdinalIgnoreCase))
        .Where(candidate => filter.Categories.Count == 0 ||
                            filter.Categories.Contains(candidate.Category, StringComparer.OrdinalIgnoreCase))
        .ToArray();

    private static bool SourcePasses(
        EvidenceProvenance provenance,
        HighValueLootLayerRequest request,
        out HighValueLootDiagnostic? diagnostic)
    {
        if (provenance.EvidenceThroughUtc > request.EvaluatedUtc ||
            request.EvaluatedUtc - provenance.EvidenceThroughUtc > request.Filter.MaximumSourceAge)
        {
            diagnostic = new(
                HighValueLootDiagnosticKind.SourceTooOld,
                "spawn.source-age-filtered",
                "The spawn source is outside the active source-age filter.");
            return false;
        }

        if (!ConfidencePasses(provenance, request.Filter.MinimumConfidence))
        {
            diagnostic = new(
                HighValueLootDiagnosticKind.ConfidenceBelowFilter,
                "spawn.confidence-filtered",
                "The spawn source is below the active confidence filter.");
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static bool FloorPasses(
        LootSpawnRecord spawn,
        HighValueLootFilter filter,
        out HighValueLootDiagnostic? diagnostic)
    {
        if (filter.FloorId is null)
        {
            diagnostic = null;
            return true;
        }

        if (spawn.Location.FloorIds.Count == 0)
        {
            diagnostic = new(
                HighValueLootDiagnosticKind.FloorUnknown,
                "spawn.floor-unknown",
                "The source did not resolve a floor, so it was not shown under a specific-floor filter.");
            return false;
        }

        if (!spawn.Location.FloorIds.Contains(filter.FloorId, StringComparer.OrdinalIgnoreCase))
        {
            diagnostic = new(
                HighValueLootDiagnosticKind.FilteredOut,
                "spawn.floor-filtered",
                "The spawn is not on the selected floor.");
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static bool ConfidencePasses(EvidenceProvenance provenance, double minimum) =>
        provenance.Confidence.Score is { } score ? score >= minimum : minimum == 0;

    private static bool NeedIsReliable(
        LootSpawnProfileNeed need,
        HighValueLootLayerRequest request) =>
        need.Status.Completeness == ResultCompleteness.Complete &&
        need.Status.Freshness == FreshnessState.Current &&
        need.Provenance.EvidenceThroughUtc <= request.EvaluatedUtc &&
        request.EvaluatedUtc - need.Provenance.EvidenceThroughUtc <= request.Filter.MaximumSourceAge &&
        ConfidencePasses(need.Provenance, request.Filter.MinimumConfidence);

    private static string Summary(
        LootSpawnRecord spawn,
        IReadOnlyList<LootSpawnCandidate> candidates,
        LootSpawnValueBasis basis,
        IReadOnlyList<long> values,
        int highValueCandidateCount,
        IReadOnlyList<LootSpawnProfileNeed> needs)
    {
        var basisText = BasisLabel(basis);
        if (basis == LootSpawnValueBasis.ProfileUtility)
        {
            return $"Profile-relevant potential · {candidates.Count.ToString(CultureInfo.InvariantCulture)} candidate(s)";
        }

        if (values.Count == 0)
        {
            return needs.Count > 0
                ? "Profile-relevant potential · Current value unavailable"
                : "Potential spawn · Current value unavailable";
        }

        var maximum = values.Max();
        return spawn.PoolKind == LootSpawnPoolKind.SingleKnownItem
            ? $"Potential {candidates[0].DisplayName} · {maximum.ToString("N0", CultureInfo.InvariantCulture)} ₽ {basisText}"
            : $"Potential up to {maximum.ToString("N0", CultureInfo.InvariantCulture)} ₽ {basisText} · " +
              $"{candidates.Count.ToString(CultureInfo.InvariantCulture)} candidates · " +
              $"{highValueCandidateCount.ToString(CultureInfo.InvariantCulture)} above threshold";
    }

    private static string BasisLabel(LootSpawnValueBasis basis) => basis switch
    {
        LootSpawnValueBasis.FleaGross => "flea gross",
        LootSpawnValueBasis.FleaNet => "flea net",
        LootSpawnValueBasis.BestTrader => "trader",
        LootSpawnValueBasis.BestNet => "best net",
        LootSpawnValueBasis.ValuePerSquare => "per square",
        LootSpawnValueBasis.ProfileUtility => "profile utility",
        _ => throw new ArgumentOutOfRangeException(nameof(basis)),
    };

    private static int NeedPriority(LootSpawnProfileNeedKind kind) => kind switch
    {
        LootSpawnProfileNeedKind.ProtectedItem => 0,
        LootSpawnProfileNeedKind.CurrentQuest => 1,
        LootSpawnProfileNeedKind.FutureQuest => 2,
        LootSpawnProfileNeedKind.Hideout => 3,
        LootSpawnProfileNeedKind.CraftOrBarter => 4,
        LootSpawnProfileNeedKind.Ammo => 5,
        LootSpawnProfileNeedKind.Key => 6,
        LootSpawnProfileNeedKind.Loadout => 7,
        LootSpawnProfileNeedKind.Event => 8,
        LootSpawnProfileNeedKind.UserPin => 9,
        LootSpawnProfileNeedKind.Wishlist => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static MapSceneObjectId StableObjectId(LootSpawnSnapshot snapshot, LootSpawnRecord spawn)
    {
        var canonical = $"{snapshot.DatasetVersion}|{snapshot.MapId}|{snapshot.TransformVersion}|{spawn.SpawnId}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new($"loot-spawn:{hash}");
    }

    private static DataProvenance SceneProvenance(EvidenceProvenance provenance) => new(
        provenance.SourceIdentifier,
        provenance.ObservedUtc,
        provenance.EvidenceThroughUtc,
        provenance.Reference,
        provenance.Confidence.Score is { } score ? new Confidence(score) : Confidence.Unknown);

    private static HighValueLootLayerResult Unavailable(
        HighValueLootDiagnosticKind kind,
        string legend,
        string code,
        string explanation,
        LootSpawnCoverage? coverage = null,
        DateTimeOffset? dataThroughUtc = null) => new(
        Layer,
        new ResultStatus(ResultCompleteness.Unavailable, FreshnessState.Unknown, code),
        legend,
        dataThroughUtc,
        coverage,
        [],
        [],
        [new HighValueLootDiagnostic(kind, code, explanation)]);

    private sealed record Projection(
        bool Include,
        LootSpawnValueTier Tier,
        IReadOnlyList<long> Values,
        IReadOnlyList<long> ValuesPerSquare,
        int HighValueCandidateCount,
        IReadOnlyList<LootSpawnProfileNeed> ProfileNeeds,
        IReadOnlyList<string> MissingFacts,
        string Summary);
}

/// <summary>The one-action layer preset; the shell supplies any additional user-required context.</summary>
public static class HighValueLootLayerPreset
{
    private static readonly IReadOnlySet<string> BuiltInOrientationLayers = new HashSet<string>(StringComparer.Ordinal)
    {
        "extracts",
        "companion-markers",
        "routes",
        "pings",
        "waypoints",
    };

    public static IReadOnlyList<MapSceneLayerState> Create(
        IReadOnlyList<MapSceneLayer> layers,
        IReadOnlyList<MapSceneLayerId>? preserveVisibleLayerIds = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        var preserved = new HashSet<MapSceneLayerId>(preserveVisibleLayerIds ?? []);
        return layers.Select(layer => new MapSceneLayerState(
                layer.Id,
                layer.Id == HighValueLootLayerService.LayerId ||
                BuiltInOrientationLayers.Contains(layer.Id.Value) ||
                preserved.Contains(layer.Id)))
            .ToArray();
    }
}

internal static class HighValueLootGuard
{
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
