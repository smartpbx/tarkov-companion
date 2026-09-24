using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>
/// #289: the "Squad" marks waiting for the relay, for the Team list's "Queued" rows.
/// </summary>
public sealed partial class RaidCockpitViewModel
{
    /// <summary>Our "Squad" marks whose send failed and which go out when the relay is back, oldest first.</summary>
    internal IReadOnlyList<RaidMark> QueuedMarks
    {
        get
        {
            if (_groupForwarder is not { } forwarder)
            {
                return [];
            }

            var marks = _marks.Marks.ToDictionary(mark => mark.Id);
            return [.. forwarder.QueuedIds.Where(marks.ContainsKey).Select(id => marks[id])];
        }
    }
}
