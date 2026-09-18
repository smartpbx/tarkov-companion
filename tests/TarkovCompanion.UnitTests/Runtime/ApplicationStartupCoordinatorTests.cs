using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Intelligence;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Ammo;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Runtime;

public sealed class ApplicationStartupCoordinatorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RefreshDependency.Synchronization)]
    [InlineData(RefreshDependency.Snapshot)]
    [InlineData(RefreshDependency.QuestRequirements)]
    [InlineData(RefreshDependency.HideoutRequirements)]
    [InlineData(RefreshDependency.MapAliases)]
    public async Task ADeadlineKeepsEveryUncooperativeRefreshDependencyOwnedUntilItReturns(
        RefreshDependency dependency)
    {
        await using var fixture = new RefreshFixture(dependency, TimeSpan.FromSeconds(1));
        fixture.Coordinator.BeginBackgroundRefresh();
        await fixture.Control.Started.Task;
        var ownedRefresh = fixture.Coordinator.BackgroundRefresh!;

        fixture.Coordinator.BeginBackgroundRefresh();
        var manualWait = fixture.Coordinator.RefreshAsync(force: true, default);
        await RuntimeTestTasks.AdvanceUntilAsync(
            fixture.Time,
            TimeSpan.FromSeconds(1),
            () => manualWait.IsCompleted
                && fixture.State.Current.Supervisor.Operations.Any(operation =>
                    operation.LastFault?.Code.Value == "operation-attempt-timeout"));
        await manualWait;
        try
        {
            Assert.Same(ownedRefresh, fixture.Coordinator.BackgroundRefresh);
            Assert.False(ownedRefresh.IsCompleted);
            Assert.Equal(1, fixture.Sync.Calls);
            Assert.Equal(1, fixture.State.Current.Supervisor.Resources.RunningIO);
            var running = Assert.Single(fixture.State.Current.Supervisor.Operations);
            Assert.Equal(BackgroundWorkState.Running, running.State);
            Assert.Null(running.CompletedUtc);
        }
        finally
        {
            // A failed pre-release assertion must not strand the deliberately uncooperative
            // dependency and turn a useful failure into a hung test process.
            fixture.Control.Release();
        }

        await ownedRefresh;
        await RuntimeTestTasks.UntilAsync(() =>
            fixture.State.Current.Supervisor.Operations.Any(operation =>
                operation.State == BackgroundWorkState.TimedOut));

        var completed = Assert.Single(fixture.State.Current.Supervisor.Operations);
        Assert.Equal(BackgroundWorkState.TimedOut, completed.State);
        Assert.Equal(0, fixture.State.Current.Supervisor.Resources.RunningIO);
        Assert.Equal(
            dependency == RefreshDependency.Synchronization
                ? DataAvailability.Error
                : DataAvailability.Cached,
            fixture.State.Current.Data.Availability);
    }

    [Fact]
    public async Task DisposeUsesOneBoundAndReportsAnUnfinishedRefreshUntilLateCompletion()
    {
        var fixture = new RefreshFixture(RefreshDependency.Synchronization, TimeSpan.FromMinutes(5));
        fixture.Coordinator.BeginBackgroundRefresh();
        await fixture.Control.Started.Task;
        var ownedRefresh = fixture.Coordinator.BackgroundRefresh!;

        var disposing = fixture.Coordinator.DisposeAsync().AsTask();
        try
        {
            var repeatedDispose = fixture.Coordinator.DisposeAsync().AsTask();
            Assert.Same(disposing, repeatedDispose);
            await RuntimeTestTasks.UntilAsync(() =>
                fixture.State.Current.Supervisor.IsStopping
                && fixture.Control.ReceivedToken.IsCancellationRequested
                && fixture.State.Current.Supervisor.Operations.Any(operation =>
                    operation.LastFault?.Code.Value == "operation-cancelled"));

            await RuntimeTestTasks.AdvanceUntilAsync(
                fixture.Time,
                TimeSpan.FromSeconds(10),
                () => disposing.IsCompleted);
            await disposing;

            Assert.False(ownedRefresh.IsCompleted);
            Assert.Equal(1, fixture.State.Current.Supervisor.Resources.RunningIO);
            var unfinished = Assert.Single(fixture.State.Current.Supervisor.Operations);
            Assert.Equal(BackgroundWorkState.Running, unfinished.State);
            Assert.Null(unfinished.CompletedUtc);
            Assert.Equal("supervisor-stop-timeout", unfinished.LastFault?.Code.Value);
        }
        finally
        {
            // Dispose returned without destroying the semaphore or supervisor state still owned
            // by the late dependency. Releasing it must complete normally, not surface an ODE
            // from the refresh finally block or a disposed supervisor signal.
            fixture.Control.Release();
        }

        await ownedRefresh;
        Assert.True(disposing.IsCompletedSuccessfully);
        Assert.Same(disposing, fixture.Coordinator.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task NormalManualRefreshRunsOnceThroughTheSupervisorAndWarmsCatalogs()
    {
        await using var fixture = new RefreshFixture(RefreshDependency.None, TimeSpan.FromSeconds(5));

        await fixture.Coordinator.RefreshAsync(force: true, default);
        await RuntimeTestTasks.UntilAsync(() =>
            fixture.State.Current.Supervisor.Operations.Any(operation =>
                operation.State == BackgroundWorkState.Succeeded));

        Assert.Equal(1, fixture.Sync.Calls);
        Assert.Equal(1, fixture.DataStore.SnapshotCalls);
        Assert.Equal(1, fixture.Catalogs.QuestCalls);
        Assert.Equal(1, fixture.Catalogs.HideoutCalls);
        Assert.Equal(1, fixture.Catalogs.AliasCalls);
        Assert.Equal(2, fixture.Catalogs.Invalidations);
        Assert.Equal(1, fixture.ItemFacts.Invalidations);
        Assert.Equal(1, fixture.Projection.Invalidations);
        Assert.Equal(DataAvailability.Current, fixture.State.Current.Data.Availability);
        Assert.Equal(3, fixture.State.Current.Data.ItemCount);
    }

    [Fact]
    public async Task Missing_loot_head_loads_at_startup_and_admits_one_cache_reusing_refresh()
    {
        var loot = new RecordingHighValueLootRuntimeSource { NeedsRefreshResult = true };
        await using var fixture = new RefreshFixture(
            RefreshDependency.None,
            TimeSpan.FromSeconds(5),
            highValueLoot: loot);

        await fixture.Coordinator.InitializeAsync(default);
        fixture.Coordinator.BeginBackgroundRefresh();
        await fixture.Coordinator.BackgroundRefresh!;

        Assert.Equal(1, loot.InitializeCalls);
        Assert.Equal(1, loot.RefreshCalls);
        Assert.False(loot.LastForce);
        Assert.Equal(1, fixture.Sync.Calls);
        Assert.Equal(
            FeatureLifecycleState.Running,
            Assert.Single(
                fixture.State.Current.Lifecycle.Features,
                feature => feature.FeatureId == new RuntimeFeatureId("high-value-loot-source")).State);
    }

    [Fact]
    public async Task OfflineToOnlineEdgeRunsTheFullCoordinatorRefreshAndPublishesTheNewMode()
    {
        var offline = 1;
        await using var fixture = new RefreshFixture(
            RefreshDependency.None,
            TimeSpan.FromSeconds(5),
            offline: true,
            offlineProbe: () => Volatile.Read(ref offline) == 1);

        await fixture.Coordinator.InitializeAsync(default);
        fixture.Coordinator.BeginBackgroundRefresh();

        Assert.True(fixture.State.Current.IsOffline);
        Assert.Equal(DataAvailability.Cached, fixture.State.Current.Data.Availability);
        Assert.Equal(0, fixture.Sync.Calls);
        Assert.Null(fixture.Coordinator.BackgroundRefresh);
        Assert.True(fixture.Time.ScheduledTimerCount > 0);

        Volatile.Write(ref offline, 0);
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await RuntimeTestTasks.UntilAsync(
            () => fixture.Sync.Calls == 1
                && fixture.State.Current.Data.Availability == DataAvailability.Current);

        var snapshot = fixture.State.Current;
        Assert.False(snapshot.IsOffline);
        Assert.Equal(3, snapshot.Data.ItemCount);
        Assert.Equal(2, fixture.DataStore.SnapshotCalls);
        Assert.Equal(1, fixture.ItemFacts.Invalidations);
        Assert.Equal(1, fixture.Projection.Invalidations);
    }

    [Fact]
    public async Task FixedOfflineModeDoesNotStartSyncWhileItsMonitorRemainsCleanlyDisposable()
    {
        var fixture = new RefreshFixture(
            RefreshDependency.None,
            TimeSpan.FromSeconds(5),
            offline: true,
            offlineProbe: static () => true);

        await fixture.Coordinator.InitializeAsync(default);
        fixture.Coordinator.BeginBackgroundRefresh();
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        await RuntimeTestTasks.DrainAsync();

        Assert.True(fixture.State.Current.IsOffline);
        Assert.Equal(DataAvailability.Cached, fixture.State.Current.Data.Availability);
        Assert.Equal(0, fixture.Sync.Calls);
        Assert.Null(fixture.Coordinator.BackgroundRefresh);

        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task CancellingOneManualWaitDoesNotOrphanOrCancelTheSharedRefresh()
    {
        await using var fixture = new RefreshFixture(
            RefreshDependency.Synchronization,
            TimeSpan.FromMinutes(5));
        using var callerCancellation = new CancellationTokenSource();
        var waiting = fixture.Coordinator.RefreshAsync(force: true, callerCancellation.Token);
        await fixture.Control.Started.Task;
        var ownedRefresh = fixture.Coordinator.BackgroundRefresh!;
        await RuntimeTestTasks.UntilAsync(() =>
            fixture.State.Current.Supervisor.Resources.RunningIO == 1);

        try
        {
            callerCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

            Assert.False(ownedRefresh.IsCompleted);
            Assert.False(fixture.Control.ReceivedToken.IsCancellationRequested);
            Assert.Equal(1, fixture.State.Current.Supervisor.Resources.RunningIO);
        }
        finally
        {
            fixture.Control.Release();
        }

        await ownedRefresh;
        await RuntimeTestTasks.UntilAsync(() =>
            fixture.State.Current.Supervisor.Operations.Any(operation =>
                operation.State == BackgroundWorkState.Succeeded));

        Assert.Equal(DataAvailability.Current, fixture.State.Current.Data.Availability);
        Assert.Equal(1, fixture.Sync.Calls);
    }

    [Fact]
    public async Task RaidHistoryRepairAndObservationDoNotRaceDatabaseInitialization()
    {
        var releaseDatabase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var history = new StubRaidHistoryService();
        await using var fixture = new RefreshFixture(
            RefreshDependency.None,
            TimeSpan.FromSeconds(5),
            history,
            releaseDatabase);

        var initializing = fixture.Coordinator.InitializeAsync(default);
        await fixture.DataStore.InitializeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await RuntimeTestTasks.DrainAsync();
            Assert.Equal(0, history.ListCalls);
            Assert.Equal(FeatureLifecycleState.NotStarted, State("observation"));
        }
        finally
        {
            releaseDatabase.TrySetResult();
        }

        await initializing;
        Assert.Equal(1, history.ListCalls);
        Assert.Equal(FeatureLifecycleState.Running, State("observation"));

        FeatureLifecycleState State(string id) => Assert.Single(
            fixture.State.Current.Lifecycle.Features,
            feature => feature.FeatureId == new RuntimeFeatureId(id)).State;
    }

    [Fact]
    public async Task RaidHistoryRepairFailureIsPublishedAndObservationStillStartsDegraded()
    {
        var history = new StubRaidHistoryService
        {
            ListFailure = new IOException("private storage failure"),
        };
        await using var fixture = new RefreshFixture(
            RefreshDependency.None,
            TimeSpan.FromSeconds(5),
            history);

        await fixture.Coordinator.InitializeAsync(default);

        var lifecycle = fixture.State.Current.Lifecycle;
        Assert.True(fixture.State.Current.DatabaseReady);
        Assert.Equal(FeatureLifecycleState.Failed, State("raid-history-repair"));
        Assert.Equal(FeatureLifecycleState.Degraded, State("observation"));
        Assert.Equal("dependency-io", Feature("raid-history-repair").LastFault!.Code.Value);

        RuntimeFeatureSnapshot Feature(string id) => Assert.Single(
            lifecycle.Features,
            feature => feature.FeatureId == new RuntimeFeatureId(id));

        FeatureLifecycleState State(string id) => Feature(id).State;
    }

    [Fact]
    public void InitializePublishesTheLifecycleSnapshotReadInsideTheStateUpdate()
    {
        var source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "ApplicationStartupCoordinator.cs"));
        var initialize = source[source.IndexOf("public async Task InitializeAsync", StringComparison.Ordinal)..];
        initialize = initialize[..initialize.IndexOf("public void BeginBackgroundRefresh", StringComparison.Ordinal)];

        Assert.Contains("Lifecycle = _lifecycle.Snapshot", initialize, StringComparison.Ordinal);
        Assert.DoesNotContain("Lifecycle = snapshot", initialize, StringComparison.Ordinal);
    }

    public enum RefreshDependency
    {
        None,
        Synchronization,
        Snapshot,
        QuestRequirements,
        HideoutRequirements,
        MapAliases,
    }

    private sealed class RefreshFixture : IAsyncDisposable
    {
        public RefreshFixture(
            RefreshDependency dependency,
            TimeSpan refreshTimeout,
            IRaidHistoryService? raidHistory = null,
            TaskCompletionSource? databaseRelease = null,
            bool offline = false,
            Func<bool>? offlineProbe = null,
            IHighValueLootRuntimeSource? highValueLoot = null)
        {
            Time = new(Epoch);
            Control = new(dependency);
            var options = new RuntimeOptions(
                false,
                offline,
                GameMode.Regular,
                "en",
                TimeSpan.FromHours(1),
                refreshTimeout)
            {
                OfflineProbe = offlineProbe,
                OfflineTransitionPollInterval = TimeSpan.FromSeconds(1),
            };
            State = new(options, Time);
            DataStore = new(Control, databaseRelease);
            Sync = new(Control);
            Catalogs = new(Control);
            ItemFacts = new();
            Projection = new();
            var profile = new StubProfileService();
            var raid = new RaidActivityCoordinator(
                new RaidStateService(),
                raidHistory ?? new StubRaidHistoryService(),
                profile,
                State,
                timeProvider: Time);
            var observation = new RaidObservationService(
                new StubPathLocator(),
                new StubLogWatcher(),
                new StubScreenshotWatcher(),
                new StubFilenameParser(),
                raid,
                new SquadStateService(),
                new FleaSaleStateService(),
                State,
                options,
                NullLogger<RaidObservationService>.Instance);
            Coordinator = new(
                DataStore,
                Sync,
                profile,
                raid,
                observation,
                new([], []),
                new EftLogParser(),
                Catalogs,
                Catalogs,
                ItemFacts,
                State,
                [Projection],
                options,
                NullLogger<ApplicationStartupCoordinator>.Instance,
                timeProvider: Time,
                highValueLoot: highValueLoot);
        }

        public ManualTimeProvider Time { get; }

        public RefreshControl Control { get; }

        public RuntimeStateStore State { get; }

        public ControlledDataStore DataStore { get; }

        public ControlledSyncService Sync { get; }

        public ControlledCatalogs Catalogs { get; }

        public StubItemFactCatalog ItemFacts { get; }

        public StubProjection Projection { get; }

        public ApplicationStartupCoordinator Coordinator { get; }

        public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
    }

    private sealed class RefreshControl(RefreshDependency dependency)
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ReceivedToken { get; private set; }

        public async Task<T> RunAsync<T>(
            RefreshDependency candidate,
            T result,
            CancellationToken cancellationToken)
        {
            if (candidate != dependency)
            {
                return result;
            }

            ReceivedToken = cancellationToken;
            Started.TrySetResult();
            // Real-time fail-safe only: injected time still controls every production deadline.
            // If a test assertion fails before the hand release, the fixture must eventually
            // unwind instead of occupying an Actions runner until the job-level timeout.
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            return result;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class ControlledSyncService(RefreshControl control) : IDataSyncService
    {
        public int Calls { get; private set; }

        public async Task<SyncReport> SyncAsync(SyncRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return await control.RunAsync(
                    RefreshDependency.Synchronization,
                    new SyncReport(
                        Epoch,
                        Epoch,
                        [new("items", true, false, 3, null)]),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class ControlledDataStore(
        RefreshControl control,
        TaskCompletionSource? initializeRelease = null) : IRuntimeDataStore
    {
        public string DatabasePath => "fixture";

        public int SnapshotCalls { get; private set; }

        public TaskCompletionSource InitializeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeStarted.TrySetResult();
            if (initializeRelease is not null)
            {
                await initializeRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public Task SeedDemoAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<CachedDataSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
        {
            SnapshotCalls++;
            return await control.RunAsync(
                    RefreshDependency.Snapshot,
                    new CachedDataSnapshot(3, 1, Epoch, null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private sealed class ControlledCatalogs(RefreshControl control) : IRequirementCatalog, IMapAliasCatalog
    {
        public int QuestCalls { get; private set; }

        public int HideoutCalls { get; private set; }

        public int AliasCalls { get; private set; }

        public int Invalidations { get; private set; }

        public async Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(
            CancellationToken cancellationToken)
        {
            QuestCalls++;
            return await control.RunAsync(
                    RefreshDependency.QuestRequirements,
                    (IReadOnlyList<QuestItemRequirement>)[],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(
            CancellationToken cancellationToken)
        {
            HideoutCalls++;
            return await control.RunAsync(
                    RefreshDependency.HideoutRequirements,
                    (IReadOnlyList<HideoutItemRequirement>)[],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HideoutStationSummary>>([]);

        public async Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken)
        {
            AliasCalls++;
            return await control.RunAsync(
                    RefreshDependency.MapAliases,
                    (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        public void Invalidate() => Invalidations++;
    }

    private sealed class StubItemFactCatalog : IItemFactCatalog
    {
        public int Invalidations { get; private set; }

        public Task<IReadOnlyList<AmmoStats>> GetAmmoAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoStats>>([]);

        public Task<IReadOnlyList<AmmoPackContents>> GetAmmoPacksAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AmmoPackContents>>([]);

        public Task<IReadOnlyList<LoadoutItemFacts>> GetLoadoutFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LoadoutItemFacts>>([]);

        public Task<IReadOnlyList<KeyFacts>> GetKeyFactsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KeyFacts>>([]);

        public void Invalidate() => Invalidations++;
    }

    private sealed class StubProjection : IInvalidatableProjection
    {
        public int Invalidations { get; private set; }

        public void Invalidate() => Invalidations++;
    }

    private sealed class RecordingHighValueLootRuntimeSource : IHighValueLootRuntimeSource
    {
        public LootSpawnSourceBundle? LastKnownGood => null;

        public bool NeedsRefreshResult { get; init; }

        public int InitializeCalls { get; private set; }

        public int RefreshCalls { get; private set; }

        public bool LastForce { get; private set; }

        public bool NeedsRefresh(DateTimeOffset evaluatedUtc, TimeSpan freshFor) => NeedsRefreshResult;

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InitializeCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask RefreshAsync(bool force, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCalls++;
            LastForce = force;
            return ValueTask.CompletedTask;
        }

        public HighValueLootLayerResult Build(
            HighValueLootRuntimeLayerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubProfileService : IPlayerProfileService
    {
        private static readonly PlayerProfile Profile = new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "fixture",
            GameMode.Regular,
            1,
            Faction.Unknown,
            null,
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new HashSet<string>(),
            new Dictionary<string, int>(),
            new Dictionary<string, EventItemState>(),
            new Dictionary<string, string>(),
            Epoch);

        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(Profile);

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) =>
            Task.FromResult(Profile);
    }

    private sealed class StubRaidHistoryService : IRaidHistoryService
    {
        private int _listCalls;

        public Exception? ListFailure { get; init; }

        public int ListCalls => Volatile.Read(ref _listCalls);

        public Task<Guid> StartAsync(RaidHistoryEntry raid, CancellationToken cancellationToken) =>
            Task.FromResult(raid.Id);

        public Task RecordEventAsync(
            Guid raidId,
            string type,
            DateTimeOffset timestampUtc,
            string payloadJson,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task EndAsync(
            Guid raidId,
            DateTimeOffset endUtc,
            string? outcome,
            string? notes,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CorrectAsync(
            Guid raidId,
            string? outcome,
            string? notes,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<RaidHistoryEntry>> ListAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _listCalls);
            return ListFailure is { } failure
                ? Task.FromException<IReadOnlyList<RaidHistoryEntry>>(failure)
                : Task.FromResult<IReadOnlyList<RaidHistoryEntry>>([]);
        }

        public Task<IReadOnlyList<ScreenshotPosition>> ListPositionsAsync(
            Guid raidId,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ScreenshotPosition>>([]);

        public Task<IReadOnlyList<string>> ListEventPayloadsAsync(
            Guid raidId,
            string type,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<RaidTrail>> ListTrailsForMapAsync(
            string mapId,
            int limit,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RaidTrail>>([]);

        public Task ExportCsvAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ExportJsonAsync(Stream destination, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubPathLocator : IEftPathLocator
    {
        public Task<EftPaths> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new EftPaths(null, null, null, Confidence.Unknown));
    }

    private sealed class StubLogWatcher : IEftLogWatcher
    {
        public IAsyncEnumerable<RaidEvidence> WatchAsync(string logRoot, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubScreenshotWatcher : IScreenshotWatcher
    {
        public IAsyncEnumerable<ScreenshotSighting> WatchAsync(
            string screenshotRoot,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubFilenameParser : IScreenshotFilenameParser
    {
        public bool TryParse(
            string filename,
            TimeSpan localUtcOffset,
            out ScreenshotPosition? position)
        {
            position = null;
            return false;
        }

        public bool TryParseFile(
            string path,
            TimeSpan localUtcOffset,
            out ScreenshotPosition? position)
        {
            position = null;
            return false;
        }
    }

    private static string RuntimeDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "TarkovCompanion.Application", "Services", "Runtime");

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "src", "TarkovCompanion.Application")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
