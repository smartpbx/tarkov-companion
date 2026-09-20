namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>
/// The camera that makes a set of points fill a card, however the plan is turned.
/// </summary>
/// <remarks>
/// [Issue 551] "especially on maps i rotated, the default fit-zoom is too zoomed out and the map
/// does not fill up the bounds." The projection fits the plan's rectangle into the card the way it
/// lies at bearing 0, and "fit" was zoom 1 of that. Streets is taller than it is wide; turned a
/// quarter to lie along a wide card it kept the scale that had made it fit the card's height
/// standing up, and filled about half the card each way. Fitting has to happen after the turn:
/// turn the points, take what they cover on screen, and scale that into the card.
///
/// Points rather than a rectangle, because what should fill the card is the map — where its
/// extracts, spawns and place names are — and not the artwork's canvas, which for several maps
/// has a wide empty margin baked in. At an odd bearing the corners of a rectangle also reach much
/// further than anything drawn in it. Every point given is inside the fitted view by
/// construction, which is what keeps an extract at the edge of a map on screen.
///
/// Everything here is in the projection's own pixels: the plan at zoom 1 and bearing 0.
/// </remarks>
public static class MapFitGeometry
{
    /// <summary>Where to centre, in the projection's pixels, and how far to zoom.</summary>
    public readonly record struct Fit(double CentreX, double CentreY, double Zoom);

    /// <summary>
    /// The fit for <paramref name="points"/> on a card, or nothing when there is nothing to fit.
    /// </summary>
    /// <param name="points">What must be on screen, in projected pixels.</param>
    /// <param name="bearingDegrees">The camera's bearing: the plan is drawn turned by minus this.</param>
    /// <param name="cardWidth">The card's width in pixels.</param>
    /// <param name="cardHeight">The card's height in pixels.</param>
    /// <param name="margin">Pixels kept clear inside each edge of the card.</param>
    public static Fit? For(
        IReadOnlyList<(double X, double Y)> points,
        double bearingDegrees,
        double cardWidth,
        double cardHeight,
        double margin)
    {
        ArgumentNullException.ThrowIfNull(points);
        var roomX = cardWidth - (2 * margin);
        var roomY = cardHeight - (2 * margin);
        if (points.Count == 0 || !double.IsFinite(bearingDegrees) ||
            !double.IsFinite(roomX) || !double.IsFinite(roomY) || roomX <= 0 || roomY <= 0)
        {
            return null;
        }

        var radians = bearingDegrees * Math.PI / 180;
        var cosine = Math.Cos(radians);
        var sine = Math.Sin(radians);
        double minimumX = double.PositiveInfinity, minimumY = double.PositiveInfinity;
        double maximumX = double.NegativeInfinity, maximumY = double.NegativeInfinity;
        foreach (var (x, y) in points)
        {
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                continue;
            }

            // Plan to screen: the same turn the camera surface makes.
            var screenX = (cosine * x) + (sine * y);
            var screenY = (-sine * x) + (cosine * y);
            minimumX = Math.Min(minimumX, screenX);
            maximumX = Math.Max(maximumX, screenX);
            minimumY = Math.Min(minimumY, screenY);
            maximumY = Math.Max(maximumY, screenY);
        }

        if (double.IsInfinity(minimumX))
        {
            return null;
        }

        var width = maximumX - minimumX;
        var height = maximumY - minimumY;
        var zoom = Math.Min(
            width > 0 ? roomX / width : double.PositiveInfinity,
            height > 0 ? roomY / height : double.PositiveInfinity);
        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            return null;
        }

        // The middle of what they cover on screen, turned back into the plan.
        var middleX = (minimumX + maximumX) / 2;
        var middleY = (minimumY + maximumY) / 2;
        return new(
            (cosine * middleX) - (sine * middleY),
            (sine * middleX) + (cosine * middleY),
            zoom);
    }
}
