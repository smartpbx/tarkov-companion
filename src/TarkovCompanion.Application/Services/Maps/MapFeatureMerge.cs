using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// Draws one marker for an exit upstream publishes once per faction.
/// </summary>
/// <remarks>
/// <para>
/// Seen on Customs in the page gallery: two labels reading "RUAF Roadblock", one above and one
/// below a single disc. There is one disc because there are two, 0.86 m apart, which at 23%
/// zoom is the same pixel — and two labels because the layout will not stack two names on one
/// spot, so it pushes one to the row above.
/// </para>
/// <para>
/// The cause is upstream's shape, which the refresh already records a remark about: a shared
/// exit is published once per faction. Customs carries <c>RUAF Roadblock</c> (pmc) and
/// <c>RUAF Roadblock_scav</c> (scav); Shoreline carries <c>Road to Customs</c> and <c>Scav Road
/// to Customs</c>; Lighthouse carries <c>Shorl_free</c> and <c>Shorl_free_scav</c>.
/// </para>
/// <para>
/// Position alone cannot decide this, and the whole feed says so. Across all seventeen maps and
/// a hundred and fifty-two exits there are five pairs closer than three metres, and two of them
/// are genuinely different exits: Customs puts <c>Old Road Gate</c> (scav) 1.03 m from <c>Dorms
/// V-Ex</c> (pmc), and Streets puts <c>scav_e2</c> 0 m from <c>scav_e6</c>. Merging on distance
/// would tell a PMC they can leave through a scav exit, which is worse than drawing two labels.
/// </para>
/// <para>
/// So the name has to agree as well, under exactly the qualifiers upstream actually uses, and
/// the position is what corroborates it. Three merges, two refusals, and every one of the five
/// is a real pair from the live feed rather than an invented case.
/// </para>
/// </remarks>
public static class MapFeatureMerge
{
    /// <summary>
    /// How close two entries must be to be the same doorway, in metres.
    /// </summary>
    /// <remarks>
    /// The real pairs are 0.86 m and closer; the nearest genuinely different exits are 1.03 m
    /// apart and are refused on their names rather than on this. Three is room for the feed to
    /// move a little without being a number doing any of the deciding.
    /// </remarks>
    private const double SamePlaceMetres = 3;

    /// <summary>The qualifiers upstream appends or prepends to mark the scav copy of an exit.</summary>
    private static readonly string[] Suffixes = ["_scav", "_pmc"];

    private static readonly string[] Prefixes = ["scav ", "pmc "];

    /// <summary>
    /// Collapses each faction pair into one exit, and leaves everything else exactly as it was.
    /// </summary>
    /// <remarks>
    /// The survivor is the unqualified name, because that is the one written on the door, and
    /// its side becomes Shared — which the map already draws with its own colour and glyph and
    /// the panel already reads as "Either". Everything else about it is the first entry's, so a
    /// merged exit keeps the conditions upstream gave the PMC copy.
    /// </remarks>
    public static IReadOnlyList<MapFeature> Collapse(IReadOnlyList<MapFeature> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        var merged = new List<MapFeature>(features.Count);
        var taken = new bool[features.Count];
        for (var index = 0; index < features.Count; index++)
        {
            if (taken[index])
            {
                continue;
            }

            var feature = features[index];
            for (var other = index + 1; other < features.Count; other++)
            {
                if (taken[other] || !IsSameExit(feature, features[other]))
                {
                    continue;
                }

                taken[other] = true;
                feature = feature with
                {
                    Name = Shorter(feature.Name, features[other].Name),
                    // Written as the word the feed itself uses for an exit either side may
                    // take, so nothing downstream has to learn a second spelling.
                    Faction = "shared",
                };
            }

            merged.Add(feature);
        }

        return merged;
    }

    /// <summary>Whether two entries are the same doorway published twice.</summary>
    private static bool IsSameExit(MapFeature first, MapFeature second) =>
        first.Kind == second.Kind &&
        first.Kind is MapFeatureKind.Extract or MapFeatureKind.Transit &&
        first.Side != second.Side &&
        Within(first.Position, second.Position) &&
        NamesTheSameExit(first.Name, second.Name);

    private static bool Within(WorldPosition first, WorldPosition second)
    {
        var x = first.X - second.X;
        var z = first.Z - second.Z;
        return (x * x) + (z * z) <= SamePlaceMetres * SamePlaceMetres;
    }

    /// <summary>
    /// Whether the two name the same exit, once a faction qualifier is off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Identical names count, and that is the commonest case rather than a corner of it.
    /// <c>$.data.maps.*.extracts[*].name</c> is one of the paths the feed publishes a
    /// translation for, and the client applies it, so the scav copy of an exit arrives with
    /// the same display name as the PMC one: the raw <c>RUAF Roadblock</c> and <c>RUAF
    /// Roadblock_scav</c> both reach this as "RUAF Roadblock".
    /// </para>
    /// <para>
    /// I wrote this refusing an exact match, on the raw names I had measured from the feed
    /// rather than the translated ones the application actually reads, and the map came back
    /// byte-identical — the pair it was written for was the pair it turned away.
    /// </para>
    /// <para>
    /// The qualifier stripping stays for the forms that survive untranslated. Lighthouse's
    /// <c>Shorl_free</c> is an internal token rather than a name and has nothing to translate
    /// to, so its scav copy still arrives with the suffix on it.
    /// </para>
    /// <para>
    /// Only the exact qualifiers the feed uses. A looser test — one name containing the other
    /// — would merge "Warehouse 4" into "Warehouse 4 Gate" the moment upstream published one.
    /// </para>
    /// </remarks>
    private static bool NamesTheSameExit(string first, string second) =>
        string.Equals(Unqualified(first), Unqualified(second), StringComparison.OrdinalIgnoreCase);

    private static string Unqualified(string name)
    {
        var trimmed = name.Trim();
        foreach (var suffix in Suffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^suffix.Length].Trim();
            }
        }

        foreach (var prefix in Prefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[prefix.Length..].Trim();
            }
        }

        return trimmed;
    }

    private static string Shorter(string first, string second) =>
        first.Length <= second.Length ? first : second;
}
