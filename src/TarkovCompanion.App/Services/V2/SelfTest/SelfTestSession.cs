using System.Globalization;
using TarkovCompanion.App.Localization;

namespace TarkovCompanion.App.Services.V2.SelfTest;

/// <summary>
/// One press of the self-test: exercises every capability once and reports what it found.
/// </summary>
/// <remarks>
/// Safe to press mid-raid, which is the only time it matters. Every reading is read-only — no
/// refresh is started, nothing is written to the game's folders, and the relay is asked for its
/// health and nothing else. The run is bounded by <see cref="Deadline"/> and by each probe's own
/// timeout, cancellable at any moment, and the probes run together so the wait for a screenshot
/// is the same wait as everything else rather than an extra one.
///
/// A probe that throws does not take the run with it: it becomes an unknown carrying the reason,
/// because a self-test that falls over is exactly the thing that would leave somebody guessing
/// again.
/// </remarks>
public sealed class SelfTestSession
{
    /// <summary>
    /// How long the screenshot probe keeps waiting, in the background, after the run has finished.
    /// </summary>
    /// <remarks>
    /// [V2 rough package 43a] Was 45 seconds, inside the run, which asked somebody to alt-tab into
    /// a game and press a key before a countdown they could not see ran out — Clayton's report was
    /// that the probe failed "only because i cant alt tab back to the game and screenshot fast
    /// enough". Five minutes, and it no longer holds anything open: the other six settle, this one
    /// says it is waiting, and it resolves itself whenever the screenshot happens.
    /// </remarks>
    public static readonly TimeSpan ScreenshotPatience = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far back a screenshot already on disk still counts as evidence.
    /// </summary>
    /// <remarks>
    /// Ten minutes is inside one raid. A shot from then went through the same folder, the same
    /// name, the same parser and the same clocks as one taken now, so it answers the question, and
    /// during a session one nearly always exists — which is the difference between a probe that
    /// usually passes on its own and one that always needs somebody to do something.
    /// </remarks>
    public static readonly TimeSpan ScreenshotLookBack = TimeSpan.FromMinutes(10);

    /// <summary>The whole run's ceiling, so a hung service cannot leave Setup testing forever.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(75);

    private readonly ISelfTestReadings _readings;
    private readonly TimeProvider _clock;
    private readonly CultureInfo _culture;

    public SelfTestSession(ISelfTestReadings readings, TimeProvider? timeProvider = null, CultureInfo? culture = null)
    {
        _readings = readings ?? throw new ArgumentNullException(nameof(readings));
        _clock = timeProvider ?? TimeProvider.System;
        _culture = culture ?? CultureInfo.CurrentCulture;
    }

    /// <summary>The capabilities in the order the report shows them, before any of them has run.</summary>
    public static IReadOnlyList<(string Id, string Title)> Capabilities { get; } =
    [
        (SelfTestProbes.FoldersId, SetupText.ProbeTitleGameFolders),
        (SelfTestProbes.LogsId, SetupText.ProbeTitleLogs),
        (SelfTestProbes.ScreenshotsId, SetupText.ProbeTitleScreenshots),
        (SelfTestProbes.GameDataId, SetupText.ProbeTitleGameData),
        (SelfTestProbes.DatabaseId, SetupText.ProbeTitleDatabase),
        (SelfTestProbes.RelayId, SetupText.ProbeTitleRelay),
        (SelfTestProbes.TabletId, SetupText.ProbeTitleTablet),
    ];

    public async Task<SelfTestSummary> RunAsync(Action<SelfTestCapability>? report, CancellationToken cancellationToken)
    {
        var startedUtc = _clock.GetUtcNow();
        var startedAt = _clock.GetTimestamp();
        using var deadline = new CancellationTokenSource(Deadline, _clock);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = run.Token;

        var folders = MeasureAsync(
            SelfTestProbes.FoldersId,
            SetupText.ProbeTitleGameFolders,
            _readings.ReadFoldersAsync,
            (reading, took) => SelfTestProbes.Folders(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);
        var logs = MeasureAsync(
            SelfTestProbes.LogsId,
            SetupText.ProbeTitleLogs,
            _readings.ReadLogsAsync,
            (reading, took) => SelfTestProbes.Logs(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);
        // The screenshot probe's fast half: a shot already on disk settles it outright, and costs a
        // folder listing. Only when there is none does anything wait, and then it waits out of the
        // way of the rest of the run.
        var screenshots = MeasureAsync(
            SelfTestProbes.ScreenshotsId,
            SetupText.ProbeTitleScreenshots,
            inner => _readings.RecentScreenshotAsync(ScreenshotLookBack, inner),
            (reading, took) => SelfTestProbes.Screenshots(reading, took, _culture),
            report,
            token);
        var gameData = MeasureAsync(
            SelfTestProbes.GameDataId,
            SetupText.ProbeTitleGameData,
            _readings.ReadGameDataAsync,
            (reading, took) => SelfTestProbes.GameData(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);
        var database = MeasureAsync(
            SelfTestProbes.DatabaseId,
            SetupText.ProbeTitleDatabase,
            _readings.ReadDatabaseAsync,
            (reading, took) => SelfTestProbes.Database(reading, took, _culture),
            report,
            token);
        var relay = MeasureAsync(
            SelfTestProbes.RelayId,
            SetupText.ProbeTitleRelay,
            _readings.ReadRelayAsync,
            (reading, took) => SelfTestProbes.Relay(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);
        var tablet = MeasureAsync(
            SelfTestProbes.TabletId,
            SetupText.ProbeTitleTablet,
            _readings.ReadTabletAsync,
            (reading, took) => SelfTestProbes.Tablet(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);

        var capabilities = await Task.WhenAll(folders, logs, screenshots, gameData, database, relay, tablet)
            .ConfigureAwait(false);

        // Nothing already on disk answered it, so it goes on waiting — on its own, outside the
        // run's deadline, and outside this method's return.
        var shot = capabilities.Single(capability => capability.Id == SelfTestProbes.ScreenshotsId);
        // Unknown means the fast half found nothing it could measure: no recent screenshot, or one
        // taken outside a raid. Facts distinguish that from a folder that cannot be looked at at
        // all, which reports Unknown with nothing behind it and is not worth waiting on — there
        // would be nothing to watch.
        if (shot.Outcome == SelfTestOutcome.Unknown && shot.Facts.Count > 0)
        {
            var waiting = SelfTestCapability.WaitingFor(
                SelfTestProbes.ScreenshotsId,
                SetupText.ProbeTitleScreenshots,
                SetupText.ProbeWaitingForScreenshot(ScreenshotPatience.TotalMinutes),
                shot.Facts);
            capabilities = [.. capabilities.Select(capability =>
                capability.Id == SelfTestProbes.ScreenshotsId ? waiting : capability)];
            report?.Invoke(waiting);
            // Linked to the caller only. The run's own deadline exists so a hung service cannot
            // leave Setup testing forever, and this is not a hung service — it is a player who has
            // not taken a screenshot yet.
            Settling = SettleScreenshotAsync(report, cancellationToken);
        }

        return new(startedUtc, _clock.GetElapsedTime(startedAt), capabilities);
    }

    /// <summary>
    /// The screenshot probe's own continuation, still running after <see cref="RunAsync"/> returned.
    /// </summary>
    /// <remarks>
    /// Null when a screenshot already on disk settled it, which is the usual case. Awaiting it is
    /// optional: the answer arrives through the same <c>report</c> callback the run used.
    /// </remarks>
    public Task<SelfTestCapability>? Settling { get; private set; }

    private async Task<SelfTestCapability> SettleScreenshotAsync(
        Action<SelfTestCapability>? report,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetTimestamp();
        SelfTestCapability capability;
        try
        {
            var reading = await _readings.WatchScreenshotAsync(ScreenshotPatience, cancellationToken).ConfigureAwait(false);
            capability = SelfTestProbes.Screenshots(reading, _clock.GetElapsedTime(startedAt), _culture);
        }
        catch (OperationCanceledException)
        {
            capability = new(
                SelfTestProbes.ScreenshotsId,
                SetupText.ProbeTitleScreenshots,
                SelfTestOutcome.Unknown,
                SetupText.ProbeStoppedBeforeScreenshot,
                [],
                _clock.GetElapsedTime(startedAt));
        }
        catch (Exception exception)
        {
            capability = new(
                SelfTestProbes.ScreenshotsId,
                SetupText.ProbeTitleScreenshots,
                SelfTestOutcome.Unknown,
                SetupText.ProbeCouldNotBeTested(exception.Message),
                [new(exception.GetType().Name, SetupText.ProbeSourceException)],
                _clock.GetElapsedTime(startedAt));
        }

        report?.Invoke(capability);
        return capability;
    }

    private async Task<SelfTestCapability> MeasureAsync<TReading>(
        string id,
        string title,
        Func<CancellationToken, Task<TReading>> read,
        Func<TReading, TimeSpan, SelfTestCapability> describe,
        Action<SelfTestCapability>? report,
        CancellationToken cancellationToken)
    {
        var startedAt = _clock.GetTimestamp();
        SelfTestCapability capability;
        try
        {
            var reading = await read(cancellationToken).ConfigureAwait(false);
            capability = describe(reading, _clock.GetElapsedTime(startedAt));
        }
        catch (OperationCanceledException)
        {
            capability = new(
                id,
                title,
                SelfTestOutcome.Unknown,
                SetupText.ProbeStoppedBeforeFinished,
                [],
                _clock.GetElapsedTime(startedAt));
        }
        catch (Exception exception)
        {
            capability = new(
                id,
                title,
                SelfTestOutcome.Unknown,
                SetupText.ProbeCouldNotBeTested(exception.Message),
                [new(exception.GetType().Name, SetupText.ProbeSourceException)],
                _clock.GetElapsedTime(startedAt));
        }

        report?.Invoke(capability);
        return capability;
    }
}
