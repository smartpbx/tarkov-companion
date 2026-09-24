using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.App.Localization;

/// <summary>How soon somebody from another spawn could be here (#314): SpawnReach gives the band's two rungs.</summary>
public static partial class RaidText
{
    /// <summary>"30–45 s away", "45 s – 3 min away", "about 10 s away", or nothing without a band.</summary>
    public static string SpawnReach(SpawnReachBand? band)
    {
        if (band is null)
        {
            return string.Empty;
        }

        return band.IsSingle
            ? UiText.Format("Raid.SpawnReach.About", ReachLabel(band.SlowestSeconds))
            : UiText.Format("Raid.SpawnReach.Away", ReachRange(band.FastestSeconds, band.SlowestSeconds));
    }

    /// <summary>"20–90 s", "1–3 min", "45 s – 3 min": the unit is written once where it can be.</summary>
    private static string ReachRange(int fromSeconds, int toSeconds) =>
        fromSeconds < SpawnReachBand.MinutesFrom && toSeconds < SpawnReachBand.MinutesFrom
            ? UiText.Format("Raid.SpawnReach.SecondsRange", fromSeconds, toSeconds)
            : fromSeconds >= SpawnReachBand.MinutesFrom && toSeconds >= SpawnReachBand.MinutesFrom
                ? UiText.Format("Raid.SpawnReach.MinutesRange", fromSeconds / 60, toSeconds / 60)
                : UiText.Format("Raid.SpawnReach.MixedRange", ReachLabel(fromSeconds), ReachLabel(toSeconds));

    private static string ReachLabel(int seconds) => seconds < SpawnReachBand.MinutesFrom
        ? UiText.Format("Raid.SpawnReach.Seconds", seconds)
        : UiText.Format("Raid.SpawnReach.Minutes", seconds / 60);
}
