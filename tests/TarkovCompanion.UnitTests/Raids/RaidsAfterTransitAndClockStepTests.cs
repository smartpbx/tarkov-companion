using System.Globalization;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.UnitTests.Raids;

/// <summary>
/// Three things one player's real logs of 2026-09-22..25 showed the raid tracking getting wrong (#892).
/// </summary>
/// <remarks>
/// The sequences, maps and times are the real ones. The ids, profile ids, addresses and session
/// ids are made up, because the repository is public and the real lines carry all of them.
/// </remarks>
public sealed class RaidsAfterTransitAndClockStepTests
{
    private const string Scav = "SCAVPROFILE000000000001";

    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("test-4", TimeSpan.FromHours(-4), "test-4", "test-4");

    private static string App(string stamp, string text) => $"{stamp}|1.1.5.1.47510|Info|application|{text}";

    private static string Notification(string stamp, string type, string status, string location, string shortId) =>
        $"{stamp}|1.1.5.1.47510|Info|backend|WebSocketSharp - message received: NOTIFICATION [EV-{shortId}-{type}] {type} " +
        "[{\"type\":\"" + type + "\",\"eventId\":\"EV-" + shortId + "-" + type + "\",\"profileid\":\"" + Scav +
        "\",\"status\":\"" + status + "\",\"location\":\"" + location +
        "\",\"raidMode\":\"Online\",\"mode\":\"deathmatch\",\"shortId\":\"" + shortId + "\"}]";

    private static string ProfileStatus(string stamp, string location, string shortId) =>
        $"{stamp}|1.1.5.1.47510|Debug|application|TRACE-NetworkGameCreate profileStatus: " +
        $"'Profileid: {Scav}, Status: Busy, RaidMode: Online, Ip: 0.0.0.0, Port: 17000, Location: {location}, " +
        $"Sid: SID, GameMode: deathmatch, shortId: {shortId}'";

    private static string ProfileReload(string stamp) =>
        App(stamp, "CompleteSelectedProfile ProfileId:PMCPROFILE0000000000001 AccountId:1");

    /// <summary>What an offline or transit raid into The Lab wrote, and all it wrote, on 2026-09-23.</summary>
    private static string[] OfflineLab(string matched, string preset, string loaded, string transit, string started) =>
    [
        App(matched, "MatchingCompleted:0 real:0 diff:0"),
        App(preset, "scene preset path:maps/laboratory_preset.bundle rcid:laboratory.ScenesPreset.asset"),
        App(loaded, "LocationLoaded:10.97 real:18.09 diff:7.12"),
        App(transit, "[Transit] Flag:None, RaidId:RAIDID000000000000000001, Count:0, Locations:laboratory -> "),
        App(started, "GameStarted:31.37(0) real:53.4(0) diff:22.03"),
    ];

    private static RaidSnapshot Feed(RaidStateService state, EftLogParser parser, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (parser.ParseLine(line, RaidReplayDecision.WrittenUtc(line, Zone)!.Value) is { } evidence)
            {
                state.Apply(evidence);
            }
        }

        return state.Current;
    }

    private static DateTimeOffset Local(string stamp) =>
        new DateTimeOffset(DateTime.Parse(stamp, CultureInfo.InvariantCulture), TimeSpan.FromHours(-4)).ToUniversalTime();

    [Theory]
    [InlineData("scene preset path:maps/laboratory_preset.bundle rcid:laboratory.ScenesPreset.asset", "the-lab")]
    [InlineData("scene preset path:maps/city_preset.bundle rcid:city.scenespreset.asset", "streets-of-tarkov")]
    [InlineData("scene preset path:maps/rezerv_base_preset.bundle rcid:Rezerv_Base.scenespreset.asset", "reserve")]
    [InlineData("scene preset path:maps/factory_day_preset.bundle rcid:factory_day.scenespreset.asset", "factory")]
    public void TheScenePresetNamesTheMapOfTheRaidItLoads(string text, string map)
    {
        var evidence = new EftLogParser().ParseLine(App("2026-09-23 01:46:59.112", text), DateTimeOffset.UnixEpoch);

        Assert.NotNull(evidence);
        Assert.Equal(map, evidence.MapId);
        Assert.Equal(RaidLifecycleState.LoadingRaid, evidence.SuggestedState);
    }

    [Fact]
    public void ATransitLineNamesTheMapItGoesTo()
    {
        var parser = new EftLogParser();

        Assert.Equal("the-lab", parser.ParseLine(
            App("2026-09-23 01:47:24.789", "[Transit] Flag:None, RaidId:R, Count:0, Locations:laboratory -> "),
            DateTimeOffset.UnixEpoch)?.MapId);
        Assert.Equal("the-lab", parser.ParseLine(
            App("2026-09-23 01:47:24.789", "[Transit] Flag:None, RaidId:R, Count:0, Locations:Lighthouse -> laboratory"),
            DateTimeOffset.UnixEpoch)?.MapId);
    }

    /// <summary>output_000.log prints some notifications over many lines, and a line from inside one is nobody's.</summary>
    [Fact]
    public void ALineFromInsideAPrettyPrintedNotificationNamesNoMap()
    {
        Assert.Null(new EftLogParser().ParseLine("  \"location\": \"factory4_day\",", DateTimeOffset.UnixEpoch));
    }

    /// <summary>
    /// 2026-09-23 01:40: a scav Lighthouse raid ends Transfer, and six minutes later the player is in
    /// The Lab. That raid wrote no short id, and the companion carried Lighthouse into it.
    /// </summary>
    [Fact]
    public void ARaidAfterATransferIsOnTheMapItsSceneLoadsNotTheMapBefore()
    {
        var parser = new EftLogParser();
        var state = new RaidStateService();
        var lighthouse = Feed(state, parser,
        [
            Notification("2026-09-23 01:21:01.824", "userConfirmed", "Busy", "Lighthouse", "LIGHT1"),
            App("2026-09-23 01:22:37.981", "GameStarted:99.52(99.52) real:115.89(115.89) diff:16.37"),
        ]);
        Assert.Equal("lighthouse", lighthouse.MapId);
        var lighthouseRaid = lighthouse.RaidId;

        var ended = Feed(state, parser, [Notification("2026-09-23 01:40:19.000", "userMatchOver", "Transfer", "Lighthouse", "LIGHT1")]);
        Assert.Equal(RaidLifecycleState.PostRaid, ended.State);

        var loading = Feed(state, parser, OfflineLab("2026-09-23 01:46:57.767", "2026-09-23 01:46:59.112", "2026-09-23 01:47:15.860", "2026-09-23 01:47:24.789", "2026-09-23 01:47:51.168")[..3]);
        Assert.Equal(RaidLifecycleState.LoadingRaid, loading.State);
        Assert.Equal("the-lab", loading.MapId);

        var lab = Feed(state, parser, OfflineLab("2026-09-23 01:46:57.767", "2026-09-23 01:46:59.112", "2026-09-23 01:47:15.860", "2026-09-23 01:47:24.789", "2026-09-23 01:47:51.168")[3..]);
        Assert.Equal(RaidLifecycleState.InRaid, lab.State);
        Assert.Equal("the-lab", lab.MapId);
        Assert.NotEqual(lighthouseRaid, lab.RaidId);
        Assert.Null(lab.Side);
    }

    /// <summary>A loading line that names no map says nothing about which map the next raid is on.</summary>
    [Fact]
    public void LoadingTheNextRaidDoesNotKeepTheLastRaidsMap()
    {
        var parser = new EftLogParser();
        var state = new RaidStateService();
        Feed(state, parser,
        [
            Notification("2026-09-23 01:21:01.824", "userConfirmed", "Busy", "Lighthouse", "LIGHT1"),
            Notification("2026-09-23 01:40:19.000", "userMatchOver", "Transfer", "Lighthouse", "LIGHT1"),
        ]);

        var loading = Feed(state, parser, [App("2026-09-23 01:47:15.860", "LocationLoaded:10.97 real:18.09 diff:7.12")]);

        Assert.Equal(RaidLifecycleState.LoadingRaid, loading.State);
        Assert.Null(loading.MapId);
        Assert.Null(loading.Side);
    }

    /// <summary>
    /// 01:47 and 01:54: two offline Labs raids with a profile reload between them, which the
    /// companion ran together into one raid.
    /// </summary>
    [Fact]
    public void TwoOfflineRaidsWithAProfileReloadBetweenThemAreTwoRaids()
    {
        var parser = new EftLogParser();
        var state = new RaidStateService();
        var first = Feed(state, parser, OfflineLab("2026-09-23 01:46:57.767", "2026-09-23 01:46:59.112", "2026-09-23 01:47:15.860", "2026-09-23 01:47:24.789", "2026-09-23 01:47:51.168"));
        Assert.Equal(RaidLifecycleState.InRaid, first.State);
        var firstRaid = first.RaidId;

        var back = Feed(state, parser, [ProfileReload("2026-09-23 01:53:44.870")]);
        Assert.Equal(RaidLifecycleState.PostRaid, back.State);

        var second = Feed(state, parser, OfflineLab("2026-09-23 01:54:08.991", "2026-09-23 01:54:10.057", "2026-09-23 01:54:24.390", "2026-09-23 01:54:34.138", "2026-09-23 01:54:57.203"));
        Assert.Equal(RaidLifecycleState.InRaid, second.State);
        Assert.Equal("the-lab", second.MapId);
        Assert.NotNull(second.RaidId);
        Assert.NotEqual(firstRaid, second.RaidId);
    }

    /// <summary>A game relaunched mid-raid reloads the profile before it reconnects; that is not the raid's end.</summary>
    [Fact]
    public void AProfileReloadDoesNotEndARaidThatHasItsOwnId()
    {
        var parser = new EftLogParser();
        var state = new RaidStateService();
        var raid = Feed(state, parser,
        [
            ProfileStatus("2026-09-23 02:21:35.035", "laboratory", "LAB0001"),
            App("2026-09-23 02:22:32.669", "GameStarted:64.24(11.64) real:76.48(12.02) diff:12.24"),
        ]);

        var after = Feed(state, parser, [ProfileReload("2026-09-23 02:30:00.000")]);

        Assert.Equal(RaidLifecycleState.InRaid, after.State);
        Assert.Equal(raid.RaidId, after.RaidId);
        Assert.Equal("the-lab", after.MapId);
    }

    /// <summary>
    /// The 2026-09-22 session: every file's clock stepped back four hours at the same moment. A
    /// Shoreline raid began at 22:59:57 on the fast clock and ended before the step; a Lighthouse
    /// raid began at 22:59:53 on the corrected clock after it. Sorted by stamp, the Lighthouse start
    /// came first and the Shoreline end closed it, so a restart during it found no raid.
    /// </summary>
    [Fact]
    public void AReplayAcrossABackwardClockStepKeepsTheRaidThatBeganAfterIt()
    {
        const string ApplicationFile = "application_000.log";
        const string BackendFile = "backend_000.log";
        var parser = new EftLogParser();
        var files = new (string File, string[] Lines)[]
        {
            (ApplicationFile,
            [
                ProfileStatus("2026-09-22 22:59:57.100", "Shoreline", "SHORE1"),
                App("2026-09-22 23:01:41.000", "GameStarted:99.52(99.52) real:115.89(115.89) diff:16.37"),
                ProfileReload("2026-09-23 01:56:45.000"),
                // The step: 01:56:45 on the fast clock, then 22:22:46 on the right one.
                ProfileReload("2026-09-22 22:22:46.000"),
                ProfileStatus("2026-09-22 22:59:53.000", "Lighthouse", "LIGHT2"),
                App("2026-09-22 23:01:44.000", "GameStarted:102.83(102.83) real:117.42(117.42) diff:14.58"),
            ]),
            (BackendFile,
            [
                Notification("2026-09-22 22:59:57.000", "userConfirmed", "Busy", "Shoreline", "SHORE1"),
                Notification("2026-09-22 23:35:00.000", "userMatchOver", "Free", "Shoreline", "SHORE1"),
                Notification("2026-09-22 22:59:55.000", "userConfirmed", "Busy", "Lighthouse", "LIGHT2"),
            ]),
        };

        // Replayed the way the watcher does it: one file after another, in name order.
        var replayed = new List<ReplayedRaidLine>();
        foreach (var (file, lines) in files)
        {
            foreach (var line in lines)
            {
                var evidence = parser.ParseLine(line, DateTimeOffset.UnixEpoch);
                Assert.NotNull(evidence);
                replayed.Add(new(evidence, RaidReplayDecision.WrittenUtc(line, Zone), replayed.Count) { Source = file });
            }
        }

        var verdict = RaidReplayDecision.Decide(replayed, gameIsRunning: true, Local("2026-09-22 23:03:00"));

        Assert.True(verdict.IsLive, verdict.Reason);
        Assert.Equal("lighthouse", verdict.Start?.MapId);
        Assert.Equal("LIGHT2", verdict.Start?.RaidKey);
        Assert.Equal("A raid on lighthouse is still running.", verdict.Reason);
        Assert.Equal(Local("2026-09-22 23:01:44"), verdict.LastSeenUtc);
    }

    /// <summary>A companion restarted during an offline Lab raid: the scene preset is the only thing that names the map.</summary>
    [Fact]
    public void AReplayDuringAnOfflineRaidResumesItOnTheMapItsSceneLoaded()
    {
        var parser = new EftLogParser();
        var replayed = OfflineLab("2026-09-23 01:46:57.767", "2026-09-23 01:46:59.112", "2026-09-23 01:47:15.860", "2026-09-23 01:47:24.789", "2026-09-23 01:47:51.168")
            .Prepend(ProfileReload("2026-09-23 01:42:47.775"))
            .Select((line, index) => new ReplayedRaidLine(
                parser.ParseLine(line, DateTimeOffset.UnixEpoch)!, RaidReplayDecision.WrittenUtc(line, Zone), index) { Source = "application" })
            .Where(line => line.Evidence is not null)
            .ToArray();

        var verdict = RaidReplayDecision.Decide(replayed, gameIsRunning: true, Local("2026-09-23 01:50:00"));

        Assert.True(verdict.IsLive, verdict.Reason);
        Assert.Equal("the-lab", verdict.Start?.MapId);

        // And once the profile has reloaded, that raid is over.
        var reload = ProfileReload("2026-09-23 01:53:44.870");
        var over = RaidReplayDecision.Decide(
            [.. replayed, new(parser.ParseLine(reload, DateTimeOffset.UnixEpoch)!, RaidReplayDecision.WrittenUtc(reload, Zone), replayed.Length) { Source = "application" }],
            gameIsRunning: true,
            Local("2026-09-23 01:54:00"));
        Assert.Null(over.Start);
    }
}
