using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.LootSpawns;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.IntegrationTests;

[Collection(SqliteCollection.Name)]
public sealed class RuntimeCompositionTests
{
    [Fact]
    public async Task DemoCompositionInitializesPersistentStateAndExercisesSharedViewModels()
    {
        var root = TemporaryRoot();
        try
        {
            await using var services = AppComposition.Build(
                CommandLine(demo: true),
                new(DataRoot: root, Offline: true));
            var viewModel = services.GetRequiredService<MainWindowViewModel>();

            await viewModel.InitializeAsync();
            await viewModel.Scanner.ScanAsync();

            var snapshot = services.GetRequiredService<IRuntimeStateStore>().Current;
            Assert.True(snapshot.DatabaseReady);
            Assert.Equal(DataAvailability.DemoFixture, snapshot.Data.Availability);
            Assert.Equal(1, snapshot.Data.ItemCount);
            Assert.Equal("demo-graphics-card", snapshot.Scan.CanonicalItemId);
            Assert.Equal("Graphics Card", viewModel.LastScanName);
            Assert.Contains("fixture", viewModel.ModeLabel, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Graphics Card", Assert.Single(viewModel.Items.Results).Name);
            Assert.Same(services.GetRequiredService<MapViewModel>(), viewModel.Map);
            Assert.NotNull(services.GetRequiredService<IQuestCatalog>());
            Assert.NotNull(services.GetRequiredService<IQuestProgressStore>());
            Assert.NotNull(services.GetRequiredService<IQuestProgressCommandService>());
            Assert.NotNull(services.GetRequiredService<IQuestReadService>());
            Assert.NotNull(services.GetRequiredService<IProjectQuestProgressJson>());
            Assert.NotNull(services.GetRequiredService<IQuestProgressImportStore>());
            Assert.NotNull(services.GetRequiredService<IQuestProgressExchangeService>());
            Assert.NotNull(services.GetRequiredService<IReviewedLootSpawnPublicationReplacementStore>());
            Assert.True(File.Exists(services.GetRequiredService<IRuntimeDataStore>().DatabasePath));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task NormalOfflineCompositionShowsHonestUnavailableState()
    {
        var root = TemporaryRoot();
        try
        {
            await using var services = AppComposition.Build(
                CommandLine(demo: false),
                new(DataRoot: root, Offline: true));
            var viewModel = services.GetRequiredService<MainWindowViewModel>();

            await viewModel.InitializeAsync();
            await viewModel.Scanner.ScanAsync();

            var snapshot = services.GetRequiredService<IRuntimeStateStore>().Current;
            Assert.True(snapshot.DatabaseReady);
            Assert.Equal(DataAvailability.Unavailable, snapshot.Data.Availability);
            Assert.Equal(0, snapshot.Data.ItemCount);
            Assert.False(snapshot.Scan.IsAvailable);
            Assert.False(snapshot.Scan.Succeeded);
            Assert.Equal("No item scanned", viewModel.LastScanName);
            Assert.Equal("Unavailable", viewModel.LastScanValue);
            Assert.Empty(viewModel.Items.Results);
            Assert.Contains("no local game data", viewModel.Items.SearchStatus, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData("false", true, false)]
    [InlineData("0", true, false)]
    [InlineData("true", false, true)]
    public void TarkovTrackerDefaultAndExplicitOptOutAreDeterministic(
        string? setting,
        bool protectedStorageAvailable,
        bool expected) =>
        Assert.Equal(
            expected,
            AppComposition.OptionalFeatureEnabled(setting, protectedStorageAvailable));

    [Fact]
    public async Task AvailableProtectedStorageEnablesDisconnectedTrackerWithoutNetwork()
    {
        var root = TemporaryRoot();
        var trackerNetwork = new FailIfUsedHandler();
        var secretStore = new FixtureIntegrationSecretStore(isAvailable: true);
        var scope = new QuestProfileScope(
            Guid.Parse("d66960bb-47bd-4276-a52e-498afbe054c3"),
            GameMode.Regular,
            "composition-generation");
        try
        {
            await using var services = AppComposition.Build(
                CommandLine(demo: false),
                new(
                    DataRoot: root,
                    Offline: false,
                    TarkovTrackerHttpMessageHandler: trackerNetwork,
                    IntegrationSecretStore: secretStore));
            var integration = services.GetRequiredService<ITarkovTrackerIntegrationService>();
            var status = await integration.GetStatusAsync(scope, CancellationToken.None);

            Assert.True(status.FeatureEnabled);
            Assert.True(status.SecureStorageAvailable);
            Assert.True(status.CanConnect);
            Assert.False(status.Connected);

            await secretStore.SaveAsync(
                new(
                    IntegrationSecretKind.TarkovTrackerProgressToken,
                    scope.ProfileId,
                    scope.GameMode,
                    scope.Generation),
                "fixture-protected-value",
                CancellationToken.None);
            var existingStatus = await integration.GetStatusAsync(scope, CancellationToken.None);
            Assert.True(existingStatus.Connected);
            Assert.Equal(0, trackerNetwork.RequestCount);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ExplicitOptOutDisablesComposedTrackerWithoutNetwork()
    {
        var root = TemporaryRoot();
        var trackerNetwork = new FailIfUsedHandler();
        try
        {
            await using var services = AppComposition.Build(
                CommandLine(demo: false),
                new(
                    DataRoot: root,
                    Offline: false,
                    TarkovTrackerOptions: new() { Enabled = false },
                    TarkovTrackerHttpMessageHandler: trackerNetwork,
                    IntegrationSecretStore: new FixtureIntegrationSecretStore(isAvailable: true)));
            var status = await services.GetRequiredService<ITarkovTrackerIntegrationService>().GetStatusAsync(
                new(
                    Guid.Parse("d66960bb-47bd-4276-a52e-498afbe054c3"),
                    GameMode.Regular,
                    "composition-generation"),
                CancellationToken.None);

            Assert.False(status.FeatureEnabled);
            Assert.False(status.CanConnect);
            Assert.Equal(0, trackerNetwork.RequestCount);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task OfflineAndUnavailableStorageCompositionDisableTrackerWithoutNetwork()
    {
        var offlineRoot = TemporaryRoot();
        var unavailableRoot = TemporaryRoot();
        var offlineNetwork = new FailIfUsedHandler();
        var unavailableNetwork = new FailIfUsedHandler();
        try
        {
            await using (var offline = AppComposition.Build(
                CommandLine(demo: false),
                new(
                    DataRoot: offlineRoot,
                    Offline: true,
                    TarkovTrackerOptions: new() { Enabled = true },
                    TarkovTrackerHttpMessageHandler: offlineNetwork,
                    IntegrationSecretStore: new FixtureIntegrationSecretStore(isAvailable: true))))
            {
                var status = await offline.GetRequiredService<ITarkovTrackerIntegrationService>().GetStatusAsync(
                    new(
                        Guid.Parse("d66960bb-47bd-4276-a52e-498afbe054c3"),
                        GameMode.Regular,
                        "composition-generation"),
                    CancellationToken.None);
                Assert.False(status.NetworkAccessEnabled);
                Assert.False(status.CanConnect);
            }

            await using (var unavailable = AppComposition.Build(
                CommandLine(demo: false),
                new(
                    DataRoot: unavailableRoot,
                    Offline: false,
                    TarkovTrackerOptions: new() { Enabled = true },
                    TarkovTrackerHttpMessageHandler: unavailableNetwork,
                    IntegrationSecretStore: new FixtureIntegrationSecretStore(isAvailable: false))))
            {
                var status = await unavailable.GetRequiredService<ITarkovTrackerIntegrationService>().GetStatusAsync(
                    new(
                        Guid.Parse("d66960bb-47bd-4276-a52e-498afbe054c3"),
                        GameMode.Regular,
                        "composition-generation"),
                    CancellationToken.None);
                Assert.False(status.SecureStorageAvailable);
                Assert.False(status.CanConnect);
            }

            Assert.Equal(0, offlineNetwork.RequestCount);
            Assert.Equal(0, unavailableNetwork.RequestCount);
        }
        finally
        {
            Cleanup(offlineRoot);
            Cleanup(unavailableRoot);
        }
    }

    [Fact]
    public async Task OfflineRestartLoadsNormalizedCacheWithoutNetwork()
    {
        var root = TemporaryRoot();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        try
        {
            var fixtureHandler = new FixtureApiHandler();
            await using (var online = AppComposition.Build(
                CommandLine(demo: false),
                new(DataRoot: root, Offline: false, TimeProvider: clock, HttpMessageHandler: fixtureHandler)))
            {
                var startup = online.GetRequiredService<ApplicationStartupCoordinator>();
                await startup.InitializeAsync(CancellationToken.None);
                await startup.RefreshAsync(force: true, CancellationToken.None);
                Assert.Equal(2, online.GetRequiredService<IRuntimeStateStore>().Current.Data.ItemCount);
                var loot = online.GetRequiredService<IHighValueLootRuntimeSource>();
                var snapshot = Assert.Single(Assert.IsType<LootSpawnSourceBundle>(loot.LastKnownGood).Snapshots);
                Assert.Single(loot.Build(LootRequest(snapshot, clock.GetUtcNow())).Entries);
                Assert.True(File.Exists(Path.Combine(root, "Cache", "LootSpawns", "publication.cache")));
            }

            var forbiddenNetwork = new FailIfUsedHandler();
            await using (var offline = AppComposition.Build(
                CommandLine(demo: false),
                new(DataRoot: root, Offline: true, TimeProvider: clock, HttpMessageHandler: forbiddenNetwork)))
            {
                await offline.GetRequiredService<ApplicationStartupCoordinator>()
                    .InitializeAsync(CancellationToken.None);
                var snapshot = offline.GetRequiredService<IRuntimeStateStore>().Current;
                var hits = await offline.GetRequiredService<IItemSearchService>()
                    .SearchAsync("Salewa", 5, CancellationToken.None);

                Assert.Equal(DataAvailability.Cached, snapshot.Data.Availability);
                Assert.Equal(2, snapshot.Data.ItemCount);
                Assert.Equal("item-001", Assert.Single(hits).Item.Id);
                Assert.Equal(0, forbiddenNetwork.RequestCount);
                var loot = offline.GetRequiredService<IHighValueLootRuntimeSource>();
                var lootSnapshot = Assert.Single(Assert.IsType<LootSpawnSourceBundle>(loot.LastKnownGood).Snapshots);
                Assert.Single(loot.Build(LootRequest(lootSnapshot, clock.GetUtcNow())).Entries);
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task EnvironmentReconnectRunsAndPublishesTheFullProductionSyncWithoutRestart()
    {
        const string variable = AppComposition.OfflineEnvironmentVariable;
        var original = Environment.GetEnvironmentVariable(variable);
        var root = TemporaryRoot();
        try
        {
            Environment.SetEnvironmentVariable(variable, "1");
            var handler = new FixtureApiHandler();
            await using (var services = AppComposition.Build(
                CommandLine(demo: false),
                new(DataRoot: root, HttpMessageHandler: handler)))
            {
                var startup = services.GetRequiredService<ApplicationStartupCoordinator>();
                var state = services.GetRequiredService<IRuntimeStateStore>();
                await startup.InitializeAsync(CancellationToken.None);
                startup.BeginBackgroundRefresh();

                Assert.True(state.Current.IsOffline);
                Assert.Equal(DataAvailability.Unavailable, state.Current.Data.Availability);
                Assert.Equal(0, handler.Count("regular/items"));
                Assert.Null(startup.BackgroundRefresh);

                Environment.SetEnvironmentVariable(variable, "0");
                for (var attempt = 0; attempt < 400 &&
                     (state.Current.IsOffline || state.Current.Data.ItemCount != 2 ||
                      state.Current.Data.Availability != DataAvailability.Current);
                     attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
                }

                var hit = Assert.Single(await services.GetRequiredService<IItemSearchService>()
                    .SearchAsync("Salewa", 5, CancellationToken.None));
                Assert.False(state.Current.IsOffline);
                Assert.Equal(DataAvailability.Current, state.Current.Data.Availability);
                Assert.Equal(2, state.Current.Data.ItemCount);
                Assert.Equal("item-001", hit.Item.Id);
                Assert.Equal(1, handler.Count("regular/items"));
                Assert.Equal(1, handler.Count("regular/maps"));
                Assert.Equal(1, handler.Count("regular/tasks"));
                Assert.Equal(1, handler.Count("regular/hideout"));
                Assert.Equal(1, handler.Count("regular/traders"));
                Assert.Equal(1, handler.Count("regular/crafts"));
                Assert.Equal(1, handler.Count("regular/barters"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
            Cleanup(root);
        }
    }

    [Fact]
    public async Task ComposedQuestBoardEditsTaskObjectiveCountAndPinState()
    {
        var root = TemporaryRoot();
        var clock = new ManualTimeProvider(new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await using var services = AppComposition.Build(
                CommandLine(demo: false),
                new(DataRoot: root, Offline: false, TimeProvider: clock, HttpMessageHandler: new FixtureApiHandler()));
            var startup = services.GetRequiredService<ApplicationStartupCoordinator>();
            await startup.InitializeAsync(CancellationToken.None);
            await startup.RefreshAsync(force: true, CancellationToken.None);
            var main = services.GetRequiredService<MainWindowViewModel>();
            var quests = main.Quests;

            await quests.InitializeAsync(CancellationToken.None);
            Assert.Contains(quests.AvailableFilters, filter => filter.Id == "locked");
            Assert.Contains(quests.AvailableFilters, filter => filter.Id == "indeterminate");
            quests.SelectedFilter = quests.AvailableFilters.Single(filter => filter.Id == "all");
            var task = Assert.Single(quests.Tasks);
            await Assert.IsType<AsyncDelegateCommand>(task.SetActiveCommand).ExecuteAsync();

            task = Assert.Single(quests.Tasks);
            Assert.Equal("Active", task.RecordedState);
            Assert.Contains("Active need: 2", Assert.Single(task.Objectives).Items, StringComparison.Ordinal);
            Assert.Equal("Found in raid", Assert.Single(task.Objectives).FoundInRaidRule);
            await Assert.IsType<AsyncDelegateCommand>(task.Objectives[0].IncrementCommand).ExecuteAsync();
            task = Assert.Single(quests.Tasks);
            Assert.Contains("1/2", Assert.Single(task.Objectives).Status, StringComparison.Ordinal);
            await Assert.IsType<AsyncDelegateCommand>(task.TogglePinCommand).ExecuteAsync();
            task = Assert.Single(quests.Tasks);
            Assert.True(task.IsPinned);
            await Assert.IsType<AsyncDelegateCommand>(task.SetUnknownCommand).ExecuteAsync();
            Assert.Equal("Unknown", Assert.Single(quests.Tasks).RecordedState);
            // The game never writes its own player's level, so the page owns it. Left at the
            // stored 1 every level requirement in the catalog reads as unmet, which is what a
            // fresh install looked like.
            Assert.Equal(1m, quests.PlayerLevel);
            await quests.SetPlayerLevelAsync(42);
            Assert.Equal(42m, quests.PlayerLevel);
            Assert.Equal(42, (await services.GetRequiredService<IPlayerProfileService>()
                .GetActiveAsync(TestContext.Current.CancellationToken)).Level);

            // Out of range is pulled back rather than refused: a spinner holding 800 gates the
            // catalog as thoroughly as one holding 1.
            await quests.SetPlayerLevelAsync(800);
            Assert.Equal((decimal)QuestsPageViewModel.MaximumLevel, quests.PlayerLevel);

            // A cleared box is not a level.
            await quests.SetPlayerLevelAsync(null);
            Assert.Equal((decimal)QuestsPageViewModel.MaximumLevel, quests.PlayerLevel);

            Assert.Contains("Regular", quests.ScopeStatus, StringComparison.Ordinal);
            Assert.Contains("regular", quests.CatalogStatus, StringComparison.OrdinalIgnoreCase);

            quests.ExchangePath = Path.Combine(root, "Support", "quest-progress-test.json");
            await quests.ExportProgressCommand.ExecuteAsync();
            Assert.True(File.Exists(quests.ExchangePath));
            Assert.Contains("Exported", quests.ExchangeStatus, StringComparison.Ordinal);
            await quests.PreviewImportCommand.ExecuteAsync();
            Assert.True(quests.HasImportProposals);
            Assert.Contains("unchanged", quests.ImportPreviewSummary, StringComparison.OrdinalIgnoreCase);
            await quests.ApplyImportCommand.ExecuteAsync();
            Assert.Contains("Applied 0", quests.ExchangeStatus, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task RaidCoordinatorPersistsTransitionsAndCsvExport()
    {
        var root = TemporaryRoot();
        var clock = new ManualTimeProvider(new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero));
        try
        {
            await using var services = AppComposition.Build(
                CommandLine(demo: true),
                new(DataRoot: root, Offline: true, TimeProvider: clock));
            await services.GetRequiredService<ApplicationStartupCoordinator>()
                .InitializeAsync(CancellationToken.None);
            var coordinator = services.GetRequiredService<RaidActivityCoordinator>();
            await services.GetRequiredService<IRuntimeScanUseCase>().ExecuteAsync(CancellationToken.None);
            clock.Advance(TimeSpan.FromMinutes(12));
            await coordinator.ApplyEvidenceAsync(
                new(
                    RaidEvidenceKind.Simulator,
                    clock.GetUtcNow(),
                    "customs",
                    RaidLifecycleState.PostRaid,
                    Confidence.Certain,
                    "Deterministic demo fixture raid ended."),
                CancellationToken.None);

            var history = services.GetRequiredService<IRaidHistoryService>();
            // Writes are queued now, so a read taken immediately after one is racing it. That
            // is the point of the queue rather than a defect in it: observation returns before
            // the database has been touched. Flushing is how a caller that genuinely needs the
            // row — a test, or a shutdown — says so.
            if (history is RaidHistoryOutbox outbox)
            {
                await outbox.FlushAsync(CancellationToken.None);
            }

            var raid = Assert.Single(await history.ListAsync(CancellationToken.None));
            Assert.Equal("customs", raid.MapId);
            Assert.Equal(clock.GetUtcNow(), raid.EndedUtc);

            await using var csv = new MemoryStream();
            await history.ExportCsvAsync(csv, CancellationToken.None);
            var text = Encoding.UTF8.GetString(csv.ToArray());
            Assert.StartsWith("id,profile_id,map_id,mode,start_utc,end_utc,outcome,notes", text, StringComparison.Ordinal);
            Assert.Contains(",customs,", text, StringComparison.Ordinal);
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static AppCommandLine CommandLine(bool demo) =>
        new(false, demo, false, false, null, null, null);

    private static HighValueLootRuntimeLayerRequest LootRequest(
        LootSpawnSnapshot snapshot,
        DateTimeOffset evaluatedUtc) => new(
        snapshot.MapId,
        snapshot.TransformVersion,
        new MapSceneBounds(-1_000, -1_000, 1_000, 1_000),
        evaluatedUtc,
        new HighValueLootFilter(
            LootSpawnValueBasis.FleaGross,
            new(1, 2, 3, 4),
            TimeSpan.FromDays(7),
            TimeSpan.FromDays(90),
            0.5),
        snapshot.Records.SelectMany(record => record.Location.FloorIds).Distinct().ToArray());

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), $"tarkov-runtime-{Guid.NewGuid():N}");

    private static void Cleanup(string root)
    {
        ScratchDirectory.Remove(root);
    }

    private sealed class FailIfUsedHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class FixtureIntegrationSecretStore(bool isAvailable) : IIntegrationSecretStore
    {
        private readonly HashSet<IntegrationSecretReference> _references = [];

        public bool IsAvailable { get; } = isAvailable;

        public Task SaveAsync(
            IntegrationSecretReference reference,
            string secret,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _references.Add(reference);
            return Task.CompletedTask;
        }

        public Task<string?> LoadAsync(
            IntegrationSecretReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }

        public Task<bool> ExistsAsync(
            IntegrationSecretReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(IsAvailable && _references.Contains(reference));
        }

        public Task DeleteAsync(
            IntegrationSecretReference reference,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _references.Remove(reference);
            return Task.CompletedTask;
        }
    }
}
