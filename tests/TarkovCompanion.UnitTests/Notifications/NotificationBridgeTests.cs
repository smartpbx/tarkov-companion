using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.Notifications;

/// <summary>
/// [V2 rough package 43] What the bridge actually shows the rules. Everything it gets wrong here
/// is something the coordinator can never get right, however good its own tests are.
/// </summary>
public sealed class NotificationBridgeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Waypoints_and_pings_both_count_as_marks_and_never_collide()
    {
        using var bridge = Build();
        var snapshot = Snapshot() with
        {
            Group = new GroupSnapshot(true, [], "Sharing", Now)
            {
                // The same relay id on both lists: a waypoint 7 and a ping 7 are two marks, and
                // treating them as one would silently drop somebody's ping.
                Waypoints = [Waypoint(7, "Ferret")],
                Pings = [Ping(7, "Sam")],
            },
        };

        var inputs = bridge.BuildInputs(snapshot);

        Assert.Equal(2, inputs.SquadMarks.Count);
        Assert.Equal(2, inputs.SquadMarks.Select(mark => mark.Id).Distinct().Count());
        Assert.Contains(inputs.SquadMarks, mark => mark is { By: "Ferret", IsPing: false });
        Assert.Contains(inputs.SquadMarks, mark => mark is { By: "Sam", IsPing: true });
    }

    [Fact]
    public void The_failed_endpoints_are_carried_as_names_rather_than_as_a_sentence()
    {
        using var bridge = Build();
        var snapshot = Snapshot() with
        {
            Data = new RuntimeDataState(DataAvailability.Cached, 10, 5, Now, "items: HTTP 503 · local data stands")
            {
                FailedEndpoints = ["items"],
            },
        };

        Assert.Equal(["items"], bridge.BuildInputs(snapshot).FailedDataEndpoints);
    }

    [Fact]
    public void The_raid_and_relay_state_reach_the_rules_unchanged()
    {
        using var bridge = Build();
        var raidId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var snapshot = Snapshot() with
        {
            Raid = new RaidSnapshot(raidId, RaidLifecycleState.InRaid, "customs", Now, Now, Confidence.Unknown, null, [], false),
            Group = new GroupSnapshot(true, [], "Sharing", Now) { StaleSince = Now.AddMinutes(-1) },
        };

        var inputs = bridge.BuildInputs(snapshot);

        Assert.Equal(RaidLifecycleState.InRaid, inputs.RaidState);
        Assert.Equal(raidId, inputs.RaidId);
        Assert.True(inputs.IsSharing);
        Assert.Equal(Now.AddMinutes(-1), inputs.RelayStaleSince);
    }

    [Fact]
    public void Nothing_is_waiting_to_install_when_no_settings_page_says_so()
    {
        using var bridge = Build();

        Assert.Null(bridge.BuildInputs(Snapshot()).UpdateReadyBuild);
    }

    [Fact]
    public void A_test_press_sends_the_real_notification_through_the_real_channels()
    {
        var channel = new RecordingChannel();
        using var bridge = Build(channel);

        bridge.Test(NotificationKind.RelayUnreachable);

        var sent = Assert.Single(channel.Sent);
        Assert.Equal(NotificationKind.RelayUnreachable, sent.Kind);
        Assert.Contains("Test", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_press_works_on_a_notification_that_is_switched_off()
    {
        // Pressing "test this" on one you have turned off is how you decide whether to turn it on.
        var channel = new RecordingChannel();
        using var bridge = Build(channel);
        await bridge.SetEnabledAsync(NotificationKind.UpdateReady, false, CancellationToken.None);

        bridge.Test(NotificationKind.UpdateReady);

        Assert.Single(channel.Sent);
    }

    [Fact]
    public async Task The_popup_stays_out_of_it_until_it_is_asked_for()
    {
        var quiet = new RecordingChannel();
        var popup = new RecordingChannel();
        using var bridge = Build(quiet, popup);

        bridge.Test(NotificationKind.DebriefReady);
        Assert.Single(quiet.Sent);
        Assert.Empty(popup.Sent);

        await bridge.SetPopupAsync(true, CancellationToken.None);
        bridge.Test(NotificationKind.DebriefReady);

        Assert.Single(popup.Sent);
    }

    [Fact]
    public async Task A_switch_is_remembered()
    {
        var store = new RecordingSettingsStore();
        using var bridge = Build(settingsStore: store);

        await bridge.SetEnabledAsync(NotificationKind.SquadMark, false, CancellationToken.None);

        Assert.False(bridge.Settings.SquadMark);
        Assert.False(Assert.Single(store.Saved).SquadMark);
    }

    private static ApplicationRuntimeSnapshot Snapshot() => V2ShellTestData.Snapshot();

    private static NotificationBridge Build(
        RecordingChannel? quiet = null,
        RecordingChannel? popup = null,
        INotificationSettingsStore? settingsStore = null) => new(
        new FakeRuntimeStore(Snapshot()),
        settingsStore ?? new RecordingSettingsStore(),
        new FakeGroupSettingsStore(),
        [quiet ?? new RecordingChannel()],
        () => popup,
        settingsPage: null,
        timeProvider: TimeProvider.System);

    private static GroupWaypointView Waypoint(long id, string by) => new(id, by, "customs", 1, 0, 2, null, null);

    private static GroupPingView Ping(long id, string by) => new(id, by, "customs", 3, 0, 4, null, Now);

    private sealed class RecordingChannel : INotificationChannel
    {
        public List<NotificationRequest> Sent { get; } = [];

        public void Show(NotificationRequest request) => Sent.Add(request);
    }

    private sealed class RecordingSettingsStore : INotificationSettingsStore
    {
        public List<NotificationSettings> Saved { get; } = [];

        public Task<NotificationSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NotificationSettings.Default);

        public Task SaveAsync(NotificationSettings settings, CancellationToken cancellationToken)
        {
            Saved.Add(settings);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGroupSettingsStore : IGroupSettingsStore
    {
        public Task<GroupSharingSettings> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GroupSharingSettings(true, "https://relay.test", "Clayton", "key", false, false));

        public Task SaveAsync(GroupSharingSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeRuntimeStore(ApplicationRuntimeSnapshot current) : IRuntimeStateStore
    {
#pragma warning disable CS0067
        public event EventHandler? Changed;
#pragma warning restore CS0067

        public ApplicationRuntimeSnapshot Current { get; private set; } = current;

        public void Update(Func<ApplicationRuntimeSnapshot, ApplicationRuntimeSnapshot> update) =>
            Current = update(Current);
    }
}
