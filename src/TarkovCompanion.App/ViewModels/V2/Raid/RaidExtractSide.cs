using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>Whose extract this is, for the raid being played (#873).</summary>
/// <remarks>
/// Reported on build 63 as "it shows both types of extracts, when it should only show the pmc ones
/// or scav ones depending on your raid". V1's map has filtered exits by side since #91
/// (<c>MapViewModel.CanBeTaken</c>, "if i am a scav, i should only see scav extracts"); the V2 Raid
/// map built its scene from the render model's unfiltered overlay list and never applied that rule,
/// so a PMC raid drew every Scav exit beside its own and listed them in Extract options.
///
/// Decided by the catalog's faction enum and the raid's side code, never by display text: the side
/// words are translated (#314) and a comparison against "PMC" would silently stop matching.
/// </remarks>
public static class RaidExtractSide
{
    /// <summary>The raid's side as the raid state keeps it ("pmc"/"scav", any case), or unknown.</summary>
    public static MapFeatureFaction Of(string? side) => side?.Trim().ToLowerInvariant() switch
    {
        "pmc" => MapFeatureFaction.Pmc,
        "scav" => MapFeatureFaction.Scav,
        _ => MapFeatureFaction.Unknown,
    };

    /// <summary>
    /// False only for an exit the catalog states is for the other side, in a raid whose side is
    /// known. Shared exits, transits and exits the feed says nothing about stay: none of them is
    /// known to be unusable.
    /// </summary>
    public static bool CanUse(MapFeatureFaction extract, MapFeatureFaction raid) =>
        raid is not (MapFeatureFaction.Pmc or MapFeatureFaction.Scav) ||
        extract is not (MapFeatureFaction.Pmc or MapFeatureFaction.Scav) ||
        extract == raid;

    /// <summary>
    /// Whether the Raid map draws a catalog element in this raid: V1's side rule
    /// (<c>MapViewModel.CanBeTaken</c>) for the V2 scene. The other side's exits go, and a scav is
    /// shown no spawns at all (#257: a scav joins mid-raid, and spawn markers bury the exits).
    /// </summary>
    public static bool KeepOnMap(MapOverlayKind layer, MapFeatureFaction faction, MapFeatureFaction raid) => layer switch
    {
        MapOverlayKind.Extracts => CanUse(faction, raid),
        MapOverlayKind.Spawns => raid != MapFeatureFaction.Scav,
        _ => true,
    };

    /// <summary>
    /// An exit for one side only, shown while the raid's side is unknown: it may or may not be
    /// the player's, so it is drawn dimmed and labelled rather than hidden or passed off as theirs.
    /// </summary>
    public static bool IsUnsure(MapFeatureFaction extract, MapFeatureFaction raid) =>
        raid is not (MapFeatureFaction.Pmc or MapFeatureFaction.Scav) &&
        extract is (MapFeatureFaction.Pmc or MapFeatureFaction.Scav);

    /// <summary>
    /// The exits a suggested route may end at, and whether that choice had to assume a PMC raid.
    /// </summary>
    /// <remarks>
    /// Routes used to drop Scav exits whenever the side was not "scav", so a raid whose side was
    /// never read got PMC routes with nothing saying so (#875). With the side known this is
    /// <see cref="CanUse"/>. With it unknown, only exits both sides can take are routed to; a map
    /// with none of those falls back to PMC exits and says it assumed so, rather than routing a
    /// player who may be a scav to an exit that will not open for them without a word.
    /// </remarks>
    public static (IReadOnlyList<MapOverlayElement> Targets, bool AssumesPmc) RouteTargets(
        IEnumerable<MapOverlayElement> extracts,
        MapFeatureFaction raid)
    {
        var all = extracts as IReadOnlyCollection<MapOverlayElement> ?? [.. extracts];
        if (raid is MapFeatureFaction.Pmc or MapFeatureFaction.Scav)
        {
            return ([.. all.Where(element => CanUse(element.Faction, raid))], false);
        }

        MapOverlayElement[] both = [.. all.Where(element => element.Faction is not (MapFeatureFaction.Pmc or MapFeatureFaction.Scav))];
        return both.Length > 0
            ? (both, false)
            : ([.. all.Where(element => CanUse(element.Faction, MapFeatureFaction.Pmc))], true);
    }
}
