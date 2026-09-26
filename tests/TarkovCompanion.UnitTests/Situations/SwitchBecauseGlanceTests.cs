using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.UnitTests.V2Now;
using static TarkovCompanion.UnitTests.Situations.SituationTimeline;

namespace TarkovCompanion.UnitTests.Situations;

/// <summary>
/// [#712 0-7] The glance ratchet's third rule, from V3.0's exit gate: every automatic switch has a
/// "because" line. Whole evenings through the situation engine, and after every step each phase
/// change so far is in the transition log with its reason, and the Now panel's foot shows the phase's own.
/// </summary>
public sealed class SwitchBecauseGlanceTests
{
    public static TheoryData<string> Evenings => [.. Timelines.Keys];

    private static readonly Dictionary<string, Action<SituationTimeline>[]> Timelines = new()
    {
        ["a PMC raid on Customs, died, back to the menu"] =
        [
            t => t.Log(ProfileReload("2026-09-23 20:00:00.000")),
            t => t.Log(AppLine("2026-09-23 20:01:00.000", "Matching with group id: 1")),
            t => t.Log(AppLine("2026-09-23 20:01:02.000", "scene preset path:maps/customs_preset.bundle rcid:customs.scenespreset.asset")),
            t => t.Log(AppLine("2026-09-23 20:01:20.000", "MatchingCompleted:11.26 real:15.03 diff:3.76")),
            t => t.Log(AppLine("2026-09-23 20:01:40.000", "LocationLoaded:10.97 real:18.09 diff:7.12")),
            t => t.Log(Notification("2026-09-23 20:01:41.000", "userConfirmed", "Busy", "bigmap", "CUST01", PmcProfile)),
            t => t.Log(AppLine("2026-09-23 20:02:40.000", "GameStarted:31.37(0) real:53.4(0) diff:22.03")),
            t => t.Screenshot("2026-09-23 20:10", -12, 4, 30, 45),
            t => t.Log(Notification("2026-09-23 20:31:00.000", "userMatchOver", "Free", "bigmap", "CUST01", PmcProfile)),
            t => t.Outcome(SituationOutcome.Died),
            t => t.Log(ProfileReload("2026-09-23 20:31:20.000")),
            t => t.At("2026-09-23 20:42:00"),
        ],
        ["a scav raid read off the extract screen, got out"] =
        [
            t => t.Log(ProfileReload("2026-09-23 21:00:00.000")),
            t => t.Log(AppLine("2026-09-23 21:01:00.000", "Matching with group id: 1")),
            t => t.Log(AppLine("2026-09-23 21:01:10.000", "MatchingCompleted:4.1 real:5.2 diff:1.1")),
            t => t.Log(Notification("2026-09-23 21:01:30.000", "userConfirmed", "Busy", "bigmap", "SCAV01", ScavProfile)),
            t => t.Log(AppLine("2026-09-23 21:02:00.000", "GameStarted:20.0(0) real:30.0(0) diff:10.0")),
            t => t.At("2026-09-23 21:05:00").ExtractScreen(TimeSpan.FromMinutes(18)),
            t => t.Log(Notification("2026-09-23 21:20:00.000", "userMatchOver", "Free", "bigmap", "SCAV01", ScavProfile)),
            t => t.Outcome(SituationOutcome.Survived),
        ],
        ["a transit from Lighthouse into The Lab"] =
        [
            t => t.Log(ProfileReload("2026-09-23 01:10:00.000")),
            t => t.Log(Notification("2026-09-23 01:21:01.824", "userConfirmed", "Busy", "Lighthouse", "LIGHT1", ScavProfile)),
            t => t.Log(AppLine("2026-09-23 01:22:37.981", "GameStarted:99.52(99.52) real:115.89(115.89) diff:16.37")),
            t => t.Log(Notification("2026-09-23 01:40:19.000", "userMatchOver", "Transfer", "Lighthouse", "LIGHT1", ScavProfile)),
            t => t.Log(AppLine("2026-09-23 01:46:57.767", "MatchingCompleted:0 real:0 diff:0")),
            t => t.Log(AppLine("2026-09-23 01:47:15.860", "LocationLoaded:10.97 real:18.09 diff:7.12")),
            t => t.Log(AppLine("2026-09-23 01:47:51.168", "GameStarted:31.37(0) real:53.4(0) diff:22.03")),
        ],
        ["a flea screenshot between raids, then the menu again"] =
        [
            t => t.Log(ProfileReload("2026-09-23 19:00:00.000")),
            t => t.At("2026-09-23 19:05:00").Scan(ScanContext.FleaListings),
            t => t.At("2026-09-23 19:09:00"),
        ],
        ["the PC clock set back four hours mid-raid"] =
        [
            t => t.Log(ProfileReload("2026-09-23 20:00:00.000")),
            t => t.Log(Notification("2026-09-23 20:01:41.000", "userConfirmed", "Busy", "bigmap", "CUST01", PmcProfile)),
            t => t.Log(AppLine("2026-09-23 20:02:40.000", "GameStarted:31.37(0) real:53.4(0) diff:22.03")),
            t => t.StepClock(TimeSpan.FromHours(-4)),
            t => t.Screenshot("2026-09-23 16:14", 25, 1, 10, 180),
            t => t.Log(Notification("2026-09-23 16:30:00.000", "userMatchOver", "Free", "bigmap", "CUST01", PmcProfile)),
        ],
    };

    [Theory]
    [MemberData(nameof(Evenings))]
    public void Every_automatic_switch_says_why_and_the_Now_panel_shows_the_newest(string evening)
    {
        using var english = NowPanelStateTests.English();
        using var timeline = new SituationTimeline();
        var switches = new List<SituationTransition>();
        timeline.Service.Changed += (_, change) =>
        {
            if (change.Previous.Phase.Value != change.Current.Phase.Value)
            {
                switches.Add(new(change.Current.Version, change.Current.ComputedUtc, change.Previous.Phase.Value, change.Current.Phase.Value, change.Current.Phase.Because));
            }
        };
        using var panel = new NowPanelViewModel(timeline.Service, timeline.Clock, post: action => action(), tick: false);

        var step = 0;
        foreach (var act in Timelines[evening])
        {
            step++;
            act(timeline);
            var log = timeline.Service.Transitions;

            // The log holds every switch seen, in order; before them at most the one the service
            // made while it was built (nothing from the game yet, to the menu).
            Assert.True(log.Count >= switches.Count, $"step {step}: {switches.Count} switches, {log.Count} logged");
            Assert.InRange(log.Count - switches.Count, 0, 1);
            Assert.Equal(switches, log.Skip(log.Count - switches.Count));
            foreach (var transition in log)
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(transition.Because),
                    $"step {step}: {transition.From} -> {transition.To} (version {transition.Version}) has no because line");
            }

            // The foot reads the phase fact itself (#712 follow-up to #952): at a switch that is the
            // switch's own reason, and within a phase it moves on with the evidence ("started the
            // raid" after "confirmed the raid"), where the transition log would hold the older line.
            if (log.Count > 0)
            {
                Assert.Equal(timeline.Now.Phase.Because, panel.State.Because);
                Assert.True(panel.State.HasBecause);
            }
        }

        Assert.NotEmpty(switches);
    }
}
