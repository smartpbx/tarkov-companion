namespace TarkovCompanion.App.Services;

public sealed record AppDataPaths(
    string Root,
    string Database,
    string Cache,
    string Logs,
    string Config,
    string Support)
{
    public static AppDataPaths Resolve(string? overrideRoot = null, bool demoMode = false)
    {
        var root = overrideRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
                ? Path.Combine(AppContext.BaseDirectory, "Data")
                : Path.Combine(DefaultLocalApplicationData(), "TarkovCompanion");
        }

        root = Path.GetFullPath(demoMode ? Path.Combine(root, "Demo") : root);
        return new(
            root,
            Path.Combine(root, "Database"),
            Path.Combine(root, "Cache"),
            Path.Combine(root, "Logs"),
            Path.Combine(root, "Config"),
            Path.Combine(root, "Support"));
    }

    /// <summary>The per-user data root, never an empty path that falls back to the current directory.</summary>
    /// <remarks>
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> can be empty in a headless
    /// Linux process. <see cref="Path.Combine(string, string)"/> then quietly produced the
    /// relative path <c>TarkovCompanion</c>, so the documented Linux self-test created its live
    /// database inside whichever repository or package directory launched it. Honour XDG first,
    /// then its conventional fallback, and fail clearly if the process has no usable home at all.
    /// Windows keeps using the established LocalAppData location existing installs rely on.
    /// </remarks>
    private static string DefaultLocalApplicationData()
    {
        return ChooseLocalApplicationData(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable("XDG_DATA_HOME"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            OperatingSystem.IsWindows());
    }

    internal static string ChooseLocalApplicationData(
        string? local,
        string? xdg,
        string? profile,
        bool isWindows)
    {
        if (!string.IsNullOrWhiteSpace(local) && Path.IsPathFullyQualified(local))
        {
            return local;
        }

        // The XDG specification requires an absolute value. Treating a malformed relative one
        // as valid would recreate the repository-writing bug this fallback exists to prevent.
        if (!isWindows && !string.IsNullOrWhiteSpace(xdg) && Path.IsPathFullyQualified(xdg))
        {
            return xdg;
        }

        if (string.IsNullOrWhiteSpace(profile) || !Path.IsPathFullyQualified(profile))
        {
            throw new InvalidOperationException("No absolute per-user application-data directory is available.");
        }

        return isWindows
            ? Path.Combine(profile, "AppData", "Local")
            : Path.Combine(profile, ".local", "share");
    }
}
