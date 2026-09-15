using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractImage = TesseractOCR.Pix.Image;

namespace TarkovCompanion.Infrastructure.Recognition;

public sealed record TesseractOcrOptions(
    string? TessdataPath = null,
    string Language = "eng",
    bool UseBundledEnglishData = true)
{
    public TimeSpan FrameTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public long MaximumSourcePixels { get; init; } = 40_000_000;

    public long MaximumInputBytes { get; init; } = 192L * 1024 * 1024;

    public long MaximumPreparedPixels { get; init; } = 40_000_000;

    public long MaximumEstimatedPeakBytes { get; init; } = 256L * 1024 * 1024;
}

/// <summary>
/// Offline Tesseract 5 OCR for the packaged Windows x64 application.
/// The provider consumes only the supplied in-memory pixels.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine, IOcrEngineStatus, IDisposable
{
    private const string ProviderName = "tesseract-5.5.1-wrapper-5.5.2";
    private const string BundledModelResource =
        "TarkovCompanion.Infrastructure.Recognition.Tessdata.eng.traineddata";
    private const string BundledModelSha256 =
        "7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly string _language;
    private readonly TesseractOcrOptions _options;
    private Engine? _engine;
    private Task? _nativeWork;
    private bool _disposed;

    public TesseractOcrEngine(TesseractOcrOptions? options = null)
    {
        options ??= new TesseractOcrOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Language);
        ValidateOptions(options);
        _options = options;
        _language = options.Language;
        Availability = Initialize(options);
    }

    public OcrEngineAvailability Availability { get; private set; }

    public async Task<OcrResult> RecognizeAsync(
        CapturedImage image,
        OcrRequest request,
        CancellationToken cancellationToken) =>
        (await RecognizeDetailedAsync(image, request, cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>
    /// Reads one region under a hard caller-visible deadline and reports the exact preparation
    /// dimensions. Tesseract's native call is not cooperatively cancellable, so a timed-out
    /// call keeps exclusive ownership of the provider until it really exits; another call can
    /// never overlap it.
    /// </summary>
    public async Task<TesseractOcrExecution> RecognizeDetailedAsync(
        CapturedImage image,
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CapturedImagePixels.Validate(image);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!Availability.IsAvailable || _engine is null)
        {
            return EmptyExecution(image, request, OcrExecutionStatus.Unavailable, "ocr_provider_unavailable");
        }

        if (!string.Equals(request.Language, _language, StringComparison.OrdinalIgnoreCase))
        {
            return EmptyExecution(image, request, OcrExecutionStatus.Unavailable, "ocr_language_unavailable");
        }

        var stopwatch = Stopwatch.StartNew();
        using var frameCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        frameCancellation.CancelAfter(_options.FrameTimeout);
        try
        {
            await _gate.WaitAsync(frameCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            return WithDuration(
                EmptyExecution(image, request, OcrExecutionStatus.TimedOut, "ocr_frame_timeout"),
                stopwatch.Elapsed);
        }

        var releaseGate = true;
        try
        {
            var region = ClampRegion(image, request.Region);
            var preparation = request.Preparation;
            var scale = preparation.SafeScale;
            var budgetDiagnostic = OcrExecutionBudget.Check(
                image,
                region,
                scale,
                _options.MaximumSourcePixels,
                _options.MaximumInputBytes,
                _options.MaximumPreparedPixels,
                _options.MaximumEstimatedPeakBytes,
                out var sourcePixels,
                out var preparedPixels,
                out var estimatedPeakBytes);
            if (budgetDiagnostic is not null)
            {
                return CreateExecution(
                    image,
                    region,
                    scale,
                    sourcePixels,
                    preparedPixels,
                    estimatedPeakBytes,
                    stopwatch.Elapsed,
                    [],
                    OcrExecutionStatus.Rejected,
                    budgetDiagnostic);
            }

            byte[] encoded;
            try
            {
                encoded = EncodePortableGraymap(image, region, preparation, frameCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                stopwatch.Stop();
                return CreateExecution(
                    image,
                    region,
                    scale,
                    sourcePixels,
                    preparedPixels,
                    estimatedPeakBytes,
                    stopwatch.Elapsed,
                    [],
                    OcrExecutionStatus.TimedOut,
                    "ocr_frame_timeout");
            }

            var engine = _engine;
            if (engine is null)
            {
                return CreateExecution(
                    image,
                    region,
                    scale,
                    sourcePixels,
                    preparedPixels,
                    estimatedPeakBytes,
                    stopwatch.Elapsed,
                    [],
                    OcrExecutionStatus.Unavailable,
                    "ocr_provider_unavailable");
            }

            var nativeWork = Task.Run(
                () => RecognizeCore(engine, encoded, region, scale),
                CancellationToken.None);
            lock (_lifetimeGate)
            {
                _nativeWork = nativeWork;
            }

            IReadOnlyList<OcrLine> lines;
            try
            {
                var remaining = _options.FrameTimeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException();
                }

                lines = await nativeWork
                    .WaitAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);
                ClearNativeWork(nativeWork);
            }
            catch (TimeoutException)
            {
                stopwatch.Stop();
                releaseGate = false;
                _ = ReleaseAfterNativeCompletionAsync(nativeWork);
                return CreateExecution(
                    image,
                    region,
                    scale,
                    sourcePixels,
                    preparedPixels,
                    estimatedPeakBytes,
                    stopwatch.Elapsed,
                    [],
                    OcrExecutionStatus.TimedOut,
                    "ocr_frame_timeout");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                releaseGate = false;
                _ = ReleaseAfterNativeCompletionAsync(nativeWork);
                throw;
            }
            catch
            {
                ClearNativeWork(nativeWork);
                throw;
            }

            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            return CreateExecution(
                image,
                region,
                scale,
                sourcePixels,
                preparedPixels,
                estimatedPeakBytes,
                stopwatch.Elapsed,
                lines,
                OcrExecutionStatus.Complete,
                null);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            stopwatch.Stop();
            _engine?.Dispose();
            _engine = null;
            Availability = new(false, ProviderName, SummarizeProviderFailure(exception));
            return WithDuration(
                EmptyExecution(image, request, OcrExecutionStatus.Failed, "ocr_provider_failed"),
                stopwatch.Elapsed);
        }
        finally
        {
            if (releaseGate)
            {
                _gate.Release();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        lock (_lifetimeGate)
        {
            if (_nativeWork is null)
            {
                _engine?.Dispose();
                _engine = null;
            }
            // Otherwise the completion observer disposes the provider after native code has
            // stopped touching it. The semaphore is intentionally not disposed: a completion
            // observer may still need to release it.
        }
    }

    private OcrEngineAvailability Initialize(TesseractOcrOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(false, ProviderName, "The packaged native provider is available on Windows only.");
        }

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return new(false, ProviderName, "The packaged provider requires a Windows x64 process.");
        }

        try
        {
            var tessdataPath = ResolveTessdataPath(options);
            var modelPath = Path.Combine(tessdataPath, $"{options.Language}.traineddata");
            if (!File.Exists(modelPath))
            {
                return new(false, ProviderName, "The configured traineddata file is missing.");
            }

            _engine = new Engine(tessdataPath, options.Language, EngineMode.LstmOnly)
            {
                DefaultPageSegMode = PageSegMode.SparseText,
            };
            return new(true, ProviderName);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            _engine?.Dispose();
            _engine = null;
            return new(false, ProviderName, SummarizeProviderFailure(exception));
        }
    }

    private static IReadOnlyList<OcrLine> RecognizeCore(
        Engine engine,
        byte[] encoded,
        PixelRect region,
        int scale)
    {
        using var pix = TesseractImage.LoadFromMemory(encoded);
        using var page = engine.Process(pix, PageSegMode.SparseText);
        var lines = new List<OcrLine>();
        foreach (var block in page.Layout)
        {
            foreach (var paragraph in block.Paragraphs)
            {
                foreach (var textLine in paragraph.TextLines)
                {
                    var text = textLine.Text?.Trim();
                    var bounds = textLine.BoundingBox;
                    if (string.IsNullOrWhiteSpace(text) || bounds is null)
                    {
                        continue;
                    }

                    // Divided back out, because the caller asked about the picture it took
                    // and a box measured on an enlarged copy of it means nothing there.
                    lines.Add(new(
                        text,
                        new PixelRect(
                            region.X + (bounds.Value.X1 / scale),
                            region.Y + (bounds.Value.Y1 / scale),
                            Math.Max(1, bounds.Value.Width / scale),
                            Math.Max(1, bounds.Value.Height / scale)),
                        NormalizeConfidence(textLine.Confidence)));
                }
            }
        }

        return lines
            .OrderBy(line => line.Bounds.Y)
            .ThenBy(line => line.Bounds.X)
            .ToArray();
    }

    private static Confidence NormalizeConfidence(float percentage) =>
        new(Math.Clamp(percentage / 100d, 0, 1));

    private static PixelRect ClampRegion(CapturedImage image, PixelRect? requested)
    {
        if (requested is null)
        {
            return new(0, 0, image.Width, image.Height);
        }

        var left = Math.Clamp(requested.X, 0, image.Width);
        var top = Math.Clamp(requested.Y, 0, image.Height);
        var right = (int)Math.Clamp((long)requested.X + requested.Width, left, image.Width);
        var bottom = (int)Math.Clamp((long)requested.Y + requested.Height, top, image.Height);
        if (right == left || bottom == top)
        {
            throw new ArgumentOutOfRangeException(nameof(requested), "OCR region must intersect the captured image.");
        }

        return new(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Writes the region out as grey pixels, prepared however the caller asked.
    /// </summary>
    /// <remarks>
    /// Enlarging repeats each pixel rather than interpolating between them. Interpolation
    /// softens an edge, and a soft edge on sixteen pixel text is the thing being fixed;
    /// repetition keeps the stroke exactly as sharp as it was and simply gives the engine more
    /// of it to work with.
    /// </remarks>
    private static byte[] EncodePortableGraymap(
        CapturedImage image,
        PixelRect region,
        OcrPreparation preparation,
        CancellationToken cancellationToken)
    {
        var scale = preparation.SafeScale;
        var width = region.Width * scale;
        var height = region.Height * scale;
        var header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        var result = new byte[checked(header.Length + (width * height))];
        header.CopyTo(result, 0);
        var offset = header.Length;
        var threshold = preparation.BrightTextOnly ? Midpoint(image, region) : (byte)0;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowStart = offset;
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var value = CapturedImagePixels.GetLuminance(image, x, y);
                if (preparation.BrightTextOnly)
                {
                    value = value >= threshold ? (byte)255 : (byte)0;
                }

                for (var repeat = 0; repeat < scale; repeat++)
                {
                    result[offset++] = value;
                }
            }

            // Every row after the first in a scaled block is the same row again.
            for (var repeat = 1; repeat < scale; repeat++)
            {
                Array.Copy(result, rowStart, result, offset, width);
                offset += width;
            }
        }

        return result;
    }

    private async Task ReleaseAfterNativeCompletionAsync(Task nativeWork)
    {
        try
        {
            await nativeWork.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Availability = new(false, ProviderName, SummarizeProviderFailure(exception));
        }
        finally
        {
            ClearNativeWork(nativeWork);
            _gate.Release();
        }
    }

    private void ClearNativeWork(Task nativeWork)
    {
        lock (_lifetimeGate)
        {
            if (ReferenceEquals(_nativeWork, nativeWork))
            {
                _nativeWork = null;
                if (_disposed)
                {
                    _engine?.Dispose();
                    _engine = null;
                }
            }
        }
    }

    private static void ValidateOptions(TesseractOcrOptions options)
    {
        if (options.FrameTimeout <= TimeSpan.Zero ||
            options.MaximumSourcePixels <= 0 ||
            options.MaximumInputBytes <= 0 ||
            options.MaximumPreparedPixels <= 0 ||
            options.MaximumEstimatedPeakBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "OCR limits must be positive.");
        }
    }

    private static TesseractOcrExecution CreateExecution(
        CapturedImage image,
        PixelRect region,
        int scale,
        long sourcePixels,
        long preparedPixels,
        long estimatedPeakBytes,
        TimeSpan duration,
        IReadOnlyList<OcrLine> lines,
        OcrExecutionStatus status,
        string? diagnostic)
    {
        var available = status is OcrExecutionStatus.Complete or OcrExecutionStatus.Partial;
        var result = new OcrResult(lines, duration, ProviderName, available, diagnostic);
        return new(
            result,
            region,
            image.Width,
            image.Height,
            scale,
            checked(region.Width * scale),
            checked(region.Height * scale),
            region.Width > 0 && region.Height > 0 ? 1 : 0,
            sourcePixels,
            preparedPixels,
            estimatedPeakBytes,
            duration,
            ProviderName,
            status,
            diagnostic);
    }

    private static TesseractOcrExecution EmptyExecution(
        CapturedImage image,
        OcrRequest request,
        OcrExecutionStatus status,
        string diagnostic)
    {
        var region = ClampRegion(image, request.Region);
        var scale = request.Preparation.SafeScale;
        var sourcePixels = checked((long)image.Width * image.Height);
        var preparedPixels = checked((long)region.Width * region.Height * scale * scale);
        var estimatedPeakBytes = checked(image.Pixels.Length + (preparedPixels * 2) + 64);
        return CreateExecution(
            image,
            region,
            scale,
            sourcePixels,
            preparedPixels,
            estimatedPeakBytes,
            TimeSpan.Zero,
            [],
            status,
            diagnostic);
    }

    private static TesseractOcrExecution WithDuration(
        TesseractOcrExecution execution,
        TimeSpan duration) => execution with
        {
            Duration = duration,
            Result = execution.Result with { Duration = duration },
        };

    /// <summary>
    /// Where to cut, when only the bright half of the picture is wanted.
    /// </summary>
    /// <remarks>
    /// Halfway between the darkest and brightest pixel in the region, rather than a fixed
    /// level. The game's panels are drawn over whatever the player is looking at, so the same
    /// interface sits on a night-time forest and a lit warehouse, and a fixed level would take
    /// all of one and none of the other.
    ///
    /// Sampled on a grid. The answer is a rough midpoint and reading every pixel of a four
    /// megapixel frame to find one is time spent on precision nothing uses.
    /// </remarks>
    private static byte Midpoint(CapturedImage image, PixelRect region)
    {
        byte darkest = 255;
        byte brightest = 0;
        var step = Math.Max(1, Math.Min(region.Width, region.Height) / 128);
        for (var y = region.Y; y < region.Y + region.Height; y += step)
        {
            for (var x = region.X; x < region.X + region.Width; x += step)
            {
                var value = CapturedImagePixels.GetLuminance(image, x, y);
                if (value < darkest)
                {
                    darkest = value;
                }

                if (value > brightest)
                {
                    brightest = value;
                }
            }
        }

        return brightest <= darkest ? (byte)128 : (byte)((darkest + brightest) / 2);
    }

    private static string ResolveTessdataPath(TesseractOcrOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.TessdataPath))
        {
            return Path.GetFullPath(options.TessdataPath);
        }

        if (!options.UseBundledEnglishData || !string.Equals(options.Language, "eng", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(AppContext.BaseDirectory, "tessdata");
        }

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new DirectoryNotFoundException("The local application-data directory is unavailable.");
        }

        var targetDirectory = Path.Combine(localData, "TarkovCompanion", "Cache", "Tessdata", "8741641");
        var targetPath = Path.Combine(targetDirectory, "eng.traineddata");
        Directory.CreateDirectory(targetDirectory);
        if (File.Exists(targetPath) && HasExpectedHash(targetPath))
        {
            return targetDirectory;
        }

        using var source = Assembly.GetExecutingAssembly().GetManifestResourceStream(BundledModelResource)
            ?? throw new InvalidOperationException("The bundled English traineddata resource is unavailable.");
        var temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            if (!HasExpectedHash(temporaryPath))
            {
                throw new InvalidDataException("The bundled English traineddata checksum is invalid.");
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return targetDirectory;
    }

    private static bool HasExpectedHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(hash, BundledModelSha256, StringComparison.Ordinal);
    }

    private static bool IsProviderFailure(Exception exception) =>
        exception is DllNotFoundException or
            BadImageFormatException or
            TypeInitializationException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException ||
        exception.GetType().Namespace?.StartsWith("TesseractOCR", StringComparison.Ordinal) == true;

    private static string SummarizeProviderFailure(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current switch
        {
            DllNotFoundException => "A packaged native OCR library or its Visual C++ runtime dependency is unavailable.",
            BadImageFormatException => "The packaged OCR native library does not match the process architecture.",
            UnauthorizedAccessException => "The OCR model cache is not writable.",
            IOException => "The OCR model cache could not be prepared.",
            _ => current.GetType().Name + ": OCR provider initialization or execution failed.",
        };
    }
}
