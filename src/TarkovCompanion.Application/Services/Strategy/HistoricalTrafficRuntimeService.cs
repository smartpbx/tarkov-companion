using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.Strategy.Data;
using V2RaidPhase = TarkovCompanion.Core.Abstractions.V2.RaidPhase;

namespace TarkovCompanion.Application.Services.Strategy;

public enum HistoricalTrafficRuntimeStatus
{
    Available = 1,
    Partial,
    NoInstalledModel,
    IncompatibleModel,
    RaidPhaseUnknown,
    NoPhaseCoverage,
}

public sealed record HistoricalTrafficRuntimeRequest
{
    public HistoricalTrafficRuntimeRequest(
        TrafficCompatibilityScope scope,
        RaidClockReading? raidClock,
        TimeSpan raidDuration,
        DateTimeOffset evaluatedUtc,
        TimeSpan maximumModelAge)
    {
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (raidDuration <= TimeSpan.Zero ||
            raidDuration > TimeSpan.FromSeconds(TrafficDataBounds.MaximumRaidElapsedSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(raidDuration));
        }

        if (evaluatedUtc == default || evaluatedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Traffic evaluation time must be a defined UTC instant.", nameof(evaluatedUtc));
        }

        if (maximumModelAge <= TimeSpan.Zero || maximumModelAge > TimeSpan.FromDays(3650))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumModelAge));
        }

        RaidClock = raidClock;
        RaidDuration = raidDuration;
        EvaluatedUtc = evaluatedUtc;
        MaximumModelAge = maximumModelAge;
    }

    public TrafficCompatibilityScope Scope { get; }

    public RaidClockReading? RaidClock { get; }

    public TimeSpan RaidDuration { get; }

    public DateTimeOffset EvaluatedUtc { get; }

    public TimeSpan MaximumModelAge { get; }
}

public sealed record TrafficPredictionReceipt
{
    public TrafficPredictionReceipt(
        string predictionId,
        string modelVersion,
        string datasetVersion,
        string artifactSha256,
        TrafficCompatibilityScope scope,
        V2RaidPhase phase,
        int elapsedSeconds,
        DateTimeOffset shownUtc,
        IReadOnlyList<ModelledIntelligence<ZoneTrafficIntensity>> zones,
        IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> corridors,
        IReadOnlyList<ModelledIntelligence<EncounterLikelihood>> encounters)
    {
        if (!IsSha256(predictionId) || !IsSha256(artifactSha256))
        {
            throw new ArgumentException("Prediction and artifact identities must be canonical SHA-256 values.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(modelVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetVersion);
        Scope = scope ?? throw new ArgumentNullException(nameof(scope));
        if (!Enum.IsDefined(phase) || elapsedSeconds is < 0 or > TrafficDataBounds.MaximumRaidElapsedSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(phase));
        }

        if (shownUtc == default || shownUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Prediction display time must be a defined UTC instant.", nameof(shownUtc));
        }

        PredictionId = predictionId;
        ModelVersion = modelVersion.Trim();
        DatasetVersion = datasetVersion.Trim();
        ArtifactSha256 = artifactSha256;
        Phase = phase;
        ElapsedSeconds = elapsedSeconds;
        ShownUtc = shownUtc;
        Zones = Freeze(zones, nameof(zones));
        Corridors = Freeze(corridors, nameof(corridors));
        Encounters = Freeze(encounters, nameof(encounters));
        if (Zones.Count == 0 && Corridors.Count == 0 && Encounters.Count == 0)
        {
            throw new ArgumentException("A shown prediction receipt must retain at least one exact value.");
        }
    }

    public string PredictionId { get; }
    public string ModelVersion { get; }
    public string DatasetVersion { get; }
    public string ArtifactSha256 { get; }
    public TrafficCompatibilityScope Scope { get; }
    public V2RaidPhase Phase { get; }
    public int ElapsedSeconds { get; }
    public DateTimeOffset ShownUtc { get; }
    public IReadOnlyList<ModelledIntelligence<ZoneTrafficIntensity>> Zones { get; }
    public IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> Corridors { get; }
    public IReadOnlyList<ModelledIntelligence<EncounterLikelihood>> Encounters { get; }

    private static bool IsSha256(string value) =>
        value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Count > TrafficDataBounds.MaximumRecords)
        {
            throw new ArgumentException("Traffic prediction values exceed the dataset record bound.", parameterName);
        }

        var copy = values.ToArray();
        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("Traffic prediction values cannot contain null entries.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }
}

public sealed record HistoricalTrafficRuntimeResult
{
    public HistoricalTrafficRuntimeResult(
        HistoricalTrafficRuntimeStatus status,
        V2RaidPhase? phase,
        int? elapsedSeconds,
        IReadOnlyList<ModelledIntelligence<ZoneTrafficIntensity>> zones,
        IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> corridors,
        IReadOnlyList<ModelledIntelligence<EncounterLikelihood>> encounters,
        TrafficPredictionReceipt? receipt,
        TrafficModelArtifactManifest? manifest,
        IReadOnlyList<TrafficCoverageGap> coverageGaps,
        string guidance)
    {
        if (!Enum.IsDefined(status) ||
            (phase is { } definedPhase && !Enum.IsDefined(definedPhase)) ||
            elapsedSeconds is < 0 or > TrafficDataBounds.MaximumRaidElapsedSeconds)
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(guidance);
        var hasProducts = receipt is not null;
        if (hasProducts != (phase is not null && elapsedSeconds is not null) ||
            hasProducts != (status is HistoricalTrafficRuntimeStatus.Available or HistoricalTrafficRuntimeStatus.Partial))
        {
            throw new ArgumentException("Traffic result status, phase, and receipt do not describe one state.");
        }

        if (receipt is not null &&
            (receipt.Phase != phase ||
             receipt.ElapsedSeconds != elapsedSeconds ||
             !receipt.Zones.SequenceEqual(zones) ||
             !receipt.Corridors.SequenceEqual(corridors) ||
             !receipt.Encounters.SequenceEqual(encounters)))
        {
            throw new ArgumentException("The immutable receipt must retain the exact values returned to the caller.", nameof(receipt));
        }

        Status = status;
        Phase = phase;
        ElapsedSeconds = elapsedSeconds;
        Zones = Freeze(zones, nameof(zones));
        Corridors = Freeze(corridors, nameof(corridors));
        Encounters = Freeze(encounters, nameof(encounters));
        Receipt = receipt;
        Manifest = manifest;
        CoverageGaps = Freeze(coverageGaps, nameof(coverageGaps));
        Guidance = guidance.Trim();
    }

    public HistoricalTrafficRuntimeStatus Status { get; }
    public V2RaidPhase? Phase { get; }
    public int? ElapsedSeconds { get; }
    public IReadOnlyList<ModelledIntelligence<ZoneTrafficIntensity>> Zones { get; }
    public IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> Corridors { get; }
    public IReadOnlyList<ModelledIntelligence<EncounterLikelihood>> Encounters { get; }
    public TrafficPredictionReceipt? Receipt { get; }
    public TrafficModelArtifactManifest? Manifest { get; }
    public IReadOnlyList<TrafficCoverageGap> CoverageGaps { get; }
    public string Guidance { get; }
    public bool HasPredictions => Receipt is not null;

    private static IReadOnlyList<T> Freeze<T>(IReadOnlyList<T> values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        var copy = values.ToArray();
        if (copy.Any(value => value is null))
        {
            throw new ArgumentException("Traffic results cannot contain null entries.", parameterName);
        }

        return Array.AsReadOnly(copy);
    }
}

/// <summary>
/// Converts one already-verified governed publication into renderer-neutral historical traffic
/// products. It never accepts a current-player or live-entity input, and held-out rows remain
/// reserved for evaluation rather than influencing a prediction.
/// </summary>
public sealed class HistoricalTrafficRuntimeService
{
    private const string RuntimeVersion = "traffic-runtime-v1";

    public HistoricalTrafficRuntimeResult Evaluate(
        TrafficModelPublication? publication,
        HistoricalTrafficRuntimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (publication is null)
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.NoInstalledModel,
                "No compatible historical model is installed. The map and manual route tools remain available.");
        }

        var manifest = publication.Manifest;
        if (!manifest.Supports(request.Scope))
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.IncompatibleModel,
                "The installed model has no exact map, game-version, mode, wipe, and cohort match.",
                manifest);
        }

        if (manifest.GeneratedUtc > request.EvaluatedUtc || manifest.DataThroughUtc > request.EvaluatedUtc)
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.IncompatibleModel,
                "The installed model is dated after the evaluation clock and cannot be aged honestly.",
                manifest);
        }

        if (!TryResolvePhase(request, out var phase, out var elapsedSeconds))
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.RaidPhaseUnknown,
                "Raid phase is unknown. Historical layers stay hidden until a reviewed raid clock is available.",
                manifest);
        }

        var sourceMap = publication.Dataset.Sources.ToDictionary(source => source.SourceId, StringComparer.Ordinal);
        var records = publication.Dataset.Records
            .Where(record => record.Scope == request.Scope &&
                             record.Partition != TrafficDataPartition.HeldOut &&
                             record.Window.Phase == phase &&
                             Includes(record.Window, elapsedSeconds) &&
                             sourceMap.TryGetValue(record.SourceId, out var source) &&
                             source.AllowedUses.Contains(TrafficDataAllowedUse.RuntimeInference))
            .OrderBy(record => record.RecordId, StringComparer.Ordinal)
            .ToArray();
        if (records.Length == 0)
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.NoPhaseCoverage,
                "The installed model has no eligible historical coverage for this raid phase.",
                manifest,
                ScopeGaps(manifest, request.Scope));
        }

        var inputs = BuildInputs(records, sourceMap, manifest.DataThroughUtc);
        if (inputs.Length == 0)
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.NoPhaseCoverage,
                "Historical rows exist, but none has a complete bounded runtime evidence lineage.",
                manifest,
                ScopeGaps(manifest, request.Scope));
        }

        var selectedSourceIds = inputs.Select(input => input.EvidenceId).ToHashSet(StringComparer.Ordinal);
        records = records.Where(record => selectedSourceIds.Contains(record.SourceId)).ToArray();
        var gaps = ScopeGaps(manifest, request.Scope);
        var freshness = request.EvaluatedUtc - manifest.DataThroughUtc > request.MaximumModelAge
            ? FreshnessState.Stale
            : FreshnessState.Current;
        var zoneCells = Aggregate(records.Where(record => record.Location.RegionId is not null), region: true);
        var corridorCells = Aggregate(records.Where(record => record.Location.CorridorId is not null), region: false);
        var maximumZonePressure = MaximumPressure(zoneCells);
        var maximumCorridorPressure = MaximumPressure(corridorCells);
        var provenance = CreateProvenance(manifest, request.EvaluatedUtc, records, inputs);
        var status = gaps.Count == 0 && freshness == FreshnessState.Current
            ? ResultCompleteness.Complete
            : ResultCompleteness.Partial;

        var zones = zoneCells
            .Where(cell => cell.KnownSamples > 0)
            .Select(cell => new ModelledIntelligence<ZoneTrafficIntensity>(
                $"traffic-zone-{request.Scope.MapId}-{cell.LocationId}-{phase}",
                new EvidencedValue<ZoneTrafficIntensity>(
                    "traffic.zone.relativeIntensity",
                    new ZoneTrafficIntensity(
                        request.Scope.MapId,
                        cell.LocationId,
                        phase,
                        maximumZonePressure == 0
                            ? 0
                            : Math.Clamp(cell.Pressure / maximumZonePressure, 0, 1)),
                    new ResultStatus(status, freshness, "traffic.historical", "Historical aggregate; not a live observation."),
                    provenance),
                inputs,
                Explain(cell, "zone")))
            .OrderByDescending(item => item.Estimate.Value!.RelativeIntensity)
            .ThenBy(item => item.Estimate.Value!.ZoneId, StringComparer.Ordinal)
            .ToArray();

        var corridors = corridorCells
            .Where(cell => cell.KnownSamples > 0)
            .Select(cell => new ModelledIntelligence<RouteCorridorPressure>(
                $"traffic-corridor-{request.Scope.MapId}-{cell.LocationId}-{phase}",
                new EvidencedValue<RouteCorridorPressure>(
                    "traffic.corridor.relativePressure",
                    new RouteCorridorPressure(
                        request.Scope.MapId,
                        cell.LocationId,
                        phase,
                        maximumCorridorPressure == 0
                            ? 0
                            : Math.Clamp(cell.Pressure / maximumCorridorPressure, 0, 1)),
                    new ResultStatus(status, freshness, "traffic.historical", "Historical aggregate; not a live observation."),
                    provenance),
                inputs,
                Explain(cell, "corridor")))
            .OrderByDescending(item => item.Estimate.Value!.RelativePressure)
            .ThenBy(item => item.Estimate.Value!.CorridorId, StringComparer.Ordinal)
            .ToArray();

        var encounters = zoneCells
            .Where(cell => cell.ContactSamples + cell.NoContactSamples > 0)
            .Select(cell => new ModelledIntelligence<EncounterLikelihood>(
                $"traffic-encounter-{request.Scope.MapId}-{cell.LocationId}-{phase}",
                new EvidencedValue<EncounterLikelihood>(
                    "traffic.zone.encounterLikelihood",
                    new EncounterLikelihood(
                        request.Scope.MapId,
                        cell.LocationId,
                        phase,
                        cell.ContactSamples / (double)(cell.ContactSamples + cell.NoContactSamples)),
                    new ResultStatus(status, freshness, "traffic.historical", "Historical aggregate; not a live observation."),
                    provenance),
                inputs,
                $"Explicit historical contact versus no-contact samples for {cell.LocationId}; unknown and avoided samples are not relabelled."))
            .OrderByDescending(item => item.Estimate.Value!.Probability)
            .ThenBy(item => item.Estimate.Value!.ZoneId, StringComparer.Ordinal)
            .ToArray();

        if (zones.Length == 0 && corridors.Length == 0 && encounters.Length == 0)
        {
            return Unavailable(
                HistoricalTrafficRuntimeStatus.NoPhaseCoverage,
                "Coverage contains only unknown observations, so no traffic value is invented.",
                manifest,
                gaps);
        }

        var receipt = CreateReceipt(
            manifest,
            request.Scope,
            phase,
            elapsedSeconds,
            request.EvaluatedUtc,
            zones,
            corridors,
            encounters);
        return new HistoricalTrafficRuntimeResult(
            status == ResultCompleteness.Complete
                ? HistoricalTrafficRuntimeStatus.Available
                : HistoricalTrafficRuntimeStatus.Partial,
            phase,
            elapsedSeconds,
            zones,
            corridors,
            encounters,
            receipt,
            manifest,
            gaps,
            freshness == FreshnessState.Stale
                ? "Historical estimates are older than the configured freshness window; inspect age and coverage before using them."
                : gaps.Count > 0
                    ? "Historical estimates are available with named coverage gaps."
                    : "Historical estimates are available for this exact profile and raid phase.");
    }

    private static bool TryResolvePhase(
        HistoricalTrafficRuntimeRequest request,
        out V2RaidPhase phase,
        out int elapsedSeconds)
    {
        phase = default;
        elapsedSeconds = 0;
        var clock = request.RaidClock;
        if (clock is null || clock.AsOfUtc > request.EvaluatedUtc || clock.Remaining > request.RaidDuration)
        {
            return false;
        }

        var age = request.EvaluatedUtc - clock.AsOfUtc;
        var remaining = clock.Remaining - age;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        elapsedSeconds = Math.Clamp(
            (int)Math.Floor((request.RaidDuration - remaining).TotalSeconds),
            0,
            (int)Math.Floor(request.RaidDuration.TotalSeconds));
        var progress = elapsedSeconds / request.RaidDuration.TotalSeconds;
        phase = progress switch
        {
            < 1d / 3d => V2RaidPhase.Early,
            < 2d / 3d => V2RaidPhase.Mid,
            _ => V2RaidPhase.Late,
        };
        return true;
    }

    private static bool Includes(TrafficPhaseWindow window, int elapsedSeconds) =>
        window.StartElapsedSeconds is null ||
        (elapsedSeconds >= window.StartElapsedSeconds.Value &&
         elapsedSeconds <= window.EndElapsedSeconds!.Value);

    private static IntelligenceInputReference[] BuildInputs(
        IEnumerable<TrafficAggregateRecord> records,
        IReadOnlyDictionary<string, TrafficDatasetSource> sources,
        DateTimeOffset dataThroughUtc) =>
        records.Select(record => record.SourceId)
            .Distinct(StringComparer.Ordinal)
            .Select(sourceId => sources[sourceId])
            .Where(source => source.Provenance.EvidenceThroughUtc <= dataThroughUtc)
            .OrderBy(source => source.SourceId, StringComparer.Ordinal)
            .GroupBy(source => source.Provenance)
            .Select(group => group.First())
            .Take(EvidenceProvenance.MaxInputCount)
            .Select(source => new IntelligenceInputReference(
                source.SourceId,
                source.Kind switch
                {
                    TrafficDataSourceKind.StaticPublicFacts => IntelligenceInputKind.PublicStructuredData,
                    TrafficDataSourceKind.ReviewedCuratedKnowledge => IntelligenceInputKind.CuratedKnowledge,
                    TrafficDataSourceKind.HistoricalAggregate => IntelligenceInputKind.HistoricalAggregate,
                    TrafficDataSourceKind.PrivateLocalFeedback => IntelligenceInputKind.PrivateLocalFeedback,
                    _ => throw new ArgumentOutOfRangeException(nameof(source.Kind)),
                },
                source.Provenance))
            .ToArray();

    private static EvidenceProvenance CreateProvenance(
        TrafficModelArtifactManifest manifest,
        DateTimeOffset evaluatedUtc,
        IReadOnlyCollection<TrafficAggregateRecord> records,
        IReadOnlyList<IntelligenceInputReference> inputs) => new(
        EvidenceSourceClass.ModelledEstimate,
        $"traffic-model:{manifest.ModelId}",
        evaluatedUtc,
        manifest.Confidence,
        new ProducerIdentity("Tarkov Companion historical traffic runtime", RuntimeVersion, manifest.ModelVersion),
        manifest.DataThroughUtc,
        manifest.GeneratedUtc,
        new EvidenceCoverage(
            records.Sum(record => record.SampleCount),
            description: $"{records.Count.ToString(CultureInfo.InvariantCulture)} eligible aggregate cells for this exact scope and phase."),
        manifest.ArtifactSha256,
        inputs.Select(input => input.Provenance).ToArray());

    private static TrafficCell[] Aggregate(IEnumerable<TrafficAggregateRecord> records, bool region) =>
        records.GroupBy(
                record => region ? record.Location.RegionId! : record.Location.CorridorId!,
                StringComparer.Ordinal)
            .Select(group =>
            {
                long contact = 0;
                long noContact = 0;
                long avoided = 0;
                long unknown = 0;
                foreach (var record in group)
                {
                    switch (record.Observation)
                    {
                        case TrafficObservationClass.Contact:
                            contact = checked(contact + record.SampleCount);
                            break;
                        case TrafficObservationClass.NoContact:
                            noContact = checked(noContact + record.SampleCount);
                            break;
                        case TrafficObservationClass.Avoided:
                            avoided = checked(avoided + record.SampleCount);
                            break;
                        case TrafficObservationClass.Unknown:
                            unknown = checked(unknown + record.SampleCount);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(record.Observation));
                    }
                }

                return new TrafficCell(group.Key, contact, noContact, avoided, unknown);
            })
            .OrderBy(cell => cell.LocationId, StringComparer.Ordinal)
            .ToArray();

    private static double MaximumPressure(IEnumerable<TrafficCell> cells) =>
        cells.Where(cell => cell.KnownSamples > 0)
            .Select(cell => cell.Pressure)
            .DefaultIfEmpty(0)
            .Max();

    private static string Explain(TrafficCell cell, string kind) =>
        $"{kind} {cell.LocationId}: {cell.ContactSamples.ToString(CultureInfo.InvariantCulture)} contact, " +
        $"{cell.NoContactSamples.ToString(CultureInfo.InvariantCulture)} no-contact, " +
        $"{cell.AvoidedSamples.ToString(CultureInfo.InvariantCulture)} avoided, and " +
        $"{cell.UnknownSamples.ToString(CultureInfo.InvariantCulture)} explicit unknown historical samples.";

    private static IReadOnlyList<TrafficCoverageGap> ScopeGaps(
        TrafficModelArtifactManifest manifest,
        TrafficCompatibilityScope scope) =>
        manifest.CoverageGaps.Where(gap => gap.Scope == scope).ToArray();

    private static TrafficPredictionReceipt CreateReceipt(
        TrafficModelArtifactManifest manifest,
        TrafficCompatibilityScope scope,
        V2RaidPhase phase,
        int elapsedSeconds,
        DateTimeOffset shownUtc,
        IReadOnlyList<ModelledIntelligence<ZoneTrafficIntensity>> zones,
        IReadOnlyList<ModelledIntelligence<RouteCorridorPressure>> corridors,
        IReadOnlyList<ModelledIntelligence<EncounterLikelihood>> encounters)
    {
        var canonical = new StringBuilder()
            .Append(RuntimeVersion).Append('\n')
            .Append(manifest.ArtifactSha256).Append('\n')
            .Append(scope.MapId).Append('\n')
            .Append(scope.GameVersion).Append('\n')
            .Append(scope.GameMode).Append('\n')
            .Append(scope.WipeId).Append('\n')
            .Append(scope.CohortId).Append('\n')
            .Append(phase).Append('\n')
            .Append(elapsedSeconds.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(shownUtc.ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        foreach (var zone in zones.OrderBy(item => item.IntelligenceId, StringComparer.Ordinal))
        {
            canonical.Append(zone.IntelligenceId).Append('=')
                .Append(zone.Estimate.Value!.RelativeIntensity.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var corridor in corridors.OrderBy(item => item.IntelligenceId, StringComparer.Ordinal))
        {
            canonical.Append(corridor.IntelligenceId).Append('=')
                .Append(corridor.Estimate.Value!.RelativePressure.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        foreach (var encounter in encounters.OrderBy(item => item.IntelligenceId, StringComparer.Ordinal))
        {
            canonical.Append(encounter.IntelligenceId).Append('=')
                .Append(encounter.Estimate.Value!.Probability.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
        }

        var predictionId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
        return new TrafficPredictionReceipt(
            predictionId,
            manifest.ModelVersion,
            manifest.DatasetVersion,
            manifest.ArtifactSha256,
            scope,
            phase,
            elapsedSeconds,
            shownUtc,
            zones,
            corridors,
            encounters);
    }

    private static HistoricalTrafficRuntimeResult Unavailable(
        HistoricalTrafficRuntimeStatus status,
        string guidance,
        TrafficModelArtifactManifest? manifest = null,
        IReadOnlyList<TrafficCoverageGap>? gaps = null) => new(
        status,
        null,
        null,
        [],
        [],
        [],
        null,
        manifest,
        gaps ?? [],
        guidance);

    private sealed record TrafficCell(
        string LocationId,
        long ContactSamples,
        long NoContactSamples,
        long AvoidedSamples,
        long UnknownSamples)
    {
        public long KnownSamples => checked(ContactSamples + NoContactSamples + AvoidedSamples);

        public double Pressure => KnownSamples == 0
            ? 0
            : (ContactSamples + (0.5 * AvoidedSamples)) / KnownSamples;
    }
}
