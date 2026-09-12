using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;

namespace TarkovCompanion.Application.Services.Raids;

/// <summary>
/// Watches the files Escape from Tarkov already writes and turns them into raid evidence.
/// </summary>
/// <remarks>
/// This is the only thing that gives the companion any idea which raid the player is in. It
/// reads two ordinary folders the game maintains on disk: its log directory, whose lines
/// reveal the map and the raid lifecycle, and its screenshot directory, whose filenames carry
/// the player's own position and heading. Nothing here reads game memory, inspects traffic,
/// or sends input.
///
/// The game is usually not running when the companion starts, and may be installed after it,
/// so discovery retries rather than deciding once. A watcher that fails is restarted rather
/// than left dead, because the alternative is a companion that silently stops following the
/// player halfway through a session.
/// </remarks>
public sealed class RaidObservationService : IAsyncDisposable
{
    private static readonly TimeSpan RediscoveryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(10);

    private readonly IEftPathLocator _pathLocator;
    private readonly IEftLogWatcher _logWatcher;
    private readonly IScreenshotWatcher _screenshotWatcher;
    private readonly IScreenshotFilenameParser _filenameParser;
    private readonly RaidActivityCoordinator _coordinator;
    private readonly SquadStateService _squad;
    private readonly IRuntimeStateStore _stateStore;
    private readonly RuntimeOptions _options;
    private readonly ILogger<RaidObservationService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private EftPaths? _watching;
    private long _eventsSeen;
    private bool _disposed;

    public RaidObservationService(
        IEftPathLocator pathLocator,
        IEftLogWatcher logWatcher,
        IScreenshotWatcher screenshotWatcher,
        IScreenshotFilenameParser filenameParser,
        RaidActivityCoordinator coordinator,
        SquadStateService squad,
        IRuntimeStateStore stateStore,
        RuntimeOptions options,
        ILogger<RaidObservationService> logger)
    {
        _pathLocator = pathLocator;
        _logWatcher = logWatcher;
        _screenshotWatcher = screenshotWatcher;
        _filenameParser = filenameParser;
        _coordinator = coordinator;
        _squad = squad;
        _stateStore = stateStore;
        _options = options;
        _logger = logger;
    }

    /// <summary>Begins watching in the background. Safe to call once.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_worker is not null)
        {
            return;
        }

        if (_options.DemoMode)
        {
            Publish(EftObservationState.Idle with
            {
                Detail = "Demo mode replays a fixture instead of observing the game.",
            });
            return;
        }

        // The platform gate belongs in composition, which decides whether the real Windows
        // watchers or the unavailable stand-ins are registered. Keeping it here instead made
        // the discovery and feeding logic impossible to exercise off Windows.
        _worker = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            EftPaths paths;
            try
            {
                paths = await _pathLocator.FindAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not look for the Escape from Tarkov folders.");
                Publish(EftObservationState.Idle with
                {
                    Detail = $"Could not look for the game folders: {exception.Message}",
                });
                await DelayAsync(RediscoveryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (paths.LogRoot is null && paths.ScreenshotRoot is null)
            {
                _logger.LogInformation("Escape from Tarkov was not found; retrying discovery.");
                Publish(new(
                    true,
                    false,
                    false,
                    null,
                    null,
                    paths.Confidence,
                    "Escape from Tarkov was not found. Raid tracking starts on its own once the game is installed."));
                await DelayAsync(RediscoveryDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            Interlocked.Exchange(ref _eventsSeen, 0);
            // A new watching session means a new game session, and the party from the last one
            // is no longer known to be standing there.
            PublishSquad(_squad.Clear());
            _watching = paths;
            PublishWatching();
            _logger.LogInformation(
                "Observing Escape from Tarkov. Logs: {LogRoot}. Screenshots: {ScreenshotRoot}. Confidence {Confidence}.",
                paths.LogRoot ?? "none",
                paths.ScreenshotRoot ?? "none",
                paths.Confidence.Value);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watchers = new List<Task>(3);
            if (paths.LogRoot is not null)
            {
                watchers.Add(WatchLogsAsync(paths.LogRoot, session.Token));
                watchers.Add(PublishSquadAsync(session.Token));
            }

            if (paths.ScreenshotRoot is not null)
            {
                watchers.Add(WatchScreenshotsAsync(paths.ScreenshotRoot, session.Token));
            }

            try
            {
                await Task.WhenAll(watchers).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Observation stopped and will be restarted.");
            }
            finally
            {
                await session.CancelAsync().ConfigureAwait(false);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                Publish(EftObservationState.Idle with
                {
                    Detail = "Observation stopped unexpectedly and is restarting.",
                });
                await DelayAsync(RestartDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WatchLogsAsync(string logRoot, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evidence in _logWatcher.WatchAsync(logRoot, cancellationToken).ConfigureAwait(false))
            {
                // Republishing on every line would churn the whole UI state hundreds of
                // times a raid, so the count is surfaced early and then occasionally.
                var seen = Interlocked.Increment(ref _eventsSeen);
                if (seen <= 3 || seen % 25 == 0)
                {
                    PublishWatching();
                    _logger.LogInformation(
                        "Read {Count} log event(s); latest is {Summary} (map {MapId}, state {State}).",
                        seen,
                        evidence.Summary,
                        evidence.MapId ?? "none",
                        evidence.SuggestedState?.ToString() ?? "unchanged");
                }
                await _coordinator.ApplyEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The Escape from Tarkov log watcher stopped.");
            throw;
        }
    }

    /// <summary>How often the party is copied into runtime state.</summary>
    /// <remarks>
    /// The party is deliberately not published as its own notifications arrive. A readiness
    /// toggle republishes every member with their whole inventory, so they land in bursts of
    /// hundreds, and pushing each one at the interface would redraw the squad list continuously
    /// while nobody had actually done anything. Copying the collapsed state on a slow tick
    /// gives the same information at a cost that does not scale with how restless the party is.
    /// </remarks>
    private static readonly TimeSpan SquadPublishInterval = TimeSpan.FromSeconds(2);

    private async Task PublishSquadAsync(CancellationToken cancellationToken)
    {
        var published = _squad.Current.UpdatedUtc;
        while (!cancellationToken.IsCancellationRequested)
        {
            await DelayAsync(SquadPublishInterval, cancellationToken).ConfigureAwait(false);
            var squad = _squad.Current;
            if (squad.UpdatedUtc == published)
            {
                continue;
            }

            published = squad.UpdatedUtc;
            PublishSquad(squad);
            _logger.LogInformation(
                "The party now has {Members} member(s) and {Dogtags} dogtag(s).",
                squad.Members.Count,
                squad.Dogtags.Count);
        }
    }

    private void PublishSquad(SquadSnapshot squad) =>
        _stateStore.Update(current => current with { Squad = squad });

    private async Task WatchScreenshotsAsync(string screenshotRoot, CancellationToken cancellationToken)
    {
        // The game writes the player's own position and heading into the screenshot filename.
        // That file is created by the game at the player's request; nothing is captured here.
        var offset = TimeZoneInfo.Local.GetUtcOffset(DateTimeOffset.UtcNow);
        try
        {
            await foreach (var path in _screenshotWatcher.WatchAsync(screenshotRoot, cancellationToken)
                               .ConfigureAwait(false))
            {
                if (!_filenameParser.TryParseFile(path, offset, out var position) || position is null)
                {
                    // Menu and hideout screenshots carry no coordinates. That is ordinary and
                    // is not worth a warning every time the player photographs their stash.
                    continue;
                }

                _logger.LogInformation(
                    "Read a position from {Filename}: X {X:F1}, Y {Y:F1}, Z {Z:F1} at {Taken:O}.",
                    position.Filename,
                    position.Position.X,
                    position.Position.Y,
                    position.Position.Z,
                    position.Timestamp);
                await _coordinator.ApplyPositionAsync(position, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The Escape from Tarkov screenshot watcher stopped.");
            throw;
        }
    }

    /// <summary>
    /// Says which folder is being watched and how much has come out of it.
    /// </summary>
    /// <remarks>
    /// "Observing" on its own was true and useless: it meant a folder had been found, not
    /// that anything was being read from it. A stale folder that the game no longer writes
    /// to looked identical to a working one, and the difference cost a raid to discover.
    /// Naming the folder and counting what has arrived makes that visible at a glance.
    /// </remarks>
    private void PublishWatching()
    {
        var paths = _watching;
        if (paths is null)
        {
            return;
        }

        var seen = Interlocked.Read(ref _eventsSeen);
        var counted = seen == 0
            ? "nothing read yet"
            : seen == 1 ? "1 event read" : $"{seen} events read";
        var detail = paths.LogRoot is null
            ? $"No log folder was found, so the map and raid state will not update. Screenshots: {paths.ScreenshotRoot}"
            : $"{counted} from {paths.LogRoot}";

        Publish(new(
            true,
            paths.LogRoot is not null,
            paths.ScreenshotRoot is not null,
            paths.LogRoot,
            paths.ScreenshotRoot,
            paths.Confidence,
            detail));
    }

    private async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Publish(EftObservationState observation) =>
        _stateStore.Update(current => current with
        {
            Observation = observation with { IsSupported = OperatingSystem.IsWindows() },
        });
}
