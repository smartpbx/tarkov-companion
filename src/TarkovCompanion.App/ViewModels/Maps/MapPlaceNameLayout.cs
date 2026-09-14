namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>One place name, as a thing that has to be drawn or dropped.</summary>
/// <remarks>
/// The position is in canvas units and the size is in screen pixels, which looks inconsistent
/// and is not: place names are counter-scaled, so their size on screen never changes while the
/// map moves underneath them. Scaling both would make the arrangement independent of zoom,
/// which is exactly the thing it must not be.
/// </remarks>
/// <param name="CenterX">Where the text is centred, in canvas units.</param>
/// <param name="CenterY">Where the text is centred, in canvas units.</param>
/// <param name="Width">How wide the text reads on screen, in screen pixels.</param>
/// <param name="Height">How tall it reads on screen, in screen pixels.</param>
/// <param name="Size">
/// How large the catalog asked for it to be drawn. Its own signal of which names matter, and
/// the only one available: a mapper who drew "Dorms" large and "boiler" small meant something
/// by it.
/// </param>
/// <param name="Text">Used only to break a tie, so the same name is dropped every time.</param>
public readonly record struct MapPlaceNameCandidate(
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    double Size,
    string Text);

/// <summary>
/// Decides which of the catalog's place names are drawn, when they will not all fit.
/// </summary>
/// <remarks>
/// <para>
/// Reported with a screenshot of Customs: "Administration Gate" and "Factory Checkpoint" drawn
/// on top of each other, both clipped, neither readable. They are two of the catalog's own
/// place names, drawn exactly where it says at the size and angle it says, with no knowledge of
/// each other.
/// </para>
/// <para>
/// A place name cannot be moved the way a marker's name can. The catalog decides where it goes
/// and a name somewhere else is a name for somewhere else. So the only move available is to
/// drop one, which this codebase has already decided is right for markers: a name that fits
/// nowhere is dropped rather than drawn over something, because an unreadable name is worse
/// than a missing one — it still costs the space and it still has to be read to be dismissed.
/// </para>
/// <para>
/// Which one survives comes from the catalog rather than from here. The larger of the two is
/// the one a mapper drew larger, and ties break alphabetically so the same name is dropped on
/// every frame rather than the two flickering.
/// </para>
/// </remarks>
public static class MapPlaceNameLayout
{
    /// <summary>
    /// The space kept clear around a marker's disc, in screen pixels.
    /// </summary>
    /// <remarks>
    /// Matches <see cref="MapLabelLayout"/>'s own figure, which is the largest disc the map
    /// draws plus a little. A place name under a disc is the other half of what was reported:
    /// "Warehouse 17" with a marker sitting on the middle of the word.
    /// </remarks>
    private const double DiscRadius = 13;

    /// <summary>
    /// Which names to draw, as a flag per candidate in the order they were given.
    /// </summary>
    /// <param name="names">The catalog's place names.</param>
    /// <param name="discs">Every marker's centre, in canvas units.</param>
    /// <param name="zoom">
    /// The current scale. Names are counter-scaled so their size on screen never changes and
    /// their spacing does, which means two names that are clear at one zoom collide at another
    /// and this has to be redone whenever it changes.
    /// </param>
    public static bool[] Choose(
        IReadOnlyList<MapPlaceNameCandidate> names,
        IReadOnlyList<(double X, double Y)> discs,
        double zoom)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(discs);
        var drawn = new bool[names.Count];
        if (names.Count == 0)
        {
            return drawn;
        }

        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            // Nothing is known about where anything is on screen, so nothing is dropped.
            Array.Fill(drawn, true);
            return drawn;
        }

        var order = Enumerable.Range(0, names.Count)
            .OrderByDescending(index => names[index].Size)
            .ThenBy(index => names[index].Text, StringComparer.Ordinal)
            .ToArray();

        var taken = new List<(double Left, double Top, double Right, double Bottom)>(names.Count);
        foreach (var index in order)
        {
            var rect = RectFor(names[index], zoom);
            if (Overlaps(rect, taken) || CoversADisc(rect, discs, zoom))
            {
                continue;
            }

            drawn[index] = true;
            taken.Add(rect);
        }

        return drawn;
    }

    /// <summary>
    /// Where a name lands on screen: its position scaled, its size not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same arithmetic <see cref="MapLabelLayout"/> does for a marker's name, and it has to
    /// be, or the two would disagree about whether a name and a label collide.
    /// </para>
    /// <para>
    /// Public because they did disagree. The marker layout built the same rectangle itself, as
    /// <c>(TextLeft + TextWidth) * zoom</c>, which scales the size as well as the position: at
    /// 23% zoom every place name was handed to that layout as a rectangle a quarter of its real
    /// width. Reported on Customs as "Scav Checkpoint" sitting across the first seven characters
    /// of "Military Checkpoint" — the layout had been told the place name stopped by its second
    /// letter. Calling this is what stops the two disagreeing again.
    /// </para>
    /// </remarks>
    public static (double Left, double Top, double Right, double Bottom) RectFor(
        MapPlaceNameCandidate name,
        double zoom)
    {
        var x = name.CenterX * zoom;
        var y = name.CenterY * zoom;
        return (x - (name.Width / 2), y - (name.Height / 2), x + (name.Width / 2), y + (name.Height / 2));
    }

    private static bool Overlaps(
        (double Left, double Top, double Right, double Bottom) rect,
        IReadOnlyList<(double Left, double Top, double Right, double Bottom)> taken)
    {
        foreach (var other in taken)
        {
            if (rect.Left < other.Right && rect.Right > other.Left &&
                rect.Top < other.Bottom && rect.Bottom > other.Top)
            {
                return true;
            }
        }

        return false;
    }

    private static bool CoversADisc(
        (double Left, double Top, double Right, double Bottom) rect,
        IReadOnlyList<(double X, double Y)> discs,
        double zoom)
    {
        foreach (var disc in discs)
        {
            var x = disc.X * zoom;
            var y = disc.Y * zoom;
            if (x + DiscRadius > rect.Left && x - DiscRadius < rect.Right &&
                y + DiscRadius > rect.Top && y - DiscRadius < rect.Bottom)
            {
                return true;
            }
        }

        return false;
    }
}
