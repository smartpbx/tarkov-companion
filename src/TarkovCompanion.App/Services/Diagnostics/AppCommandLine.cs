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

    /// <summary>Read each stash-grid caption as its own production-provider comparison.</summary>
    public bool OcrProbeCells { get; init; }

    /// <summary>
    /// A map to open on, instead of the default one.
    /// </summary>
    /// <remarks>
    /// The map is the most complex thing here and the hardest to see from Linux, and the page
    /// gallery photographs it in exactly one state: cold launch, default map, base floor, flat.
    /// So the gallery proves the map draws something rather than that it draws this map, or
    /// this floor, or the stack. Every map defect reported so far was found by looking at a
    /// picture, and these are the pictures nobody was taking.
    /// </remarks>
    public string? MapId { get; init; }

    /// <summary>A floor to select once that map has loaded, by name or id.</summary>
    public string? MapFloor { get; init; }

    /// <summary>Whether to open with the floors drawn as a stack.</summary>
    public bool StacksFloors { get; init; }

    /// <summary>
    /// Options that were passed and are not recognised.
    /// </summary>
    /// <remarks>
    /// An unknown option used to do nothing and say nothing, so an option that had not shipped
    /// yet was indistinguishable from an option that had no effect. Somebody ran
    /// <c>--ocr-probe-region</c> against a build without it, saw no region applied, and the
    /// available conclusion was that the region had not helped.
    ///
    /// Reported rather than fatal. A flag from a newer build passed to an older one is a
    /// mistake worth telling somebody about, not a reason to refuse to start.
    /// </remarks>
    public IReadOnlyList<string> UnknownOptions { get; init; } = [];

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
            MapId = GetValue(args, "--map"),
            MapFloor = GetValue(args, "--floor"),
            StacksFloors = HasFlag(args, "--stack"),
            OcrProbePath = GetValue(args, "--ocr-probe"),
            OcrProbeRegion = GetValue(args, "--ocr-probe-region"),
            OcrProbeCells = HasFlag(args, "--ocr-probe-cells"),
            UnknownOptions = FindUnknown(args),
            OcrProbeLines = GetValue(args, "--ocr-probe-lines") is { } lines &&
                int.TryParse(lines, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) &&
                count > 0
                    ? count
                    : null,
        };
    }

    /// <summary>Every option in the list, so the parser can say which ones it did not know.</summary>
    private static readonly string[] Known =
    [
        "--self-test",
        "--demo",
        "--headless",
        "--developer-mode",
        "--output",
        "--demo-fixture",
        "--diagnostic-channel",
        "--page",
        "--map",
        "--floor",
        "--stack",
        "--ocr-probe",
        "--ocr-probe-region",
        "--ocr-probe-lines",
        "--ocr-probe-cells",
    ];

    /// <summary>
    /// Anything that looks like an option and is not one.
    /// </summary>
    /// <remarks>
    /// Only what precedes a value is examined: an option's own value can be any string, and a
    /// value beginning with two dashes is already refused by <see cref="GetValue"/>.
    /// </remarks>
    private static IReadOnlyList<string> FindUnknown(IReadOnlyList<string> args)
    {
        var unknown = new List<string>();
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            if (Known.Contains(argument, StringComparer.OrdinalIgnoreCase))
            {
                // Step over its value, so a value that happens to start with two dashes is not
                // reported as an option. GetValue refuses those anyway; this keeps the two
                // readings of the same list agreeing.
                if (TakesValue(argument) && index + 1 < args.Count)
                {
                    index++;
                }

                continue;
            }

            unknown.Add(argument);
        }

        return unknown;
    }

    private static bool TakesValue(string option) => option is
        "--output" or "--demo-fixture" or "--diagnostic-channel" or
        "--page" or "--map" or "--floor" or
        "--ocr-probe" or "--ocr-probe-region" or "--ocr-probe-lines";

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
