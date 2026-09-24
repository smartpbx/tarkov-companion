using TarkovCompanion.App.Localization;
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
        _raid.ClockCountsDown,
        _raid.TimeLeftDetail);

    /// <remarks>[#314] Whether the clock counts down is passed in, not read off the words ("… left"),
    /// which change with the interface language.</remarks>
    internal static string DescribeExtractClock(bool isInRaid, string clock, bool countsDown, string detail)
    {
        if (!isInRaid)
        {
            return string.Empty;
        }

        return countsDown && clock.Length > 0
            ? $"{clock} · {detail}"
            : RaidText.TimeLeftUnknown(detail);
    }
}
