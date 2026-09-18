using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// The rectangle of Leaflet map units the artwork on screen actually covers.
/// </summary>
/// <remarks>
/// Both numbers a renderer needs and could not previously agree on: where the plan starts and
/// how big it is, in the same units <see cref="MapCatalogTransform.TryProject"/> puts a world
/// position into. Everything drawn on a map — the artwork, an extract, the player's dot, a
/// trail, a loot spawn — is placed against this one rectangle, so "the marker is on the
/// building it names" is a property of one transform rather than of several that happen to
/// agree.
/// </remarks>
public sealed record MapPlanRect(double MinimumX, double MinimumY, double MaximumX, double MaximumY)
{
    public double Width => MaximumX - MinimumX;

    public double Height => MaximumY - MinimumY;

    /// <summary>The plan's true width-to-height ratio: what says Streets is wide and Factory is not.</summary>
    public double Aspect => Height > 0 ? Width / Height : double.NaN;

    public bool IsValid =>
        double.IsFinite(MinimumX) && double.IsFinite(MinimumY) &&
        double.IsFinite(MaximumX) && double.IsFinite(MaximumY) &&
        Width > 0 && Height > 0;
}

/// <summary>
/// The one plan rectangle behind every map projection, for the artwork that is actually loaded.
/// </summary>
/// <remarks>
/// A map is drawn either from one rasterized SVG covering the variant's reviewed bounds, or from
/// a grid of PNG tiles covering a slightly larger, tile-aligned rectangle. Those are different
/// rectangles, and which one is on screen depends on the artwork the loader chose, so the answer
/// has to come from the render model rather than from the variant alone.
///
/// Before this existed, V1 derived the rectangle inside its canvas coordinate mapper and the V2
/// scene did not derive it at all: it declared a 0–100 square as the plan's bounds and then fed
/// raw Leaflet coordinates into it. Markers were therefore laid out in one space and the artwork
/// in another, which is why Customs' extracts sat piled against the edges of the plan instead of
/// on its exits, why the camera opened centred on a point outside the plan, and why pans snapped
/// back the moment the clamp pulled that point onto the bounds.
/// </remarks>
public static class MapPlanProjection
{
    /// <summary>The plan rectangle for the artwork this model is showing, or null if it cannot be placed.</summary>
    public static MapPlanRect? For(MapRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return TilePlanRect(model) ?? Reviewed(model);
    }

    /// <summary>
    /// The rectangle the reviewed map itself covers — the shape the map really is.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 46] Not the same rectangle as <see cref="For"/> whenever the artwork is
    /// a tile grid, and that difference is the aspect-ratio bug. A tile grid snaps outwards to
    /// whole tiles, so the picture covers up to one whole tile more ground than the map does on
    /// each of its four sides, and at the coarse levels the loader can pick a tile is a large
    /// fraction of the map. The snapped rectangle is always squarer than the map, measured
    /// against the 2026-09-18 catalog: Customs is 1.967 wide-to-tall and its grid is 1.700,
    /// Shoreline is 1.510 and its grid is 1.333, The Lab is 1.372 and its grid is 1.333. Fitting
    /// the grid into the card therefore drew Customs as though it were a third squarer than it
    /// is, and left a blank band of unreviewed tiles above and below it.
    ///
    /// A renderer that fits this rectangle rather than the grid's draws the map at its own shape
    /// and at the size the card can actually give it. It keeps #413's contract as long as the
    /// artwork it draws covers this same rectangle — which is why the cockpit composes the tile
    /// mosaic cropped to it rather than to the grid.
    ///
    /// Which bounds are reviewed depends on the artwork: a drawing covers the variant's own SVG
    /// bounds where it publishes them (Reserve is the one that does), and tiles are planned from
    /// the variant's world bounds, so a tile map's reviewed rectangle is those.
    /// </remarks>
    public static MapPlanRect? Reviewed(MapRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var variant = model.Variant;
        if (variant.Transform is not { IsValid: true })
        {
            return null;
        }

        var bounds = model.Background?.Kind == MapBackgroundKind.TileTemplate
            ? variant.Bounds
            : variant.SvgBounds ?? variant.Bounds;
        if (bounds?.IsValid != true)
        {
            return null;
        }

        var corners = new[]
        {
            new WorldPosition(bounds.First.X, 0, bounds.First.Y),
            new WorldPosition(bounds.First.X, 0, bounds.Second.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.First.Y),
            new WorldPosition(bounds.Second.X, 0, bounds.Second.Y),
        };
        var projected = new MapPoint[corners.Length];
        for (var index = 0; index < corners.Length; index++)
        {
            if (!variant.Transform.TryProject(corners[index], out projected[index]))
            {
                return null;
            }
        }

        return Validated(new(
            projected.Min(point => point.X),
            projected.Min(point => point.Y),
            projected.Max(point => point.X),
            projected.Max(point => point.Y)));
    }

    /// <summary>The tile grid's own outward-snapped rectangle, or null when this is not a tile map.</summary>
    private static MapPlanRect? TilePlanRect(MapRenderModel model)
    {
        var variant = model.Variant;
        // The level the loader actually fetched, not the pyramid's minimum. These must be the
        // same number or every marker lands in a different coordinate space from the artwork.
        if (variant.Transform is not { IsValid: true } ||
            model.Background?.Kind != MapBackgroundKind.TileTemplate ||
            (model.Background.TileZoom ?? variant.MinimumZoom) is not { } zoom)
        {
            return null;
        }

        var plan = MapTilePlanner.Plan(variant, zoom, MapTilePlanner.MaximumTilesPerView);
        if (!plan.IsValid)
        {
            return null;
        }

        // The tile grid snaps outwards to whole tiles, so it covers a little more ground than the
        // reviewed bounds do. That larger rectangle is what the whole mosaic covers.
        var scale = Math.Pow(2, zoom);
        return Validated(new(
            plan.OriginPixelX / scale,
            plan.OriginPixelY / scale,
            (plan.OriginPixelX + plan.Width) / scale,
            (plan.OriginPixelY + plan.Height) / scale));
    }

    private static MapPlanRect? Validated(MapPlanRect rect) => rect.IsValid ? rect : null;
}
