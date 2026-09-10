using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.App.ViewModels.Quests;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.IntegrationTests;

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
            }
        }
        finally
        {
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
            Assert.Equal("Objective source requires found-in-raid items.", Assert.Single(task.Objectives).FoundInRaidRule);
            await Assert.IsType<AsyncDelegateCommand>(task.Objectives[0].IncrementCommand).ExecuteAsync();
            task = Assert.Single(quests.Tasks);
            Assert.Contains("1/2", Assert.Single(task.Objectives).Status, StringComparison.Ordinal);
            await Assert.IsType<AsyncDelegateCommand>(task.TogglePinCommand).ExecuteAsync();
            task = Assert.Single(quests.Tasks);
            Assert.True(task.IsPinned);
            await Assert.IsType<AsyncDelegateCommand>(task.SetUnknownCommand).ExecuteAsync();
            Assert.Equal("Unknown", Assert.Single(quests.Tasks).RecordedState);
            Assert.Contains("exact mode Regular", quests.ScopeStatus, StringComparison.Ordinal);
            Assert.Contains("generation", quests.ScopeStatus, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("json.tarkov.dev", quests.CatalogStatus, StringComparison.OrdinalIgnoreCase);

            quests.ExchangePath = Path.Combine(root, "Support", "quest-progress-test.json");
            await quests.ExportProgressCommand.ExecuteAsync();
            Assert.True(File.Exists(quests.ExchangePath));
            Assert.Contains("Exported", quests.ExchangeStatus, StringComparison.Ordinal);
            await quests.PreviewImportCommand.ExecuteAsync();
            Assert.True(quests.HasImportProposals);
            Assert.Contains("unchanged", quests.ImportPreviewSummary, StringComparison.OrdinalIgnoreCase);
            await quests.ApplyImportCommand.ExecuteAsync();
            Assert.Contains("Applied 0 changes", quests.ExchangeStatus, StringComparison.Ordinal);
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

    private static string TemporaryRoot() =>
        Path.Combine(Path.GetTempPath(), $"tarkov-runtime-{Guid.NewGuid():N}");

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
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
