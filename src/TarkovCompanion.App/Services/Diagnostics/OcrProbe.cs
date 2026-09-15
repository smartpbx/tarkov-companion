using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.App.Services.Diagnostics;

public sealed record OcrProbeFrame(int Width, int Height, DateTimeOffset CapturedUtc);

public sealed record OcrProbeLine(
    string? Text,
    PixelRect Bounds,
    double? Confidence);

public sealed record OcrProbeTile(
    int Ordinal,
    PixelRect SourceRegion,
    int Width,
    int Height,
    int Scale,
    double DurationMilliseconds,
    string Status,
    string? DiagnosticCode);

public sealed record OcrProbePass(
    string Name,
    string Provider,
    string Status,
    string? DiagnosticCode,
    PixelRect SourceRegion,
    int SourceWidth,
    int SourceHeight,
    int PreparedWidth,
    int PreparedHeight,
    int Scale,
    int TileCount,
    int AttemptedTileCount,
    int CompletedTileCount,
    long SourcePixelCount,
    long EstimatedPeakBytes,
    double DurationMilliseconds,
    int LineCount,
    int ScoredLineCount,
    int UnscoredLineCount,
    string? DetectedContext,
    double? ContextScore,
    bool HealthAndCharacterTextPresent,
    bool VersionStripTextPresent,
    IReadOnlyList<OcrProbeLine> Lines,
    IReadOnlyList<OcrProbeTile> Tiles);

public sealed record OcrProbeEngine(
    string Provider,
    bool IsAvailable,
    string? UnavailableReason,
    IReadOnlyList<OcrProbePass> Passes);

public sealed record OcrProbeCell(
    int Ordinal,
    PixelRect Bounds,
    PixelRect Caption);

/// <summary>
/// Stable local producer output for OCR diagnostics. It is deliberately independent of the
/// provisional corpus/scorer schema: in particular, line confidence stays nullable.
/// </summary>
public sealed record OcrProbeReport(
    string SchemaVersion,
    string Mode,
    DateTimeOffset GeneratedUtc,
    OcrProbeFrame Frame,
    PixelRect SourceRegion,
    string? DiagnosticCode,
    IReadOnlyList<OcrProbeCell> Cells,
    IReadOnlyList<OcrProbeEngine> Engines)
{
    public const string CurrentSchemaVersion = "tarkov-companion.ocr-probe.v1";
}

/// <summary>
/// Reads one deliberately selected local screenshot and reports what each production OCR
/// provider measured. Source pixels, source paths, and screenshot filenames never enter the
/// machine-readable result.
/// </summary>
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

    private static readonly JsonSerializerOptions ReportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Runs every preparation over one screenshot and writes a human-readable table.</summary>
    public static async Task<int> RunAsync(
        string screenshotPath,
        AppCommandLine options,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(screenshotPath, options, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return 1;
        }

        var (services, image, region) = loaded.Value;
        using (services)
        {
            var detector = services.GetRequiredService<ScanContextDetector>();
            var supplemental = new SupplementalOcrSignalDetector();
            var lineCount = options.OcrProbeLines ?? DefaultLines;
            var engines = Engines(services);
            var engineReports = new List<OcrProbeEngine>(engines.Count);

            // EFT screenshot filenames may carry exact world coordinates. The selected path
            // is deliberately never echoed into diagnostic output.
            Console.WriteLine($"selected screenshot · {image.Width}x{image.Height}");
            if (options.OcrProbeRegion is not null)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"region {region.X},{region.Y} {region.Width}x{region.Height}"));
            }

            Console.WriteLine();
            foreach (var (engineName, engine) in engines)
            {
                Console.WriteLine($"── {engineName} ──");
                if (engine is IOcrEngineStatus status && !status.Availability.IsAvailable)
                {
                    Console.WriteLine($"unavailable · {status.Availability.Reason ?? "no reason reported"}");
                    Console.WriteLine();
                    engineReports.Add(new(
                        status.Availability.Provider,
                        false,
                        status.Availability.Reason,
                        []));
                    continue;
                }

                var preparations = Preparations(engine);
                var passes = new List<OcrProbePass>(preparations.Count);
                foreach (var (name, preparation) in preparations)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pass = await ExecuteAsync(
                            engine,
                            image,
                            new OcrRequest(ScanContext.Unknown, region) { Preparation = preparation },
                            name,
                            includeText: true,
                            detector,
                            supplemental,
                            cancellationToken)
                        .ConfigureAwait(false);
                    passes.Add(pass);

                    var megapixels = pass.PreparedWidth / 1000d * pass.PreparedHeight / 1000d;
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"{name}: {pass.DetectedContext ?? "Unknown"} {FormatScore(pass.ContextScore)} · " +
                        $"{pass.LineCount} lines · {pass.PreparedWidth}x{pass.PreparedHeight} " +
                        $"({megapixels:F1} MP) · {pass.TileCount} tile(s) · " +
                        $"{pass.DurationMilliseconds / 1000:F1}s · {pass.Status}"));
                    Console.WriteLine("  " + Sample(pass, lineCount));
                    Console.WriteLine();
                }

                var availability = engine as IOcrEngineStatus;
                engineReports.Add(new(
                    availability?.Availability.Provider ?? engineName,
                    true,
                    null,
                    passes));
                Console.WriteLine();
            }

            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                var report = Report(
                    "full-frame",
                    image,
                    region,
                    null,
                    [],
                    RedactLineText(engineReports));
                await WriteReportAsync(options.OutputPath, report, cancellationToken).ConfigureAwait(false);
            }
        }

        return 0;
    }

    /// <summary>
    /// Runs each production provider over the caption returned by <see cref="StashGrid.Cells"/>
    /// and emits one JSON document. The human comparison goes to stderr so stdout remains a
    /// parseable machine result when no output file was requested.
    /// </summary>
    public static async Task<int> RunCellsAsync(
        string screenshotPath,
        AppCommandLine options,
        CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(screenshotPath, options, cancellationToken).ConfigureAwait(false);
        if (loaded is null)
        {
            return 1;
        }

        var (services, image, region) = loaded.Value;
        using (services)
        {
            var cells = StashGrid.Cells(image, region)
                .Select((cell, ordinal) => new OcrProbeCell(ordinal, cell.Bounds, cell.Caption))
                .ToArray();
            if (cells.Length == 0)
            {
                Console.Error.WriteLine("stash grid not found in the selected source region");
            }
            var detector = services.GetRequiredService<ScanContextDetector>();
            var supplemental = new SupplementalOcrSignalDetector();
            var engineReports = new List<OcrProbeEngine>();

            foreach (var (engineName, engine) in Engines(services))
            {
                var availability = engine as IOcrEngineStatus;
                if (availability is not null && !availability.Availability.IsAvailable)
                {
                    Console.Error.WriteLine(
                        $"{engineName}: unavailable · {availability.Availability.Reason ?? "no reason reported"}");
                    engineReports.Add(new(
                        availability.Availability.Provider,
                        false,
                        availability.Availability.Reason,
                        []));
                    continue;
                }

                var passes = new List<OcrProbePass>(cells.Length);
                foreach (var cell in cells)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var pass = await ExecuteAsync(
                            engine,
                            image,
                            new OcrRequest(ScanContext.Container, cell.Caption),
                            $"cell-{cell.Ordinal}",
                            includeText: true,
                            detector,
                            supplemental,
                            cancellationToken)
                        .ConfigureAwait(false);
                    passes.Add(pass);
                }

                Console.Error.WriteLine(
                    $"{engineName}: {passes.Count(pass => pass.Status == "complete")}/{cells.Length} cell(s) completed");
                foreach (var pass in passes)
                {
                    Console.Error.WriteLine($"  {pass.Name}: {Sample(pass, DefaultCellLines)}");
                }

                engineReports.Add(new(
                    availability?.Availability.Provider ?? engineName,
                    true,
                    null,
                    passes));
            }

            var report = Report(
                "cells",
                image,
                region,
                cells.Length == 0 ? "stash_grid_not_found" : null,
                cells,
                engineReports);
            if (string.IsNullOrWhiteSpace(options.OutputPath))
            {
                await Console.Out.WriteLineAsync(SerializeReport(report)).ConfigureAwait(false);
            }
            else
            {
                await WriteReportAsync(options.OutputPath, report, cancellationToken).ConfigureAwait(false);
            }
        }

        return 0;
    }

    private static async Task<OcrProbePass> ExecuteAsync(
        IOcrEngine engine,
        CapturedImage image,
        OcrRequest request,
        string name,
        bool includeText,
        ScanContextDetector detector,
        SupplementalOcrSignalDetector supplementalDetector,
        CancellationToken cancellationToken)
    {
#if WINDOWS
        if (engine is TarkovCompanion.Platform.Windows.Ocr.WindowsMediaOcrEngine windows)
        {
            var execution = await windows
                .RecognizeDetailedAsync(image, request, cancellationToken)
                .ConfigureAwait(false);
            return Pass(
                name,
                execution.Result,
                execution.Status.ToString(),
                execution.DiagnosticCode,
                image,
                execution.SourceRegion,
                execution.SourceWidth,
                execution.SourceHeight,
                execution.SourceRegion.Width,
                execution.SourceRegion.Height,
                execution.Scale,
                execution.TileCount,
                execution.AttemptedTileCount,
                execution.CompletedTileCount,
                execution.SourcePixelCount,
                execution.EstimatedPeakBytes,
                execution.Duration,
                includeText,
                detector,
                supplementalDetector,
                execution.Tiles.Select(tile => new OcrProbeTile(
                    tile.Ordinal,
                    tile.SourceRegion,
                    tile.Width,
                    tile.Height,
                    tile.Scale,
                    tile.Duration.TotalMilliseconds,
                    Status(tile.Status.ToString()),
                    tile.DiagnosticCode)).ToArray());
        }
#endif
        if (engine is TesseractOcrEngine tesseract)
        {
            var execution = await tesseract
                .RecognizeDetailedAsync(image, request, cancellationToken)
                .ConfigureAwait(false);
            var tiles = execution.TileCount == 0
                ? []
                : new[]
                {
                    new OcrProbeTile(
                        0,
                        execution.SourceRegion,
                        execution.PreparedWidth,
                        execution.PreparedHeight,
                        execution.Scale,
                        execution.Duration.TotalMilliseconds,
                        Status(execution.Status.ToString()),
                        execution.DiagnosticCode),
                };
            return Pass(
                name,
                execution.Result,
                execution.Status.ToString(),
                execution.DiagnosticCode,
                image,
                execution.SourceRegion,
                execution.SourceWidth,
                execution.SourceHeight,
                execution.PreparedWidth,
                execution.PreparedHeight,
                execution.Scale,
                execution.TileCount,
                execution.TileCount,
                execution.Status == OcrExecutionStatus.Complete ? execution.TileCount : 0,
                execution.SourcePixelCount,
                execution.EstimatedPeakBytes,
                execution.Duration,
                includeText,
                detector,
                supplementalDetector,
                tiles);
        }

        var result = await engine.RecognizeAsync(image, request, cancellationToken).ConfigureAwait(false);
        var region = request.Region ?? new PixelRect(0, 0, image.Width, image.Height);
        var scale = request.Preparation.SafeScale;
        return Pass(
            name,
            result,
            result.IsAvailable ? "complete" : "unavailable",
            result.DiagnosticCode,
            image,
            region,
            image.Width,
            image.Height,
            checked(region.Width * scale),
            checked(region.Height * scale),
            scale,
            1,
            1,
            result.IsAvailable ? 1 : 0,
            checked((long)image.Width * image.Height),
            image.Pixels.Length,
            result.Duration,
            includeText,
            detector,
            supplementalDetector,
            []);
    }

    private static OcrProbePass Pass(
        string name,
        OcrResult result,
        string status,
        string? diagnostic,
        CapturedImage image,
        PixelRect sourceRegion,
        int sourceWidth,
        int sourceHeight,
        int preparedWidth,
        int preparedHeight,
        int scale,
        int tileCount,
        int attemptedTileCount,
        int completedTileCount,
        long sourcePixelCount,
        long estimatedPeakBytes,
        TimeSpan duration,
        bool includeText,
        ScanContextDetector detector,
        SupplementalOcrSignalDetector supplementalDetector,
        IReadOnlyList<OcrProbeTile> tiles)
    {
        var context = detector.Detect(image, result);
        var supplemental = supplementalDetector.Detect(result);
        var lines = result.Lines.Select(line => new OcrProbeLine(
            includeText ? line.Text : null,
            line.Bounds,
            line.Confidence?.Value)).ToArray();
        return new(
            name,
            result.Engine,
            Status(status),
            diagnostic,
            sourceRegion,
            sourceWidth,
            sourceHeight,
            preparedWidth,
            preparedHeight,
            scale,
            tileCount,
            attemptedTileCount,
            completedTileCount,
            sourcePixelCount,
            estimatedPeakBytes,
            duration.TotalMilliseconds,
            result.Lines.Count,
            result.Lines.Count(line => line.Confidence is not null),
            result.Lines.Count(line => line.Confidence is null),
            context.Context.ToString(),
            result.IsAvailable ? context.Confidence.Value : null,
            supplemental.HealthAndCharacter.IsPresent,
            supplemental.VersionStrip.IsPresent,
            lines,
            tiles);
    }

    private static async Task<(ServiceProvider Services, CapturedImage Image, PixelRect Region)?> LoadAsync(
        string screenshotPath,
        AppCommandLine options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(screenshotPath);
        ArgumentNullException.ThrowIfNull(options);
        if (!File.Exists(screenshotPath))
        {
            await Console.Error.WriteLineAsync("The selected OCR probe screenshot does not exist.").ConfigureAwait(false);
            return null;
        }

        if (!string.IsNullOrWhiteSpace(options.OutputPath) &&
            string.Equals(
                Path.GetFullPath(screenshotPath),
                Path.GetFullPath(options.OutputPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            await Console.Error.WriteLineAsync("The OCR report output must not overwrite its source screenshot.")
                .ConfigureAwait(false);
            return null;
        }

        var services = AppComposition.Build(options);
        try
        {
            var loader = services.GetRequiredService<IScreenshotImageLoader>();
            var image = await loader.LoadAsync(screenshotPath, cancellationToken).ConfigureAwait(false);
            if (image is null)
            {
                await Console.Error.WriteLineAsync("The selected OCR probe screenshot could not be decoded.").ConfigureAwait(false);
                services.Dispose();
                return null;
            }

            var requested = ParseRegion(options.OcrProbeRegion, image);
            return (services, image, requested ?? new PixelRect(0, 0, image.Width, image.Height));
        }
        catch
        {
            services.Dispose();
            throw;
        }
    }

    private static OcrProbeReport Report(
        string mode,
        CapturedImage image,
        PixelRect region,
        string? diagnosticCode,
        IReadOnlyList<OcrProbeCell> cells,
        IReadOnlyList<OcrProbeEngine> engines) => new(
            OcrProbeReport.CurrentSchemaVersion,
            mode,
            DateTimeOffset.UtcNow,
            new(image.Width, image.Height, image.CapturedUtc),
            region,
            diagnosticCode,
            cells,
            engines);

    private static IReadOnlyList<OcrProbeEngine> RedactLineText(IReadOnlyList<OcrProbeEngine> engines) =>
        engines.Select(engine => engine with
        {
            Passes = engine.Passes.Select(pass => pass with
            {
                Lines = pass.Lines.Select(line => line with { Text = null }).ToArray(),
            }).ToArray(),
        }).ToArray();

    private static async Task WriteReportAsync(
        string outputPath,
        OcrProbeReport report,
        CancellationToken cancellationToken)
    {
        var json = SerializeReport(report);
        await File.WriteAllTextAsync(outputPath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
    }

    public static string SerializeReport(OcrProbeReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, ReportJson);
    }

    /// <summary>Every production recogniser this build has, rather than only the selected one.</summary>
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

    private static IReadOnlyList<(string Name, OcrPreparation Preparation)> Preparations(IOcrEngine engine)
    {
#if WINDOWS
        if (engine is TarkovCompanion.Platform.Windows.Ocr.WindowsMediaOcrEngine)
        {
            // Windows performs its own binarization and runs every tile at native scale. Six
            // labels for six ignored Tesseract preparations used to print the same Windows
            // result six times and falsely claim five transformations were measured.
            return [("native Windows", OcrPreparation.AsCaptured)];
        }
#endif
        return Variants;
    }

    private const int DefaultLines = 12;
    private const int DefaultCellLines = 3;

    /// <summary>Turns "x,y,w,h" in frame fractions into a bounded source rectangle.</summary>
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
            if (!double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[index]) ||
                !double.IsFinite(numbers[index]))
            {
                Console.Error.WriteLine($"Ignoring --ocr-probe-region '{value}': '{parts[index]}' is not a finite number.");
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

    private static string Sample(OcrProbePass pass, int count)
    {
        var builder = new StringBuilder();
        foreach (var line in pass.Lines.Take(count))
        {
            if (builder.Length > 0)
            {
                builder.Append(" | ");
            }

            builder.Append(line.Text ?? "[text omitted from report]");
        }

        if (pass.Lines.Count > count)
        {
            builder.Append(string.Create(CultureInfo.InvariantCulture, $" | (+{pass.Lines.Count - count} more)"));
        }

        return builder.Length == 0 ? "(nothing)" : builder.ToString();
    }

    private static string FormatScore(double? value) => value is { } score
        ? score.ToString("F2", CultureInfo.InvariantCulture)
        : "unscored";

    private static string Status(string value)
    {
        var builder = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character == '_' ? '-' : character));
        }

        return builder.ToString();
    }
}
