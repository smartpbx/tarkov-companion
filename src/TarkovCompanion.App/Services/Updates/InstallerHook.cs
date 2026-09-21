namespace TarkovCompanion.App.Services.Updates;

/// <summary>
/// Whether the installer started this process to run a hook, rather than a player to use it.
/// </summary>
/// <remarks>
/// <c>Update.exe</c> runs the application with <c>--veloapp-install</c>, <c>-updated</c>,
/// <c>-obsolete</c> or <c>-uninstall</c> and a version, and waits for it to exit before it touches
/// a file. <c>VelopackApp.Build().Run()</c> answers those and exits; nothing of the application
/// may run first, because every millisecond of it is a millisecond the updater spends waiting
/// with the old build's folder still in use (#599).
/// </remarks>
public static class InstallerHook
{
    private const string Prefix = "--veloapp-";

    public static bool IsHookInvocation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var argument in args)
        {
            if (argument is not null && argument.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
