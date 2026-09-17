using Avalonia;

namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>
/// Works out the rectangle of the map panel the floating chrome leaves free.
/// </summary>
/// <remarks>
/// Reported on Streets, screenshotted with every piece of chrome open: the top-left toolbar,
/// the top-right toolbar, the Layers expander and the bottom status expander between them cover
/// enough of the panel that a fit-to-panel map ran an extract label under each of the four.
/// <see cref="MapView.axaml.cs"/>'s FitAndCentre fitted into the whole viewport minus a fixed
/// padding; it had no idea the overlays existed, let alone how big they currently are.
///
/// Each overlay here sits in one of the panel's corners or edges. Sorting a rectangle to the
/// side its centre is nearest turns "leave this rectangle uncovered" into four independent
/// margins, which is simple enough to compute every time an overlay resizes and to unit test
/// without a window. It is deliberately conservative rather than exact: a corner overlay
/// shrinks the free rectangle on both the axis it actually covers and the one it merely sits
/// near, which trades a few free pixels in the opposite corner for never sliding a label under
/// chrome that grew since the last fit.
/// </remarks>
public static class MapOverlayFit
{
    /// <summary>
    /// The rectangle of <paramref name="viewport"/>, in the same top-left-origin coordinates as
    /// <paramref name="overlays"/>, that none of <paramref name="overlays"/> covers, shrunk by
    /// <paramref name="margin"/> on every side so a fitted map does not sit flush against chrome.
    /// </summary>
    /// <remarks>
    /// Overlays with no area (collapsed to nothing, or faded out and reporting a zero size) are
    /// skipped rather than treated as a corner pinned at the origin. Falls back to the whole
    /// viewport, margin included, if the overlays leave nothing usable — better an over-full
    /// panel than a fit nobody can see.
    /// </remarks>
    public static Rect ComputeFreeRect(Size viewport, IReadOnlyList<Rect> overlays, double margin)
    {
        var left = 0d;
        var top = 0d;
        var right = viewport.Width;
        var bottom = viewport.Height;

        foreach (var overlay in overlays)
        {
            if (overlay.Width <= 0 || overlay.Height <= 0)
            {
                continue;
            }

            var centre = overlay.Center;
            if (centre.X < viewport.Width / 2)
            {
                left = Math.Max(left, overlay.Right);
            }
            else
            {
                right = Math.Min(right, overlay.Left);
            }

            if (centre.Y < viewport.Height / 2)
            {
                top = Math.Max(top, overlay.Bottom);
            }
            else
            {
                bottom = Math.Min(bottom, overlay.Top);
            }
        }

        left += margin;
        top += margin;
        right -= margin;
        bottom -= margin;

        var width = right - left;
        var height = bottom - top;
        if (width <= 0 || height <= 0)
        {
            var fallbackWidth = Math.Max(0, viewport.Width - (2 * margin));
            var fallbackHeight = Math.Max(0, viewport.Height - (2 * margin));
            return new(
                Math.Min(margin, viewport.Width),
                Math.Min(margin, viewport.Height),
                fallbackWidth,
                fallbackHeight);
        }

        return new(left, top, width, height);
    }
}
