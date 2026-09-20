using TarkovCompanion.App.Services.V2.Notifications;
using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.V2Shell;

namespace TarkovCompanion.UnitTests.Notifications;

/// <summary>
/// [V2 rough package 43] What the tray icon says without being opened. This is the whole of the
/// "at a glance" the brief asks for, so it is worth checking it reads correctly in each state.
/// </summary>
public sealed class TrayPresenceStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_raid_in_progress_names_the_map_and_colours_the_icon()
    {
        var text = TrayPresenceState.Describe(InRaid("customs"), unread: 0);

        Assert.Equal(TrayStatus.InRaid, text.Status);
        Assert.Equal("In raid · customs", text.RaidLine);
        Assert.Contains("Tarkov Companion", text.Tooltip, StringComparison.Ordinal);
        Assert.Contains("In raid · customs", text.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finished_raid_says_the_debrief_is_waiting()
    {
        var text = TrayPresenceState.Describe(Raid(RaidLifecycleState.PostRaid), unread: 0);

        Assert.Equal(TrayStatus.PostRaid, text.Status);
        Assert.Contains("debrief", text.RaidLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_dead_relay_outranks_the_raid_state_because_it_is_the_thing_that_needs_somebody()
    {
        var snapshot = InRaid("customs") with
        {
            Group = new GroupSnapshot(true, [], "Sharing", Now) { StaleSince = Now.AddMinutes(-2) },
        };

        var text = TrayPresenceState.Describe(snapshot, unread: 0);

        Assert.Equal(TrayStatus.Attention, text.Status);
        Assert.Equal("Relay unreachable", text.GroupLine);
    }

    [Fact]
    public void A_relay_that_is_off_is_not_a_problem_to_report()
    {
        var text = TrayPresenceState.Describe(Raid(RaidLifecycleState.Menu), unread: 0);

        Assert.Equal(TrayStatus.Menu, text.Status);
        Assert.Equal("Not sharing", text.GroupLine);
    }

    [Fact]
    public void Sharing_counts_the_people_it_is_sharing_with()
    {
        var snapshot = Raid(RaidLifecycleState.Menu) with
        {
            Group = new GroupSnapshot(true, [Member("Ferret"), Member("Sam")], "Sharing", Now),
        };

        Assert.Equal("Sharing with 2", TrayPresenceState.Describe(snapshot, unread: 0).GroupLine);
    }

    [Fact]
    public void Anything_unread_is_counted_where_it_can_be_seen_without_opening_anything()
    {
        Assert.Equal(string.Empty, TrayPresenceState.Describe(InRaid("customs"), 0).UnreadLine);
        Assert.Equal("1 new", TrayPresenceState.Describe(InRaid("customs"), 1).UnreadLine);

        var many = TrayPresenceState.Describe(InRaid("customs"), 4);

        Assert.Equal("4 new", many.UnreadLine);
        Assert.Contains("4 new", many.Tooltip, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tooltip_stays_inside_what_a_tray_will_actually_show()
    {
        // Windows truncates past 127 characters, and a truncated tooltip loses the end, which is
        // where the unread count is.
        var snapshot = InRaid(new string('m', 200)) with
        {
            Group = new GroupSnapshot(true, [Member("Ferret")], "Sharing", Now),
        };

        var text = TrayPresenceState.Describe(snapshot, unread: 12);

        Assert.True(text.Tooltip.Length <= TrayPresenceState.MaximumTooltipLength);
    }

    private static ApplicationRuntimeSnapshot InRaid(string mapId) =>
        Raid(RaidLifecycleState.InRaid, mapId);

    private static ApplicationRuntimeSnapshot Raid(RaidLifecycleState state, string? mapId = null) =>
        V2ShellTestData.Snapshot() with
        {
            Raid = new RaidSnapshot(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                state,
                mapId,
                Now.AddMinutes(-5),
                Now,
                Confidence.Unknown,
                null,
                [],
                false),
        };

    private static GroupMemberView Member(string name) =>
        new(name, "customs", RaidLifecycleState.InRaid, "PMC", null, null, null, [], []);
}
