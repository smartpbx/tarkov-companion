using System.Diagnostics;
using System.IO.Compression;

namespace TarkovCompanion.Application.Services.Updates;

/// <summary>
/// Puts a downloaded build in place of the running one.
/// </summary>
/// <remarks>
/// A running application cannot replace its own files, so the swap is done by a short script
/// that waits for this process to exit and then moves directories. The order matters: the
/// running build is moved to a holding name, the new build takes its place, and only once that
/// has succeeded is the previous fallback replaced. A failure at any point leaves either the
/// old build or the new one, never a mixture, and never without a fallback.
///
/// The build is unpacked and checked before this application is asked to close, so the last
/// thing between the player and a working install is two renames rather than a download.
/// </remarks>
public sealed class UpdateInstaller(UpdateOptions options)
{
    /// <summary>How long the swap script keeps trying before giving up and saying so.</summary>
    /// <remarks>
    /// Measured rather than guessed. Ninety seconds was not enough on a real machine: the
    /// application exited the same second the script started, and the folder was still held
    /// for longer than that. The install sits on a OneDrive-synced desktop, and a sync client
    /// rescans a tree after three hundred files change underneath it.
    ///
    /// The cost of waiting too long is a command window that sits there; the cost of giving up
    /// too early is a download that cannot be applied and a player who has to be told why. The
    /// first is cheaper, so this is set well past what was observed.
    /// </remarks>
    private const int WaitSeconds = 420;

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
        var holding = install + "-updating";
        var scriptPath = Path.Combine(options.StagingDirectory, "apply-update.cmd");

        // The order here is the whole safety property, and the first version had it wrong.
        //
        // It deleted the previous build before attempting the risky move, so when that move
        // failed it had already destroyed the only fallback, and then printed that nothing had
        // been changed. On a real machine that message was read by two readers as meaning the
        // net was still there. It was not.
        //
        // So the running build is moved to a holding name first, the new build goes in, and
        // only once that has succeeded is the old fallback replaced. The fallback is never
        // deleted before the replacement has landed, and every message below says what is
        // actually true at the point it is printed.
        var script = $"""
            @echo off
            setlocal enabledelayedexpansion
            set "INSTALL={install}"
            set "STAGED={stagedDirectory}"
            set "PREVIOUS={previous}"
            set "HOLD={holding}"

            echo Waiting for Tarkov Companion to close...
            if exist "%HOLD%" rmdir /S /Q "%HOLD%"

            set /a attempt=0
            :retry
            move "%INSTALL%" "%HOLD%" >nul 2>&1
            if not errorlevel 1 goto moved
            set /a attempt+=1
            if !attempt! GEQ {WaitSeconds} (
                echo.
                echo Tarkov Companion did not release its folder within {WaitSeconds} seconds.
                echo Nothing has been changed. The build you were running is still installed
                echo and the previous one is still beside it.
                echo Close the application and run this file again:
                echo   {scriptPath}
                echo.
                pause
                exit /b 1
            )
            timeout /t 1 /nobreak >nul
            goto retry

            :moved
            move "%STAGED%" "%INSTALL%" >nul 2>&1
            if errorlevel 1 (
                echo.
                echo Could not put the new build in place. Restoring the one you were running.
                move "%HOLD%" "%INSTALL%" >nul 2>&1
                echo The previous build has not been touched.
                pause
                exit /b 1
            )

            rem The new build is in place, so the old fallback may be replaced now and not before.
            if exist "%PREVIOUS%" rmdir /S /Q "%PREVIOUS%"
            move "%HOLD%" "%PREVIOUS%" >nul 2>&1

            start "" "%INSTALL%\TarkovCompanion.exe"
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
