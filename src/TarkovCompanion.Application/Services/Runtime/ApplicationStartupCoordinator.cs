using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Execution;
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
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _backgroundGate = new();
    private readonly BackgroundWorkSupervisor _supervisor;
    private readonly FeatureLifecycleCoordinator _lifecycle;
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
        _supervisor = new(
            _timeProvider,
            new(
                capacity: 16,
                reservedInteractiveAdmission: 2,
                maxConcurrent: 4,
                lightLimit: 4,
                ioLimit: 2,
                cpuLimit: 1,
                maxPriorityBurst: 6,
                terminalHistoryLimit: 64,
                defaultStopTimeout: options.RefreshTimeout));
        _lifecycle = new(
            BuildFeatureGraph(),
            _timeProvider,
            new(
                maxParallelStarts: 3,
                startTimeout: options.RefreshTimeout,
                stopTimeout: TimeSpan.FromSeconds(10)));
        _supervisor.Changed += PublishSupervisorState;
        _lifecycle.Changed += PublishLifecycleState;
        _raidActivityCoordinator.OutboxChanged += PublishOutboxState;
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
            "The {Provider} recogniser is {State}.",
            availability.Provider,
            availability.IsAvailable ? "available" : "unavailable");
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
        var snapshot = await _lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);
        _stateStore.Update(current => current with
        {
            Lifecycle = snapshot,
            Outbox = _raidActivityCoordinator.OutboxSnapshot,
        });
    }

    public void BeginBackgroundRefresh()
    {
        if (_options.DemoMode || _options.Offline || !NeedsRefresh(_stateStore.Current.Data))
        {
            return;
        }

        lock (_backgroundGate)
        {
            if (_backgroundRefresh is { IsCompleted: false })
            {
                return;
            }

            var operationId = OperationId.New();
            var execution = new OperationExecutionRequest(
                new("data-refresh"),
                operationId,
                CorrelationId.New(),
                new("json-tarkov-dev"),
                OperationPolicy.Once(
                    _options.RefreshTimeout,
                    WorkloadClass.IO,
                    OperationRestartMode.Manual,
                    IdempotencyRequirement.Guaranteed));
            var admission = _supervisor.Submit(
                new(execution, new("catalog-refresh"), WorkPriority.Background),
                async (_, token) => await RefreshSupervisedAsync(force: true, token).ConfigureAwait(false));
            _backgroundRefresh = admission.Handle.Completion;
        }
    }

    public async Task RefreshAsync(bool force, CancellationToken cancellationToken) =>
        await RefreshWithFaultAsync(force, cancellationToken).ConfigureAwait(false);

    private async Task RefreshSupervisedAsync(bool force, CancellationToken cancellationToken)
    {
        if (await RefreshWithFaultAsync(force, cancellationToken).ConfigureAwait(false) is { } fault)
        {
            throw new RuntimeFaultException(fault);
        }
    }

    private async Task<RuntimeFault?> RefreshWithFaultAsync(bool force, CancellationToken cancellationToken)
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
            return null;
        }

        using var deadline = new CancellationTokenSource(_options.RefreshTimeout, _timeProvider);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        await _refreshLock.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        try
        {
            _stateStore.Update(current => current with
            {
                Data = current.Data with { Availability = DataAvailability.Refreshing, Detail = "Refreshing stale game data in the background." },
            });

            var report = await _dataSyncService.SyncAsync(
                    new(_options.GameMode, _options.Language, force),
                    operationCancellation.Token)
                .WaitAsync(operationCancellation.Token)
                .ConfigureAwait(false);
            var cached = await _dataStore.LoadSnapshotAsync(operationCancellation.Token)
                .WaitAsync(operationCancellation.Token)
                .ConfigureAwait(false);

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

            await WarmCatalogsAsync(operationCancellation.Token)
                .WaitAsync(operationCancellation.Token)
                .ConfigureAwait(false);

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
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await SetRefreshErrorAsync(
                "The background refresh exceeded its bounded timeout.",
                cancellationToken).ConfigureAwait(false);
            _logger.LogWarning("The game-data refresh exceeded {RefreshTimeout}.", _options.RefreshTimeout);
            return new(
                RuntimeFailureKind.Timeout,
                new("data-refresh-timeout"),
                RuntimeRecoveryAction.RetryManually,
                new("feature:data-refresh"),
                _timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await SetRefreshErrorAsync(
                "The refresh failed; any existing local cache remains available.",
                cancellationToken).ConfigureAwait(false);
            _logger.LogError("The game-data refresh failed; a sanitized runtime state was published.");
            return RuntimeFault.FromException(
                exception,
                _timeProvider,
                new("feature:data-refresh"));
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Stops features and supervised work, then releases what nothing still owns.</summary>
    /// <remarks>
    /// This used to unsubscribe from lifecycle and supervisor state before stopping them, so the
    /// shutdown itself was never published, and it disposed the refresh lock unconditionally —
    /// under a refresh that had ignored cancellation and would release that lock when it
    /// finally returned. A stop that runs out of time now leaves the lock alive for that late
    /// release, and the published state says which work is still unfinished.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        var lifecycle = await _lifecycle.StopAsync().ConfigureAwait(false);
        var supervisor = await _supervisor.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        _stateStore.Update(current => current with
        {
            Lifecycle = _lifecycle.Snapshot,
            Supervisor = _supervisor.Snapshot,
            Outbox = _raidActivityCoordinator.OutboxSnapshot,
        });
        _supervisor.Changed -= PublishSupervisorState;
        _lifecycle.Changed -= PublishLifecycleState;
        _raidActivityCoordinator.OutboxChanged -= PublishOutboxState;
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        if (supervisor.CompletedWithinDeadline && lifecycle.IsQuiescent && _refreshLock.Wait(0))
        {
            // Held while disposing, so a caller arriving now fails as disposed rather than
            // entering a lock that is about to disappear under it.
            _refreshLock.Dispose();
        }
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
                cached.LastError is null
                    ? "No local game data"
                    : "The last local game-data operation failed.");
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
        var quest = await _requirementCatalog.GetQuestRequirementsAsync(cancellationToken).ConfigureAwait(false);
        var hideout = await _requirementCatalog.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(false);
        _needAggregation.Update(quest, hideout);

        // The game names the map in its log with an internal token; upstream publishes
        // that token beside the map id, so the parser learns the pairing instead of
        // carrying a hand-written table of guesses.
        _logParser.UpdateAliases(await _mapAliasCatalog.GetAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string DescribeEmptyRefresh(IReadOnlyList<SyncEndpointResult> errors)
    {
        if (errors.Count == 0)
        {
            return "The refresh reported success but no game items were stored.";
        }

        return $"No game items are available; {errors.Count} endpoint refresh(es) failed.";
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
            _logger.LogWarning("The post-failure data snapshot could not be read.");
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

    private IReadOnlyList<RuntimeFeatureDefinition> BuildFeatureGraph()
    {
        var database = new RuntimeFeatureId("database");
        var cachedData = new RuntimeFeatureId("cached-data");
        var profile = new RuntimeFeatureId("profile");
        var raidHistoryRepair = new RuntimeFeatureId("raid-history-repair");
        return
        [
            new(
                database,
                FeatureStartupPriority.WorkspaceCritical,
                [],
                InitializeDataStoreAsync),
            new(
                cachedData,
                FeatureStartupPriority.WorkspaceCritical,
                [new(database, FeatureDependencyKind.Hard)],
                LoadCachedDataAsync),
            new(
                profile,
                FeatureStartupPriority.WorkspaceCritical,
                [new(database, FeatureDependencyKind.Hard)],
                LoadProfileAsync),
            new(
                new("scanner"),
                FeatureStartupPriority.WorkspaceCritical,
                [],
                InitializeScannerAsync),
            new(
                raidHistoryRepair,
                FeatureStartupPriority.WorkspaceCritical,
                [new(database, FeatureDependencyKind.Optional)],
                _raidActivityCoordinator.CloseAbandonedAsync),
            new(
                new("observation"),
                FeatureStartupPriority.WorkspaceCritical,
                // Repair improves the record but observation is the irreplaceable input. A
                // failed or timed-out repair therefore degrades observation; it must never
                // prevent the watcher from starting.
                [new(raidHistoryRepair, FeatureDependencyKind.Optional)],
                StartObservationAsync,
                _ => _observationService.DisposeAsync().AsTask()),
            new(
                new("demo-raid"),
                FeatureStartupPriority.Normal,
                [new(cachedData, FeatureDependencyKind.Hard)],
                InitializeDemoRaidAsync),
            new(
                new("catalog-warmup"),
                FeatureStartupPriority.Normal,
                [new(cachedData, FeatureDependencyKind.Optional)],
                WarmCatalogsAsync),
            new(
                new("group-session"),
                FeatureStartupPriority.Normal,
                [],
                StartGroupSessionAsync),
        ];
    }

    private async Task InitializeDataStoreAsync(CancellationToken cancellationToken)
    {
        await _dataStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_options.DemoMode)
        {
            await _dataStore.SeedDemoAsync(cancellationToken).ConfigureAwait(false);
        }

        // Loading one projection may still fail independently. The database itself became
        // ready when initialization (and the optional fixture seed) completed, so preserve
        // that measured fact instead of letting an unrelated reader report it unavailable.
        _stateStore.Update(current => current with { DatabaseReady = true });
    }

    private async Task LoadCachedDataAsync(CancellationToken cancellationToken)
    {
        var cached = await _dataStore.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        _stateStore.Update(current => current with
        {
            Data = Describe(cached),
        });
    }

    private async Task LoadProfileAsync(CancellationToken cancellationToken)
    {
        var profile = await _profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        _stateStore.Update(current => current with { Profile = profile });
    }

    private Task InitializeScannerAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stateStore.Update(current => current with { Scan = DescribeScanner() });
        return Task.CompletedTask;
    }

    private async Task InitializeDemoRaidAsync(CancellationToken cancellationToken)
    {
        if (!_options.DemoMode)
        {
            return;
        }

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

    private Task StartObservationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _observationService.Start();
        return Task.CompletedTask;
    }

    private Task StartGroupSessionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _groupSession?.Start();
        return Task.CompletedTask;
    }

    private void PublishSupervisorState(object? sender, EventArgs eventArgs) =>
        _stateStore.Update(current => current with { Supervisor = _supervisor.Snapshot });

    private void PublishLifecycleState(object? sender, EventArgs eventArgs) =>
        _stateStore.Update(current => current with { Lifecycle = _lifecycle.Snapshot });

    private void PublishOutboxState(object? sender, EventArgs eventArgs) =>
        _stateStore.Update(current => current with { Outbox = _raidActivityCoordinator.OutboxSnapshot });
}
