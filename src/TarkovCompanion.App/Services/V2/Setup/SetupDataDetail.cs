using System.Globalization;
using TarkovCompanion.App.Services.V2.Shell;
using TarkovCompanion.Application.Services.Execution;
using TarkovCompanion.Application.Services.Runtime;

namespace TarkovCompanion.App.Services.V2.Setup;

/// <summary>One line of Setup › Data: a label and what is true of it now.</summary>
public sealed record SetupFact(string Label, string Value);

/// <summary>What the Data section says about the game-data refresh, in the words a player would use.</summary>
/// <param name="Facts">Source, coverage, last attempt, last success and the next refresh, in that order.</param>
/// <param name="Reason">Why the data is not current, or null when it is.</param>
/// <param name="NeedsRetry">Whether the last refresh left the player without data, or without a fresh copy of it.</param>
public sealed record SetupDataDetail(IReadOnlyList<SetupFact> Facts, string? Reason, bool NeedsRetry)
{
    /// <summary>The seven endpoints one refresh reads: items, maps, tasks, hideout, traders, crafts, barters.</summary>
    public const int EndpointsPerRefresh = 7;

    /// <summary>The runtime coordinator's name for the refresh operation.</summary>
    private const string RefreshFeature = "data-refresh";

    /// <summary>
    /// Reads the refresh's own state instead of restating one sentence of it. The page used to show a
    /// single status string ("Cached · 5,442 items · Refreshed from 7 endpoints"), which cannot say when
    /// the last try was, whether it failed, or when the next one will be.
    /// </summary>
    /// <param name="snapshot">The published runtime snapshot.</param>
    /// <param name="scope">The mode and language being fetched, e.g. "PvE · en".</param>
    /// <param name="refreshesAfter">How old the data may get before a launch refreshes it.</param>
    /// <param name="now">The clock; every age is computed against it.</param>
    public static SetupDataDetail Describe(
        ApplicationRuntimeSnapshot snapshot,
        string scope,
        TimeSpan refreshesAfter,
        DateTimeOffset now,
        CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var data = snapshot.Data;
        var attempt = snapshot.Supervisor.Operations
            .Where(operation => operation.FeatureId.Value == RefreshFeature)
            .OrderByDescending(operation => operation.CompletedUtc ?? operation.StartedUtc ?? operation.SubmittedUtc)
            .FirstOrDefault();

        var coverage = data.ItemCount > 0
            ? V2ShellText.Format("V2.Setup.Data.Coverage", culture, data.ItemCount, Math.Min(data.SyncedEndpointCount, EndpointsPerRefresh), EndpointsPerRefresh)
            : V2ShellText.Get("V2.Setup.Data.NoData");
        var lastSuccess = data.UpdatedUtc is { } updated
            ? V2ShellText.Age(updated, now, culture)
            : V2ShellText.Get("V2.Setup.Data.Never");
        var failed = attempt is { State: BackgroundWorkState.Faulted or BackgroundWorkState.TimedOut or BackgroundWorkState.Rejected };

        var facts = new List<SetupFact>
        {
            new(V2ShellText.Get("V2.Setup.Data.SourceLabel"), V2ShellText.Format("V2.Setup.Data.Source", culture, scope)),
            new(V2ShellText.Get("V2.Setup.Data.CoverageLabel"), coverage),
            new(V2ShellText.Get("V2.Setup.Data.LastAttemptLabel"), DescribeAttempt(attempt, snapshot.IsOffline, now, culture)),
            new(V2ShellText.Get("V2.Setup.Data.LastSuccessLabel"), lastSuccess),
            new(V2ShellText.Get("V2.Setup.Data.NextLabel"), DescribeNext(snapshot, refreshesAfter, culture)),
        };

        var degraded = data.Availability is DataAvailability.Cached or DataAvailability.Error or DataAvailability.Unavailable
            || snapshot.IsOffline;
        var needsRetry = !snapshot.IsOffline
            && !snapshot.IsDemoMode
            && (data.Availability is DataAvailability.Error or DataAvailability.Unavailable || failed);
        return new(facts, degraded && data.Detail.Length > 0 ? data.Detail : null, needsRetry);
    }

    private static string DescribeAttempt(BackgroundWorkSnapshot? attempt, bool offline, DateTimeOffset now, CultureInfo culture)
    {
        if (attempt is null)
        {
            return offline ? V2ShellText.Get("V2.Setup.Data.AttemptOffline") : V2ShellText.Get("V2.Setup.Data.AttemptNone");
        }

        var when = attempt.CompletedUtc ?? attempt.StartedUtc ?? attempt.SubmittedUtc;
        return attempt.State switch
        {
            BackgroundWorkState.Pending or BackgroundWorkState.Running or BackgroundWorkState.Restarting =>
                V2ShellText.Get("V2.Setup.Data.AttemptRunning"),
            BackgroundWorkState.Succeeded => V2ShellText.Format("V2.Setup.Data.AttemptSucceeded", culture, V2ShellText.Age(when, now, culture)),
            BackgroundWorkState.TimedOut => V2ShellText.Format("V2.Setup.Data.AttemptTimedOut", culture, V2ShellText.Age(when, now, culture)),
            BackgroundWorkState.Cancelled => V2ShellText.Format("V2.Setup.Data.AttemptStopped", culture, V2ShellText.Age(when, now, culture)),
            _ => V2ShellText.Format("V2.Setup.Data.AttemptFailed", culture, V2ShellText.Age(when, now, culture)),
        };
    }

    private static string DescribeNext(ApplicationRuntimeSnapshot snapshot, TimeSpan refreshesAfter, CultureInfo culture)
    {
        if (snapshot.IsDemoMode)
        {
            return V2ShellText.Get("V2.Setup.Data.NextDemo");
        }

        if (snapshot.IsOffline)
        {
            return V2ShellText.Get("V2.Setup.Data.NextOffline");
        }

        // Said as what the app does, because that is all it does: it refreshes at launch when the data is
        // old enough, and when the connection returns. There is no timer, so "in 3 hours" would be a claim.
        return V2ShellText.Format("V2.Setup.Data.NextAtLaunch", culture, (int)Math.Round(refreshesAfter.TotalHours));
    }
}
