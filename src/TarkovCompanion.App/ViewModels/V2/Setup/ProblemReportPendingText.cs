using System.Globalization;
using TarkovCompanion.Application.Services.Feedback;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.App.ViewModels.V2.Setup;

/// <summary>The one line Setup › Diagnostics shows while problem reports wait for the relay (#314).</summary>
public static class ProblemReportPendingText
{
    public static string Describe(IReadOnlyList<QueuedProblemReport> queued, CultureInfo? culture = null)
    {
        if (queued.Count == 0)
        {
            return string.Empty;
        }

        var next = queued.Min(report => report.NextAttemptUtc);
        var noun = queued.Count == 1 ? "report" : "reports";
        return $"{queued.Count} {noun} queued · next try {LocalTime.ShortTime(next, culture)}";
    }
}
