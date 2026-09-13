namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>One marker's name, as a thing that has to be fitted somewhere.</summary>
/// <param name="CenterX">The marker's canvas position.</param>
/// <param name="CenterY">The marker's canvas position.</param>
/// <param name="Width">How wide the name reads on screen, in screen pixels.</param>
/// <param name="Height">How tall it reads on screen, in screen pixels.</param>
/// <param name="Priority">Which name survives when two cannot both be shown. Higher wins.</param>
public readonly record struct MapLabelCandidate(
    double CenterX,
    double CenterY,
    double Width,
    double Height,
    int Priority);

/// <summary>
/// Decides where each marker's name sits, so that no two of them overlap.
/// </summary>
/// <remarks>
/// Reported with a screenshot: on Customs "Sniper Roadblock" sat across the marker above it and
/// across the name below it. Every name was drawn at the same fixed offset under its own disc
/// with no knowledge of its neighbours, so wherever markers clustered the names piled up.
///
/// The only mitigation was hiding every name below a zoom threshold, which is why they
/// appeared and vanished as a group rather than arranging themselves. That also meant the map
/// could not name anything at the zoom where somebody looks at the whole of it, which is
/// exactly when "which exit is that" is asked.
///
/// Names are counter-scaled, so their size on screen never changes and their spacing does:
/// pulling the map back moves the discs together while the names stay the same size. The
/// arrangement is therefore a function of zoom and is redone whenever it changes.
///
/// A name that fits nowhere is dropped rather than drawn over something. The order decides who
/// gets dropped: an exit the player can take now, then exits and transits, then everything
/// else, and alphabetically within a rank so the same name is dropped each time rather than
/// flickering between two.
/// </remarks>
public static class MapLabelLayout
{
    /// <summary>The slot for a name that would cover something whatever is tried.</summary>
    public const int Hidden = -1;

    /// <summary>How far below the marker's centre the first slot starts, in screen pixels.</summary>
    /// <remarks>Half the disc box plus a two pixel gap, which is where names have always sat.</remarks>
    public const double FirstSlotTop = 19;

    /// <summary>The space kept clear around a disc so a name never covers one.</summary>
    private const double DiscRadius = 13;

    /// <summary>The gap between two names stacked in consecutive slots.</summary>
    private const double RowGap = 3;

    /// <summary>
    /// Where each name goes, as a slot index, or <see cref="Hidden"/>.
    /// </summary>
    /// <remarks>
    /// Slots run below, above, a row further below, and a row further above. Two rows is the
    /// limit deliberately: a name three rows from its own disc, with other discs in between,
    /// stops saying which marker it belongs to.
    /// </remarks>
    public static int[] Arrange(IReadOnlyList<MapLabelCandidate> labels, double zoom)
    {
        ArgumentNullException.ThrowIfNull(labels);
        var slots = new int[labels.Count];
        if (labels.Count == 0)
        {
            return slots;
        }

        if (!double.IsFinite(zoom) || zoom <= 0)
        {
            // Nothing is known about where anything is, so nothing is moved.
            Array.Fill(slots, 0);
            return slots;
        }

        var discs = new (double X, double Y)[labels.Count];
        for (var index = 0; index < labels.Count; index++)
        {
            discs[index] = (labels[index].CenterX * zoom, labels[index].CenterY * zoom);
        }

        var order = Enumerable.Range(0, labels.Count)
            .OrderByDescending(index => labels[index].Priority)
            .ThenBy(index => discs[index].Y)
            .ThenBy(index => discs[index].X)
            .ToArray();

        var taken = new List<(double Left, double Top, double Right, double Bottom)>(labels.Count);
        Array.Fill(slots, Hidden);
        foreach (var index in order)
        {
            for (var slot = 0; slot < SlotCount; slot++)
            {
                var rect = RectFor(labels[index], discs[index], slot);
                if (CoversADisc(rect, discs) || Overlaps(rect, taken))
                {
                    continue;
                }

                slots[index] = slot;
                taken.Add(rect);
                break;
            }
        }

        return slots;
    }

    /// <summary>How far below the marker's centre a name's top edge sits, in screen pixels.</summary>
    /// <remarks>
    /// Negative above, positive below. Exposed because the view has to turn this back into a
    /// margin inside the marker's own box, and two descriptions of the same geometry drift.
    /// </remarks>
    public static double TopOffsetFor(int slot, double height) => slot switch
    {
        0 => FirstSlotTop,
        1 => -FirstSlotTop - height,
        2 => FirstSlotTop + height + RowGap,
        _ => -FirstSlotTop - (2 * height) - RowGap,
    };

    private const int SlotCount = 4;

    private static (double Left, double Top, double Right, double Bottom) RectFor(
        MapLabelCandidate label,
        (double X, double Y) disc,
        int slot)
    {
        var top = disc.Y + TopOffsetFor(slot, label.Height);
        var half = label.Width / 2;
        return (disc.X - half, top, disc.X + half, top + label.Height);
    }

    private static bool CoversADisc(
        (double Left, double Top, double Right, double Bottom) rect,
        (double X, double Y)[] discs)
    {
        foreach (var disc in discs)
        {
            if (rect.Left < disc.X + DiscRadius &&
                rect.Right > disc.X - DiscRadius &&
                rect.Top < disc.Y + DiscRadius &&
                rect.Bottom > disc.Y - DiscRadius)
            {
                return true;
            }
        }

        return false;
    }

    private static bool Overlaps(
        (double Left, double Top, double Right, double Bottom) rect,
        List<(double Left, double Top, double Right, double Bottom)> taken)
    {
        foreach (var other in taken)
        {
            if (rect.Left < other.Right &&
                rect.Right > other.Left &&
                rect.Top < other.Bottom &&
                rect.Bottom > other.Top)
            {
                return true;
            }
        }

        return false;
    }
}
