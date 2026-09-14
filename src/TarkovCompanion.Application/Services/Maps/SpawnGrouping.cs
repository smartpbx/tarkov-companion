using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// Draws one marker per spawn area rather than one per spawn point.
/// </summary>
/// <remarks>
/// <para>
/// A spawn is published as every individual position the game may put somebody at, and a group
/// of players spawning together occupies a dozen of them within a few metres. Drawn one per
/// point that is 2,550 markers across the maps — 273 on Customs, 400 on Streets — which buries
/// the exits the map exists to show. What a player wants is the place, not the places.
/// </para>
/// <para>
/// Grouped by proximity rather than by the catalog's <c>zoneName</c>, because that field is an
/// identifier about half the time: of the player spawns, 1,471 distinct zone names cover 2,550
/// points, so grouping by it barely groups anything. Proximity is what the question actually
/// means — "where would a group of players arrive" is a place on the ground.
/// </para>
/// <para>
/// Each group takes every point within <see cref="WithinMetres"/> of its seed, rather than
/// growing a group by chaining from member to member. Chaining measures much better on paper —
/// 369 markers instead of 551 — and produces groups 283 metres across on Reserve and 271 on
/// Customs, because a line of spawns links end to end. A marker at the middle of one of those
/// is not where anybody spawns. Seeding bounds a group at twice the radius by construction, and
/// measured against the real catalog the widest is 65 metres.
/// </para>
/// <para>
/// Grouped by the two things the map itself distinguishes, and nothing else: the side, which is
/// what it filters on, and whether a player can spawn there, which is what the panel beside it
/// lists. Splitting on the raw category string as well looked reasonable and produced 815
/// markers instead of 551, because upstream writes "player", "player, bot" and "player, bot,
/// boss" for positions in the same yard. Those are one place to somebody reading a map.
/// </para>
/// <para>
/// Sides are never merged. A scav area and a PMC area can sit in the same yard, and one marker
/// for both would have to claim a side.
/// </para>
/// </remarks>
public static class SpawnGrouping
{
    /// <summary>How far from its seed a point may be and still be the same place.</summary>
    /// <remarks>
    /// Forty metres is a building or a yard, and it is what this application already treats as
    /// "this place" when it names a waypoint. It means a marker is never more than forty metres
    /// from any point it stands for, which is the property that makes drawing one honest.
    /// </remarks>
    public const double WithinMetres = 40;

    /// <summary>
    /// Replaces the spawn points with one feature per spawn area, leaving everything else be.
    /// </summary>
    public static IReadOnlyList<MapFeature> Collapse(IReadOnlyList<MapFeature> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        // Carried with their positions in the original list, because the order features arrive
        // in is the order they are drawn in, and spawns appended after everything else would
        // paint over the exits -- which are the markers the map exists to show.
        var spawns = new List<(int At, MapFeature Feature)>();
        var kept = new List<(int At, MapFeature Feature)>(features.Count);
        for (var index = 0; index < features.Count; index++)
        {
            var feature = features[index];
            (feature.Kind == MapFeatureKind.Spawn ? spawns : kept).Add((index, feature));
        }

        if (spawns.Count == 0)
        {
            return features;
        }

        // Ordered so the same catalog always produces the same markers in the same places. A
        // seed chosen by enumeration order would move a marker when upstream reordered a list.
        foreach (var side in spawns
            .GroupBy(
                spawn => $"{spawn.Feature.Side}|{SpawnProximity.IsPlayerSpawn(spawn.Feature)}",
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var left = side
                .OrderBy(spawn => spawn.Feature.Position.X)
                .ThenBy(spawn => spawn.Feature.Position.Z)
                .ToList();
            while (left.Count > 0)
            {
                var seed = left[0];
                var group = left
                    .Where(spawn => Flat(seed.Feature.Position, spawn.Feature.Position) <= WithinMetres)
                    .ToArray();
                left.RemoveAll(group.Contains);
                // The earliest place any of its members held, so a group sits where its
                // spawns sat rather than after everything that was not a spawn.
                kept.Add((group.Min(spawn => spawn.At), Merge([.. group.Select(spawn => spawn.Feature)])));
            }
        }

        return [.. kept.OrderBy(entry => entry.At).Select(entry => entry.Feature)];
    }

    /// <summary>
    /// One feature standing for a group, at the middle of it.
    /// </summary>
    /// <remarks>
    /// The height is the lowest rather than the average, because the floor filter compares a
    /// feature's height against the floor being read and a group spread up a hillside should
    /// appear on the floor its lowest member is on rather than half way between two.
    ///
    /// How many points it stands for is said, because "Spawn" on its own gives no sense of
    /// whether this is a five-man's arrival or one straggler's.
    /// </remarks>
    private static MapFeature Merge(IReadOnlyList<MapFeature> group)
    {
        var first = group[0];
        if (group.Count == 1)
        {
            return first;
        }

        // The name most of the group agrees on, because upstream gives positions in one yard
        // different zone suffixes and the first one alphabetically is not more true than the
        // rest. Ties go to the shortest, which is the one without a suffix.
        var name = group
            .GroupBy(spawn => spawn.Name, StringComparer.Ordinal)
            .OrderByDescending(names => names.Count())
            .ThenBy(names => names.Key.Length)
            .ThenBy(names => names.Key, StringComparer.Ordinal)
            .First()
            .Key;

        return first with
        {
            Position = new(
                group.Average(spawn => spawn.Position.X),
                group.Min(spawn => spawn.Position.Y),
                group.Average(spawn => spawn.Position.Z)),
            Name = $"{name} · {group.Count} points",
        };
    }

    /// <summary>Flat distance, because the map is flat and so is the question.</summary>
    private static double Flat(WorldPosition from, WorldPosition to)
    {
        var dx = from.X - to.X;
        var dz = from.Z - to.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }
}
