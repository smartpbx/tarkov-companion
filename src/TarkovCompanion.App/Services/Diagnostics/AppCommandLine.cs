using System.Globalization;

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

    /// <summary>
    /// A screenshot to read every way the engine can, instead of starting the application.
    /// </summary>
    /// <remarks>
    /// The engine has been reading many lines and almost no words, and which of the two
    /// plausible causes it is cannot be settled by argument. This runs one real screenshot
    /// through each preparation and prints what each produced, so the answer comes from the
    /// pictures somebody already has.
    /// </remarks>
    public string? OcrProbePath { get; init; }

    /// <summary>
    /// The part of the screenshot to probe, as fractions of the frame: "x,y,w,h".
    /// </summary>
    /// <remarks>
    /// Probing a whole frame cannot find a panel. Measured on a real 3840x1080 screenshot:
    /// reading the whole thing returned 240 lines with the extract names not among the first
    /// twelve any preparation printed, and reading the panel alone returned sixteen with every
    /// name legible. Somebody probing a whole frame concludes the text is unreadable when it
    /// is merely buried.
    ///
    /// Fractions rather than pixels, because the frame this is pointed at is not always the
    /// frame the region was measured on.
    /// </remarks>
    public string? OcrProbeRegion { get; init; }

    /// <summary>How many lines the probe prints per preparation. Twelve on a 32:9 frame is a rounding error.</summary>
    public int? OcrProbeLines { get; init; }

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
            OcrProbePath = GetValue(args, "--ocr-probe"),
            OcrProbeRegion = GetValue(args, "--ocr-probe-region"),
            OcrProbeLines = GetValue(args, "--ocr-probe-lines") is { } lines &&
                int.TryParse(lines, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
                count > 0
                    ? count
                    : null,
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
