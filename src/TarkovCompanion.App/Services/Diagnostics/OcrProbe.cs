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
    int PlannedTileCount,
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
    IReadOnlyList<OcrProbeTile> Tiles)
{
    /// <summary>Lines whose text the provider cut to its per-line ceiling.</summary>
    public int TruncatedLineCount { get; init; }
}

/// <summary>One provider's share of a probe run.</summary>
/// <remarks>
/// Planned passes are what the run meant to read with this provider. Attempted passes started;
/// completed passes finished with a complete or empty read. A deadline or an exhausted provider
/// leaves the difference visible instead of shrinking the plan to match what happened.
///
/// Availability is read again after the passes. It used to be written as available whatever
/// happened, so a provider that failed on its first pass and marked itself unavailable was
/// reported as a healthy provider with five unexplained missing passes.
/// </remarks>
public sealed record OcrProbeEngine(
    string Provider,
    bool IsAvailable,
    string? UnavailableReason,
    IReadOnlyList<OcrProbePass> Passes)
{
    public int PlannedPassCount { get; init; }

    public int AttemptedPassCount { get; init; }

    public int CompletedPassCount { get; init; }

    /// <summary>Why this provider's remaining passes were not attempted, when a provider failure stopped them.</summary>
    public string? DiagnosticCode { get; init; }
}

public sealed record OcrProbeCell(
    int Ordinal,
    PixelRect Bounds,
    PixelRect Caption);

/// <summary>
/// Stable local producer output for OCR diagnostics. It is deliberately independent of the #272
/// corpus interchange, which it is never scored through: in particular, line confidence stays nullable.
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

    /// <summary>Cells the stash grid returned, before the run's cell ceiling was applied.</summary>
    public int? DetectedCellCount { get; init; }
}

/// <summary>Bounds on one probe run.</summary>
/// <remarks>
/// A 7680x2160 stash can return about a thousand cells, and each is read by every production
/// provider. Without a ceiling and a single deadline, one command could spend an afternoon
/// holding the OCR gate, and pressing Ctrl+C could not stop it.
/// </remarks>
public sealed record OcrProbeLimits
{
    /// <summary>One deadline for every provider pass in the run.</summary>
    public TimeSpan RunTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>The most stash-grid captions one run reads with each provider.</summary>
    public int MaximumCells { get; init; } = 256;

    /// <summary>
    /// How long a provider's report waits, after its last pass, for native work a pass abandoned to
    /// settle before availability is recorded.
    /// </summary>
    /// <remarks>
    /// Separate from the run deadline, which may be what abandoned the work. Work still running
    /// when this runs out is reported as <see cref="OcrProbe.SettleTimeoutDiagnostic"/> rather
    /// than as a provider that is known to be available.
    /// </remarks>
    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The clock the run deadline is measured on: the system's, unless a test supplies one.</summary>
    /// <remarks>
    /// A system timer fires on a pool thread. A test that stalls discovery past the deadline and
    /// expects it to have fired meanwhile measures the pool's latency under a parallel run.
    /// </remarks>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Reads one deliberately selected local screenshot and reports what each production OCR
/// provider measured. Source pixels, source paths, and screenshot filenames never enter the
/// machine-readable result.
/// </summary>
public static class OcrProbe
{
    public const string DeadlineDiagnostic = "ocr_probe_deadline_exceeded";
    public const string CellLimitDiagnostic = "ocr_probe_cell_limit_exceeded";

    /// <summary>
    /// A provider's native work abandoned by a pass had not settled when its report was recorded,
    /// so the availability in that report is the last known one and may since have changed.
    /// </summary>
    public const string SettleTimeoutDiagnostic = "ocr_provider_settle_timeout";
    private const string MemoryExhaustedDiagnostic = "ocr_memory_exhausted";
    private const string ProviderFailedDiagnostic = "ocr_provider_failed";
    private const string ProviderUnavailableDiagnostic = "ocr_provider_unavailable";

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
            var report = await ProbeFrameAsync(
                    image,
                    region,
                    Engines(services),
                    services.GetRequiredService<ScanContextDetector>(),
                    new OcrProbeLimits(),
                    options.OcrProbeLines ?? DefaultLines,
                    Console.Out,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(options.OutputPath))
            {
                await WriteReportAsync(
                        options.OutputPath,
                        report with { Engines = RedactLineText(report.Engines) },
                        cancellationToken)
                    .ConfigureAwait(false);
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
            var report = await ProbeCellsAsync(
                    image,
                    region,
                    Engines(services),
                    services.GetRequiredService<ScanContextDetector>(),
                    new OcrProbeLimits(),
                    Console.Error,
                    cancellationToken)
                .ConfigureAwait(false);
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

    /// <summary>
    /// Reads every supported preparation of one region with each provider, under one run
    /// deadline. Caller cancellation throws; the deadline ends the run with its evidence kept.
    /// </summary>
    public static async Task<OcrProbeReport> ProbeFrameAsync(
        CapturedImage image,
        PixelRect region,
        IReadOnlyList<(string Name, IOcrEngine Engine)> engines,
        ScanContextDetector detector,
        OcrProbeLimits limits,
        int sampleLines,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(log);
        using var run = new ProbeRun(limits, cancellationToken);
        var supplemental = new SupplementalOcrSignalDetector();
        var reports = new List<OcrProbeEngine>(engines.Count);
        foreach (var (engineName, engine) in engines)
        {
            var planned = Preparations(engine)
                .Select(variant => (variant.Name, Request: new OcrRequest(ScanContext.Unknown, region)
                {
                    Preparation = variant.Preparation,
                }))
                .ToArray();
            log.WriteLine($"── {engineName} ──");
            if (engine is IOcrEngineStatus status && !status.Availability.IsAvailable)
            {
                log.WriteLine($"unavailable · {status.Availability.Reason ?? "no reason reported"}");
                log.WriteLine();
                reports.Add(Unavailable(status.Availability, planned.Length));
                continue;
            }

            var report = await ProbeEngineAsync(
                    run,
                    engineName,
                    engine,
                    image,
                    planned,
                    detector,
                    supplemental,
                    pass => DescribeFramePass(log, pass, sampleLines),
                    cancellationToken)
                .ConfigureAwait(false);
            if (report.DiagnosticCode is not null)
            {
                log.WriteLine($"stopped after provider failure: {report.DiagnosticCode}");
            }

            reports.Add(report);
            log.WriteLine();
        }

        if (run.StopDiagnostic is not null)
        {
            log.WriteLine($"probe stopped early: {run.StopDiagnostic}");
        }

        return Report("full-frame", image, region, run.StopDiagnostic, [], reports);
    }

    /// <summary>
    /// Finds the stash grid in the region and reads its captions, with the search spending from
    /// the same run deadline as every provider pass.
    /// </summary>
    /// <remarks>
    /// The search reads every pixel of the region once per axis. It used to run before the run's
    /// deadline started, so on a large region the slowest step of the command was the only one the
    /// deadline did not cover. A deadline that expires during the search reports
    /// <see cref="DeadlineDiagnostic"/> with no cells and no passes; caller cancellation throws.
    /// </remarks>
    public static async Task<OcrProbeReport> ProbeCellsAsync(
        CapturedImage image,
        PixelRect region,
        IReadOnlyList<(string Name, IOcrEngine Engine)> engines,
        ScanContextDetector detector,
        OcrProbeLimits limits,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(log);
        using var run = new ProbeRun(limits, cancellationToken);
        IReadOnlyList<StashCell> cells;
        try
        {
            cells = StashGrid.Cells(image, region, run.Token);
        }
        catch (OperationCanceledException) when (run.IsExpired)
        {
            log.WriteLine($"probe stopped early: {DeadlineDiagnostic}");
            var notStarted = engines
                .Select(entry => (entry.Engine as IOcrEngineStatus)?.Availability is { } availability
                    ? new OcrProbeEngine(
                        availability.Provider,
                        availability.IsAvailable,
                        availability.IsAvailable ? null : availability.Reason,
                        [])
                    : new OcrProbeEngine(entry.Name, true, null, []))
                .ToArray();
            return Report("cells", image, region, DeadlineDiagnostic, [], notStarted);
        }

        return await ReadCellsAsync(run, image, region, cells, engines, detector, limits, log, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads at most <see cref="OcrProbeLimits.MaximumCells"/> stash captions with each provider
    /// under one run deadline. Caller cancellation throws; the deadline ends the run with its
    /// evidence kept and every provider's planned, attempted and completed counts intact.
    /// </summary>
    public static async Task<OcrProbeReport> ProbeCellsAsync(
        CapturedImage image,
        PixelRect region,
        IReadOnlyList<StashCell> cells,
        IReadOnlyList<(string Name, IOcrEngine Engine)> engines,
        ScanContextDetector detector,
        OcrProbeLimits limits,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(cells);
        ArgumentNullException.ThrowIfNull(engines);
        ArgumentNullException.ThrowIfNull(detector);
        ArgumentNullException.ThrowIfNull(log);
        using var run = new ProbeRun(limits, cancellationToken);
        return await ReadCellsAsync(run, image, region, cells, engines, detector, limits, log, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<OcrProbeReport> ReadCellsAsync(
        ProbeRun run,
        CapturedImage image,
        PixelRect region,
        IReadOnlyList<StashCell> cells,
        IReadOnlyList<(string Name, IOcrEngine Engine)> engines,
        ScanContextDetector detector,
        OcrProbeLimits limits,
        TextWriter log,
        CancellationToken cancellationToken)
    {
        var selected = cells
            .Take(limits.MaximumCells)
            .Select((cell, ordinal) => new OcrProbeCell(ordinal, cell.Bounds, cell.Caption))
            .ToArray();
        if (cells.Count == 0)
        {
            log.WriteLine("stash grid not found in the selected source region");
        }
        else if (cells.Count > selected.Length)
        {
            log.WriteLine($"stash grid returned {cells.Count} cells; reading the first {selected.Length}");
        }

        var planned = selected
            .Select(cell => (Name: $"cell-{cell.Ordinal}", Request: new OcrRequest(ScanContext.Container, cell.Caption)))
            .ToArray();
        var supplemental = new SupplementalOcrSignalDetector();
        var reports = new List<OcrProbeEngine>(engines.Count);
        foreach (var (engineName, engine) in engines)
        {
            if (engine is IOcrEngineStatus status && !status.Availability.IsAvailable)
            {
                log.WriteLine($"{engineName}: unavailable · {status.Availability.Reason ?? "no reason reported"}");
                reports.Add(Unavailable(status.Availability, planned.Length));
                continue;
            }

            var report = await ProbeEngineAsync(
                    run,
                    engineName,
                    engine,
                    image,
                    planned,
                    detector,
                    supplemental,
                    _ => { },
                    cancellationToken)
                .ConfigureAwait(false);
            log.WriteLine(
                $"{engineName}: {report.CompletedPassCount}/{report.PlannedPassCount} cell(s) completed, " +
                $"{report.AttemptedPassCount} attempted" +
                (report.DiagnosticCode is null ? string.Empty : $"; stopped after {report.DiagnosticCode}"));
            foreach (var pass in report.Passes)
            {
                log.WriteLine($"  {pass.Name}: {Sample(pass, DefaultCellLines)}");
            }

            reports.Add(report);
        }

        if (run.StopDiagnostic is not null)
        {
            log.WriteLine($"probe stopped early: {run.StopDiagnostic}");
        }

        var diagnostic = run.StopDiagnostic
            ?? (cells.Count == 0
                ? "stash_grid_not_found"
                : cells.Count > selected.Length ? CellLimitDiagnostic : null);
        return Report("cells", image, region, diagnostic, selected, reports) with
        {
            DetectedCellCount = cells.Count,
        };
    }

    private static async Task<OcrProbeEngine> ProbeEngineAsync(
        ProbeRun run,
        string engineName,
        IOcrEngine engine,
        CapturedImage image,
        IReadOnlyList<(string Name, OcrRequest Request)> planned,
        ScanContextDetector detector,
        SupplementalOcrSignalDetector supplemental,
        Action<OcrProbePass> onPass,
        CancellationToken cancellationToken)
    {
        var passes = new List<OcrProbePass>(planned.Count);
        var attempted = 0;
        string? providerFailure = null;
        foreach (var (name, request) in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (run.StopDiagnostic is not null || providerFailure is not null)
            {
                break;
            }

            attempted++;
            try
            {
                var pass = await ExecuteAsync(engine, image, request, name, detector, supplemental, run.Token)
                    .ConfigureAwait(false);
                passes.Add(pass);
                onPass(pass);
                if (string.Equals(pass.DiagnosticCode, MemoryExhaustedDiagnostic, StringComparison.Ordinal))
                {
                    // The next pass would ask for the same memory again.
                    run.StopDiagnostic = MemoryExhaustedDiagnostic;
                }
                else
                {
                    // Only this provider stops; the next one has its own native state. Every
                    // later pass used to ask the failed provider again and record the same failure.
                    providerFailure = ProviderFailure(engine, pass);
                }
            }
            catch (OperationCanceledException) when (run.IsExpired)
            {
                run.StopDiagnostic = DeadlineDiagnostic;
            }
        }

        // Settled before availability is read. A pass that timed out, or that the run deadline
        // cancelled, leaves its native read running, and a failure that read reports later retires
        // the provider. Availability read at once was read before that failure landed, and a report
        // written from it said available with no diagnostic for as long as it was kept. The wait is
        // bounded on its own: the run deadline may be what abandoned the work.
        var settled = engine is not TesseractOcrEngine tesseract ||
                      await tesseract.WaitForSettledAsync(run.SettleTimeout, cancellationToken).ConfigureAwait(false);
        var availability = (engine as IOcrEngineStatus)?.Availability;
        return new(
            availability?.Provider ?? engineName,
            availability?.IsAvailable ?? true,
            availability is { IsAvailable: false } ? availability.Reason : null,
            passes)
        {
            PlannedPassCount = planned.Count,
            AttemptedPassCount = attempted,
            CompletedPassCount = passes.Count(pass => pass.Status is "complete" or "empty"),
            // What stopped the passes first; otherwise a failure that landed after the last one;
            // otherwise that the provider's final state could not be waited for.
            DiagnosticCode = providerFailure ??
                             (availability is { IsAvailable: false } ? ProviderFailedDiagnostic : null) ??
                             (settled ? null : SettleTimeoutDiagnostic),
        };
    }

    /// <summary>The failure that ends a provider's passes, or null while it is still answering.</summary>
    private static string? ProviderFailure(IOcrEngine engine, OcrProbePass pass)
    {
        if (pass.DiagnosticCode is ProviderFailedDiagnostic or ProviderUnavailableDiagnostic)
        {
            return pass.DiagnosticCode;
        }

        // A provider can mark itself unavailable from a late failure of work an earlier pass
        // abandoned, while the pass that just finished reported only its own timeout.
        return engine is IOcrEngineStatus { Availability.IsAvailable: false } ? ProviderFailedDiagnostic : null;
    }

    private static OcrProbeEngine Unavailable(OcrEngineAvailability availability, int planned) => new(
        availability.Provider,
        false,
        availability.Reason,
        [])
    {
        PlannedPassCount = planned,
    };

    private static async Task<OcrProbePass> ExecuteAsync(
        IOcrEngine engine,
        CapturedImage image,
        OcrRequest request,
        string name,
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
                execution.PlannedTileCount,
                execution.AttemptedTileCount,
                execution.CompletedTileCount,
                execution.SourcePixelCount,
                execution.EstimatedPeakBytes,
                execution.Duration,
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
                    tile.DiagnosticCode)).ToArray()) with
                {
                    TruncatedLineCount = execution.TruncatedLineCount,
                };
        }
#endif
        if (engine is TesseractOcrEngine tesseract)
        {
            var execution = await tesseract
                .RecognizeDetailedAsync(image, request, cancellationToken)
                .ConfigureAwait(false);
            // Tesseract reads the region as one tile. It is listed only once native work
            // actually started, so a rejected or gate-timed-out pass shows no tile that ran.
            var tiles = execution.AttemptedTileCount == 0
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
                execution.PlannedTileCount,
                execution.AttemptedTileCount,
                execution.CompletedTileCount,
                execution.SourcePixelCount,
                execution.EstimatedPeakBytes,
                execution.Duration,
                detector,
                supplementalDetector,
                tiles) with
                {
                    TruncatedLineCount = execution.TruncatedLineCount,
                };
        }

        var result = await engine.RecognizeAsync(image, request, cancellationToken).ConfigureAwait(false);
        var region = request.Region ?? new PixelRect(0, 0, image.Width, image.Height);
        var scale = request.Preparation.SafeScale;
        var status = !result.IsAvailable
            ? "unavailable"
            : result.DiagnosticCode is not (null or "ocr_no_text" or "ocr_region_empty")
                ? "partial"
                : result.Lines.Count == 0 ? "empty" : "complete";
        return Pass(
            name,
            result,
            status,
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
            status is "complete" or "empty" ? 1 : 0,
            checked((long)image.Width * image.Height),
            image.Pixels.Length,
            result.Duration,
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
        int plannedTileCount,
        int attemptedTileCount,
        int completedTileCount,
        long sourcePixelCount,
        long estimatedPeakBytes,
        TimeSpan duration,
        ScanContextDetector detector,
        SupplementalOcrSignalDetector supplementalDetector,
        IReadOnlyList<OcrProbeTile> tiles)
    {
        var context = detector.Detect(image, result);
        var supplemental = supplementalDetector.Detect(result);
        var lines = result.Lines.Select(line => new OcrProbeLine(
            line.Text,
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
            plannedTileCount,
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

    internal static Task WriteReportAsync(
        string outputPath,
        OcrProbeReport report,
        CancellationToken cancellationToken)
    {
        var json = Encoding.UTF8.GetBytes(SerializeReport(report) + Environment.NewLine);
        return WriteAtomicallyAsync(
            outputPath,
            (stream, token) => stream.WriteAsync(json, token).AsTask(),
            cancellationToken);
    }

    /// <summary>Writes a whole file at the destination, or leaves the destination as it was.</summary>
    /// <remarks>
    /// The report used to be written in place, so Ctrl+C partway through left a truncated JSON
    /// document where the report belonged, which reads as a report until something parses it, and
    /// the probe said no report was written. It is now written beside the destination and moved
    /// over it only once complete. Cancellation is honoured up to the move and not after it, and the
    /// partial file is removed either way.
    /// </remarks>
    internal static async Task WriteAtomicallyAsync(
        string destination,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(destination);
        var temporary = $"{target}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             FileOptions.Asynchronous))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Removing the partial file is best effort; the destination is already correct.
            }
        }
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

    private static void DescribeFramePass(TextWriter log, OcrProbePass pass, int sampleLines)
    {
        var megapixels = pass.PreparedWidth / 1000d * pass.PreparedHeight / 1000d;
        log.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{pass.Name}: {pass.DetectedContext ?? "Unknown"} {FormatScore(pass.ContextScore)} · " +
            $"{pass.LineCount} lines · {pass.PreparedWidth}x{pass.PreparedHeight} " +
            $"({megapixels:F1} MP) · {pass.CompletedTileCount}/{pass.PlannedTileCount} tile(s) · " +
            $"{pass.DurationMilliseconds / 1000:F1}s · {pass.Status}"));
        log.WriteLine("  " + Sample(pass, sampleLines));
        log.WriteLine();
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

    /// <summary>One run's deadline, linked to the caller, and the reason it stopped early if it did.</summary>
    private sealed class ProbeRun : IDisposable
    {
        private readonly CancellationTokenSource _timer;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationToken _caller;

        public ProbeRun(OcrProbeLimits limits, CancellationToken caller)
        {
            ArgumentNullException.ThrowIfNull(limits);
            ArgumentNullException.ThrowIfNull(limits.TimeProvider, nameof(limits));
            if (limits.RunTimeout <= TimeSpan.Zero ||
                limits.RunTimeout > TimeSpan.FromDays(1) ||
                limits.MaximumCells <= 0 ||
                limits.SettleTimeout < TimeSpan.Zero ||
                limits.SettleTimeout > TimeSpan.FromDays(1))
            {
                throw new ArgumentOutOfRangeException(nameof(limits), "OCR probe limits must be positive and bounded.");
            }

            caller.ThrowIfCancellationRequested();
            _caller = caller;
            SettleTimeout = limits.SettleTimeout;
            _timer = new CancellationTokenSource(limits.RunTimeout, limits.TimeProvider);
            _deadline = CancellationTokenSource.CreateLinkedTokenSource(caller, _timer.Token);
        }

        public CancellationToken Token => _deadline.Token;

        public TimeSpan SettleTimeout { get; }

        public bool IsExpired => _timer.IsCancellationRequested && !_caller.IsCancellationRequested;

        public string? StopDiagnostic { get; set; }

        public void Dispose()
        {
            _deadline.Dispose();
            _timer.Dispose();
        }
    }
}
