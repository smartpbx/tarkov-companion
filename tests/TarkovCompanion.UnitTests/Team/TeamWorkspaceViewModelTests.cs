using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging.Abstractions;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.App.ViewModels;
using TarkovCompanion.App.ViewModels.V2.Team;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.Team;

public sealed class TeamWorkspaceViewModelTests
{
    [Theory]
    [InlineData(TeamWorkspaceSection.Overview, true, false, false)]
    [InlineData(TeamWorkspaceSection.Group, false, true, false)]
    [InlineData(TeamWorkspaceSection.Devices, false, false, true)]
    public void The_active_route_shows_its_own_pane(
        TeamWorkspaceSection section, bool overview, bool group, bool devices)
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));

        viewModel.SetActiveSection(section);

        Assert.Equal(overview, viewModel.IsOverview);
        Assert.Equal(group, viewModel.IsGroupSection);
        Assert.Equal(devices, viewModel.IsDevicesSection);
    }

    [Fact]
    public void Context_panel_links_go_through_the_attached_navigation()
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));
        // Unattached (as in a test or before the shell exists), a link does nothing rather than fail.
        viewModel.OpenSharedPlanCommand.Execute(null);

        var visited = new List<V2RouteId>();
        viewModel.AttachNavigation(visited.Add);
        viewModel.OpenSharedPlanCommand.Execute(null);
        viewModel.ManageGroupCommand.Execute(null);
        viewModel.ManageDevicesCommand.Execute(null);

        Assert.Equal(new[] { V2Routes.Raid, V2Routes.Group, V2Routes.Tablet }, visited);
    }

    [Fact]
    public void Members_read_as_map_and_raid_state_and_their_shared_quests_are_counted()
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));
        var geo = Member("Geo") with { MapId = "streets-of-tarkov", RaidState = RaidLifecycleState.InRaid, Quests = ["Debut", "Shortage"] };
        var riley = Member("Riley") with { MapId = null, RaidState = RaidLifecycleState.Unknown, Quests = ["shortage "] };
        var group = new GroupSnapshot(true, [geo, riley], "Sharing", DateTimeOffset.UtcNow);

        viewModel.Apply(SnapshotWithGroup(group));

        Assert.Equal("Streets of Tarkov · In raid", viewModel.Presence.Single(row => row.Name == "Geo").Detail);
        Assert.False(viewModel.Presence.Single(row => row.Name == "Riley").HasDetail);
        Assert.Equal("2 sharing", viewModel.MemberCountLabel);
        Assert.Collection(
            viewModel.TeamQuests,
            row => Assert.Equal(("Shortage", "2 members"), (row.Name, row.CountLabel)),
            row => Assert.Equal(("Debut", "1 member"), (row.Name, row.CountLabel)));
    }

    [Fact]
    public async Task Loading_reads_the_stored_group_settings_into_the_form()
    {
        var settings = new FakeGroupSettingsStore(new(
            true, "https://relay.example.test/", "Clay", "a-key-long-enough", true, false));
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), settings);

        await viewModel.LoadAsync();

        Assert.True(viewModel.IsEnabled);
        Assert.Equal("https://relay.example.test/", viewModel.ServerUri);
        Assert.Equal("Clay", viewModel.DisplayName);
        Assert.Equal("a-key-long-enough", viewModel.Key);
        Assert.True(viewModel.SharesLoadout);
        Assert.False(viewModel.SharesQuests);
    }

    [Fact]
    public async Task Saving_writes_the_form_through_to_the_settings_store()
    {
        var settings = new FakeGroupSettingsStore(GroupSharingSettings.Off);
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), settings)
        {
            IsEnabled = true,
            ServerUri = "https://relay.example.test/",
            DisplayName = "Clay",
            Key = "a-key-long-enough",
        };

        await ((AsyncDelegateCommand)viewModel.SaveCommand).ExecuteAsync();

        Assert.NotNull(settings.LastSaved);
        Assert.True(settings.LastSaved!.IsEnabled);
        Assert.Equal("Clay", settings.LastSaved.DisplayName);
        Assert.Contains("Saved", viewModel.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leaving_needs_a_second_press_before_it_turns_sharing_off()
    {
        var settings = new FakeGroupSettingsStore(new(
            true, "https://relay.example.test/", "Clay", "a-key-long-enough", false, false));
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), settings);
        await viewModel.LoadAsync();

        await ((AsyncDelegateCommand)viewModel.LeaveCommand).ExecuteAsync();

        Assert.Equal("Confirm leave", viewModel.LeaveLabel);
        Assert.True(viewModel.IsEnabled);
        Assert.Null(settings.LastSaved);

        await ((AsyncDelegateCommand)viewModel.LeaveCommand).ExecuteAsync();

        Assert.False(viewModel.IsEnabled);
        Assert.NotNull(settings.LastSaved);
        Assert.False(settings.LastSaved!.IsEnabled);
        Assert.Equal("Leave group", viewModel.LeaveLabel);
    }

    [Fact]
    public void Applying_a_snapshot_reads_connection_health_from_whether_the_group_is_stale()
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));

        viewModel.Apply(SnapshotWithGroup(GroupSnapshot.Off));
        Assert.Equal("Not in a group", viewModel.ConnectionHealth);

        var connected = new GroupSnapshot(true, [], "Sharing as Clay · nobody else here", DateTimeOffset.UtcNow);
        viewModel.Apply(SnapshotWithGroup(connected));
        Assert.Equal("Connected", viewModel.ConnectionHealth);

        var reconnecting = connected with { StaleSince = DateTimeOffset.UtcNow.AddSeconds(-10) };
        viewModel.Apply(SnapshotWithGroup(reconnecting));
        Assert.Equal("Reconnecting", viewModel.ConnectionHealth);
    }

    [Fact]
    public void Presence_rows_are_live_stale_or_offline()
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));
        var live = Member("Geo") with { Since = TimeSpan.FromSeconds(3) };
        var quiet = Member("Riley") with { Since = TimeSpan.FromMinutes(2) };
        var group = new GroupSnapshot(true, [live, quiet], "Sharing", DateTimeOffset.UtcNow);

        viewModel.Apply(SnapshotWithGroup(group));

        Assert.Equal(2, viewModel.Presence.Count);
        Assert.Equal(TeamPresenceState.Live, viewModel.Presence.Single(row => row.Name == "Geo").State);
        Assert.Equal(TeamPresenceState.Stale, viewModel.Presence.Single(row => row.Name == "Riley").State);

        // Reconnecting on a last-good read: nobody can be vouched for as live or stale any more.
        viewModel.Apply(SnapshotWithGroup(group with { StaleSince = DateTimeOffset.UtcNow }));
        Assert.All(viewModel.Presence, row => Assert.Equal(TeamPresenceState.Offline, row.State));
    }

    [Fact]
    public void Waypoints_are_numbered_like_the_map_list_and_pings_carry_remaining_time()
    {
        var clock = new Clock();
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off), clock: clock);
        var waypointA = new GroupWaypointView(1, "Geo", "customs", 0, 0, 0, null, null) { CreatedUtc = clock.GetUtcNow().AddMinutes(-2) };
        var waypointB = new GroupWaypointView(2, "Riley", "customs", 0, 0, 0, "Extract", "Geo") { CreatedUtc = clock.GetUtcNow().AddMinutes(-1) };
        var ping = new GroupPingView(3, "Geo", "customs", 0, 0, 0, null, clock.GetUtcNow().AddSeconds(-40));
        var group = new GroupSnapshot(true, [], "Sharing", DateTimeOffset.UtcNow)
        {
            Waypoints = [waypointA, waypointB],
            Pings = [ping],
        };

        viewModel.Apply(SnapshotWithGroup(group));

        Assert.Equal(3, viewModel.Marks.Count);
        var first = viewModel.Marks[0];
        Assert.Equal("1", first.Name);
        Assert.Equal("marked by Geo", first.ByLabel);
        Assert.False(first.IsReached);

        var second = viewModel.Marks[1];
        Assert.Equal("Extract", second.Name);
        Assert.Contains("reached by Geo", second.ByLabel, StringComparison.Ordinal);
        Assert.True(second.IsReached);

        Assert.Equal(new[] { "1", "2" }, viewModel.Waypoints.Select(row => row.Number));
        Assert.Equal("Waypoint 1", first.Title);
        Assert.Equal("Customs · by Geo · 2m 0s ago", first.Detail);
        Assert.Equal("Extract", second.Title);

        var pingRow = viewModel.Marks[2];
        Assert.Same(pingRow, viewModel.Pings.Single());
        Assert.Null(pingRow.Number);
        Assert.Equal("Ping", pingRow.Kind);
        Assert.Equal("Ping", pingRow.Name);
        Assert.NotNull(pingRow.RemainingLabel);
        Assert.True(pingRow.HasRemaining);
    }

    [Fact]
    public void Waypoints_are_numbered_per_map_so_the_list_matches_each_map()
    {
        var waypoints = new GroupWaypointView[]
        {
            new(1, "Geo", "customs", 0, 0, 0, null, null),
            new(2, "Geo", "woods", 0, 0, 0, null, null),
            new(3, "Geo", "Customs", 0, 0, 0, null, null),
        };

        var numbered = TeamWorkspaceViewModel.NumberWaypoints(waypoints);

        Assert.Equal(new[] { 1, 1, 2 }, numbered.Select(item => item.Number));
    }

    [Fact]
    public void The_centre_map_draws_numbered_waypoints_joined_in_order_and_skips_what_it_cannot_place()
    {
        var group = new GroupSnapshot(true, [], "Sharing", DateTimeOffset.UtcNow)
        {
            Waypoints =
            [
                new(1, "Geo", "customs", 10, 0, 10, "Dorms", null),
                new(2, "Geo", "woods", 20, 0, 20, null, null),
                new(3, "Riley", "customs", 30, 0, 30, null, "Geo"),
                new(4, "Riley", "customs", -1, 0, -1, null, null),
            ],
            Pings = [new(5, "Sam", "customs", 40, 0, 40, null, DateTimeOffset.UtcNow)],
        };

        var (layer, objects) = TeamWorkspaceViewModel.BuildGroupMarks(
            group,
            mapId => mapId == "customs",
            position => position.X < 0 ? null : new MapScenePoint(position.X, position.Z),
            DateTimeOffset.UtcNow);

        Assert.NotNull(layer);
        var route = Assert.Single(objects, item => item.Kind == MapSceneObjectKind.Route);
        Assert.Equal(new[] { 10.0, 30.0 }, route.Geometry.Points.Select(point => point.X));
        var waypoints = objects.Where(item => item.Kind == MapSceneObjectKind.Waypoint).ToArray();
        // The woods waypoint is off this map, and the fourth cannot be placed; numbers stay the list's.
        Assert.Equal(new[] { "1", "2" }, waypoints.Select(item => item.Label));
        Assert.Equal("group-waypoint:3", waypoints[1].Id.Value);
        Assert.Contains("reached by Geo", waypoints[1].Detail, StringComparison.Ordinal);
        Assert.Single(objects, item => item.Kind == MapSceneObjectKind.Ping);
    }

    [Fact]
    public void Without_marks_on_the_map_there_is_no_marks_layer()
    {
        var (layer, objects) = TeamWorkspaceViewModel.BuildGroupMarks(
            GroupSnapshot.Off, _ => true, position => new MapScenePoint(position.X, position.Z), DateTimeOffset.UtcNow);

        Assert.Null(layer);
        Assert.Empty(objects);
    }

    [Fact]
    public void Without_a_raid_map_the_centre_map_says_how_to_get_one()
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));

        viewModel.RefreshMapPreview();

        Assert.False(viewModel.HasMapPreview);
        Assert.NotEmpty(viewModel.MapNote);
        Assert.Equal("None yet", viewModel.MarksSummary);
        Assert.NotNull(viewModel.PairTabletTooltip);
    }

    [Fact]
    public async Task Removing_a_mark_calls_through_to_the_group_session()
    {
        long? deleted = null;
        var handler = new StubHandler(request =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                deleted = long.Parse(request.RequestUri!.Segments[^1], System.Globalization.CultureInfo.InvariantCulture);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var session = GroupSession(handler);
        var viewModel = new TeamWorkspaceViewModel(session, new FakeGroupSettingsStore(GroupSharingSettings.Off));
        var waypoint = new GroupWaypointView(42, "Geo", "customs", 0, 0, 0, null, null);
        viewModel.Apply(SnapshotWithGroup(new GroupSnapshot(true, [], "Sharing", DateTimeOffset.UtcNow) { Waypoints = [waypoint] }));

        await ((AsyncDelegateCommand)viewModel.Marks.Single().RemoveCommand!).ExecuteAsync();

        Assert.Equal(42, deleted);
    }

    [Fact]
    public void Without_a_pairing_view_model_the_devices_section_degrades_clearly()
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));

        Assert.True(viewModel.HasNoDevices);
        Assert.False(viewModel.CanPairDevice);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.PairingUnavailableReason));

        // Never throws even with nothing to pair against.
        viewModel.PairTabletCommand.Execute(null);
    }

    [Fact]
    public void A_presence_row_says_where_the_member_was_and_what_they_shared()
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));
        var geo = Member("Geo") with
        {
            Position = new(312, 0, -104),
            PositionAge = TimeSpan.FromSeconds(40),
            Loadout = ["Primary: AK-74N"],
            Quests = ["Debut"],
        };
        var riley = Member("Riley");

        viewModel.Apply(SnapshotWithGroup(new GroupSnapshot(true, [geo, riley], "Sharing", DateTimeOffset.UtcNow)));

        var row = viewModel.Presence.Single(candidate => candidate.Name == "Geo");
        Assert.Equal("312, -104 · from a screenshot 40s ago", row.Position);
        Assert.Equal("Primary: AK-74N · Debut", row.Shared);
        Assert.True(row.HasPosition && row.HasShared);

        // V1 said "No screenshot position shared" in words; V2 leaves the line out instead.
        var quiet = viewModel.Presence.Single(candidate => candidate.Name == "Riley");
        Assert.False(quiet.HasPosition);
        Assert.False(quiet.HasShared);
    }

    [Fact]
    public void The_group_tells_a_player_what_their_own_game_never_wrote_down()
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));

        viewModel.Apply(SnapshotWithGroup(new GroupSnapshot(true, [], "Sharing", DateTimeOffset.UtcNow)
        {
            MyLoadout = ["Primary: AK-74N", "Rig: Slick"],
            MyLevel = 42,
            MySide = "Usec",
        }));

        Assert.Equal("Primary: AK-74N · Rig: Slick", viewModel.MyLoadout);
        Assert.Equal("Level 42 · Usec", viewModel.MyProfile);
        Assert.True(viewModel.HasMyProfile);

        // Nobody has said anything yet: the profile line is absent rather than a row of "not known".
        viewModel.Apply(SnapshotWithGroup(new GroupSnapshot(true, [], "Sharing", DateTimeOffset.UtcNow)));
        Assert.Equal("Nobody in your party is running this yet.", viewModel.MyLoadout);
        Assert.False(viewModel.HasMyProfile);
    }

    [Fact]
    public void The_in_game_party_is_absent_until_the_shell_attaches_the_squad_view_model()
    {
        var viewModel = new TeamWorkspaceViewModel(GroupSession(), new FakeGroupSettingsStore(GroupSharingSettings.Off));
        Assert.False(viewModel.HasParty);
        Assert.Null(viewModel.Party);

        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        var party = new SquadPageViewModel(new StubItemRepository());
        viewModel.AttachParty(party);

        Assert.Same(party, viewModel.Party);
        Assert.True(viewModel.HasParty);
        Assert.Contains(nameof(TeamWorkspaceViewModel.Party), changed);
        Assert.Contains(nameof(TeamWorkspaceViewModel.HasParty), changed);
    }

    private static GroupMemberView Member(string name) =>
        new(name, null, RaidLifecycleState.Unknown, null, null, null, null, [], []);

    private static ApplicationRuntimeSnapshot SnapshotWithGroup(GroupSnapshot group) =>
        V2ShellTestData.Snapshot() with { Group = group };

    /// <summary>
    /// A <see cref="GroupSessionService"/> to hand to the workspace's constructor. Its own
    /// settings are usable (unlike the workspace's own <see cref="FakeGroupSettingsStore"/> in
    /// most tests here) because <see cref="GroupSessionService.RemoveMarkAsync"/> refuses to call
    /// the relay at all when its settings are not — the two stores are logically the same one in
    /// production, but nothing in these tests depends on that.
    /// </summary>
    private static GroupSessionService GroupSession(StubHandler? handler = null) => new(
        new FakeGroupSettingsStore(new(
            true, "https://relay.example.test/", "Clay", "a-key-long-enough", false, false)),
        new RuntimeStateStore(new(false, true, GameMode.Regular, "en", TimeSpan.FromHours(9), TimeSpan.FromMinutes(5))),
        new HttpClient(handler ?? new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))) { Timeout = Timeout.InfiniteTimeSpan },
        NullLogger<GroupSessionService>.Instance);

    /// <summary>The squad view model only looks item names up; none of these tests has a party to name.</summary>
    private sealed class StubItemRepository : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemDefinition?>(null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult<ItemPriceSnapshot?>(null);
    }

    private sealed class Clock : TimeProvider
    {
        private readonly DateTimeOffset _now = new(2026, 9, 15, 18, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request);
    }

    private sealed class FakeGroupSettingsStore(GroupSharingSettings stored) : IGroupSettingsStore
    {
        public GroupSharingSettings? LastSaved { get; private set; }

        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) => Task.FromResult(stored);

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken)
        {
            LastSaved = settings;
            return Task.CompletedTask;
        }
    }
}
