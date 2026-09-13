using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>One way out of this map, and how far it is.</summary>
/// <param name="Name">As the catalog names it, which is what the extract panel prints.</param>
/// <param name="Side">Who can use it, where the feed says.</param>
/// <param name="MetresFromPlayer">
/// Straight-line distance, which is a floor and not a route, or null before the player has
/// been placed.
/// </param>
/// <param name="Bearing">Which way, in the eight points somebody can act on, or empty.</param>
/// <param name="IsTransit">Whether it leads to another map rather than out of the raid.</param>
/// <param name="WasOffered">Whether a scan of the extract screen named this one.</param>
public sealed record NearbyExtract(
    string Name,
    MapFeatureFaction Side,
    double? MetresFromPlayer,
    string Bearing,
    bool IsTransit,
    bool WasOffered);

/// <summary>
/// The exits nearest the player, whether or not anything has been recognised.
/// </summary>
/// <remarks>
/// The Extracts readout said "None observed" until a screenshot of the extract panel was
/// matched, and on a real Woods screen the facts file records one exit in five matching. So the
/// panel that answers "how do I get out" was usually empty, while the map beside it already
/// held every exit's position, name and faction.
///
/// This reverses which of the two is the foundation. The catalog is what the list is built
/// from, because it is always there; a recognised panel decorates the rows it confirms. An OCR
/// pass that reads one name out of eight then improves a list of eight rather than producing a
/// list of one.
///
/// Straight lines, and the distance says so wherever it is shown. A route through Customs is
/// not the length of the line across it, and presenting one as the other would be the kind of
/// false precision this application is otherwise careful about.
/// </remarks>
public static class ExtractProximity
{
    /// <summary>How many to list, because a map has more exits than anybody reads.</summary>
    private const int DefaultLimit = 6;

    /// <summary>
    /// The exits this player could take, nearest first, transits after the true exits.
    /// </summary>
    /// <param name="features">Every fixed feature of the map.</param>
    /// <param name="player">
    /// Where the player is now, or null before a screenshot has placed them. Without one the
    /// list is still worth having — which exits this side can use is most of the answer — so it
    /// is built and ordered by name, with no distance rather than a distance from the map's
    /// origin, which would be a number that means nothing.
    /// </param>
    /// <param name="side">
    /// The player's own side. An exit stated to be for the other one is left out entirely,
    /// because it is not a worse option, it is not an option — drawing it sends somebody to a
    /// door that will not open.
    /// </param>
    /// <param name="offered">Names a scan of the extract screen matched, if any.</param>
    public static IReadOnlyList<NearbyExtract> Near(
        IReadOnlyList<MapFeature> features,
        WorldPosition? player,
        MapFeatureFaction side,
        IReadOnlyCollection<string>? offered = null,
        int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(features);
        var confirmed = offered is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(offered, StringComparer.OrdinalIgnoreCase);

        var found = new List<NearbyExtract>();
        foreach (var feature in features)
        {
            if (feature.Kind is not (MapFeatureKind.Extract or MapFeatureKind.Transit) ||
                !CanBeTaken(feature.Side, side))
            {
                continue;
            }

            found.Add(new(
                feature.Name,
                feature.Side,
                player is { } from ? SpawnProximity.Distance(from, feature.Position) : null,
                player is { } at ? SpawnProximity.Compass(at, feature.Position) : string.Empty,
                feature.Kind == MapFeatureKind.Transit,
                confirmed.Contains(feature.Name)));
        }

        // Offered first, because a confirmed exit is worth more than a nearer unconfirmed one;
        // then exits before transits, because leaving the raid is the usual question; then by
        // distance, which is the only one of the three that is a measurement. Name last, so a
        // list built before the player has been placed still comes out in a settled order
        // rather than whatever order the catalog happened to be in.
        return
        [
            .. found
                .OrderByDescending(exit => exit.WasOffered)
                .ThenBy(exit => exit.IsTransit)
                .ThenBy(exit => exit.MetresFromPlayer ?? double.MaxValue)
                .ThenBy(exit => exit.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit),
        ];
    }

    /// <summary>
    /// Whether an exit is one this raid could actually use.
    /// </summary>
    /// <remarks>
    /// The same rule the map draws by. A shared exit is for everybody, and one the feed says
    /// nothing about is not known to be unusable, so both stay; only an exit stated to be for
    /// the other side is removed.
    /// </remarks>
    public static bool CanBeTaken(MapFeatureFaction exit, MapFeatureFaction side) =>
        side == MapFeatureFaction.Unknown ||
        exit is MapFeatureFaction.Unknown or MapFeatureFaction.Shared ||
        exit == side;
}
