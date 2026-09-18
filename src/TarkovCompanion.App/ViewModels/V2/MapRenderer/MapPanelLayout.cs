namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>Where a map workspace's context panel sits relative to the map.</summary>
public enum MapPanelPlacement
{
    /// <summary>Down the side of the map, as docs/design/v2's concepts draw it.</summary>
    Beside = 0,

    /// <summary>Underneath the map, across the full width.</summary>
    Below,
}

/// <summary>
/// V2 rough package 32: chooses the placement that actually draws more map.
/// </summary>
/// <remarks>
/// A plan is never stretched and never cropped by its own fit, so a map card whose shape is far
/// from the plan's shape spends the difference on nothing. That is what the acceptance sweep
/// measured on Plan at 1920x1080: a wide map (Customs is about 1.8:1) inside a card left almost
/// square by a 340px list and a 440px panel, with roughly 400 of the card's 930 vertical pixels
/// empty.
///
/// The rule is arithmetic rather than a breakpoint list, so a window size nobody has tried gets
/// the same answer for the same reason: work out the plan's drawn area for each arrangement and
/// take the bigger one. At 1920x1080 that moves Plan's objectives under the map and the map grows
/// by about half; at 3840x1080 the same arithmetic leaves it beside, which is where the concept
/// puts it, and the Raid and Team cockpits — which have no left-hand list eating their width —
/// keep it beside at every size.
/// </remarks>
public static class MapPanelLayout
{
    /// <summary>
    /// How much better below has to be before the panel moves.
    /// </summary>
    /// <remarks>
    /// Beside is the concepts' arrangement and the one a player is used to, so a near-tie keeps
    /// it. It also stops the panel flipping back and forth while a window is dragged across the
    /// crossing point, which a bare comparison would do on a single pixel.
    /// </remarks>
    public const double BelowMargin = 1.15;

    /// <summary>The placement that draws the larger plan, given what each arrangement costs.</summary>
    /// <param name="availableWidth">Width for the map and the panel together.</param>
    /// <param name="availableHeight">Height for the map and the panel together.</param>
    /// <param name="panelWidth">What the panel takes from the width when it sits beside the map.</param>
    /// <param name="panelHeight">What the panel takes from the height when it sits below the map.</param>
    /// <param name="planAspect">The plan's width divided by its height.</param>
    public static MapPanelPlacement Choose(
        double availableWidth,
        double availableHeight,
        double panelWidth,
        double panelHeight,
        double planAspect)
    {
        var beside = DrawnArea(availableWidth - panelWidth, availableHeight, planAspect);
        var below = DrawnArea(availableWidth, availableHeight - panelHeight, planAspect);
        return beside > 0 && below > beside * BelowMargin ? MapPanelPlacement.Below : MapPanelPlacement.Beside;
    }

    /// <summary>
    /// The area a plan of that shape covers when it is contained in that box, in square pixels.
    /// </summary>
    /// <remarks>
    /// The same "contain" fit <see cref="MapSceneProjection"/> applies, so this answers the
    /// question the projection will answer later rather than a proxy for it. A box or an aspect
    /// that cannot be drawn into is worth nothing, which makes an unusable arrangement lose.
    /// </remarks>
    public static double DrawnArea(double width, double height, double planAspect)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(planAspect) ||
            width <= 0 || height <= 0 || planAspect <= 0)
        {
            return 0;
        }

        var drawnWidth = Math.Min(width, height * planAspect);
        return drawnWidth * (drawnWidth / planAspect);
    }
}
