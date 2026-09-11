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
        var screenshots = FirstExisting(candidates.ScreenshotRoots);
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
        paths.Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(_probe.DirectoryExists);
}

public sealed class SystemEftPathProbe : IEftPathProbe
{
    public EftPathCandidates GetCandidates()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new([], [], []);
        }

        return GetWindowsCandidates();
    }

    public bool DirectoryExists(string path) => Directory.Exists(path);

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
        var installRoots = RegistryInstallRoots()
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
        var screenshotRoots = new[]
        {
            Path.Combine(documents, "Escape from Tarkov", "Screenshots"),
            Path.Combine(pictures, "Escape from Tarkov"),
        };
        return new(installRoots, logRoots, screenshotRoots);
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
