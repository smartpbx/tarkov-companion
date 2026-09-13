using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Arranging a map's floors as a stack, in the order they are in the building.
/// </summary>
public sealed class FloorStackTests
{
    [Fact]
    public void Stacks_the_floors_by_height_rather_than_by_listing_order()
    {
        // A catalog that lists "Underground" last would otherwise stack it on the roof. This
        // is the real order Streets publishes them in.
        var floors = new[]
        {
            Floor("second", "2nd Floor", 10, 15),
            Floor("third", "3rd Floor", 15, 20),
            Floor("underground", "Underground", -10000, -6),
            Floor("fifth", "5th Floor", 25, 10000),
        };

        var stacked = FloorStack.Arrange(floors, null);

        Assert.Equal(
            ["Underground", "2nd Floor", "3rd Floor", "5th Floor"],
            stacked.Select(placement => placement.Floor.Name));
        Assert.Equal(0, stacked[0].Offset);
        Assert.Equal(FloorStack.Separation, stacked[1].Offset);
        Assert.Equal(FloorStack.Separation * 3, stacked[3].Offset);
    }

    [Fact]
    public void Ignores_the_sentinel_bounds_the_catalog_uses_for_everything_below_and_above()
    {
        // "Underground" is published as -10000..-6 and "5th Floor" as 25..10000. Taking those
        // literally would drop one far below the rest and leave the real floors bunched.
        Assert.Equal(-6, FloorStack.Elevation(Floor("underground", "Underground", -10000, -6)));
        Assert.Equal(25, FloorStack.Elevation(Floor("fifth", "5th Floor", 25, 10000)));
    }

    [Fact]
    public void A_floor_nobody_could_measure_goes_beneath_everything_that_was_measured()
    {
        var floors = new[]
        {
            Floor("second", "2nd Floor", 10, 15),
            new MapFloorDefinition("plan", "Plan", null, null, false, []),
        };

        var stacked = FloorStack.Arrange(floors, null);

        Assert.Equal("Plan", stacked[0].Floor.Name);
        Assert.Null(FloorStack.Elevation(floors[1]));
    }

    [Fact]
    public void The_floor_being_read_is_solid_and_the_rest_are_context()
    {
        var floors = new[] { Floor("second", "2nd Floor", 10, 15), Floor("third", "3rd Floor", 15, 20) };

        var stacked = FloorStack.Arrange(floors, floors[1]);

        Assert.False(stacked[0].IsSelected);
        Assert.True(stacked[1].IsSelected);
        Assert.Equal(1, stacked[1].Opacity);
        Assert.True(stacked[0].Opacity is > 0 and < 1, "a floor that is not being read is still worth seeing");
    }

    [Fact]
    public void The_markers_rise_with_the_floor_they_belong_to()
    {
        // Without this they would stay on the lowest plane while the map they describe rose
        // above them, which is a marker pointing at the wrong floor.
        var floors = new[]
        {
            Floor("second", "2nd Floor", 10, 15),
            Floor("third", "3rd Floor", 15, 20),
            Floor("fourth", "4th Floor", 20, 25),
        };
        var stacked = FloorStack.Arrange(floors, floors[2]);

        Assert.Equal(FloorStack.Separation * 2, FloorStack.OffsetOf(stacked, floors[2]));
        Assert.Equal(0, FloorStack.OffsetOf(stacked, floors[0]));
    }

    [Fact]
    public void Nothing_selected_sits_on_the_ground()
    {
        var floors = new[] { Floor("second", "2nd Floor", 10, 15) };

        Assert.Equal(0, FloorStack.OffsetOf(FloorStack.Arrange(floors, null), null));
        Assert.Equal(0, FloorStack.OffsetOf([], floors[0]));
    }

    [Fact]
    public void A_map_with_no_floors_stacks_nothing()
    {
        Assert.Empty(FloorStack.Arrange([], null));
    }

    [Fact]
    public void A_floor_whose_id_differs_only_in_case_is_still_that_floor()
    {
        var floors = new[] { Floor("Second", "2nd Floor", 10, 15) };
        var stacked = FloorStack.Arrange(floors, Floor("second", "2nd Floor", 10, 15));

        Assert.True(stacked[0].IsSelected);
    }

    private static MapFloorDefinition Floor(string id, string name, double minimum, double maximum) =>
        new(id, name, name.Replace(' ', '_'), null, false, [new(minimum, maximum, [])]);
}
