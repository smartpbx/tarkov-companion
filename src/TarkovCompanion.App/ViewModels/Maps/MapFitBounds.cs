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

    private static double Area(Rect rect) =>
        rect.Width > 0 && rect.Height > 0 ? rect.Width * rect.Height : 0;
}
