using System.Globalization;
using TarkovCompanion.App.Services.Updates;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Ends the process a fixed time after the first real request to exit, wherever it is stuck.
/// </summary>
/// <remarks>
/// #735. The teardown budget and the exit watchdog from #512/#659 were both armed only once the
/// desktop lifetime had returned, so they bounded the part of an exit that comes after the
/// interface has let go and nothing before it. A close that was accepted but never reached the
/// end of the lifetime (a <c>Closed</c> handler, the lifetime's own exit event, a second window
/// holding <c>OnLastWindowClose</c> open) left a process no bound applied to, and the Windows
/// page gallery killed it twenty seconds later with nothing in the log to say where it stood.
/// The watchdog also ended the process with <see cref="Environment.Exit(int)"/>, which runs exit
/// handlers and native detach code and can itself hang.
///
/// So the clock now starts at the request: the main window closing for real, the tray's Quit, the
/// session ending, the lifetime's exit, or the update hand-over. Whichever comes first arms it,
/// the rest only record how far the exit got, and the line written when it fires names that
/// stage. It ends the process with <c>TerminateProcess</c>, which nothing in the process can hold
/// up. On the ordinary path it is a background thread that never wakes.
/// </remarks>
internal sealed class ExitDeadline(TimeSpan after, Action end, Action<string> log)
{
    /// <summary>
    /// Fifteen seconds from the request. Teardown's own budget is eight (nine with its slack) and
    /// the watchdog behind it four more, so an exit that uses everything it is allowed still ends
    /// on its own first; the gallery allows twenty from its close to the process being gone.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    /// <summary>The one this process uses.</summary>
    public static ExitDeadline Current { get; } = new(
        Budget,
        () => new HardProcessEnder(_ => { }).EndNow(),
        line => CrashLog.Write("lifecycle", line));

    private int _armed;
    private string _reason = string.Empty;
    private volatile string _stage = "requested";

    public bool IsArmed => Volatile.Read(ref _armed) == 1;

    /// <summary>Starts the clock. Only the first request does; later ones are stages of it.</summary>
    /// <returns>Whether this call armed it.</returns>
    public bool Arm(string reason)
    {
        if (Interlocked.Exchange(ref _armed, 1) == 1)
        {
            Reached(reason);
            return false;
        }

        _reason = reason;
        _stage = reason;
        log(string.Create(
            CultureInfo.InvariantCulture,
            $"Exit requested ({reason}); the process ends within {after.TotalSeconds:0} s."));
        new Thread(Watch)
        {
            IsBackground = true,
            Name = "tarkov-companion-exit-deadline",
        }.Start();
        return true;
    }

    /// <summary>Records how far the exit got, for the line written if it never finishes.</summary>
    public void Reached(string stage) => _stage = stage;

    private void Watch()
    {
        Thread.Sleep(after);
        // The line is worth having and not worth waiting for: a writer stuck on the log's lock or
        // on a full disk must not be what keeps the process alive.
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"Exit requested ({_reason}) {after.TotalSeconds:0} s ago and the process is still running; last stage: {_stage}. Ending it.");
        try
        {
            Task.Run(() => log(line)).Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
        }

        end();
    }
}
