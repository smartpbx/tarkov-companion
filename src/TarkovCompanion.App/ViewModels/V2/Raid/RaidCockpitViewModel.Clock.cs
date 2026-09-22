using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>The extract card's live raid clock and its visible basis.</summary>
public sealed partial class RaidCockpitViewModel
{
    /// <summary>
    /// One short line for the Extract options header: the countdown and where it came from, or
    /// the honest unknown state and the two ways to supply it.
    /// </summary>
    public string ExtractClockSummary => DescribeExtractClock(
        _stateStore.Current.Raid.State == RaidLifecycleState.InRaid,
        _raid.Clock,
        _raid.TimeLeftDetail);

    internal static string DescribeExtractClock(bool isInRaid, string clock, string detail)
    {
        if (!isInRaid)
        {
            return string.Empty;
        }

        return clock.EndsWith(" left", StringComparison.Ordinal)
            ? $"{clock} · {detail}"
            : $"Time left unknown · {detail}";
    }
}
