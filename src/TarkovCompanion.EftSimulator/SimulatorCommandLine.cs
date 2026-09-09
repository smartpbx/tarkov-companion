namespace TarkovCompanion.EftSimulator;

public sealed record SimulatorCommandLine(
    bool DeveloperMode,
    bool ListScenarios,
    bool EmitFixturesOnly,
    string Scenario,
    string? LogRoot,
    string? ScreenshotRoot,
    string? StateOutput)
{
    public static SimulatorCommandLine Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new(
            HasFlag(args, "--developer-mode"),
            HasFlag(args, "--list-scenarios"),
            HasFlag(args, "--emit-fixtures-only"),
            GetValue(args, "--scenario") ?? SimulatorScenarioCatalog.All[0].Id,
            GetValue(args, "--log-root"),
            GetValue(args, "--screenshot-root"),
            GetValue(args, "--state-output"));
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
