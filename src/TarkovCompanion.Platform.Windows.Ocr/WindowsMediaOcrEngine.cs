using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;
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
///
/// Requests are serialized. A caller-visible timeout or cancellation can return before the
/// native operation observes it, and a second request started in that interval used to run
/// beside the abandoned one and double the memory the budget had promised. The gate is now held
/// until the abandoned native work really settles, and that settle path also observes its
/// late result so a failure cannot go unobserved.
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

    // A tile exists twice while it is handed over: in the managed BGRA staging buffer and in the
    // SoftwareBitmap copied from it. The first estimate counted one of them.
    private const int TileCopies = 2;
    private const int BytesPerStagedPixel = 4;

    // Pixels copied between cancellation checks. A row is at most one tile edge.
    private const int CancellationCheckPixels = 1 << 16;

    // E_OUTOFMEMORY, which is also OutOfMemoryException's own HRESULT. WinRT reports exhaustion
    // while creating a SoftwareBitmap as a COMException carrying it.
    private const int OutOfMemoryHResult = unchecked((int)0x8007000E);

    private readonly SemaphoreSlim _gate = new(1, 1);
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
        var plan = new FramePlan(image, region, checked((long)image.Width * image.Height), image.Pixels.Length);
        if (_recognizer is null)
        {
            return plan.Create(WindowsOcrExecutionStatus.Unavailable, "ocr_provider_unavailable");
        }

        if (plan.SourcePixels > _options.MaximumSourcePixels || plan.SourceBytes > _options.MaximumInputBytes)
        {
            return plan.Create(WindowsOcrExecutionStatus.Rejected, "ocr_input_limit_exceeded");
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            // Nothing was asked of Windows. Available and empty, and said so, rather than a
            // complete read that happened to find no text.
            return plan.Create(WindowsOcrExecutionStatus.Empty, "ocr_region_empty");
        }

        var configuredLimit = _options.MaximumTileDimension ?? CompatibilityMaximumTileDimension;
        var nativeLimit = Math.Min(configuredLimit, (int)WindowsOcr.OcrEngine.MaxImageDimension);
        var tiles = PlanTiles(region, nativeLimit, _options.TileOverlap);
        plan = plan with { Tiles = tiles };
        if (tiles.Count > _options.MaximumTiles)
        {
            return plan.Create(WindowsOcrExecutionStatus.Rejected, "ocr_tile_limit_exceeded");
        }

        var largestTilePixels = tiles.Max(tile => (long)tile.Width * tile.Height);
        plan = plan with { EstimatedPeakBytes = EstimatePeakBytes(plan.SourceBytes, largestTilePixels) };
        if (plan.EstimatedPeakBytes > _options.MaximumEstimatedPeakBytes)
        {
            return plan.Create(WindowsOcrExecutionStatus.Rejected, "ocr_memory_limit_exceeded");
        }

        var frameWatch = Stopwatch.StartNew();
        if (!await _gate.WaitAsync(_options.FrameTimeout, cancellationToken).ConfigureAwait(false))
        {
            // Another request, or native work one of them had to abandon, still owns Windows OCR.
            return plan.Create(WindowsOcrExecutionStatus.TimedOut, "ocr_frame_timeout", frameWatch.Elapsed);
        }

        return await RecognizeTilesAsync(plan, largestTilePixels, frameWatch, cancellationToken).ConfigureAwait(false);
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

    internal static long EstimatePeakBytes(long sourceBytes, long largestTilePixels) =>
        checked(sourceBytes + (largestTilePixels * BytesPerStagedPixel * TileCopies));

    /// <summary>
    /// Runs the planned tiles while holding the provider gate, and hands the gate to the native
    /// work instead of releasing it whenever that work outlives this request.
    /// </summary>
    private async Task<WindowsOcrExecution> RecognizeTilesAsync(
        FramePlan plan,
        long largestTilePixels,
        Stopwatch frameWatch,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? frameCancellation = null;
        byte[]? staging = null;
        Task? unsettled = null;
        var tiles = plan.Tiles;
        var lines = new FrameLines(_options.MaximumLines, _options.MaximumLineTextLength);
        var tileLines = new List<IReadOnlyList<OcrLine>>(tiles.Count);
        var tileReports = new List<WindowsOcrTileExecution>(tiles.Count);
        var attempted = 0;
        var completed = 0;
        var failed = 0;
        var timedOut = false;
        var memoryExhausted = false;
        try
        {
            frameCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var remainingAtStart = _options.FrameTimeout - frameWatch.Elapsed;
            if (remainingAtStart > TimeSpan.Zero)
            {
                frameCancellation.CancelAfter(remainingAtStart);
            }

            try
            {
                // One staging buffer for the request, sized for the largest tile and cleared
                // before the gate is released, so managed tile copies neither pile up waiting for
                // a collection nor outlive the request.
                staging = new byte[checked(largestTilePixels * BytesPerStagedPixel)];
            }
            catch (OutOfMemoryException)
            {
                memoryExhausted = true;
            }

            for (var ordinal = 0; staging is not null && ordinal < tiles.Count; ordinal++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = _options.FrameTimeout - frameWatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    // Not attempted, so not reported as a tile that ran.
                    timedOut = true;
                    break;
                }

                var tile = tiles[ordinal];
                var tileWatch = Stopwatch.StartNew();
                var tileTask = RecognizeTileAsync(plan.Image, tile, staging, frameCancellation.Token);
                attempted++;
                try
                {
                    var nativeLines = await tileTask
                        .WaitAsync(remaining, cancellationToken)
                        .ConfigureAwait(false);
                    tileWatch.Stop();
                    completed++;
                    tileLines.Add(lines.Accept(tile, nativeLines));
                    tileReports.Add(TileReport(ordinal, tile, tileWatch.Elapsed, WindowsOcrExecutionStatus.Complete, null));
                    if (lines.LimitReached)
                    {
                        break;
                    }
                }
                catch (TimeoutException)
                {
                    tileWatch.Stop();
                    unsettled = tileTask;
                    frameCancellation.Cancel();
                    timedOut = true;
                    tileReports.Add(TileReport(ordinal, tile, tileWatch.Elapsed, WindowsOcrExecutionStatus.TimedOut, "ocr_frame_timeout"));
                    break;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    tileWatch.Stop();
                    unsettled = tileTask;
                    timedOut = true;
                    tileReports.Add(TileReport(ordinal, tile, tileWatch.Elapsed, WindowsOcrExecutionStatus.TimedOut, "ocr_frame_timeout"));
                    break;
                }
                catch (OperationCanceledException)
                {
                    // The caller's cancellation still throws, but the native read it abandoned
                    // keeps the gate until it settles; see the finally below.
                    unsettled = tileTask;
                    throw;
                }
                catch (Exception exception) when (IsOutOfMemory(exception))
                {
                    // Every later tile would allocate the same buffers again. Stop here.
                    tileWatch.Stop();
                    Observe(tileTask);
                    failed++;
                    memoryExhausted = true;
                    tileReports.Add(TileReport(ordinal, tile, tileWatch.Elapsed, WindowsOcrExecutionStatus.Failed, "ocr_memory_exhausted"));
                    break;
                }
                catch (Exception)
                {
                    tileWatch.Stop();
                    Observe(tileTask);
                    failed++;
                    tileReports.Add(TileReport(ordinal, tile, tileWatch.Elapsed, WindowsOcrExecutionStatus.Failed, "ocr_tile_failed"));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            frameWatch.Stop();
            var merge = OcrLineDeduplicator.Merge(tileLines.ToArray());
            var (status, diagnostic) = Classify(
                completed,
                failed,
                timedOut,
                memoryExhausted,
                lines,
                merge);
            var available = status is WindowsOcrExecutionStatus.Complete
                or WindowsOcrExecutionStatus.Empty
                or WindowsOcrExecutionStatus.Partial;
            var result = new OcrResult(merge.Lines, frameWatch.Elapsed, ProviderName, available, diagnostic);
            return new WindowsOcrExecution(
                result,
                plan.Region,
                plan.Image.Width,
                plan.Image.Height,
                1,
                tiles.Count,
                attempted,
                completed,
                plan.SourcePixels,
                plan.EstimatedPeakBytes,
                frameWatch.Elapsed,
                ProviderName,
                status,
                diagnostic,
                tileReports)
            {
                ProviderLineCount = lines.Accepted,
                TruncatedLineCount = lines.Truncated,
            };
        }
        finally
        {
            if (unsettled is { IsCompleted: false })
            {
                _ = ReleaseWhenSettledAsync(unsettled, frameCancellation, staging);
            }
            else
            {
                Observe(unsettled);
                Release(frameCancellation, staging);
            }
        }
    }

    private static (WindowsOcrExecutionStatus Status, string? Diagnostic) Classify(
        int completed,
        int failed,
        bool timedOut,
        bool memoryExhausted,
        FrameLines lines,
        OcrLineMerge merge)
    {
        if (timedOut)
        {
            return completed > 0
                ? (WindowsOcrExecutionStatus.Partial, "ocr_frame_timeout_partial")
                : (WindowsOcrExecutionStatus.TimedOut, "ocr_frame_timeout");
        }

        if (memoryExhausted)
        {
            return completed > 0
                ? (WindowsOcrExecutionStatus.Partial, "ocr_memory_exhausted")
                : (WindowsOcrExecutionStatus.Failed, "ocr_memory_exhausted");
        }

        if (failed > 0)
        {
            return completed > 0
                ? (WindowsOcrExecutionStatus.Partial, "ocr_partial_tiles")
                : (WindowsOcrExecutionStatus.Failed, "ocr_provider_failed");
        }

        if (lines.LimitReached)
        {
            return (WindowsOcrExecutionStatus.Partial, "ocr_line_limit_exceeded");
        }

        if (lines.Truncated > 0)
        {
            return (WindowsOcrExecutionStatus.Partial, "ocr_line_text_truncated");
        }

        if (!merge.IsExhaustive)
        {
            return (WindowsOcrExecutionStatus.Partial, "ocr_dedupe_budget_exhausted");
        }

        if (merge.Lines.Count == 0)
        {
            return (WindowsOcrExecutionStatus.Empty, "ocr_no_text");
        }

        return (WindowsOcrExecutionStatus.Complete, null);
    }

    private async Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeTileAsync(
        CapturedImage image,
        PixelRect tile,
        byte[] staging,
        CancellationToken cancellationToken)
    {
        // This method owns the bitmap until the native operation really finishes. A strict
        // caller-side timeout may return before a misbehaving component observes cancellation;
        // disposing its input in that interval would turn a bounded timeout into a use-after-
        // dispose race.
        using var bitmap = ToSoftwareBitmap(image, tile, staging, cancellationToken);
        return await _recognizer!
            .RecognizeAsync(bitmap, _options.MaximumLines, cancellationToken)
            .ConfigureAwait(false);
    }

    private static SoftwareBitmap ToSoftwareBitmap(
        CapturedImage image,
        PixelRect region,
        byte[] staging,
        CancellationToken cancellationToken)
    {
        var length = checked(region.Width * region.Height * BytesPerStagedPixel);
        var source = image.Pixels.Span;
        var bytesPerPixel = BytesPerPixel(image.Format);
        var redOffset = image.Format == PixelFormat.Rgba8888 ? 0 : 2;
        var blueOffset = image.Format == PixelFormat.Rgba8888 ? 2 : 0;
        var sinceCheck = CancellationCheckPixels;

        for (var y = 0; y < region.Height; y++)
        {
            if (sinceCheck >= CancellationCheckPixels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sinceCheck = 0;
            }

            var sourceRow = checked(((region.Y + y) * image.Stride) + (region.X * bytesPerPixel));
            var targetRow = y * region.Width * BytesPerStagedPixel;
            for (var x = 0; x < region.Width; x++)
            {
                var from = sourceRow + (x * bytesPerPixel);
                var to = targetRow + (x * BytesPerStagedPixel);
                if (image.Format == PixelFormat.Gray8)
                {
                    var grey = source[from];
                    staging[to] = grey;
                    staging[to + 1] = grey;
                    staging[to + 2] = grey;
                }
                else
                {
                    staging[to] = source[from + blueOffset];
                    staging[to + 1] = source[from + 1];
                    staging[to + 2] = source[from + redOffset];
                }

                staging[to + 3] = 255;
            }

            sinceCheck += region.Width;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var bitmap = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            region.Width,
            region.Height,
            BitmapAlphaMode.Premultiplied);
        try
        {
            bitmap.CopyFromBuffer(staging.AsBuffer(0, length));
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private async Task ReleaseWhenSettledAsync(
        Task nativeWork,
        CancellationTokenSource? frameCancellation,
        byte[]? staging)
    {
        try
        {
            await nativeWork.ConfigureAwait(false);
        }
        catch
        {
            // The request that abandoned this work already reported its timeout, failure or
            // cancellation. Awaiting here observes a late failure; it is not reported twice.
        }
        finally
        {
            Release(frameCancellation, staging);
        }
    }

    private void Release(CancellationTokenSource? frameCancellation, byte[]? staging)
    {
        if (staging is not null)
        {
            Array.Clear(staging);
        }

        frameCancellation?.Dispose();
        _gate.Release();
    }

    private static void Observe(Task? task)
    {
        if (task is { IsFaulted: true })
        {
            _ = task.Exception;
        }
    }

    private static bool IsOutOfMemory(Exception exception) =>
        exception is OutOfMemoryException || exception.HResult == OutOfMemoryHResult;

    private static WindowsOcrTileExecution TileReport(
        int ordinal,
        PixelRect tile,
        TimeSpan duration,
        WindowsOcrExecutionStatus status,
        string? diagnostic) => new(
            ordinal,
            tile,
            tile.Width,
            tile.Height,
            1,
            duration,
            status,
            diagnostic);

    private static PixelRect Translate(PixelRect tile, PixelRect bounds)
    {
        // Windows reports word boxes inside the bitmap it was given. Clamped anyway, so a
        // misbehaving component can neither move evidence outside its tile nor overflow the
        // translation into source coordinates.
        var left = Math.Clamp(bounds.X, 0, tile.Width);
        var top = Math.Clamp(bounds.Y, 0, tile.Height);
        var right = (int)Math.Clamp((long)bounds.X + bounds.Width, left, tile.Width);
        var bottom = (int)Math.Clamp((long)bounds.Y + bounds.Height, top, tile.Height);
        return new(tile.X + left, tile.Y + top, right - left, bottom - top);
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
            options.FrameTimeout > TimeSpan.FromDays(1) ||
            options.MaximumSourcePixels <= 0 ||
            options.MaximumInputBytes <= 0 ||
            options.MaximumEstimatedPeakBytes <= 0 ||
            options.MaximumTiles <= 0 ||
            options.MaximumLines <= 0 ||
            options.MaximumLineTextLength <= 1 ||
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

    /// <summary>What a request measured before any native work, so every exit reports the same facts.</summary>
    private sealed record FramePlan(CapturedImage Image, PixelRect Region, long SourcePixels, long SourceBytes)
    {
        public IReadOnlyList<PixelRect> Tiles { get; init; } = [];

        public long EstimatedPeakBytes { get; init; } = SourceBytes;

        public WindowsOcrExecution Create(
            WindowsOcrExecutionStatus status,
            string diagnostic,
            TimeSpan duration = default)
        {
            var available = status == WindowsOcrExecutionStatus.Empty;
            var result = new OcrResult([], duration, ProviderName, available, diagnostic);
            return new(
                result,
                Region,
                Image.Width,
                Image.Height,
                1,
                Tiles.Count,
                0,
                0,
                SourcePixels,
                EstimatedPeakBytes,
                duration,
                ProviderName,
                status,
                diagnostic,
                []);
        }
    }

    /// <summary>Lines accepted from every tile of one request, under its line and text ceilings.</summary>
    private sealed class FrameLines(int maximumLines, int maximumTextLength)
    {
        public int Accepted { get; private set; }

        public int Truncated { get; private set; }

        public bool LimitReached { get; private set; }

        public IReadOnlyList<OcrLine> Accept(PixelRect tile, IReadOnlyList<WindowsOcrNativeLine> nativeLines)
        {
            var lines = new List<OcrLine>(Math.Min(nativeLines.Count, maximumLines));
            foreach (var native in nativeLines)
            {
                if (string.IsNullOrWhiteSpace(native.Text))
                {
                    continue;
                }

                if (Accepted == maximumLines)
                {
                    LimitReached = true;
                    break;
                }

                var text = native.Text.Trim();
                if (text.Length > maximumTextLength)
                {
                    // Never leave half of a surrogate pair at the cut.
                    var length = char.IsHighSurrogate(text[maximumTextLength - 1])
                        ? maximumTextLength - 1
                        : maximumTextLength;
                    text = text[..length];
                    Truncated++;
                }

                Accepted++;
                lines.Add(new OcrLine(
                    text,
                    Translate(tile, native.Bounds),
                    // Windows.Media.Ocr publishes no confidence. Null is the v1 representation
                    // of an unscored reading; numeric zero means a provider actually scored zero.
                    null));
            }

            return lines;
        }
    }
}

internal sealed record WindowsOcrNativeLine(string Text, PixelRect Bounds);

internal interface IWindowsOcrRecognizer
{
    /// <summary>
    /// Reads one bitmap, returning at most one line past <paramref name="maximumLines"/> so the
    /// engine can tell a full page from a cut one.
    /// </summary>
    Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(
        SoftwareBitmap bitmap,
        int maximumLines,
        CancellationToken cancellationToken);
}

internal sealed class WindowsOcrRecognizer(WindowsOcr.OcrEngine engine) : IWindowsOcrRecognizer
{
    public async Task<IReadOnlyList<WindowsOcrNativeLine>> RecognizeAsync(
        SoftwareBitmap bitmap,
        int maximumLines,
        CancellationToken cancellationToken)
    {
        // The returned task completes only when Windows reports the operation finished or
        // cancelled, which is what lets the engine hold its gate until native work settles.
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
        var lines = new List<WindowsOcrNativeLine>();
        foreach (var line in result.Lines)
        {
            if (lines.Count > maximumLines)
            {
                break;
            }

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
