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

    /// <summary>The most provider lines one request accepts.</summary>
    public int MaximumLines { get; init; } = 4_096;

    /// <summary>The longest text one accepted line keeps.</summary>
    public int MaximumLineTextLength { get; init; } = 1_024;
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

    // How many pixels preparation touches between cancellation checks. A row is not a bounded
    // unit: a 40-million-pixel single-row region would otherwise run to the end uninterrupted.
    private const int CancellationCheckPixels = 1 << 16;
    private const int PortableGraymapHeaderAllowance = 64;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifetimeGate = new();
    private readonly string _language;
    private readonly TesseractOcrOptions _options;
    private ITesseractPageReader? _reader;
    private Task? _nativeWork;
    private bool _disposed;

    // Set, with Availability, under the lifetime lock when native work fails. A retired reader is
    // never read again and is freed as soon as no native work is registered on it.
    private bool _retired;

    public TesseractOcrEngine(TesseractOcrOptions? options = null)
    {
        options ??= new TesseractOcrOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Language);
        ValidateOptions(options);
        _options = options;
        _language = options.Language;
        Availability = Initialize(options);
    }

    /// <summary>
    /// A provider over a supplied page reader, so the gate, deadline and limits can be proven on
    /// any host rather than only where the native library loads.
    /// </summary>
    internal TesseractOcrEngine(TesseractOcrOptions options, ITesseractPageReader reader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Language);
        ValidateOptions(options);
        _options = options;
        _language = options.Language;
        _reader = reader;
        Availability = new(true, ProviderName);
    }

    public OcrEngineAvailability Availability { get; private set; }

    /// <summary>
    /// Runs inside the lifetime lock after the reader is taken and before its read is registered.
    /// </summary>
    /// <remarks>
    /// A test stands a concurrent Dispose here to prove it cannot land between the two.
    /// </remarks>
    internal Action? NativeWorkRegistering { get; set; }

    /// <summary>Runs as <see cref="WaitForSettledAsync"/> begins, before it waits for the provider.</summary>
    /// <remarks>A test releases stalled native work here, once a settle wait is certainly under way.</remarks>
    internal Action? SettleWaiting { get; set; }

    /// <summary>
    /// Waits until the provider is idle: no request is reading and no native work a request
    /// abandoned is still running.
    /// </summary>
    /// <remarks>
    /// <see cref="Availability"/> is final only then. A timed-out or cancelled request returns while
    /// its native read carries on, and a failure that read reports later retires the provider after
    /// the request has gone. Anything that records availability once it has finished reading, as the
    /// OCR probe's report does, used to record it before that failure landed and so kept saying
    /// available. Abandoned work holds the provider gate until it has settled and the provider has
    /// been retired if it failed, so obtaining the gate here is the proof that it has.
    /// </remarks>
    /// <returns>
    /// True once idle; false when native work had still not settled within <paramref name="timeout"/>,
    /// in which case the availability read afterwards may yet change.
    /// </returns>
    public async Task<bool> WaitForSettledAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "The settle wait must be non-negative and bounded.");
        }

        SettleWaiting?.Invoke();
        if (!await _gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _gate.Release();
        return true;
    }

    public async Task<OcrResult> RecognizeAsync(
        CapturedImage image,
        OcrRequest request,
        CancellationToken cancellationToken) =>
        (await RecognizeDetailedAsync(image, request, cancellationToken).ConfigureAwait(false)).Result;

    /// <summary>
    /// Reads one region under a hard caller-visible deadline and reports the exact preparation
    /// dimensions. Tesseract's native call is not cooperatively cancellable, so a timed-out or
    /// caller-cancelled call keeps exclusive ownership of the provider until it really exits;
    /// another call can never overlap it.
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

        var region = ClampRegion(image, request.Region);
        var preparation = request.Preparation;
        var budgetDiagnostic = OcrExecutionBudget.Check(
            image,
            region,
            preparation.SafeScale,
            _options.MaximumSourcePixels,
            _options.MaximumInputBytes,
            _options.MaximumPreparedPixels,
            _options.MaximumEstimatedPeakBytes,
            out var sourcePixels,
            out var preparedPixels,
            out var estimatedPeakBytes);
        var plan = new ExecutionPlan(
            image,
            region,
            preparation.SafeScale,
            sourcePixels,
            preparedPixels,
            estimatedPeakBytes);

        // The reader itself is looked at only under the lifetime lock below. Read here, a Dispose
        // racing this request made it report an unavailable provider instead of a disposed one.
        if (!Availability.IsAvailable)
        {
            return plan.Create(TimeSpan.Zero, OcrExecutionStatus.Unavailable, "ocr_provider_unavailable");
        }

        if (!string.Equals(request.Language, _language, StringComparison.OrdinalIgnoreCase))
        {
            return plan.Create(TimeSpan.Zero, OcrExecutionStatus.Unavailable, "ocr_language_unavailable");
        }

        if (budgetDiagnostic == OcrExecutionBudget.InputLimitExceeded)
        {
            return plan.Create(TimeSpan.Zero, OcrExecutionStatus.Rejected, budgetDiagnostic);
        }

        if (region.Width == 0 || region.Height == 0)
        {
            // The same answer the Windows provider gives, in the same order. This used to throw,
            // so a contextual crop clamped to nothing ended the whole scan instead of reading as
            // an available, empty pass.
            return plan.Create(TimeSpan.Zero, OcrExecutionStatus.Empty, OcrOutcome.RegionEmpty);
        }

        if (budgetDiagnostic is not null)
        {
            return plan.Create(TimeSpan.Zero, OcrExecutionStatus.Rejected, budgetDiagnostic);
        }

        var stopwatch = Stopwatch.StartNew();
        if (!await _gate.WaitAsync(_options.FrameTimeout, cancellationToken).ConfigureAwait(false))
        {
            // A previous request, or native work it had to abandon, still owns the provider.
            return plan.Create(stopwatch.Elapsed, OcrExecutionStatus.TimedOut, "ocr_frame_timeout");
        }

        var releaseGate = true;
        var attempted = 0;
        try
        {
            // Looked at again now that this request owns the provider. It can have queued behind
            // native work an earlier request abandoned, and that work can fail, or the provider be
            // disposed, while it waits. The availability check above ran before either; a late
            // failure used to mark the provider unavailable but leave its reader in place, so the
            // request queued behind it prepared the frame and started a read on the failed engine.
            lock (_lifetimeGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_retired || _reader is null)
                {
                    return plan.Create(stopwatch.Elapsed, OcrExecutionStatus.Unavailable, "ocr_provider_unavailable");
                }
            }

            var remaining = _options.FrameTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return plan.Create(stopwatch.Elapsed, OcrExecutionStatus.TimedOut, "ocr_frame_timeout");
            }

            using var frameCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            frameCancellation.CancelAfter(remaining);
            byte[] encoded;
            try
            {
                encoded = EncodePortableGraymap(image, region, preparation, frameCancellation.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return plan.Create(stopwatch.Elapsed, OcrExecutionStatus.TimedOut, "ocr_frame_timeout");
            }
            catch (OutOfMemoryException)
            {
                return plan.Create(stopwatch.Elapsed, OcrExecutionStatus.Failed, OcrOutcome.MemoryExhausted);
            }

            remaining = _options.FrameTimeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return plan.Create(stopwatch.Elapsed, OcrExecutionStatus.TimedOut, "ocr_frame_timeout");
            }

            var scale = plan.Scale;
            var maximumLines = _options.MaximumLines;
            Task<IReadOnlyList<OcrLine>> nativeWork;
            lock (_lifetimeGate)
            {
                // Taking the reader, checking for disposal and registering the native work are one
                // step. Dispose frees the reader only when it finds no registered work under this
                // lock, and they used to be three: a Dispose landing between taking the reader and
                // registering the read freed it, and the read then started on a freed engine.
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_retired || _reader is not { } reader)
                {
                    return plan.Create(stopwatch.Elapsed, OcrExecutionStatus.Unavailable, "ocr_provider_unavailable");
                }

                NativeWorkRegistering?.Invoke();
                nativeWork = Task.Run(
                    () => reader.Read(encoded, region, scale, maximumLines),
                    CancellationToken.None);
                _nativeWork = nativeWork;
            }

            attempted = 1;

            IReadOnlyList<OcrLine> nativeLines;
            try
            {
                nativeLines = await nativeWork
                    .WaitAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                releaseGate = false;
                _ = ReleaseAfterNativeCompletionAsync(nativeWork);
                return plan.Create(
                    stopwatch.Elapsed,
                    OcrExecutionStatus.TimedOut,
                    "ocr_frame_timeout",
                    attempted: attempted);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                releaseGate = false;
                _ = ReleaseAfterNativeCompletionAsync(nativeWork);
                throw;
            }
            catch (OutOfMemoryException)
            {
                ClearNativeWork(nativeWork);
                return plan.Create(
                    stopwatch.Elapsed,
                    OcrExecutionStatus.Failed,
                    OcrOutcome.MemoryExhausted,
                    attempted: attempted);
            }
            catch
            {
                ClearNativeWork(nativeWork);
                throw;
            }

            ClearNativeWork(nativeWork);
            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            var lines = BoundLines(nativeLines, out var truncated, out var limitReached);
            var status = OcrExecutionStatus.Complete;
            string? diagnostic = null;
            if (limitReached)
            {
                status = OcrExecutionStatus.Partial;
                diagnostic = "ocr_line_limit_exceeded";
            }
            else if (truncated > 0)
            {
                status = OcrExecutionStatus.Partial;
                diagnostic = "ocr_line_text_truncated";
            }
            else if (lines.Count == 0)
            {
                // A read that ran to completion and found nothing is an answer about the pixels.
                // It stays available, and says so, instead of looking like a complete read.
                status = OcrExecutionStatus.Empty;
                diagnostic = OcrOutcome.NoText;
            }

            return plan.Create(
                stopwatch.Elapsed,
                status,
                diagnostic,
                lines,
                attempted: attempted,
                completed: 1) with
                {
                    ProviderLineCount = lines.Count,
                    TruncatedLineCount = truncated,
                };
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            stopwatch.Stop();
            lock (_lifetimeGate)
            {
                RetireLocked(exception);
            }

            return plan.Create(
                stopwatch.Elapsed,
                OcrExecutionStatus.Failed,
                "ocr_provider_failed",
                attempted: attempted);
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
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // With native work registered, the completion observer frees the reader after native
            // code has stopped touching it. The semaphore is intentionally not disposed: a
            // completion observer may still need to release it.
            FreeReaderIfUnusedLocked();
        }
    }

    private OcrEngineAvailability Initialize(TesseractOcrOptions options)
    {
        if (!OperatingSystem.IsWindows())
        {
            return OcrEngineAvailability.Unavailable(ProviderName, OcrUnavailableReason.WindowsOnly, "The packaged native provider is available on Windows only.");
        }

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return OcrEngineAvailability.Unavailable(ProviderName, OcrUnavailableReason.NeedsX64, "The packaged provider requires a Windows x64 process.");
        }

        Engine? engine = null;
        try
        {
            var tessdataPath = ResolveTessdataPath(options);
            var modelPath = Path.Combine(tessdataPath, $"{options.Language}.traineddata");
            if (!File.Exists(modelPath))
            {
                return OcrEngineAvailability.Unavailable(ProviderName, OcrUnavailableReason.TraineddataMissing, "The configured traineddata file is missing.");
            }

            engine = new Engine(tessdataPath, options.Language, EngineMode.LstmOnly)
            {
                DefaultPageSegMode = PageSegMode.SparseText,
            };
            _reader = new NativeTesseractPageReader(engine);
            return new(true, ProviderName);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            engine?.Dispose();
            _reader = null;
            return Unavailable(exception);
        }
    }

    private IReadOnlyList<OcrLine> BoundLines(
        IReadOnlyList<OcrLine> nativeLines,
        out int truncated,
        out bool limitReached)
    {
        truncated = 0;
        limitReached = false;
        var lines = new List<OcrLine>(Math.Min(nativeLines.Count, _options.MaximumLines));
        foreach (var line in nativeLines)
        {
            if (string.IsNullOrWhiteSpace(line.Text))
            {
                continue;
            }

            if (lines.Count == _options.MaximumLines)
            {
                limitReached = true;
                break;
            }

            if (line.Text.Length <= _options.MaximumLineTextLength)
            {
                lines.Add(line);
                continue;
            }

            truncated++;
            lines.Add(line with { Text = Truncate(line.Text, _options.MaximumLineTextLength) });
        }

        return lines;
    }

    private static string Truncate(string text, int maximumLength)
    {
        // Never leave half of a surrogate pair at the cut.
        var length = char.IsHighSurrogate(text[maximumLength - 1]) ? maximumLength - 1 : maximumLength;
        return text[..length];
    }

    private static PixelRect ClampRegion(CapturedImage image, PixelRect? requested)
    {
        if (requested is null)
        {
            return new(0, 0, image.Width, image.Height);
        }

        // A region that misses the frame clamps to zero area; the caller reports it as empty.
        var left = Math.Clamp(requested.X, 0, image.Width);
        var top = Math.Clamp(requested.Y, 0, image.Height);
        var right = (int)Math.Clamp((long)requested.X + requested.Width, left, image.Width);
        var bottom = (int)Math.Clamp((long)requested.Y + requested.Height, top, image.Height);
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
        var width = checked(region.Width * scale);
        var height = checked(region.Height * scale);
        var header = Encoding.ASCII.GetBytes($"P5\n{width} {height}\n255\n");
        var result = new byte[checked(header.Length + ((long)width * height))];
        header.CopyTo(result, 0);
        var offset = header.Length;
        var threshold = preparation.BrightTextOnly ? Midpoint(image, region, cancellationToken) : (byte)0;
        var sinceCheck = (long)CancellationCheckPixels;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            var rowStart = offset;
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                if (sinceCheck >= CancellationCheckPixels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sinceCheck = 0;
                }

                var value = CapturedImagePixels.GetLuminance(image, x, y);
                if (preparation.BrightTextOnly)
                {
                    value = value >= threshold ? (byte)255 : (byte)0;
                }

                for (var repeat = 0; repeat < scale; repeat++)
                {
                    result[offset++] = value;
                }

                sinceCheck += scale;
            }

            // Every row after the first in a scaled block is the same row again. One copy is at
            // most one prepared row, so a check before each keeps the interval bounded.
            for (var repeat = 1; repeat < scale; repeat++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Array.Copy(result, rowStart, result, offset, width);
                offset += width;
            }
        }

        return result;
    }

    /// <summary>
    /// Holds the gate for native work a request abandoned, and settles that work before opening it.
    /// </summary>
    /// <remarks>
    /// The work is cleared and, when it failed, the provider retired, in one step under the
    /// lifetime lock and before the gate is released. A request queued on the gate therefore
    /// finds the provider either still usable or already retired, and never a failed engine
    /// whose reader is still in place.
    /// </remarks>
    private async Task ReleaseAfterNativeCompletionAsync(Task nativeWork)
    {
        Exception? providerFailure = null;
        try
        {
            await nativeWork.ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            providerFailure = exception;
        }
        catch
        {
            // The abandoning request already reported its timeout or cancellation. Awaiting
            // here observes a late failure so it cannot surface as an unobserved exception.
        }
        finally
        {
            ClearNativeWork(nativeWork, providerFailure);
            _gate.Release();
        }
    }

    private void ClearNativeWork(Task nativeWork, Exception? providerFailure = null)
    {
        lock (_lifetimeGate)
        {
            if (ReferenceEquals(_nativeWork, nativeWork))
            {
                _nativeWork = null;
            }

            if (providerFailure is not null)
            {
                RetireLocked(providerFailure);
            }
            else
            {
                FreeReaderIfUnusedLocked();
            }
        }
    }

    /// <summary>Marks the provider unavailable and its reader unusable. Caller holds the lifetime lock.</summary>
    private void RetireLocked(Exception failure)
    {
        _retired = true;
        Availability = Unavailable(failure);
        FreeReaderIfUnusedLocked();
    }

    /// <summary>
    /// Frees the reader once no native work is registered on it and nothing may read it again.
    /// Caller holds the lifetime lock.
    /// </summary>
    private void FreeReaderIfUnusedLocked()
    {
        if (_nativeWork is null && (_disposed || _retired))
        {
            _reader?.Dispose();
            _reader = null;
        }
    }

    private static void ValidateOptions(TesseractOcrOptions options)
    {
        if (options.FrameTimeout <= TimeSpan.Zero ||
            options.FrameTimeout > TimeSpan.FromDays(1) ||
            options.MaximumSourcePixels <= 0 ||
            options.MaximumInputBytes <= 0 ||
            options.MaximumPreparedPixels <= 0 ||
            options.MaximumPreparedPixels > Array.MaxLength - PortableGraymapHeaderAllowance ||
            options.MaximumEstimatedPeakBytes <= 0 ||
            options.MaximumLines <= 0 ||
            options.MaximumLineTextLength <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "OCR limits must be positive and bounded.");
        }
    }

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
    /// megapixel frame to find one is time spent on precision nothing uses. The grid step comes
    /// from the shorter side, so a very long thin region can still sample every pixel; that is
    /// why this checks cancellation by samples taken rather than by row.
    /// </remarks>
    private static byte Midpoint(CapturedImage image, PixelRect region, CancellationToken cancellationToken)
    {
        byte darkest = 255;
        byte brightest = 0;
        var step = Math.Max(1, Math.Min(region.Width, region.Height) / 128);
        var sinceCheck = CancellationCheckPixels;
        for (var y = region.Y; y < region.Y + region.Height; y += step)
        {
            for (var x = region.X; x < region.X + region.Width; x += step)
            {
                if (sinceCheck >= CancellationCheckPixels)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    sinceCheck = 0;
                }

                sinceCheck++;
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
        exception is not ObjectDisposedException &&
        (exception is DllNotFoundException or
            BadImageFormatException or
            TypeInitializationException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException ||
        exception.GetType().Namespace?.StartsWith("TesseractOCR", StringComparison.Ordinal) == true);

    private static OcrEngineAvailability Unavailable(Exception exception)
    {
        var current = exception;
        while (current.InnerException is not null)
        {
            current = current.InnerException;
        }

        return current switch
        {
            DllNotFoundException => OcrEngineAvailability.Unavailable(ProviderName, OcrUnavailableReason.NativeLibraryMissing, "A packaged native OCR library or its Visual C++ runtime dependency is unavailable."),
            BadImageFormatException => OcrEngineAvailability.Unavailable(ProviderName, OcrUnavailableReason.ArchitectureMismatch, "The packaged OCR native library does not match the process architecture."),
            UnauthorizedAccessException => OcrEngineAvailability.Unavailable(ProviderName, OcrUnavailableReason.CacheNotWritable, "The OCR model cache is not writable."),
            IOException => OcrEngineAvailability.Unavailable(ProviderName, OcrUnavailableReason.CacheNotPrepared, "The OCR model cache could not be prepared."),
            _ => OcrEngineAvailability.Unavailable(ProviderName, OcrUnavailableReason.ProviderFailed, current.GetType().Name + ": OCR provider initialization or execution failed.", current.GetType().Name),
        };
    }

    /// <summary>What one request measured before any work started, so every exit reports the same facts.</summary>
    private sealed record ExecutionPlan(
        CapturedImage Image,
        PixelRect Region,
        int Scale,
        long SourcePixels,
        long PreparedPixels,
        long EstimatedPeakBytes)
    {
        public TesseractOcrExecution Create(
            TimeSpan duration,
            OcrExecutionStatus status,
            string? diagnostic,
            IReadOnlyList<OcrLine>? lines = null,
            int attempted = 0,
            int completed = 0)
        {
            var available = status is OcrExecutionStatus.Complete or OcrExecutionStatus.Empty or OcrExecutionStatus.Partial;
            var result = new OcrResult(lines ?? [], duration, ProviderName, available, diagnostic);
            return new(
                result,
                Region,
                Image.Width,
                Image.Height,
                Scale,
                checked(Region.Width * Scale),
                checked(Region.Height * Scale),
                Region.Width > 0 && Region.Height > 0 ? 1 : 0,
                attempted,
                completed,
                SourcePixels,
                PreparedPixels,
                EstimatedPeakBytes,
                duration,
                ProviderName,
                status,
                diagnostic);
        }
    }
}

/// <summary>The one synchronous, uninterruptible native step of a Tesseract read.</summary>
internal interface ITesseractPageReader : IDisposable
{
    /// <summary>
    /// Reads an encoded page and returns lines in source-image coordinates, stopping one line
    /// past <paramref name="maximumLines"/> so the caller can tell a full page from a cut one.
    /// </summary>
    IReadOnlyList<OcrLine> Read(byte[] portableGraymap, PixelRect region, int scale, int maximumLines);
}

internal sealed class NativeTesseractPageReader(Engine engine) : ITesseractPageReader
{
    public IReadOnlyList<OcrLine> Read(byte[] portableGraymap, PixelRect region, int scale, int maximumLines)
    {
        using var pix = TesseractImage.LoadFromMemory(portableGraymap);
        using var page = engine.Process(pix, PageSegMode.SparseText);
        var lines = new List<OcrLine>();
        foreach (var block in page.Layout)
        {
            foreach (var paragraph in block.Paragraphs)
            {
                foreach (var textLine in paragraph.TextLines)
                {
                    if (lines.Count > maximumLines)
                    {
                        return Order(lines);
                    }

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

        return Order(lines);
    }

    public void Dispose() => engine.Dispose();

    private static OcrLine[] Order(List<OcrLine> lines) => lines
        .OrderBy(line => line.Bounds.Y)
        .ThenBy(line => line.Bounds.X)
        .ToArray();

    private static Confidence NormalizeConfidence(float percentage) =>
        new(Math.Clamp(percentage / 100d, 0, 1));
}
