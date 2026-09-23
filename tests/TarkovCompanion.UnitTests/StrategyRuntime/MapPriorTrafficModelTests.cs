using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Strategy;
using TarkovCompanion.Application.Services.Strategy.Prior;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Strategy;

namespace TarkovCompanion.UnitTests.StrategyRuntime;

public sealed class MapPriorTrafficModelTests
{
    private static readonly MapPriorTrafficModel Model = new(new StrategyModel());

    [Fact]
    public void AnEmptyBasisYieldsNoFieldAndSaysWhatWasMissing()
    {
        // Place names alone say nothing about where players move.
        var prior = Model.Build(Inputs([new(TrafficPriorSourceKind.NamedPlace, new(500, 500), 1, "Dorms")]), RaidPhase.Early);

        Assert.False(prior.HasField);
        Assert.Empty(prior.Hotspots);
        Assert.True(prior.Basis.IsEmpty);
        Assert.Equal(0, prior.Basis.PlayerSpawns);
    }

    [Fact]
    public void AMapThatCannotBeMeasuredYieldsNoField()
    {
        var inputs = Inputs([new(TrafficPriorSourceKind.PlayerSpawn, new(100, 100), 1, "Player spawn")]) with { UnitsPerMetre = 0 };

        Assert.False(Model.Build(inputs, RaidPhase.Early).HasField);
    }

    [Fact]
    public void SpawnsAreBusiestEarlyAndExtractsLate()
    {
        var spawn = new MapPoint(150, 500);
        var extract = new MapPoint(850, 500);
        var inputs = Inputs(
        [
            new(TrafficPriorSourceKind.PlayerSpawn, spawn, 1, "Player spawn"),
            new(TrafficPriorSourceKind.Extract, extract, 1, "Gate"),
        ]);

        var early = Model.Build(inputs, RaidPhase.Early).Field!;
        var late = Model.Build(inputs, RaidPhase.Late).Field!;

        Assert.True(early.ValueAt(spawn) > early.ValueAt(extract));
        Assert.True(late.ValueAt(extract) > late.ValueAt(spawn));
        Assert.Equal(1, early.Values.Max(), 6);
        Assert.All(early.Values, value => Assert.InRange(value, 0, 1));
    }

    [Fact]
    public void AHotspotIsNamedAfterThePlaceItIsAtAndSaysWhatDrivesIt()
    {
        var dorms = new MapPoint(500, 500);
        var loot = Enumerable.Range(0, 12)
            .Select(index => new TrafficPriorSource(TrafficPriorSourceKind.HighValueLoot, new(490 + (index * 2), 500), 0.7, "High-value loot"));
        var inputs = Inputs(
        [
            .. loot,
            new(TrafficPriorSourceKind.NamedPlace, dorms, 1, "Dorms"),
            new(TrafficPriorSourceKind.PlayerSpawn, new(100, 100), 1, "Player spawn"),
        ]);

        var prior = Model.Build(inputs, RaidPhase.Mid);

        var top = prior.Hotspots[0];
        Assert.Equal("Dorms", top.Name);
        Assert.Contains("high-value loot", top.Drivers);
        Assert.Equal(12, prior.Basis.LootSpawns);
        Assert.True(Math.Abs(top.Position.X - 500) < 40 && Math.Abs(top.Position.Y - 500) < 40);
    }

    [Fact]
    public void ThePlayersOwnRaidsAreBlendedInCountedAndCapped()
    {
        var sources = new TrafficPriorSource[] { new(TrafficPriorSourceKind.PlayerSpawn, new(100, 100), 1, "Player spawn") };
        IReadOnlyList<MapPoint> trail = [new(800, 200), new(800, 800)];
        var without = Model.Build(Inputs(sources), RaidPhase.Early);
        var with = Model.Build(Inputs(sources) with { OwnRaidTrails = [.. Enumerable.Repeat(trail, 30)] }, RaidPhase.Early);

        Assert.Equal(0, without.Field!.ValueAt(new(800, 500)), 3);
        Assert.True(with.Field!.ValueAt(new(800, 500)) > 0.1);
        Assert.Equal(30, with.Basis.OwnRaids);
        Assert.Equal(MapPriorTrafficModel.MaximumOwnRaidShare, with.Basis.OwnRaidShare, 6);
        Assert.True(with.Confidence.Value > without.Confidence.Value);
        Assert.True(with.Confidence.Value < 0.5, "A prior nobody has validated is never better than low.");
    }

    [Fact]
    public void ThePictureOfTheFieldStopsWhereThePlanDoes()
    {
        var inputs = Inputs([new(TrafficPriorSourceKind.PlayerSpawn, new(100, 100), 1, "Player spawn")]) with { Maximum = new(1000, 505) };

        var field = Model.Build(inputs, RaidPhase.Early).Field!;

        Assert.Equal(1000, field.Width, 6);
        Assert.Equal(505, field.Height, 6);
        Assert.True(field.Rows * field.Cell >= 505);
    }

    [Fact]
    public void OnlyPlayerAndBossSpawnsExtractsAndPlaceNamesAreSources()
    {
        var sources = TrafficPriorSources.FromOverlay(
        [
            new(MapOverlayKind.Spawns, new(1, 1), "Spawn · 10 points") { Faction = MapFeatureFaction.Pmc },
            new(MapOverlayKind.Spawns, new(2, 2), "Spawn") { Faction = MapFeatureFaction.Scav },
            new(MapOverlayKind.Spawns, new(3, 3), "Bot spawn · Customs"),
            new(MapOverlayKind.Spawns, new(4, 4), "Sniper spawn"),
            new(MapOverlayKind.Spawns, new(5, 5), "Boss spawn · Dormitory · 4 points"),
            new(MapOverlayKind.Extracts, new(6, 6), "ZB-1011") { Faction = MapFeatureFaction.Pmc },
            new(MapOverlayKind.Extracts, new(6.5, 6.5), "ZB-1011") { Faction = MapFeatureFaction.Scav },
            new(MapOverlayKind.Extracts, new(6.75, 6.75), "Boiler Room Basement (Co-op)"),
            new(MapOverlayKind.Extracts, new(7, 7), "Factory →"),
            new(MapOverlayKind.Labels, new(8, 8), "Main Bridge"),
            new(MapOverlayKind.Labels, new(9, 9), "Dorms"),
            new(MapOverlayKind.Keys, new(10, 10), "Marked room"),
        ]);

        Assert.Equal(
            [
                (TrafficPriorSourceKind.PlayerSpawn, 1d),
                (TrafficPriorSourceKind.PlayerSpawn, 0.5),
                (TrafficPriorSourceKind.BossArea, 1d),
                (TrafficPriorSourceKind.Extract, 1d),
                (TrafficPriorSourceKind.Extract, 0.5),
                (TrafficPriorSourceKind.Crossing, 1d),
                (TrafficPriorSourceKind.NamedPlace, 1d),
            ],
            sources.Select(source => (source.Kind, source.Weight)));
        Assert.Equal("Dormitory", sources[2].Name);
    }

    private static TrafficPriorInputs Inputs(IReadOnlyList<TrafficPriorSource> sources) =>
        new("test-map", new(0, 0), new(1000, 1000), 1, sources, [], DateTimeOffset.UnixEpoch);
}
