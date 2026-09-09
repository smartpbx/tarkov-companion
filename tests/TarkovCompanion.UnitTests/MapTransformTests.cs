using TarkovCompanion.Application.Services;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

public sealed class MapTransformTests
{
    [Fact]
    public void TransformsWorldXZIntoVisualPlane()
    {
        var service = new MapTransformService();
        var config = new MapTransformConfig(
            "fixture",
            -100,
            100,
            -50,
            50,
            1000,
            500,
            0,
            false,
            true,
            new DataProvenance("fixture", DateTimeOffset.UnixEpoch));

        var result = service.Transform(new WorldPosition(0, 12, -50), config);

        Assert.Equal(500, result.X, 6);
        Assert.Equal(500, result.Y, 6);
    }

    [Fact]
    public void SelectsFloorByElevationAndHonorsExclusiveUpperBound()
    {
        var floors = new[]
        {
            new MapFloorLayer("lower", "Lower", -10, 0, null),
            new MapFloorLayer("upper", "Upper", 0, 10, null),
        };

        var result = new MapTransformService().SelectFloor(0, floors);

        Assert.Equal("upper", result?.Id);
    }
}
