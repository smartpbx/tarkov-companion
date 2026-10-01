using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.UnitTests.Situations;
using static TarkovCompanion.UnitTests.Situations.SituationTimeline;

namespace TarkovCompanion.UnitTests.Raids;

/// <summary>
/// [#985] "i also still dont see the nearby pmc spawns when raid starts": a raid start replayed
/// through the app's own parser, raid state and situation, on a PC whose clock is four hours fast.
/// </summary>
/// <remarks>
/// Every line is synthetic, shaped like the owner's 2026-09-24 logs: the log's local stamps are
/// real UTC because the PC reads its UTC hardware clock as Eastern time, so the notification's
/// ObjectId (server time) is four hours behind the PC's own idea of now. On main the raid began
/// "four hours ago" and the five-minute window was closed before the loading screen was.
/// </remarks>
public sealed class OpeningSpawnsAtRaidStartTests
{
    /// <summary>2026-09-24T20:00:00Z, the server's own stamp on the confirmation.</summary>
    private const string ServerStampedId = "6ab58140" + "0000000000000000";

    private static readonly DateTimeOffset ServerStart = new(2026, 9, 24, 20, 0, 0, TimeSpan.Zero);

    /// <summary>Five PMC areas (one of three points, one shared), a scav one and a scripted PMC.</summary>
    private static readonly MapFeature[] Customs =
    [
        Spawn("Old Gas", 300, 100, "pmc"), Spawn("Old Gas", 305, 104, "pmc"), Spawn("Old Gas", 296, 98, "pmc"),
        Spawn("Crossroads", -200, -300, "pmc"),
        Spawn("Dorms", 60, 20, "pmc"),
        Spawn("Big Red", 500, 400, "pmc"),
        Spawn("Shared Yard", -50, 90, "all"),
        Spawn("Scav Zone", 20, 10, "scav"),
        new(MapFeatureKind.Spawn, "Scripted", new(10, 0, 10), "pmc", "bot"),
    ];

    [Fact]
    public void A_pmc_raid_shows_every_pmc_area_at_once_then_the_nearby_ones_then_nothing_after_five_minutes()
    {
        using var timeline = new SituationTimeline();
        var policy = new EarlyRaidSpawnPolicy(timeline.Clock);
        timeline.Logs(
            ProfileReload("2026-09-24 19:58:00.000"),
            AppLine("2026-09-24 19:59:00.000", "Matching with group id: 1"),
            AppLine("2026-09-24 19:59:02.000", "scene preset path:maps/customs_preset.bundle rcid:customs.scenespreset.asset"),
            AppLine("2026-09-24 19:59:20.000", "MatchingCompleted:11.26 real:15.03 diff:3.76"),
            AppLine("2026-09-24 19:59:58.000", "LocationLoaded:10.97 real:18.09 diff:7.12"),
            Confirmed("2026-09-24 20:00:01.000", PmcProfile, "CUST01"),
            ProfileStatus("2026-09-24 20:00:02.000", "bigmap", "CUST01", PmcProfile));

        // The raid's start is on the PC's clock, a second before the line was read, not four hours.
        Assert.Equal(RaidLifecycleState.InRaid, timeline.Raid.State);
        Assert.Equal("customs", timeline.Raid.MapId);
        Assert.Equal(ServerStart + TimeSpan.FromHours(4), timeline.Raid.StartedUtc);

        // Map known, still loading, no screenshot: every PMC area of the map, nothing scav or scripted.
        var loading = Select(timeline, policy);
        Assert.Equal(EarlyRaidSpawnPhase.Active, loading.Phase);
        Assert.False(loading.IsNearby);
        Assert.False(loading.SideAssumed);
        Assert.Equal(["Old Gas · 3 points", "Crossroads", "Dorms", "Big Red", "Shared Yard"], loading.Areas.Select(area => area.Name));

        // The first screenshot narrows them to the nearby ones, and the lines are drawn at full strength.
        timeline.Log(AppLine("2026-09-24 20:01:40.000", "GameStarted:31.37(0) real:53.4(0) diff:22.03"));
        timeline.Screenshot("2026-09-24 20:02", 0, 1, 0, 90);
        var nearby = Select(timeline, policy);
        Assert.Equal(EarlyRaidSpawnPhase.Active, nearby.Phase);
        Assert.True(nearby.IsNearby);
        Assert.Equal(["Dorms", "Shared Yard"], SpawnLines.Within(nearby.Areas, SpawnLines.DefaultRadiusMetres).Select(area => area.Name));
        Assert.Equal(1, SpawnLines.Strength(timeline.Clock.Now - nearby.OpenedUtc!.Value));

        // Counted from the moment the player could move, not from the confirmation (which would have closed at 20:05).
        timeline.At("2026-09-24 20:06:30");
        Assert.Equal(EarlyRaidSpawnPhase.Active, Select(timeline, policy).Phase);

        timeline.At("2026-09-24 20:06:41");
        var expired = Select(timeline, policy);
        Assert.Equal(EarlyRaidSpawnPhase.Expired, expired.Phase);
        Assert.Empty(expired.Areas);
    }

    [Fact]
    public void A_scav_raid_shows_nothing()
    {
        using var timeline = new SituationTimeline();
        var policy = new EarlyRaidSpawnPolicy(timeline.Clock);
        timeline.Logs(
            ProfileReload("2026-09-24 19:58:00.000"),
            Confirmed("2026-09-24 20:00:01.000", ScavProfile, "SCAV01"),
            ProfileStatus("2026-09-24 20:00:02.000", "bigmap", "SCAV01", ScavProfile));

        Assert.Equal("scav", timeline.Raid.Side);
        var selection = Select(timeline, policy);
        Assert.Equal(EarlyRaidSpawnPhase.Unaffected, selection.Phase);
        Assert.Empty(selection.Areas);
    }

    [Fact]
    public void A_raid_no_line_names_the_side_of_is_shown_as_pmc_and_says_so()
    {
        var clock = new SituationTimeline.ManualClock(ServerStart + TimeSpan.FromMinutes(1));
        var selection = new EarlyRaidSpawnPolicy(clock).Select(Customs, null, null, MapFeatureFaction.Unknown, ServerStart);

        Assert.Equal(EarlyRaidSpawnPhase.Active, selection.Phase);
        Assert.True(selection.SideAssumed);
        Assert.Equal(5, selection.Areas.Count);
        Assert.DoesNotContain(selection.Areas, area => area.Side == MapFeatureFaction.Scav);
    }

    [Theory]
    [InlineData(0, 0)] // a PC that is right
    [InlineData(4 * 3600, 4 * 3600)] // the owner's: UTC hardware clock read as Eastern
    [InlineData(-4 * 3600, -4 * 3600)]
    [InlineData((5 * 3600) + 1800, (5 * 3600) + 1800)] // a half-hour zone
    [InlineData(25, 0)] // a line read 25 s after it was written
    [InlineData((4 * 3600) + 40, 4 * 3600)]
    [InlineData(3 * 24 * 3600, 0)] // read days later: not a clock, left alone
    public void A_server_start_moves_by_whole_quarter_hours_onto_this_pcs_clock(int readAfterSeconds, int movedSeconds)
    {
        var read = ServerStart + TimeSpan.FromSeconds(readAfterSeconds);

        Assert.Equal(ServerStart + TimeSpan.FromSeconds(movedSeconds), RaidStartClock.OnThisPc(ServerStart, read));
    }

    [Fact]
    public void A_raid_recovered_at_startup_keeps_the_servers_start()
    {
        var restart = ServerStart + TimeSpan.FromHours(4) + TimeSpan.FromMinutes(12);

        Assert.Equal(ServerStart, RaidStartClock.For(ServerStart, resumed: true, restart));
        Assert.Equal(restart, RaidStartClock.For(null, resumed: false, restart));
    }

    private static string Confirmed(string stamp, string profile, string shortId) =>
        Notification(stamp, "userConfirmed", "Busy", "bigmap", shortId, profile)
            .Replace($"EV-{shortId}-userConfirmed", ServerStampedId, StringComparison.Ordinal);

    /// <summary>What the Raid map asks for on a rebuild: the first screenshot, the latest, the side, the start, GameStarted.</summary>
    private static EarlyRaidSpawnSelection Select(SituationTimeline timeline, EarlyRaidSpawnPolicy policy)
    {
        var raid = timeline.Raid;
        var side = raid.Side switch
        {
            "PMC" => MapFeatureFaction.Pmc,
            "scav" => MapFeatureFaction.Scav,
            _ => MapFeatureFaction.Unknown,
        };
        var gameStarted = timeline.Now.Stages.LastOrDefault(stage => stage.Kind == RaidPhaseMarkerKind.GameStarted)?.ObservedUtc;
        return policy.Select(
            Customs,
            raid.PositionTrail.Count > 0 ? raid.PositionTrail[0].Position : null,
            raid.LastKnownPosition?.Position,
            side,
            raid.StartedUtc,
            gameStarted);
    }

    private static MapFeature Spawn(string name, double x, double z, string side) =>
        new(MapFeatureKind.Spawn, name, new(x, 0, z), side, "player");
}
