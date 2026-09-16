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
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private LootSpawnSourceBundle? _lastKnownGood;

    public HighValueLootRuntimeSource(
        ILootSpawnSourcePublicationStore publicationStore,
        ILootSpawnSourceRefreshService refreshService,
        HighValueLootLayerService layerService)
    {
        _publicationStore = publicationStore ?? throw new ArgumentNullException(nameof(publicationStore));
        _refreshService = refreshService ?? throw new ArgumentNullException(nameof(refreshService));
        _layerService = layerService ?? throw new ArgumentNullException(nameof(layerService));
    }

    public LootSpawnSourceBundle? LastKnownGood => Volatile.Read(ref _lastKnownGood);

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
        return _layerService.Build(new(
            request.MapId,
            request.TransformVersion,
            request.MapBounds,
            request.EvaluatedUtc,
            request.Filter,
            snapshot,
            request.FloorIds), cancellationToken);
    }
}
