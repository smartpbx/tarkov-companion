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
    bool WasOffered)
{
    /// <summary>Whether the catalog supplies a trusted position for this row.</summary>
    /// <remarks>
    /// False only for an EXFIL row the screenshot proved was offered but neither the primary
    /// catalog nor a reviewed supplement could place. It stays in the list and never becomes a
    /// marker at the map origin.
    /// </remarks>
    public bool HasKnownPosition { get; init; } = true;
}

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
    /// <param name="definitions">
    /// Position-optional extract definitions. These keep a reviewed exit listable, with its
    /// faction intact, when no coordinate exists and therefore no <see cref="MapFeature"/> can
    /// represent it.
    /// </param>
    public static IReadOnlyList<NearbyExtract> Near(
        IReadOnlyList<MapFeature> features,
        WorldPosition? player,
        MapFeatureFaction side,
        IReadOnlyCollection<string>? offered = null,
        int limit = DefaultLimit,
        IReadOnlyList<MapExtract>? definitions = null)
    {
        ArgumentNullException.ThrowIfNull(features);
        var confirmed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in offered ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name) && Identity(name) is { Length: > 0 } identity)
            {
                confirmed.TryAdd(identity, name.Trim());
            }
        }

        var found = new List<NearbyExtract>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var feature in features)
        {
            if (feature.Kind is not (MapFeatureKind.Extract or MapFeatureKind.Transit))
            {
                continue;
            }

            var identity = Identity(feature.Name);
            known.Add(identity);
            if (!CanBeTaken(feature.Side, side))
            {
                continue;
            }

            found.Add(new(
                feature.Name,
                feature.Side,
                player is { } from ? SpawnProximity.Distance(from, feature.Position) : null,
                player is { } at ? SpawnProximity.Compass(at, feature.Position) : string.Empty,
                feature.Kind == MapFeatureKind.Transit,
                confirmed.ContainsKey(identity)));
        }

        // MapFeature deliberately requires a real world position, while MapExtract does not.
        // Merge definitions after features so a positioned marker remains the richer row and a
        // positionless reviewed exit still retains its supported side. Grouping is required for
        // shared extracts: the primary feed can publish the same name once per faction.
        foreach (var definition in (definitions ?? [])
            .Where(definition => !string.IsNullOrWhiteSpace(definition.Name))
            .GroupBy(definition => Identity(definition.Name), StringComparer.Ordinal))
        {
            if (definition.Key.Length == 0 || !known.Add(definition.Key))
            {
                continue;
            }

            var candidate = definition.First();
            var definitionSide = CombineSides(definition.Select(value => SideFromConditions(value.Conditions)));
            if (!CanBeTaken(definitionSide, side))
            {
                continue;
            }

            found.Add(new(
                candidate.Name,
                definitionSide,
                null,
                string.Empty,
                IsTransit: false,
                WasOffered: confirmed.ContainsKey(definition.Key))
            {
                HasKnownPosition = false,
            });
        }

        // The game can add an extract before the structured catalog catches up. A screenshot's
        // EXFIL row is still useful evidence that the exit was offered, so retain any name the
        // static features and position-optional definitions could not represent. Looking in the
        // complete known set matters: a known exit filtered out for the other side must not come
        // back as an unclassified catalog gap.
        foreach (var (identity, name) in confirmed)
        {
            if (known.Contains(identity))
            {
                continue;
            }

            found.Add(new(
                name.Trim(),
                MapFeatureFaction.Unknown,
                null,
                string.Empty,
                IsTransit: false,
                WasOffered: true)
            {
                HasKnownPosition = false,
            });
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
                .ThenBy(exit => exit.HasKnownPosition)
                .ThenBy(exit => exit.IsTransit)
                .ThenBy(exit => exit.MetresFromPlayer ?? double.MaxValue)
                .ThenBy(exit => exit.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit),
        ];
    }

    /// <summary>Identity shared with the supplement merge: labels differ in punctuation.</summary>
    private static string Identity(string value) => string.Concat(
        value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant));

    /// <summary>Reads the faction prefix emitted by the map-definition cache.</summary>
    private static MapFeatureFaction SideFromConditions(string? conditions)
    {
        if (string.IsNullOrWhiteSpace(conditions))
        {
            return MapFeatureFaction.Unknown;
        }

        if (conditions.Contains("Either side", StringComparison.OrdinalIgnoreCase))
        {
            return MapFeatureFaction.Shared;
        }

        if (conditions.Contains("PMC only", StringComparison.OrdinalIgnoreCase))
        {
            return MapFeatureFaction.Pmc;
        }

        return conditions.Contains("Scav only", StringComparison.OrdinalIgnoreCase)
            ? MapFeatureFaction.Scav
            : MapFeatureFaction.Unknown;
    }

    private static MapFeatureFaction CombineSides(IEnumerable<MapFeatureFaction> sides)
    {
        var known = sides.Where(side => side != MapFeatureFaction.Unknown).Distinct().ToArray();
        if (known.Contains(MapFeatureFaction.Shared) ||
            known.Contains(MapFeatureFaction.Pmc) && known.Contains(MapFeatureFaction.Scav))
        {
            return MapFeatureFaction.Shared;
        }

        return known.Length == 1 ? known[0] : MapFeatureFaction.Unknown;
    }

    /// <summary>What the compact extract row should say about location.</summary>
    public static string DescribeLocation(NearbyExtract exit)
    {
        ArgumentNullException.ThrowIfNull(exit);
        if (!exit.HasKnownPosition)
        {
            return "Location unavailable";
        }

        return exit.MetresFromPlayer is { } metres
            ? $"{SpawnProximity.Describe(metres)} {exit.Bearing}".TrimEnd()
            : string.Empty;
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
