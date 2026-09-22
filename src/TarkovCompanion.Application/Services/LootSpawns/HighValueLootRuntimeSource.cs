using TarkovCompanion.Core.Domain.Evidence;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.Application.Services.LootSpawns;

/// <summary>The selected map-transform context needed to project the current governed head.</summary>
public sealed record HighValueLootRuntimeLayerRequest(
    string MapId,
    string TransformVersion,
    MapSceneBounds MapBounds,
    DateTimeOffset EvaluatedUtc,
    HighValueLootFilter Filter,
    IReadOnlyList<string>? FloorIds = null);

/// <summary>
/// Owns the one process-wide last-known-good loot publication consumed by desktop map scenes.
/// </summary>
/// <remarks>
/// Loading and refresh are serialized, while reads remain lock-free because a source bundle is
/// immutable. A failed refresh never clears the in-memory head. The layer projector still checks
/// the exact map and transform before exposing any marker, so a durable snapshot cannot drift onto
/// a different map revision after restart.
/// </remarks>
public interface IHighValueLootRuntimeSource
{
    LootSpawnSourceBundle? LastKnownGood { get; }

    /// <summary>
    /// What the most recent <see cref="RefreshAsync"/> attempt found, whether or not it advanced
    /// <see cref="LastKnownGood"/>. [Issue 563] A refresh that quarantines its candidate used to
    /// leave no trace anywhere a player or Setup > Data could see; this is that trace.
    /// </summary>
    LootSpawnSourceRefreshOutcome? LastRefreshOutcome { get; }

    bool NeedsRefresh(DateTimeOffset evaluatedUtc, TimeSpan freshFor);

    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask RefreshAsync(bool force, CancellationToken cancellationToken = default);

    HighValueLootLayerResult Build(
        HighValueLootRuntimeLayerRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class HighValueLootRuntimeSource : IHighValueLootRuntimeSource
{
    private readonly ILootSpawnSourcePublicationStore _publicationStore;
    private readonly ILootSpawnSourceRefreshService _refreshService;
    private readonly HighValueLootLayerService _layerService;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private LootSpawnSourceBundle? _lastKnownGood;
    private LootSpawnSourceRefreshOutcome? _lastRefreshOutcome;
    private ReboundSnapshot? _rebound;

    public HighValueLootRuntimeSource(
        ILootSpawnSourcePublicationStore publicationStore,
        ILootSpawnSourceRefreshService refreshService,
        HighValueLootLayerService layerService,
        TimeProvider? timeProvider = null)
    {
        _publicationStore = publicationStore ?? throw new ArgumentNullException(nameof(publicationStore));
        _refreshService = refreshService ?? throw new ArgumentNullException(nameof(refreshService));
        _layerService = layerService ?? throw new ArgumentNullException(nameof(layerService));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public LootSpawnSourceBundle? LastKnownGood => Volatile.Read(ref _lastKnownGood);

    public LootSpawnSourceRefreshOutcome? LastRefreshOutcome => Volatile.Read(ref _lastRefreshOutcome);

    public bool NeedsRefresh(DateTimeOffset evaluatedUtc, TimeSpan freshFor)
    {
        if (evaluatedUtc == default || evaluatedUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A non-default UTC evaluation time is required.", nameof(evaluatedUtc));
        }

        if (freshFor <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(freshFor));
        }

        var head = LastKnownGood;
        return head is null || evaluatedUtc - head.Identity.ImportedUtc > freshFor;
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var durableHead = await _publicationStore
                .ReadLastKnownGoodAsync(cancellationToken)
                .ConfigureAwait(false);
            if (durableHead is not null)
            {
                Volatile.Write(ref _lastKnownGood, durableHead);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask RefreshAsync(bool force, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _refreshService
                .RefreshAsync(force, cancellationToken)
                .ConfigureAwait(false);
            var retained = result.Published ?? result.LastKnownGood;
            if (retained is not null)
            {
                Volatile.Write(ref _lastKnownGood, retained);
            }

            Volatile.Write(
                ref _lastRefreshOutcome,
                new LootSpawnSourceRefreshOutcome(_timeProvider.GetUtcNow(), result.Disposition, result.Diagnostics));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public HighValueLootLayerResult Build(
        HighValueLootRuntimeLayerRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = LastKnownGood?.Snapshots.SingleOrDefault(candidate =>
            string.Equals(candidate.MapId, request.MapId, StringComparison.Ordinal));
        var rebound = snapshot is not null &&
                      !string.Equals(snapshot.TransformVersion, request.TransformVersion, StringComparison.Ordinal);
        if (rebound)
        {
            snapshot = Rebind(snapshot!, request.TransformVersion);
        }

        var result = _layerService.Build(new(
            request.MapId,
            request.TransformVersion,
            request.MapBounds,
            request.EvaluatedUtc,
            request.Filter,
            snapshot,
            request.FloorIds), cancellationToken);
        return rebound ? MarkMaybeStale(result) : result;
    }

    /// <summary>
    /// [Issue 563] A publication projected under another revision of this map's catalog variant
    /// (the catalog refreshed and the loot import has not yet caught up, or the other way round)
    /// is still drawn, labelled as possibly misplaced, rather than refused outright: a refused
    /// snapshot left "High-value loot only" with an empty map and no hint why. Positions outside
    /// the current plan are still dropped by the layer's own bounds check.
    /// </summary>
    private LootSpawnSnapshot Rebind(LootSpawnSnapshot snapshot, string transformVersion)
    {
        var cached = Volatile.Read(ref _rebound);
        if (cached is not null &&
            ReferenceEquals(cached.Source, snapshot) &&
            string.Equals(cached.Snapshot.TransformVersion, transformVersion, StringComparison.Ordinal))
        {
            return cached.Snapshot;
        }

        var records = snapshot.Records.Select(record => new LootSpawnRecord(
                record.SpawnId,
                record.MapId,
                record.Label,
                record.Location,
                record.PoolKind,
                record.Candidates,
                record.SpawnProbability,
                record.RespawnBehavior,
                record.DatasetVersion,
                transformVersion,
                record.Status,
                record.Provenance,
                record.AccessNote))
            .ToArray();
        var copy = new LootSpawnSnapshot(
            snapshot.SnapshotId,
            snapshot.DatasetVersion,
            snapshot.MapId,
            transformVersion,
            snapshot.GeneratedUtc,
            snapshot.Status,
            snapshot.Coverage,
            snapshot.Provenance,
            records);
        Volatile.Write(ref _rebound, new ReboundSnapshot(snapshot, copy));
        return copy;
    }

    private static HighValueLootLayerResult MarkMaybeStale(HighValueLootLayerResult result)
    {
        if (result.Status.Completeness is ResultCompleteness.Unavailable or ResultCompleteness.Unknown)
        {
            return result;
        }

        const string Suffix = " · Map changed, positions may be off";
        var legend = result.CompactLegend.Length + Suffix.Length <= 256
            ? result.CompactLegend + Suffix
            : result.CompactLegend;
        return new(
            result.Layer,
            result.MapId,
            result.TransformVersion,
            result.AppliedFilter,
            new ResultStatus(ResultCompleteness.Partial, FreshnessState.Stale, "loot-spawns.transform-stale"),
            legend,
            result.DataThroughUtc,
            result.Coverage,
            result.Objects,
            result.Entries,
            result.Diagnostics
                .Append(new HighValueLootDiagnostic(
                    HighValueLootDiagnosticKind.SnapshotMismatch,
                    "snapshot.transform-stale",
                    "The loot-spawn data was projected for an earlier revision of this map; positions may be off until the next loot refresh."))
                .ToArray());
    }

    private sealed record ReboundSnapshot(LootSpawnSnapshot Source, LootSpawnSnapshot Snapshot);
}

/// <summary>
/// [Issue 563] What one <see cref="IHighValueLootRuntimeSource.RefreshAsync"/> attempt found.
/// Kept even when the attempt quarantined and changed nothing, so Setup > Data can say when the
/// import last ran and why it did not publish, instead of only ever showing silence.
/// </summary>
public sealed record LootSpawnSourceRefreshOutcome(
    DateTimeOffset AttemptedUtc,
    LootSpawnSourceImportDisposition Disposition,
    IReadOnlyList<LootSpawnSourceDiagnostic> Diagnostics);
