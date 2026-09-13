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
    bool UseBundledEnglishData = true);

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
    private readonly string _language;
    private Engine? _engine;
    private bool _disposed;

    public TesseractOcrEngine(TesseractOcrOptions? options = null)
    {
        options ??= new TesseractOcrOptions();
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Language);
        _language = options.Language;
        Availability = Initialize(options);
    }

    public OcrEngineAvailability Availability { get; private set; }

    public async Task<OcrResult> RecognizeAsync(
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
            return new([], TimeSpan.Zero, ProviderName, false, "ocr_provider_unavailable");
        }

        if (!string.Equals(request.Language, _language, StringComparison.OrdinalIgnoreCase))
        {
            return new([], TimeSpan.Zero, ProviderName, false, "ocr_language_unavailable");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var region = ClampRegion(image, request.Region);
            var preparation = request.Preparation;
            var encoded = EncodePortableGraymap(image, region, preparation);
            var lines = await Task.Run(
                () => RecognizeCore(encoded, region, preparation.SafeScale),
                CancellationToken.None).ConfigureAwait(false);
            stopwatch.Stop();
            cancellationToken.ThrowIfCancellationRequested();
            return new(lines, stopwatch.Elapsed, ProviderName);
        }
        catch (Exception exception) when (IsProviderFailure(exception))
        {
            _engine?.Dispose();
            _engine = null;
            Availability = new(false, ProviderName, SummarizeProviderFailure(exception));
            return new([], TimeSpan.Zero, ProviderName, false, "ocr_provider_failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _engine?.Dispose();
        _gate.Dispose();
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

    private IReadOnlyList<OcrLine> RecognizeCore(byte[] encoded, PixelRect region, int scale)
    {
        using var pix = TesseractImage.LoadFromMemory(encoded);
        using var page = _engine!.Process(pix, PageSegMode.SparseText);
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
        var right = Math.Clamp(requested.X + requested.Width, left, image.Width);
        var bottom = Math.Clamp(requested.Y + requested.Height, top, image.Height);
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
    private static byte[] EncodePortableGraymap(CapturedImage image, PixelRect region, OcrPreparation preparation)
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
