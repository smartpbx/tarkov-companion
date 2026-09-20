using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.LootSpawns;
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
    private readonly IHighValueLootRuntimeSource? _highValueLoot;
    private readonly IRequirementCatalog _requirementCatalog;
    private readonly IItemFactCatalog _itemFactCatalog;
    private readonly IReadOnlyList<IInvalidatableProjection> _projections;
    private readonly IRuntimeStateStore _stateStore;
    private readonly RuntimeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ApplicationStartupCoordinator> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _backgroundGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    // The one explicit "database is ready" gate every startup consumer must await before its
    // first query. LegacyProfileContextBootstrap and SettingsPageViewModel's retention load both
    // used to run their own database reads directly from App.axaml.cs / a view-model
    // constructor, racing InitializeDataStoreAsync's migrations on a fresh data folder — "no
    // such table: profile_workspaces" and an unobserved "no such table: retention_policies" on
    // 2026-09-17. TrySetResult/TrySetException here always run before anyone can be waiting on
    // it, because InitializeDataStoreAsync is the sole producer.
    private readonly TaskCompletionSource _databaseReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BackgroundWorkSupervisor _supervisor;
    private readonly FeatureLifecycleCoordinator _lifecycle;
    private Task<BackgroundWorkResult>? _backgroundRefresh;
    private readonly IProfileRuntimeContextService? _profileContext;
    // The mode and language the last refresh was *attempted* for (not the last that succeeded), so a
    // refresh that fails does not read as "scope changed" and loop; a manual Sync now retries.
    private volatile SyncScope? _lastAttemptedScope;
    private Task? _offlineModeMonitor;
    private bool _backgroundRefreshNeedsOnlineFollowup;
    private Task? _disposeTask;
    private bool _disposeStarted;

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
        IMapFeatureCatalog? mapFeatures = null,
        // Optional because fixture and headless compositions may intentionally omit the map
        // layer while the desktop composition always supplies the durable production source.
        IHighValueLootRuntimeSource? highValueLoot = null,
        // #269: the active profile decides which mode and language the catalog is fetched for.
        // Optional so a composition without profiles (tests, headless) keeps using RuntimeOptions.
        IProfileRuntimeContextService? profileContext = null)
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
        _highValueLoot = highValueLoot;
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
        _profileContext = profileContext;
        if (_profileContext is not null)
        {
            _profileContext.ContextChanged += OnProfileContextChanged;
        }
    }

    private readonly IOcrEngineStatus? _ocrStatus;
    private readonly GroupSessionService? _groupSession;

    /// <summary>
    /// Completes once migrations have been applied (or faults if they failed), so any startup
    /// consumer that reads or writes the database can await it before its first query instead of
    /// racing <see cref="InitializeAsync"/>. The wait is bounded by the caller's own token; it
    /// never cancels the shared migration work another awaiter may still be depending on.
    /// </summary>
    public Task DatabaseReadyAsync(CancellationToken cancellationToken) =>
        _databaseReady.Task.WaitAsync(cancellationToken);

    public Task? BackgroundRefresh
    {
        get
        {
            lock (_backgroundGate)
            {
                return _backgroundRefresh;
            }
        }
    }

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
        await _lifecycle.StartAsync(cancellationToken).ConfigureAwait(false);
        _stateStore.Update(current => current with
        {
            // Read while applying the store update. A timed-out start can settle between the
            // StartAsync await and this publication; writing its captured return value here
            // regressed the runtime state over that newer lifecycle notification.
            Lifecycle = _lifecycle.Snapshot,
            Outbox = _raidActivityCoordinator.OutboxSnapshot,
        });
        EnsureOfflineModeMonitor();
    }

    public void BeginBackgroundRefresh()
    {
        EnsureOfflineModeMonitor();
        var needsLootRefresh = _highValueLoot?.NeedsRefresh(
            _timeProvider.GetUtcNow().ToUniversalTime(),
            _options.DataFreshFor) == true;
        if (_options.DemoMode || _options.IsOffline ||
            (!NeedsRefresh(_stateStore.Current.Data) && !needsLootRefresh))
        {
            return;
        }

        lock (_backgroundGate)
        {
            if (_disposeStarted)
            {
                return;
            }

            if (_backgroundRefresh is { IsCompleted: false })
            {
                return;
            }

            _backgroundRefresh = StartRefreshUnsafe(force: true, WorkPriority.Background);
        }
    }

    /// <summary>Starts or joins the one supervised refresh and bounds only this caller's wait.</summary>
    /// <remarks>
    /// The wait is deliberately outside the refresh operation. A caller may stop waiting at its
    /// deadline, but the supervisor's completion remains incomplete, keeps its IO admission, and
    /// keeps the refresh semaphore alive until the dependency task itself returns. Passing a caller
    /// token into the shared operation instead would let one cancelled button press stop work that
    /// startup or another caller is already waiting for.
    /// </remarks>
    public async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOfflineModeMonitor();
        Task<BackgroundWorkResult> refresh;
        lock (_backgroundGate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            refresh = _backgroundRefresh is { IsCompleted: false } active
                ? active
                : _backgroundRefresh = StartRefreshUnsafe(force, WorkPriority.UserBlocking);
        }

        try
        {
            await refresh
                .WaitAsync(_options.RefreshTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The operation remains owned and its Running supervisor snapshot is the truthful
            // result until the dependency returns. RefreshWithFaultAsync publishes the eventual
            // timeout/cancellation state after that real completion.
            _logger.LogWarning(
                "The caller stopped waiting for the game-data refresh after {RefreshTimeout}; the supervised operation is still observed.",
                _options.RefreshTimeout);
        }
    }

    private Task<BackgroundWorkResult> StartRefreshUnsafe(bool force, WorkPriority priority)
    {
        // Remember the mode at admission, rather than at completion. If an offline refresh is
        // already draining cached responses when connectivity returns, the transition monitor
        // must follow it with one admitted online refresh that normalizes the newly fetched data.
        _backgroundRefreshNeedsOnlineFollowup = _options.IsOffline;
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
            new(execution, new("catalog-refresh"), priority),
            async (_, token) => await RefreshSupervisedAsync(force, token).ConfigureAwait(false));
        return admission.Handle.Completion;
    }

    private async Task RefreshSupervisedAsync(bool force, CancellationToken cancellationToken)
    {
        if (await RefreshWithFaultAsync(force, cancellationToken).ConfigureAwait(false) is { } fault)
        {
            throw new RuntimeFaultException(fault);
        }
    }

    private async Task<RuntimeFault?> RefreshWithFaultAsync(bool force, CancellationToken cancellationToken)
    {
        if (_options.IsOffline)
        {
            PublishOfflineState();
            return null;
        }

        using var deadline = new CancellationTokenSource(_options.RefreshTimeout, _timeProvider);
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        await _refreshLock.WaitAsync(operationCancellation.Token).ConfigureAwait(false);
        CachedDataSnapshot? cached = null;
        try
        {
            _stateStore.Update(current => current with
            {
                IsOffline = _options.IsOffline,
                Data = current.Data with { Availability = DataAvailability.Refreshing, Detail = "Refreshing stale game data in the background." },
            });

            var scope = await ResolveSyncScopeAsync(operationCancellation.Token).ConfigureAwait(false);
            if (scope is null)
            {
                // The active profile's mode is unknown. Fetching Regular data for it would put
                // another mode's items and quests under a profile that never chose them.
                PublishUnknownModeState();
                return null;
            }

            _lastAttemptedScope = scope;
            var report = await _dataSyncService.SyncAsync(
                    new(scope.Mode, scope.Language, force),
                    operationCancellation.Token)
                .ConfigureAwait(false);
            operationCancellation.Token.ThrowIfCancellationRequested();
            cached = await _dataStore.LoadSnapshotAsync(operationCancellation.Token).ConfigureAwait(false);
            operationCancellation.Token.ThrowIfCancellationRequested();

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

            await WarmCatalogsAsync(operationCancellation.Token).ConfigureAwait(false);
            operationCancellation.Token.ThrowIfCancellationRequested();

            if (_highValueLoot is not null)
            {
                // The shared sync has just refreshed maps and items. Reading its exact response
                // cache avoids a second forced download while still normalizing and publishing
                // the independently governed loot bundle.
                await _highValueLoot
                    .RefreshAsync(force: false, operationCancellation.Token)
                    .ConfigureAwait(false);
                operationCancellation.Token.ThrowIfCancellationRequested();
            }

            var errors = report.Endpoints.Where(endpoint => endpoint.Error is not null).ToArray();
            // "2 endpoint refresh(es) failed" with no names took a day to diagnose because
            // nothing had logged which two, or why. Every failure is now named here — the one
            // place every refresh path (startup, manual sync, reconnect) funnels through.
            foreach (var failed in errors)
            {
                _logger.LogWarning(
                    "The {Endpoint} game-data endpoint did not refresh: {Reason}",
                    failed.Endpoint,
                    failed.Error);
            }

            // The sync has always known when an endpoint answered from a stale cache instead
            // of refreshing, and has never said so. "Refreshed" while three endpoints served
            // yesterday's rows is a claim the player would act on.
            var stale = report.Endpoints.Count(endpoint => endpoint.UsedStaleCache);
            _stateStore.Update(current => current with
            {
                IsOffline = _options.IsOffline,
                Data = Describe(cached) with
                {
                    Detail = cached.ItemCount == 0
                        ? DescribeEmptyRefresh(errors)
                        : errors.Length == 0
                            ? stale == 0
                                ? $"Refreshed from {report.Endpoints.Count} endpoints"
                                : $"Refreshed from {report.Endpoints.Count} endpoints · {stale} served a cached copy"
                            : $"{DescribeEndpointFailures(errors)} · local data stands",
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
        catch (OperationCanceledException) when (
            deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            SetRefreshError(
                "The background refresh exceeded its bounded timeout.",
                cached?.ItemCount);
            _logger.LogWarning("The game-data refresh exceeded {RefreshTimeout}.", _options.RefreshTimeout);
            return new(
                RuntimeFailureKind.Timeout,
                new("data-refresh-timeout"),
                RuntimeRecoveryAction.RetryManually,
                new("feature:data-refresh"),
                _timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            SetRefreshError(
                deadline.IsCancellationRequested
                    ? "The background refresh exceeded its bounded timeout."
                    : "The refresh was stopped; any existing local cache remains available.",
                cached?.ItemCount);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await SetRefreshErrorFromStoreAsync(
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
    public ValueTask DisposeAsync()
    {
        lock (_backgroundGate)
        {
            _disposeStarted = true;
            // DisposeCoreAsync yields before touching any dependency, so no lifecycle callback
            // can run while this admission gate is held. Keeping the one task also makes repeated
            // and concurrent disposal await the same bounded shutdown.
            _disposeTask ??= DisposeCoreAsync();
            return new(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        _lifetime.Cancel();
        Task? offlineModeMonitor;
        lock (_backgroundGate)
        {
            offlineModeMonitor = _offlineModeMonitor;
        }

        // Both owners receive the same shutdown window. Waiting for one full bound before even
        // asking the other to stop turned two truthful ten-second waits into a twenty-second
        // application shutdown.
        var lifecycleStopping = _lifecycle.StopAsync();
        var supervisorStopping = _supervisor.StopAsync(TimeSpan.FromSeconds(10));
        await Task.WhenAll(lifecycleStopping, supervisorStopping).ConfigureAwait(false);
        var lifecycle = await lifecycleStopping.ConfigureAwait(false);
        var supervisor = await supervisorStopping.ConfigureAwait(false);
        if (offlineModeMonitor is not null)
        {
            await offlineModeMonitor.ConfigureAwait(false);
        }
        _stateStore.Update(current => current with
        {
            Lifecycle = _lifecycle.Snapshot,
            Supervisor = _supervisor.Snapshot,
            Outbox = _raidActivityCoordinator.OutboxSnapshot,
        });
        _supervisor.Changed -= PublishSupervisorState;
        _lifecycle.Changed -= PublishLifecycleState;
        _raidActivityCoordinator.OutboxChanged -= PublishOutboxState;
        if (_profileContext is not null)
        {
            _profileContext.ContextChanged -= OnProfileContextChanged;
        }

        if (supervisor.CompletedWithinDeadline)
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
        }

        // A timed-out supervisor still needs its cancelled synchronization objects when the
        // dependency eventually returns and releases its slot. Calling DisposeAsync here used to
        // wait a second RefreshTimeout after the already-bounded ten-second stop; leaving those
        // objects alive is both the truthful ownership model and the single shutdown bound.
        if (supervisor.CompletedWithinDeadline && lifecycle.IsQuiescent && _refreshLock.Wait(0))
        {
            // Held while disposing, so a caller arriving now fails as disposed rather than
            // entering a lock that is about to disappear under it.
            _refreshLock.Dispose();
        }

        _lifetime.Dispose();
    }

    /// <summary>Starts the process-lifetime observer that turns a reconnect into a full sync.</summary>
    /// <remarks>
    /// The JSON client can notice the same switch and refresh its raw cache, but it cannot own
    /// normalized tables, projection invalidation or runtime-state publication. This observer
    /// therefore admits one forced coordinator refresh on every offline-to-online edge. It does
    /// no I/O while the fixed offline setting remains enabled and never delays startup.
    /// </remarks>
    private void EnsureOfflineModeMonitor()
    {
        if (_options.DemoMode || _options.OfflineProbe is null)
        {
            return;
        }

        lock (_backgroundGate)
        {
            if (_disposeStarted || _offlineModeMonitor is { IsCompleted: false })
            {
                return;
            }

            _offlineModeMonitor = MonitorOfflineModeAsync(_lifetime.Token);
        }
    }

    private async Task MonitorOfflineModeAsync(CancellationToken cancellationToken)
    {
        var observedOffline = _stateStore.Current.IsOffline;
        try
        {
            while (true)
            {
                var offline = _options.IsOffline;
                if (offline != observedOffline)
                {
                    observedOffline = offline;
                    if (offline)
                    {
                        lock (_backgroundGate)
                        {
                            // A refresh admitted online can cross this edge between endpoints.
                            // Its mixed cached result is useful, but it is not the online refresh
                            // promised by the next reconnect.
                            if (_backgroundRefresh is { IsCompleted: false })
                            {
                                _backgroundRefreshNeedsOnlineFollowup = true;
                            }
                        }

                        PublishOfflineState();
                    }
                    else
                    {
                        _stateStore.Update(current => current with
                        {
                            IsOffline = false,
                            Data = current.Data with
                            {
                                Detail = "Connection restored; refreshing local game data.",
                            },
                        });
                        await RefreshAfterReconnectAsync(cancellationToken).ConfigureAwait(false);
                        observedOffline = _options.IsOffline;
                    }
                }

                var pollInterval = ValidOfflinePollInterval();
                await Task.Delay(
                        pollInterval,
                        _timeProvider,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal coordinator shutdown. The shared refresh, if any, remains owned by the
            // supervisor and is stopped through its existing bounded shutdown path.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The offline-mode transition monitor stopped unexpectedly.");
        }
    }

    private async Task RefreshAfterReconnectAsync(CancellationToken cancellationToken)
    {
        Task<BackgroundWorkResult>? refreshStartedOffline = null;
        Task<BackgroundWorkResult>? onlineRefresh = null;
        lock (_backgroundGate)
        {
            if (_disposeStarted)
            {
                return;
            }

            if (_backgroundRefresh is { IsCompleted: false } active)
            {
                if (_backgroundRefreshNeedsOnlineFollowup)
                {
                    refreshStartedOffline = active;
                }
                else
                {
                    onlineRefresh = active;
                }
            }
            else
            {
                onlineRefresh = _backgroundRefresh = StartRefreshUnsafe(
                    force: true,
                    WorkPriority.Background);
            }
        }

        if (refreshStartedOffline is not null)
        {
            await refreshStartedOffline.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (_options.IsOffline)
            {
                return;
            }

            lock (_backgroundGate)
            {
                if (_disposeStarted)
                {
                    return;
                }

                onlineRefresh = _backgroundRefresh is { IsCompleted: false } active
                    && !_backgroundRefreshNeedsOnlineFollowup
                    ? active
                    : _backgroundRefresh = StartRefreshUnsafe(force: true, WorkPriority.Background);
            }
        }

        if (onlineRefresh is not null)
        {
            await onlineRefresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private TimeSpan ValidOfflinePollInterval() =>
        _options.OfflineTransitionPollInterval > TimeSpan.Zero
            ? _options.OfflineTransitionPollInterval
            : TimeSpan.FromSeconds(1);

    /// <summary>The mode and language one catalog refresh is for.</summary>
    private sealed record SyncScope(GameMode Mode, string Language);

    /// <summary>
    /// What the catalog should be fetched for right now: the active profile's mode and language,
    /// <see cref="RuntimeOptions"/> when there is no profile at all (a V1 launch, or before the first
    /// profile exists), and nothing when the profile's mode is unknown.
    /// </summary>
    private async Task<SyncScope?> ResolveSyncScopeAsync(CancellationToken cancellationToken)
    {
        if (_profileContext is null)
        {
            return new(_options.GameMode, _options.Language);
        }

        var snapshot = _profileContext.Current;
        if (!snapshot.IsInitialized)
        {
            try
            {
                snapshot = await _profileContext.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Before the profile table exists, or with an unreadable workspace: the options
                // are the pre-profile behaviour, so the catalog still loads on a first launch.
                _logger.LogWarning(exception, "The profile context could not be read; refreshing with the runtime options.");
                return new(_options.GameMode, _options.Language);
            }
        }

        return ScopeOf(snapshot);
    }

    private SyncScope? ScopeOf(ProfileRuntimeContextSnapshot snapshot) => snapshot.State switch
    {
        ProfileRuntimeContextState.Ready when snapshot.CatalogScope is { } catalog =>
            new(catalog.GameMode, catalog.Language),
        ProfileRuntimeContextState.UnknownGameMode => null,
        _ => new(_options.GameMode, _options.Language),
    };

    /// <summary>
    /// A profile switch, create or restore committed: show the new profile's progress at once, and
    /// fetch the catalog again only if its mode or language is not the one last fetched.
    /// </summary>
    /// <remarks>
    /// The reaction is one supervised operation, so shutdown waits for it and its faults are
    /// recorded, rather than a detached task. It reads the *latest* context when it runs, not the
    /// one that raised the event: several quick switches collapse into whatever is active by then.
    /// </remarks>
    private void OnProfileContextChanged(ProfileRuntimeContextChanged change)
    {
        lock (_backgroundGate)
        {
            if (_disposeStarted)
            {
                return;
            }

            var execution = new OperationExecutionRequest(
                new("profile-change"),
                OperationId.New(),
                CorrelationId.New(),
                new("profile-context"),
                OperationPolicy.Once(
                    _options.RefreshTimeout,
                    WorkloadClass.Light,
                    OperationRestartMode.Manual,
                    IdempotencyRequirement.Guaranteed));
            _supervisor.Submit(
                new(execution, new("profile-change"), WorkPriority.UserBlocking),
                async (_, token) => await ApplyProfileChangeAsync(token).ConfigureAwait(false));
        }
    }

    private async Task ApplyProfileChangeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadProfileAsync(cancellationToken).ConfigureAwait(false);
            // A refresh already running may be for the scope before this switch. Joining it is not
            // enough, so look again once it ends; three looks bounds a player flipping back and forth.
            for (var pass = 0; pass < 3; pass++)
            {
                var scope = ScopeOf(_profileContext!.Current);
                if (scope is null)
                {
                    PublishUnknownModeState();
                    return;
                }

                if (_options.DemoMode || _options.IsOffline || scope == _lastAttemptedScope)
                {
                    return;
                }

                Task<BackgroundWorkResult> refresh;
                lock (_backgroundGate)
                {
                    if (_disposeStarted)
                    {
                        return;
                    }

                    refresh = _backgroundRefresh is { IsCompleted: false } active
                        ? active
                        : _backgroundRefresh = StartRefreshUnsafe(force: true, WorkPriority.UserBlocking);
                }

                await refresh
                    .WaitAsync(_options.RefreshTimeout, _timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or TimeoutException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The profile change could not be applied to the runtime state.");
        }
    }

    private void PublishUnknownModeState()
    {
        _stateStore.Update(current => current with
        {
            Data = current.Data with
            {
                Availability = current.Data.ItemCount > 0
                    ? DataAvailability.Cached
                    : DataAvailability.Unavailable,
                Detail = "This profile has no game mode. Choose PvP, PvE or Seasonal to load game data.",
            },
        });
    }

    private void PublishOfflineState()
    {
        _stateStore.Update(current => current with
        {
            IsOffline = true,
            Data = current.Data with
            {
                Availability = current.Data.ItemCount > 0
                    ? DataAvailability.Cached
                    : DataAvailability.Unavailable,
                Detail = current.Data.ItemCount > 0
                    ? "Offline mode is enabled; using the local game-data cache."
                    : "Offline, and no local game data",
            },
        });
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

        if (_options.IsOffline)
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
        cancellationToken.ThrowIfCancellationRequested();
        var hideout = await _requirementCatalog.GetHideoutRequirementsAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _needAggregation.Update(quest, hideout);

        // The game names the map in its log with an internal token; upstream publishes
        // that token beside the map id, so the parser learns the pairing instead of
        // carrying a hand-written table of guesses.
        var aliases = await _mapAliasCatalog.GetAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _logParser.UpdateAliases(aliases);
    }

    private static string DescribeEmptyRefresh(IReadOnlyList<SyncEndpointResult> errors)
    {
        if (errors.Count == 0)
        {
            return "The refresh reported success but no game items were stored.";
        }

        return $"No game items are available; {DescribeEndpointFailures(errors)}.";
    }

    /// <summary>
    /// Names the endpoint and reason for every failed refresh, short enough for a one-line
    /// banner. "2 endpoint refresh(es) failed" with no names left this unreproducible for a day.
    /// </summary>
    private static string DescribeEndpointFailures(IReadOnlyList<SyncEndpointResult> errors) =>
        string.Join("; ", errors.Select(error => $"{error.Endpoint}: {ShortenReason(error.Error)}"));

    private static string ShortenReason(string? reason)
    {
        const int maximumLength = 120;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "unknown reason";
        }

        var firstLine = reason.Split('\n', 2)[0].Trim();
        return firstLine.Length <= maximumLength ? firstLine : firstLine[..maximumLength] + "…";
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
    private void SetRefreshError(string detail, int? observedItemCount = null)
    {
        _stateStore.Update(current =>
        {
            var itemCount = observedItemCount ?? current.Data.ItemCount;
            return current with
            {
                Data = current.Data with
                {
                    Availability = itemCount > 0 ? DataAvailability.Cached : DataAvailability.Error,
                    ItemCount = itemCount,
                    Detail = detail,
                },
            };
        });
    }

    private async Task SetRefreshErrorFromStoreAsync(string detail, CancellationToken cancellationToken)
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

        SetRefreshError(detail, itemCount);
    }

    private IReadOnlyList<RuntimeFeatureDefinition> BuildFeatureGraph()
    {
        var database = new RuntimeFeatureId("database");
        var cachedData = new RuntimeFeatureId("cached-data");
        var profile = new RuntimeFeatureId("profile");
        var raidHistoryRepair = new RuntimeFeatureId("raid-history-repair");
        var features = new List<RuntimeFeatureDefinition>
        {
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
                // Repair reads and writes raid-history tables. It cannot race schema creation:
                // a missing table is a repair failure, not evidence that there was nothing to do.
                [new(database, FeatureDependencyKind.Hard)],
                _raidActivityCoordinator.CloseAbandonedAsync),
            new(
                new("observation"),
                FeatureStartupPriority.WorkspaceCritical,
                // Observation can proceed without a successful repair, but both services touch
                // schema-bound history. Database initialization is therefore a hard gate while
                // repair remains optional and only degrades the watcher if it fails.
                [
                    new(database, FeatureDependencyKind.Hard),
                    new(raidHistoryRepair, FeatureDependencyKind.Optional),
                ],
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
        };
        if (_highValueLoot is not null)
        {
            features.Add(new(
                new("high-value-loot-source"),
                FeatureStartupPriority.Normal,
                [],
                cancellationToken => _highValueLoot.InitializeAsync(cancellationToken).AsTask()));
        }

        return features;
    }

    private async Task InitializeDataStoreAsync(CancellationToken cancellationToken)
    {
        try
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
            _databaseReady.TrySetResult();
        }
        catch (Exception exception)
        {
            // Every awaiter of DatabaseReadyAsync needs to hear about this, not just the
            // lifecycle graph's own fault handling for the "database" feature.
            _databaseReady.TrySetException(exception);
            throw;
        }
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
