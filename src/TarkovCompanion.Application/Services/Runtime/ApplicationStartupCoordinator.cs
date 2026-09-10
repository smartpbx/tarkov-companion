using Microsoft.Extensions.Logging;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

public sealed class ApplicationStartupCoordinator : IAsyncDisposable
{
    private readonly IRuntimeDataStore _dataStore;
    private readonly IDataSyncService _dataSyncService;
    private readonly IPlayerProfileService _profileService;
    private readonly RaidActivityCoordinator _raidActivityCoordinator;
    private readonly IRuntimeStateStore _stateStore;
    private readonly RuntimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ApplicationStartupCoordinator> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Task? _backgroundRefresh;

    public ApplicationStartupCoordinator(
        IRuntimeDataStore dataStore,
        IDataSyncService dataSyncService,
        IPlayerProfileService profileService,
        RaidActivityCoordinator raidActivityCoordinator,
        IRuntimeStateStore stateStore,
        RuntimeOptions options,
        ILogger<ApplicationStartupCoordinator> logger,
        TimeProvider? timeProvider = null)
    {
        _dataStore = dataStore;
        _dataSyncService = dataSyncService;
        _profileService = profileService;
        _raidActivityCoordinator = raidActivityCoordinator;
        _stateStore = stateStore;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task? BackgroundRefresh => _backgroundRefresh;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _dataStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_options.DemoMode)
        {
            await _dataStore.SeedDemoAsync(cancellationToken).ConfigureAwait(false);
        }

        var cached = await _dataStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        _stateStore.Update(current => current with
        {
            DatabaseReady = true,
            Data = Describe(cached),
            Profile = profile,
        });

        if (_options.DemoMode)
        {
            var now = _timeProvider.GetUtcNow();
            await _raidActivityCoordinator.ApplyEvidenceAsync(
                new(
                    RaidEvidenceKind.Simulator,
                    now,
                    "customs",
                    RaidLifecycleState.InRaid,
                    new Confidence(0.95),
                    "Deterministic demo fixture selected Customs."),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public void BeginBackgroundRefresh()
    {
        if (_options.DemoMode || _options.Offline || !NeedsRefresh(_stateStore.Current.Data))
        {
            return;
        }

        _backgroundRefresh ??= Task.Run(
            () => RefreshAsync(force: true, _stopping.Token),
            CancellationToken.None);
    }

    public async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        if (_options.Offline)
        {
            _stateStore.Update(current => current with
            {
                Data = current.Data with
                {
                    Availability = current.Data.ItemCount > 0 ? DataAvailability.Cached : DataAvailability.Unavailable,
                    Detail = current.Data.ItemCount > 0
                        ? "Offline mode is enabled; using the local cache."
                        : "Offline mode is enabled and no local game data is available.",
                },
            });
            return;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stateStore.Update(current => current with
            {
                Data = current.Data with { Availability = DataAvailability.Refreshing, Detail = "Refreshing stale game data in the background." },
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.RefreshTimeout);
            var report = await _dataSyncService.SyncAsync(
                new(_options.GameMode, _options.Language, force),
                timeout.Token).ConfigureAwait(false);
            var cached = await _dataStore.LoadSnapshotAsync(timeout.Token).ConfigureAwait(false);
            var errors = report.Endpoints.Where(endpoint => endpoint.Error is not null).ToArray();
            _stateStore.Update(current => current with
            {
                Data = Describe(cached) with
                {
                    Detail = errors.Length == 0
                        ? $"Game data refreshed from {report.Endpoints.Count} endpoints."
                        : $"Local data remains available; {errors.Length} endpoint refreshes failed.",
                    Availability = errors.Length == report.Endpoints.Count
                        ? cached.ItemCount > 0 ? DataAvailability.Cached : DataAvailability.Error
                        : DataAvailability.Current,
                },
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            SetRefreshError("The background refresh exceeded its bounded timeout.");
            _logger.LogWarning("The game-data refresh exceeded {RefreshTimeout}.", _options.RefreshTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetRefreshError("The refresh failed; any existing local cache remains available.");
            _logger.LogError(exception, "The game-data refresh failed.");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_backgroundRefresh is not null)
        {
            try
            {
                await _backgroundRefresh.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _refreshLock.Dispose();
        _stopping.Dispose();
    }

    private RuntimeDataState Describe(CachedDataSnapshot cached)
    {
        if (_options.DemoMode)
        {
            return new(
                DataAvailability.DemoFixture,
                cached.ItemCount,
                cached.SyncedEndpointCount,
                cached.LastSuccessUtc,
                "Deterministic local demo fixtures are loaded.");
        }

        if (cached.ItemCount == 0)
        {
            return new(
                cached.LastError is null ? DataAvailability.Unavailable : DataAvailability.Error,
                0,
                cached.SyncedEndpointCount,
                cached.LastSuccessUtc,
                cached.LastError ?? "No local game data is available.");
        }

        if (_options.Offline)
        {
            return new(
                DataAvailability.Cached,
                cached.ItemCount,
                cached.SyncedEndpointCount,
                cached.LastSuccessUtc,
                "Offline mode is enabled; using the local game-data cache.");
        }

        var stale = cached.LastSuccessUtc is null || _timeProvider.GetUtcNow() - cached.LastSuccessUtc > _options.DataFreshFor;
        return new(
            stale ? DataAvailability.Cached : DataAvailability.Current,
            cached.ItemCount,
            cached.SyncedEndpointCount,
            cached.LastSuccessUtc,
            stale ? "Usable local data is loaded and marked stale." : "Usable local game data is loaded.");
    }

    private bool NeedsRefresh(RuntimeDataState data) =>
        data.ItemCount == 0 || data.UpdatedUtc is null || _timeProvider.GetUtcNow() - data.UpdatedUtc > _options.DataFreshFor;

    private void SetRefreshError(string detail) => _stateStore.Update(current => current with
    {
        Data = current.Data with
        {
            Availability = current.Data.ItemCount > 0 ? DataAvailability.Cached : DataAvailability.Error,
            Detail = detail,
        },
    });
}
