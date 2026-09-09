using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

public enum MapProjectionKind
{
    Unknown,
    Interactive,
    TwoDimensional,
    ThreeDimensional,
}

public enum MapCatalogAvailability
{
    Current,
    Cached,
    OfflineCached,
    Unavailable,
}

public sealed record MapCatalogProvenance(
    Uri SourceUri,
    DateTimeOffset RetrievedUtc,
    string ContentSha256,
    MapCatalogAvailability Availability);

public readonly record struct MapCatalogPoint(double X, double Y);

public sealed record MapCatalogBounds(MapCatalogPoint First, MapCatalogPoint Second)
{
    public string? Description { get; init; }

    public bool IsValid =>
        IsFinite(First.X) &&
        IsFinite(First.Y) &&
        IsFinite(Second.X) &&
        IsFinite(Second.Y) &&
        First != Second;

    public bool Contains(double x, double z)
    {
        var minX = Math.Min(First.X, Second.X);
        var maxX = Math.Max(First.X, Second.X);
        var minZ = Math.Min(First.Y, Second.Y);
        var maxZ = Math.Max(First.Y, Second.Y);
        return x >= minX && x <= maxX && z >= minZ && z <= maxZ;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>
/// The four-value Leaflet transformation and rotation published in tarkov.dev's maps.json.
/// World X/Z is rotated about the origin, then scaled and offset; Leaflet's Y axis is inverted.
/// </summary>
public sealed record MapCatalogTransform(
    double ScaleX,
    double OffsetX,
    double ScaleY,
    double OffsetY,
    double RotationDegrees)
{
    public bool IsValid =>
        IsFinite(ScaleX) && ScaleX != 0 &&
        IsFinite(OffsetX) &&
        IsFinite(ScaleY) && ScaleY != 0 &&
        IsFinite(OffsetY) &&
        IsFinite(RotationDegrees);

    public bool TryProject(WorldPosition position, out MapPoint point)
    {
        point = default;
        if (!IsValid || !IsFinite(position.X) || !IsFinite(position.Z))
        {
            return false;
        }

        var radians = RotationDegrees * Math.PI / 180d;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        var rotatedX = (position.X * cosine) - (position.Z * sine);
        var rotatedZ = (position.X * sine) + (position.Z * cosine);
        point = new(
            (ScaleX * rotatedX) + OffsetX,
            (-ScaleY * rotatedZ) + OffsetY);
        return true;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed record MapLayerExtent(
    double? MinimumHeight,
    double? MaximumHeight,
    IReadOnlyList<MapCatalogBounds> Bounds)
{
    public bool Contains(WorldPosition position)
    {
        var inHeight =
            (MinimumHeight is null || position.Y >= MinimumHeight) &&
            (MaximumHeight is null || position.Y < MaximumHeight);
        return inHeight && (Bounds.Count == 0 || Bounds.Any(bounds => bounds.Contains(position.X, position.Z)));
    }
}

public sealed record MapFloorDefinition(
    string Id,
    string Name,
    string? SvgLayer,
    Uri? TilePath,
    bool IsVisibleByDefault,
    IReadOnlyList<MapLayerExtent> Extents);

public sealed record MapCatalogLabel(
    string Text,
    MapCatalogPoint Position,
    double RotationDegrees,
    double SizePercent,
    double? MinimumHeight,
    double? MaximumHeight);

public sealed record MapVariant(
    string LocationId,
    string Key,
    MapProjectionKind Projection,
    string ProjectionName,
    string? Orientation,
    string? Specific,
    Uri? SvgPath,
    Uri? TilePath,
    int TileSize,
    int? MinimumZoom,
    int? MaximumZoom,
    MapCatalogBounds? Bounds,
    MapCatalogBounds? SvgBounds,
    MapCatalogTransform? Transform,
    string? SvgLayer,
    double? MinimumHeight,
    double? MaximumHeight,
    string? Author,
    Uri? AuthorLink,
    IReadOnlyList<string> AlternateLocationIds,
    IReadOnlyList<MapFloorDefinition> Floors,
    IReadOnlyList<MapCatalogLabel> Labels)
{
    public bool HasRuntimeAsset => SvgPath is not null || TilePath is not null;

    public bool IsInteractive => Projection == MapProjectionKind.Interactive;

    public string DisplayName => string.Join(
        " · ",
        new[] { ProjectionName, Orientation, Specific }.Where(value => !string.IsNullOrWhiteSpace(value))!);
}

public sealed record MapLocation(
    string Id,
    string? SourceId,
    string Name,
    string? Description,
    string? PrimaryPath,
    IReadOnlyList<MapVariant> Variants);

public sealed record TarkovDevMapCatalog(
    IReadOnlyList<MapLocation> Locations,
    MapCatalogProvenance Provenance)
{
    public MapLocation? FindLocation(string locationId) =>
        Locations.FirstOrDefault(location => string.Equals(location.Id, locationId, StringComparison.OrdinalIgnoreCase));
}

public sealed record MapCatalogLoadResult(
    TarkovDevMapCatalog? Catalog,
    MapCatalogAvailability Availability,
    string? Message)
{
    public bool IsAvailable => Catalog is not null;
}
