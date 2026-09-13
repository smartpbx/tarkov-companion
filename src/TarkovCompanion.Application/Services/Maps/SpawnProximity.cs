using System.Globalization;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>One player spawn point near where this raid began.</summary>
/// <param name="Name">What the map calls it.</param>
/// <param name="Side">Who starts there: PMC, scav, or either.</param>
/// <param name="Position">Where it is, in world coordinates.</param>
/// <param name="MetresFromStart">How far it is from where this raid began.</param>
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
/// The first screenshot of a raid is taken where the player spawned, so the player spawn points
/// near it are where everybody else began. In the first ninety seconds of a raid that is the
/// most useful thing a map can say, and every one of those points has been synced to disk since
/// the first data refresh without ever being put to this use.
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
/// The anchor is the first screenshot, not the spawn. Somebody who runs for a minute before
/// taking one has an anchor a minute from where they started, so nothing here calls it "your
/// spawn"; the panel says what it is anchored to and lets the player judge it.
/// </para>
/// </remarks>
public static class SpawnProximity
{
    /// <summary>How far from the start still counts as "near", in metres.</summary>
    /// <remarks>
    /// A hundred and fifty metres. Far enough to cover the spawns that share a corner of the
    /// map with you, close enough that the list stays short enough to read while the raid is
    /// starting, which is the only moment it is ever read.
    /// </remarks>
    public const double DefaultRadiusMetres = 150;

    /// <summary>The most worth listing, because a longer list is not read at all.</summary>
    private const int Maximum = 8;

    /// <summary>
    /// The player spawns within a radius of where this raid began, nearest first.
    /// </summary>
    /// <param name="features">Every fixed feature of the map.</param>
    /// <param name="anchor">Where the raid began, as far as the screenshots know.</param>
    /// <param name="player">Where the player is now, for distance and bearing. Optional.</param>
    /// <param name="side">
    /// The player's own side, so the list answers "where did the others start" rather than
    /// "where could anybody start". Unknown lists both.
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
        var found = new List<NearbySpawn>();
        foreach (var feature in features)
        {
            if (feature.Kind != MapFeatureKind.Spawn || !IsPlayerSpawn(feature) || !Matches(feature.Side, side))
            {
                continue;
            }

            var fromStart = Distance(anchor, feature.Position);
            if (fromStart > radiusMetres)
            {
                continue;
            }

            found.Add(new(
                feature.Name,
                feature.Side,
                feature.Position,
                fromStart,
                player is { } here ? Distance(here, feature.Position) : null,
                player is { } from ? Compass(from, feature.Position) : null));
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

        var degrees = (Math.Atan2(dx, -dz) * 180 / Math.PI + 360) % 360;
        var index = (int)Math.Round(degrees / 45) % 8;
        return (string[])["N", "NE", "E", "SE", "S", "SW", "W", "NW"][index];
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
