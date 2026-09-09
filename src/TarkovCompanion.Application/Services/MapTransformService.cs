using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services;

public sealed class MapTransformService : IMapTransformService
{
    public MapPoint Transform(WorldPosition position, MapTransformConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var width = config.WorldMaxX - config.WorldMinX;
        var height = config.WorldMaxZ - config.WorldMinZ;
        if (width <= 0 || height <= 0 || config.VisualWidth <= 0 || config.VisualHeight <= 0)
        {
            throw new ArgumentException("Map transform bounds must have positive extents.", nameof(config));
        }

        var normalizedX = (position.X - config.WorldMinX) / width;
        var normalizedY = (position.Z - config.WorldMinZ) / height;
        if (config.FlipX)
        {
            normalizedX = 1 - normalizedX;
        }

        if (config.FlipY)
        {
            normalizedY = 1 - normalizedY;
        }

        var radians = config.RotationDegrees * (Math.PI / 180);
        var centeredX = normalizedX - 0.5;
        var centeredY = normalizedY - 0.5;
        var rotatedX = (centeredX * Math.Cos(radians)) - (centeredY * Math.Sin(radians)) + 0.5;
        var rotatedY = (centeredX * Math.Sin(radians)) + (centeredY * Math.Cos(radians)) + 0.5;

        return new(rotatedX * config.VisualWidth, rotatedY * config.VisualHeight);
    }

    public MapFloorLayer? SelectFloor(double elevation, IReadOnlyList<MapFloorLayer> floors) =>
        floors.FirstOrDefault(x => elevation >= x.MinHeight && elevation < x.MaxHeight);
}
