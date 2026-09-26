using TarkovCompanion.App.ViewModels.V2.Shell;
using TarkovCompanion.Application.Services.Readiness;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#712 1-13] The Raid page's readiness strip in each state, decided by the real rules from a
/// made-up machine. <c>--readiness-demo first-run|partial|local-only|format|ready</c>: nothing
/// found and the catalog still downloading; logs found, screenshots not, a failed download;
/// Local only with no catalog; a game update that changed the log format; everything ready (no
/// strip). Dev tool only.
/// </summary>
internal static class ReadinessStripDemo
{
    public static void Apply(V2ShellViewModel? shell, string? mode, Action<int> pump)
    {
        if (mode is null || shell is null)
        {
            return;
        }

        var searched = new EftObservationState(true, false, false, null, null, Confidence.Unknown, "not found") { Searched = true };
        var logsOnly = searched with { IsWatchingLogs = true, LogRoot = @"C:\Battlestate Games\Escape from Tarkov\Logs" };
        var both = logsOnly with { IsWatchingScreenshots = true, ScreenshotRoot = @"C:\Users\p\OneDrive\Documents\Escape from Tarkov\Screenshots" };
        var empty = new RuntimeDataState(DataAvailability.Unavailable, 0, 0, null, string.Empty);
        var stored = new RuntimeDataState(DataAvailability.Current, 4200, 9, DateTimeOffset.UtcNow.AddHours(-2), string.Empty);
        var degraded = new FormatHealthReport(
        [
            new(FormatSource.GameLog, FormatHealthStatus.Degraded, 3, 77, "1.1.6.0.49000", "1.1.5.1.47510", DateTimeOffset.UtcNow),
            new(FormatSource.Notification, FormatHealthStatus.Ok, 12, 0, "1.1.6.0.49000", "1.1.6.0.49000", DateTimeOffset.UtcNow),
            new(FormatSource.ScreenshotName, FormatHealthStatus.Ok, 5, 0, null, null, DateTimeOffset.UtcNow),
        ], "1.1.6.0.49000");

        var input = mode.ToLowerInvariant() switch
        {
            "first-run" => new ReadinessStripInput(false, searched, true, empty with { Availability = DataAvailability.Refreshing }, false, false, null),
            "partial" => new ReadinessStripInput(false, logsOnly, true, empty with { Availability = DataAvailability.Error }, false, false, null),
            "local-only" => new ReadinessStripInput(false, both, true, empty, true, false, null),
            "format" => new ReadinessStripInput(false, both, true, stored, false, true, degraded),
            "ready" => new ReadinessStripInput(false, both, true, stored, false, false, null),
            _ => throw new ArgumentException($"Unknown --readiness-demo '{mode}'."),
        };

        var state = ReadinessStripRules.Evaluate(input);
        shell.ReadinessStrip.PinForPreview(state);
        pump(20);
        Console.WriteLine($"Readiness strip: visible={state.IsVisible} shown={shell.ShowsReadinessStrip} " +
            string.Join(" | ", shell.ReadinessStrip.Items.Select(item => $"{item.Label} {item.State}{(item.HasFix ? " [" + item.FixLabel + "]" : string.Empty)}")));
    }
}
