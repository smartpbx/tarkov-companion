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
/// this panel is ever read.
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

        var elements = new List<MapOverlayElement>(features.Count);
        foreach (var feature in features)
        {
            if (!transform.TryProject(feature.Position, out var point))
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
                SizePercent: feature.Kind is MapFeatureKind.Extract or MapFeatureKind.Transit ? 130 : 80,
                // The height is the floor filter's input, and a feature sits at one height
                // rather than spanning a range.
                MinimumHeight: feature.Position.Y,
                MaximumHeight: feature.Position.Y)
            {
                Faction = feature.Side,
            });
        }

        return elements;
    }

    private static MapOverlayKind LayerFor(MapFeatureKind kind) => kind switch
    {
        MapFeatureKind.Spawn => MapOverlayKind.Spawns,
        MapFeatureKind.Lock => MapOverlayKind.Keys,
        _ => MapOverlayKind.Extracts,
    };

    /// <summary>
    /// Labels a marker with what somebody needs to read at a glance, and nothing else.
    /// </summary>
    private static string Describe(MapFeature feature) => feature.Kind switch
    {
        MapFeatureKind.Extract when feature.Faction is { Length: > 0 } faction
            => $"{feature.Name} ({faction})",
        MapFeatureKind.Transit => $"{feature.Name} →",
        MapFeatureKind.Spawn when feature.Faction is { Length: > 0 } sides => $"{feature.Name} · {sides}",
        _ => feature.Name,
    };
}
