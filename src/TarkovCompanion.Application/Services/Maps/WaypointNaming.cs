using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.Application.Services.Maps;

/// <summary>
/// Gives a mark the name of the place it was put on.
/// </summary>
/// <remarks>
/// Every waypoint ever dropped from either client sent <c>label: null</c>, so the map showed
/// "1, 2, 3" and the only way to say which one you meant was to point at the screen — which is
/// the one thing a squad on four machines cannot do. The server has stored a label since the
/// day it was written and nothing has ever sent one.
///
/// The name is not asked for. Typing one means alt-tabbing out of a raid to a keyboard, which
/// is the gesture this whole feature exists to avoid, and a typed name would be better only if
/// somebody ever typed it. The map already knows what is where.
///
/// Two sources, one rule: the nearest named thing wins. Catalog features carry exits, transits,
/// locked doors and spawns; the label layer carries the names a player would actually say —
/// Dorms, Big Red, Fortress. Loot is excluded, and that exclusion is the difference between
/// this working and not: Woods alone has 815 loot positions, so one is within range of
/// anywhere, and every mark on the map would be called "Duffle bag".
///
/// Nothing near enough means no name. A mark called after something eighty metres away is
/// worse than a mark called "3", because "3" is not a claim.
/// </remarks>
public static class WaypointNaming
{
    /// <summary>
    /// How far a name may reach.
    /// </summary>
    /// <remarks>
    /// Forty metres is a building. Further starts naming a mark after the thing across the
    /// road from it, and a label layer's position is the middle of a piece of text rather than
    /// the middle of the place, so the slack is already being spent on that.
    /// </remarks>
    public const double WithinMetres = 40;

    /// <summary>
    /// What to call a mark at <paramref name="mark"/>, or null if nothing near enough is named.
    /// </summary>
    public static string? Describe(
        WorldPosition mark,
        IReadOnlyList<MapFeature> features,
        IReadOnlyList<MapCatalogLabel> places)
    {
        ArgumentNullException.ThrowIfNull(features);
        ArgumentNullException.ThrowIfNull(places);

        string? best = null;
        var nearest = WithinMetres;

        foreach (var feature in features)
        {
            if (feature.Kind == MapFeatureKind.Loot || string.IsNullOrWhiteSpace(feature.Name))
            {
                continue;
            }

            var metres = SpawnProximity.Distance(mark, feature.Position);
            if (metres <= nearest)
            {
                nearest = metres;
                best = feature.Name.Trim();
            }
        }

        foreach (var place in places)
        {
            if (string.IsNullOrWhiteSpace(place.Text))
            {
                continue;
            }

            // The catalog's labels are two-dimensional and the second number is world Z, which
            // is what the projection reads it as when it draws them.
            var metres = SpawnProximity.Distance(mark, new(place.Position.X, mark.Y, place.Position.Y));
            if (metres <= nearest)
            {
                nearest = metres;
                best = Tidy(place.Text);
            }
        }

        return best;
    }

    /// <summary>
    /// Flattens a label the map draws across two lines into one a panel can print.
    /// </summary>
    /// <remarks>
    /// Place names are laid out as artwork, so several carry line breaks to sit inside a
    /// building. A waypoint name goes in a list and a tooltip, where a newline is a hole.
    /// </remarks>
    private static string Tidy(string text) => string.Join(
        ' ',
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
