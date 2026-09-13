using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>One place near the player where the game spawns loot.</summary>
/// <param name="Name">What sort of thing it is, or what can be found in it.</param>
/// <param name="Metres">How far it is from the player.</param>
/// <param name="Bearing">Which way it lies, as a compass point.</param>
public sealed record NearbyLoot(string Name, double Metres, string Bearing);

/// <summary>
/// What the map says is worth looking in, near where the player is standing.
/// </summary>
/// <remarks>
/// <para>
/// Every loot position on every map has been synced to disk since the first refresh and read
/// by nothing. It is not drawn on the map and should not be: Woods alone has 815 of them and a
/// map wearing all of them answers no question at all. The question they do answer is "what is
/// near me", which is a short list rather than a layer.
/// </para>
/// <para>
/// These are places loot <em>can</em> be. The feed publishes where the game puts containers and
/// what can spawn in each; nothing anywhere knows what is in one this raid. Whatever shows this
/// has to say so, or it reads as a loot radar and is neither that nor honest about not being
/// one.
/// </para>
/// </remarks>
public static class LootProximity
{
    /// <summary>How far counts as near, in metres.</summary>
    /// <remarks>
    /// Fifty. A container a hundred metres away is not "near you" in a game where a hundred
    /// metres can be a building, a fence and somebody with a rifle.
    /// </remarks>
    public const double DefaultRadiusMetres = 50;

    /// <summary>The most worth listing, because a longer list is not read mid-raid.</summary>
    private const int Maximum = 6;

    public static IReadOnlyList<NearbyLoot> Near(
        IReadOnlyList<MapFeature> features,
        WorldPosition player,
        double radiusMetres = DefaultRadiusMetres)
    {
        ArgumentNullException.ThrowIfNull(features);
        var found = new List<NearbyLoot>();
        foreach (var feature in features)
        {
            if (feature.Kind != MapFeatureKind.Loot)
            {
                continue;
            }

            var metres = SpawnProximity.Distance(player, feature.Position);
            if (metres > radiusMetres)
            {
                continue;
            }

            found.Add(new(feature.Name, metres, SpawnProximity.Compass(player, feature.Position)));
        }

        // Nearest first, and one line per distinct thing: forty drawers in a barracks is one
        // fact about that barracks, not forty facts.
        return
        [
            .. found
                .OrderBy(loot => loot.Metres)
                .DistinctBy(loot => loot.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(Maximum),
        ];
    }
}
