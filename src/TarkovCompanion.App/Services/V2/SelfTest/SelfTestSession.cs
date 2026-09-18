using System.Globalization;

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
    /// <summary>How long the screenshot probe waits for the player to press their screenshot key.</summary>
    public static readonly TimeSpan ScreenshotPatience = TimeSpan.FromSeconds(45);

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
        (SelfTestProbes.FoldersId, "Game folders"),
        (SelfTestProbes.LogsId, "Logs"),
        (SelfTestProbes.ScreenshotsId, "Screenshots"),
        (SelfTestProbes.GameDataId, "Game data"),
        (SelfTestProbes.DatabaseId, "Database"),
        (SelfTestProbes.RelayId, "Relay"),
        (SelfTestProbes.TabletId, "Tablet"),
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
            "Game folders",
            _readings.ReadFoldersAsync,
            (reading, took) => SelfTestProbes.Folders(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);
        var logs = MeasureAsync(
            SelfTestProbes.LogsId,
            "Logs",
            _readings.ReadLogsAsync,
            (reading, took) => SelfTestProbes.Logs(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);
        var screenshots = MeasureAsync(
            SelfTestProbes.ScreenshotsId,
            "Screenshots",
            inner => _readings.WatchScreenshotAsync(ScreenshotPatience, inner),
            (reading, took) => SelfTestProbes.Screenshots(reading, took, _culture),
            report,
            token);
        var gameData = MeasureAsync(
            SelfTestProbes.GameDataId,
            "Game data",
            _readings.ReadGameDataAsync,
            (reading, took) => SelfTestProbes.GameData(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);
        var database = MeasureAsync(
            SelfTestProbes.DatabaseId,
            "Database",
            _readings.ReadDatabaseAsync,
            (reading, took) => SelfTestProbes.Database(reading, took, _culture),
            report,
            token);
        var relay = MeasureAsync(
            SelfTestProbes.RelayId,
            "Relay",
            _readings.ReadRelayAsync,
            (reading, took) => SelfTestProbes.Relay(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);
        var tablet = MeasureAsync(
            SelfTestProbes.TabletId,
            "Tablet",
            _readings.ReadTabletAsync,
            (reading, took) => SelfTestProbes.Tablet(reading, _clock.GetUtcNow(), took, _culture),
            report,
            token);

        var capabilities = await Task.WhenAll(folders, logs, screenshots, gameData, database, relay, tablet)
            .ConfigureAwait(false);
        return new(startedUtc, _clock.GetElapsedTime(startedAt), capabilities);
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
                "Stopped before this finished.",
                [],
                _clock.GetElapsedTime(startedAt));
        }
        catch (Exception exception)
        {
            capability = new(
                id,
                title,
                SelfTestOutcome.Unknown,
                $"Could not be tested: {exception.Message}",
                [new(exception.GetType().Name, "the exception this probe raised")],
                _clock.GetElapsedTime(startedAt));
        }

        report?.Invoke(capability);
        return capability;
    }
}
