using TarkovCompanion.App.Localization;
using Microsoft.Extensions.Logging;
using TarkovCompanion.App.Services.Diagnostics;
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
/// <param name="Notes">The newer build's release notes, as the feed carries them (markdown), when it has any.</param>
/// <param name="Held">Whether a newer build exists and is kept back by "stay on this version".</param>
public sealed record UpdateProgress(
    string Status,
    bool CanDownload = false,
    bool CanApply = false,
    string? Available = null,
    bool Failed = false,
    string? Notes = null,
    bool Held = false);

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
public sealed partial class VelopackUpdateGateway
{
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromMinutes(30);

    private readonly ILogger? _logger;
    private readonly TarkovCompanion.Core.Network.INetworkPolicy? _network;

    /// <summary>
    /// [#292] "Local only · off" or "Update checks · off" when the policy says no, before the feed is asked.
    /// </summary>
    private UpdateProgress? Blocked() => _network?.Check(TarkovCompanion.Core.Network.NetworkService.UpdateChecks) switch
    {
        TarkovCompanion.Core.Network.NetworkVerdict.LocalOnly =>
            new(SetupText.NetworkState(TarkovCompanion.Core.Network.NetworkVerdict.LocalOnly)),
        TarkovCompanion.Core.Network.NetworkVerdict.SwitchedOff => new(SetupText.NetworkUpdateOff),
        _ => null,
    };
    private readonly Lazy<UpdateManager?> _manager;
    private UpdateInfo? _pending;
    private VelopackAsset? _verified;

    public VelopackUpdateGateway(
        ILogger<VelopackUpdateGateway>? logger = null,
        AppDataPaths? paths = null,
        // [#292] Local only and the update-check switch; null allows everything (tests, tools).
        TarkovCompanion.Core.Network.INetworkPolicy? network = null)
        : this(UpdateChannel.FromEnvironment(), source: null, locator: null, logger, network: network)
    {
        State = paths is null ? null : UpdateStateFile.For(paths, logger);
    }

    private VelopackUpdateGateway(
        UpdateChannel channel,
        IUpdateSource? source,
        IVelopackLocator? locator,
        ILogger? logger,
        IUpdateFeedTransport? transport = null,
        TarkovCompanion.Core.Network.INetworkPolicy? network = null)
    {
        Channel = channel;
        _network = network;
        _logger = logger;
        _locator = locator;
        _transport = new Lazy<IUpdateFeedTransport>(() => transport ?? Channel.OpenTransport(CreateClient()));
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
        ILogger? logger = null,
        IUpdateFeedTransport? transport = null,
        IUpdateStateStore? state = null,
        TarkovCompanion.Core.Network.INetworkPolicy? network = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(locator);
        return new VelopackUpdateGateway(channel, source, locator, logger, transport, network) { State = state };
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
            source ??= new HashVerifiedUpdateSource(_transport.Value, _logger);
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
    private HttpClient CreateClient()
    {
        var client = new HttpClient(TarkovCompanion.Application.Services.Network.NetworkPolicyHandler.Wrap(
            _network,
            TarkovCompanion.Core.Network.NetworkService.UpdateChecks))
        {
            Timeout = FeedTimeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TarkovCompanion-Updater/1.0");
        return client;
    }

    /// <summary>Which feed this build follows.</summary>
    public UpdateChannel Channel { get; }

    /// <summary>Whether this copy was installed, as opposed to run out of a folder.</summary>
    public bool IsInstalled => RenderPending is not null || _manager.Value?.IsInstalled == true;

    /// <summary>
    /// For the render tool only: a build that did not apply, so Setup > Updates can be looked at
    /// in that state on a machine with no installation and no updater.
    /// </summary>
    internal static PendingUpdate? RenderPending { get; set; }

    /// <summary>
    /// A newer build already downloaded when this one started, which means it was tried and did
    /// not apply. Null when there is none. Makes that build the one <see cref="ApplyAndRestart"/> applies.
    /// </summary>
    /// <remarks>
    /// The package got into <c>packages\</c> through <see cref="DownloadAsync"/> in an earlier
    /// run, which does not keep a file that did not match the feed, and the updater would apply
    /// it by itself at the next start in any case.
    /// </remarks>
    /// <param name="readApplyError">Reads the updater's log for an application id; the real one when null.</param>
    public PendingUpdate? PendingFromLastAttempt(Func<string, string?>? readApplyError = null)
    {
        if (RenderPending is { } demo)
        {
            return demo;
        }

        try
        {
            if (_manager.Value is not { IsInstalled: true } manager || manager.UpdatePendingRestart is not { } waiting)
            {
                return null;
            }

            _verified = waiting;
            readApplyError ??= appId => VelopackApplyLog.ReadLastApplyError(VelopackApplyLog.PathFor(appId));
            var pending = new PendingUpdate(waiting.Version.ToString(), readApplyError(manager.AppId ?? "TarkovCompanionDesktop"));
            _logger?.LogWarning("{Status}", pending.Status);
            return pending;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger?.LogInformation(exception, "Could not tell whether a downloaded build is waiting");
            return null;
        }
    }

    /// <summary>The running version, and whether an installer put it there.</summary>
    /// <remarks>
    /// A build run from a folder still has a version, and used to keep it to itself: the page
    /// said only that it was not installed, which is no help to somebody asked which build
    /// they are looking at. An installed build shows the installer's number, because that is
    /// the one the updater compares against the feed.
    /// </remarks>
    public string InstalledBuild => _manager.Value is { IsInstalled: true } manager
        && manager.CurrentVersion is { } version
        ? SetupText.UpdateInstalledVersion(version)
        : SetupText.UpdateRunningFromFolder(AppBuildIdentity.Current.Version);

    public async Task<UpdateProgress> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_manager.Value is not { IsInstalled: true } manager)
        {
            return new UpdateProgress(SetupText.UpdateCannotUpdateFolder);
        }

        if (Blocked() is { } blocked)
        {
            return blocked;
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
            return new(SetupText.UpdateCouldNotCheck(exception.Message), Failed: true);
        }

        if (_pending is not { } update)
        {
            return new(SetupText.UpdateUpToDate);
        }

        var available = update.TargetFullRelease.Version.ToString();
        _logger?.LogInformation("{Installed}; {Available} is available", InstalledBuild, available);
        if (HeldByPin(manager, available) is { } held)
        {
            _pending = null;
            return held;
        }

        return new(
            SetupText.UpdateAvailable(available),
            CanDownload: true,
            Available: available,
            Notes: update.TargetFullRelease.NotesMarkdown);
    }

    /// <summary>
    /// Fetches the waiting build and checks it against the feed. The running build is untouched.
    /// </summary>
    /// <param name="progress">Told 0 to 100 as the download goes.</param>
    public async Task<UpdateProgress> DownloadAsync(CancellationToken cancellationToken, Action<int>? progress = null)
    {
        if (_pending is not { } update || _manager.Value is not { } manager)
        {
            return new(SetupText.UpdateCheckFirst);
        }

        if (Blocked() is { } blocked)
        {
            return blocked with { CanDownload = true, Available = update.TargetFullRelease.Version.ToString() };
        }

        var available = update.TargetFullRelease.Version.ToString();
        try
        {
            _verified = null;
            _applyManager = null;
            _pendingPin = null;
            // Off the calling thread (#888): this copies and hashes a ~100 MB package, and the caller
            // is the Setup button on the UI thread, which froze for seconds on a busy disk. Awaited,
            // so the kept copy still exists before the download can delete the package it copies.
            await Task.Run(() => KeepInstalledPackage(manager), cancellationToken).ConfigureAwait(true);
            await manager.DownloadUpdatesAsync(update, progress, cancellationToken).ConfigureAwait(true);
            cancellationToken.ThrowIfCancellationRequested();
            _verified = update.TargetFullRelease;
            return new(SetupText.UpdateReady(available), CanApply: true, Available: available);
        }
        catch (UpdateHashMismatchException exception)
        {
            // Logged with both hashes where it was refused. Said here without them: two
            // sixty-four character strings are not something anybody reads on a settings page.
            _logger?.LogError(exception, "Refused {Available}: the download did not match the feed", available);
            return new(
                SetupText.UpdateRefused,
                CanDownload: true,
                Available: available);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogWarning(exception, "Could not download");
            return new(SetupText.UpdateCouldNotDownload(exception.Message), CanDownload: true, Available: available);
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
        if (_verified is not { } update || (_applyManager ?? _manager.Value) is not { } manager)
        {
            return;
        }

        RecordApply(update);
        _logger?.LogInformation("Applying version {Version} and restarting.", update.Version);
        if (HandOver is not { } handOver)
        {
            manager.ApplyUpdatesAndRestart(update);
            return;
        }

        // #599. The updater is started last and this process is ended outright, instead of the
        // library starting it first and then calling Environment.Exit in the middle of everything.
        handOver.Run(
            update.Version.ToString(),
            () => manager.WaitExitThenApplyUpdates(update, silent: false, restart: true));
    }

    /// <summary>
    /// How the running application gets out of the updater's way. Set by the entry point.
    /// </summary>
    /// <remarks>
    /// Null anywhere there is no application to stop (tests, tools), which leaves the library's
    /// own start-then-exit. See <see cref="UpdateHandOver"/> for why the real application must
    /// not use that.
    /// </remarks>
    public UpdateHandOver? HandOver { get; set; }
}
