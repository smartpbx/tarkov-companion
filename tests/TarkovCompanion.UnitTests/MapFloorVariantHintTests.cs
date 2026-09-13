using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Telling somebody where the floors went.
/// </summary>
/// <remarks>
/// Reported as "the 3d view doesnt seem to work at all for me". Part of that was a missing style
/// on the toggle and part was a notification that never fired; both are fixed. This is the part
/// that was left.
///
/// Every map upstream publishes carries its layers on one variant only — the interactive one,
/// which is also the only one with a transform. Customs publishes four variants and exactly one
/// of them has floors. So a player who switches artwork loses the stack, the toggle correctly
/// disappears, and nothing anywhere says why or how to get it back. A feature that vanishes
/// silently is indistinguishable from one that is broken, which is how it was reported.
/// </remarks>
public sealed class MapFloorVariantHintTests
{
    [Fact]
    public void The_variant_with_floors_is_named_when_the_one_on_screen_has_none()
    {
        var found = MapViewModel.FloorsElsewhere(Customs(), selectedKey: "customs-2d");

        Assert.NotNull(found);
        Assert.Equal("customs", found.Key);
    }

    [Fact]
    public void The_variant_already_showing_floors_does_not_point_at_itself()
    {
        Assert.Null(MapViewModel.FloorsElsewhere(Customs(), selectedKey: "customs"));
    }

    [Fact]
    public void A_map_where_nothing_has_floors_says_nothing()
    {
        // Factory is genuinely one floor. A line about missing floors on every flat map would
        // be noise, and noise on a map is worse than silence.
        var flat = Customs() with
        {
            Variants = [Variant("factory", floors: 0), Variant("factory-2d", floors: 0, interactive: false)],
        };

        Assert.Null(MapViewModel.FloorsElsewhere(flat, selectedKey: "factory-2d"));
    }

    [Fact]
    public void A_single_floor_is_not_floors()
    {
        // CanStack is two or more for the same reason: one floor stacked on nothing is the flat
        // map with extra steps.
        var one = Customs() with { Variants = [Variant("customs", floors: 1), Variant("customs-2d", floors: 0, interactive: false)] };

        Assert.Null(MapViewModel.FloorsElsewhere(one, selectedKey: "customs-2d"));
    }

    [Fact]
    public void A_variant_with_no_artwork_is_not_offered()
    {
        // Switching to it would replace a map somebody can see with one that cannot be drawn,
        // which is a worse answer than the missing toggle.
        var unreachable = Customs() with
        {
            Variants = [Variant("customs", floors: 4, hasArtwork: false), Variant("customs-2d", floors: 0, interactive: false)],
        };

        Assert.Null(MapViewModel.FloorsElsewhere(unreachable, selectedKey: "customs-2d"));
    }

    [Fact]
    public void The_interactive_one_wins_even_when_another_has_more_floors()
    {
        // It is the one that also carries a transform, so it is the one that can place the
        // player on the floor it is showing them. More floors on a map that cannot say where
        // you are is not the better answer.
        var both = Customs() with
        {
            Variants =
            [
                Variant("customs", floors: 4),
                Variant("customs-3d", floors: 9, interactive: false),
                Variant("customs-2d", floors: 0, interactive: false),
            ],
        };

        Assert.Equal("customs", MapViewModel.FloorsElsewhere(both, selectedKey: "customs-2d")?.Key);
    }

    [Fact]
    public void No_map_open_is_not_a_failure()
    {
        Assert.Null(MapViewModel.FloorsElsewhere(null, selectedKey: null));
    }

    private static MapLocation Customs() => new(
        Id: "customs",
        SourceId: "customs",
        Name: "Customs",
        Description: null,
        PrimaryPath: null,
        Variants:
        [
            Variant("customs", floors: 4),
            Variant("customs-2d", floors: 0, interactive: false),
            Variant("customs-3d", floors: 0, interactive: false),
        ]);

    private static MapVariant Variant(string key, int floors, bool interactive = true, bool hasArtwork = true) => new(
        LocationId: "customs",
        Key: key,
        Projection: interactive ? MapProjectionKind.Interactive : MapProjectionKind.TwoDimensional,
        ProjectionName: interactive ? "Interactive" : "2D",
        Orientation: null,
        Specific: null,
        SvgPath: hasArtwork ? new Uri("https://example.invalid/customs.svg") : null,
        TilePath: null,
        TileSize: 256,
        MinimumZoom: 0,
        MaximumZoom: 4,
        Bounds: null,
        SvgBounds: null,
        Transform: null,
        SvgLayer: null,
        MinimumHeight: null,
        MaximumHeight: null,
        Author: "tarkov.dev",
        AuthorLink: null,
        AlternateLocationIds: [],
        Floors: [.. Enumerable.Range(1, floors).Select(index => new MapFloorDefinition($"floor-{index}", $"Floor {index}", null, null, true, []))],
        Labels: []);
}
