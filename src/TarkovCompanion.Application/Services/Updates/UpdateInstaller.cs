using System.Diagnostics;
using System.IO.Compression;

namespace TarkovCompanion.Application.Services.Updates;

/// <summary>
/// Puts a downloaded build in place of the running one.
/// </summary>
/// <remarks>
/// A running application cannot replace its own files, so the swap is done by a short script
/// that waits for this process to exit and then moves directories. The order matters: the
/// current install is renamed aside first, and only then does the new one take its place, so a
/// failure at any point leaves either the old build or the new one and never a half-written
/// mixture of the two. If the second move fails the script puts the old one back.
///
/// The build is unpacked and checked before this application is asked to close, so the last
/// thing between the player and a working install is two renames rather than a download.
/// </remarks>
public sealed class UpdateInstaller(UpdateOptions options)
{
    /// <summary>
    /// Unpacks a verified download and hands back where it went.
    /// </summary>
    /// <remarks>
    /// The archive holds a single top-level folder, so the real payload is that folder rather
    /// than the extraction root. Unpacking happens while the application is still running,
    /// which is the slow part, so applying afterwards is quick.
    /// </remarks>
    public string Stage(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var staged = Path.Combine(options.StagingDirectory, "pending");
        if (Directory.Exists(staged))
        {
            Directory.Delete(staged, recursive: true);
        }

        Directory.CreateDirectory(staged);
        ZipFile.ExtractToDirectory(archivePath, staged);

        // An archive that unpacks into one folder is the shape the packaging script produces;
        // anything else is used as-is rather than guessed at.
        var entries = Directory.GetFileSystemEntries(staged);
        return entries.Length == 1 && Directory.Exists(entries[0]) ? entries[0] : staged;
    }

    /// <summary>
    /// Verifies a staged build is usable before anything is moved.
    /// </summary>
    /// <remarks>
    /// Swapping in a folder with no executable would leave nothing to start, and the player
    /// would find that out by double-clicking a shortcut that does nothing.
    /// </remarks>
    public static bool LooksRunnable(string stagedDirectory) =>
        File.Exists(Path.Combine(stagedDirectory, "TarkovCompanion.exe"))
        && File.Exists(Path.Combine(stagedDirectory, "BUILD_INFO.txt"));

    /// <summary>
    /// Writes the swap script and starts it, then leaves it to run after this process exits.
    /// </summary>
    /// <remarks>
    /// Returns the script's path so a caller can say where it went. It waits on this process
    /// id rather than on a fixed delay, so a slow shutdown does not race it.
    /// </remarks>
    public string BeginApply(string stagedDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDirectory);
        if (!LooksRunnable(stagedDirectory))
        {
            throw new InvalidOperationException("The staged build has no application in it; nothing was changed.");
        }

        var install = options.InstallDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var previous = install + "-previous";
        var scriptPath = Path.Combine(options.StagingDirectory, "apply-update.cmd");
        var script = $"""
            @echo off
            setlocal
            echo Waiting for Tarkov Companion to close...
            :wait
            tasklist /FI "PID eq {Environment.ProcessId}" 2>nul | find "{Environment.ProcessId}" >nul
            if not errorlevel 1 (
                timeout /t 1 /nobreak >nul
                goto wait
            )

            if exist "{previous}" rmdir /S /Q "{previous}"
            move "{install}" "{previous}" >nul
            if errorlevel 1 (
                echo Could not set the current build aside. Nothing was changed.
                pause
                exit /b 1
            )

            move "{stagedDirectory}" "{install}" >nul
            if errorlevel 1 (
                echo Could not put the new build in place. Restoring the previous one.
                move "{previous}" "{install}" >nul
                pause
                exit /b 1
            )

            start "" "{Path.Combine(install, "TarkovCompanion.exe")}"
            endlocal
            """;
        File.WriteAllText(scriptPath, script);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{scriptPath}\"")
        {
            UseShellExecute = true,
            CreateNoWindow = false,
            WindowStyle = ProcessWindowStyle.Minimized,
        });
        return scriptPath;
    }
}
