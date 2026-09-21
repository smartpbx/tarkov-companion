using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.Maps;

/// <summary>
/// Decides when the map should move itself onto the raid's map.
/// </summary>
/// <remarks>
/// <para>
/// The map used to follow only when the raid's <em>map id</em> differed from the one it last
/// followed. That is the wrong question. A player who finishes a Lighthouse raid, opens Shoreline
/// by hand to plan something, and then loads into Lighthouse again is on the same map id as
/// before, so nothing moved and the companion sat on Shoreline for the whole raid: "my map doesn't
/// seem to be auto changing on new map" (#568). A raid that was never reported over made it worse,
/// because every raid after it looked like more of the same one.
/// </para>
/// <para>
/// The question is whether this is a raid the map has not yet been taken to. So it follows once
/// per raid, and again if that raid's map changes; picking another map by hand in the middle of a
/// raid is respected until the next raid begins.
/// </para>
/// </remarks>
public sealed class RaidMapFollow
{
    private string? _followedMapId;
    private Guid? _followedRaidId;

    /// <summary>The map to move to now, or null to leave the map where the player put it.</summary>
    public string? Next(RaidSnapshot raid)
    {
        ArgumentNullException.ThrowIfNull(raid);
        var mapId = raid.MapId;
        if (string.IsNullOrWhiteSpace(mapId) || raid.IsManualMapOverride)
        {
            return null;
        }

        var sameMap = string.Equals(mapId, _followedMapId, StringComparison.OrdinalIgnoreCase);
        // A raid id that is not known yet (still loading) is not a different raid from the one
        // it is about to become, so only a known, different id counts as a new raid.
        var newRaid = raid.RaidId is { } raidId && raidId != _followedRaidId;
        if (sameMap && !newRaid)
        {
            return null;
        }

        _followedMapId = mapId;
        _followedRaidId = raid.RaidId ?? _followedRaidId;
        return mapId;
    }
}
