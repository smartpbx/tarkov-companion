using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Core.Domain.Maps;

public readonly record struct WorldPosition(double X, double Y, double Z);

public readonly record struct MapPoint(double X, double Y);

public readonly record struct QuaternionOrientation(double X, double Y, double Z, double W)
{
    public QuaternionOrientation Normalize()
    {
        var magnitude = Math.Sqrt((X * X) + (Y * Y) + (Z * Z) + (W * W));
        if (magnitude < 1e-12)
        {
            throw new InvalidOperationException("A zero quaternion has no orientation.");
        }

        return new(X / magnitude, Y / magnitude, Z / magnitude, W / magnitude);
    }
}

public sealed record ScreenshotPosition(
    DateTimeOffset Timestamp,
    WorldPosition Position,
    QuaternionOrientation Orientation,
    double HeadingDegrees,
    TimeSpan? InGameTime,
    int? DuplicateIndex,
    string Filename);

public sealed record MapTransformConfig(
    string MapId,
    double WorldMinX,
    double WorldMaxX,
    double WorldMinZ,
    double WorldMaxZ,
    double VisualWidth,
    double VisualHeight,
    double RotationDegrees,
    bool FlipX,
    bool FlipY,
    DataProvenance Provenance);

public sealed record MapFloorLayer(string Id, string Name, double MinHeight, double MaxHeight, string? AssetReference);

public sealed record MapExtract(
    string Id,
    string MapId,
    string Name,
    MapPoint? Position,
    string? Conditions,
    DataProvenance Provenance);

public enum ExtractStatus
{
    Active,
    Closed,
    Pending,
    Unknown,
}

public sealed record ObservedExtract(
    string ExtractId,
    string Name,
    ExtractStatus Status,
    Confidence Confidence,
    string Source,
    DateTimeOffset ObservedUtc);

public sealed record ActiveExtract(string ExtractId, string Name, Confidence Confidence, string Source);

public sealed record MapDefinition(
    string Id,
    string Name,
    TimeSpan? PmcRaidDuration,
    TimeSpan? ScavRaidDuration,
    IReadOnlyList<MapFloorLayer> Floors,
    IReadOnlyList<MapExtract> Extracts,
    MapTransformConfig? Transform,
    DataProvenance Provenance);
