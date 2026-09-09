namespace TarkovCompanion.App.Services;

public sealed record AppDataPaths(
    string Root,
    string Database,
    string Cache,
    string Logs,
    string Config,
    string DebugCaptures,
    string Support)
{
    public static AppDataPaths Resolve(string? overrideRoot = null, bool demoMode = false)
    {
        var root = overrideRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"))
                ? Path.Combine(AppContext.BaseDirectory, "Data")
                : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TarkovCompanion");
        }

        root = Path.GetFullPath(demoMode ? Path.Combine(root, "Demo") : root);
        return new(
            root,
            Path.Combine(root, "Database"),
            Path.Combine(root, "Cache"),
            Path.Combine(root, "Logs"),
            Path.Combine(root, "Config"),
            Path.Combine(root, "DebugCaptures"),
            Path.Combine(root, "Support"));
    }
}
