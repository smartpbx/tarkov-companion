using System.Globalization;

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
    private static readonly Lock Gate = new();
    private static string? _directory;

    public static string? FilePath => _directory is null ? null : Path.Combine(_directory, "startup.log");

    public static void Install(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _directory = logDirectory;

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
}
