using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// Turns a map's fixed features into markers the map can draw.
/// </summary>
/// <remarks>
/// Extracts are the point of this. A player deciding where to leave from wants to see the
/// exits and which side may use them, and that is the single thing a companion map is for
/// once the raid is running. Spawns and locked doors are useful before a raid and noise during
/// one, so they are off by default and behind their own toggles.
///
/// Loot containers and hazards are in the same data and are deliberately left out. A map
/// covered in several hundred markers answers no question quickly, and quickly is the only way
/// this panel is ever read. Woods alone has 815 loot positions; the question those answer is
/// "what is near me", which the column beside the map answers without drawing any of them.
/// </remarks>
public static class MapFeatureProjection
{
    /// <summary>
    /// Projects features through the variant's own transform, dropping what cannot be placed.
    /// </summary>
    /// <remarks>
    /// A feature that will not project is silently omitted rather than placed at the origin,
    /// because a marker in the wrong place is worse than a missing one: it sends somebody
    /// somewhere.
    /// </remarks>
    public static IReadOnlyList<MapOverlayElement> Project(
        MapVariant variant,
        IReadOnlyList<MapFeature> features)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(features);
        if (variant.Transform is not { IsValid: true } transform)
        {
            return [];
        }

        // One exit published once per faction is one exit. Upstream ships it twice, less than a
        // metre apart, and the map drew both: two labels reading "RUAF Roadblock" above and
        // below a single disc, because at map zoom the two discs are the same pixel.
        // One marker per spawn area rather than per spawn point. A group of players arriving
        // together occupies a dozen positions within a few metres, and drawn one each that is
        // 273 markers on Customs and 400 on Streets, which buries the exits.
        var elements = new List<MapOverlayElement>(features.Count);
        foreach (var feature in SpawnGrouping.Collapse(MapFeatureMerge.Collapse(features)))
        {
            // Loot never becomes a marker. Eight hundred of them would bury the exits, and
            // falling through to the extract layer -- which is what the layer mapping below
            // does with anything it does not recognise -- would draw a duffle bag as a way out.
            if (feature.Kind == MapFeatureKind.Loot || !transform.TryProject(feature.Position, out var point))
            {
                continue;
            }

            elements.Add(new(
                LayerFor(feature.Kind),
                point,
                Describe(feature),
                RotationDegrees: 0,
                // Extracts read larger than spawns because they are what somebody is looking
                // for when it matters; a spawn is context.
                SizePercent: feature.Kind is MapFeatureKind.Extract or MapFeatureKind.Transit ? 130
                    : feature.Kind == MapFeatureKind.Switch ? 70
                    : 80,
                // The height is the floor filter's input, and a feature sits at one height
                // rather than spanning a range.
                MinimumHeight: feature.Position.Y,
                MaximumHeight: feature.Position.Y)
            {
                Faction = feature.Side,
                Detail = feature.Detail,
                CatalogId = feature.CatalogId,
                Switch = feature.Switch,
                ExtractRequirements = feature.ExtractRequirements,
            });
        }

        return elements;
    }

    private static MapOverlayKind LayerFor(MapFeatureKind kind) => kind switch
    {
        MapFeatureKind.Spawn => MapOverlayKind.Spawns,
        MapFeatureKind.Lock => MapOverlayKind.Keys,
        MapFeatureKind.Switch => MapOverlayKind.Switches,
        _ => MapOverlayKind.Extracts,
    };

    /// <summary>
    /// Labels a marker with what somebody needs to read at a glance, and nothing else.
    /// </summary>
    /// <summary>
    /// What a marker is called on the map.
    /// </summary>
    /// <remarks>
    /// The side used to be appended in words, so an exit read "Dorms V-Ex (pmc)" and a spawn
    /// read "ZoneScav · Pmc, Scav". That was the only way to tell a scav exit from a PMC one,
    /// because every marker was drawn identically, and it made the longest labels on the map
    /// longer still. They collide: names sit at a fixed offset under their disc, so on Customs
    /// "Sniper Roadblock (scav)" lands across the marker above it and the label below it.
    ///
    /// The disc colour and the glyph now carry the side, so the words are saying a second time
    /// what the shape already said, at the cost of the width that makes them unreadable. The
    /// full description is still one hover away.
    ///
    /// This does not fix the collisions, which need real de-collision rather than shorter
    /// text. It removes the part of them that was redundant.
    /// </remarks>
    private static string Describe(MapFeature feature) => feature.Kind switch
    {
        MapFeatureKind.Transit => $"{feature.Name} →",
        _ => feature.Name,
    };
}
