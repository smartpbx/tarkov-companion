using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
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
}

public sealed class WindowsEftPathLocator(IEftPathProbe? probe = null) : IEftPathLocator
{
    private readonly IEftPathProbe _probe = probe ?? new SystemEftPathProbe();

    public Task<EftPaths> FindAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = _probe.GetCandidates();
        var install = FirstExisting(candidates.InstallRoots);
        var logs = FirstExisting(candidates.LogRoots);
        var screenshots = BestScreenshotRoot(candidates.ScreenshotRoots);
        var foundCount = new[] { install, logs, screenshots }.Count(path => path is not null);
        var confidence = foundCount switch
        {
            3 => new Confidence(0.95),
            2 => new Confidence(0.80),
            1 => new Confidence(0.60),
            _ => Confidence.Unknown,
        };
        return Task.FromResult(new EftPaths(install, logs, screenshots, confidence));
    }

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

        // The game writes its logs inside its own install directory, one folder per launch.
        // Looking only under LocalLow and Documents found nothing on a real installation, so
        // raid tracking never started at all. Install-relative paths come first because that
        // is where the logs actually are.
        var logRoots = installRoots
            .Select(root => Path.Combine(root, "Logs"))
            .Concat(new[]
            {
                Path.Combine(localLow, "Battlestate Games", "EscapeFromTarkov", "Logs"),
                Path.Combine(documents, "Escape from Tarkov", "Logs"),
            })
            .ToArray();
        // The game keeps its logs inside its own install directory, so its screenshots are
        // looked for there first too. Only Documents and Pictures were offered before, which
        // on an install like that finds nothing and leaves position permanently unavailable.
        var screenshotRoots = installRoots
            .Select(root => Path.Combine(root, "Screenshots"))
            .Concat(new[]
            {
                Path.Combine(documents, "Escape from Tarkov", "Screenshots"),
                Path.Combine(pictures, "Escape from Tarkov"),
                Path.Combine(pictures, "Escape from Tarkov", "Screenshots"),
            })
            .ToArray();
        return new(installRoots, logRoots, screenshotRoots);
    }

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
