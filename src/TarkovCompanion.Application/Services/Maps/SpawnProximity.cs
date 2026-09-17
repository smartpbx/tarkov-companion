using System.Globalization;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>One spawn area near where this raid began, standing for every point inside it.</summary>
/// <param name="Name">What the map calls it, including how many points it stands for.</param>
/// <param name="Side">Who starts there: PMC, scav, or either.</param>
/// <param name="Position">The area's marker position, in world coordinates.</param>
/// <param name="MetresFromStart">How far the area is from where this raid began.</param>
/// <param name="MetresFromPlayer">How far it is from where the player is now, where that is known.</param>
/// <param name="Bearing">Which way it lies from the player, as a compass point.</param>
public sealed record NearbySpawn(
    string Name,
    MapFeatureFaction Side,
    WorldPosition Position,
    double MetresFromStart,
    double? MetresFromPlayer,
    string? Bearing);

/// <summary>
/// Works out where the other players in this raid started.
/// </summary>
/// <remarks>
/// <para>
/// The first screenshot of a raid is taken where the player spawned, so the player spawn areas
/// near it are where everybody else began. In the first ninety seconds of a raid that is the
/// most useful thing a map can say, and every one of those points has been synced to disk since
/// the first data refresh without ever being put to this use.
/// </para>
/// <para>
/// Built from the same <see cref="SpawnGrouping"/> the map markers use, one row per area rather
/// than one per point, so a list row, a threat line drawn from it and a marker on the map are
/// the same thing. Listing every point gave "Spawn · Village 0 m", "10 m", "13 m", "13 m", "15
/// m", "16 m", "17 m" for a single group of people who arrived together; that is one place, not
/// seven rows.
/// </para>
/// <para>
/// What counts as a player spawn comes from the feed rather than from a guess. Across every map
/// there are 3018 spawn points: 1390 are categorised "player" alone, and the sides they carry
/// are "all" on 860, "pmc" on 528 and "scav" on 1628. So a point where a PMC can start is one
/// whose categories include "player" and whose side is PMC or shared, and the scav points are
/// the same test with the other side. Bot, boss and sniper points are somebody else's problem
/// and are left out.
/// </para>
/// <para>
/// A scav gets nothing at all, matching the map's spawn layer
/// (<c>MapViewModel.CanBeTaken</c>): a scav joins twenty minutes in and arrives wherever the
/// game puts them, so neither where the PMCs started nor where the other scavs may arrive is a
/// question they are asking.
/// </para>
/// <para>
/// The anchor is the first screenshot, not the spawn. Somebody who runs for a minute before
/// taking one has an anchor a minute from where they started, so nothing here calls it "your
/// spawn"; the panel says what it is anchored to and lets the player judge it. The area nearest
/// that anchor, if it is close enough to actually be it, is left out of the list: "where the
/// others started" should not list where you did.
/// </para>
/// </remarks>
public static class SpawnProximity
{
    /// <summary>How far from the start still counts as "near", in metres.</summary>
    /// <remarks>
    /// Three hundred metres. Rows are areas now, not points, so widening the radius from the
    /// original hundred and fifty no longer risks the list itself growing — the cap of eight
    /// rows does that job — it only reaches the areas that share a corner of the map with you.
    /// </remarks>
    public const double DefaultRadiusMetres = 300;

    /// <summary>The most worth listing, because a longer list is not read at all.</summary>
    private const int Maximum = 8;

    private static readonly string[] CompassPoints = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>
    /// The player spawns within a radius of where this raid began, nearest first.
    /// </summary>
    /// <param name="features">Every fixed feature of the map.</param>
    /// <param name="anchor">Where the raid began, as far as the screenshots know.</param>
    /// <param name="player">Where the player is now, for distance and bearing. Optional.</param>
    /// <param name="side">
    /// The player's own side, so the list answers "where did the others start" rather than
    /// "where could anybody start". Unknown lists both. Scav lists nothing.
    /// </param>
    /// <param name="radiusMetres">How far out to look.</param>
    public static IReadOnlyList<NearbySpawn> Near(
        IReadOnlyList<MapFeature> features,
        WorldPosition anchor,
        WorldPosition? player,
        MapFeatureFaction side,
        double radiusMetres = DefaultRadiusMetres)
    {
        ArgumentNullException.ThrowIfNull(features);

        // A scav joins twenty minutes in and arrives wherever the game puts them, so this list
        // is a question they are not asking. The map's spawn layer already draws nothing for a
        // scav; the panel and the threat lines built from it now agree with it.
        if (side == MapFeatureFaction.Scav)
        {
            return [];
        }

        var areas = SpawnGrouping.Collapse(features)
            .Where(feature =>
                feature.Kind == MapFeatureKind.Spawn &&
                IsPlayerSpawn(feature) &&
                Matches(feature.Side, side))
            .ToArray();

        if (areas.Length == 0)
        {
            return [];
        }

        // The area nearest the anchor is where this player started. Nearest rather than an
        // exact hit because the anchor is the first screenshot, not a catalog point, and a
        // group's marker sits at the middle of it rather than on any single spawn.
        var ownIndex = 0;
        for (var index = 1; index < areas.Length; index++)
        {
            if (Distance(anchor, areas[index].Position) < Distance(anchor, areas[ownIndex].Position))
            {
                ownIndex = index;
            }
        }

        var excludeOwnArea = Distance(anchor, areas[ownIndex].Position) <= SpawnGrouping.WithinMetres;

        var found = new List<NearbySpawn>();
        for (var index = 0; index < areas.Length; index++)
        {
            if (excludeOwnArea && index == ownIndex)
            {
                continue;
            }

            var area = areas[index];
            var fromStart = Distance(anchor, area.Position);
            if (fromStart > radiusMetres)
            {
                continue;
            }

            found.Add(new(
                area.Name,
                area.Side,
                area.Position,
                fromStart,
                player is { } here ? Distance(here, area.Position) : null,
                player is { } from ? Compass(from, area.Position) : null));
        }

        return [.. found.OrderBy(spawn => spawn.MetresFromStart).Take(Maximum)];
    }

    /// <summary>
    /// Whether this point is somewhere a player starts, as opposed to a bot.
    /// </summary>
    /// <remarks>
    /// The categories arrive joined into one string by whatever read them, so this is a
    /// substring test rather than a set test. "player" is the word; "botpmc" is a scripted PMC
    /// and contains neither.
    /// </remarks>
    public static bool IsPlayerSpawn(MapFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return feature.Detail is { Length: > 0 } categories &&
            categories.Contains("player", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Flat distance in metres, ignoring height, because a map is flat.</summary>
    /// <remarks>
    /// Height is deliberately left out. Two points a hundred metres apart on the picture are a
    /// hundred metres apart to somebody reading it, and including a thirty metre height
    /// difference would make the number disagree with the map it is printed beside.
    /// </remarks>
    public static double Distance(WorldPosition from, WorldPosition to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    /// <summary>
    /// Which way one point lies from another, as one of the eight compass points.
    /// </summary>
    /// <remarks>
    /// North is negative Z in the game's world, which is the convention the rest of the map
    /// already uses for headings, so the two agree and a player can read a bearing here and a
    /// marker there without them contradicting each other.
    /// </remarks>
    public static string Compass(WorldPosition from, WorldPosition to)
    {
        var dx = to.X - from.X;
        var dz = to.Z - from.Z;
        if (Math.Abs(dx) < 0.001 && Math.Abs(dz) < 0.001)
        {
            return "here";
        }

        var degrees = ((Math.Atan2(dx, -dz) * 180 / Math.PI) + 360) % 360;
        return CompassPoints[(int)Math.Round(degrees / 45) % 8];
    }

    /// <summary>How a distance reads beside a map: whole metres, no false precision.</summary>
    public static string Describe(double metres) =>
        string.Create(CultureInfo.CurrentCulture, $"{metres:F0} m");

    /// <summary>
    /// Whether a spawn is one the player's own side could have started at.
    /// </summary>
    /// <remarks>
    /// An unknown side lists everything, because the alternative is an empty panel on a raid
    /// whose side was never established, and an empty panel reads as a broken feature.
    /// </remarks>
    private static bool Matches(MapFeatureFaction spawn, MapFeatureFaction wanted) =>
        wanted == MapFeatureFaction.Unknown ||
        spawn == MapFeatureFaction.Shared ||
        spawn == MapFeatureFaction.Unknown ||
        spawn == wanted;
}
