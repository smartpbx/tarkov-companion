using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TarkovCompanion.App.Localization;
using TarkovCompanion.App.ViewModels.V2.Now;
using TarkovCompanion.App.Views.V2.Now;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Situations;
using TarkovCompanion.UnitTests.Situations;
using TarkovCompanion.UnitTests.V2MapRenderer;
using static TarkovCompanion.UnitTests.Situations.SituationTimeline;

namespace TarkovCompanion.UnitTests.V2Now;

/// <summary>
/// [#712, #403] NOW's matching and loading line and SQUAD's rows come from the situation's own
/// <see cref="Situation.Stages"/> and <see cref="Situation.Party"/>, so the Now panel says what the
/// pre-raid brief says. Before this, NOW read "The game is looking for a raid" whatever the log had
/// written, the foot line was the transition log's last entry rather than the phase's own reason,
/// and a party with nobody sharing showed "No squad sharing" while the brief listed who was ready.
/// </summary>
[Collection(AvaloniaHeadlessCollection.Name)]
public sealed class NowPanelStagesAndPartyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 14, 0, 0, TimeSpan.Zero);

    private static string Backend(string stamp, string type, string payload) =>
        $"{stamp}|1.1.5.1.47510|Info|backend|WebSocketSharp - message received: NOTIFICATION e0000000000000000000000a {type} {payload}";

    private static string Ready(string stamp, long aid, string name) => Backend(stamp, "groupMatchRaidReady",
        "[{\"type\":\"groupMatchRaidReady\",\"eventId\":\"e1\",\"extendedProfile\":{\"_id\":\"p" + aid + "\",\"aid\":" + aid +
        ",\"Info\":{\"Nickname\":\"" + name + "\",\"Side\":\"Bear\",\"Level\":20},\"isLeader\":false,\"isReady\":true}}]");

    private static string NotReady(string stamp, long aid) => Backend(stamp, "groupMatchRaidNotReady",
        "[{\"type\":\"groupMatchRaidNotReady\",\"eventId\":\"e2\",\"aid\":" + aid + "}]");

    /// <summary>The real path: log lines through the app's parsers and SituationService into the panel.</summary>
    [Fact]
    public void Matching_then_spawning_reads_the_stages_and_the_party_from_the_situation()
    {
        using var english = NowPanelStateTests.English();
        using var timeline = new SituationTimeline();
        using var panel = new NowPanelViewModel(timeline.Service, timeline.Clock, post: action => action(), tick: false);
        var pulses = new List<string>();
        panel.SquadPulsed += (_, row) => pulses.Add(row.Name);

        timeline.Logs(
            ProfileReload("2026-09-23 21:40:00.000"),
            Ready("2026-09-23 21:48:20.000", 1000002, "PLAYER_B"),
            Ready("2026-09-23 21:48:21.000", 1000003, "PLAYER_C"),
            AppLine("2026-09-23 21:48:26.506", "Matching with group id: 1"),
            AppLine("2026-09-23 21:48:27.000", "scene preset path:maps/customs_preset.bundle rcid:customs.scenespreset.asset"),
            AppLine("2026-09-23 21:48:28.414", "TRACE-NetworkGameMatching G"));

        var matching = panel.State;
        Assert.Equal(SituationPhase.Matching, matching.Phase);
        Assert.Equal(NowText.Phase(SituationPhase.Matching), matching.NowHeadline);
        Assert.Equal(Stage(RaidPhaseMarkerKind.MatchingStarted, timeline.Now), matching.NowDetail);
        Assert.Equal(timeline.Now.Phase.Because, matching.Because);
        Assert.Equal(NowText.SquadFromGame, matching.SquadSource);
        Assert.Equal(timeline.Now.Party!.Because, matching.SquadBecause);
        Assert.Equal(["PLAYER_B", "PLAYER_C"], panel.SquadRows.Select(row => row.Name));
        Assert.All(panel.SquadRows, row => Assert.Equal(NowText.PartyReady, row.Where));
        Assert.All(panel.SquadRows, row => Assert.False(row.CanPing));

        // Taking Ready back pulses that row, like a squadmate's area change does.
        timeline.Log(NotReady("2026-09-23 21:48:30.000", 1000003));
        Assert.Equal(NowText.PartyNotReady, panel.SquadRows.Single(row => row.Name == "PLAYER_C").Where);
        Assert.Equal(["PLAYER_C"], pulses);

        timeline.Logs(
            AppLine("2026-09-23 21:48:41.537", "MatchingCompleted:11.26 real:15.03 diff:3.76"),
            AppLine("2026-09-23 21:48:52.056", "LocationLoaded:20.14 real:25.55 diff:5.41"),
            Notification("2026-09-23 21:48:52.400", "userConfirmed", "Busy", "bigmap", "CUST02", PmcProfile),
            ProfileStatus("2026-09-23 21:48:52.470", "bigmap", "CUST02", PmcProfile),
            AppLine("2026-09-23 21:49:35.393", "GameSpawn:58.67(0.09) real:68.88(0.08) diff:10.2"));

        var spawning = panel.State;
        Assert.Equal(SituationPhase.Loading, spawning.Phase);
        Assert.Equal(NowText.SpawningMap("Customs"), spawning.NowHeadline);
        Assert.Equal(
            string.Join(" · ", new[]
            {
                RaidPhaseMarkerKind.MatchingStarted, RaidPhaseMarkerKind.MatchingCompleted,
                RaidPhaseMarkerKind.LocationLoaded, RaidPhaseMarkerKind.Spawning,
            }.Select(kind => Stage(kind, timeline.Now))),
            spawning.NowDetail);
        Assert.Equal(timeline.Now.Phase.Because, spawning.Because);

        // Once everybody is loading, Ready no longer means anything: the party is named, nothing more.
        Assert.All(panel.SquadRows, row => Assert.Equal(NowText.PartyMember, row.Where));
    }

    [Fact]
    public void Squadmates_companions_win_over_the_game_party_list()
    {
        using var english = NowPanelStateTests.English();
        var situation = NowPanelStateTests.InRaid(minutesIn: 10, length: 40) with { Party = Party(("PLAYER_B", true)) };

        var state = NowPanelState.Project(situation, Now);

        Assert.Equal(["Geo", "Riley", "Sam"], state.Squad.Select(row => row.Name));
        Assert.Equal(NowText.SquadSource, state.SquadSource);
        Assert.False(state.HasSquadBecause);
    }

    [Fact]
    public void A_party_shows_in_the_menu_and_a_matching_line_without_a_Ready_line_is_empty_not_invented()
    {
        using var english = NowPanelStateTests.English();
        var menu = NowPanelStateTests.Out(SituationPhase.Menu) with { Party = Party(("PLAYER_B", true), ("PLAYER_C", null)) };

        var state = NowPanelState.Project(menu, Now);

        Assert.True(state.ShowsSquad);
        Assert.Equal([NowText.PartyReady, NowText.PartyMember], state.Squad.Select(row => row.Where));
        Assert.All(state.Squad, row => Assert.Equal(string.Empty, row.Age));

        // Only queue steps G/H/I: the headline says matching, and no time is made up for Ready.
        var steps = NowPanelStateTests.Out(SituationPhase.Matching) with
        {
            Stages = [new(RaidPhaseMarkerKind.MatchingStep, Now.AddSeconds(-3))],
        };
        Assert.Equal(string.Empty, NowPanelState.Project(steps, Now).NowDetail);
    }

    /// <summary>The real view: the stage line, each party row, and where they came from are on screen.</summary>
    [Fact]
    public async Task The_view_shows_the_stage_line_the_party_rows_and_their_source()
    {
        using var session = HeadlessSessions.StartNew(typeof(NowPanelFitTests.NowPanelApp));
        var (seen, stageLine) = await session.Dispatch(
            () =>
            {
                using var english = NowPanelStateTests.English();
                using var zone = LocalTime.UseZone(TimeZoneInfo.Utc);
                using var panel = new NowPanelViewModel(null, post: action => action(), tick: false);
                panel.Show(new Situation(4, Now, new(SituationPhase.Matching, new(0.9), SituationSource.GameLog, Now.AddSeconds(-40), "You pressed Ready."))
                {
                    Stages = [new(RaidPhaseMarkerKind.MatchingStarted, Now.AddSeconds(-40)), new(RaidPhaseMarkerKind.MatchingStep, Now.AddSeconds(-38))],
                    Party = Party(("PLAYER_B", true), ("PLAYER_C", false)),
                });
                var view = new NowPanelView { DataContext = panel };
                var window = new Window
                {
                    Width = 400,
                    Height = 950,
                    Content = view,
                    FontFamily = new Avalonia.Media.FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter"),
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var texts = view.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => text.IsEffectivelyVisible && text.Bounds.Height > 0)
                    .Select(text => text.Text is { Length: > 0 } plain
                        ? plain
                        : string.Concat(text.Inlines?.OfType<Run>().Select(run => run.Text) ?? []))
                    .ToArray();
                window.Close();
                return (texts, NowText.Stage(RaidPhaseMarkerKind.MatchingStarted, LocalTime.ShortTime(Now.AddSeconds(-40))));
            },
            CancellationToken.None);

        using var scope = NowPanelStateTests.English();
        Assert.Contains(stageLine, seen);
        Assert.Contains(NowText.SquadFromGame, seen);
        Assert.Contains(seen, text => text.Contains("PLAYER_C", StringComparison.Ordinal) && text.Contains(NowText.PartyNotReady, StringComparison.Ordinal));
        Assert.Contains(seen, text => text.Contains("PLAYER_B", StringComparison.Ordinal) && text.EndsWith(NowText.PartyReady, StringComparison.Ordinal));
        Assert.Contains("You pressed Ready.", seen);
        Assert.DoesNotContain(NowText.SquadEmpty, seen);
    }

    private static string Stage(RaidPhaseMarkerKind kind, Situation situation) =>
        NowText.Stage(kind, LocalTime.ShortTime(situation.Stages.First(stage => stage.Kind == kind).ObservedUtc));

    private static SituationParty Party(params (string Name, bool? Ready)[] members) => new(
        [.. members.Select(member => new SituationPartyMember(member.Name, member.Ready, IsLeader: false))],
        members.Count(member => member.Ready == true),
        Now.AddSeconds(-30),
        "The game's group notifications.");
}
