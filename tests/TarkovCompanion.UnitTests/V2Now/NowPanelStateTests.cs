using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Loot;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>[#712 0-4] Each situation phase gives the Now panel the blocks the epic names, in human units.</summary>
public sealed class NowPanelStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void In_raid_shows_every_block_with_the_count_labelled_as_a_count()
    {
        using var scope = English();
        var state = NowPanelState.Project(InRaid(minutesIn: 19, length: 35), Now, [Exit("ZB-1011", 310, "W", offered: true)]);

        Assert.True(state.ShowsYou);
        Assert.True(state.ShowsSquad);
        Assert.True(state.ShowsNext);
        Assert.True(state.ShowsScan);
        Assert.Equal("16:00 left", state.NowHeadline);
        Assert.Equal(NowText.ClockCounted, state.NowDetail);
        Assert.Equal(NowText.RunThroughEnded("7:00"), state.NowNote);
        Assert.True(state.NowNoteIsDone);
        Assert.Equal(NowTone.Normal, state.Tone);
        Assert.Equal("Dorms 3-story · 2nd floor · facing NE", state.YouWhere);
        Assert.Equal("12 s ago", state.YouAge);
        Assert.Equal("Nearest offered exit: ZB-1011 · 310 m W · ~4 min", state.YouExit);
        Assert.False(state.HasYouExitNote);
        Assert.Equal("Golden Zibbo lighter", state.NextLabel);
        Assert.Equal("40 m leg", state.NextDetail);
        Assert.Equal("then: Bronze pocket watch", state.ThenLabel);
        Assert.Equal(NowText.ScanNone, state.ScanNone);
        Assert.True(state.ShowsScanHint);
    }

    [Fact]
    public void Early_in_the_raid_says_until_when_it_counts_as_a_run_through()
    {
        using var scope = English();
        var state = NowPanelState.Project(InRaid(minutesIn: 3, length: 35), Now);

        Assert.Equal(NowText.RunThroughUntil("7:00"), state.NowNote);
        Assert.False(state.NowNoteIsDone);
    }

    [Fact]
    public void A_clock_read_off_the_extract_screen_says_where_and_how_long_ago()
    {
        using var scope = English();
        var situation = InRaid(minutesIn: 19, length: 35) with
        {
            Clock = new(SituationClockBasis.Observed, TimeSpan.FromMinutes(20), Now.AddMinutes(-3), Now.AddMinutes(-19), "read"),
        };

        var state = NowPanelState.Project(situation, Now);

        Assert.Equal("17:00 left", state.NowHeadline);
        Assert.Equal(NowText.ClockObserved("3 min ago"), state.NowDetail);
    }

    [Fact]
    public void Late_raid_turns_amber_and_names_the_time_to_leave_then_red_once_it_has_passed()
    {
        using var scope = English();
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
        var late = NowPanelState.Project(InRaid(minutesIn: 29, length: 35), Now, [Exit("ZB-1011", 310, "W", offered: true)]);

        // 6 minutes left, 4 minutes' walk, 2 minutes' margin: leave now.
        Assert.Equal(NowTone.Urgent, late.Tone);
        Assert.Equal(NowText.HeadingLateRaid, late.NowHeading);
        Assert.Equal(NowText.LeaveNow("ZB-1011", 4), late.NowNote);

        var earlier = NowPanelState.Project(InRaid(minutesIn: 26, length: 35), Now, [Exit("ZB-1011", 310, "W", offered: true)]);
        Assert.Equal(NowTone.Late, earlier.Tone);
        Assert.Equal(NowText.LeaveBy("ZB-1011", LocalTime.ShortTime(Now.AddMinutes(3)), 4, 2), earlier.NowNote);

        var past = NowPanelState.Project(InRaid(minutesIn: 32, length: 35), Now, [Exit("ZB-1011", 310, "W", offered: true)]);
        Assert.Equal(NowTone.Urgent, past.Tone);
        Assert.Equal(NowText.LeaveNow("ZB-1011", 4), past.NowNote);
    }

    [Fact]
    public void An_offered_exit_beats_a_nearer_one_nobody_saw_offered_and_a_transit_is_never_named()
    {
        var chosen = NowPanelState.ChooseExit(
        [
            Exit("Crossroads", 80, "N", offered: false),
            Exit("Transit to Factory", 20, "E", offered: true, transit: true),
            Exit("ZB-1011", 310, "W", offered: true),
        ]);
        Assert.Equal("ZB-1011", chosen?.Name);

        using var scope = English();
        var unconfirmed = NowPanelState.Project(InRaid(19, 35), Now, [Exit("Crossroads", 80, "N", offered: false)]);
        Assert.StartsWith("Nearest exit: Crossroads", unconfirmed.YouExit, StringComparison.Ordinal);
        Assert.Equal(NowText.ExitUnconfirmed, unconfirmed.YouExitNote);
    }

    [Fact]
    public void An_old_screenshot_dims_you_and_no_screenshot_says_so()
    {
        using var scope = English();
        var old = InRaid(19, 35);
        old = old with { You = old.You! with { TakenUtc = Now.AddMinutes(-3) } };
        Assert.True(NowPanelState.Project(old, Now).YouIsStale);

        var none = NowPanelState.Project(InRaid(19, 35) with { You = null }, Now);
        Assert.False(none.YouIsPlaced);
        Assert.Equal(NowText.NoScreenshot, none.YouWhere);
    }

    [Theory]
    [InlineData(SituationPhase.Menu, false, false)]
    [InlineData(SituationPhase.Screen, false, false)]
    [InlineData(SituationPhase.Matching, false, true)]
    [InlineData(SituationPhase.Loading, false, true)]
    [InlineData(SituationPhase.PostRaid, false, false)]
    [InlineData(SituationPhase.Dead, false, false)]
    [InlineData(SituationPhase.Extracted, false, false)]
    [InlineData(SituationPhase.Unknown, false, false)]
    public void Out_of_a_raid_there_is_no_YOU_and_NEXT_only_before_one(SituationPhase phase, bool you, bool next)
    {
        using var scope = English();
        var state = NowPanelState.Project(Out(phase), Now);

        Assert.Equal(you, state.ShowsYou);
        Assert.Equal(next, state.ShowsNext);
        Assert.Equal(NowTone.Normal, state.Tone);
        Assert.True(state.ShowsScan);
        Assert.False(state.ShowsScanHint);
        Assert.Equal(NowText.ScanNoneOutOfRaid, state.ScanNone);
    }

    [Fact]
    public void Each_ending_says_who_said_so_and_the_log_alone_says_it_cannot_tell()
    {
        using var scope = English();
        Assert.Equal(NowText.Phase(SituationPhase.PostRaid), NowPanelState.Project(Out(SituationPhase.PostRaid), Now).NowHeadline);
        Assert.Equal(NowText.PhaseLine(SituationPhase.PostRaid), NowPanelState.Project(Out(SituationPhase.PostRaid), Now).NowDetail);

        var died = Out(SituationPhase.Dead) with
        {
            Outcome = new(SituationOutcome.Died, Confidence.Certain, SituationSource.Player, Now, "asked"),
        };
        var state = NowPanelState.Project(died, Now);
        Assert.Equal(NowText.Phase(SituationPhase.Dead), state.NowHeadline);
        Assert.Equal(NowText.PhaseLine(SituationPhase.Dead), state.NowDetail);

        var loading = Out(SituationPhase.Loading) with
        {
            Map = new("customs", Confidence.Certain, SituationSource.GameLog, Now, "log"),
        };
        Assert.Equal(NowText.LoadingMap("Customs"), NowPanelState.Project(loading, Now).NowHeadline);
    }

    [Fact]
    public void Squad_rows_say_area_distance_and_age_and_only_a_placed_member_in_this_raid_can_be_pinged()
    {
        using var scope = English();
        var state = NowPanelState.Project(InRaid(19, 35), Now);

        Assert.Collection(
            state.Squad,
            geo =>
            {
                Assert.Equal("Old Gas Station · 140 m NE", geo.Where);
                Assert.Equal("12 s", geo.Age);
                Assert.True(geo.CanPing);
            },
            riley =>
            {
                Assert.Equal("on Woods", riley.Where);
                Assert.False(riley.CanPing);
                Assert.True(riley.IsAway);
            },
            sam =>
            {
                Assert.Equal(NowText.SquadQuiet("Crackhouse"), sam.Where);
                Assert.False(sam.CanPing);
            });
        Assert.Equal("Geo 140 m NE  ·  Riley on Woods  ·  Sam gone quiet · last Crackhouse", state.SquadLine);
    }

    [Fact]
    public void A_fresh_verdict_takes_the_room_an_old_one_is_one_line_and_last_raid_s_is_not_shown()
    {
        using var scope = English();
        var situation = InRaid(19, 35);
        var fresh = NowPanelState.Project(situation, Now, verdict: Verdict(Now.AddSeconds(-2)));

        Assert.True(fresh.IsVerdictFocus);
        Assert.False(fresh.ShowsNext);
        Assert.Equal(3, fresh.VerdictRows.Count);
        Assert.Equal("Take 2", fresh.TakeText);
        Assert.Equal("· Swap 1", fresh.SwapText);
        Assert.Equal("· Leave 1", fresh.LeaveText);
        Assert.Equal("2 s ago", fresh.ScanAge);

        var older = NowPanelState.Project(situation, Now, verdict: Verdict(Now.AddMinutes(-5)));
        Assert.False(older.IsVerdictFocus);
        Assert.True(older.HasVerdict);
        Assert.True(older.ShowsNext);
        Assert.Empty(older.VerdictRows);

        var lastRaid = NowPanelState.Project(situation, Now, verdict: Verdict(Now.AddMinutes(-40)));
        Assert.False(lastRaid.HasVerdict);
        Assert.True(lastRaid.HasNoScan);
    }

    [Fact]
    public void A_screen_read_between_raids_is_named_in_last_scan()
    {
        using var scope = English();
        var screen = Out(SituationPhase.Screen) with
        {
            LastScan = new(ScanContext.SingleItem, "Graphics card", 1, "Sell", Now.AddSeconds(-30), "read"),
        };

        var state = NowPanelState.Project(screen, Now);

        Assert.Equal(NowText.ScanItem("Graphics card", "Sell"), state.ScanLine);
        Assert.Equal(NowText.ScreenLine(NowText.ScreenItem), state.NowDetail);
    }

    [Fact]
    public void Nothing_on_the_panel_is_a_coordinate()
    {
        using var scope = English();
        var state = NowPanelState.Project(InRaid(19, 35), Now, [Exit("ZB-1011", 310, "W", offered: true)]);
        string[] shown = [state.NowHeadline, state.NowDetail, state.NowNote, state.YouWhere, state.YouExit, .. state.Squad.Select(row => row.Where)];

        Assert.All(shown, text => Assert.DoesNotContain("123.4", text, StringComparison.Ordinal));
        Assert.All(shown, text => Assert.DoesNotContain("-56.7", text, StringComparison.Ordinal));
    }

    internal static IDisposable English() => UiText.Scope(UiText.Create("en"));

    internal static Situation InRaid(int minutesIn, int length)
    {
        var started = Now.AddMinutes(-minutesIn);
        return new Situation(7, Now, new(SituationPhase.InRaid, new Confidence(0.95), SituationSource.GameLog, started, "The game started the raid."))
        {
            RaidId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Map = new("customs", Confidence.Certain, SituationSource.GameLog, started, "log"),
            Clock = new(SituationClockBasis.Counted, TimeSpan.FromMinutes(length), started, started, "counted"),
            You = new("Dorms 3-story", "2nd floor", "NE", 45, new WorldPosition(123.4, 2, -56.7), Now.AddSeconds(-12), "shot"),
            Squad =
            [
                new("Geo", SquadMemberState.InRaid, "customs", "Old Gas Station", 140, "NE", Now.AddSeconds(-12), "shared"),
                new("Riley", SquadMemberState.OnAnotherMap, "woods", null, null, null, Now.AddSeconds(-50), "shared"),
                new("Sam", SquadMemberState.Quiet, "customs", "Crackhouse", 60, "N", Now.AddMinutes(-4), "shared"),
            ],
            Next = new("zibbo", "Golden Zibbo lighter", 40, "customs", started, "route"),
            Then = new("watch", "Bronze pocket watch", null, "customs", started, "route"),
        };
    }

    internal static Situation Out(SituationPhase phase) =>
        new(3, Now, new(phase, new Confidence(0.9), SituationSource.GameLog, Now.AddMinutes(-1), "log"));

    internal static NowExit Exit(string name, double metres, string compass, bool offered, bool transit = false) =>
        new(name, metres, compass, offered, transit);

    internal static NowLootVerdict Verdict(DateTimeOffset received) => new(
        2,
        1,
        1,
        0,
        [
            new(LootScanVerdict.Take, "Virtex programmable processor", "Gunsmith later", "₽86k / sq"),
            new(LootScanVerdict.Take, "Fuel conditioner", "Hideout", "₽61k / sq"),
            new(LootScanVerdict.Swap, "Electric drill", "Swap the wires", "₽32k / sq"),
        ],
        received);
}
