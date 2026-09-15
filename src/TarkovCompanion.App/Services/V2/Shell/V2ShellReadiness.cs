using System.Globalization;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.V2.Shell;

public enum V2CheckStatus
{
    Ready = 1,
    NeedsAction,
    Optional,

    /// <summary>Not confirmed either way. Never counted as ready, never counted as broken.</summary>
    Unconfirmed,
    Failed,
    Checking,
}

/// <summary>One readiness check, with the evidence behind its status and where to fix it.</summary>
public sealed record V2ReadinessCheck(
    string Id,
    string LabelKey,
    V2CheckStatus Status,
    string Detail,
    V2RouteId ActionRoute,
    bool Required);

/// <summary>
/// The checklist behind Get ready, Home, and the one compact health affordance in the header.
/// </summary>
/// <remarks>
/// One model for all three, so the header count and the checklist cannot disagree. V1 has six
/// chips along the top and a status bar along the bottom that restate some of the same facts in
/// different words; #267 replaces both with this.
///
/// A check that has not been confirmed says so. The #265 state matrix is explicit that unchecked
/// readiness is "unconfirmed", never quietly "ready", and the header therefore distinguishes
/// "nothing needs action" from "status unconfirmed".
/// </remarks>
public sealed record V2ReadinessSummary(IReadOnlyList<V2ReadinessCheck> Checks)
{
    public int RequiredCount => Checks.Count(check => check.Required);

    public int ReadyCount => Checks.Count(check => check.Required && check.Status == V2CheckStatus.Ready);

    public int NeedsActionCount => Checks.Count(check => check.Required && check.Status is V2CheckStatus.NeedsAction or V2CheckStatus.Failed);

    public int UnconfirmedCount => Checks.Count(check => check.Required && check.Status is V2CheckStatus.Unconfirmed or V2CheckStatus.Checking);

    public V2SurfaceStateKind Kind =>
        Checks.Any(check => check.Required && check.Status == V2CheckStatus.Failed) ? V2SurfaceStateKind.Failed
        : Checks.Any(check => check.Required && check.Status == V2CheckStatus.Checking) ? V2SurfaceStateKind.Loading
        : NeedsActionCount > 0 || UnconfirmedCount > 0 ? V2SurfaceStateKind.Partial
        : V2SurfaceStateKind.Ready;
}

public static class V2Readiness
{
    /// <summary>Game data older than this is labelled stale rather than ready.</summary>
    public static readonly TimeSpan GameDataStaleAfter = TimeSpan.FromHours(24);

    public static V2ReadinessSummary Evaluate(ApplicationRuntimeSnapshot snapshot, DateTimeOffset nowUtc, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(culture);
        var observation = snapshot.Observation;
        var demo = snapshot.IsDemoMode;
        var demoDetail = V2ShellText.Get("V2.Shell.Detail.DemoFixture");

        return new(
        [
            new(
                "game-log",
                "V2.Shell.Check.GameLog",
                demo || !observation.IsSupported
                    ? V2CheckStatus.Unconfirmed
                    : observation.IsWatchingLogs ? V2CheckStatus.Ready : V2CheckStatus.NeedsAction,
                demo ? demoDetail : observation.IsWatchingLogs && observation.LogRoot is { } logRoot ? logRoot : observation.Detail,
                V2Routes.Setup,
                Required: true),
            new(
                "screenshots",
                "V2.Shell.Check.Screenshots",
                demo || !observation.IsSupported
                    ? V2CheckStatus.Unconfirmed
                    : observation.IsWatchingScreenshots ? V2CheckStatus.Ready : V2CheckStatus.NeedsAction,
                demo ? demoDetail : observation.IsWatchingScreenshots && observation.ScreenshotRoot is { } screenshotRoot ? screenshotRoot : observation.Detail,
                V2Routes.Setup,
                Required: true),
            new(
                "text-recognition",
                "V2.Shell.Check.Recognition",
                demo ? V2CheckStatus.Unconfirmed : snapshot.Scan.IsAvailable ? V2CheckStatus.Ready : V2CheckStatus.NeedsAction,
                demo ? demoDetail : snapshot.Scan.Detail,
                V2Routes.Setup,
                Required: true),
            GameData(snapshot, nowUtc, culture),
            new(
                "profile",
                "V2.Shell.Check.Profile",
                snapshot.Profile is null ? V2CheckStatus.Unconfirmed : V2CheckStatus.Ready,
                snapshot.Profile is { } profile
                    ? V2ShellText.Format("V2.Shell.Detail.Profile", culture, profile.Name, profile.GameMode)
                    : V2ShellText.Get("V2.Shell.Detail.ProfileNotLoaded"),
                V2Routes.Plan,
                Required: true),
            new(
                "group-sharing",
                "V2.Shell.Check.GroupSharing",
                V2CheckStatus.Optional,
                snapshot.Group.IsSharing
                    ? V2ShellText.Format("V2.Shell.Detail.Sharing", culture, snapshot.Group.Members.Count)
                    : V2ShellText.Get("V2.Shell.Detail.NotSharing"),
                V2Routes.Group,
                Required: false),
        ]);
    }

    private static V2ReadinessCheck GameData(ApplicationRuntimeSnapshot snapshot, DateTimeOffset nowUtc, CultureInfo culture)
    {
        var data = snapshot.Data;
        var status = data.Availability switch
        {
            DataAvailability.DemoFixture => V2CheckStatus.Unconfirmed,
            DataAvailability.Error => V2CheckStatus.Failed,
            DataAvailability.Refreshing when data.ItemCount == 0 => V2CheckStatus.Checking,
            DataAvailability.Unavailable when !snapshot.DatabaseReady && !snapshot.IsOffline => V2CheckStatus.Checking,
            DataAvailability.Unavailable => V2CheckStatus.NeedsAction,
            _ when data.UpdatedUtc is null => V2CheckStatus.Unconfirmed,
            _ when nowUtc - data.UpdatedUtc > GameDataStaleAfter => V2CheckStatus.NeedsAction,
            _ => V2CheckStatus.Ready,
        };

        var detail = data.UpdatedUtc is { } synced && data.ItemCount > 0
            ? V2ShellText.Format("V2.Shell.Detail.GameData", culture, data.ItemCount, V2ShellText.Age(synced, nowUtc, culture))
            : data.Detail;
        return new("game-data", "V2.Shell.Check.GameData", status, detail, V2Routes.Setup, Required: true);
    }
}
