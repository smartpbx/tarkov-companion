using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// [#929] Which things on the Raid plan a right-click is for, rather than the map under them.
/// </summary>
/// <remarks>
/// "i still couldnt ping the map when i died from a scav raid but my friends were still in."
/// The pings themselves went out; the right-click never made one. The renderer treated a press on
/// any drawn object as a press on that object, and this page gives most of them no right-click
/// meaning, so the press did nothing. Measured over a 24 by 14 grid of the Customs plan after a
/// raid, 57 of 336 presses were swallowed that way: 46 inside the modelled-traffic circles, which
/// are drawn by default and centred on the places a squad fights, 11 on extracts and transits.
/// Squadmates' markers, their trails and the group route swallowed more wherever they were.
///
/// So only what a right-click does something to is hit: our own marks and lines (their menus),
/// the group's pings and waypoints (removal), and an objective's pin when hand-done is kept (#571).
/// An objective's area is not its pin; a press inside a quest zone pings it.
///
/// #938: only the player's own objective pins. A squadmate's pin carries the same
/// <c>quest:{objectiveId}:…</c> id, and taking the press for it pinged nothing and toggled an
/// objective the player does not have, or quietly un-marked one they had marked done by hand.
/// </remarks>
public sealed partial class RaidCockpitViewModel
{
    private bool TakesRightClick(MapSceneObject item) =>
        TakesRightClick(item, _groupSession is not null, _handDone is not null, id => OwnDrawing(id) is not null, IsSquadObjective);

    /// <summary>#938: whether this scene object is one of a squadmate's objective pins or zones.</summary>
    private bool IsSquadObjective(MapSceneObjectId id) => _squadScene.Objects.Any(item => item.Id == id);

    internal static bool TakesRightClick(
        MapSceneObject item,
        bool hasGroup,
        bool keepsHandDone,
        Func<MapSceneObjectId, bool> isOwnDrawing,
        Func<MapSceneObjectId, bool>? isSquadObjective = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(isOwnDrawing);
        var id = item.Id;
        if (TryParseMarkId(id, out _) || isOwnDrawing(id))
        {
            return true;
        }

        if (hasGroup &&
            (id.Value.StartsWith(GroupWaypointPrefix, StringComparison.Ordinal) ||
             id.Value.StartsWith(GroupPingPrefix, StringComparison.Ordinal)))
        {
            return true;
        }

        return keepsHandDone &&
            item.Geometry.Kind == MapSceneGeometryKind.Point &&
            TryParseObjectiveId(id, out _) &&
            isSquadObjective?.Invoke(id) != true;
    }
}
