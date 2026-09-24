namespace TarkovCompanion.App.ViewModels.V2.MapRenderer;

/// <summary>
/// Nudges point markers that project to (almost) the same spot into a small ring around it, so a
/// waypoint dropped on top of a quest objective — or two of either — stay legible instead of one
/// hiding the other.
/// </summary>
/// <remarks>
/// Issue 508: "when two pins land on each other, they must both remain legible (offset or
/// stack)." This only ever adjusts where a marker is <em>drawn</em>: the nudge it returns is
/// added to the pin's own on-screen position (see <c>MapSceneRendererObjectViewModel.PinOffsetX</c>
/// /<c>PinOffsetY</c>), never to <c>AnchorLeft</c>/<c>AnchorTop</c> themselves, which stay the
/// exact projected point every hit-test, gesture and landing test in this repository already
/// depends on. A collided pin's tip is therefore a few pixels off the exact spot on purpose — the
/// alternative is one pin invisible under another, which points at nothing at all.
/// </remarks>
internal static class MapMarkerOverlapLayout
{
    /// <summary>
    /// How close two anchors have to be, in the same canvas DIPs <c>AnchorLeft</c>/<c>AnchorTop</c>
    /// use, to count as landing on each other. Anything closer than one marker box already has
    /// its chip touching or covering its neighbour's.
    /// </summary>
    public const double CollisionDistance = 30;

    /// <summary>How far a collided marker is nudged from the shared point, in the same DIPs.</summary>
    public const double RingRadius = 16;

    /// <summary>How far apart two neighbours on the ring are at the least, in the same DIPs.</summary>
    public const double MinimumRingSpacing = 22;

    /// <summary>
    /// [#797] The ring grows with its group: once objectives stopped folding into a count badge,
    /// five or six of them on one Reserve bunker overlapped each other on the fixed 16-DIP ring.
    /// Up to four keep the old ring; beyond that neighbours stay at least one pin apart.
    /// </summary>
    public static double RadiusFor(int count) =>
        count < 2 ? 0 : Math.Max(RingRadius, MinimumRingSpacing / (2 * Math.Sin(Math.PI / count)));

    /// <summary>
    /// The (dx, dy) to add to each anchor's own drawn position, in the same order as
    /// <paramref name="anchors"/>. Zero for a marker with nothing else near it.
    /// </summary>
    /// <param name="markerScale">
    /// The marks' drawn scale. The nudge is applied inside it, so the pull toward the group's
    /// middle, which is in canvas DIPs, is divided by it to land where it means to.
    /// </param>
    public static IReadOnlyList<(double DeltaX, double DeltaY)> Resolve(
        IReadOnlyList<(double X, double Y)> anchors,
        double markerScale = 1)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        var result = new (double DeltaX, double DeltaY)[anchors.Count];
        var visited = new bool[anchors.Count];
        for (var i = 0; i < anchors.Count; i++)
        {
            if (visited[i])
            {
                continue;
            }

            var group = new List<int> { i };
            visited[i] = true;
            for (var j = i + 1; j < anchors.Count; j++)
            {
                if (visited[j])
                {
                    continue;
                }

                var dx = anchors[j].X - anchors[i].X;
                var dy = anchors[j].Y - anchors[i].Y;
                if (Math.Sqrt((dx * dx) + (dy * dy)) < CollisionDistance)
                {
                    group.Add(j);
                    visited[j] = true;
                }
            }

            if (group.Count < 2)
            {
                continue;
            }

            // Spread the colliding group evenly around the ring: a pair sits left and right of
            // the shared point, a trio at the points of a triangle, and so on. The order is the
            // order they were given in, so it is stable for the same input every time.
            // [#797] Around the group's middle, not each pin's own anchor: anchors a few DIPs
            // apart each nudged around themselves could land right back on one another.
            var radius = RadiusFor(group.Count);
            var middleX = group.Average(index => anchors[index].X);
            var middleY = group.Average(index => anchors[index].Y);
            for (var slot = 0; slot < group.Count; slot++)
            {
                var angle = (2 * Math.PI * slot / group.Count) - (Math.PI / 2);
                var anchor = anchors[group[slot]];
                result[group[slot]] = (
                    (radius * Math.Cos(angle)) + ((middleX - anchor.X) / markerScale),
                    (radius * Math.Sin(angle)) + ((middleY - anchor.Y) / markerScale));
            }
        }

        return result;
    }

    /// <summary>
    /// [#573] Groups of at least <paramref name="minimumCount"/> anchors that chain together within
    /// <paramref name="distance"/> of one another, as index lists in input order; everything else
    /// is left out. Chained rather than all-pairs: three marks along one building are one spot even
    /// when the two ends are further apart than the distance.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Stacks(
        IReadOnlyList<(double X, double Y)> anchors,
        double distance,
        int minimumCount)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        var group = new int[anchors.Count];
        Array.Fill(group, -1);
        var groups = new List<List<int>>();
        for (var start = 0; start < anchors.Count; start++)
        {
            if (group[start] >= 0)
            {
                continue;
            }

            var members = new List<int> { start };
            group[start] = groups.Count;
            for (var next = 0; next < members.Count; next++)
            {
                var (x, y) = anchors[members[next]];
                for (var other = 0; other < anchors.Count; other++)
                {
                    if (group[other] >= 0)
                    {
                        continue;
                    }

                    var dx = anchors[other].X - x;
                    var dy = anchors[other].Y - y;
                    if (Math.Sqrt((dx * dx) + (dy * dy)) < distance)
                    {
                        group[other] = groups.Count;
                        members.Add(other);
                    }
                }
            }

            members.Sort();
            groups.Add(members);
        }

        return groups.Where(members => members.Count >= minimumCount).ToArray();
    }
}
