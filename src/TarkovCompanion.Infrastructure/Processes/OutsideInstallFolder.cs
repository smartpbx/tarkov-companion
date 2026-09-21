using System.Diagnostics;

namespace TarkovCompanion.Infrastructure.Processes;

/// <summary>
/// Keeps this process, and every process it starts, from standing in the install folder.
/// </summary>
/// <remarks>
/// #599: no build after 2.0.1324 reached the owner. The updater fetched each one and then failed
/// ten times to move <c>current\</c> ("being used by another process"), with the companion fully
/// closed and no file under <c>current\</c> open anywhere. What was held was the directory
/// itself, which is what a process's working directory is: a handle kept for the life of the
/// process. Both shortcuts start the companion with its working directory set to
/// <c>current\</c>, and everything it launches inherits that and keeps it: a browser first
/// opened from a wiki link, a file manager from "Open backup folder", a map rasteriser child. A
/// browser started that way pins <c>current\</c> until the browser closes, long after the
/// companion has gone, and the updater cannot see why.
///
/// So the companion leaves the install folder first thing (<see cref="LeaveInstallFolder"/>),
/// and every launch is built here with a working directory that is somewhere else. Which
/// program was holding it on the owner's machine was never identified and no longer matters.
/// </remarks>
public static class OutsideInstallFolder
{
    private static readonly Lazy<string> Default = new(() => Choose(
        AppContext.BaseDirectory,
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetTempPath(),
            Environment.SystemDirectory,
        ],
        Directory.Exists));

    /// <summary>A directory that exists and is not the install folder or anything inside it.</summary>
    public static string WorkingDirectory => Default.Value;

    /// <summary>The first candidate that exists and is not under <paramref name="installFolder"/>.</summary>
    /// <remarks>
    /// The filesystem root is the last resort, not an exception: a launch with a poor working
    /// directory still works, and one that throws here is a link that does nothing.
    /// </remarks>
    public static string Choose(string installFolder, IEnumerable<string?> candidates, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(exists);
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate)
                && !IsUnder(candidate, installFolder)
                && exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return Path.GetPathRoot(Path.GetFullPath(installFolder)) ?? Path.DirectorySeparatorChar.ToString();
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    /// <remarks>
    /// By whole path segments. The companion's data lives in <c>...\TarkovCompanion</c> beside an
    /// install in <c>...\TarkovCompanionDesktop</c>, and a plain prefix test calls the second a
    /// child of the first.
    /// </remarks>
    public static bool IsUnder(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder))
        {
            return false;
        }

        var inner = Trim(Path.GetFullPath(path));
        var outer = Trim(Path.GetFullPath(folder));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return inner.Equals(outer, comparison)
            || inner.StartsWith(outer + Path.DirectorySeparatorChar, comparison);

        static string Trim(string value) =>
            value.Length > 1 ? value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : value;
    }

    /// <summary>Opens an address, a file or a folder with whatever the system uses for it.</summary>
    public static ProcessStartInfo ShellOpen(string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        return new ProcessStartInfo(target)
        {
            UseShellExecute = true,
            WorkingDirectory = WorkingDirectory,
        };
    }

    /// <summary>Runs a program directly, with no shell.</summary>
    public static ProcessStartInfo Program(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            WorkingDirectory = WorkingDirectory,
        };
    }

    /// <summary>
    /// Moves the working directory out of the install folder, if that is where it is.
    /// </summary>
    /// <remarks>
    /// Only then. A tool run from a work folder with relative paths on its command line (the
    /// self-test's <c>--output</c>, the OCR probe) is not standing in the install folder and is
    /// left where it is.
    /// </remarks>
    /// <returns>Where it moved to, or null when it did not need to.</returns>
    public static string? LeaveInstallFolder(
        string installFolder,
        Func<string> currentDirectory,
        Action<string> setCurrentDirectory,
        string? moveTo = null)
    {
        ArgumentNullException.ThrowIfNull(currentDirectory);
        ArgumentNullException.ThrowIfNull(setCurrentDirectory);
        try
        {
            if (!IsUnder(currentDirectory(), installFolder))
            {
                return null;
            }

            var destination = moveTo ?? WorkingDirectory;
            setCurrentDirectory(destination);
            return destination;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or System.Security.SecurityException)
        {
            // Nothing to stand on, which is not a reason to refuse to start.
            return null;
        }
    }

    /// <summary>The same, for this process.</summary>
    public static string? LeaveInstallFolder() => LeaveInstallFolder(
        AppContext.BaseDirectory,
        Directory.GetCurrentDirectory,
        Directory.SetCurrentDirectory);
}
