using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Application.Services.Raids;
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
}

public sealed class WindowsEftPathLocator(
    IEftPathProbe? probe = null,
    // Optional so a composition without stored settings still discovers, which is what every
    // test that builds this by hand relies on.
    IEftPathOverrideStore? overrides = null) : IEftPathLocator
{
    private readonly IEftPathProbe _probe = probe ?? new SystemEftPathProbe();

    public async Task<EftPaths> FindAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var named = overrides is null
            ? EftPathOverrides.None
            : await overrides.GetAsync(cancellationToken).ConfigureAwait(false);
        var candidates = _probe.GetCandidates();
        var install = FirstExisting(candidates.InstallRoots);
        var logs = Named(named.LogRoot) ?? FirstExisting(candidates.LogRoots);
        var screenshots = Named(named.ScreenshotRoot) ?? BestScreenshotRoot(candidates.ScreenshotRoots);
        var foundCount = new[] { install, logs, screenshots }.Count(path => path is not null);
        var confidence = foundCount switch
        {
            3 => new Confidence(0.95),
            2 => new Confidence(0.80),
            1 => new Confidence(0.60),
            _ => Confidence.Unknown,
        };
        return new EftPaths(install, logs, screenshots, confidence);
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

    public EftPathCandidates GetCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], [], []);
        }

        return GetWindowsCandidates();
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


    [SupportedOSPlatform("windows")]
    private static EftPathCandidates GetWindowsCandidates()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var localLow = Directory.GetParent(localAppData)?.FullName is { } appData
            ? Path.Combine(appData, "LocalLow")
            : localAppData;
        var systemDrive = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        var installRoots = RunningGameRoots()
            .Concat(RegistryInstallRoots())
            .Concat(new[]
            {
                // The launcher's own default is "Battlestate Games\\Escape from Tarkov" on the
                // system drive. Only the abbreviated "EFT" folder was listed here, which does
                // not exist on an ordinary install.
                Path.Combine(systemDrive, "Battlestate Games", "Escape from Tarkov"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battlestate Games", "Escape from Tarkov"),
                Path.Combine(systemDrive, "Battlestate Games", "EFT"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Battlestate Games", "EFT"),
            })
            .ToArray();

        // Every folder a personal Documents or Pictures could be, because the one the API
        // reports is only right when nothing has moved it.
        //
        // OneDrive redirects Documents on the machine this was written against and does not on
        // the machine of the first person to install it, and those two cases produce different
        // paths from the same call. Worse, a machine can have both at once: OneDrive owns the
        // known folder while the game, configured earlier, still writes into the original. So
        // both are offered and the one holding the newest screenshot wins.
        var personalRoots = new[]
            {
                documents,
                pictures,
                Path.Combine(profile, "Documents"),
                Path.Combine(profile, "Pictures"),
                Path.Combine(profile, "OneDrive", "Documents"),
                Path.Combine(profile, "OneDrive", "Pictures"),
            }
            .Concat(OneDriveRoots().SelectMany(root => new[]
            {
                Path.Combine(root, "Documents"),
                Path.Combine(root, "Pictures"),
            }))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .ToArray();

        // The game writes its logs inside its own install directory, one folder per launch.
        // Looking only under LocalLow and Documents found nothing on a real installation, so
        // raid tracking never started at all. Install-relative paths come first because that
        // is where the logs actually are.
        var logRoots = installRoots
            .Select(root => Path.Combine(root, "Logs"))
            .Append(Path.Combine(localLow, "Battlestate Games", "EscapeFromTarkov", "Logs"))
            .Concat(personalRoots.Select(root => Path.Combine(root, "Escape from Tarkov", "Logs")))
            .ToArray();

        // The game keeps its logs inside its own install directory, so its screenshots are
        // looked for there first too. The explicit Screenshots folders come before the bare
        // game folders, so that where nothing has an image in it the more specific guess wins
        // rather than the folder that merely contains it.
        var screenshotRoots = installRoots
            .Select(root => Path.Combine(root, "Screenshots"))
            .Concat(personalRoots.Select(root => Path.Combine(root, "Escape from Tarkov", "Screenshots")))
            .Concat(personalRoots.Select(root => Path.Combine(root, "Escape from Tarkov")))
            .ToArray();
        return new(installRoots, logRoots, screenshotRoots);
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

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> RegistryInstallRoots()
    {
        const string uninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\EscapeFromTarkov";
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(uninstallKey);
            if (key?.GetValue("InstallLocation") is string path && !string.IsNullOrWhiteSpace(path))
            {
                yield return path;
            }
        }
    }
}
