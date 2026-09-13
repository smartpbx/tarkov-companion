using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.App.Services.Diagnostics;

/// <summary>
/// Reads one real screenshot several ways and says which way read it best.
/// </summary>
/// <remarks>
/// The engine has been reading plenty of lines and almost none of the words. Verbatim, from a
/// 3840x1080 screenshot of the stash:
///
/// <code>
/// [&amp; Searching experience - Items (3) | LOOT THIS | \VPO-136 | A-2607 | BD |p | Zarya | SR-MP]
/// [VSS Ses | Bp B B | 4-2607 | pee tl | RAMP: | "DUG | SLA, | eer | ISS | tit | + a | a=,]
/// </code>
///
/// Real strings leak through, so the capture is of the game and the engine is running. It is
/// simply reading badly, and a count of lines said none of that: it looked like success.
///
/// Two candidate causes, neither settled by argument. The interface text is around sixteen
/// pixels tall where the engine wants thirty, and the rest of the frame is scenery that it
/// reads as words. So rather than pick one and ship it, this runs the same picture through
/// each preparation and prints what each produced. Point it at the screenshots somebody
/// actually has and the answer is measured instead of assumed.
/// </remarks>
public static class OcrProbe
{
    private static readonly (string Name, OcrPreparation Preparation)[] Variants =
    [
        ("as captured", OcrPreparation.AsCaptured),
        ("2x", new(2)),
        ("3x", new(3)),
        ("bright text only", new(1, BrightTextOnly: true)),
        ("2x, bright text only", new(2, BrightTextOnly: true)),
        ("3x, bright text only", new(3, BrightTextOnly: true)),
    ];

    /// <summary>Runs every preparation over one screenshot and writes a table to the console.</summary>
    public static async Task<int> RunAsync(string screenshotPath, AppCommandLine options, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshotPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(screenshotPath))
        {
            await Console.Error.WriteLineAsync($"No such screenshot: {screenshotPath}").ConfigureAwait(false);
            return 1;
        }

        using var services = AppComposition.Build(options);
        var loader = services.GetRequiredService<IScreenshotImageLoader>();
        var detector = services.GetRequiredService<ScanContextDetector>();

        // Both engines, named, rather than whichever one the container hands back.
        //
        // This resolved IOcrEngine — the Windows engine on Windows — and then ran six
        // OcrPreparation variants that engine deliberately ignores, printing the same read six
        // times beside SafeScale figures it never used. Every measurement in
        // EFT_SCREENSHOT_FACTS.md so far is therefore Tesseract's, while the engine that
        // ships on Windows is the one nothing has ever measured.
        var engines = Engines(services);
        var image = await loader.LoadAsync(screenshotPath, cancellationToken).ConfigureAwait(false);
        if (image is null)
        {
            await Console.Error.WriteLineAsync($"Could not read that screenshot: {screenshotPath}").ConfigureAwait(false);
            return 1;
        }

        var region = ParseRegion(options.OcrProbeRegion, image);
        var lineCount = options.OcrProbeLines ?? DefaultLines;
        Console.WriteLine($"{Path.GetFileName(screenshotPath)} · {image.Width}x{image.Height}");
        if (region is { } cropped)
        {
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"region {cropped.X},{cropped.Y} {cropped.Width}x{cropped.Height}"));
        }

        Console.WriteLine();
        foreach (var (engineName, engine) in engines)
        {
            Console.WriteLine($"── {engineName} ──");
            if (engine is IOcrEngineStatus status && !status.Availability.IsAvailable)
            {
                Console.WriteLine($"unavailable · {status.Availability.Reason ?? "no reason reported"}");
                Console.WriteLine();
                continue;
            }

        foreach (var (name, preparation) in Variants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await engine
                .RecognizeAsync(
                    image,
                    new(ScanContext.Unknown, region) { Preparation = preparation },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsAvailable)
            {
                Console.WriteLine($"{name}: engine unavailable · {result.DiagnosticCode}");
                continue;
            }

            // The size it was actually read at, beside the time it took. At three times a
            // 3840x1080 frame is thirty-seven megapixels, and printing the number next to the
            // seconds makes the cost of a preparation obvious rather than implied.
            var width = (region?.Width ?? image.Width) * preparation.SafeScale;
            var height = (region?.Height ?? image.Height) * preparation.SafeScale;
            var megapixels = width / 1000d * height / 1000d;
            var detected = detector.Detect(image, result);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: {detected.Context} {detected.Confidence.Value:F2} · {result.Lines.Count} lines · " +
                $"{width}x{height} ({megapixels:F1} MP) · {result.Duration.TotalSeconds:F1}s"));
            Console.WriteLine("  " + Sample(result.Lines, lineCount));
            Console.WriteLine();
        }

            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// Every recogniser this build has, by name, rather than whichever one would be chosen.
    /// </summary>
    /// <remarks>
    /// The point of a probe is to compare them. Resolving IOcrEngine gives the one that would
    /// ship — on Windows that is the Windows engine — so the probe measured one engine while
    /// every recorded measurement in EFT_SCREENSHOT_FACTS.md came from the other, and nobody
    /// could see that from the output.
    ///
    /// An engine that will not start is listed and reported rather than skipped, because "the
    /// Windows recogniser is unavailable here, and this is why" is one of the answers the
    /// probe exists to give.
    /// </remarks>
    private static IReadOnlyList<(string Name, IOcrEngine Engine)> Engines(IServiceProvider services)
    {
        var engines = new List<(string, IOcrEngine)>();
#if WINDOWS
        engines.Add((
            "windows-media-ocr",
            services.GetRequiredService<TarkovCompanion.Platform.Windows.Ocr.WindowsMediaOcrEngine>()));
#endif
        engines.Add(("tesseract", services.GetRequiredService<TesseractOcrEngine>()));
        return engines;
    }

    /// <summary>How many lines are printed unless somebody asks for more.</summary>
    private const int DefaultLines = 12;

    /// <summary>
    /// Turns "x,y,w,h" in fractions of the frame into a rectangle in pixels.
    /// </summary>
    /// <remarks>
    /// Fractions rather than pixels, because a region measured on one screenshot is usually
    /// pointed at another. A value that will not parse is reported and ignored rather than
    /// silently treated as the whole frame, which would look like the region simply did not
    /// help.
    /// </remarks>
    public static PixelRect? ParseRegion(string? value, CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            Console.Error.WriteLine($"Ignoring --ocr-probe-region '{value}': expected four numbers, x,y,w,h.");
            return null;
        }

        var numbers = new double[4];
        for (var index = 0; index < 4; index++)
        {
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index]))
            {
                Console.Error.WriteLine($"Ignoring --ocr-probe-region '{value}': '{parts[index]}' is not a number.");
                return null;
            }
        }

        var x = Math.Clamp((int)Math.Round(numbers[0] * image.Width), 0, image.Width);
        var y = Math.Clamp((int)Math.Round(numbers[1] * image.Height), 0, image.Height);
        var width = Math.Clamp((int)Math.Round(numbers[2] * image.Width), 0, image.Width - x);
        var height = Math.Clamp((int)Math.Round(numbers[3] * image.Height), 0, image.Height - y);
        if (width <= 0 || height <= 0)
        {
            Console.Error.WriteLine($"Ignoring --ocr-probe-region '{value}': it selects nothing.");
            return null;
        }

        return new(x, y, width, height);
    }

    /// <summary>
    /// The first of what it read, so a person can see the difference rather than trust a score.
    /// </summary>
    private static string Sample(IReadOnlyList<OcrLine> lines, int count)
    {
        var builder = new StringBuilder();
        foreach (var line in lines.Take(count))
        {
            if (builder.Length > 0)
            {
                builder.Append(" | ");
            }

            builder.Append(line.Text);
        }

        if (lines.Count > count)
        {
            builder.Append(string.Create(CultureInfo.InvariantCulture, $" | (+{lines.Count - count} more)"));
        }

        return builder.Length == 0 ? "(nothing)" : builder.ToString();
    }
}
