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
        var engine = services.GetRequiredService<IOcrEngine>();
        var detector = services.GetRequiredService<ScanContextDetector>();
        var image = await loader.LoadAsync(screenshotPath, cancellationToken).ConfigureAwait(false);
        if (image is null)
        {
            await Console.Error.WriteLineAsync($"Could not read that screenshot: {screenshotPath}").ConfigureAwait(false);
            return 1;
        }

        Console.WriteLine($"{Path.GetFileName(screenshotPath)} · {image.Width}x{image.Height}");
        Console.WriteLine();
        foreach (var (name, preparation) in Variants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await engine
                .RecognizeAsync(image, new(ScanContext.Unknown) { Preparation = preparation }, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsAvailable)
            {
                Console.WriteLine($"{name}: engine unavailable · {result.DiagnosticCode}");
                continue;
            }

            var detected = detector.Detect(image, result);
            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{name}: {detected.Context} {detected.Confidence.Value:F2} · {result.Lines.Count} lines · {result.Duration.TotalSeconds:F1}s"));
            Console.WriteLine("  " + Sample(result.Lines));
            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// The first of what it read, so a person can see the difference rather than trust a score.
    /// </summary>
    private static string Sample(IReadOnlyList<OcrLine> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines.Take(12))
        {
            if (builder.Length > 0)
            {
                builder.Append(" | ");
            }

            builder.Append(line.Text);
        }

        return builder.Length == 0 ? "(nothing)" : builder.ToString();
    }
}
