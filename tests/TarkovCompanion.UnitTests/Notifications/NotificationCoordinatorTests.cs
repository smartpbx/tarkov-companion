using TarkovCompanion.Application.Services.Notifications;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Notifications;

/// <summary>
/// [V2 rough package 43] Which of the five notifications fire, which stay quiet, and what a burst
/// of them reads as. Every rule here is the difference between a second monitor worth glancing at
/// and one worth turning off.
/// </summary>
public sealed class NotificationCoordinatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 18, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_squadmate_mark_during_a_raid_is_the_one_that_interrupts()
    {
        var coordinator = new NotificationCoordinator();

        coordinator.Observe(InRaid(marks: [new(1, "Ferret", IsPing: true)]), NotificationSettings.Default);
        var raised = coordinator.Observe(InRaid(at: Start.AddSeconds(5)), NotificationSettings.Default);

        var notification = Assert.Single(raised);
        Assert.Equal(NotificationKind.SquadMark, notification.Kind);
        Assert.Equal("Ferret dropped a ping", notification.Title);
        Assert.Equal(1, notification.Count);
    }

    [Fact]
    public void The_same_mark_is_never_announced_twice()
    {
        var coordinator = new NotificationCoordinator();
        coordinator.Observe(InRaid(marks: [new(1, "Ferret", false)]), NotificationSettings.Default);
        Assert.Single(coordinator.Observe(InRaid(at: Start.AddSeconds(5), marks: [new(1, "Ferret", false)]), NotificationSettings.Default));

        var again = coordinator.Observe(
            InRaid(at: Start.AddSeconds(30), marks: [new(1, "Ferret", false)]),
            NotificationSettings.Default);

        Assert.Empty(again);
    }

    [Fact]
    public void Your_own_mark_is_never_announced_back_to_you()
    {
        var coordinator = new NotificationCoordinator();

        coordinator.Observe(
            InRaid(marks: [new(1, "Clayton", false), new(2, "Ferret", false)]),
            NotificationSettings.Default);
        var raised = coordinator.Observe(InRaid(at: Start.AddSeconds(5)), NotificationSettings.Default);

        var notification = Assert.Single(raised);
        Assert.Equal("Ferret dropped a mark", notification.Title);
        Assert.Equal(1, notification.Count);
    }

    [Fact]
    public void A_burst_becomes_one_line_naming_the_one_person_who_sent_it()
    {
        var coordinator = new NotificationCoordinator();

        coordinator.Observe(
            InRaid(marks: [new(1, "Ferret", false), new(2, "Ferret", false), new(3, "Ferret", true)]),
            NotificationSettings.Default);
        var raised = coordinator.Observe(InRaid(at: Start.AddSeconds(5)), NotificationSettings.Default);

        var notification = Assert.Single(raised);
        Assert.Equal("3 marks from Ferret", notification.Title);
        Assert.Equal(3, notification.Count);
    }

    [Fact]
    public void A_burst_from_several_people_counts_the_people_rather_than_listing_them()
    {
        var coordinator = new NotificationCoordinator();

        coordinator.Observe(
            InRaid(marks: [new(1, "Ferret", false), new(2, "Sam", false), new(3, "Sam", false)]),
            NotificationSettings.Default);
        var raised = coordinator.Observe(InRaid(at: Start.AddSeconds(5)), NotificationSettings.Default);

        Assert.Equal("3 marks from 2 squadmates", Assert.Single(raised).Title);
    }

    [Fact]
    public void A_big_burst_does_not_wait_out_the_window()
    {
        var coordinator = new NotificationCoordinator();

        // Five at once is somebody hammering the ping key; waiting four more seconds to say so
        // would be four seconds of the thing the notification exists to shorten.
        var raised = coordinator.Observe(
            InRaid(marks:
            [
                new(1, "Ferret", true), new(2, "Ferret", true), new(3, "Ferret", true),
                new(4, "Ferret", true), new(5, "Ferret", true),
            ]),
            NotificationSettings.Default);

        Assert.Equal("5 marks from Ferret", Assert.Single(raised).Title);
    }

    [Fact]
    public void A_mark_dropped_outside_a_raid_is_seen_but_not_announced_and_does_not_come_back()
    {
        var coordinator = new NotificationCoordinator();

        Assert.Empty(coordinator.Observe(
            Inputs(RaidLifecycleState.Menu, marks: [new(1, "Ferret", false)]),
            NotificationSettings.Default));

        // The raid starts and the relay still lists it. Replaying the lobby on landing would be
        // the first thing this feature did wrong.
        coordinator.Observe(InRaid(at: Start.AddSeconds(30), marks: [new(1, "Ferret", false)]), NotificationSettings.Default);
        Assert.Empty(coordinator.Observe(InRaid(at: Start.AddSeconds(40)), NotificationSettings.Default));
    }

    [Fact]
    public void A_switched_off_notification_stays_off()
    {
        var coordinator = new NotificationCoordinator();
        var off = NotificationSettings.Default.With(NotificationKind.SquadMark, false);

        coordinator.Observe(InRaid(marks: [new(1, "Ferret", false)]), off);

        Assert.Empty(coordinator.Observe(InRaid(at: Start.AddSeconds(5)), off));
    }

    [Fact]
    public void A_finished_raid_offers_its_debrief_once()
    {
        var coordinator = new NotificationCoordinator();
        var raidId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        coordinator.Observe(InRaid(raidId: raidId), NotificationSettings.Default);

        var raised = coordinator.Observe(
            Inputs(RaidLifecycleState.PostRaid, at: Start.AddMinutes(20), raidId: raidId),
            NotificationSettings.Default);
        Assert.Equal(NotificationKind.DebriefReady, Assert.Single(raised).Kind);

        Assert.Empty(coordinator.Observe(
            Inputs(RaidLifecycleState.PostRaid, at: Start.AddMinutes(21), raidId: raidId),
            NotificationSettings.Default));
    }

    [Fact]
    public void A_failed_refresh_names_the_endpoints_that_did_not_answer()
    {
        var coordinator = new NotificationCoordinator();

        var raised = coordinator.Observe(
            Inputs(RaidLifecycleState.Menu, failedEndpoints: ["tasks", "items"]),
            NotificationSettings.Default);

        var notification = Assert.Single(raised);
        Assert.Equal(NotificationKind.DataRefreshFailed, notification.Kind);
        Assert.Contains("items and tasks", notification.Body, StringComparison.Ordinal);
        Assert.Equal(2, notification.Count);
    }

    [Fact]
    public void The_same_endpoints_failing_again_is_not_news_until_they_recover()
    {
        var coordinator = new NotificationCoordinator();
        var failing = Inputs(RaidLifecycleState.Menu, failedEndpoints: ["items"]);
        Assert.Single(coordinator.Observe(failing, NotificationSettings.Default));
        Assert.Empty(coordinator.Observe(failing, NotificationSettings.Default));

        // A clean refresh in between makes the next failure news again.
        coordinator.Observe(Inputs(RaidLifecycleState.Menu), NotificationSettings.Default);

        Assert.Single(coordinator.Observe(failing, NotificationSettings.Default));
    }

    [Theory]
    [InlineData(NotificationKind.DebriefReady)]
    [InlineData(NotificationKind.DataRefreshFailed)]
    [InlineData(NotificationKind.UpdateReady)]
    [InlineData(NotificationKind.RelayUnreachable)]
    public void Everything_except_a_squadmate_mark_waits_until_the_raid_is_over(NotificationKind kind)
    {
        var coordinator = new NotificationCoordinator();
        var duringRaid = Trigger(kind, RaidLifecycleState.InRaid, Start);

        Assert.Empty(coordinator.Observe(duringRaid, NotificationSettings.Default));

        // Suppressed, not swallowed: the condition is still true a minute later, out of the raid.
        var afterRaid = Trigger(kind, RaidLifecycleState.Menu, Start.AddMinutes(1));
        Assert.Equal(kind, Assert.Single(coordinator.Observe(afterRaid, NotificationSettings.Default)).Kind);
    }

    [Fact]
    public void A_downloaded_build_is_offered_once_per_build()
    {
        var coordinator = new NotificationCoordinator();
        var ready = Inputs(RaidLifecycleState.Menu, updateReadyBuild: "1.4.0");
        Assert.Equal(NotificationKind.UpdateReady, Assert.Single(coordinator.Observe(ready, NotificationSettings.Default)).Kind);
        Assert.Empty(coordinator.Observe(ready, NotificationSettings.Default));

        var newer = Inputs(RaidLifecycleState.Menu, updateReadyBuild: "1.4.1");

        Assert.Single(coordinator.Observe(newer, NotificationSettings.Default));
    }

    [Fact]
    public void A_relay_outage_is_one_notification_and_recovering_arms_the_next_one()
    {
        var coordinator = new NotificationCoordinator();
        var down = Inputs(RaidLifecycleState.Menu, isSharing: true, relayStaleSince: Start);
        Assert.Equal(NotificationKind.RelayUnreachable, Assert.Single(coordinator.Observe(down, NotificationSettings.Default)).Kind);
        Assert.Empty(coordinator.Observe(down, NotificationSettings.Default));

        coordinator.Observe(Inputs(RaidLifecycleState.Menu, isSharing: true), NotificationSettings.Default);

        Assert.Single(coordinator.Observe(down, NotificationSettings.Default));
    }

    [Fact]
    public void A_relay_that_is_not_being_shared_with_cannot_be_unreachable()
    {
        var coordinator = new NotificationCoordinator();

        var raised = coordinator.Observe(
            Inputs(RaidLifecycleState.Menu, isSharing: false, relayStaleSince: Start),
            NotificationSettings.Default);

        Assert.Empty(raised);
    }

    [Fact]
    public void Nothing_at_all_happening_says_nothing_at_all()
    {
        var coordinator = new NotificationCoordinator();

        Assert.Empty(coordinator.Observe(Inputs(RaidLifecycleState.Menu), NotificationSettings.Default));
        Assert.Empty(coordinator.Observe(InRaid(), NotificationSettings.Default));
    }

    /// <summary>The one condition that raises the given kind, at the given raid state.</summary>
    private static NotificationInputs Trigger(NotificationKind kind, RaidLifecycleState state, DateTimeOffset at) => kind switch
    {
        NotificationKind.DebriefReady => Inputs(
            RaidLifecycleState.PostRaid,
            at: at,
            raidId: Guid.Parse("22222222-2222-2222-2222-222222222222")) with
        {
            // PostRaid is the trigger, so "during a raid" for this one means the raid has not
            // reported its end yet; InRaid is what the coordinator is asked to stay quiet through.
            RaidState = state == RaidLifecycleState.InRaid ? RaidLifecycleState.InRaid : RaidLifecycleState.PostRaid,
        },
        NotificationKind.DataRefreshFailed => Inputs(state, at: at, failedEndpoints: ["items"]),
        NotificationKind.UpdateReady => Inputs(state, at: at, updateReadyBuild: "1.4.0"),
        NotificationKind.RelayUnreachable => Inputs(state, at: at, isSharing: true, relayStaleSince: at),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static NotificationInputs InRaid(
        DateTimeOffset? at = null,
        IReadOnlyList<SquadMarkInput>? marks = null,
        Guid? raidId = null) =>
        Inputs(RaidLifecycleState.InRaid, at, marks, raidId);

    private static NotificationInputs Inputs(
        RaidLifecycleState state,
        DateTimeOffset? at = null,
        IReadOnlyList<SquadMarkInput>? marks = null,
        Guid? raidId = null,
        IReadOnlyList<string>? failedEndpoints = null,
        string? updateReadyBuild = null,
        bool isSharing = false,
        DateTimeOffset? relayStaleSince = null) => new()
        {
            NowUtc = at ?? Start,
            RaidState = state,
            RaidId = raidId,
            PlayerName = "Clayton",
            SquadMarks = marks ?? [],
            FailedDataEndpoints = failedEndpoints ?? [],
            UpdateReadyBuild = updateReadyBuild,
            IsSharing = isSharing,
            RelayStaleSince = relayStaleSince,
        };
}
