using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Runtime;

public sealed class ApplicationStartupCoordinator : IAsyncDisposable
{
    private readonly IRuntimeDataStore _dataStore;
    private readonly IDataSyncService _dataSyncService;
    private readonly IPlayerProfileService _profileService;
    private readonly RaidActivityCoordinator _raidActivityCoordinator;
    private readonly RaidObservationService _observationService;
    private readonly ProfileNeedAggregationService _needAggregation;
    private readonly EftLogParser _logParser;
    private readonly IMapAliasCatalog _mapAliasCatalog;
    private readonly IMapDefinitionCache? _mapDefinitions;
    private readonly IMapFeatureCatalog? _mapFeatures;
    private readonly IRequirementCatalog _requirementCatalog;
    private readonly IItemFactCatalog _itemFactCatalog;
    private readonly IReadOnlyList<IInvalidatableProjection> _projections;
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
        RaidObservationService observationService,
        ProfileNeedAggregationService needAggregation,
        EftLogParser logParser,
        IMapAliasCatalog mapAliasCatalog,
        IRequirementCatalog requirementCatalog,
        IItemFactCatalog itemFactCatalog,
        IRuntimeStateStore stateStore,
        // Everything else built once from the item catalog. Registered as a collection so a
        // new one is invalidated by being registered rather than by somebody remembering to
        // add a line here — which is how the recognition resolver came to be missed.
        IEnumerable<IInvalidatableProjection> projections,
        RuntimeOptions options,
        ILogger<ApplicationStartupCoordinator> logger,
        IOcrEngineStatus? ocrStatus = null,
        // Optional so every test that builds this by hand keeps compiling, and so a
        // composition without sharing is a valid composition rather than a broken one.
        GroupSessionService? groupSession = null,
        TimeProvider? timeProvider = null,
        // Optional for the same reason, and because a composition that reads maps out of a
        // fixture rather than the database is still a valid composition.
        IMapDefinitionCache? mapDefinitions = null,
        IMapFeatureCatalog? mapFeatures = null)
    {
        _dataStore = dataStore;
        _dataSyncService = dataSyncService;
        _profileService = profileService;
        _raidActivityCoordinator = raidActivityCoordinator;
        _observationService = observationService;
        _groupSession = groupSession;
        _needAggregation = needAggregation;
        _logParser = logParser;
        _mapAliasCatalog = mapAliasCatalog;
        _mapDefinitions = mapDefinitions;
        _mapFeatures = mapFeatures;
        _requirementCatalog = requirementCatalog;
        _projections = projections.ToArray();
        _itemFactCatalog = itemFactCatalog;
        _stateStore = stateStore;
        _options = options;
        _logger = logger;
        _ocrStatus = ocrStatus;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private readonly IOcrEngineStatus? _ocrStatus;
    private readonly GroupSessionService? _groupSession;

    public Task? BackgroundRefresh => _backgroundRefresh;

    /// <summary>
    /// Says whether scanning can run at all, before anyone has tried it.
    /// </summary>
    /// <remarks>
    /// The scanner used to start life described as unavailable, on the grounds that nothing
    /// had been scanned yet. Those are not the same thing, and the interface said the scanner
    /// was unavailable from the moment the application opened whether it worked or not, so a
    /// player with a perfectly good scanner and a player missing its native dependency were
    /// shown the same word. The engine knows which it is, and knows why, so it is asked.
    ///
    /// The reason matters more than the verdict. The recogniser's most likely failure is a
    /// missing Visual C++ runtime, which the package does not carry, and that is something a
    /// player can fix in two minutes if anybody tells them.
    /// </remarks>
    private ScanExecutionResult DescribeScanner()
    {
        var now = _timeProvider.GetUtcNow();
        if (_ocrStatus is null)
        {
            return ScanExecutionResult.Unavailable(
                "Scanning is not available on this platform.",
                now);
        }

        var availability = _ocrStatus.Availability;
        _logger.LogInformation(
            "The {Provider} recogniser is {State}. {Reason}",
            availability.Provider,
            availability.IsAvailable ? "available" : "unavailable",
            availability.Reason ?? "No reason was reported.");
        return availability.IsAvailable
            ? ScanExecutionResult.Ready(
                "Ready. Take a screenshot with the game's own key and it will be read.",
                now)
            : ScanExecutionResult.Unavailable(
                availability.Reason is { } reason
                    ? $"Scanning cannot run: {reason}"
                    : $"Scanning cannot run; {availability.Provider} did not start.",
                now);
    }

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
            Scan = DescribeScanner(),
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

        await WarmCatalogsAsync(cancellationToken).ConfigureAwait(false);

        // Before observation starts, so it can never race the raid the watcher is about to
        // recover. It closes only rows no raid could still be; anything recent enough to be
        // resumed is left for the resume to decide about.
        await _raidActivityCoordinator.CloseAbandonedAsync(cancellationToken).ConfigureAwait(false);

        // Watching the game's own log and screenshot folders is what lets the map follow the
        // player. It starts here rather than on demand because the game is usually launched
        // after the companion, and discovery keeps retrying until it appears.
        _observationService.Start();
        // Started unconditionally, and does nothing at all until the player has turned sharing
        // on. Starting it only when enabled would mean a restart to begin sharing, and the
        // service's own first act is to check whether it should send anything.
        _groupSession?.Start();
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
                        : "Offline, and no local game data",
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

            // Fresh rows landed, so anything projected from the old ones is now stale.
            _requirementCatalog.Invalidate();
            _itemFactCatalog.Invalidate();
            _mapAliasCatalog.Invalidate();
            _mapDefinitions?.Invalidate();
            _mapFeatures?.Invalidate();
            foreach (var projection in _projections)
            {
                projection.Invalidate();
            }

            await WarmCatalogsAsync(timeout.Token).ConfigureAwait(false);

            var errors = report.Endpoints.Where(endpoint => endpoint.Error is not null).ToArray();
            // The sync has always known when an endpoint answered from a stale cache instead
            // of refreshing, and has never said so. "Refreshed" while three endpoints served
            // yesterday's rows is a claim the player would act on.
            var stale = report.Endpoints.Count(endpoint => endpoint.UsedStaleCache);
            _stateStore.Update(current => current with
            {
                Data = Describe(cached) with
                {
                    Detail = cached.ItemCount == 0
                        ? DescribeEmptyRefresh(errors)
                        : errors.Length == 0
                            ? stale == 0
                                ? $"Refreshed from {report.Endpoints.Count} endpoints"
                                : $"Refreshed from {report.Endpoints.Count} endpoints · {stale} served a cached copy"
                            : $"{errors.Length} of {report.Endpoints.Count} endpoints failed · local data stands",
                    // A refresh that reports "Current" while the item catalog is empty is a
                    // false claim about the data the user is looking at. Partial success only
                    // counts as current when something actually landed.
                    Availability = cached.ItemCount == 0
                        ? DataAvailability.Error
                        : errors.Length == report.Endpoints.Count
                            ? DataAvailability.Cached
                            : DataAvailability.Current,
                },
            });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await SetRefreshErrorAsync(
                "The background refresh exceeded its bounded timeout.",
                cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("The game-data refresh exceeded {RefreshTimeout}.", _options.RefreshTimeout);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await SetRefreshErrorAsync(
                "The refresh failed; any existing local cache remains available.",
                cancellationToken).ConfigureAwait(false);
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
        await _observationService.DisposeAsync().ConfigureAwait(false);
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
                cached.LastError ?? "No local game data");
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

    /// <summary>
    /// Reads the requirements and hands them to the service that answers from them.
    /// </summary>
    /// <remarks>
    /// This runs at startup and again after every completed refresh, so the scanner's quest
    /// and hideout reasons reflect the rows currently on disk without any caller ever
    /// blocking on a database read.
    /// </remarks>
    private async Task WarmCatalogsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var quest = await _requirementCatalog.GetQuestRequirementsAsync(cancellationToken).ConfigureAwait(false);
            var hideout = await _requirementCatalog.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(false);
            _needAggregation.Update(quest, hideout);

            // The game names the map in its log with an internal token; upstream publishes
            // that token beside the map id, so the parser learns the pairing instead of
            // carrying a hand-written table of guesses.
            _logParser.UpdateAliases(await _mapAliasCatalog.GetAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The requirement catalog could not be preloaded.");
        }
    }

    private static string DescribeEmptyRefresh(IReadOnlyList<SyncEndpointResult> errors)
    {
        if (errors.Count == 0)
        {
            return "The refresh reported success but no game items were stored.";
        }

        var named = errors.Select(endpoint => $"{endpoint.Endpoint} ({endpoint.Error})");
        return $"No game items are available; {errors.Count} endpoint refresh(es) failed: {string.Join("; ", named)}";
    }

    private bool NeedsRefresh(RuntimeDataState data) =>
        data.ItemCount == 0 || data.UpdatedUtc is null || _timeProvider.GetUtcNow() - data.UpdatedUtc > _options.DataFreshFor;

    /// <summary>
    /// Records a failed refresh against what the database actually holds.
    /// </summary>
    /// <remarks>
    /// Endpoints commit as they arrive, so a refresh that fails part way through can still
    /// have stored items. Reading the item count from the pre-refresh state reported zero and
    /// left item search disabled until the next restart even though the data was on disk.
    /// </remarks>
    private async Task SetRefreshErrorAsync(string detail, CancellationToken cancellationToken)
    {
        var itemCount = _stateStore.Current.Data.ItemCount;
        try
        {
            var cached = await _dataStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            itemCount = cached.ItemCount;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The post-failure data snapshot could not be read.");
        }

        _stateStore.Update(current => current with
        {
            Data = current.Data with
            {
                Availability = itemCount > 0 ? DataAvailability.Cached : DataAvailability.Error,
                ItemCount = itemCount,
                Detail = detail,
            },
        });
    }
}
