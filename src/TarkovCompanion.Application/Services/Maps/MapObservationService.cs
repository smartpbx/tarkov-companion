using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

public enum PositionFreshness
{
    Fresh,
    Stale,
    FutureTimestamp,
    MissingTransform,
    InvalidTransform,
}

public sealed record MapPositionObservation(
    ScreenshotPosition Position,
    MapPoint? MapPoint,
    MapFloorLayer? Floor,
    PositionFreshness Freshness,
    TimeSpan Age,
    string Guidance)
{
    public bool CanPlot => MapPoint is not null && Freshness is PositionFreshness.Fresh or PositionFreshness.Stale;
}

public sealed class MapObservationService(
    IScreenshotFilenameParser filenameParser,
    IMapTransformService transformService,
    TimeSpan? maximumFreshAge = null)
{
    private readonly TimeSpan _maximumFreshAge = maximumFreshAge ?? TimeSpan.FromMinutes(2);

    public bool TryObserve(
        string filename,
        TimeSpan localUtcOffset,
        MapDefinition map,
        DateTimeOffset nowUtc,
        out MapPositionObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(map);
        observation = null;
        if (!filenameParser.TryParse(filename, localUtcOffset, out var position) || position is null)
        {
            return false;
        }

        var age = nowUtc.ToUniversalTime() - position.Timestamp.ToUniversalTime();
        if (age < TimeSpan.FromSeconds(-30))
        {
            observation = new(position, null, null, PositionFreshness.FutureTimestamp, age, "Screenshot time is in the future; position was not plotted.");
            return true;
        }

        if (map.Transform is null)
        {
            observation = new(position, null, null, PositionFreshness.MissingTransform, age, "This map has no validated world-to-map transform; no position is guessed.");
            return true;
        }

        var validation = MapTransformValidator.Validate(map.Transform, map.Floors);
        if (!validation.IsValid)
        {
            observation = new(position, null, null, PositionFreshness.InvalidTransform, age, validation.Error!);
            return true;
        }

        var freshness = age > _maximumFreshAge ? PositionFreshness.Stale : PositionFreshness.Fresh;
        var guidance = freshness == PositionFreshness.Fresh
            ? "Last-known screenshot position."
            : $"Last-known screenshot position is stale ({age:g} old).";
        observation = new(
            position,
            transformService.Transform(position.Position, map.Transform),
            transformService.SelectFloor(position.Position.Y, map.Floors),
            freshness,
            age,
            guidance);
        return true;
    }
}

public sealed record TransformValidationResult(bool IsValid, string? Error)
{
    public static TransformValidationResult Valid { get; } = new(true, null);

    public static TransformValidationResult Invalid(string error) => new(false, error);
}

public static class MapTransformValidator
{
    public static TransformValidationResult Validate(MapTransformConfig config, IReadOnlyList<MapFloorLayer> floors)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(floors);
        var values = new[]
        {
            config.WorldMinX, config.WorldMaxX, config.WorldMinZ, config.WorldMaxZ,
            config.VisualWidth, config.VisualHeight, config.RotationDegrees,
        };
        if (values.Any(value => !double.IsFinite(value)))
        {
            return TransformValidationResult.Invalid("Map transform contains a non-finite value; no position is plotted.");
        }

        if (config.WorldMaxX <= config.WorldMinX || config.WorldMaxZ <= config.WorldMinZ
            || config.VisualWidth <= 0 || config.VisualHeight <= 0)
        {
            return TransformValidationResult.Invalid("Map transform bounds must have positive world and visual extents; no position is plotted.");
        }

        var orderedFloors = floors.OrderBy(floor => floor.MinHeight).ToArray();
        if (orderedFloors.Any(floor => !double.IsFinite(floor.MinHeight)
                || !double.IsFinite(floor.MaxHeight)
                || floor.MaxHeight <= floor.MinHeight))
        {
            return TransformValidationResult.Invalid("A floor has invalid elevation bounds; no position is plotted.");
        }

        for (var index = 1; index < orderedFloors.Length; index++)
        {
            if (orderedFloors[index].MinHeight < orderedFloors[index - 1].MaxHeight)
            {
                return TransformValidationResult.Invalid("Map floor elevation ranges overlap; no position is plotted.");
            }
        }

        return TransformValidationResult.Valid;
    }
}
