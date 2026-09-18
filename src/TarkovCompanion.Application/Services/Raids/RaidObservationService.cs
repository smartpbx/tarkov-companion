using Microsoft.Extensions.Logging;
using TarkovCompanion.Application.Services.CaptureSessions;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Runtime;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Raids;
using TarkovCompanion.Core.Domain.Recognition;

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
    private static readonly TimeSpan SourceRediscoveryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScreenshotScanDrainTimeout = TimeSpan.FromSeconds(5);
    private const int MaximumPendingScreenshotScans = 16;

    private readonly IEftPathLocator _pathLocator;
    private readonly IEftLogWatcher _logWatcher;
    private readonly IScreenshotWatcher _screenshotWatcher;
    private readonly IScreenshotFilenameParser _filenameParser;
    private readonly IScreenshotImageLoader? _imageLoader;
    private readonly IScanUseCase? _scanUseCase;
    private readonly ICaptureSessionService? _captureSessions;
    private readonly IProfileRuntimeContextService? _profileRuntimeContext;
    private readonly ScreenshotRetentionService? _retention;
    private readonly IScreenshotRetentionStore? _retentionSettings;
    private readonly RaidActivityCoordinator _coordinator;
    private readonly SquadStateService _squad;
    private readonly FleaSaleStateService _fleaSales;
    private readonly IRuntimeStateStore _stateStore;
    private readonly RuntimeOptions _options;
    private readonly ILogger<RaidObservationService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _screenshotScanGate = new();
    private readonly HashSet<Task> _screenshotScans = [];
    private readonly object _screenshotPublicationGate = new();
    private Task? _worker;
    private EftPaths? _watching;
    private long _eventsSeen;
    private long _nextScreenshotSourceGeneration;
    private long _activeScreenshotSourceGeneration;
    private long _nextScreenshotScanOrdinal;
    private long _lastPublishedScreenshotScanOrdinal;

    /// <summary>Whether an unparsable screenshot name has already been reported this session.</summary>
    private int _unreadableNameReported;
    private bool _disposed;

    public RaidObservationService(
        IEftPathLocator pathLocator,
        IEftLogWatcher logWatcher,
        IScreenshotWatcher screenshotWatcher,
        IScreenshotFilenameParser filenameParser,
        RaidActivityCoordinator coordinator,
        SquadStateService squad,
        FleaSaleStateService fleaSales,
        IRuntimeStateStore stateStore,
        RuntimeOptions options,
        ILogger<RaidObservationService> logger,
        // Optional so the service still composes where there is nothing to scan with, which is
        // every platform but Windows. Defaults rather than a null object, because a null object
        // here would have to pretend a scan happened.
        IScreenshotImageLoader? imageLoader = null,
        IScanUseCase? scanUseCase = null,
        ScreenshotRetentionService? retention = null,
        IScreenshotRetentionStore? retentionSettings = null,
        ICaptureSessionService? captureSessions = null,
        IProfileRuntimeContextService? profileRuntimeContext = null,
        TimeProvider? timeProvider = null)
    {
        _pathLocator = pathLocator;
        _logWatcher = logWatcher;
        _screenshotWatcher = screenshotWatcher;
        _filenameParser = filenameParser;
        _coordinator = coordinator;
        _imageLoader = imageLoader;
        _scanUseCase = scanUseCase;
        _captureSessions = captureSessions;
        _profileRuntimeContext = profileRuntimeContext;
        _retention = retention;
        _retentionSettings = retentionSettings;
        _squad = squad;
        _fleaSales = fleaSales;
        _stateStore = stateStore;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

    private CancellationTokenSource? _session;

    /// <summary>
    /// Stops watching and looks for the folders again.
    /// </summary>
    /// <remarks>
    /// Called when the player names a folder by hand. Discovery only runs between watching
    /// sessions and a session runs until it is cancelled, so without this a saved folder would
    /// do nothing at all until the next launch, which reads as the setting being ignored.
    ///
    /// Cancelling is the whole mechanism. The loop already treats a session ending as a reason
    /// to rediscover and restart.
    /// </remarks>
    public void Rediscover()
    {
        try
        {
            Volatile.Read(ref _session)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The session ended on its own between the read and the cancel, which is the
            // outcome asked for.
        }
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
            Interlocked.Exchange(ref _unreadableNameReported, 0);
            // A new watching session means a new game session, and neither the party from the
            // last one nor its sales belong to this one.
            PublishSquad(_squad.Clear());
            PublishFleaSales(_fleaSales.Clear());
            _watching = paths;
            PublishWatching();
            _logger.LogInformation(
                "Observing Escape from Tarkov. Logs: {LogRoot}. Screenshots: {ScreenshotRoot}. Confidence {Confidence}.",
                paths.LogRoot ?? "none",
                paths.ScreenshotRoot ?? "none",
                paths.Confidence.Value);

            using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Held so that naming a folder by hand takes effect without a restart. The
            // watchers run until they are cancelled, so without this the loop never comes back
            // round to ask where the folders are, and somebody who had just typed one would
            // see nothing happen.
            _ = Interlocked.Exchange(ref _session, session);
            var sourceWatchers = new List<Task>(2);
            var sessionTasks = new List<Task>(4);
            if (paths.LogRoot is not null)
            {
                var logWatcher = WatchLogsAsync(paths.LogRoot, session.Token);
                sourceWatchers.Add(logWatcher);
                sessionTasks.Add(logWatcher);
                sessionTasks.Add(PublishObservationsAsync(session.Token));
            }

            if (paths.ScreenshotRoot is not null)
            {
                var screenshotWatcher = WatchScreenshotsAsync(paths.ScreenshotRoot, session.Token);
                sourceWatchers.Add(screenshotWatcher);
                sessionTasks.Add(screenshotWatcher);
                sessionTasks.Add(TidyScreenshotsAsync(session.Token));
            }

            try
            {
                // Watchers are intended to live for the session. Waiting for all of them before
                // cancellation meant one failed source could sit beside an infinite healthy
                // sibling forever, leaving readiness green after intake had stopped.
                var completed = await Task.WhenAny(sourceWatchers).ConfigureAwait(false);
                if (!cancellationToken.IsCancellationRequested && !session.IsCancellationRequested)
                {
                    await completed.ConfigureAwait(false);
                    throw new InvalidOperationException("An observation source ended unexpectedly.");
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Observation stopped and will be restarted.");
            }
            finally
            {
                await session.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(sessionTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception exception)
                {
                    // The source that ended the session was reported above. Observe any sibling
                    // fault as well so no background task exception is left unowned.
                    _logger.LogDebug(exception, "An observation source also faulted during restart.");
                }

                // Screenshot work is allowed to outlive filename handling, but not the watching
                // session that owns it. Cancel first, then drain, so a disappearing source cannot
                // remain green while a queued decoder is still waiting on the old folder.
                await DrainScreenshotScansAsync().ConfigureAwait(false);

                Interlocked.CompareExchange(ref _session, null, session);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                Publish(EftObservationState.Idle with
                {
                    Detail = "Looking for the game folders again.",
                });
                await DelayAsync(RestartDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WatchLogsAsync(string logRoot, CancellationToken cancellationToken)
    {
        // Every lifecycle change is logged whatever the throttle says. Diagnosing "the raid
        // did not end" from a log that only samples one line in twenty-five means proving a
        // negative from an incomplete record; one line per transition makes it a single
        // lookup instead.
        var lastState = RaidLifecycleState.Unknown;
        try
        {
            await foreach (var evidence in _logWatcher.WatchAsync(logRoot, cancellationToken).ConfigureAwait(false))
            {
                // Republishing on every line would churn the whole UI state hundreds of
                // times a raid, so the count is surfaced early and then occasionally.
                var seen = Interlocked.Increment(ref _eventsSeen);
                var logThisEvent = seen <= 3 || seen % 25 == 0;
                if (logThisEvent)
                {
                    PublishWatching();
                }

                var current = await _coordinator.ApplyEvidenceAsync(evidence, cancellationToken).ConfigureAwait(false);
                if (current.State != lastState)
                {
                    _logger.LogInformation(
                        "Raid state moved from {Previous} to {State} on map {MapId}. Evidence: {Summary}",
                        lastState,
                        current.State,
                        current.MapId ?? "none",
                        evidence.Summary);
                    lastState = current.State;
                }

                if (logThisEvent)
                {
                    // The map reported here is the one the raid now holds, not the one this
                    // single line happened to name. Most lines name no map and the state
                    // rightly keeps the last one, so logging the line's own map printed
                    // "map none" in the middle of a raid on a known map and read like the map
                    // had been lost. It had not, and working that out cost real time.
                    _logger.LogInformation(
                        "Read {Count} log event(s); latest is {Summary} (raid map {MapId}, state {State}).",
                        seen,
                        evidence.Summary,
                        current.MapId ?? "none",
                        current.State);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The Escape from Tarkov log watcher stopped.");
            throw;
        }
    }

    /// <summary>How often the party and the sale list are copied into runtime state.</summary>
    /// <remarks>
    /// The party is deliberately not published as its own notifications arrive. A readiness
    /// toggle republishes every member with their whole inventory, so they land in bursts of
    /// hundreds, and pushing each one at the interface would redraw the squad list continuously
    /// while nobody had actually done anything. Copying the collapsed state on a slow tick
    /// gives the same information at a cost that does not scale with how restless the party is.
    /// </remarks>
    private static readonly TimeSpan SquadPublishInterval = TimeSpan.FromSeconds(2);

    private async Task PublishObservationsAsync(CancellationToken cancellationToken)
    {
        var publishedSquad = _squad.Current.UpdatedUtc;
        var publishedSales = _fleaSales.Current.UpdatedUtc;
        while (!cancellationToken.IsCancellationRequested)
        {
            await DelayAsync(SquadPublishInterval, cancellationToken).ConfigureAwait(false);
            var squad = _squad.Current;
            if (squad.UpdatedUtc != publishedSquad)
            {
                publishedSquad = squad.UpdatedUtc;
                PublishSquad(squad);
                _logger.LogInformation("The party now has {Members} member(s).", squad.Members.Count);
            }

            var sales = _fleaSales.Current;
            if (sales.UpdatedUtc != publishedSales)
            {
                publishedSales = sales.UpdatedUtc;
                PublishFleaSales(sales);
                _logger.LogInformation("{Sales} flea sale(s) observed this session.", sales.Sales.Count);
            }
        }
    }

    private void PublishSquad(SquadSnapshot squad) =>
        _stateStore.Update(current => current with { Squad = squad });

    private void PublishFleaSales(FleaSalesSnapshot sales) =>
        _stateStore.Update(current => current with { FleaSales = sales });

    private async Task WatchScreenshotsAsync(string screenshotRoot, CancellationToken cancellationToken)
    {
        // The game writes the player's own position and heading into the screenshot filename.
        // That file is created by the game at the player's request; nothing is captured here.
        var offset = TimeZoneInfo.Local.GetUtcOffset(_timeProvider.GetUtcNow());
        var currentRoot = screenshotRoot;
        while (!cancellationToken.IsCancellationRequested)
        {
            var sourceGeneration = BeginScreenshotSourceGeneration();
            using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await foreach (var sighting in _screenshotWatcher.WatchAsync(currentRoot, source.Token)
                                   .ConfigureAwait(false))
                {
                    var path = sighting.Path;
                    if (sighting.IsSettled)
                    {
                        // Read whether or not the name carried coordinates: a screenshot of an
                        // item or an extract list is worth reading wherever it was taken. Menu
                        // and hideout shots simply have no position, which is ordinary and not
                        // worth a warning every time the player photographs their stash. This is
                        // what makes the game's own screenshot key do the whole job: one press
                        // gives the position when there is one, and whatever the picture shows
                        // either way, with no second shortcut and no window needing focus.
                        //
                        // Only the pixels wait here. The position left on the sighting below,
                        // one to two seconds earlier.
                        await QueueScreenshotScanAsync(path, sourceGeneration, source.Token).ConfigureAwait(false);
                        continue;
                    }

                    // The position is in the filename and costs a regex; the picture costs a
                    // decode and an OCR pass. Reading the picture first meant the marker waited
                    // seconds for a number that was already in hand, and because this loop is
                    // sequential the next screenshot's marker queued behind the previous scan
                    // as well — which is what "the marks take a second to show up" was.
                    //
                    // Remembered whether or not it parses, because the ones that do not are
                    // exactly the ones somebody needs to see.
                    RememberScreenshotName(Path.GetFileName(path));

                    // So: place the player, then read the picture.
                    if (_filenameParser.TryParseFile(path, offset, out var position) && position is not null)
                    {
                        _logger.LogInformation(
                            "Read a filename position on sight at {Taken:O}; exact coordinates are not logged.",
                            position.Timestamp);
                        await _coordinator.ApplyPositionAsync(position, source.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        // Said once per raid, with the name.
                        //
                        // This was silent on the grounds that menu and hideout screenshots carry
                        // no coordinates and a warning every time somebody photographs their stash
                        // is noise. True — and it also made the one case that matters invisible: a
                        // player whose game writes a filename this parser does not recognise takes
                        // screenshots all raid, sees the game confirm every one, and never appears
                        // on anybody's map, with nothing anywhere saying why.
                        //
                        // The name is the whole diagnosis. Once per raid is often enough to be
                        // found and rare enough not to bury the log.
                        if (Interlocked.Exchange(ref _unreadableNameReported, 1) == 0)
                        {
                            _logger.LogInformation(
                                "No position in the name of {Filename}. Ordinary for a menu or stash screenshot. " +
                                "If this was taken in a raid, the name is not in the shape this build expects " +
                                "and is worth reporting.",
                                MaskScreenshotName(Path.GetFileName(path)));
                        }
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("The screenshot source ended unexpectedly.");
                }

                return;
            }
            catch (CaptureSourceUnavailableException)
            {
                // Scans belong to one concrete folder generation. Keeping the log watcher alive
                // must not also let an OCR read against a vanished generation run indefinitely.
                InvalidateScreenshotSourceGeneration(sourceGeneration);
                await source.CancelAsync().ConfigureAwait(false);
                if (_watching is { } unavailable)
                {
                    _watching = unavailable with { ScreenshotRoot = null };
                    PublishWatching();
                }

                _logger.LogWarning(
                    "The configured screenshot source became unavailable and will be rediscovered.");
                await DrainScreenshotScansAsync().ConfigureAwait(false);
                var recovered = await RediscoverScreenshotSourceAsync(cancellationToken).ConfigureAwait(false);
                if (recovered?.ScreenshotRoot is not { } recoveredRoot)
                {
                    return;
                }

                currentRoot = recoveredRoot;
                if (_watching is { } current)
                {
                    _watching = new(
                        recovered.InstallRoot ?? current.InstallRoot,
                        current.LogRoot,
                        recoveredRoot,
                        recovered.Confidence);
                    PublishWatching();
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                InvalidateScreenshotSourceGeneration(sourceGeneration);
                await source.CancelAsync().ConfigureAwait(false);
                _logger.LogWarning(exception, "The Escape from Tarkov screenshot watcher stopped.");
                throw;
            }
            finally
            {
                InvalidateScreenshotSourceGeneration(sourceGeneration);
            }
        }
    }

    private async Task<EftPaths?> RediscoverScreenshotSourceAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await DelayAsync(SourceRediscoveryDelay, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            try
            {
                var paths = await _pathLocator.FindAsync(cancellationToken).ConfigureAwait(false);
                if (paths.ScreenshotRoot is not null)
                {
                    return paths;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not rediscover the screenshot source.");
            }
        }

        return null;
    }

    /// <summary>How often the screenshot folder is swept.</summary>
    /// <remarks>
    /// Hourly, which is far more often than needed to hold a folder steady and rare enough to
    /// cost nothing. The first sweep is delayed rather than run at startup so that launching
    /// the companion is never the moment files disappear; somebody who has just opened it to
    /// look at yesterday's screenshot gets to look at it.
    /// </remarks>
    private static readonly TimeSpan TidyInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Tidies old screenshots out of the game's folder, on a slow timer.
    /// </summary>
    /// <remarks>
    /// Here rather than in its own service because this is the one place that knows where the
    /// game keeps its screenshots, and because the folder is only worth sweeping while the
    /// companion is watching it. The settings are read on each sweep rather than cached, so
    /// turning the feature off in the interface takes effect at the next sweep instead of at
    /// the next restart.
    /// </remarks>
    /// <summary>Keeps the last few screenshot names, newest first.</summary>
    /// <remarks>
    /// Three is enough to see a pattern and few enough that nothing is being accumulated.
    /// </remarks>
    private void RememberScreenshotName(string name)
    {
        // The punctuation and letter shape diagnose a parser mismatch; coordinates and the
        // timestamp do not. Mask them before the name reaches runtime state or support text.
        name = MaskScreenshotName(name);
        _stateStore.Update(current =>
        {
            var names = new List<string>(4) { name };
            names.AddRange(current.RecentScreenshotNames.Where(
                existing => !string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)).Take(2));
            return current with { RecentScreenshotNames = names };
        });
    }

    private async Task TidyScreenshotsAsync(CancellationToken cancellationToken)
    {
        if (_retention is null || _retentionSettings is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            await DelayAsync(TidyInterval, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var settings = await _retentionSettings.GetAsync(cancellationToken).ConfigureAwait(false);
                var activeRoot = _watching?.ScreenshotRoot;
                if (activeRoot is null)
                {
                    continue;
                }

                var tidied = _retention.Tidy(activeRoot, settings);
                if (tidied > 0)
                {
                    _logger.LogInformation(
                        "Moved {Count} screenshot(s) older than {Hours}h to the recycle bin from {Folder}.",
                        tidied,
                        settings.SafeRetentionHours,
                        activeRoot);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Never worth stopping observation for. A folder that could not be tidied this
                // hour is tidied the next one.
                _logger.LogWarning(exception, "Could not tidy the screenshot folder.");
            }
        }
    }

    /// <summary>
    /// Reads a screenshot the player took, as though they had asked for a scan.
    /// </summary>
    /// <remarks>
    /// Deliberately unable to interrupt observation. A picture that cannot be decoded, or a
    /// recogniser that fails on it, must not stop the companion following the raid; losing a
    /// scan is a much smaller thing than losing the map.
    /// </remarks>
    private async Task ScanScreenshotAsync(
        string path,
        long sourceGeneration,
        long scanOrdinal,
        CancellationToken cancellationToken)
    {
        if (_imageLoader is null)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested
            || !IsScreenshotSourceGenerationActive(sourceGeneration))
        {
            return;
        }

        if (_captureSessions is not null)
        {
            try
            {
                var current = _stateStore.Current;
                var activeProfile = _profileRuntimeContext?.Current.ActiveProfile;
                var context = new CaptureContextMetadata(
                    activeWorkspace: null,
                    activeProfile: activeProfile?.Context.Identity.ProfileId.ToString("D"),
                    activeMap: current.Raid.MapId,
                    activePlan: null,
                    selectedEntity: null,
                    priorScan: null,
                    initiatingDevice: "desktop",
                    profileContext: activeProfile?.Context);
                var receipt = await _captureSessions.EnqueueAsync(
                        new(
                            CaptureDeliveryKind.WatchedFile,
                            new ScreenshotFileCaptureSource(path, _imageLoader),
                            context,
                            _timeProvider.GetUtcNow(),
                            CaptureCorrelationId.New()),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (receipt.Disposition != CaptureQueueDisposition.Accepted)
                {
                    _logger.LogWarning(
                        "A settled screenshot was not admitted to capture intake: {Code}.",
                        receipt.Code);
                }

                if (cancellationToken.IsCancellationRequested
                    || !IsScreenshotSourceGenerationActive(sourceGeneration))
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Capture sessions are additive until the composition owner connects the
                // reviewed result consumer. They must never remove the established scan/HUD
                // route merely because their own intake is unavailable.
                _logger.LogWarning(
                    exception,
                    "Could not enqueue the screenshot {Filename} for capture review.",
                    MaskScreenshotName(Path.GetFileName(path)));
            }
        }

        if (_scanUseCase is null)
        {
            return;
        }

        try
        {
            var image = await _imageLoader.LoadAsync(path, cancellationToken).ConfigureAwait(false);
            if (image is null)
            {
                _logger.LogInformation(
                    "The screenshot {Filename} could not be read as a picture.",
                    MaskScreenshotName(Path.GetFileName(path)));
                return;
            }

            if (cancellationToken.IsCancellationRequested
                || !IsScreenshotSourceGenerationActive(sourceGeneration))
            {
                return;
            }

            var outcome = await _scanUseCase.ScanImageAsync(image, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested
                || !IsScreenshotSourceGenerationActive(sourceGeneration))
            {
                return;
            }

            _logger.LogInformation(
                "Read {Filename} as {Context} with status {Status}.",
                MaskScreenshotName(Path.GetFileName(path)),
                outcome.Context,
                outcome.Status);

            PublishScreenshotOutcome(sourceGeneration, scanOrdinal, outcome);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Could not scan the screenshot {Filename}.",
                MaskScreenshotName(Path.GetFileName(path)));
        }
    }

    /// <summary>
    /// Carries the game's own display onto the raid, with how long each bar has ever been.
    /// </summary>
    /// <remarks>
    /// The longest seen is per raid, not for all time. A bar's full length is a fact about this
    /// screen at this resolution, and a player who changed either would otherwise be measured
    /// for the rest of the wipe against a bar that no longer exists. Cleared when a raid ends,
    /// alongside everything else that belongs to one.
    ///
    /// A frame with no display drawn is recorded as one. The game fades it out of roughly one
    /// screenshot in eight, and "the display was not in that frame" is a different statement
    /// from "the bar was empty" — showing the second when the first is true is how a companion
    /// tells somebody they are dying when they are not.
    /// </remarks>
    private void PublishScreenshotOutcome(long sourceGeneration, long scanOrdinal, ScanOutcome outcome)
    {
        var result = ScanExecutionResult.FromOutcome(outcome, "game screenshot");
        var publishesScan = result.IsWorthReporting;
        var hud = outcome.Recognition.Hud;
        if (!publishesScan && hud is null)
        {
            return;
        }

        // Decodes deliberately run concurrently so a slow OCR pass does not hold every later
        // screenshot behind it. Publication cannot be concurrent: without this generation and
        // ordinal fence, an older pass that finishes last replaces a newer item/HUD reading, and
        // a dependency that ignores cancellation can publish after its screenshot folder has
        // disappeared. The state write stays inside the gate so checking and publishing are one
        // ordered operation rather than a check-then-act race.
        lock (_screenshotPublicationGate)
        {
            if (_activeScreenshotSourceGeneration != sourceGeneration
                || scanOrdinal <= _lastPublishedScreenshotScanOrdinal)
            {
                return;
            }

            _stateStore.Update(current =>
            {
                var updated = publishesScan ? current with { Scan = result } : current;
                if (hud is null)
                {
                    return updated;
                }

                // Keyed to the raid rather than reset by a guess. A new raid id means the
                // lengths remembered describe a body the previous raid is over for.
                if (_longestRaidId != current.Raid.RaidId)
                {
                    _longestRaidId = current.Raid.RaidId;
                    _longestBars.Clear();
                }

                var bars = new List<RaidHudBar>(hud.Bars.Count);
                foreach (var bar in hud.Bars)
                {
                    var kind = bar.Kind.ToString();
                    var longest = Math.Max(_longestBars.GetValueOrDefault(kind), bar.Length);
                    _longestBars[kind] = longest;
                    bars.Add(new(kind, bar.Length, longest));
                }

                return updated with
                {
                    Raid = updated.Raid with
                    {
                        Hud = new(hud.IsPresent, hud.Detail, outcome.ObservedUtc, bars),
                    },
                };
            });

            _lastPublishedScreenshotScanOrdinal = scanOrdinal;
        }
    }

    /// <summary>The longest each bar has been drawn this raid, by colour.</summary>
    private readonly Dictionary<string, int> _longestBars = new(StringComparer.Ordinal);

    /// <summary>Which raid those lengths belong to, so they are cleared with it.</summary>
    private Guid? _longestRaidId;

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
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
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

    private static string MaskScreenshotName(string name) =>
        string.Concat(name.Select(character => char.IsAsciiDigit(character) ? '#' : character));

    private long BeginScreenshotSourceGeneration()
    {
        var generation = Interlocked.Increment(ref _nextScreenshotSourceGeneration);
        lock (_screenshotPublicationGate)
        {
            _activeScreenshotSourceGeneration = generation;
            _lastPublishedScreenshotScanOrdinal = 0;
        }

        return generation;
    }

    private void InvalidateScreenshotSourceGeneration(long generation)
    {
        lock (_screenshotPublicationGate)
        {
            if (_activeScreenshotSourceGeneration == generation)
            {
                _activeScreenshotSourceGeneration = 0;
            }
        }
    }

    private bool IsScreenshotSourceGenerationActive(long generation)
    {
        lock (_screenshotPublicationGate)
        {
            return _activeScreenshotSourceGeneration == generation;
        }
    }

    private Task QueueScreenshotScanAsync(
        string path,
        long sourceGeneration,
        CancellationToken cancellationToken)
    {
        lock (_screenshotScanGate)
        {
            if (_screenshotScans.Count >= MaximumPendingScreenshotScans)
            {
                // Filename evidence has already been applied. Refuse this expensive scan
                // explicitly instead of blocking enumeration and delaying every later position
                // behind the OCR backlog.
                _logger.LogWarning(
                    "The bounded screenshot scan queue is full; {Filename} was not scanned.",
                    MaskScreenshotName(Path.GetFileName(path)));
                return Task.CompletedTask;
            }
        }

        var scanOrdinal = Interlocked.Increment(ref _nextScreenshotScanOrdinal);
        var scan = ScanScreenshotAsync(path, sourceGeneration, scanOrdinal, cancellationToken);
        lock (_screenshotScanGate)
        {
            _screenshotScans.Add(scan);
        }

        _ = scan.ContinueWith(
            completed =>
            {
                _ = completed.Exception;
                lock (_screenshotScanGate)
                {
                    _screenshotScans.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return Task.CompletedTask;
    }

    private async Task DrainScreenshotScansAsync()
    {
        Task[] scans;
        lock (_screenshotScanGate)
        {
            scans = [.. _screenshotScans];
        }

        var drain = Task.WhenAll(scans);
        try
        {
            await drain.WaitAsync(ScreenshotScanDrainTimeout, _timeProvider).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "{Count} screenshot scan(s) did not stop within the bounded drain window; rediscovery will continue.",
                scans.Count(task => !task.IsCompleted));
            _ = drain.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }
}
