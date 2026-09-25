using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services;
using TarkovCompanion.App.Services.Updates;
using TarkovCompanion.App.ViewModels.V2.Setup;
using TarkovCompanion.Application.Services.Feedback;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Network;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Network;
using TarkovCompanion.Infrastructure.Settings;
using TarkovCompanion.Infrastructure.TarkovTracker;
using TarkovCompanion.UnitTests.Updates;

namespace TarkovCompanion.UnitTests.Network;

/// <summary>
/// [#292] Local only and the per-service switches: every outbound client asks the policy before it
/// connects, and a client that is refused says so instead of failing.
/// </summary>
public sealed class LocalOnlyPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tc-local-only-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Theory]
    [InlineData(false, true, NetworkVerdict.Allowed)]
    [InlineData(false, false, NetworkVerdict.SwitchedOff)]
    [InlineData(true, true, NetworkVerdict.LocalOnly)]
    [InlineData(true, false, NetworkVerdict.LocalOnly)]
    public void LocalOnlyWinsOverEveryServiceSwitch(bool localOnly, bool squadOn, NetworkVerdict expected)
    {
        var controls = new NetworkControls { LocalOnly = localOnly, SquadSharing = squadOn };

        Assert.Equal(expected, controls.Check(NetworkService.SquadSharing));
    }

    [Fact]
    public void GameDataHasNoSwitchAndStopsOnlyForLocalOnly()
    {
        Assert.Equal(NetworkVerdict.Allowed, NetworkControls.Default.Check(NetworkService.GameData));
        Assert.Equal(NetworkVerdict.LocalOnly, NetworkControls.Default.Check(NetworkService.GameData, localOnlyForced: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => NetworkControls.Default.With(NetworkService.GameData, false));
    }

    [Fact]
    public void TheSwitchesSurviveARestart()
    {
        var path = Path.Combine(_root, JsonFileNetworkControlsStore.FileName);
        var first = new NetworkPolicyService(new JsonFileNetworkControlsStore(path));
        first.Set(first.Controls with { LocalOnly = true, UpdateChecks = false });

        var second = new NetworkPolicyService(new JsonFileNetworkControlsStore(path));

        Assert.True(second.Controls.LocalOnly);
        Assert.False(second.Controls.UpdateChecks);
        Assert.True(second.Controls.SquadSharing);
    }

    [Fact]
    public void AMissingSwitchInTheFileKeepsItsDefault()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, JsonFileNetworkControlsStore.FileName);
        File.WriteAllText(path, """{ "localOnly": true }""");

        var controls = new JsonFileNetworkControlsStore(path).Read();

        Assert.True(controls.LocalOnly);
        Assert.True(controls.TarkovTracker);
        Assert.True(controls.ProblemReports);
    }

    /// <summary>
    /// #888: a network.json of zeros (a power cut after the rename) used to start with the
    /// defaults, which have Local only off: the privacy switch failed open without a word.
    /// </summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("\0\0\0\0")]
    [InlineData("")]
    [InlineData("null")]
    public void AnUnreadableFileTurnsLocalOnlyOnKeepsTheFileAndSaysSo(string contents)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, JsonFileNetworkControlsStore.FileName);
        File.WriteAllText(path, contents);

        var policy = new NetworkPolicyService(new JsonFileNetworkControlsStore(path));
        var page = new SetupNetworkControlsViewModel(policy);

        Assert.True(policy.Controls.LocalOnly);
        Assert.Equal(NetworkVerdict.LocalOnly, policy.Check(NetworkService.GameData));
        Assert.True(policy.RecoveredFromUnreadableFile);
        Assert.True(page.WasReset);
        Assert.True(page.IsLocalOnly);
        // The bad file is set aside, not lost, and the next launch reads Local only on.
        Assert.Single(Directory.GetFiles(_root, JsonFileNetworkControlsStore.FileName + ".corrupt-*"));
        Assert.True(new NetworkPolicyService(new JsonFileNetworkControlsStore(path)).Controls.LocalOnly);

        // Choosing again clears the notice.
        page.ToggleLocalOnlyCommand.Execute(null);
        Assert.False(policy.Controls.LocalOnly);
        Assert.False(page.WasReset);
    }

    [Fact]
    public void AFileThatCannotBeOpenedFailsClosedButIsNotOverwritten()
    {
        var store = new ThrowingStore();

        var policy = new NetworkPolicyService(store);

        Assert.True(policy.Controls.LocalOnly);
        Assert.True(policy.RecoveredFromUnreadableFile);
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public void TheOfflineVariableHoldsLocalOnlyOnWhateverTheSwitchSays()
    {
        var forced = true;
        var policy = new NetworkPolicyService(new MemoryStore(), () => forced);

        Assert.Equal(NetworkVerdict.LocalOnly, policy.Check(NetworkService.GameData));
        forced = false;
        Assert.Equal(NetworkVerdict.Allowed, policy.Check(NetworkService.GameData));
    }

    [Fact]
    public async Task TheHandlerRefusesWithoutOpeningAConnectionAndResumesWhenTurnedOff()
    {
        var policy = new NetworkPolicyService(new MemoryStore(new NetworkControls { LocalOnly = true }));
        var inner = new CountingHandler();
        using var client = new HttpClient(new NetworkPolicyHandler(policy, service: null, inner));

        var refused = await Assert.ThrowsAsync<NetworkBlockedException>(() => client.GetAsync("https://json.tarkov.dev/regular/items"));
        Assert.Equal(NetworkVerdict.LocalOnly, refused.Verdict);
        Assert.Equal(0, inner.Calls);

        policy.Set(policy.Controls with { LocalOnly = false });
        using var answered = await client.GetAsync("https://json.tarkov.dev/regular/items");

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task AServiceSwitchStopsOnlyItsOwnClient()
    {
        var policy = new NetworkPolicyService(new MemoryStore(new NetworkControls { TarkovTracker = false }));
        var tracker = new CountingHandler();
        var shared = new CountingHandler();
        using var trackerClient = new HttpClient(new NetworkPolicyHandler(policy, NetworkService.TarkovTracker, tracker));
        using var sharedClient = new HttpClient(new NetworkPolicyHandler(policy, service: null, shared));

        await Assert.ThrowsAsync<NetworkBlockedException>(() => trackerClient.GetAsync("https://tarkovtracker.io/api/v2/token"));
        using var _ = await sharedClient.GetAsync("https://json.tarkov.dev/regular/items");

        Assert.Equal(0, tracker.Calls);
        Assert.Equal(1, shared.Calls);
    }

    /// <summary>
    /// The composed application with Local only saved: the shared client (catalog, maps, relay, report),
    /// the catalog's offline probe and TarkovTracker all refuse, and nothing reaches either handler.
    /// </summary>
    [Fact]
    public async Task UnderLocalOnlyTheComposedAppSendsNothing()
    {
        var config = AppDataPaths.Resolve(_root, demoMode: true).Config;
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, JsonFileNetworkControlsStore.FileName), """{ "localOnly": true }""");
        var shared = new CountingHandler();
        var tracker = new CountingHandler();

        await using var services = AppComposition.Build(
            new AppCommandLine(false, true, false, false, null, null, null),
            new(DataRoot: _root, Offline: false, HttpMessageHandler: shared,
                TarkovTrackerOptions: new TarkovTrackerOptions { Enabled = true },
                TarkovTrackerHttpMessageHandler: tracker));

        await Assert.ThrowsAsync<NetworkBlockedException>(
            () => services.GetRequiredService<HttpClient>().GetAsync("https://json.tarkov.dev/regular/items"));
        Assert.True(services.GetRequiredService<RuntimeOptions>().IsOffline, "The catalog would still refresh.");
        await Assert.ThrowsAsync<InvalidOperationException>(() => services.GetRequiredService<TarkovTrackerApiClient>()
            .ValidateTokenAsync(new string('a', 22), GameMode.Regular, CancellationToken.None));
        var report = await services.GetRequiredService<GroupSessionService>().TrySendReportAsync("report", CancellationToken.None);
        Assert.Equal(ReportDelivery.Refused, report.Delivery);
        Assert.Contains("Local only", report.Message, StringComparison.Ordinal);

        Assert.Equal(0, shared.Calls);
        Assert.Equal(0, tracker.Calls);
    }

    [Fact]
    public async Task TheGroupSessionSaysLocalOnlyAndSendsNothingThenResumesWhenItIsTurnedOff()
    {
        var policy = new NetworkPolicyService(new MemoryStore(new NetworkControls { LocalOnly = true }));
        var relay = new CountingHandler();
        var store = new RuntimeStateStore(new(false, Offline: false, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)));
        await using var session = new GroupSessionService(
            new EnabledGroup(),
            store,
            new HttpClient(relay),
            NullLogger<GroupSessionService>.Instance,
            network: policy);

        session.Start();
        Assert.True(await WaitAsync(() => store.Current.Group.Status?.Code is GroupStatus.LocalOnly), "Never said Local only.");
        Assert.Null(await session.SendMarkAsync("customs", default, "here", isPing: true, CancellationToken.None));
        Assert.Equal(0, relay.Calls);

        policy.Set(policy.Controls with { LocalOnly = false });

        Assert.True(await WaitAsync(() => relay.Calls > 0, attempts: 200), "Sharing did not resume.");
    }

    [Fact]
    public async Task SquadSharingSwitchedOffSaysSoRatherThanLocalOnly()
    {
        var policy = new NetworkPolicyService(new MemoryStore(new NetworkControls { SquadSharing = false }));
        var relay = new CountingHandler();
        var store = new RuntimeStateStore(new(false, Offline: false, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5)));
        await using var session = new GroupSessionService(
            new EnabledGroup(), store, new HttpClient(relay), NullLogger<GroupSessionService>.Instance, network: policy);

        session.Start();

        Assert.True(await WaitAsync(() => store.Current.Group.Status?.Code is GroupStatus.SwitchedOff));
        Assert.Equal(0, relay.Calls);
    }

    [Fact]
    public async Task UpdateChecksUnderLocalOnlyDoNotAskTheFeed()
    {
        using var harness = new RoughChannelHarness(installedVersion: "1.0.100");
        harness.Publish("1.0.200");
        var policy = new NetworkPolicyService(new MemoryStore(new NetworkControls { LocalOnly = true }));
        var gateway = VelopackUpdateGateway.Create(
            UpdateChannel.Rough,
            new HashVerifiedUpdateSource(new DirectoryUpdateFeedTransport(harness.Feed), harness.Log),
            harness.Locator,
            harness.Log,
            network: policy);

        var blocked = await gateway.CheckAsync(CancellationToken.None);
        Assert.Equal(SetupText.NetworkState(NetworkVerdict.LocalOnly), blocked.Status);
        Assert.Null(blocked.Available);
        Assert.False(blocked.Failed);

        policy.Set(policy.Controls with { LocalOnly = false, UpdateChecks = false });
        Assert.Equal(SetupText.NetworkUpdateOff, (await gateway.CheckAsync(CancellationToken.None)).Status);

        policy.Set(policy.Controls with { UpdateChecks = true });
        Assert.Equal("1.0.200", (await gateway.CheckAsync(CancellationToken.None)).Available);
    }

    [Fact]
    public void TheSetupRowsSayWhatIsHappeningNow()
    {
        var policy = new NetworkPolicyService(new MemoryStore(new NetworkControls { ProblemReports = false }));
        var page = new SetupNetworkControlsViewModel(policy);
        var reports = page.Rows.Single(row => row.Service == NetworkService.ProblemReports);
        var squad = page.Rows.Single(row => row.Service == NetworkService.SquadSharing);
        var data = page.Rows.Single(row => row.Service == NetworkService.GameData);

        Assert.False(page.IsLocalOnly);
        Assert.Equal(SetupText.NetworkState(NetworkVerdict.SwitchedOff), reports.StateLabel);
        Assert.Equal(SetupText.NetworkState(NetworkVerdict.Allowed), squad.StateLabel);
        Assert.False(data.HasSwitch);

        page.ToggleLocalOnlyCommand.Execute(null);

        Assert.True(page.IsLocalOnly);
        Assert.All(page.Rows, row => Assert.Equal("Local only · off", row.StateLabel));
        Assert.All(page.Rows, row => Assert.False(row.CanToggle));
        // The player's own choices are kept under Local only, so turning it off restores them.
        Assert.True(squad.IsOn);
        Assert.False(reports.IsOn);

        page.ToggleLocalOnlyCommand.Execute(null);
        squad.ToggleCommand.Execute(null);

        Assert.False(policy.Controls.SquadSharing);
        Assert.Equal(SetupText.NetworkState(NetworkVerdict.SwitchedOff), squad.StateLabel);
    }

    [Fact]
    public void TheOfflineVariableShowsTheSwitchHeldOn()
    {
        var page = new SetupNetworkControlsViewModel(new NetworkPolicyService(new MemoryStore(), () => true));

        Assert.True(page.IsLocalOnly);
        Assert.True(page.IsForced);
        Assert.False(page.CanToggleLocalOnly);
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, int attempts = 100)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    private sealed class ThrowingStore : INetworkControlsStore
    {
        public int Saves { get; private set; }

        public NetworkControls Read() => throw new IOException("locked");

        public void Save(NetworkControls controls) => Saves++;
    }

    /// <summary>
    /// [#902 P6] The switch said "Off" while the Data page said "Offline mode is on". The state beside
    /// the switch says what is in force: on, off, or on because this run started offline.
    /// </summary>
    [Fact]
    public void LocalOnlySaysStartedOfflineWhenTheRunBeganWithoutTheNetworkAndTheSwitchIsOff()
    {
        var policy = new NetworkPolicyService(new MemoryStore());
        var startedOffline = true;
        var page = new SetupNetworkControlsViewModel(policy, startedOffline: () => startedOffline);

        Assert.False(page.IsLocalOnly);
        Assert.Equal("On · started offline", page.LocalOnlyState);

        policy.Set(policy.Controls with { LocalOnly = true });
        Assert.Equal("On", page.LocalOnlyState);

        startedOffline = false;
        policy.Set(policy.Controls with { LocalOnly = false });
        Assert.Equal("Off", page.LocalOnlyState);
    }

    private sealed class MemoryStore(NetworkControls? initial = null) : INetworkControlsStore
    {
        private NetworkControls _controls = initial ?? NetworkControls.Default;

        public NetworkControls Read() => _controls;

        public void Save(NetworkControls controls) => _controls = controls;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class EnabledGroup : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(true, "https://relay.example.test/", "Clay", "a-key-long-enough", false, true));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
