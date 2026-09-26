using System.Text;
using System.Text.Json.Nodes;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.Services.V2;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.GroupServer;
using TarkovCompanion.UnitTests.Runtime;
using static TarkovCompanion.UnitTests.V2Now.NowPanelStateTests;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>[#712 0-11] The Now panel as the tablet receives it: the desk's words, anchors instead of ticks, bounded.</summary>
public sealed class TabletNowPanelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Mid_raid_carries_the_desks_words_with_the_clock_as_the_anchor_it_is_counted_from()
    {
        using var scope = English();
        var situation = InRaid(minutesIn: 19, length: 35);
        var state = NowPanelState.Project(situation, Now, [Exit("ZB-1011", 310, "W", offered: true)]);

        var panel = TabletNowPanelBuilder.Build(state, situation);

        Assert.Equal(TabletNowPanel.CurrentVersion, panel.Version);
        Assert.Equal("InRaid", panel.Phase);
        Assert.Equal("Customs", panel.MapName);
        Assert.Equal("{clock} left", panel.Now.Headline);
        var clock = Assert.IsType<TabletNowClock>(panel.Now.Clock);
        Assert.Equal(("left", Now.AddMinutes(16), "Counted", true), (clock.Kind, clock.AtUtc, clock.Basis, clock.IsCounted));
        // The count keeps its label: never the game's clock.
        Assert.Equal(NowText.ClockCounted, panel.Now.Detail);
        Assert.Null(panel.Now.DetailSinceUtc);
        Assert.Equal(NowText.RunThroughEnded("7:00"), panel.Now.Note);
        Assert.True(panel.Now.NoteIsDone);

        var you = Assert.IsType<TabletNowYou>(panel.You);
        Assert.Equal("Dorms 3-story · 2nd floor · facing NE", you.Where);
        Assert.Equal(Now.AddSeconds(-12), you.SinceUtc);
        Assert.Equal("Nearest offered exit: ZB-1011 · 310 m W · ~4 min", you.Exit);

        var squad = Assert.IsType<TabletNowSquad>(panel.Squad);
        Assert.Equal(["Geo", "Riley", "Sam"], squad.Rows.Select(row => row.Name));
        Assert.Equal([true, false, false], squad.Rows.Select(row => row.CanPing));
        Assert.Equal(Now.AddSeconds(-12), squad.Rows[0].SeenUtc);
        // Another map has no age on the desk either.
        Assert.Null(squad.Rows[1].SeenUtc);

        var next = Assert.IsType<TabletNowNext>(panel.Next);
        Assert.Equal(("Golden Zibbo lighter", "40 m leg", "then: Bronze pocket watch"), (next.Label, next.Detail, next.Then));
        Assert.Equal(NowText.ScanNone, panel.Scan.Line);
        Assert.Equal(NowText.ScanNoneHint, panel.Scan.Hint);
        Assert.Equal("{0} ago", panel.Words.Ago);
    }

    [Fact]
    public void A_clock_read_off_the_extract_screen_is_not_counted_and_its_age_is_a_stamp()
    {
        using var scope = English();
        var situation = InRaid(minutesIn: 19, length: 35) with
        {
            Clock = new(SituationClockBasis.Observed, TimeSpan.FromMinutes(20), Now.AddMinutes(-3), Now.AddMinutes(-19), "read"),
        };

        var panel = TabletNowPanelBuilder.Build(NowPanelState.Project(situation, Now), situation);

        Assert.False(panel.Now.Clock!.IsCounted);
        Assert.Equal(Now.AddMinutes(17), panel.Now.Clock.AtUtc);
        Assert.Equal(NowText.ClockObserved("{ago}"), panel.Now.Detail);
        Assert.Equal(Now.AddMinutes(-3), panel.Now.DetailSinceUtc);
    }

    [Fact]
    public void A_second_later_with_nothing_new_the_payload_is_the_same_and_a_late_raid_is_a_change()
    {
        using var scope = English();
        using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
        var situation = InRaid(minutesIn: 19, length: 35);
        var gate = new TabletNowChangeGate();
        IReadOnlyList<NowExit> exits = [Exit("ZB-1011", 310, "W", offered: true)];

        var sent = Enumerable.Range(0, 60)
            .Select(second => gate.Offer(TabletNowPanelBuilder.Build(NowPanelState.Project(situation, Now.AddSeconds(second), exits), situation)))
            .Count(changed => changed);

        Assert.Equal(1, sent);
        // 16 minutes left now; at 10 left the desk turns amber and names the time to leave.
        var late = TabletNowPanelBuilder.Build(NowPanelState.Project(situation, Now.AddMinutes(6).AddSeconds(1), exits), situation);
        Assert.True(gate.Offer(late));
        Assert.Equal("Late", late.Tone);
        Assert.Equal(NowText.HeadingLateRaid, late.Now.Heading);
        Assert.False(gate.Offer(TabletNowPanelBuilder.Build(NowPanelState.Project(situation, Now.AddMinutes(6).AddSeconds(2), exits), situation)));
    }

    [Fact]
    public void Out_of_raid_says_the_phase_and_has_no_clock()
    {
        using var scope = English();
        var situation = Out(SituationPhase.Menu);

        var panel = TabletNowPanelBuilder.Build(NowPanelState.Project(situation, Now), situation);

        Assert.Equal("Not in raid", panel.Now.Headline);
        Assert.Null(panel.Now.Clock);
        Assert.Null(panel.You);
        Assert.Null(panel.Squad);
        Assert.Null(panel.Next);
        Assert.Equal(NowText.ScanNoneOutOfRaid, panel.Scan.Line);
    }

    [Fact]
    public void A_fresh_verdict_carries_its_counts_and_top_rows_and_the_squad_goes_to_its_short_form()
    {
        using var scope = English();
        var situation = InRaid(minutesIn: 19, length: 35);
        var state = NowPanelState.Project(situation, Now, verdict: Verdict(Now.AddSeconds(-20)));

        var panel = TabletNowPanelBuilder.Build(state, situation);

        Assert.Equal("Take 2 · Swap 1 · Leave 1", panel.Scan.Line);
        Assert.Equal(Now.AddSeconds(-20), panel.Scan.SinceUtc);
        Assert.Equal(["Take", "Take", "Swap"], panel.Scan.Rows.Select(row => row.Verdict));
        Assert.Equal("TAKE", panel.Scan.Rows[0].Word);
        Assert.Null(panel.Next);
        Assert.Equal("140 m NE", panel.Squad!.Rows[0].Where);
    }

    [Fact]
    public void Every_string_is_clipped_every_list_capped_and_the_worst_case_fits_the_relays_bound()
    {
        var huge = new string('é', 500);
        var raw = Panel(huge, squad: 40, verdictRows: 40);

        var bounded = raw.Bounded();

        Assert.Equal(TabletNowPanel.MaximumSquadRows, bounded.Squad!.Rows.Count);
        Assert.Equal(TabletNowPanel.MaximumVerdictRows, bounded.Scan.Rows.Count);
        Assert.Equal(TabletNowPanel.MaximumText, bounded.Now.Headline.Length);
        Assert.Equal(TabletNowPanel.MaximumText, bounded.Squad.Rows[0].Where.Length);
        var size = TabletNowPanelJson.Serialize(bounded).Length;
        Assert.True(size < RelayNowPanelBound.MaximumNowBytes, $"{size} bytes");
        Assert.True(RelayNowPanelBound.Fits(Surface(bounded)));
        // What the bound is for: a desktop that did not clip.
        Assert.False(RelayNowPanelBound.Fits(Surface(raw)));
    }

    [Fact]
    public void The_relay_does_not_measure_a_surface_without_the_Now_block()
    {
        Assert.True(RelayNowPanelBound.Fits(Surface(null)));
        Assert.True(RelayNowPanelBound.Fits("{\"mapId\":\"customs\",\"objects\":[{\"now\":\"not the top level\"}]}"u8));
        Assert.True(RelayNowPanelBound.Fits("not json"u8));
    }

    [Fact]
    public void Shifting_to_the_relays_clock_moves_every_stamp_and_nothing_else()
    {
        using var scope = English();
        var situation = InRaid(minutesIn: 19, length: 35);
        var panel = TabletNowPanelBuilder.Build(NowPanelState.Project(situation, Now, verdict: Verdict(Now.AddSeconds(-20))), situation);
        var fast = TimeSpan.FromHours(-4);

        var shifted = panel.Shifted(fast);

        Assert.Equal(panel.Now.Clock!.AtUtc + fast, shifted.Now.Clock!.AtUtc);
        Assert.Equal(panel.You!.SinceUtc + fast, shifted.You!.SinceUtc);
        Assert.Equal(panel.Squad!.Rows[0].SeenUtc + fast, shifted.Squad!.Rows[0].SeenUtc);
        Assert.Null(shifted.Squad.Rows[1].SeenUtc);
        Assert.Equal(panel.Scan.SinceUtc + fast, shifted.Scan.SinceUtc);
        Assert.Equal(panel.Now.Headline, shifted.Now.Headline);
    }

    [Fact]
    public void The_wiring_sends_the_panel_once_per_change_and_null_when_the_flag_goes_off()
    {
        using var scope = English();
        var clock = new ManualTimeProvider(Now);
        var flag = true;
        using var viewModel = new NowPanelViewModel(null, clock, tick: false);
        var host = new NowPanelHost(() => flag);
        var gate = new TabletNowChangeGate();
        var sent = new List<TabletNowPanel?>();
        TabletNowPanelWiring.Attach(host, panel =>
        {
            if (!gate.Offer(panel))
            {
                return false;
            }

            sent.Add(panel);
            return true;
        });

        host.Panel = viewModel;
        viewModel.Show(InRaid(minutesIn: 19, length: 35));
        for (var second = 0; second < 30; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            viewModel.Refresh();
        }

        // The Menu-phase panel the view model starts with, then the raid: never a tick.
        Assert.Equal(["Unknown", "InRaid"], sent.Select(panel => panel!.Phase));

        flag = false;
        host.Refresh();
        Assert.Null(sent[^1]);
    }

    private static byte[] Surface(TabletNowPanel? now)
    {
        var surface = new TabletMapSurface(
            1, "customs", "Customs", "default", "v1", new TabletMapPlan(0, 0, 10, 10), null, [], ["1F"], [], [],
            new TabletMapView("1F", 5, 5, 1, null, null), null, null, Now, Now: now);
        return TabletMapSurfaceJson.Serialize(surface);
    }

    private static TabletNowPanel Panel(string text, int squad, int verdictRows) => new(
        TabletNowPanel.CurrentVersion,
        text,
        text,
        text,
        new TabletNowBlock(text, text, new TabletNowClock(text, Now, text, true), text, Now, text, false),
        new TabletNowYou(text, text, text, Now, false, text, text),
        new TabletNowSquad(text, [.. Enumerable.Range(0, squad).Select(_ => new TabletNowSquadRow(text, text, Now, true, false, text))], text, text),
        new TabletNowNext(text, text, text, text, text),
        new TabletNowScan(text, text, Now, text, [.. Enumerable.Range(0, verdictRows).Select(_ => new TabletNowVerdictRow(text, text, text, text, text))]),
        text,
        new TabletNowWords(text, text, text, text, text));
}
