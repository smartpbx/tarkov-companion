namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>
/// [#573] How big a map mark is drawn at a given camera zoom: smallest with the whole plan fitted,
/// where marks crowd, growing to full size as the player zooms in.
/// </summary>
/// <remarks>
/// The issue asks for marks smallest at fit zoom, where they crowd, growing to full size as the
/// player zooms in, never below a legible size or a comfortable hit target (which may be larger
/// than the drawing). Zoom 1 is the fitted plan (see
/// MapSceneRendererViewModel.MinimumZoom). The growth is logarithmic in zoom because every zoom
/// step multiplies zoom by 1.25, so each press grows a mark by the same amount. A person or a
/// ping is never shrunk: those say where somebody is or what they flagged, which is the one thing
/// a crowded map must not hide.
/// </remarks>
public static class MapMarkerScale
{
    /// <summary>The drawing's scale with the plan fitted.</summary>
    public const double AtFit = 0.7;

    /// <summary>The zoom from which a mark is drawn at full size.</summary>
    public const double FullSizeZoom = 4;

    /// <summary>The smallest on-screen hit target a mark keeps, in screen pixels, at any scale.</summary>
    public const double MinimumHitPixels = 32;

    /// <summary>The hit box every boxed mark already had at full size, in marker DIPs.</summary>
    public const double HitBoxAtFullSize = 40;

    public static double For(double zoom)
    {
        if (!double.IsFinite(zoom) || zoom <= 1)
        {
            return AtFit;
        }

        if (zoom >= FullSizeZoom)
        {
            return 1;
        }

        return AtFit + ((1 - AtFit) * Math.Log(zoom) / Math.Log(FullSizeZoom));
    }

    /// <summary>
    /// The hit box, in the mark's own DIPs, that stays at least <see cref="MinimumHitPixels"/> on
    /// screen once the mark is drawn at <paramref name="scale"/>.
    /// </summary>
    public static double HitExtent(double scale) =>
        Math.Max(HitBoxAtFullSize, MinimumHitPixels / Math.Clamp(scale, AtFit, 1));
}
