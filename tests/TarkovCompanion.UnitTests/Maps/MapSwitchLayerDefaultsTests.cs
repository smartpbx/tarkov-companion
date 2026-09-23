using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests.Maps;

public sealed class MapSwitchLayerDefaultsTests
{
    [Theory]
    [InlineData("the-lab", true)]
    [InlineData("reserve", true)]
    [InlineData("interchange", true)]
    [InlineData("customs", false)]
    public void Switches_are_quietly_on_only_on_power_heavy_maps(string mapId, bool expected)
    {
        var location = new MapLocation(mapId, null, mapId, null, null, []);
        var variant = new MapVariant(
            mapId,
            "plan",
            MapProjectionKind.TwoDimensional,
            "2D",
            null,
            null,
            new("https://example.test/map.svg"),
            null,
            256,
            null,
            null,
            new(new(0, 0), new(100, 100)),
            new(new(0, 0), new(100, 100)),
            new(1, 0, 1, 0, 0),
            null,
            null,
            null,
            "Example",
            new("https://example.test"),
            [],
            [],
            []);

        var model = new MapPresentationService().Create(location, variant);

        Assert.Equal(expected, model.Overlays.Single(item => item.Kind == MapOverlayKind.Switches).IsVisible);
    }
}
