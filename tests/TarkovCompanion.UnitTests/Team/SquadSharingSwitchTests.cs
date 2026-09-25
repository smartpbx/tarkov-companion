using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Network;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Network;
using TarkovCompanion.Infrastructure.Settings;

namespace TarkovCompanion.UnitTests.Team;

/// <summary>
/// [#902] Squad sharing is one set of switches on Team, saved when flipped, shown the same in
/// Setup, and marked Blocked on Team when Local only stops it.
/// </summary>
public sealed class SquadSharingSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tc-squad-sharing-" + Guid.NewGuid().ToString("N"));

    private string GroupFile => Path.Combine(_root, "group.json");

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

    [Fact]
    public async Task A_flipped_switch_survives_leaving_the_tab_and_a_restart()
    {
        Directory.CreateDirectory(_root);
        var store = new JsonFileGroupSettingsStore(GroupFile);
        await store.SaveAsync(new(true, "https://relay.example.test/", "Clay", "a-key-long-enough", false, true), CancellationToken.None);
        var team = Team(store);
        team.SetActiveSection(TeamWorkspaceSection.Group);
        await team.LoadAsync();
        Assert.True(team.SharesQuests);

        team.SharesQuests = false;
        await team.PendingSave;

        // Devices and back, the way the shell does it: every Team tab reloads the form.
        team.SetActiveSection(TeamWorkspaceSection.Devices);
        await team.LoadAsync();
        team.SetActiveSection(TeamWorkspaceSection.Group);
        await team.LoadAsync();
        Assert.False(team.SharesQuests);

        // A restart: a new store and a new view model over the same file.
        var restarted = Team(new JsonFileGroupSettingsStore(GroupFile));
        await restarted.LoadAsync();
        Assert.False(restarted.SharesQuests);
        Assert.True(restarted.IsEnabled);
    }

    [Fact]
    public async Task Typed_fields_survive_a_reload_and_a_switch_does_not_commit_them()
    {
        Directory.CreateDirectory(_root);
        var store = new JsonFileGroupSettingsStore(GroupFile);
        await store.SaveAsync(new(false, "https://relay.example.test/", "Clay", "a-key-long-enough", false, true), CancellationToken.None);
        var team = Team(store);
        await team.LoadAsync();

        team.ServerUri = "https://other.example.test/";
        team.SharesLoadout = true;
        await team.PendingSave;
        await team.LoadAsync();

        Assert.Equal("https://other.example.test/", team.ServerUri);
        Assert.True(team.HasUnsavedFields);
        Assert.Equal("Not saved yet · press Save", team.Status);
        var stored = await store.GetAsync(CancellationToken.None);
        Assert.True(stored.SharesLoadout);
        Assert.Equal("https://relay.example.test/", stored.ServerUri);

        await ((AsyncDelegateCommand)team.SaveCommand).ExecuteAsync();

        Assert.False(team.HasUnsavedFields);
        Assert.Equal("https://other.example.test/", (await store.GetAsync(CancellationToken.None)).ServerUri);
    }

    [Fact]
    public async Task Setup_shows_the_switches_Team_saved_without_a_restart()
    {
        Directory.CreateDirectory(_root);
        var store = new JsonFileGroupSettingsStore(GroupFile);
        await store.SaveAsync(new(true, "https://relay.example.test/", "Clay", "a-key-long-enough", false, true), CancellationToken.None);
        var setup = new GroupPageViewModel(store, static action => action());
        await setup.InitializeAsync(CancellationToken.None);
        Assert.Equal("Share with my squad · on   My loadout · off   My quest progress · on", setup.SharingSummary);
        var refreshed = new TaskCompletionSource();
        setup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GroupPageViewModel.SharingSummary) && !setup.SharesQuests)
            {
                refreshed.TrySetResult();
            }
        };
        var team = Team(store);
        await team.LoadAsync();

        team.SharesQuests = false;
        await team.PendingSave;

        await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Share with my squad · on   My loadout · off   My quest progress · off", setup.SharingSummary);
    }

    [Fact]
    public async Task Local_only_marks_the_switch_Blocked_and_links_to_it()
    {
        var network = new NetworkPolicyService(new MemoryControls());
        var team = Team(new MemoryGroupStore(), network);
        await team.LoadAsync();
        Assert.False(team.IsSharingBlocked);

        network.Set(network.Controls with { LocalOnly = true });

        Assert.True(team.IsSharingBlocked);
        Assert.Equal("Blocked · Local only is on", team.SharingBlockedLabel);
        var opened = 0;
        team.OpenDataPrivacyRequested += (_, _) => opened++;
        team.OpenNetworkSettingsCommand.Execute(null);
        Assert.Equal(1, opened);

        network.Set(network.Controls with { LocalOnly = false, SquadSharing = false });
        Assert.Equal("Blocked · Squad sharing is off in Data & Network", team.SharingBlockedLabel);

        network.Set(network.Controls with { SquadSharing = true });
        Assert.False(team.IsSharingBlocked);
    }

    private static TeamWorkspaceViewModel Team(IGroupSettingsStore store, INetworkPolicy? network = null) =>
        new(Session(), store, networkPolicy: network, dispatch: static action => action());

    private static GroupSessionService Session() => new(
        new MemoryGroupStore(),
        new RuntimeStateStore(new(false, true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5))),
        new HttpClient(new OkHandler()) { Timeout = Timeout.InfiniteTimeSpan },
        NullLogger<GroupSessionService>.Instance);

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class MemoryGroupStore : IGroupSettingsStore
    {
        private GroupSharingSettings _stored = GroupSharingSettings.Off;

        public event EventHandler? Changed;

        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_stored);

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken)
        {
            _stored = settings;
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryControls : INetworkControlsStore
    {
        private NetworkControls _controls = NetworkControls.Default;

        public NetworkControls Read() => _controls;

        public void Save(NetworkControls controls) => _controls = controls;
    }
}
