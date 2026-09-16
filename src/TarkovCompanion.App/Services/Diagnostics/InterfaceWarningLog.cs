using System.Diagnostics;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Writes the interface toolkit's own warnings to a file, when a tool asks for them.
/// </summary>
/// <remarks>
/// Bindings are compiled, so a property that is renamed and not followed through is not a
/// compile error on the page that binds to it: the page loads, the control draws, and the
/// value is simply never there. The toolkit says so, at warning level, in the binding area,
/// and until now nobody was listening.
///
/// That is the failure the page gallery was built to catch and could not. It launched every
/// page, photographed each one, and passed as long as a window appeared. A page bound to a
/// property that no longer exists presents a window perfectly well.
///
/// Off unless the environment names a file, so a player's run writes nothing and pays nothing.
/// The toolkit already routes its own log to <see cref="Trace"/>; this only gives that
/// somewhere to land.
/// </remarks>
public static class InterfaceWarningLog
{
    /// <summary>The environment variable a tool sets to the file it wants the warnings in.</summary>
    public const string PathVariable = "TARKOV_COMPANION_UI_WARNING_LOG";

    /// <summary>Starts recording, and reports whether it did.</summary>
    public static bool TryStart()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is not { Length: > 0 } path)
        {
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Flushed on every write. The page gallery closes the window and reads the file
            // straight afterwards, and a buffered listener would hand it an empty one.
            Trace.Listeners.Add(new TextWriterTraceListener(path));
            Trace.AutoFlush = true;
            // A clean page may legitimately produce no toolkit warnings. Emit an explicit
            // non-fault marker so the gallery can distinguish that success from a listener
            // that was requested but never attached. Relying on an incidental framework log
            // left verification-only windows waiting until their readiness deadline.
            Trace.WriteLine("[Diagnostic] Interface warning capture armed.");
            Trace.Flush();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A diagnostic that cannot be written is not a reason to refuse to start.
            return false;
        }
    }
}
