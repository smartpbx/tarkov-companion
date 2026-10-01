using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

public sealed class EarlyRaidSpawnPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Early_pmc_window_returns_only_grouped_player_areas_near_the_start()
    {
        var policy = new EarlyRaidSpawnPolicy(new FixedClock(Now));
        MapFeature[] features =
        [
            Spawn("Near", 100, 0, "pmc"),
            Spawn("Near", 110, 5, "pmc"),
            Spawn("Shared", 180, 0, "all"),
            Spawn("Far", 301, 0, "pmc"),
            Spawn("Scav", 120, 0, "scav"),
            new(MapFeatureKind.Spawn, "Bot", At(140, 0), "pmc", "bot"),
        ];

        var result = policy.Select(
            features,
            At(0, 0),
            At(20, 0),
            MapFeatureFaction.Pmc,
            Now - TimeSpan.FromMinutes(4));

        Assert.Equal(EarlyRaidSpawnPhase.Active, result.Phase);
        Assert.Equal(["Near · 2 points", "Shared"], result.Areas.Select(area => area.Name));
        Assert.All(result.Areas, area => Assert.InRange(area.MetresFromStart, 0, 300));
    }

    [Fact]
    public void Pmc_window_expires_at_five_minutes_by_the_injected_clock()
    {
        var clock = new FixedClock(Now);
        var policy = new EarlyRaidSpawnPolicy(clock);

        var active = policy.Select([Spawn("A", 100, 0, "pmc")], At(0, 0), null, MapFeatureFaction.Pmc, Now);
        clock.Now = Now + TimeSpan.FromMinutes(5);
        var expired = policy.Select([Spawn("A", 100, 0, "pmc")], At(0, 0), null, MapFeatureFaction.Pmc, Now);

        Assert.Equal(EarlyRaidSpawnPhase.Active, active.Phase);
        Assert.Single(active.Areas);
        Assert.Equal(EarlyRaidSpawnPhase.Expired, expired.Phase);
        Assert.Empty(expired.Areas);
    }

    [Fact]
    public void A_scav_raid_is_unaffected()
    {
        var policy = new EarlyRaidSpawnPolicy(new FixedClock(Now));

        var result = policy.Select([Spawn("A", 100, 0, "pmc")], At(0, 0), null, MapFeatureFaction.Scav, Now);

        Assert.Equal(EarlyRaidSpawnPhase.Unaffected, result.Phase);
        Assert.Empty(result.Areas);
    }

    /// <summary>[#985] Nothing named the side: shown as a PMC raid, and the selection says it was assumed.</summary>
    [Fact]
    public void An_unknown_side_is_shown_as_pmc_without_the_scav_areas()
    {
        var policy = new EarlyRaidSpawnPolicy(new FixedClock(Now));

        var result = policy.Select([Spawn("A", 100, 0, "pmc"), Spawn("S", 120, 0, "scav")], At(0, 0), null, MapFeatureFaction.Unknown, Now);

        Assert.Equal(EarlyRaidSpawnPhase.Active, result.Phase);
        Assert.True(result.SideAssumed);
        Assert.Equal(["A"], result.Areas.Select(area => area.Name));
    }

    /// <summary>[#985] Before any screenshot: every PMC area, unmeasured.</summary>
    [Fact]
    public void Before_a_screenshot_every_pmc_area_shows()
    {
        var policy = new EarlyRaidSpawnPolicy(new FixedClock(Now));

        var result = policy.Select([Spawn("A", 100, 0, "pmc"), Spawn("B", 900, 0, "all"), Spawn("S", 120, 0, "scav")], null, null, MapFeatureFaction.Pmc, Now);

        Assert.Equal(EarlyRaidSpawnPhase.Active, result.Phase);
        Assert.False(result.IsNearby);
        Assert.Equal(["A", "B"], result.Areas.Select(area => area.Name));
        Assert.All(result.Areas, area => Assert.True(double.IsNaN(area.MetresFromStart)));
    }

    /// <summary>[#985] Five minutes from GameStarted, when it came after the start and within a load's length.</summary>
    [Fact]
    public void The_window_runs_from_the_game_starting()
    {
        Assert.Equal(Now + TimeSpan.FromSeconds(95), EarlyRaidSpawnPolicy.OpensAt(Now, Now + TimeSpan.FromSeconds(95)));
        Assert.Equal(Now, EarlyRaidSpawnPolicy.OpensAt(Now, null));
        Assert.Equal(Now, EarlyRaidSpawnPolicy.OpensAt(Now, Now - TimeSpan.FromMinutes(1)));
        Assert.Equal(Now, EarlyRaidSpawnPolicy.OpensAt(Now, Now + TimeSpan.FromMinutes(40)));
    }

    private static WorldPosition At(double x, double z) => new(x, 0, z);

    private static MapFeature Spawn(string name, double x, double z, string side) =>
        new(MapFeatureKind.Spawn, name, At(x, z), side, "player");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
