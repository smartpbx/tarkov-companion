using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>What the settings page needs to know about updating, and nothing else.</summary>
/// <param name="Status">A sentence for the player.</param>
/// <param name="CanDownload">Whether a newer build is waiting to be fetched.</param>
/// <param name="CanApply">Whether a build is fetched and waiting for a restart.</param>
public sealed record UpdateProgress(string Status, bool CanDownload = false, bool CanApply = false);

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
/// Everything degrades to doing nothing. A build run from a folder rather than installed
/// reports that and offers no buttons, which is what a developer running from a publish
/// directory should see.
/// </remarks>
public sealed class VelopackUpdateGateway
{
    /// <summary>
    /// Where builds come from.
    /// </summary>
    /// <remarks>
    /// Public GitHub releases rather than the group server. The server is one self-hosted
    /// container behind a tunnel, and an update channel that depends on it is a weaker promise
    /// than a CDN for no gain. Velopack can take any static HTTP source, so moving this later
    /// is one line if there is ever a reason to.
    /// </remarks>
    public const string ReleaseRepository = "https://github.com/smartpbx/tarkov-companion";

    private readonly ILogger<VelopackUpdateGateway>? _logger;
    private readonly Lazy<UpdateManager?> _manager;
    private UpdateInfo? _pending;

    public VelopackUpdateGateway(ILogger<VelopackUpdateGateway>? logger = null)
    {
        _logger = logger;
        _manager = new Lazy<UpdateManager?>(CreateManager);
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
    private UpdateManager? CreateManager()
    {
        try
        {
            // Pre-releases included: the rolling build is how this reaches the people who use
            // it, and there has never been a stable channel to hold back for.
            return new UpdateManager(new GithubSource(ReleaseRepository, null, prerelease: true));
        }
        catch (Exception exception)
        {
            _logger?.LogInformation(
                exception,
                "Updates are unavailable in this process; it was not started by the installed application.");
            return null;
        }
    }

    /// <summary>Whether this copy was installed, as opposed to run out of a folder.</summary>
    public bool IsInstalled => _manager.Value?.IsInstalled == true;

    /// <summary>The running version, or a plain statement that there is not one.</summary>
    public string InstalledBuild => _manager.Value is { IsInstalled: true } manager
        && manager.CurrentVersion is { } version
        ? $"Version {version}"
        : "Running from a folder, not installed";

    public async Task<UpdateProgress> CheckAsync(CancellationToken cancellationToken)
    {
        if (_manager.Value is not { IsInstalled: true } manager)
        {
            return new("This copy was run from a folder rather than installed, so it cannot update itself.");
        }

        try
        {
            _pending = await manager.CheckForUpdatesAsync().ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            return _pending is null
                ? new("You are on the newest build.")
                : new($"Version {_pending.TargetFullRelease.Version} is available.", CanDownload: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "Could not check for updates.");
            return new($"Could not check for updates: {exception.Message}");
        }
    }

    public async Task<UpdateProgress> DownloadAsync(CancellationToken cancellationToken)
    {
        if (_pending is not { } update || _manager.Value is not { } manager)
        {
            return new("Check for updates first.");
        }

        try
        {
            await manager.DownloadUpdatesAsync(update).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            return new(
                $"Version {update.TargetFullRelease.Version} is ready. It installs when the application closes.",
                CanApply: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "Could not download the update.");
            return new($"Could not download the update: {exception.Message}", CanDownload: true);
        }
    }

    /// <summary>
    /// Applies the update and reopens on the new build. Does not return if it succeeds.
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_pending is not { } update || _manager.Value is not { } manager)
        {
            return;
        }

        _logger?.LogInformation("Applying version {Version} and restarting.", update.TargetFullRelease.Version);
        manager.ApplyUpdatesAndRestart(update);
    }
}
