using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.Application.Services.FormatGuards;

namespace TarkovCompanion.V2RenderPreview;

/// <summary>
/// [#712 0-3] Feeds the real format-health monitor synthetic lines, so Setup › Updates &amp;
/// Diagnostics' readiness row can be looked at. <c>--format-health-demo ok</c> reads well;
/// <c>--format-health-demo degraded</c> is a build whose log header changed shape (invented).
/// </summary>
internal static class FormatHealthDemo
{
    public static void Apply(IServiceProvider services, string? mode)
    {
        if (mode is null || services.GetService<FormatHealthMonitor>() is not { } monitor)
        {
            return;
        }

        Feed(monitor, "1.1.5.1.47510", known: true);
        for (var n = 0; n < 5; n++)
        {
            monitor.ObserveScreenshotName($"2026-01-01[20-1{n}]_1.0, 2.0, 3.0_0.0, 0.0, 0.0, 1.0_10.5 (0).png");
        }

        if (string.Equals(mode, "degraded", StringComparison.OrdinalIgnoreCase))
        {
            Feed(monitor, "1.1.6.0.49000", known: false);
        }
    }

    private static void Feed(FormatHealthMonitor monitor, string build, bool known)
    {
        var path = $"C:/EFT/Logs/log_2026.01.01_20-00-00_{build}/2026.01.01_20-00-00_{build} application_000.log";
        for (var n = 0; n < 80; n++)
        {
            monitor.ObserveLogLine(path, known
                ? $"2026-01-01 20:00:{n % 60:00}.000|{build}|Info|application|LocationLoaded:18.4 real:24.73 diff:6.33"
                : $"2026-01-01T20:00:{n % 60:00}.000Z [Info] application: LocationLoaded 18.4");
        }
    }
}
