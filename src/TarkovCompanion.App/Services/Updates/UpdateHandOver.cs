using System.Diagnostics;
using System.Globalization;

namespace TarkovCompanion.App.Services.Updates;

/// <summary>How this process ends once the updater has been started.</summary>
public interface IProcessEnder
{
    /// <summary>Starts a timer that ends the process outright if it is somehow still here.</summary>
    void ArmKill(TimeSpan after);

    /// <summary>Ends the process now. Does not return when it works.</summary>
    void EndNow();
}

/// <summary>What the hand-over does to the rest of the application, in the order it does it.</summary>
/// <param name="StopAcceptingWork">Cancels held calls and ends child processes. Must not block.</param>
/// <param name="CloseInterface">Hides the windows and removes the tray icon. Must not block.</param>
/// <param name="FlushAsync">Releases what holds files and sockets, inside the budget it is given.</param>
public sealed record UpdateHandOverSteps(
    Action StopAcceptingWork,
    Action CloseInterface,
    Func<TimeSpan, Task> FlushAsync);

/// <summary>
/// Leaves the process, fast, so the updater can replace the files it was running from.
/// </summary>
/// <remarks>
/// What the old path did: <c>UpdateManager.ApplyUpdatesAndRestart</c> starts <c>Update.exe</c> and
/// calls <see cref="Environment.Exit(int)"/> on the UI thread, in the middle of whatever the
/// application was doing. None of this application's own teardown runs on that path (the
/// <c>finally</c> in <c>Main</c> is never reached). In the owner's log "Applying version X" is
/// the last line every updating process ever wrote, and the next run always opened with "the
/// previous run did not reach its own shutdown", for updates that worked as much as for the ones
/// that did not. A map rasteriser child was left running with the executable open for up to
/// ninety seconds, and nothing was closed.
///
/// So the order is turned round. The ordinary staged teardown runs first (about 180 ms on the
/// owner's machine), under a hard two-second budget that is abandoned rather than extended; the
/// clean-exit marker is written; the updater is started last; and then the process is ended by
/// <c>TerminateProcess</c>, which runs no exit handlers and no native library detach code and so
/// cannot be held up by either, with <see cref="Environment.Exit(int)"/> as the fallback and a
/// timer behind both. <c>Update.exe</c> cannot wait for this process on the owner's machine
/// ("Access is denied", 37 attempts of 38), so its clock starts the moment it is launched.
///
/// This is not what stopped #599's updates applying; a working directory left in the install
/// folder was (<c>OutsideInstallFolder</c>). It is the second fault the same log showed.
/// </remarks>
public sealed class UpdateHandOver(UpdateHandOverSteps steps, IProcessEnder ender, Action<string> log)
{
    /// <summary>All the time teardown gets. What is not released by then is left to the kill.</summary>
    public static readonly TimeSpan FlushBudget = TimeSpan.FromSeconds(2);

    /// <summary>How long after the updater starts before the last-resort kill fires.</summary>
    public static readonly TimeSpan KillAfter = TimeSpan.FromSeconds(1.5);

    /// <summary>Stops, flushes, starts the updater, and ends the process.</summary>
    /// <param name="version">The build being installed, for the log.</param>
    /// <param name="startUpdater">Starts <c>Update.exe</c> and returns at once.</param>
    public void Run(string version, Action startUpdater)
    {
        ArgumentNullException.ThrowIfNull(startUpdater);
        var elapsed = Stopwatch.StartNew();
        log($"Update hand-over to {version}: started.");

        Guarded("stop", steps.StopAcceptingWork);
        Guarded("close", steps.CloseInterface);
        var stopped = elapsed.ElapsedMilliseconds;

        var flushed = Flush();
        var flushCost = elapsed.ElapsedMilliseconds - stopped;

        try
        {
            startUpdater();
        }
        catch (Exception exception)
        {
            // The windows are gone and the services are stopped, so there is no application left
            // to go back to. The package is still on disk: the next launch applies it before
            // anything else starts, and says so on Setup if that fails too.
            log($"Update hand-over to {version}: the updater could not be started ({exception.Message}). Ending anyway.");
            ender.ArmKill(KillAfter);
            ender.EndNow();
            return;
        }

        log(string.Create(
            CultureInfo.InvariantCulture,
            $"Update hand-over to {version}: stop+close {stopped}ms · flush {flushCost}ms{(flushed ? string.Empty : " (abandoned)")} · updater started at {elapsed.ElapsedMilliseconds}ms · ending the process."));
        ender.ArmKill(KillAfter);
        ender.EndNow();
    }

    private bool Flush()
    {
        try
        {
            // On its own thread, and waited for with a deadline rather than awaited: this runs on
            // the UI thread, and a teardown step that posts to the dispatcher would otherwise wait
            // for the thread that is waiting for it.
            return Task.Run(() => steps.FlushAsync(FlushBudget)).Wait(FlushBudget);
        }
        catch (AggregateException exception)
        {
            log($"Update hand-over: flush failed ({exception.InnerException?.Message ?? exception.Message}).");
            return true;
        }
    }

    private void Guarded(string name, Action step)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            log($"Update hand-over: {name} failed ({exception.Message}).");
        }
    }
}

/// <summary>Ends this process with <c>TerminateProcess</c>, and failing that the ordinary way.</summary>
/// <remarks>
/// <see cref="Process.Kill()"/> first, because it is the one exit nothing in the process can
/// delay: <see cref="Environment.Exit(int)"/> runs process-exit handlers, then <c>ExitProcess</c>
/// ends every other thread (including any timer meant to rescue it) and runs each native
/// library's detach code under the loader lock. The exit code is -1 and nobody reads it.
/// </remarks>
public sealed class HardProcessEnder(Action<string> log) : IProcessEnder
{
    public void ArmKill(TimeSpan after)
    {
        var timer = new Thread(() =>
        {
            Thread.Sleep(after);
            try
            {
                log($"Still running {after.TotalSeconds:0.#} seconds after the update hand-over; killing the process.");
            }
            catch (Exception)
            {
                // The kill matters; the line about it does not.
            }

            Kill();
        })
        {
            IsBackground = true,
            Name = "tarkov-companion-update-kill",
        };
        timer.Start();
    }

    public void EndNow()
    {
        Kill();
        Environment.Exit(0);
    }

    private static void Kill()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            self.Kill();
        }
        catch (Exception)
        {
            // Refused by the machine. Environment.Exit is next.
        }
    }
}
