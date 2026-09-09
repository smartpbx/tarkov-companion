using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

public enum MapBackgroundKind
{
    Svg,
    TileTemplate,
}

public enum MapAssetAvailability
{
    Available,
    CachedOffline,
    Unavailable,
}

public enum MapTransformAvailability
{
    Valid,
    Unavailable,
    Invalid,
}

public enum MapOverlayKind
{
    CompanionMarkers,
    Extracts,
    Labels,
    Routes,
    RiskAndTraffic,
    Filters,
}

public sealed record MapBackground(
    MapBackgroundKind Kind,
    Uri SourceUri,
    string? CachedPath,
    MapAssetAvailability Availability,
    string? Message);

public sealed record MapOverlayLayer(MapOverlayKind Kind, string Name, bool IsVisible, bool IsHighlighted);

public sealed record MapOverlayElement(
    MapOverlayKind Layer,
    MapPoint Position,
    string Label,
    double RotationDegrees = 0,
    double SizePercent = 100);

public sealed record MapRenderModel(
    MapLocation Location,
    MapVariant Variant,
    MapBackground? Background,
    MapTransformAvailability TransformAvailability,
    string TransformMessage,
    IReadOnlyList<MapOverlayLayer> Overlays,
    IReadOnlyList<MapOverlayElement> OverlayElements,
    IReadOnlyList<MapFloorDefinition> Floors,
    MapFloorDefinition? SelectedFloor,
    string AttributionText,
    Uri LicenseUri)
{
    public bool CanRender => Background is not null && Background.Availability != MapAssetAvailability.Unavailable;

    public IReadOnlyList<MapOverlayElement> VisibleOverlayElements => CanRender
        ? OverlayElements
            .Where(element => Overlays.Any(layer => layer.Kind == element.Layer && layer.IsVisible))
            .ToArray()
        : [];

    public MapRenderModel SetLayerVisibility(MapOverlayKind kind, bool isVisible) =>
        this with
        {
            Overlays = Overlays
                .Select(layer => layer.Kind == kind ? layer with { IsVisible = isVisible } : layer)
                .ToArray(),
        };

    public MapRenderModel HighlightLayer(MapOverlayKind? kind) =>
        this with
        {
            Overlays = Overlays
                .Select(layer => layer with { IsHighlighted = layer.Kind == kind })
                .ToArray(),
        };

    public MapRenderModel SelectFloor(string? floorId) =>
        this with
        {
            SelectedFloor = Floors.FirstOrDefault(floor =>
                string.Equals(floor.Id, floorId, StringComparison.OrdinalIgnoreCase)),
        };

    public bool TryMapPosition(WorldPosition position, out MapPoint point)
    {
        point = default;
        return TransformAvailability == MapTransformAvailability.Valid &&
            Variant.Transform is not null &&
            Variant.Transform.TryProject(position, out point);
    }
}

public sealed class MapPresentationService
{
    public static readonly Uri LicenseUri = new("https://creativecommons.org/licenses/by-nc-sa/4.0/");

    public MapRenderModel Create(
        MapLocation location,
        MapVariant variant,
        string? cachedAssetPath = null,
        MapAssetAvailability assetAvailability = MapAssetAvailability.Available,
        string? assetMessage = null,
        IEnumerable<MapOverlayElement>? companionElements = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(variant);
        if (!string.Equals(location.Id, variant.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The map variant does not belong to the requested location.", nameof(variant));
        }

        var background = CreateBackground(variant, cachedAssetPath, assetAvailability, assetMessage);
        var transformAvailability = variant.Transform switch
        {
            null => MapTransformAvailability.Unavailable,
            { IsValid: true } when variant.Bounds?.IsValid == true => MapTransformAvailability.Valid,
            _ => MapTransformAvailability.Invalid,
        };
        var transformMessage = transformAvailability switch
        {
            MapTransformAvailability.Valid => "Validated tarkov.dev transform.",
            MapTransformAvailability.Unavailable => "No upstream transform is available; position markers are hidden.",
            _ => "The upstream transform is invalid; position markers are hidden.",
        };

        var floors = variant.Floors;
        var selectedFloor = floors.FirstOrDefault(floor => floor.IsVisibleByDefault) ?? floors.FirstOrDefault();
        var author = string.IsNullOrWhiteSpace(variant.Author) ? "tarkov.dev contributors" : variant.Author;
        var overlayElements = CreateLabelElements(variant)
            .Concat(companionElements ?? [])
            .ToArray();
        return new(
            location,
            variant,
            background,
            transformAvailability,
            transformMessage,
            CreateDefaultOverlays(),
            overlayElements,
            floors,
            selectedFloor,
            $"Map by {author} via tarkov.dev · CC BY-NC-SA 4.0",
            LicenseUri);
    }

    public MapFloorDefinition? SelectFloor(MapVariant variant, WorldPosition position)
    {
        ArgumentNullException.ThrowIfNull(variant);
        return variant.Floors
                .Where(floor => !string.Equals(floor.Id, "base", StringComparison.Ordinal))
                .FirstOrDefault(floor => floor.Extents.Any(extent => extent.Contains(position)))
            ?? variant.Floors.FirstOrDefault(floor => floor.Extents.Any(extent => extent.Contains(position)))
            ?? variant.Floors.FirstOrDefault(floor => floor.IsVisibleByDefault);
    }

    private static MapBackground? CreateBackground(
        MapVariant variant,
        string? cachedAssetPath,
        MapAssetAvailability availability,
        string? message)
    {
        if (variant.TilePath is not null)
        {
            return new(MapBackgroundKind.TileTemplate, variant.TilePath, cachedAssetPath, availability, message);
        }

        if (variant.SvgPath is not null)
        {
            return new(MapBackgroundKind.Svg, variant.SvgPath, cachedAssetPath, availability, message);
        }

        return null;
    }

    private static IReadOnlyList<MapOverlayLayer> CreateDefaultOverlays() =>
    [
        new(MapOverlayKind.CompanionMarkers, "Companion markers", true, false),
        new(MapOverlayKind.Extracts, "Extracts", true, false),
        new(MapOverlayKind.Labels, "Labels", true, false),
        new(MapOverlayKind.Routes, "Routes", true, false),
        new(MapOverlayKind.RiskAndTraffic, "Risk / traffic", true, false),
        new(MapOverlayKind.Filters, "Filters", true, false),
    ];

    private static IReadOnlyList<MapOverlayElement> CreateLabelElements(MapVariant variant)
    {
        if (variant.Transform is null)
        {
            return [];
        }

        var labels = new List<MapOverlayElement>();
        foreach (var label in variant.Labels)
        {
            if (variant.Transform.TryProject(new(label.Position.X, 0, label.Position.Y), out var point))
            {
                labels.Add(new(MapOverlayKind.Labels, point, label.Text, label.RotationDegrees, label.SizePercent));
            }
        }

        return labels;
    }
}

public sealed record MapTilePlanItem(int Zoom, int X, int Y, double Left, double Top, int Size);

public sealed record MapTilePlan(
    IReadOnlyList<MapTilePlanItem> Tiles,
    double Width,
    double Height,
    double OriginPixelX,
    double OriginPixelY,
    string? Error)
{
    public bool IsValid => Error is null && Tiles.Count > 0;
}

public static class MapTilePlanner
{
    public static MapTilePlan Plan(MapVariant variant, int zoom, int maximumTiles = 64)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (variant.TilePath is null || variant.Transform is null || !variant.Transform.IsValid || variant.Bounds?.IsValid != true)
        {
            return new([], 0, 0, 0, 0, "PNG tiles require a valid upstream tile template, bounds, and transform.");
        }

        if (variant.TileSize is <= 0 or > 4096 ||
            variant.MinimumZoom is not { } minimumZoom ||
            variant.MaximumZoom is not { } maximumZoom ||
            minimumZoom > maximumZoom)
        {
            return new([], 0, 0, 0, 0, "PNG tiles require valid upstream tile-size and zoom metadata.");
        }

        if (zoom < minimumZoom || zoom > maximumZoom || zoom is < -30 or > 30)
        {
            return new([], 0, 0, 0, 0, "The requested zoom is outside the upstream zoom range.");
        }

        if (maximumTiles <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumTiles));
        }

        var bounds = variant.Bounds;
        var corners = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        };
        var scale = Math.Pow(2, zoom);
        var projected = corners.Select(corner =>
        {
            variant.Transform.TryProject(corner, out var point);
            return new MapPoint(point.X * scale, point.Y * scale);
        }).ToArray();
        if (projected.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            return new([], 0, 0, 0, 0, "The upstream tile projection is outside the supported numeric range.");
        }

        var minimumPixelX = projected.Min(point => point.X);
        var maximumPixelX = projected.Max(point => point.X);
        var minimumPixelY = projected.Min(point => point.Y);
        var maximumPixelY = projected.Max(point => point.Y);
        var minimumSupportedPixel = int.MinValue * (double)variant.TileSize;
        var maximumSupportedPixel = int.MaxValue * (double)variant.TileSize;
        if (minimumPixelX < minimumSupportedPixel || maximumPixelX > maximumSupportedPixel ||
            minimumPixelY < minimumSupportedPixel || maximumPixelY > maximumSupportedPixel)
        {
            return new([], 0, 0, 0, 0, "The upstream tile projection is outside the supported coordinate range.");
        }

        var minimumTileX = (int)Math.Floor(minimumPixelX / variant.TileSize);
        var maximumTileX = (int)Math.Floor(maximumPixelX / variant.TileSize);
        var minimumTileY = (int)Math.Floor(minimumPixelY / variant.TileSize);
        var maximumTileY = (int)Math.Floor(maximumPixelY / variant.TileSize);
        var tileCountX = (long)maximumTileX - minimumTileX + 1;
        var tileCountY = (long)maximumTileY - minimumTileY + 1;
        var count = tileCountX * tileCountY;
        if (count <= 0 || count > maximumTiles)
        {
            return new([], 0, 0, 0, 0, $"The upstream bounds require {count} tiles; the safe limit is {maximumTiles}.");
        }

        var tiles = new List<MapTilePlanItem>((int)count);
        for (var y = (long)minimumTileY; y <= maximumTileY; y++)
        {
            for (var x = (long)minimumTileX; x <= maximumTileX; x++)
            {
                tiles.Add(new(
                    zoom,
                    (int)x,
                    (int)y,
                    (x - minimumTileX) * variant.TileSize,
                    (y - minimumTileY) * variant.TileSize,
                    variant.TileSize));
            }
        }

        return new(
            tiles,
            tileCountX * variant.TileSize,
            tileCountY * variant.TileSize,
            (double)minimumTileX * variant.TileSize,
            (double)minimumTileY * variant.TileSize,
            null);
    }
}

public sealed record MapViewportState(double Zoom, double PanX, double PanY)
{
    public static MapViewportState Default { get; } = new(1, 0, 0);

    public MapViewportState ZoomBy(double factor, int minimumZoom, int maximumZoom)
    {
        if (!double.IsFinite(factor) || factor <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(factor));
        }

        return this with { Zoom = Math.Clamp(Zoom * factor, minimumZoom, maximumZoom) };
    }

    public MapViewportState PanBy(double deltaX, double deltaY) =>
        this with { PanX = PanX + deltaX, PanY = PanY + deltaY };
}
