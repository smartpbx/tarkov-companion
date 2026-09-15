using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using PixelFormat = TarkovCompanion.Core.Domain.Recognition.PixelFormat;
// Aliased rather than imported. Windows.Media.Ocr has its own OcrEngine, OcrResult and
// OcrLine, and this file is the one place in the codebase where both sets are in scope; an
// unqualified OcrResult here would be ambiguous in one direction and quietly wrong in the
// other.
using WindowsOcr = Windows.Media.Ocr;

namespace TarkovCompanion.Platform.Windows.Ocr;

/// <summary>
/// Reads caller-supplied pixels through the OCR engine built into Windows.
/// </summary>
/// <remarks>
/// Windows refuses a bitmap whose larger side exceeds its native limit. Reducing a 4K or
/// ultrawide frame to fit used to discard source pixels before OCR and made the smallest text
/// the first thing to disappear. This implementation keeps native resolution and divides only
/// oversized requested regions into deterministic overlapping tiles. Tile bitmaps and native
/// results live only for the request; neither pixels nor paths are persisted.
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsMediaOcrEngine : IOcrEngine, IOcrEngineStatus
{
    public const string ProviderName = "windows-media-ocr";

    // OcrEngine.MaxImageDimension is not stable across Windows runner images. A hosted image
    // first exposed this by advertising a limit above 4K, which silently bypassed the tile path
    // that 4K diagnostics and seam handling rely on. Keep the compatibility edge fixed while
    // still honoring a smaller limit reported by the installed Windows component.
    private const int CompatibilityMaximumTileDimension = 2_600;

    private readonly IWindowsOcrRecognizer? _recognizer;
    private readonly WindowsMediaOcrOptions _options;

    public WindowsMediaOcrEngine()
    {
        _options = ValidateOptions(new WindowsMediaOcrOptions());
        try
        {
            // The user's own languages first, because somebody running a French Windows has a
            // French recogniser installed and an English one very likely not. English is the
            // fallback because the game's interface is what is being read, not the system's.
            var engine = WindowsOcr.OcrEngine.TryCreateFromUserProfileLanguages()
                ?? WindowsOcr.OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            _recognizer = engine is null ? null : new WindowsOcrRecognizer(engine);
            Availability = engine is null
                ? new(false, ProviderName, "Windows has no OCR language pack installed for this profile.")
                : new(true, ProviderName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Creating the engine touches a system component that a stripped Windows install,
            // a Server SKU or a policy can remove. Said plainly rather than thrown, because the
            // whole point of this class is to be the one that might not be there.
            _recognizer = null;
            Availability = new(false, ProviderName, Summarize(exception));
        }
    }

    internal WindowsMediaOcrEngine(
        IWindowsOcrRecognizer? recognizer,
        WindowsMediaOcrOptions? options = null,
        string? unavailableReason = null)
    {
        _recognizer = recognizer;
        _options = ValidateOptions(options ?? new WindowsMediaOcrOptions());
        Availability = recognizer is null
            ? new(false, ProviderName, unavailableReason ?? "Windows OCR is unavailable.")
            : new(true, ProviderName);
    }

    public OcrEngineAvailability Availability { get; }

    public async Task<OcrResult> RecognizeAsync(
        CapturedImage image,
        OcrRequest request,
        CancellationToken cancellationToken) =>
        (await RecognizeDetailedAsync(image, request, cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>
    /// Reads one bounded source region and returns the exact native work performed.
    /// </summary>
    public async Task<WindowsOcrExecution> RecognizeDetailedAsync(
        CapturedImage image,
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(request);
        ValidateImage(image);
        cancellationToken.ThrowIfCancellationRequested();

        var region = Clamp(image, request.Region);
        var sourcePixels = checked((long)image.Width * image.Height);
        var sourceBytes = image.Pixels.Length;
        if (_recognizer is null)
        {
            return EmptyExecution(
                image,
                region,
                sourcePixels,
                sourceBytes,
                WindowsOcrExecutionStatus.Unavailable,
                "ocr_provider_unavailable");
        }

        if (sourcePixels > _options.MaximumSourcePixels || sourceBytes > _options.MaximumInputBytes)
        {
            return EmptyExecution(
                image,
                region,
                sourcePixels,
                sourceBytes,
                WindowsOcrExecutionStatus.Rejected,
                "ocr_input_limit_exceeded");
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            return EmptyExecution(
                image,
                region,
                sourcePixels,
                sourceBytes,
                WindowsOcrExecutionStatus.Complete,
                null);
        }

        var configuredLimit = _options.MaximumTileDimension ?? CompatibilityMaximumTileDimension;
        var nativeLimit = Math.Min(configuredLimit, (int)WindowsOcr.OcrEngine.MaxImageDimension);
        var tiles = PlanTiles(region, nativeLimit, _options.TileOverlap);
        if (tiles.Count > _options.MaximumTiles)
        {
            return EmptyExecution(
                image,
                region,
                sourcePixels,
                sourceBytes,
                WindowsOcrExecutionStatus.Rejected,
                "ocr_tile_limit_exceeded",
                tileCount: tiles.Count);
        }

        var largestTilePixels = tiles.Max(tile => checked((long)tile.Width * tile.Height));
        var estimatedPeakBytes = checked(sourceBytes + (largestTilePixels * 4));
        if (estimatedPeakBytes > _options.MaximumEstimatedPeakBytes)
        {
            return EmptyExecution(
                image,
                region,
                sourcePixels,
                estimatedPeakBytes,
                WindowsOcrExecutionStatus.Rejected,
                "ocr_memory_limit_exceeded",
                tileCount: tiles.Count);
        }

        var frameWatch = Stopwatch.StartNew();
        using var frameCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        frameCancellation.CancelAfter(_options.FrameTimeout);
        var lines = new List<TiledOcrLine>();
        var tileReports = new List<WindowsOcrTileExecution>(tiles.Count);
        var completed = 0;
        var failed = 0;
        var timedOut = false;

        for (var ordinal = 0; ordinal < tiles.Count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tile = tiles[ordinal];
            var tileWatch = Stopwatch.StartNew();
            var remaining = _options.FrameTimeout - frameWatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                timedOut = true;
                tileReports.Add(new(
                    ordinal,
                    tile,
                    tile.Width,
                    tile.Height,
                    1,
                    TimeSpan.Zero,
                    WindowsOcrExecutionStatus.TimedOut,
                    "ocr_frame_timeout"));
                break;
            }

            Task<IReadOnlyList<OcrLine>>? tileTask = null;
            try
            {
                tileTask = RecognizeTileAsync(image, tile, frameCancellation.Token);
                var nativeLines = await tileTask
                    .WaitAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);
                tileWatch.Stop();
                completed++;
                tileReports.Add(new(
                    ordinal,
                    tile,
                    tile.Width,
                    tile.Height,
                    1,
                    tileWatch.Elapsed,
                    WindowsOcrExecutionStatus.Complete,
                    null));
                lines.AddRange(nativeLines.Select(line => new TiledOcrLine(line, ordinal)));
            }
            catch (TimeoutException)
            {
                frameCancellation.Cancel();
                ObserveCompletion(tileTask);
                tileWatch.Stop();
                timedOut = true;
                tileReports.Add(new(
                    ordinal,
                    tile,
                    tile.Width,
                    tile.Height,
                    1,
                    tileWatch.Elapsed,
                    WindowsOcrExecutionStatus.TimedOut,
                    "ocr_frame_timeout"));
                break;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                ObserveCompletion(tileTask);
                tileWatch.Stop();
                timedOut = true;
                tileReports.Add(new(
                    ordinal,
                    tile,
                    tile.Width,
                    tile.Height,
                    1,
                    tileWatch.Elapsed,
                    WindowsOcrExecutionStatus.TimedOut,
                    "ocr_frame_timeout"));
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                tileWatch.Stop();
                failed++;
                tileReports.Add(new(
                    ordinal,
                    tile,
                    tile.Width,
                    tile.Height,
                    1,
                    tileWatch.Elapsed,
                    WindowsOcrExecutionStatus.Failed,
                    "ocr_tile_failed"));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        frameWatch.Stop();
        var deduplicated = Deduplicate(lines);
        var status = timedOut
            ? completed > 0 ? WindowsOcrExecutionStatus.Partial : WindowsOcrExecutionStatus.TimedOut
            : failed > 0
                ? completed > 0 ? WindowsOcrExecutionStatus.Partial : WindowsOcrExecutionStatus.Failed
                : WindowsOcrExecutionStatus.Complete;
        var diagnostic = status switch
        {
            WindowsOcrExecutionStatus.Partial when timedOut => "ocr_frame_timeout_partial",
            WindowsOcrExecutionStatus.Partial => "ocr_partial_tiles",
            WindowsOcrExecutionStatus.TimedOut => "ocr_frame_timeout",
            WindowsOcrExecutionStatus.Failed => "ocr_provider_failed",
            _ => null,
        };
        var available = status is WindowsOcrExecutionStatus.Complete or WindowsOcrExecutionStatus.Partial;
        var result = new OcrResult(deduplicated, frameWatch.Elapsed, ProviderName, available, diagnostic);
        return new(
            result,
            region,
            image.Width,
            image.Height,
            1,
            tiles.Count,
            tileReports.Count,
            completed,
            sourcePixels,
            estimatedPeakBytes,
            frameWatch.Elapsed,
            ProviderName,
            status,
            diagnostic,
            tileReports);
    }

    /// <summary>
    /// Plans deterministic native-resolution tiles with no more than the requested overlap.
    /// A narrow last tile is safe: all but its new far-edge pixels were already included in
    /// the preceding tile, while reducing the whole frame would discard detail everywhere.
    /// </summary>
    internal static IReadOnlyList<PixelRect> PlanTiles(PixelRect region, int maximumDimension, int overlap)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (maximumDimension <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        }

        if (overlap < 0 || overlap >= maximumDimension)
        {
            throw new ArgumentOutOfRangeException(nameof(overlap));
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            return [];
        }

        var x = AxisStarts(region.X, region.Width, maximumDimension, overlap);
        var y = AxisStarts(region.Y, region.Height, maximumDimension, overlap);
        return y.SelectMany(top => x.Select(left => new PixelRect(
                left,
                top,
                Math.Min(maximumDimension, region.X + region.Width - left),
                Math.Min(maximumDimension, region.Y + region.Height - top))))
            .ToArray();
    }

    private async Task<IReadOnlyList<OcrLine>> RecognizeTileAsync(
        CapturedImage image,
        PixelRect tile,
        CancellationToken cancellationToken)
    {
        // This method owns the bitmap until the native operation really finishes. A strict
        // caller-side timeout may return before a misbehaving component observes cancellation;
        // disposing its input in that interval would turn a bounded timeout into a use-after-
        // dispose race.
        using var bitmap = ToSoftwareBitmap(image, tile, cancellationToken);
        var nativeLines = await _recognizer!.RecognizeAsync(bitmap, cancellationToken).ConfigureAwait(false);
        return nativeLines
            .Where(line => !string.IsNullOrWhiteSpace(line.Text))
            .Select(line => new OcrLine(
                line.Text.Trim(),
                new PixelRect(
                    tile.X + line.Bounds.X,
                    tile.Y + line.Bounds.Y,
                    line.Bounds.Width,
                    line.Bounds.Height),
                // Windows.Media.Ocr publishes no confidence. Null is the v1 representation of
                // an unscored reading; numeric zero means a provider actually scored zero.
                null))
            .ToArray();
    }

    private static SoftwareBitmap ToSoftwareBitmap(
        CapturedImage image,
        PixelRect region,
        CancellationToken cancellationToken)
    {
        var pixels = new byte[checked(region.Width * region.Height * 4)];
        var source = image.Pixels.Span;
        var bytesPerPixel = BytesPerPixel(image.Format);
        var redOffset = image.Format == PixelFormat.Rgba8888 ? 0 : 2;
        var blueOffset = image.Format == PixelFormat.Rgba8888 ? 2 : 0;

        for (var y = 0; y < region.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceRow = checked(((region.Y + y) * image.Stride) + (region.X * bytesPerPixel));
            var targetRow = y * region.Width * 4;
            for (var x = 0; x < region.Width; x++)
            {
                var from = sourceRow + (x * bytesPerPixel);
                var to = targetRow + (x * 4);
                if (image.Format == PixelFormat.Gray8)
                {
                    var grey = source[from];
                    pixels[to] = grey;
                    pixels[to + 1] = grey;
                    pixels[to + 2] = grey;
                }
                else
                {
                    pixels[to] = source[from + blueOffset];
                    pixels[to + 1] = source[from + 1];
                    pixels[to + 2] = source[from + redOffset];
                }

                pixels[to + 3] = 255;
            }
        }

        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            region.Width,
            region.Height,
            BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        return bitmap;
    }

    private static IReadOnlyList<OcrLine> Deduplicate(IReadOnlyList<TiledOcrLine> tiled)
    {
        var selected = new List<TiledOcrLine>();
        foreach (var candidate in tiled
                     .OrderBy(line => line.Line.Bounds.Y)
                     .ThenBy(line => line.Line.Bounds.X)
                     .ThenBy(line => Normalize(line.Line.Text), StringComparer.Ordinal)
                     .ThenBy(line => line.TileOrdinal))
        {
            var duplicateIndex = selected.FindIndex(existing => IsDuplicate(existing.Line, candidate.Line));
            if (duplicateIndex < 0)
            {
                selected.Add(candidate);
                continue;
            }

            if (IsBetter(candidate, selected[duplicateIndex]))
            {
                selected[duplicateIndex] = candidate;
            }
        }

        return selected
            .Select(line => line.Line)
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ThenBy(line => line.Text, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsDuplicate(OcrLine left, OcrLine right)
    {
        var leftText = Normalize(left.Text);
        var rightText = Normalize(right.Text);
        if (leftText.Length == 0 || rightText.Length == 0)
        {
            return false;
        }

        var textMatches = string.Equals(leftText, rightText, StringComparison.Ordinal) ||
            (Math.Min(leftText.Length, rightText.Length) * 10 >= Math.Max(leftText.Length, rightText.Length) * 6 &&
             (leftText.Contains(rightText, StringComparison.Ordinal) ||
              rightText.Contains(leftText, StringComparison.Ordinal)));
        if (!textMatches)
        {
            return false;
        }

        var intersectionWidth = Math.Max(
            0,
            Math.Min(left.Bounds.X + left.Bounds.Width, right.Bounds.X + right.Bounds.Width) -
            Math.Max(left.Bounds.X, right.Bounds.X));
        var intersectionHeight = Math.Max(
            0,
            Math.Min(left.Bounds.Y + left.Bounds.Height, right.Bounds.Y + right.Bounds.Height) -
            Math.Max(left.Bounds.Y, right.Bounds.Y));
        var intersection = (long)intersectionWidth * intersectionHeight;
        var smaller = Math.Min(
            (long)left.Bounds.Width * left.Bounds.Height,
            (long)right.Bounds.Width * right.Bounds.Height);
        return smaller > 0 && intersection * 2 >= smaller;
    }

    private static bool IsBetter(TiledOcrLine candidate, TiledOcrLine current)
    {
        var candidateText = Normalize(candidate.Line.Text);
        var currentText = Normalize(current.Line.Text);
        if (candidateText.Length != currentText.Length)
        {
            return candidateText.Length > currentText.Length;
        }

        var candidateArea = (long)candidate.Line.Bounds.Width * candidate.Line.Bounds.Height;
        var currentArea = (long)current.Line.Bounds.Width * current.Line.Bounds.Height;
        return candidateArea != currentArea
            ? candidateArea > currentArea
            : candidate.TileOrdinal < current.TileOrdinal;
    }

    private static string Normalize(string text) => new(
        text.Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static void ObserveCompletion(Task? task)
    {
        if (task is null || task.IsCompleted)
        {
            return;
        }

        _ = ObserveAsync(task);

        static async Task ObserveAsync(Task pending)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch
            {
                // The request already reported the timeout. This observer exists only so a
                // native component that finishes later cannot leave an unobserved exception.
            }
        }
    }

    private static IReadOnlyList<int> AxisStarts(int origin, int length, int maximum, int overlap)
    {
        if (length <= maximum)
        {
            return [origin];
        }

        var step = maximum - overlap;
        var count = checked((int)Math.Ceiling((length - overlap) / (double)step));
        return Enumerable.Range(0, count)
            .Select(index => checked(origin + (index * step)))
            .ToArray();
    }

    private WindowsOcrExecution EmptyExecution(
        CapturedImage image,
        PixelRect region,
        long sourcePixels,
        long estimatedPeakBytes,
        WindowsOcrExecutionStatus status,
        string? diagnostic,
        int tileCount = 0)
    {
        var available = status == WindowsOcrExecutionStatus.Complete;
        var result = new OcrResult([], TimeSpan.Zero, ProviderName, available, diagnostic);
        return new(
            result,
            region,
            image.Width,
            image.Height,
            1,
            tileCount,
            0,
            0,
            sourcePixels,
            estimatedPeakBytes,
            TimeSpan.Zero,
            ProviderName,
            status,
            diagnostic,
            []);
    }

    private static PixelRect Clamp(CapturedImage image, PixelRect? region)
    {
        if (region is not { } requested)
        {
            return new(0, 0, image.Width, image.Height);
        }

        var x = Math.Clamp(requested.X, 0, image.Width);
        var y = Math.Clamp(requested.Y, 0, image.Height);
        var right = (int)Math.Clamp((long)requested.X + requested.Width, x, image.Width);
        var bottom = (int)Math.Clamp((long)requested.Y + requested.Height, y, image.Height);
        return new(
            x,
            y,
            right - x,
            bottom - y);
    }

    private static void ValidateImage(CapturedImage image)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentException("Captured image dimensions must be positive.", nameof(image));
        }

        var bytesPerPixel = BytesPerPixel(image.Format);
        if (image.Stride < checked(image.Width * bytesPerPixel) ||
            image.Pixels.Length < checked(image.Stride * image.Height))
        {
            throw new ArgumentException("Captured image buffer is smaller than its dimensions and stride.", nameof(image));
        }
    }

    private static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Bgra8888 or PixelFormat.Rgba8888 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static WindowsMediaOcrOptions ValidateOptions(WindowsMediaOcrOptions options)
    {
        if (options.FrameTimeout <= TimeSpan.Zero ||
            options.MaximumSourcePixels <= 0 ||
            options.MaximumInputBytes <= 0 ||
            options.MaximumEstimatedPeakBytes <= 0 ||
            options.MaximumTiles <= 0 ||
            options.TileOverlap < 0 ||
            options.MaximumTileDimension is <= 0 ||
            options.MaximumTileDimension > (int)WindowsOcr.OcrEngine.MaxImageDimension ||
            options.TileOverlap >= (options.MaximumTileDimension ?? (int)WindowsOcr.OcrEngine.MaxImageDimension))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Windows OCR limits must be positive and bounded.");
        }

        return options;
    }

    private static string Summarize(Exception exception) => exception switch
    {
        TypeLoadException or DllNotFoundException or EntryPointNotFoundException =>
            "This Windows install does not carry the OCR component.",
        UnauthorizedAccessException =>
            "Windows refused access to its OCR component.",
        _ => "Windows could not start its OCR engine: " + exception.Message,
    };

    private sealed record TiledOcrLine(OcrLine Line, int TileOrdinal);
}

internal sealed record WindowsOcrNativeLine(string Text, PixelRect Bounds);

internal interface IWindowsOcrRecognizer
{
    Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken);
}

internal sealed class WindowsOcrRecognizer(WindowsOcr.OcrEngine engine) : IWindowsOcrRecognizer
{
    public async Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken)
    {
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        var lines = new List<WindowsOcrNativeLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            double left = double.MaxValue, top = double.MaxValue, right = 0, bottom = 0;
            foreach (var word in line.Words)
            {
                left = Math.Min(left, word.BoundingRect.Left);
                top = Math.Min(top, word.BoundingRect.Top);
                right = Math.Max(right, word.BoundingRect.Right);
                bottom = Math.Max(bottom, word.BoundingRect.Bottom);
            }

            var bounds = left is double.MaxValue
                ? new PixelRect(0, 0, 0, 0)
                : new PixelRect(
                    (int)Math.Floor(left),
                    (int)Math.Floor(top),
                    Math.Max(0, (int)Math.Ceiling(right) - (int)Math.Floor(left)),
                    Math.Max(0, (int)Math.Ceiling(bottom) - (int)Math.Floor(top)));
            lines.Add(new(line.Text.Trim(), bounds));
        }

        return lines;
    }
}
