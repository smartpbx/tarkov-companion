namespace TarkovCompanion.App.Views.V2.MapRenderer;

/// <summary>
/// Where a pin's own drawn tip sits inside the 44x44 marker box, so the tip — not the middle of
/// the pin's head — lands on the exact map coordinate the box is anchored to.
/// </summary>
/// <remarks>
/// Issue 508: "a square with a number in it is hard to locate where it actually means, so a pin
/// style would be better." <c>MapSceneRendererObjectViewModel.AnchorLeft</c>/<c>AnchorTop</c>
/// still describe the top-left of a box centred on the projected point — every hit-test and
/// gesture test in this repository is built on that — so this class never moves the box. It only
/// says where, inside that unmoving box, a pin shaped <see cref="Width"/> x <see cref="Height"/>
/// has to be drawn for its own bottom-centre point (its tip) to land exactly on the box's centre.
///
/// This holds at any zoom and any map rotation without extra work, because the box itself already
/// does that job: <c>MarkerInverseZoom</c> keeps it the same size on screen regardless of camera
/// zoom, and <c>MarkerUprightDegrees</c> exactly cancels the camera's own rotation, so the box
/// never turns on screen. A fixed pixel offset from its centre is therefore the same offset at
/// every zoom level and every bearing — see <c>MapPinAnchorTests</c>.
/// </remarks>
public static class MapPinGeometry
{
    /// <summary>The pin's own drawn width, in the same DIPs as the marker box.</summary>
    public const double Width = 28;

    /// <summary>The pin's own drawn height, from the top of its head to its tip.</summary>
    public const double Height = 36;

    /// <summary>
    /// The pin's top-left corner, in coordinates local to a <paramref name="boxExtent"/> square
    /// box, so that the pin's tip — its own bottom-centre point — lands exactly on the box's own
    /// centre.
    /// </summary>
    public static (double Left, double Top) TopLeftFor(double boxExtent) =>
        ((boxExtent - Width) / 2, (boxExtent / 2) - Height);

    /// <summary>
    /// Where the pin's tip actually lands, in the same box-local coordinates as
    /// <see cref="TopLeftFor"/> — a sanity check that it is the box's own centre.
    /// </summary>
    public static (double X, double Y) TipFor(double boxExtent)
    {
        var (left, top) = TopLeftFor(boxExtent);
        return (left + (Width / 2), top + Height);
    }
}
