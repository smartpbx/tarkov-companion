using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
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
            await services.GetRequiredService<IScanUseCase>().ExecuteAsync(CancellationToken.None);
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
}
