using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.App.Localization;

/// <summary>[#712 0-3] The format-health readiness row in Setup › Updates &amp; Diagnostics.</summary>
public static partial class SetupText
{
    /// <summary>"Game logs: OK · Screenshots: OK", or which one changed and what that breaks.</summary>
    public static string FormatHealthRow(FormatHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return UiText.Format("Setup.FormatHealth.Row", FormatHealthLogs(report), FormatHealthScreenshots(report));
    }

    public static string FormatHealthLogs(FormatHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var degraded = new[] { report.For(FormatSource.GameLog), report.For(FormatSource.Notification) }
            .FirstOrDefault(source => source.Status == FormatHealthStatus.Degraded);
        var state = degraded is not null
            ? degraded.ChangedAfterUpdate
                ? UiText.Format("Setup.FormatHealth.LogsChangedAfter", degraded.GameVersion)
                : UiText.Get("Setup.FormatHealth.LogsChanged")
            : Plain(report.Logs);
        return UiText.Format("Setup.FormatHealth.Logs", state);
    }

    public static string FormatHealthScreenshots(FormatHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var screenshots = report.For(FormatSource.ScreenshotName);
        var state = screenshots.Status == FormatHealthStatus.Degraded
            ? screenshots.ChangedAfterUpdate
                ? UiText.Format("Setup.FormatHealth.ScreenshotsChangedAfter", screenshots.GameVersion)
                : UiText.Get("Setup.FormatHealth.ScreenshotsChanged")
            : Plain(screenshots.Status);
        return UiText.Format("Setup.FormatHealth.Screenshots", state);
    }

    private static string Plain(FormatHealthStatus status) => status == FormatHealthStatus.Ok
        ? UiText.Get("Setup.FormatHealth.Ok")
        : UiText.Get("Setup.FormatHealth.Waiting");
}
