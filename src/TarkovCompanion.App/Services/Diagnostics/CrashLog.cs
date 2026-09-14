using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Appends startup and crash detail to a file the user can find and send on.
/// </summary>
/// <remarks>
/// The application is a <c>WinExe</c>, so anything written to the console is discarded and a
/// failure before the window appears is completely invisible: no window, no dialog, no file.
/// The only other sink is <see cref="System.Diagnostics.Trace"/>, which needs a debugger
/// attached. This writes somewhere an ordinary user can reach.
/// </remarks>
public static class CrashLog
{
    /// <summary>How large the log may grow before the previous one is rolled aside.</summary>
    /// <remarks>
    /// It was File.AppendAllText with no cap at all, on a file that gains a full stack trace
    /// every five seconds for as long as the relay is unreachable. Two megabytes is far more
    /// than anybody reads and far less than anybody notices.
    /// </remarks>
    private const long MaximumBytes = 2 * 1024 * 1024;

    private static readonly Lock Gate = new();
    private static string? _directory;
    private static bool _subscribed;
    private static string _lastEntry = string.Empty;
    private static int _repeats;
    private static DateTimeOffset _repeatsSince;

    public static string? FilePath => _directory is null ? null : Path.Combine(_directory, "startup.log");

    public static void Install(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        lock (Gate)
        {
            _directory = logDirectory;

            // A new destination is a new log, so what was written last belongs to a file this
            // one is no longer appending to. Left standing, the collapse below compares the
            // first line of the new log against the last line of the old one and, when they
            // match — which for the started line they always do — writes nothing at all.
            _lastEntry = string.Empty;
            _repeats = 0;
        }

        // Which build wrote this, first thing. A log that cannot name its own build is a log
        // somebody has to guess about, and every assembly reported 1.0.0.0 until the packaging
        // script started stamping one.
        var assembly = typeof(CrashLog).Assembly;
        var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        Write("started", string.Create(
            CultureInfo.InvariantCulture,
            $"build {build} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}"));

        // Once, however many times Install is called. The application calls it at startup and
        // nowhere else, but a second subscription would write every unhandled exception twice,
        // and the count only ever goes up.
        lock (Gate)
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, arguments) =>
            Write("unhandled-exception", arguments.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, arguments) =>
        {
            Write("unobserved-task-exception", arguments.Exception);
            arguments.SetObserved();
        };
    }

    /// <summary>
    /// Stops writing, until something installs a destination again.
    /// </summary>
    /// <remarks>
    /// The destination is a static, which is right for a crash log — anything that fails has to
    /// be able to say so without being handed a logger first — and it means that while one is
    /// installed, every part of the process writes to it.
    ///
    /// That is what makes the log's own tests unable to clean up after themselves: another
    /// thread logging anything holds a handle on a directory the test is about to delete, and
    /// on Windows a directory with an open handle in it cannot be removed. Detaching first
    /// closes the window rather than widening the catch around it.
    ///
    /// The repeat state goes with it, for the same reason <see cref="Install"/> clears it: what
    /// was written last belongs to a file this class is no longer appending to.
    /// </remarks>
    public static void Detach()
    {
        lock (Gate)
        {
            _directory = null;
            _lastEntry = string.Empty;
            _repeats = 0;
        }
    }

    public static void Write(string category, Exception? exception) =>
        Write(category, exception?.ToString() ?? "No exception detail was available.");

    public static void Write(string category, string detail)
    {
        var entry = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} [{category}] {detail}{Environment.NewLine}");

        try
        {
            lock (Gate)
            {
                // Read inside the lock and used from the local, not re-read from the field.
                // Checking FilePath outside and then using _directory! inside was a window a
                // Detach on another thread fitted through: the check passed, the field went
                // null, and Directory.CreateDirectory(null) threw ArgumentNullException —
                // which this catch does not cover and which surfaced inside whatever was
                // trying to log. A diagnostic that takes down its caller is the one thing this
                // class must never do.
                if (_directory is not { } directory)
                {
                    return;
                }

                var path = Path.Combine(directory, "startup.log");
                Directory.CreateDirectory(directory);

                // The same line over and over is one fact, not two hundred. While the relay is
                // unreachable this file gained a full stack trace every five seconds, which
                // buried everything else that had happened that evening.
                var line = $"[{category}] {detail}";
                if (line == _lastEntry)
                {
                    _repeats++;
                    return;
                }

                if (_repeats > 0)
                {
                    Append(path, string.Create(
                        CultureInfo.InvariantCulture,
                        $"{DateTimeOffset.UtcNow:O} [repeat] still failing ({_repeats + 1}x) since {_repeatsSince:HH:mm}{Environment.NewLine}"));
                    _repeats = 0;
                }

                _lastEntry = line;
                _repeatsSince = DateTimeOffset.UtcNow;
                Roll(path);
                Append(path, entry);
            }
        }
        catch (Exception failure) when (failure is IOException
                                        or UnauthorizedAccessException
                                        or NotSupportedException
                                        or ArgumentException)
        {
            // Diagnostics must never become the reason the application fails. ArgumentException
            // is here because it is the shape a bad path arrives as, and the alternative to
            // catching it is a logging call throwing into a caller that was only trying to say
            // something had gone wrong. The window that produced one is closed above; this is
            // the belt to that brace.
        }
    }

    /// <summary>
    /// Appends a line, sharing the file both ways while it does.
    /// </summary>
    /// <remarks>
    /// <see cref="File.AppendAllText(string, string?)"/> holds the file as
    /// <see cref="FileShare.Read"/>, and Windows negotiates sharing from both ends: for the
    /// moment the append is open, every other handle asking for write access is refused, even
    /// one that was itself willing to share. A diagnostic log is not worth refusing anybody
    /// over. Ordering is not what the share mode protects anyway — every write in this class is
    /// already serialised behind <see cref="Gate"/>, and the writes that are not are not ours
    /// to order.
    /// </remarks>
    private static void Append(string path, string text)
    {
        using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        using var writer = new StreamWriter(stream);
        writer.Write(text);
    }

    /// <summary>
    /// Keeps one previous log beside the current one, and no more.
    /// </summary>
    /// <remarks>
    /// One, because the thing somebody needs is almost always "this run, or the one before it
    /// where the problem actually happened". Keeping ten would be keeping nine nobody opens.
    /// </remarks>
    private static void Roll(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length < MaximumBytes)
        {
            return;
        }

        File.Move(path, path + ".1", overwrite: true);
    }
}
