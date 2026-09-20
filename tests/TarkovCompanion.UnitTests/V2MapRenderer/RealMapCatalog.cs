using System.Text.Json;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Infrastructure.Maps;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// Every interactive map tarkov.dev publishes, and the shape each drawing actually is.
/// </summary>
/// <remarks>
/// See fixtures/maps/README.md. The catalog half goes through the production parser, so a test
/// built on this is testing the maps as the application reads them rather than a shape this
/// repository invented. <see cref="SvgAspect"/> is the one fact the catalog does not carry: the
/// published drawing's own <c>viewBox</c>, measured from the file at its <c>svgPath</c>. Without
/// it nothing can say whether a rectangle this application worked out matches the picture.
/// </remarks>
internal sealed class RealMapCatalog
{
    private static readonly Lazy<RealMapCatalog> Instance = new(() => new(Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "maps",
        "catalog-geometry-2026-09-18.json")));

    private static readonly DateTimeOffset RetrievedUtc = new(2026, 9, 18, 0, 0, 0, TimeSpan.Zero);

    private readonly TarkovDevMapCatalog _catalog;
    private readonly Dictionary<string, double> _svgAspects = new(StringComparer.OrdinalIgnoreCase);

    private RealMapCatalog(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        _catalog = TarkovDevMapCatalogParser.Parse(
            document.RootElement.GetProperty("maps").GetRawText(),
            new(document.RootElement.GetProperty("source").GetString()!),
            RetrievedUtc);
        foreach (var entry in document.RootElement.GetProperty("svgViewBox").EnumerateObject())
        {
            var size = entry.Value.EnumerateArray().Select(value => value.GetDouble()).ToArray();
            _svgAspects[entry.Name] = size[0] / size[1];
        }
    }

    public static RealMapCatalog Load() => Instance.Value;

    public IReadOnlyList<MapLocation> Locations => _catalog.Locations;

    public MapVariant Variant(MapLocation location) => location.Variants
        .Single(variant => variant.Projection == MapProjectionKind.Interactive);

    /// <summary>The published drawing's own width-to-height ratio, or null where it publishes none.</summary>
    public double? SvgAspect(string locationId) =>
        _svgAspects.TryGetValue(locationId, out var aspect) ? aspect : null;

    /// <summary>The render model for one of the artworks a map publishes.</summary>
    public MapRenderModel Model(MapLocation location, MapBackgroundKind artwork, int? tileZoom = null)
    {
        var variant = Variant(location);
        var model = new MapPresentationService().Create(
            location,
            variant,
            artwork == MapBackgroundKind.Svg ? $"/cache/{location.Id}.svg" : $"/cache/{location.Id}",
            MapAssetAvailability.Available,
            null,
            [],
            artwork);
        return model.Background is { Kind: MapBackgroundKind.TileTemplate } background
            ? model with { Background = background with { TileZoom = tileZoom ?? variant.MinimumZoom } }
            : model;
    }
}
