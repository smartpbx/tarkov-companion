using TarkovCompanion.App.Localization;
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
            ? SetupText.DataCoverage(data.ItemCount, Math.Min(data.SyncedEndpointCount, EndpointsPerRefresh), EndpointsPerRefresh)
            : SetupText.DataNoData;
        var lastSuccess = data.UpdatedUtc is { } updated
            ? V2ShellText.Age(updated, now, culture)
            : SetupText.DataNever;
        var failed = attempt is { State: BackgroundWorkState.Faulted or BackgroundWorkState.TimedOut or BackgroundWorkState.Rejected };

        var facts = new List<SetupFact>
        {
            new(SetupText.DataSourceLabel, SetupText.DataSource(scope)),
            new(SetupText.DataCoverageLabel, coverage),
            new(SetupText.DataLastAttemptLabel, DescribeAttempt(attempt, snapshot.IsOffline, now, culture)),
            new(SetupText.DataLastSuccessLabel, lastSuccess),
            new(SetupText.DataNextLabel, DescribeNext(snapshot, refreshesAfter, culture)),
        };

        var degraded = data.Availability is DataAvailability.Cached or DataAvailability.Error or DataAvailability.Unavailable
            || snapshot.IsOffline;
        var needsRetry = !snapshot.IsOffline
            && !snapshot.IsDemoMode
            && (data.Availability is DataAvailability.Error or DataAvailability.Unavailable || failed);
        var reason = SetupText.DataDetail(data);
        return new(facts, degraded && reason.Length > 0 ? reason : null, needsRetry);
    }

    private static string DescribeAttempt(BackgroundWorkSnapshot? attempt, bool offline, DateTimeOffset now, CultureInfo culture)
    {
        if (attempt is null)
        {
            return offline ? SetupText.DataAttemptOffline : SetupText.DataAttemptNone;
        }

        var when = attempt.CompletedUtc ?? attempt.StartedUtc ?? attempt.SubmittedUtc;
        return attempt.State switch
        {
            BackgroundWorkState.Pending or BackgroundWorkState.Running or BackgroundWorkState.Restarting =>
                SetupText.DataAttemptRunning,
            BackgroundWorkState.Succeeded => SetupText.DataAttemptSucceeded(V2ShellText.Age(when, now, culture)),
            BackgroundWorkState.TimedOut => SetupText.DataAttemptTimedOut(V2ShellText.Age(when, now, culture)),
            BackgroundWorkState.Cancelled => SetupText.DataAttemptStopped(V2ShellText.Age(when, now, culture)),
            _ => SetupText.DataAttemptFailed(V2ShellText.Age(when, now, culture)),
        };
    }

    private static string DescribeNext(ApplicationRuntimeSnapshot snapshot, TimeSpan refreshesAfter, CultureInfo culture)
    {
        if (snapshot.IsDemoMode)
        {
            return SetupText.DataNextDemo;
        }

        if (snapshot.IsOffline)
        {
            return SetupText.DataNextOffline;
        }

        // Said as what the app does, because that is all it does: it refreshes at launch when the data is
        // old enough, and when the connection returns. There is no timer, so "in 3 hours" would be a claim.
        return SetupText.DataNextAtLaunch((int)Math.Round(refreshesAfter.TotalHours));
    }
}
