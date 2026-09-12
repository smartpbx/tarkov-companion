namespace TarkovCompanion.App.Services.Diagnostics;

public sealed record AppCommandLine(
    bool SelfTest,
    bool Demo,
    bool Headless,
    bool DeveloperMode,
    string? OutputPath,
    string? DemoFixturePath,
    string? DiagnosticChannelPath)
{
    /// <summary>
    /// A destination to open instead of the default one, by the name shown in the sidebar.
    /// </summary>
    /// <remarks>
    /// An init property rather than another positional parameter. This record is constructed
    /// by hand in several test projects, and adding to the positional list broke every one of
    /// them to add a diagnostic option none of them care about.
    /// </remarks>
    public string? StartPage { get; init; }

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
            GetValue(args, "--diagnostic-channel"))
        {
            StartPage = GetValue(args, "--page"),
        };
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
