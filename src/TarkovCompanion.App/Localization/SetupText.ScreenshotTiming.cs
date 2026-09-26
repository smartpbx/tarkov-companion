namespace TarkovCompanion.App.Localization;

/// <summary>[#712 0-12] Setup › Updates &amp; Diagnostics: how long each kind of screenshot takes.</summary>
public static partial class SetupText
{
    public static string ScreenshotTimingHeading => UiText.Get("Setup.ScreenshotTiming.Heading");
    public static string ScreenshotTimingHint => UiText.Get("Setup.ScreenshotTiming.Hint");
    public static string ScreenshotTimingNoneYet => UiText.Get("Setup.ScreenshotTiming.NoneYet");

    /// <summary>"Loot (12): settled 0.1 s / 0.2 s · decoded ...".</summary>
    public static string ScreenshotTimingRow(string kind, int count, string milestones) =>
        UiText.Format("Setup.ScreenshotTiming.Row", kind, count, milestones);

    /// <summary>"settled 90 ms / 210 ms".</summary>
    public static string ScreenshotTimingMilestone(string milestone, string p50, string p95) =>
        UiText.Format("Setup.ScreenshotTiming.Milestone", milestone, p50, p95);
}
