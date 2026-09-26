using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using TarkovCompanion.Application.Services.Profiles;
using TarkovCompanion.Application.Services.Raids;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;

namespace TarkovCompanion.Platform.Windows.Discovery;

public sealed record EftPathCandidates(
    IReadOnlyList<string> InstallRoots,
    IReadOnlyList<string> LogRoots,
    IReadOnlyList<string> ScreenshotRoots);

public interface IEftPathProbe
{
    EftPathCandidates GetCandidates();

    bool DirectoryExists(string path);

    /// <summary>
    /// When the newest image in a folder was written, or null when the folder holds none.
    /// </summary>
    /// <remarks>
    /// An install leaves several plausible screenshot folders in place and only writes to one
    /// of them. Taking whichever exists first can land on an empty folder the game abandoned,
    /// and then position never appears and discovery still reports success.
    /// </remarks>
    DateTimeOffset? NewestImageWrite(string path) => null;

    /// <summary>
    /// When the newest *.log file under a folder, at any depth, was written, or null when the
    /// folder holds none.
    /// </summary>
    /// <remarks>
    /// A reinstall to a different drive can leave the old install's Logs folder behind,
    /// existing but no longer written to. Existence alone cannot tell that folder from the one
    /// the game is actually using; nothing was ever read from it because nothing was ever
    /// there to read.
    /// </remarks>
    DateTimeOffset? NewestLogWrite(string path) => null;
}

public sealed class WindowsEftPathLocator(
    IEftPathProbe? probe = null,
    // Optional so a composition without stored settings still discovers, which is what every
    // test that builds this by hand relies on.
    IEftPathOverrideStore? overrides = null,
    TimeProvider? timeProvider = null,
    ILogger<WindowsEftPathLocator>? logger = null) : IEftPathLocator, IEftInstallDiscoverySource
{
    private readonly IEftPathProbe _probe = probe ?? new SystemEftPathProbe();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ILogger<WindowsEftPathLocator> _logger = logger ?? NullLogger<WindowsEftPathLocator>.Instance;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private readonly ConcurrentQueue<EftInstallDiscoveryChanged> _pendingChanges = new();
    private EftInstallDiscoverySnapshot? _current;
    private int _publishingChanges;

    public event Action<EftInstallDiscoveryChanged>? StateChanged;

    public EftInstallDiscoverySnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return _current ??= EftInstallDiscoverySnapshot.Uninitialized(UtcNow());
            }
        }
    }

    public async Task<EftPaths> FindAsync(CancellationToken cancellationToken)
    {
        var state = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (state.Status == EftInstallDiscoveryStatus.Unavailable)
        {
            throw new InvalidOperationException($"{state.Code}: {state.Detail}");
        }

        return state.Paths;
    }

    public async Task<EftInstallDiscoverySnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        EftInstallDiscoverySnapshot result;
        try
        {
            result = await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }

        PublishPendingChanges();
        return result;
    }

    private async Task<EftInstallDiscoverySnapshot> RefreshCoreAsync(CancellationToken cancellationToken)
    {
        EftPathOverrides named = EftPathOverrides.None;
        Exception? configurationError = null;
        if (overrides is not null)
        {
            try
            {
                named = await overrides.GetAsync(cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The game-folder settings reader returned no value.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Keep automatic discovery useful, but retain the configuration failure as the
                // primary state. Falling back used to erase the only evidence that malformed
                // discovery settings needed repair.
                configurationError = exception;
                _logger.LogWarning(exception, "Saved EFT game-folder settings could not be read; automatic discovery will continue.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var previous = Current;
            var candidates = _probe.GetCandidates()
                ?? throw new InvalidDataException("The EFT path probe returned no candidates.");
            var previousInstallExists = previous.Paths.InstallRoot is { } previousInstall &&
                _probe.DirectoryExists(previousInstall);
            var install = FirstExisting(candidates.InstallRoots) ??
                (previousInstallExists ? previous.Paths.InstallRoot : null);
            var logs = Named(named.LogRoot) ?? BestLogRoot(candidates.LogRoots);
            var screenshots = Named(named.ScreenshotRoot) ?? BestScreenshotRoot(candidates.ScreenshotRoots);
            var foundCount = new[] { install, logs, screenshots }.Count(path => path is not null);
            var confidence = foundCount switch
            {
                3 => new Confidence(0.95),
                2 => new Confidence(0.80),
                1 => new Confidence(0.60),
                _ => Confidence.Unknown,
            };
            var paths = new EftPaths(install, logs, screenshots, confidence);
            if (configurationError is not null)
            {
                return Commit(
                    EftInstallDiscoveryStatus.InvalidConfiguration,
                    paths,
                    "eft-discovery-configuration-invalid",
                    ErrorDetail(
                        "Saved game-folder settings could not be read",
                        configurationError,
                        "Re-save them in Settings, then retry discovery."),
                    FaultOf(configurationError, "Saved game-folder settings could not be read", "Re-save them in Settings, then retry discovery."));
            }

            if (install is not null)
            {
                var recovered = (previous.Status is EftInstallDiscoveryStatus.Missing
                    or EftInstallDiscoveryStatus.InvalidConfiguration
                    or EftInstallDiscoveryStatus.Invalidated
                    or EftInstallDiscoveryStatus.Unavailable) ||
                    string.Equals(previous.Code, "eft-discovery-recovered", StringComparison.Ordinal);
                return Commit(
                    EftInstallDiscoveryStatus.Ready,
                    paths,
                    recovered ? "eft-discovery-recovered" : "eft-discovery-ready",
                    recovered
                        ? "Escape from Tarkov installation discovery recovered; file observation can resume."
                        : "Escape from Tarkov installation and file roots are available.");
            }

            var disappeared = previous.Status == EftInstallDiscoveryStatus.Invalidated ||
                (previous.Status == EftInstallDiscoveryStatus.Ready &&
                 previous.Paths.InstallRoot is not null &&
                 !previousInstallExists);
            return Commit(
                disappeared ? EftInstallDiscoveryStatus.Invalidated : EftInstallDiscoveryStatus.Missing,
                paths,
                disappeared ? "eft-install-disappeared" : "eft-install-missing",
                disappeared
                    ? "The selected Escape from Tarkov installation is no longer available. Reconnect its drive, reinstall, or choose valid folders in Settings."
                    : "Escape from Tarkov is not installed or could not be found. Install it or choose valid folders in Settings, then retry discovery.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "EFT installation discovery failed.");
            return Commit(
                EftInstallDiscoveryStatus.Unavailable,
                new EftPaths(null, null, null, Confidence.Unknown),
                "eft-discovery-unavailable",
                ErrorDetail(
                    "Escape from Tarkov installation discovery failed",
                    exception,
                    "Retry discovery or choose valid folders in Settings."),
                FaultOf(exception, "Escape from Tarkov installation discovery failed", "Retry discovery or choose valid folders in Settings."));
        }
    }

    public async IAsyncEnumerable<EftInstallDiscoverySnapshot> WatchAsync(
        TimeSpan interval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Discovery interval must be positive.");
        }

        var initial = await RefreshAsync(cancellationToken).ConfigureAwait(false);
        yield return initial;
        using var timer = new PeriodicTimer(interval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var priorRevision = Current.Revision;
            var observed = await RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (observed.Revision != priorRevision)
            {
                yield return observed;
            }
        }
    }

    private EftInstallDiscoverySnapshot Commit(
        EftInstallDiscoveryStatus status,
        EftPaths paths,
        string code,
        string detail,
        string? fault = null)
    {
        EftInstallDiscoveryChanged? change = null;
        EftInstallDiscoverySnapshot result;
        lock (_stateGate)
        {
            var current = _current ??= EftInstallDiscoverySnapshot.Uninitialized(UtcNow());
            var changed = current.Status != status ||
                !Equals(current.Paths, paths) ||
                !string.Equals(current.Code, code, StringComparison.Ordinal) ||
                !string.Equals(current.Detail, detail, StringComparison.Ordinal);
            result = new(
                changed ? checked(current.Revision + 1) : current.Revision,
                status,
                paths,
                UtcNow(),
                code,
                detail)
            {
                Fault = fault,
            };
            _current = result;
            if (changed)
            {
                change = new(result);
                _pendingChanges.Enqueue(change);
            }
        }

        return result;
    }

    private void PublishPendingChanges()
    {
        while (Interlocked.CompareExchange(ref _publishingChanges, 1, 0) == 0)
        {
            try
            {
                while (_pendingChanges.TryDequeue(out var change))
                {
                    Deliver(change);
                }
            }
            finally
            {
                Volatile.Write(ref _publishingChanges, 0);
            }

            if (_pendingChanges.IsEmpty)
            {
                return;
            }
        }
    }

    private void Deliver(EftInstallDiscoveryChanged change)
    {
        if (StateChanged is not { } handlers)
        {
            return;
        }

        foreach (Action<EftInstallDiscoveryChanged> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(change);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "An EFT install-discovery subscriber failed after revision {Revision} was published.",
                    change.Snapshot.Revision);
            }
        }
    }

    private DateTimeOffset UtcNow() => _timeProvider.GetUtcNow().ToUniversalTime();

    private static string ErrorDetail(string prefix, Exception exception, string recovery) =>
        $"{prefix} ({FaultOf(exception, prefix, recovery)}). {recovery}";

    /// <summary>
    /// The exception's own message, bounded and on one line, as <see cref="ErrorDetail"/> puts it
    /// between its words. [#314] Kept apart so the App can say the words around it.
    /// </summary>
    private static string FaultOf(Exception exception, string prefix, string recovery)
    {
        const int maximumDetailLength = 2048;
        var fixedLength = prefix.Length + recovery.Length + 5;
        var maximumMessageLength = Math.Max(0, maximumDetailLength - fixedLength);
        var rawMessage = exception.Message;
        if (rawMessage.Length > maximumMessageLength)
        {
            rawMessage = rawMessage[..maximumMessageLength];
        }

        var message = new string(rawMessage
            .Select(character => char.IsControl(character) ? ' ' : character)
            .ToArray()).Trim();
        if (message.Length == 0)
        {
            message = "no additional detail";
        }

        return message;
    }

    /// <summary>
    /// A folder the player named, when it is really there.
    /// </summary>
    /// <remarks>
    /// It wins outright over everything discovery found: somebody who has typed a path has
    /// answered the question the guessing exists to answer. A path that does not exist is
    /// ignored rather than honoured, so a typo leaves the guesses working instead of leaving
    /// the companion watching nothing.
    /// </remarks>
    private string? Named(string? path) =>
        !string.IsNullOrWhiteSpace(path) && _probe.DirectoryExists(path) ? path : null;

    private string? FirstExisting(IEnumerable<string> paths) =>
        Existing(paths).FirstOrDefault();

    /// <summary>
    /// Picks the log folder the game is actually writing to.
    /// </summary>
    /// <remarks>
    /// The same reasoning as <see cref="BestScreenshotRoot"/>, and for the same underlying
    /// problem: a machine can have more than one folder on disk that looks like a real EFT log
    /// root -- a leftover from a reinstall to a different drive, most often -- and the first
    /// one that merely exists is not necessarily the one the game writes to now. Where more
    /// than one candidate exists, the one holding the newest log line wins.
    /// </remarks>
    private string? BestLogRoot(IEnumerable<string> paths)
    {
        var existing = Existing(paths).ToArray();
        if (existing.Length <= 1)
        {
            return existing.FirstOrDefault();
        }

        var newest = existing
            .Select(path => (Path: path, Written: _probe.NewestLogWrite(path)))
            .Where(entry => entry.Written is not null)
            .OrderByDescending(entry => entry.Written!.Value)
            .Select(entry => entry.Path)
            .FirstOrDefault();
        return newest ?? existing[0];
    }

    /// <summary>
    /// Picks the screenshot folder the game is actually using.
    /// </summary>
    /// <remarks>
    /// Where more than one candidate exists, the one holding the most recent screenshot wins.
    /// Ordering the candidates by hand cannot settle this: which folder the game writes to
    /// depends on the player's own settings. When none of them holds an image there is nothing
    /// to choose between, so the usual first-existing order stands.
    /// </remarks>
    private string? BestScreenshotRoot(IEnumerable<string> paths)
    {
        var existing = Existing(paths).ToArray();
        if (existing.Length <= 1)
        {
            return existing.FirstOrDefault();
        }

        var newest = existing
            .Select(path => (Path: path, Written: _probe.NewestImageWrite(path)))
            .Where(entry => entry.Written is not null)
            .OrderByDescending(entry => entry.Written!.Value)
            .Select(entry => entry.Path)
            .FirstOrDefault();
        return newest ?? existing[0];
    }

    private IEnumerable<string> Existing(IEnumerable<string> paths) =>
        paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(_probe.DirectoryExists);
}

public sealed class SystemEftPathProbe : IEftPathProbe
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg",
    };

    private readonly Func<EftDiscoveryEnvironment>? _environment;

    public SystemEftPathProbe()
    {
    }

    /// <summary>
    /// [#712 1-13] Guesses from the environment given rather than from Windows: a fixture tree
    /// stands in for a machine, and the rest of this probe (existence, newest write) is the real
    /// file system.
    /// </summary>
    public SystemEftPathProbe(Func<EftDiscoveryEnvironment> environment)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
    }

    public EftPathCandidates GetCandidates()
    {
        if (_environment is not null)
        {
            return EftPathCandidateBuilder.Build(_environment());
        }

        if (!OperatingSystem.IsWindows())
        {
            return new([], [], []);
        }

        return EftPathCandidateBuilder.Build(WindowsEnvironment());
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public DateTimeOffset? NewestImageWrite(string path)
    {
        try
        {
            var newest = new DirectoryInfo(path)
                .EnumerateFiles()
                .Where(file => ImageExtensions.Contains(file.Extension))
                .Select(file => file.LastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return newest == DateTime.MinValue ? null : new DateTimeOffset(newest, TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public DateTimeOffset? NewestLogWrite(string path)
    {
        try
        {
            var newest = new DirectoryInfo(path)
                .EnumerateFiles("*.log", SearchOption.AllDirectories)
                .Select(file => file.LastWriteTimeUtc)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            return newest == DateTime.MinValue ? null : new DateTimeOffset(newest, TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }


    [SupportedOSPlatform("windows")]
    private static EftDiscoveryEnvironment WindowsEnvironment()
    {
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return new(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            systemDrive,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            FixedDrives(),
            OneDriveRoots().ToArray(),
            RegistryInstallRoots().ToArray(),
            SteamRoots().ToArray(),
            RunningGameRoots().ToArray());
    }

    /// <summary>Every fixed, ready drive: the launcher lets a player install the game to any of them.</summary>
    private static string[] FixedDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Steam's own folder, which holds its library list.</summary>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> SteamRoots()
    {
        var found = new List<string>();
        try
        {
            using (var user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
            {
                if (user?.GetValue("SteamPath") is string steamPath && !string.IsNullOrWhiteSpace(steamPath))
                {
                    found.Add(steamPath.Replace('/', '\\'));
                }
            }

            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Valve\Steam") ?? baseKey.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
                if (key?.GetValue("InstallPath") is string installPath && !string.IsNullOrWhiteSpace(installPath))
                {
                    found.Add(installPath);
                }
            }
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No readable Steam key: the per-drive SteamLibrary guesses still stand.
        }

        return found;
    }

    /// <summary>
    /// Wherever OneDrive says it has put the user's folders.
    /// </summary>
    /// <remarks>
    /// OneDrive sets these itself, so they are right when it has redirected Documents and
    /// absent when it has not. Reading them beats guessing at a folder name, which changes
    /// with the account: a personal account is "OneDrive" and a work one is "OneDrive -
    /// Contoso".
    /// </remarks>
    private static IEnumerable<string> OneDriveRoots() =>
        new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(Environment.GetEnvironmentVariable)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);

    /// <summary>
    /// Where the game is installed, according to the game itself.
    /// </summary>
    /// <remarks>
    /// A candidate list only ever guesses, and a wrong guess that happens to exist is worse
    /// than no guess: discovery reports success and then watches a folder the game never
    /// writes to. If the game is running it can simply be asked, and that answer is right by
    /// construction whatever drive it was installed on.
    /// </remarks>
    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RunningGameRoots()
    {
        // The game process itself is protected by its anti-cheat and reports an empty path
        // even to an elevated reader, so it cannot be asked directly. Its anti-cheat sibling
        // and the launcher are readable and sit in the same install directory.
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("EscapeFromTarkov_BE")
                .Concat(Process.GetProcessesByName("EscapeFromTarkov"))
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            yield break;
        }

        foreach (var process in processes)
        {
            string? directory = null;
            try
            {
                directory = Path.GetDirectoryName(process.MainModule?.FileName);
            }
            catch (Exception exception) when (exception is Win32Exception
                                              or InvalidOperationException
                                              or NotSupportedException)
            {
                // A 64-bit game read from a process without rights to it; fall through to
                // the static candidates rather than failing discovery.
            }
            finally
            {
                process.Dispose();
            }

            if (!string.IsNullOrWhiteSpace(directory))
            {
                yield return directory;
            }
        }
    }

    /// <summary>Escape from Tarkov's Steam app id, which names its uninstall key.</summary>
    private const string SteamAppId = "3932890";

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryInstallRoots()
    {
        // The launcher's own key, then Steam's ("Steam App <id>") for the Steam release.
        string[] uninstallKeys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + SteamAppId,
        ];
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            foreach (var uninstallKey in uninstallKeys)
            {
                using var key = baseKey.OpenSubKey(uninstallKey);
                if (key?.GetValue("InstallLocation") is string path && !string.IsNullOrWhiteSpace(path))
                {
                    yield return path;
                }
            }
        }
    }
}
