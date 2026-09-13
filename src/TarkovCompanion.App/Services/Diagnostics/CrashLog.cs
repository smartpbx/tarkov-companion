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
    private static string _lastEntry = string.Empty;
    private static int _repeats;
    private static DateTimeOffset _repeatsSince;

    public static string? FilePath => _directory is null ? null : Path.Combine(_directory, "startup.log");

    public static void Install(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _directory = logDirectory;

        // Which build wrote this, first thing. A log that cannot name its own build is a log
        // somebody has to guess about, and every assembly reported 1.0.0.0 until the packaging
        // script started stamping one.
        var assembly = typeof(CrashLog).Assembly;
        var build = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown";
        Write("started", string.Create(
            CultureInfo.InvariantCulture,
            $"build {build} · {RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture}"));

        AppDomain.CurrentDomain.UnhandledException += (_, arguments) =>
            Write("unhandled-exception", arguments.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, arguments) =>
        {
            Write("unobserved-task-exception", arguments.Exception);
            arguments.SetObserved();
        };
    }

    public static void Write(string category, Exception? exception) =>
        Write(category, exception?.ToString() ?? "No exception detail was available.");

    public static void Write(string category, string detail)
    {
        var path = FilePath;
        if (path is null)
        {
            return;
        }

        var entry = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.UtcNow:O} [{category}] {detail}{Environment.NewLine}");

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(_directory!);

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
                    File.AppendAllText(path, string.Create(
                        CultureInfo.InvariantCulture,
                        $"{DateTimeOffset.UtcNow:O} [repeat] still failing ({_repeats + 1}x) since {_repeatsSince:HH:mm}{Environment.NewLine}"));
                    _repeats = 0;
                }

                _lastEntry = line;
                _repeatsSince = DateTimeOffset.UtcNow;
                Roll(path);
                File.AppendAllText(path, entry);
            }
        }
        catch (Exception failure) when (failure is IOException
                                        or UnauthorizedAccessException
                                        or NotSupportedException)
        {
            // Diagnostics must never become the reason the application fails.
        }
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
