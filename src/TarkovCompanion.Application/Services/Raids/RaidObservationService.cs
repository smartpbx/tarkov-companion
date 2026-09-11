using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;

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
    private readonly IRuntimeStateStore _stateStore;
    private readonly RuntimeOptions _options;
    private readonly ILogger<RaidObservationService> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private bool _disposed;

    public RaidObservationService(
        IEftPathLocator pathLocator,
        IEftLogWatcher logWatcher,
        IScreenshotWatcher screenshotWatcher,
        IScreenshotFilenameParser filenameParser,
        RaidActivityCoordinator coordinator,
        IRuntimeStateStore stateStore,
        RuntimeOptions options,
        ILogger<RaidObservationService> logger)
    {
        _pathLocator = pathLocator;
        _logWatcher = logWatcher;
        _screenshotWatcher = screenshotWatcher;
        _filenameParser = filenameParser;
        _coordinator = coordinator;
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

            Publish(new(
                true,
                paths.LogRoot is not null,
                paths.ScreenshotRoot is not null,
                paths.LogRoot,
                paths.ScreenshotRoot,
                paths.Confidence,
                Describe(paths)));

            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var watchers = new List<Task>(2);
            if (paths.LogRoot is not null)
            {
                watchers.Add(WatchLogsAsync(paths.LogRoot, session.Token));
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
                await _coordinator.ApplyEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The Escape from Tarkov log watcher stopped.");
            throw;
        }
    }

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
                if (_filenameParser.TryParse(Path.GetFileName(path), offset, out var position) && position is not null)
                {
                    await _coordinator.ApplyPositionAsync(position, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The Escape from Tarkov screenshot watcher stopped.");
            throw;
        }
    }

    private static string Describe(EftPaths paths) => (paths.LogRoot, paths.ScreenshotRoot) switch
    {
        (not null, not null) => "Watching the game's log and screenshot folders.",
        (not null, null) =>
            "Watching the game's log folder. No screenshot folder was found, so position will not update.",
        (null, not null) =>
            "Watching the game's screenshot folder. No log folder was found, so the map and raid state will not update.",
        _ => "Escape from Tarkov was not found.",
    };

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
