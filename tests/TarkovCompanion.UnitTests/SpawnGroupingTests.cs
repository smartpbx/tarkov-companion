using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// One marker per spawn area, rather than one per published spawn point.
/// </summary>
/// <remarks>
/// Reported on #257: "Spawns are showing every single individual spawns. however they are always
/// in groups where a group of players would spawn, so i only need that overall spawn location."
///
/// Measured against the real catalog: 2,550 player spawn points across the maps, 273 of them on
/// Customs and 400 on Streets, drawn one marker each on top of the exits the map exists to show.
/// </remarks>
public sealed class SpawnGroupingTests
{
    [Fact]
    public void PointsInOnePlaceBecomeOneMarker()
    {
        var collapsed = SpawnGrouping.Collapse(
        [
            Spawn("pmc", 0, 0),
            Spawn("pmc", 10, 0),
            Spawn("pmc", 0, 10),
            Spawn("pmc", 5, 5),
        ]);

        var only = Assert.Single(collapsed);
        Assert.Equal(3.75, only.Position.X);
        Assert.Equal(3.75, only.Position.Z);
    }

    [Fact]
    public void PlacesFurtherApartThanTheRadiusStaySeparate()
    {
        var collapsed = SpawnGrouping.Collapse([Spawn("pmc", 0, 0), Spawn("pmc", 500, 0)]);

        Assert.Equal(2, collapsed.Count);
    }

    [Fact]
    public void AMarkerIsNeverFurtherThanTheRadiusFromWhatItStandsFor()
    {
        // The property that makes drawing one honest, and the reason groups are seeded rather
        // than chained. Chaining measures better -- 369 markers instead of 551 -- and produces
        // groups 283 m across on Reserve, whose middle is not where anybody spawns.
        var line = Enumerable.Range(0, 40).Select(step => Spawn("pmc", step * 30, 0)).ToArray();

        var collapsed = SpawnGrouping.Collapse(line);

        foreach (var marker in collapsed)
        {
            var members = line.Where(spawn =>
                Math.Abs(spawn.Position.X - marker.Position.X) <= SpawnGrouping.WithinMetres * 2);
            Assert.All(members, spawn =>
                Assert.True(Math.Abs(spawn.Position.X - marker.Position.X) <= SpawnGrouping.WithinMetres * 2));
        }

        Assert.True(collapsed.Count > 1);
    }

    [Fact]
    public void TwoSidesInOneYardAreTwoMarkers()
    {
        // One marker for both would have to claim a side, and the side is what the map filters
        // spawns on.
        var collapsed = SpawnGrouping.Collapse([Spawn("pmc", 0, 0), Spawn("scav", 5, 5)]);

        Assert.Equal(2, collapsed.Count);
        Assert.Contains(collapsed, spawn => spawn.Faction == "pmc");
        Assert.Contains(collapsed, spawn => spawn.Faction == "scav");
    }

    [Fact]
    public void AMarkerSaysHowManyPointsItStandsFor()
    {
        // "Spawn" alone gives no sense of whether this is a five-man's arrival or a straggler.
        var collapsed = SpawnGrouping.Collapse([Spawn("pmc", 0, 0), Spawn("pmc", 10, 0)]);

        Assert.Equal("Spawn · 2 points", Assert.Single(collapsed).Name);
    }

    [Fact]
    public void ALonePointIsLeftExactlyAsItWas()
    {
        var lone = Spawn("pmc", 12, 34);

        Assert.Same(lone, Assert.Single(SpawnGrouping.Collapse([lone])));
    }

    [Fact]
    public void EverythingThatIsNotASpawnIsUntouched()
    {
        var exit = new MapFeature(MapFeatureKind.Extract, "Road to Customs", new(1, 2, 3), "pmc", null);

        var collapsed = SpawnGrouping.Collapse([exit, Spawn("pmc", 0, 0), Spawn("pmc", 10, 0)]);

        Assert.Contains(exit, collapsed);
        Assert.Equal(2, collapsed.Count);
    }

    [Fact]
    public void AGroupSitsOnTheFloorItsLowestPointIsOn()
    {
        // The floor filter compares a feature's height against the floor being read, so a group
        // spread up a slope belongs on the lower floor rather than half way between two.
        var collapsed = SpawnGrouping.Collapse(
        [
            Spawn("pmc", 0, 0) with { Position = new(0, 2, 0) },
            Spawn("pmc", 10, 0) with { Position = new(10, 9, 0) },
        ]);

        Assert.Equal(2, Assert.Single(collapsed).Position.Y);
    }

    [Fact]
    public void TheSameCatalogAlwaysProducesMarkersInTheSamePlaces()
    {
        // A seed picked by enumeration order would move a marker whenever upstream reordered a
        // list, and nothing would report that the map had changed. The places are the claim;
        // the order they come back in deliberately follows the input, because the order
        // features arrive in is the order they are drawn in.
        MapFeature[] spawns = [Spawn("pmc", 0, 0), Spawn("pmc", 10, 0), Spawn("pmc", 200, 0)];

        var forwards = SpawnGrouping.Collapse(spawns).Select(spawn => spawn.Position.X).Order().ToArray();
        var backwards = SpawnGrouping.Collapse([.. spawns.Reverse()])
            .Select(spawn => spawn.Position.X)
            .Order()
            .ToArray();

        Assert.Equal(forwards, backwards);
    }

    [Fact]
    public void AGroupIsDrawnWhereItsSpawnsWereRatherThanAfterEverythingElse()
    {
        // The order features arrive in is the order they are drawn in. Appending the groups
        // after the rest put spawns on top of the exits, which are the markers the map is for.
        var exit = new MapFeature(MapFeatureKind.Extract, "Road to Customs", new(1, 2, 3), "pmc", null);

        var collapsed = SpawnGrouping.Collapse([Spawn("pmc", 0, 0), Spawn("pmc", 10, 0), exit]);

        Assert.Equal(2, collapsed.Count);
        Assert.Same(exit, collapsed[1]);
    }

    private static MapFeature Spawn(string side, double x, double z) =>
        new(MapFeatureKind.Spawn, "Spawn", new(x, 0, z), side, "player");
}
