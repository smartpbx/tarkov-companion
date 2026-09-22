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

    [Theory]
    [InlineData(MapFeatureFaction.Scav)]
    [InlineData(MapFeatureFaction.Unknown)]
    public void Non_pmc_raids_are_unaffected(MapFeatureFaction side)
    {
        var policy = new EarlyRaidSpawnPolicy(new FixedClock(Now));

        var result = policy.Select([Spawn("A", 100, 0, "pmc")], At(0, 0), null, side, Now);

        Assert.Equal(EarlyRaidSpawnPhase.Unaffected, result.Phase);
        Assert.Empty(result.Areas);
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
