using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>What the settings page needs to know about updating, and nothing else.</summary>
/// <param name="Status">A sentence for the player.</param>
/// <param name="CanDownload">Whether a newer build is waiting to be fetched.</param>
/// <param name="CanApply">Whether a build is fetched and waiting for a restart.</param>
/// <param name="Available">The newer build's version, when there is one.</param>
/// <param name="Failed">Whether the feed could not be asked, so "nothing newer" is not known.</param>
public sealed record UpdateProgress(
    string Status,
    bool CanDownload = false,
    bool CanApply = false,
    string? Available = null,
    bool Failed = false);

/// <summary>
/// Installs and updates the application in place.
/// </summary>
/// <remarks>
/// This replaces a hand-written updater that never once completed a swap on a real machine.
/// Four attempts failed for the same underlying reason rather than four different ones: the
/// application was unpacked from a zip onto a OneDrive-synced Desktop, where the sync client
/// holds handles on the folder and its files continuously. A directory rename cannot succeed
/// there, and mirroring file by file only moved the failure to whichever file was locked when
/// it arrived. The answer was never a better script. It was to stop installing there.
///
/// Velopack installs per user under LocalAppData, which nothing syncs and nothing locks, and
/// it owns the restart, so the running process is gone before its files are touched. That is
/// the whole reason for taking a dependency rather than writing a fifth script.
///
/// It follows the rough channel on the relay (<see cref="UpdateChannel.Rough"/>). For a week the
/// installed updater pointed at nothing, because the only feed was the signed private ring and
/// that is switched off until its environments and signing identity exist. Meanwhile every test
/// build was a zip extracted by hand into a fresh, empty data folder. The rough channel is the
/// shorter road: an unsigned feed whose packages are checked against the SHA256 it lists
/// (<see cref="HashVerifiedUpdateSource"/>). The signed ring replaces it, not the reverse.
///
/// Everything degrades to doing nothing. A build run from a folder rather than installed
/// reports that and offers no buttons, which is what a developer running from a publish
/// directory should see.
/// </remarks>
public sealed class VelopackUpdateGateway
{
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromMinutes(30);

    private readonly ILogger? _logger;
    private readonly Lazy<UpdateManager?> _manager;
    private UpdateInfo? _pending;
    private UpdateInfo? _verified;

    public VelopackUpdateGateway(ILogger<VelopackUpdateGateway>? logger = null)
        : this(UpdateChannel.FromEnvironment(), source: null, locator: null, logger)
    {
    }

    private VelopackUpdateGateway(
        UpdateChannel channel,
        IUpdateSource? source,
        IVelopackLocator? locator,
        ILogger? logger)
    {
        Channel = channel;
        _logger = logger;
        _manager = new Lazy<UpdateManager?>(() => CreateManager(source, locator));
    }

    /// <summary>
    /// A gateway over a given feed and a given idea of what is installed.
    /// </summary>
    /// <remarks>
    /// A factory rather than a second public constructor so the container, which picks a
    /// constructor by what it can resolve, only ever sees one.
    /// </remarks>
    public static VelopackUpdateGateway Create(
        UpdateChannel channel,
        IUpdateSource source,
        IVelopackLocator locator,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(locator);
        return new VelopackUpdateGateway(channel, source, locator, logger);
    }

    /// <summary>
    /// Builds the updater, or decides there is not one.
    /// </summary>
    /// <remarks>
    /// Lazy and forgiving, for a specific reason. Constructing an UpdateManager throws unless
    /// VelopackApp.Build().Run() has already run, and that only happens in the real entry
    /// point. Building it eagerly in the constructor therefore took down anything that
    /// resolved this type without a full application around it, which is every test that
    /// composes the main window, and it did so at composition time where the failure looks
    /// like the whole container is broken rather than like updates being unavailable.
    ///
    /// An application that cannot update itself is a small loss. An application that will not
    /// start is a total one, so this never throws.
    /// </remarks>
    private UpdateManager? CreateManager(IUpdateSource? source, IVelopackLocator? locator)
    {
        try
        {
            // One client for the life of the process, never disposed: it is created at most
            // once, and only by a build that was installed.
            source ??= new HashVerifiedUpdateSource(Channel.OpenTransport(CreateClient()), _logger);
            return new UpdateManager(source, options: null, locator);
        }
        catch (Exception exception)
        {
            _logger?.LogInformation(
                exception,
                "Not started by the installed application, so it cannot update");
            return null;
        }
    }

    /// <summary>
    /// Named, because the relay sits behind a proxy that may challenge a request with no agent.
    /// </summary>
    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = FeedTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovCompanion-Updater/1.0");
        return client;
    }

    /// <summary>Which feed this build follows.</summary>
    public UpdateChannel Channel { get; }

    /// <summary>Whether this copy was installed, as opposed to run out of a folder.</summary>
    public bool IsInstalled => _manager.Value?.IsInstalled == true;

    /// <summary>The running version, or a plain statement that there is not one.</summary>
    public string InstalledBuild => _manager.Value is { IsInstalled: true } manager
        && manager.CurrentVersion is { } version
        ? $"Version {version}"
        : "Running from a folder, not installed";

    public async Task<UpdateProgress> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_manager.Value is not { IsInstalled: true } manager)
        {
            return new UpdateProgress("Run from a folder, so it cannot update itself");
        }

        _verified = null;
        try
        {
            _pending = await manager.CheckForUpdatesAsync().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _pending = null;
            _logger?.LogWarning(exception, "Could not check {Feed} for a newer build", Channel.Feed);
            return new($"Could not check · {exception.Message}", Failed: true);
        }

        if (_pending is not { } update)
        {
            return new("Up to date");
        }

        var available = update.TargetFullRelease.Version.ToString();
        _logger?.LogInformation("{Installed}; {Available} is available", InstalledBuild, available);
        return new($"{available} is available", CanDownload: true, Available: available);
    }

    /// <summary>
    /// Fetches the waiting build and checks it against the feed. The running build is untouched.
    /// </summary>
    /// <param name="progress">Told 0 to 100 as the download goes.</param>
    public async Task<UpdateProgress> DownloadAsync(CancellationToken cancellationToken, Action<int>? progress = null)
    {
        if (_pending is not { } update || _manager.Value is not { } manager)
        {
            return new("Check for updates first.");
        }

        var available = update.TargetFullRelease.Version.ToString();
        try
        {
            _verified = null;
            await manager.DownloadUpdatesAsync(update, progress, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _verified = update;
            return new($"{available} is ready · it installs when this restarts", CanApply: true, Available: available);
        }
        catch (UpdateHashMismatchException exception)
        {
            // Logged with both hashes where it was refused. Said here without them: two
            // sixty-four character strings are not something anybody reads on a settings page.
            _logger?.LogError(exception, "Refused {Available}: the download did not match the feed", available);
            return new(
                "Refused · the download did not match the feed, so nothing was installed",
                CanDownload: true,
                Available: available);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "Could not download");
            return new($"Could not download · {exception.Message}", CanDownload: true, Available: available);
        }
    }

    /// <summary>
    /// Applies the update and reopens on the new build. Does not return if it succeeds.
    /// </summary>
    /// <remarks>
    /// Only a build whose download finished and matched the feed. Asked to apply a package that
    /// is not on disk, the updater does not refuse: it runs anyway and applies whatever package
    /// it finds. So a refused or interrupted download must never get this far, whatever a
    /// caller's buttons happen to allow.
    /// </remarks>
    public void ApplyAndRestart()
    {
        if (_verified is not { } update || _manager.Value is not { } manager)
        {
            return;
        }

        _logger?.LogInformation("Applying version {Version} and restarting.", update.TargetFullRelease.Version);
        manager.ApplyUpdatesAndRestart(update);
    }
}
