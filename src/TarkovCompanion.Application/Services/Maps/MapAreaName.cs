using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// Which building's floor the player is standing on.
/// </summary>
/// <remarks>
/// Reported as wanting the separate smaller maps some buildings have. The catalog does not
/// publish separate maps, and it publishes something better: a floor is a list of extents, and
/// an extent is a height band and a set of named rectangles. Customs alone has eighteen of
/// them, "dorms", "boiler", "big red 2nd", "oilrig &amp; panda", each with its own heights.
///
/// Which is why a floor number alone is not an answer on those maps. The second floor of dorms
/// is 2.7 to 6.5 metres and the second floor of big red starts at 5.7, so "2nd Floor" means
/// two different heights depending on which building you are in. The floor chosen from a
/// player's height has always taken the rectangles into account; it simply never said which
/// one it matched, and the name has been parsed and thrown away since the parser was written.
/// </remarks>
public static class MapAreaName
{
    /// <summary>
    /// The named area on this floor that contains the position, or null.
    /// </summary>
    /// <remarks>
    /// An extent whose height band also contains the position is preferred, because a building
    /// footprint repeats on every floor it has and only one of them is the floor being stood
    /// on. Where no band matches, the footprint still names the building, which is the right
    /// answer for somebody looking at a floor they are not on.
    ///
    /// A floor with no named rectangles returns nothing rather than the floor's own name. A
    /// label that repeats what the chooser above it already says is noise.
    /// </remarks>
    public static string? Describe(MapFloorDefinition? floor, WorldPosition position)
    {
        if (floor is null)
        {
            return null;
        }

        return Named(floor, position, requireHeight: true) ?? Named(floor, position, requireHeight: false);
    }

    private static string? Named(MapFloorDefinition floor, WorldPosition position, bool requireHeight)
    {
        foreach (var extent in floor.Extents)
        {
            if (requireHeight && !InHeight(extent, position))
            {
                continue;
            }

            foreach (var bounds in extent.Bounds)
            {
                if (bounds.Description is { Length: > 0 } description && bounds.Contains(position.X, position.Z))
                {
                    return description;
                }
            }
        }

        return null;
    }

    private static bool InHeight(MapLayerExtent extent, WorldPosition position) =>
        (extent.MinimumHeight is null || position.Y >= extent.MinimumHeight) &&
        (extent.MaximumHeight is null || position.Y < extent.MaximumHeight);
}
