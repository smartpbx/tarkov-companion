using System.Diagnostics;
using System.IO.Compression;

namespace TarkovCompanion.Application.Services.Updates;

/// <summary>
/// Puts a downloaded build in place of the running one.
/// </summary>
/// <remarks>
/// A running application cannot replace its own files, so the swap is done by a short script
/// that waits for this process to exit and then moves directories. The order matters: the
/// A rollback copy is made first, so it exists before anything changes, then the new build is
/// mirrored over the install. The result is checked for an application and a build stamp, and
/// the previous build is mirrored back if either is missing. A failure therefore leaves a
/// runnable build and always a fallback.
///
/// The build is unpacked and checked before this application is asked to close, so the last
/// thing between the player and a working install is two renames rather than a download.
/// </remarks>
public sealed class UpdateInstaller(UpdateOptions options)
{
    /// <summary>How many times a single busy file is retried before the update gives up.</summary>
    /// <remarks>
    /// Per file rather than for the whole operation, which is the difference between this and
    /// the approach it replaces. A sync client holding one file for a moment costs a retry;
    /// holding the directory cost the entire update.
    /// </remarks>
    private const int FileRetries = 8;

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
    /// Returns the script's path so a caller can name it if the swap has to be run by hand.
    /// The caller is expected to close the application immediately afterwards; the script
    /// waits for the folder to be released rather than for any particular moment, so starting
    /// it slightly early is harmless.
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

        // The install is replaced file by file rather than by renaming folders.
        //
        // Renaming was the obvious approach and it does not work here. A directory rename
        // fails while anything holds a handle on the directory or any file beneath it, and the
        // install sits on a OneDrive-synced desktop where the sync client holds handles more
        // or less continuously. Waiting longer does not help: on a real machine it never
        // succeeded, and a longer timeout only made the failure slower.
        //
        // Mirroring copes, because it works on one file at a time and retries the few that are
        // momentarily busy. It costs the atomicity a rename gave, so the new build is checked
        // afterwards and the previous one is mirrored back if the check fails. The rollback is
        // made by copying before anything is touched, so it exists throughout.
        var script = $"""
            @echo off
            setlocal
            set "INSTALL={install}"
            set "STAGED={stagedDirectory}"
            set "PREVIOUS={previous}"

            echo Updating Tarkov Companion...

            rem Wait for the application to go before touching its folder. Mirroring over a
            rem running executable fails on the one file that matters, and the new copy would
            rem then start while the old one still held the single-instance lock and exit
            rem again, leaving nothing running at all. Sixty seconds is far longer than
            rem teardown takes; past that, proceed and let robocopy's retries do their work.
            echo Waiting for it to close...
            set /a TRIES=0
            :waitloop
            tasklist /FI "IMAGENAME eq TarkovCompanion.exe" /NH 2>nul | find /I "TarkovCompanion.exe" >nul
            if errorlevel 1 goto closed
            set /a TRIES+=1
            if %TRIES% GEQ 60 goto closed
            ping -n 2 127.0.0.1 >nul
            goto waitloop
            :closed

            rem The rollback is made first, by copying, so it exists before anything changes.
            robocopy "%INSTALL%" "%PREVIOUS%" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul
            if errorlevel 8 (
                echo.
                echo Could not set a rollback copy aside. Nothing has been changed.
                pause
                exit /b 1
            )

            robocopy "%STAGED%" "%INSTALL%" /MIR /R:{FileRetries} /W:2 /NFL /NDL /NJH /NJS /NP >nul
            if errorlevel 8 goto restore
            if not exist "%INSTALL%\TarkovCompanion.exe" goto restore
            if not exist "%INSTALL%\BUILD_INFO.txt" goto restore

            rmdir /S /Q "%STAGED%" 2>nul
            start "" "%INSTALL%\TarkovCompanion.exe"
            endlocal
            exit /b 0

            :restore
            echo.
            echo The new build could not be put in place. Restoring the one you were running.
            robocopy "%PREVIOUS%" "%INSTALL%" /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP >nul
            if errorlevel 8 (
                echo The restore also failed. Your previous build is intact at:
                echo   %PREVIOUS%
            ) else (
                echo Restored. You are back on the build you were running.
            )
            pause
            exit /b 1
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
