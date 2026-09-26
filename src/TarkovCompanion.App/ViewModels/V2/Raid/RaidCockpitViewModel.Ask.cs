using TarkovCompanion.Application.Services.Ask;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

public sealed partial class RaidCockpitViewModel
{
    /// <summary>
    /// [#712 2-5] The Raid map's exits as the Ask box answers "best extract from here": the same
    /// list, distances and "offered" marks the extract panel and the Now panel show.
    /// </summary>
    /// <remarks>
    /// Null until a map with exits is open. The side and area come from the situation (ADR 0022);
    /// distances are from the last screenshot's position and are straight lines, as the panel says.
    /// </remarks>
    internal AskRaidSnapshot? AskSnapshot()
    {
        var exits = _map.NearbyExits;
        if (_map.SelectedLocation is not { } location || exits.Count == 0)
        {
            return null;
        }

        var situation = _situation?.Current;
        var position = _map.PlayerPosition;
        return new(
            location.Name,
            situation?.Side?.Value ?? SituationSide.Unknown,
            position?.Timestamp,
            position is null ? null : situation?.You?.AreaName,
            [.. exits.Select(exit => new AskExit(
                exit.Name,
                exit.HasKnownPosition ? exit.MetresFromPlayer : null,
                exit.Bearing,
                exit.WasOffered,
                exit.IsTransit,
                _map.ExtractRequirementsFor(exit.Name)))]);
    }
}
