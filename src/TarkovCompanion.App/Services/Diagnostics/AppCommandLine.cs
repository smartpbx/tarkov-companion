namespace TarkovCompanion.App.Services.Diagnostics;

/// <param name="StartPage">
/// A destination to open instead of the default one, by the name shown in the sidebar.
/// </param>
public sealed record AppCommandLine(
    bool SelfTest,
    bool Demo,
    bool Headless,
    bool DeveloperMode,
    string? OutputPath,
    string? DemoFixturePath,
    string? DiagnosticChannelPath,
    string? StartPage)
{
    public static AppCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new(
            HasFlag(args, "--self-test"),
            HasFlag(args, "--demo"),
            HasFlag(args, "--headless"),
            HasFlag(args, "--developer-mode"),
            GetValue(args, "--output"),
            GetValue(args, "--demo-fixture"),
            GetValue(args, "--diagnostic-channel"),
            GetValue(args, "--page"));
    }

    private static bool HasFlag(IReadOnlyList<string> args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static string? GetValue(IReadOnlyList<string> args, string option)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index == args.Count - 1 || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{option} requires a value.", nameof(args));
            }

            return args[index + 1];
        }

        return null;
    }
}
