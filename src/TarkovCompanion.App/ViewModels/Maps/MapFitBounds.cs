using Avalonia;

namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>
/// Decides what Fit frames, out of the two pictures a map can be made of.
/// </summary>
/// <remarks>
/// Reported on Shoreline's third floor: the building sat as a thumbnail in the middle of an
/// otherwise empty panel, fitted at 109%, which is the whole map scaled to the window.
///
/// A floor layer replaces the tiles' artwork without removing the tiles. The grid was still
/// listed, none of its tiles carried a picture, and the measurement fell back to the extent of
/// the whole plan, so Fit framed all of Shoreline when the player had asked for one building
/// inside it.
/// </remarks>
public static class MapFitBounds
{
    /// <summary>
    /// Picks the rectangle Fit and centring should use.
    /// </summary>
    /// <remarks>
    /// Where both drew, the smaller wins. That is the floor layer inside the map rather than
    /// the map itself, and framing one building when a floor has been chosen is the point of
    /// choosing a floor. A background covering the same ground as the tiles is not smaller and
    /// does not take over.
    /// </remarks>
    /// <param name="tiles">What the loaded tiles cover, empty when none carries artwork.</param>
    /// <param name="background">What the single background picture actually drew on.</param>
    public static Rect Choose(Rect tiles, Rect background)
    {
        var tiled = Area(tiles);
        var drawn = Area(background);
        if (tiled <= 0)
        {
            return drawn > 0 ? background : default;
        }

        return drawn > 0 && drawn < tiled ? background : tiles;
    }

    /// <summary>
    /// How far past the artwork Fit will stretch to take a marker in, as a share of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Measured across every map in the live feed, as the distance a way out falls outside its
    /// own map's declared bounds. Eight maps have none at all. The genuine overhangs are
    /// Customs at 3.2%, Interchange at 1.5%, Factory at 1.2% and Woods at 0.4% — and Ground
    /// Zero has three exits at <b>123%</b>, which is more than the whole map away from it.
    /// </para>
    /// <para>
    /// So there is a wide empty gap between every real overhang and the one pathological case,
    /// and the allowance goes in it. A tenth is three times the largest real one and a twelfth
    /// of the smallest bad one. Without it, Ground Zero would fit to under half scale to make
    /// room for blank ground and park the map in a corner — which is the exact regression
    /// ContentBounds was introduced to stop, arriving by the other door.
    /// </para>
    /// </remarks>
    public const double MarkerAllowance = 0.10;

    /// <summary>
    /// Stretches the drawn area to take in the markers on it, within the allowance.
    /// </summary>
    /// <remarks>
    /// Fit framed the artwork, which guaranteed you could see the picture and not that you
    /// could see the exits. On Streets it cut "Transit to Interchange" and "Courtyard" in half
    /// on a view marked Fit, and one of those is a way out of the raid.
    ///
    /// Markers outside the allowance are not chased. They are left outside the fitted view
    /// rather than allowed to decide it, because one marker on blank ground half a map away is
    /// not worth showing the whole map smaller to reach.
    /// </remarks>
    /// <param name="content">The drawn artwork, in canvas units.</param>
    /// <param name="markers">Every marker's centre, in the same units.</param>
    public static Rect WithMarkers(Rect content, IReadOnlyList<Point> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        if (Area(content) <= 0 || markers.Count == 0)
        {
            return content;
        }

        var left = content.X - (content.Width * MarkerAllowance);
        var top = content.Y - (content.Height * MarkerAllowance);
        var right = content.Right + (content.Width * MarkerAllowance);
        var bottom = content.Bottom + (content.Height * MarkerAllowance);

        var x1 = content.X;
        var y1 = content.Y;
        var x2 = content.Right;
        var y2 = content.Bottom;
        foreach (var marker in markers)
        {
            // Each axis on its own. A marker far off to the side but level with the map still
            // pulls the view sideways, and refusing it wholesale would lose the common case to
            // guard the rare one.
            if (marker.X >= left && marker.X <= right)
            {
                x1 = Math.Min(x1, marker.X);
                x2 = Math.Max(x2, marker.X);
            }

            if (marker.Y >= top && marker.Y <= bottom)
            {
                y1 = Math.Min(y1, marker.Y);
                y2 = Math.Max(y2, marker.Y);
            }
        }

        return new(x1, y1, x2 - x1, y2 - y1);
    }

    private static double Area(Rect rect) =>
        rect.Width > 0 && rect.Height > 0 ? rect.Width * rect.Height : 0;
}
