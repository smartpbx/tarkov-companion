using TarkovCompanion.Infrastructure.Persistence;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Turns on <see cref="SqliteInterfaceThreadGuard"/> in a Debug build, or in any build when
/// <c>TARKOV_DB_THREAD_GUARD=1</c>, writing each finding to the crash log.
/// </summary>
/// <remarks>
/// #270. A statement on the interface thread is invisible on an idle database and a frozen window
/// behind a busy one, so it is found by looking for it, not by waiting for the freeze. Off in a
/// Release build unless asked for: it is a developer's finder, and the trace callback it installs
/// runs on every statement.
/// </remarks>
internal static class DatabaseThreadGuard
{
    public const string EnvironmentVariable = "TARKOV_DB_THREAD_GUARD";

    public static void StartIfWanted()
    {
#if DEBUG
        var wanted = true;
#else
        var wanted = Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";
#endif
        if (!wanted)
        {
            return;
        }

        SqliteInterfaceThreadGuard.Enable(
            static () => Avalonia.Threading.Dispatcher.UIThread.CheckAccess(),
            static message => CrashLog.Write("database-on-interface-thread", message));
    }
}
