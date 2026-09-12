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
    QuestObjectives,
    CompanionMarkers,
    Extracts,
    Spawns,
    Keys,
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
    string? Message)
{
    /// <summary>
    /// Which level of the tile pyramid is actually loaded.
    /// </summary>
    /// <remarks>
    /// This exists because the level is a choice now rather than a constant. It used to be
    /// the pyramid's own minimum on both sides of the map: the loader fetched the coarsest
    /// level there is, and the coordinate mapper independently assumed the same number. They
    /// agreed, so the markers were in the right places, and the map was a thumbnail magnified
    /// by a scale transform. Zooming in never fetched anything sharper, which is why one map
    /// reads as a flat brown mass.
    ///
    /// Now that the loader picks a level, the mapper has to be told which one, because the
    /// canvas is 2^zoom world units across and everything drawn on it is placed in that
    /// space. If these two ever disagree again, every marker moves and the artwork does not.
    /// </remarks>
    public int? TileZoom { get; init; }
}

public sealed record MapOverlayLayer(MapOverlayKind Kind, string Name, bool IsVisible, bool IsHighlighted);

public sealed record MapOverlayElement(
    MapOverlayKind Layer,
    MapPoint Position,
    string Label,
    double RotationDegrees = 0,
    double SizePercent = 100,
    double? MinimumHeight = null,
    double? MaximumHeight = null)
{
    /// <summary>Which side this is for, so the map can draw it differently.</summary>
    /// <remarks>
    /// An init property rather than another positional parameter, because most elements are
    /// not features and have nothing to say here.
    /// </remarks>
    public MapFeatureFaction Faction { get; init; } = MapFeatureFaction.Unknown;

    /// <summary>
    /// Whether this is one the player has actually been offered this raid.
    /// </summary>
    /// <remarks>
    /// Every map has ten or so extracts and a raid offers a handful of them, chosen when it
    /// starts. Showing all of them equally is the difference between a reference diagram and
    /// an answer: the player wants the ones they can use now, and the rest are context.
    ///
    /// Only an extract list the player scanned sets this. Nothing infers which extracts are
    /// open, because the game does not write that down anywhere the companion can read.
    /// </remarks>
    public bool IsActive { get; init; }
}

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
            .Where(element =>
                Overlays.Any(layer => layer.Kind == element.Layer && layer.IsVisible) &&
                IsVisibleOnSelectedFloor(element))
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

    private bool IsVisibleOnSelectedFloor(MapOverlayElement element)
    {
        if (element.MinimumHeight is null && element.MaximumHeight is null || SelectedFloor is null)
        {
            return true;
        }

        var boundedExtents = SelectedFloor.Extents
            .Where(extent => extent.MinimumHeight is not null || extent.MaximumHeight is not null)
            .ToArray();
        if (boundedExtents.Length == 0)
        {
            return true;
        }

        return boundedExtents.Any(extent =>
            (extent.MinimumHeight is null || element.MaximumHeight is null || element.MaximumHeight > extent.MinimumHeight) &&
            (extent.MaximumHeight is null || element.MinimumHeight is null || extent.MaximumHeight > element.MinimumHeight));
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
        IEnumerable<MapOverlayElement>? companionElements = null,
        MapBackgroundKind? artwork = null)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(variant);
        if (!string.Equals(location.Id, variant.LocationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The map variant does not belong to the requested location.", nameof(variant));
        }

        var background = CreateBackground(variant, cachedAssetPath, assetAvailability, assetMessage, artwork);
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

    /// <summary>
    /// Says which artwork the map is actually showing.
    /// </summary>
    /// <remarks>
    /// This used to answer from what the variant PUBLISHES rather than from what was LOADED,
    /// and it gave tiles unconditional priority. Seven maps publish both a tile pyramid and a
    /// hand-drawn plan, so on every one of them, choosing the drawing produced a render model
    /// that still said "tiles". The caller then went on to display the drawing anyway, and the
    /// coordinate mapper, reading the model, projected every marker through tile pixel space
    /// onto SVG artwork. That is the reported fault: on the drawing, all the icons are in the
    /// wrong place while the picture itself looks correct, because the picture is stretched to
    /// fill whatever canvas it is given and cannot betray the mismatch.
    ///
    /// The contradiction was visible in the result and nobody read it: a background of kind
    /// TileTemplate whose SourceUri was the tile template and whose CachedAssetPath pointed at
    /// an SVG. A background that describes two different things is proof its kind was inferred
    /// rather than chosen.
    ///
    /// So the choice is now passed in. An explicit request wins when the variant can honour
    /// it; otherwise this falls back to the old order, which is still correct for the many
    /// maps that publish only one artwork.
    /// </remarks>
    private static MapBackground? CreateBackground(
        MapVariant variant,
        string? cachedAssetPath,
        MapAssetAvailability availability,
        string? message,
        MapBackgroundKind? artwork)
    {
        if (artwork == MapBackgroundKind.Svg && variant.SvgPath is not null)
        {
            return new(MapBackgroundKind.Svg, variant.SvgPath, cachedAssetPath, availability, message);
        }

        if (artwork == MapBackgroundKind.TileTemplate && variant.TilePath is not null)
        {
            return new(MapBackgroundKind.TileTemplate, variant.TilePath, cachedAssetPath, availability, message);
        }

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
        new(MapOverlayKind.QuestObjectives, "Quest objectives", false, false),
        new(MapOverlayKind.CompanionMarkers, "Companion markers", true, false),
        new(MapOverlayKind.Extracts, "Extracts", true, false),
        new(MapOverlayKind.Labels, "Labels", true, false),
        new(MapOverlayKind.Spawns, "Spawns", false, false),
        new(MapOverlayKind.Keys, "Locked doors", false, false),
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
                labels.Add(new(
                    MapOverlayKind.Labels,
                    point,
                    label.Text,
                    label.RotationDegrees,
                    label.SizePercent,
                    label.MinimumHeight,
                    label.MaximumHeight));
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
