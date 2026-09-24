using TarkovCompanion.Application.Services.Raids;

namespace TarkovCompanion.App.Localization;

/// <summary>The raid clock's words (#314): RaidTimer says which way it runs and how it is known.</summary>
public static partial class RaidText
{
    public static string ClockSetByHand => UiText.Get("Raid.Clock.SetByHand");
    public static string ClockNoRaid => UiText.Get("Raid.Clock.NoRaid");

    /// <summary>"20:56 left", "14:03 elapsed", or nothing when neither is known.</summary>
    public static string ClockText(RaidClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock.Direction switch
        {
            RaidClockDirection.Left => UiText.Format("Raid.Clock.Left", clock.Minutes),
            RaidClockDirection.Elapsed => UiText.Format("Raid.Clock.Elapsed", clock.Minutes),
            _ => string.Empty,
        };
    }

    /// <summary>Which source the time came from, so a count is not read as a reading.</summary>
    public static string ClockBasis(RaidTimeBasis basis) => basis switch
    {
        RaidTimeBasis.Observed => UiText.Get("Raid.Clock.Observed"),
        RaidTimeBasis.Counted => UiText.Get("Raid.Clock.Counted"),
        _ => UiText.Get("Raid.Clock.UnknownBasis"),
    };
}
