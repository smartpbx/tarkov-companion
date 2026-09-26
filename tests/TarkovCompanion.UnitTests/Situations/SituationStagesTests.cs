using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Situations;
using static TarkovCompanion.UnitTests.Situations.SituationTimeline;

namespace TarkovCompanion.UnitTests.Situations;

/// <summary>
/// [#403] The finer matching and loading stages and the party's readiness, as 1.1.5.1.47510 wrote
/// them (2026-09-22..25). Every line is synthetic in the real shapes and the real order.
/// </summary>
public sealed class SituationStagesTests
{
    private static string Backend(string stamp, string type, string payload) =>
        $"{stamp}|1.1.5.1.47510|Info|backend|WebSocketSharp - message received: NOTIFICATION e0000000000000000000000a {type} {payload}";

    private static string Ready(string stamp, long aid, string name) => Backend(stamp, "groupMatchRaidReady",
        "[{\"type\":\"groupMatchRaidReady\",\"eventId\":\"e1\",\"extendedProfile\":{\"_id\":\"p" + aid + "\",\"aid\":" + aid +
        ",\"Info\":{\"Nickname\":\"" + name + "\",\"Side\":\"Bear\",\"Level\":20},\"isLeader\":false,\"isReady\":true}}]");

    private static string NotReady(string stamp, long aid) => Backend(stamp, "groupMatchRaidNotReady",
        "[{\"type\":\"groupMatchRaidNotReady\",\"eventId\":\"e2\",\"aid\":" + aid + "}]");

    /// <summary>
    /// The second raid of 2026-09-23 wrote MatchingCompleted between queue steps G and H. Reading the
    /// last line put a loading raid back in the queue; the furthest stage keeps it loading.
    /// </summary>
    [Fact]
    public void AQueueStepAfterMatchingCompletedDoesNotPutTheRaidBackInTheQueue()
    {
        using var timeline = new SituationTimeline();
        timeline.Logs(
            ProfileReload("2026-09-23 22:00:00.000"),
            AppLine("2026-09-23 22:03:40.293", "Matching with group id: 1"),
            AppLine("2026-09-23 22:03:41.000", "scene preset path:maps/customs_preset.bundle rcid:customs.scenespreset.asset"),
            AppLine("2026-09-23 22:03:41.989", "TRACE-NetworkGameMatching G"));
        Assert.Equal(SituationPhase.Matching, timeline.Now.Phase.Value);
        Assert.Contains("10:03:40", timeline.Now.Phase.Because, StringComparison.Ordinal);

        timeline.Logs(
            AppLine("2026-09-23 22:03:44.054", "MatchingCompleted:3.76 real:3.76 diff:0"),
            AppLine("2026-09-23 22:03:46.226", "TRACE-NetworkGameMatching H"),
            AppLine("2026-09-23 22:03:46.227", "TRACE-NetworkGameMatching I"));

        Assert.Equal(SituationPhase.Loading, timeline.Now.Phase.Value);
    }

    /// <summary>GameSpawn comes ten to twenty seconds before GameStarted; the brief and the Now panel read it from Stages.</summary>
    [Fact]
    public void SpawningIsALoadingStageWithItsTimeAndEveryStageKeepsItsTimestamp()
    {
        using var timeline = new SituationTimeline();
        timeline.Logs(
            ProfileReload("2026-09-23 21:40:00.000"),
            AppLine("2026-09-23 21:48:26.506", "Matching with group id: 1"),
            AppLine("2026-09-23 21:48:27.000", "scene preset path:maps/customs_preset.bundle rcid:customs.scenespreset.asset"),
            AppLine("2026-09-23 21:48:28.414", "TRACE-NetworkGameMatching G"),
            AppLine("2026-09-23 21:48:41.537", "MatchingCompleted:11.26 real:15.03 diff:3.76"),
            AppLine("2026-09-23 21:48:52.056", "LocationLoaded:20.14 real:25.55 diff:5.41"),
            Notification("2026-09-23 21:48:52.400", "userConfirmed", "Busy", "bigmap", "CUST02", PmcProfile),
            ProfileStatus("2026-09-23 21:48:52.470", "bigmap", "CUST02", PmcProfile),
            AppLine("2026-09-23 21:49:35.393", "GameSpawn:58.67(0.09) real:68.88(0.08) diff:10.2"));

        Assert.Equal(SituationPhase.Loading, timeline.Now.Phase.Value);
        Assert.Contains("spawning", timeline.Now.Phase.Because, StringComparison.Ordinal);
        Assert.Equal(
            [RaidPhaseMarkerKind.MatchingStarted, RaidPhaseMarkerKind.MatchingStep, RaidPhaseMarkerKind.MatchingCompleted,
             RaidPhaseMarkerKind.LocationLoaded, RaidPhaseMarkerKind.Spawning],
            timeline.Now.Stages.Select(stage => stage.Kind));
        Assert.Equal(new DateTimeOffset(2026, 9, 24, 1, 48, 26, 506, TimeSpan.Zero), timeline.Now.Stages[0].ObservedUtc);

        timeline.Log(AppLine("2026-09-23 21:49:45.923", "GameStarted:66.63(5.53) real:79.41(6.6) diff:12.78"));
        Assert.Equal(SituationPhase.InRaid, timeline.Now.Phase.Value);
        Assert.Equal(RaidPhaseMarkerKind.GameStarted, timeline.Now.Stages[^1].Kind);
    }

    /// <summary>A queue left and entered again starts its stages over.</summary>
    [Fact]
    public void ANewReadyStartsTheStagesOver()
    {
        using var timeline = new SituationTimeline();
        timeline.Logs(
            ProfileReload("2026-09-23 22:00:00.000"),
            AppLine("2026-09-23 22:01:00.000", "Matching with group id: 1"),
            AppLine("2026-09-23 22:01:02.000", "TRACE-NetworkGameMatching G"),
            AppLine("2026-09-23 22:02:00.000", "Matching with group id: 1"));

        Assert.Equal(SituationPhase.Matching, timeline.Now.Phase.Value);
        Assert.Equal(RaidPhaseMarkerKind.MatchingStarted, Assert.Single(timeline.Now.Stages).Kind);
    }

    /// <summary>
    /// [#403] A squadmate who pressed Ready and then took it back showed as ready: 161 not-ready lines
    /// in six sessions were read as nothing.
    /// </summary>
    [Fact]
    public void ASquadmateWhoUnreadiesIsNoLongerReady()
    {
        using var timeline = new SituationTimeline();
        timeline.Logs(
            ProfileReload("2026-09-23 20:00:00.000"),
            Ready("2026-09-23 20:00:10.000", 1000002, "PLAYER_B"),
            Ready("2026-09-23 20:00:11.000", 1000003, "PLAYER_C"));
        Assert.Equal(2, timeline.Now.Party?.ReadyCount);

        timeline.Log(NotReady("2026-09-23 20:00:20.000", 1000003));

        var party = timeline.Now.Party!;
        Assert.Equal(1, party.ReadyCount);
        Assert.Equal(false, party.Members.Single(member => member.Name == "PLAYER_C").IsReady);
        Assert.Contains("1 of 2 ready", party.Because, StringComparison.Ordinal);
    }
}
