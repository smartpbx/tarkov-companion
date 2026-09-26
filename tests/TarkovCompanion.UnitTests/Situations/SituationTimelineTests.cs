using TarkovCompanion.Application.Services.Situations;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Situations;
using static TarkovCompanion.UnitTests.Situations.SituationTimeline;

namespace TarkovCompanion.UnitTests.Situations;

/// <summary>Whole evenings through the situation engine (ADR 0022), one per row of #712's situation table.</summary>
public sealed class SituationTimelineTests
{
    /// <summary>A PMC raid on Customs as the 2026-09-23 logs wrote one, from Ready to the menu.</summary>
    private static void PmcCustomsRaid(SituationTimeline timeline) => timeline.Logs(
        ProfileReload("2026-09-23 20:00:00.000"),
        AppLine("2026-09-23 20:01:00.000", "Matching with group id: 1"),
        AppLine("2026-09-23 20:01:02.000", "scene preset path:maps/customs_preset.bundle rcid:customs.scenespreset.asset"),
        AppLine("2026-09-23 20:01:20.000", "MatchingCompleted:11.26 real:15.03 diff:3.76"),
        AppLine("2026-09-23 20:01:40.000", "LocationLoaded:10.97 real:18.09 diff:7.12"),
        Notification("2026-09-23 20:01:41.000", "userConfirmed", "Busy", "bigmap", "CUST01", PmcProfile),
        ProfileStatus("2026-09-23 20:01:42.000", "bigmap", "CUST01", PmcProfile),
        AppLine("2026-09-23 20:02:40.000", "GameStarted:31.37(0) real:53.4(0) diff:22.03"));

    [Fact]
    public void APmcRaidGoesFromTheQueueThroughLoadingIntoTheRaidAndOutAgain()
    {
        using var timeline = new SituationTimeline();
        Assert.Equal(SituationPhase.Menu, timeline.Now.Phase.Value);

        timeline.Logs(ProfileReload("2026-09-23 20:00:00.000"), AppLine("2026-09-23 20:01:00.000", "Matching with group id: 1"));
        Assert.Equal(SituationPhase.Matching, timeline.Now.Phase.Value);

        // The scene preset names the map while the queue is still running.
        timeline.Log(AppLine("2026-09-23 20:01:02.000", "scene preset path:maps/customs_preset.bundle rcid:customs.scenespreset.asset"));
        Assert.Equal(SituationPhase.Matching, timeline.Now.Phase.Value);
        Assert.Equal("customs", timeline.Now.Map?.Value);

        timeline.Log(AppLine("2026-09-23 20:01:20.000", "MatchingCompleted:11.26 real:15.03 diff:3.76"));
        Assert.Equal(SituationPhase.Loading, timeline.Now.Phase.Value);

        // The confirmation puts the raid state in raid a minute before the player can move.
        timeline.Logs(
            AppLine("2026-09-23 20:01:40.000", "LocationLoaded:10.97 real:18.09 diff:7.12"),
            Notification("2026-09-23 20:01:41.000", "userConfirmed", "Busy", "bigmap", "CUST01", PmcProfile),
            ProfileStatus("2026-09-23 20:01:42.000", "bigmap", "CUST01", PmcProfile));
        Assert.Equal(SituationPhase.Loading, timeline.Now.Phase.Value);
        Assert.Equal(SituationSide.Pmc, timeline.Now.Side?.Value);
        Assert.True(timeline.Now.Side?.IsInferred);

        timeline.Log(AppLine("2026-09-23 20:02:40.000", "GameStarted:31.37(0) real:53.4(0) diff:22.03"));
        Assert.Equal(SituationPhase.InRaid, timeline.Now.Phase.Value);
        Assert.Equal(SituationSource.GameLog, timeline.Now.Phase.Source);
        Assert.Contains("started the raid", timeline.Now.Phase.Because, StringComparison.Ordinal);

        // The clock counts from the confirmation against the map's 40 minutes, and says it is a count.
        timeline.Screenshot("2026-09-23 20:10", -12, 4, 30, 45);
        var clock = timeline.Now.Clock!;
        Assert.Equal(SituationClockBasis.Counted, clock.Basis);
        Assert.True(clock.IsInferred);
        Assert.Equal(TimeSpan.FromMinutes(40) - TimeSpan.FromSeconds((8 * 60) + 19), clock.RemainingAt(timeline.Clock.Now));

        var you = timeline.Now.You!;
        Assert.Equal("Dorms", you.AreaName);
        Assert.Equal("2nd floor", you.FloorName);
        Assert.Equal("NE", you.Facing);
        Assert.Equal(TimeSpan.FromSeconds(12), you.AgeAt(timeline.Clock.Now + TimeSpan.FromSeconds(12)));

        timeline.Log(Notification("2026-09-23 20:31:00.000", "userMatchOver", "Free", "bigmap", "CUST01", PmcProfile));
        Assert.Equal(SituationPhase.PostRaid, timeline.Now.Phase.Value);
        Assert.Equal(SituationOutcome.Unknown, timeline.Now.Outcome?.Value);
        Assert.Null(timeline.Now.You);
        Assert.Null(timeline.Now.Clock);

        timeline.Outcome(SituationOutcome.Died);
        Assert.Equal(SituationPhase.Dead, timeline.Now.Phase.Value);
        Assert.Equal(SituationSource.Player, timeline.Now.Phase.Source);

        timeline.Log(ProfileReload("2026-09-23 20:31:20.000"));
        timeline.At("2026-09-23 20:42:00");
        Assert.Equal(SituationPhase.Menu, timeline.Now.Phase.Value);

        Assert.Equal(
            [SituationPhase.Matching, SituationPhase.Loading, SituationPhase.InRaid, SituationPhase.PostRaid, SituationPhase.Dead, SituationPhase.Menu],
            timeline.Phases);
        AssertVersionsRiseByOne(timeline);
    }

    [Fact]
    public void AScavRaidTakesItsClockFromTheExtractScreenAndEndsExtracted()
    {
        using var timeline = new SituationTimeline();
        timeline.Logs(
            ProfileReload("2026-09-23 21:00:00.000"),
            AppLine("2026-09-23 21:01:00.000", "Matching with group id: 1"),
            AppLine("2026-09-23 21:01:02.000", "scene preset path:maps/customs_preset.bundle rcid:customs.scenespreset.asset"),
            AppLine("2026-09-23 21:01:10.000", "MatchingCompleted:4.1 real:5.2 diff:1.1"),
            Notification("2026-09-23 21:01:30.000", "userConfirmed", "Busy", "bigmap", "SCAV01", ScavProfile),
            AppLine("2026-09-23 21:02:00.000", "GameStarted:20.0(0) real:30.0(0) diff:10.0"));
        Assert.Equal(SituationPhase.InRaid, timeline.Now.Phase.Value);
        Assert.Equal(SituationSide.Scav, timeline.Now.Side?.Value);

        // A scav joins a raid already running, so its start says nothing about time left.
        Assert.Equal(SituationClockBasis.Elapsed, timeline.Now.Clock?.Basis);
        Assert.Null(timeline.Now.Clock?.RemainingAt(timeline.Clock.Now));

        timeline.At("2026-09-23 21:05:00").ExtractScreen(TimeSpan.FromMinutes(18));
        var clock = timeline.Now.Clock!;
        Assert.Equal(SituationClockBasis.Observed, clock.Basis);
        Assert.False(clock.IsInferred);
        Assert.Equal(TimeSpan.FromMinutes(17), clock.RemainingAt(timeline.Clock.Now + TimeSpan.FromMinutes(1)));
        Assert.Equal(ScanContext.ExtractList, timeline.Now.LastScan?.Kind);

        timeline.Log(Notification("2026-09-23 21:20:00.000", "userMatchOver", "Free", "bigmap", "SCAV01", ScavProfile));
        timeline.Outcome(SituationOutcome.Survived);
        Assert.Equal(SituationPhase.Extracted, timeline.Now.Phase.Value);
        Assert.Equal([SituationPhase.Matching, SituationPhase.Loading, SituationPhase.InRaid, SituationPhase.PostRaid, SituationPhase.Extracted], timeline.Phases);
    }

    /// <summary>
    /// #892: a scav Lighthouse raid ends Transfer and six minutes later the player is in The Lab, a
    /// raid that writes no confirmation, no short id and no end.
    /// </summary>
    [Fact]
    public void ATransitIntoTheLabIsANewRaidOnTheLabWithItsSideUnknown()
    {
        using var timeline = new SituationTimeline();
        timeline.Logs(
            ProfileReload("2026-09-23 01:10:00.000"),
            Notification("2026-09-23 01:21:01.824", "userConfirmed", "Busy", "Lighthouse", "LIGHT1", ScavProfile),
            AppLine("2026-09-23 01:22:37.981", "GameStarted:99.52(99.52) real:115.89(115.89) diff:16.37"));
        Assert.Equal(SituationPhase.InRaid, timeline.Now.Phase.Value);
        Assert.Equal("lighthouse", timeline.Now.Map?.Value);
        var lighthouse = timeline.Now.RaidId;

        timeline.Log(Notification("2026-09-23 01:40:19.000", "userMatchOver", "Transfer", "Lighthouse", "LIGHT1", ScavProfile));
        Assert.Equal(SituationPhase.PostRaid, timeline.Now.Phase.Value);
        Assert.Equal(SituationSide.Scav, timeline.Now.Side?.Value);
        Assert.False(timeline.Now.Side?.IsInferred);

        timeline.Logs(
            AppLine("2026-09-23 01:46:57.767", "MatchingCompleted:0 real:0 diff:0"),
            AppLine("2026-09-23 01:46:59.112", "scene preset path:maps/laboratory_preset.bundle rcid:laboratory.ScenesPreset.asset"),
            AppLine("2026-09-23 01:47:15.860", "LocationLoaded:10.97 real:18.09 diff:7.12"));
        Assert.Equal(SituationPhase.Loading, timeline.Now.Phase.Value);
        Assert.Equal("the-lab", timeline.Now.Map?.Value);

        timeline.Logs(
            AppLine("2026-09-23 01:47:24.789", "[Transit] Flag:None, RaidId:RAIDID000000000000000001, Count:0, Locations:laboratory -> "),
            AppLine("2026-09-23 01:47:51.168", "GameStarted:31.37(0) real:53.4(0) diff:22.03"));
        Assert.Equal(SituationPhase.InRaid, timeline.Now.Phase.Value);
        Assert.Equal("the-lab", timeline.Now.Map?.Value);
        Assert.NotEqual(lighthouse, timeline.Now.RaidId);
        Assert.Equal(SituationSide.Unknown, timeline.Now.Side?.Value);
        Assert.Equal([SituationPhase.InRaid, SituationPhase.PostRaid, SituationPhase.Loading, SituationPhase.InRaid], timeline.Phases);
    }

    /// <summary>#891: the PC clock steps back four hours mid-raid, and the raid does not notice.</summary>
    [Fact]
    public void AClockStepMidRaidKeepsTheRaidAndItsClock()
    {
        using var timeline = new SituationTimeline();
        PmcCustomsRaid(timeline);
        timeline.At("2026-09-23 20:12:40");
        var raid = timeline.Now.RaidId;
        var before = timeline.Now.Clock!.RemainingAt(timeline.Clock.Now)!.Value;
        var phasesBefore = timeline.Phases.Count;

        timeline.StepClock(TimeSpan.FromHours(-4));
        timeline.Clock.Now += TimeSpan.FromSeconds(30);
        timeline.Service.Refresh();

        Assert.Equal(SituationPhase.InRaid, timeline.Now.Phase.Value);
        Assert.Equal(raid, timeline.Now.RaidId);
        Assert.Equal(before - TimeSpan.FromSeconds(30), timeline.Now.Clock!.RemainingAt(timeline.Clock.Now));
        Assert.Equal(phasesBefore, timeline.Phases.Count);

        // A screenshot named by the corrected clock is a new place, not a new raid.
        timeline.Screenshot("2026-09-23 16:14", 25, 1, 10, 180);
        Assert.Equal(raid, timeline.Now.RaidId);
        Assert.Equal("Old Gas Station", timeline.Now.You?.AreaName);
        Assert.Equal("S", timeline.Now.You?.Facing);

        timeline.Log(Notification("2026-09-23 16:30:00.000", "userMatchOver", "Free", "bigmap", "CUST01", PmcProfile));
        Assert.Equal(SituationPhase.PostRaid, timeline.Now.Phase.Value);
    }

    [Fact]
    public void ASquadmateStillInTheRaidAfterYouDieStaysInItWithTheirAreaAndAge()
    {
        using var timeline = new SituationTimeline();
        PmcCustomsRaid(timeline);
        timeline.Screenshot("2026-09-23 20:10", -12, 4, 30, 0)
            .Relay(Member("Geo", "customs", RaidLifecycleState.InRaid, new WorldPosition(128, 1, -30), TimeSpan.FromSeconds(12)));

        var geo = Assert.Single(timeline.Now.Squad);
        Assert.Equal(SquadMemberState.InRaid, geo.State);
        Assert.Equal("Old Gas Station", geo.AreaName);
        Assert.Equal(Math.Round(Math.Sqrt((140 * 140) + (60 * 60))), geo.DistanceMetres);
        Assert.Equal("NE", geo.Compass);
        Assert.Equal(TimeSpan.FromSeconds(12), geo.AgeAt(timeline.Clock.Now));

        timeline.Log(Notification("2026-09-23 20:25:00.000", "userMatchOver", "Free", "bigmap", "CUST01", PmcProfile))
            .Outcome(SituationOutcome.Died)
            .Relay(Member("Geo", "customs", RaidLifecycleState.InRaid, new WorldPosition(140, 1, -40), TimeSpan.FromSeconds(5)));

        Assert.Equal(SituationPhase.Dead, timeline.Now.Phase.Value);
        geo = Assert.Single(timeline.Now.Squad);
        Assert.Equal(SquadMemberState.InRaid, geo.State);
        Assert.Equal("Old Gas Station", geo.AreaName);
        // You have no position once you are out, so no distance is claimed from it.
        Assert.Null(geo.DistanceMetres);
        Assert.Contains("Geo's companion", geo.Because, StringComparison.Ordinal);
    }

    [Fact]
    public void NextIsTheFirstStopOfTheOpenedRouteOnThisMapAndThenTheSecond()
    {
        using var timeline = new SituationTimeline();
        PmcCustomsRaid(timeline);
        var reason = new ObjectiveRouteReason(ObjectiveRouteReasonKind.NearestFrom, "start");
        timeline.Service.SetPlan(new SituationPlan(
            "customs",
            "your last screenshot",
            [
                new ObjectiveRouteStep(1, "zibbo", "Golden Zibbo lighter", new MapScenePoint(1, 1), 40.4, reason),
                new ObjectiveRouteStep(2, "watch", "Bronze pocket watch", new MapScenePoint(2, 2), 220, reason),
            ],
            timeline.Clock.Now));

        Assert.Equal("Golden Zibbo lighter", timeline.Now.Next?.Label);
        Assert.Equal(40, timeline.Now.Next?.DistanceMetres);
        Assert.Equal("Bronze pocket watch", timeline.Now.Then?.Label);
        Assert.Null(timeline.Now.Then?.DistanceMetres);

        timeline.Service.SetPlan(new SituationPlan("woods", "a spawn", [], timeline.Clock.Now));
        Assert.Null(timeline.Now.Next);
    }

    [Fact]
    public void AFleaScreenshotBetweenRaidsIsAScreenUntilItGoesStale()
    {
        using var timeline = new SituationTimeline();
        timeline.Log(ProfileReload("2026-09-23 19:00:00.000")).At("2026-09-23 19:05:00").Scan(ScanContext.FleaListings);
        Assert.Equal(SituationPhase.Screen, timeline.Now.Phase.Value);
        Assert.Contains("flea market", timeline.Now.Phase.Because, StringComparison.Ordinal);

        timeline.At("2026-09-23 19:09:00");
        Assert.Equal(SituationPhase.Menu, timeline.Now.Phase.Value);
    }

    [Fact]
    public void NothingNewMeansNoNewVersion()
    {
        using var timeline = new SituationTimeline();
        PmcCustomsRaid(timeline);
        var version = timeline.Now.Version;

        timeline.At("2026-09-23 20:05:00");
        timeline.At("2026-09-23 20:06:00");
        timeline.Log(AppLine("2026-09-23 20:06:30.000", "GC::Collect"));

        Assert.Equal(version, timeline.Now.Version);
    }

    [Fact]
    public void ObserversGetTheCurrentSituationAndEveryChangeInOrder()
    {
        using var timeline = new SituationTimeline();
        var seen = new List<long>();
        using var subscription = timeline.Service.Subscribe(new Observer(situation => seen.Add(situation.Version)));
        PmcCustomsRaid(timeline);

        Assert.Equal(Enumerable.Range((int)seen[0], seen.Count).Select(value => (long)value), seen);
        Assert.Equal(timeline.Now.Version, seen[^1]);
        Assert.Contains(timeline.Service.Transitions, transition => transition.To == SituationPhase.InRaid && transition.Because.Length > 0);
    }

    [Fact]
    public void AScreenshotNamedByTheGameIsReadThroughTheRealParser()
    {
        using var timeline = new SituationTimeline();
        PmcCustomsRaid(timeline);
        timeline.ScreenshotNamed("2026-09-23[20-09]_-12.5, 4.2, 30.1_0, 0, 0, 1_12.00 (0).png");

        Assert.Equal("Dorms", timeline.Now.You?.AreaName);
        Assert.Equal(SituationPhase.InRaid, timeline.Now.Phase.Value);
    }

    [Fact]
    public void TheDiagnosticsPanelListsEachTransitionOnce()
    {
        using var timeline = new SituationTimeline();
        var panel = new TarkovCompanion.App.ViewModels.V2.Setup.SituationDiagnosticsViewModel(timeline.Service, timeline.Clock, null);
        PmcCustomsRaid(timeline);

        var versions = timeline.Service.Transitions.Select(transition => transition.Version).ToArray();
        Assert.Equal(versions.Distinct().Count(), versions.Length);
        Assert.Equal(Math.Min(8, versions.Length), panel.Transitions.Count);
        Assert.Equal(panel.Transitions.Count, panel.Transitions.Distinct().Count());
        Assert.StartsWith($"v{timeline.Now.Version} ", panel.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>A render showed one transition logged twice: a places answer re-entered the fold.</summary>
    [Fact]
    public void ACallbackFromInsideTheFoldPublishesEachChangeOnce()
    {
        using var timeline = new SituationTimeline();
        timeline.Places.RaiseOnDescribe = true;
        PmcCustomsRaid(timeline);
        timeline.Screenshot("2026-09-23 20:10", -12, 4, 30, 45);

        var versions = timeline.Service.Transitions.Select(transition => transition.Version).ToArray();
        Assert.Equal(versions.Distinct().Count(), versions.Length);
        AssertVersionsRiseByOne(timeline);
        Assert.Equal("Dorms", timeline.Now.You?.AreaName);
    }

    private static void AssertVersionsRiseByOne(SituationTimeline timeline)
    {
        for (var index = 1; index < timeline.Seen.Count; index++)
        {
            Assert.Equal(timeline.Seen[index - 1].Version + 1, timeline.Seen[index].Version);
        }
    }

    private sealed class Observer(Action<Situation> next) : IObserver<Situation>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(Situation value) => next(value);
    }
}
