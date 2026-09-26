using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Situations;

namespace TarkovCompanion.Application.Services.FormatGuards;

/// <summary>[#712 0-3] The situation's FormatHealth fact, so a V3 surface can say why it is unsure.</summary>
/// <remarks>
/// Its time is when a status last changed, never "now", so the situation's version does not move
/// while nothing did (ADR 0022 §7). The "because" is English, like every other fact's.
/// </remarks>
public static class FormatHealthFact
{
    public static SituationFact<FormatHealthStatus>? From(FormatHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Sources.All(source => source.Status == FormatHealthStatus.Unknown))
        {
            return null;
        }

        var degraded = report.Sources.Where(source => source.Status == FormatHealthStatus.Degraded).ToList();
        var since = report.Sources.Max(source => source.SinceUtc) ?? DateTimeOffset.UnixEpoch;
        if (degraded.Count == 0)
        {
            return new(
                FormatHealthStatus.Ok,
                new Confidence(0.9),
                SituationSource.GameLog,
                since,
                "The game's logs and screenshot names are in the shapes this build reads.");
        }

        var because = string.Join(" ", degraded.Select(Explain));
        return new(
            FormatHealthStatus.Degraded,
            new Confidence(0.9),
            degraded.Any(source => source.Source != FormatSource.ScreenshotName) ? SituationSource.GameLog : SituationSource.Screenshot,
            since,
            because);
    }

    private static string Explain(FormatSourceHealth source)
    {
        var what = source.Source switch
        {
            FormatSource.GameLog => "Log lines",
            FormatSource.Notification => "Raid and quest notifications",
            _ => "Screenshot names",
        };
        var when = source.ChangedAfterUpdate ? $" since update {source.GameVersion}" : string.Empty;
        // No counts: they move with every line, and the fact must not (ADR 0022 §7).
        return $"{what} are not in a known shape{when}.";
    }
}
