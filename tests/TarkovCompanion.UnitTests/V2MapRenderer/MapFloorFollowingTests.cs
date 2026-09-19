using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [V2 rough package 46] Which maps can follow somebody downstairs, and which cannot.
/// </summary>
/// <remarks>
/// Reported from a raid: "The vertical follow doesn't seem to be working, I am trying to go from
/// main floor to basement and map didn't update." The code path reads correctly, so this tests the
/// data instead: for every map that publishes more than one floor, does the reviewed extent
/// actually cover a place somebody stands underground?
///
/// The answer is not the same for every map, and that is the finding rather than a caveat:
///
/// <code>
///   map             lower floor     covers
///   factory         Tunnels         everywhere below -1 m
///   streets         Underground     everywhere below -6 m
///   the-lab         Technical       everywhere below -0.9 m
///   icebreaker      Control Room    everywhere below 3.5 m (a ship, drawn deck by deck)
///   shoreline       Underground     two rectangles: the resort's west wing and admin
///   reserve         Bunkers         five rectangles: storage, command, D2, hermetic, E1
///   ground-zero     Garage          two rectangles: the garage and the underpass
///   customs         Underground     six rectangles, and the dorms basement is NOT one of them
///   interchange     -               publishes no lower floor at all
/// </code>
///
/// So on Interchange there was never a floor to follow into, and on Customs a basement outside
/// those six rectangles resolves to the ground plan rather than to Underground. On the map Clayton
/// was actually on — Lighthouse — tarkov.dev publishes no floor layers at all, so the follow has
/// nothing to choose between and correctly does nothing.
///
/// Every coordinate below is read out of the published extent it is meant to fall in, so this
/// tests the catalog as the application parses it rather than a position this repository invented.
/// </remarks>
public sealed class MapFloorFollowingTests
{
    public static TheoryData<string, double, double, double, string> UndergroundPlaces() => new()
    {
        // map, x, y (height), z, the floor the map should move to
        { "factory", 0, -3, 0, "Tunnels" },
        { "streets-of-tarkov", 0, -10, 0, "Underground" },
        { "the-lab", -180, -3, -350, "Technical" },
        { "icebreaker", 0, 0, 0, "Control Room" },
        // Inside the resort's west wing, the first rectangle Shoreline's Underground names.
        { "shoreline", -187, -8, -86, "Underground" },
        // Inside the storage bunker, the first rectangle Reserve's Bunkers names.
        { "reserve", 73, -10, -120, "Bunkers" },
        // Inside the garage, the first rectangle Ground Zero's Garage names.
        { "ground-zero", 80, 10, 45, "Garage" },
        // Inside the boiler room, one of the six rectangles Customs' Underground names.
        { "customs", 105, -2, -50, "Underground" },
    };

    [Theory]
    [MemberData(nameof(UndergroundPlaces))]
    public void A_height_inside_a_published_underground_extent_selects_that_floor(
        string mapId,
        double x,
        double y,
        double z,
        string expectedFloor)
    {
        var variant = Variant(mapId);
        var floor = new MapPresentationService().SelectFloor(variant, new(x, y, z));

        Assert.NotNull(floor);
        Assert.Equal(expectedFloor, floor!.Name);
    }

    [Fact]
    public void Customs_dorms_basement_is_not_in_any_published_underground_rectangle()
    {
        // The gap, named. Customs' Underground names zb-1011, zb-1012, old gas, the switch
        // basement, zb-013 and the boiler room — not the dorms, whose own rectangle the catalog
        // publishes for the 2nd and 3rd floors. Standing under dorms therefore resolves to the
        // ground plan. The code was never going to work there; the extents are the bug.
        var variant = Variant("customs");
        var dorms = variant.Floors
            .Single(floor => floor.Name == "2nd Floor").Extents
            .SelectMany(extent => extent.Bounds)
            .First(bounds => bounds.Description == "dorms");
        var middleX = (dorms.First.X + dorms.Second.X) / 2;
        var middleZ = (dorms.First.Y + dorms.Second.Y) / 2;

        var underground = variant.Floors.Single(floor => floor.Name == "Underground");
        Assert.DoesNotContain(
            underground.Extents,
            extent => extent.Contains(new(middleX, -2, middleZ)));

        var floor = new MapPresentationService().SelectFloor(variant, new(middleX, -2, middleZ));
        Assert.Equal("Base", floor!.Name);
    }

    [Fact]
    public void Interchange_publishes_no_floor_below_the_ground_plan()
    {
        // Two floors, both bounded to the mall and both above it. Nothing to follow down into.
        var variant = Variant("interchange");
        Assert.Equal(["Base", "2nd Floor", "3rd Floor"], variant.Floors.Select(floor => floor.Name));

        var floor = new MapPresentationService().SelectFloor(variant, new(0, -20, 0));
        Assert.Equal("Base", floor!.Name);
    }

    [Fact]
    public void The_map_Clayton_was_on_publishes_no_floors_at_all()
    {
        // Lighthouse, Terminal, Woods and The Labyrinth are drawn as one storey upstream, so the
        // follow has a single rung and correctly leaves the map alone. Worth a test because "the
        // map did not change floor" and "this map has no floors" look identical on screen.
        foreach (var mapId in new[] { "lighthouse", "terminal", "woods", "the-labyrinth" })
        {
            Assert.Single(Variant(mapId).Floors);
        }

        // One rung is the guard FollowFloor takes before it consults anything, so such a map is
        // "not following" and says nothing — the ladder is not even drawn.
        Assert.Equal(string.Empty, MapViewModel.DescribeFloorSource(following: false, hasPosition: true, matched: null));
    }

    [Fact]
    public void A_height_that_matches_no_floor_says_so_rather_than_moving_the_map()
    {
        // The words package 39 wrote, now shown where the floors are chosen. A map with a base
        // floor always matches something, so this is the sentence for the floor ladder of a map
        // whose reviewed heights genuinely leave a gap.
        Assert.Equal(
            "Your height matches no floor here — pick the floor yourself",
            MapViewModel.DescribeFloorSource(following: true, hasPosition: true, matched: null));
        Assert.Equal(
            "No screenshot yet — pick the floor yourself",
            MapViewModel.DescribeFloorSource(following: true, hasPosition: false, matched: null));
        Assert.Equal(
            string.Empty,
            MapViewModel.DescribeFloorSource(following: false, hasPosition: true, matched: null));
    }

    [Fact]
    public void The_floor_ladder_carries_that_sentence()
    {
        // It used to live only in the status line at the other end of the card.
        var view = File.ReadAllText(RepositoryFile(
            "src/TarkovCompanion.App/Views/V2/MapRenderer/MapSceneRendererView.axaml"));
        Assert.Contains("v2-map-floor-source", view, StringComparison.Ordinal);
        Assert.Contains("{Binding FloorSourceNote}", view, StringComparison.Ordinal);
    }

    private static MapVariant Variant(string mapId)
    {
        var catalog = RealMapCatalog.Load();
        return catalog.Variant(catalog.Locations.Single(location => location.Id == mapId));
    }

    private static string RepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TarkovCompanion.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory.FullName, relativePath);
        Assert.True(File.Exists(path), $"{relativePath} is not where this test expects it.");
        return path;
    }
}
