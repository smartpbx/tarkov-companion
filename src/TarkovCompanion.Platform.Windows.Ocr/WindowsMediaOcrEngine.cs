using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Recognition;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using PixelFormat = TarkovCompanion.Core.Domain.Recognition.PixelFormat;

namespace TarkovCompanion.Platform.Windows.Ocr;

/// <summary>
/// Reads text with the engine Windows already has.
/// </summary>
/// <remarks>
/// <para>
/// Recognition is the thing in this application that keeps not working, and the engine it has
/// been using needs a native binary and the Microsoft Visual C++ redistributable, which the
/// Settings page has to warn about because it is a real installation requirement somebody has
/// to satisfy before a single screenshot can be read.
/// </para>
/// <para>
/// Windows has shipped an OCR engine since Windows 10. It needs no package, no native binary
/// and no runtime, and it returns per-word bounding boxes, which is more than the other one
/// gives. On the extract panel it is also simply better: the panel's names are drawn light and
/// small, and the thresholding the other engine needs is exactly what eats thin pale text.
/// </para>
/// <para>
/// This is an addition, never a replacement. It is preferred where it works; where it does not
/// the other engine answers, and a machine where neither works says so rather than failing
/// silently.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsMediaOcrEngine : IOcrEngine, IOcrEngineStatus
{
    public const string ProviderName = "windows-media-ocr";

    private readonly OcrEngine? _engine;

    public WindowsMediaOcrEngine()
    {
        try
        {
            // The user's own languages first, because somebody running a French Windows has a
            // French recogniser installed and an English one very likely not. English is the
            // fallback because the game's interface is what is being read, not the system's.
            _engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
            Availability = _engine is null
                ? new(false, ProviderName, "Windows has no OCR language pack installed for this profile.")
                : new(true, ProviderName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Creating the engine touches a system component that a stripped Windows install,
            // a Server SKU or a policy can remove. Said plainly rather than thrown, because the
            // whole point of this class is to be the one that might not be there.
            _engine = null;
            Availability = new(false, ProviderName, Summarize(exception));
        }
    }

    public OcrEngineAvailability Availability { get; }

    public async Task<OcrResult> RecognizeAsync(
        CapturedImage image,
        OcrRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_engine is null)
        {
            return new([], TimeSpan.Zero, ProviderName, false, "ocr_provider_unavailable");
        }

        var region = Clamp(image, request.Region);
        if (region.Width <= 0 || region.Height <= 0)
        {
            return new([], TimeSpan.Zero, ProviderName, true);
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            // Deliberately ignores OcrRequest.Preparation. That describes a threshold and a
            // pixel-repeat scale invented for the other engine, and this one is worse for
            // both: it does its own binarisation and its own scaling, and handing it a
            // one-bit picture throws away the greys it uses to find edges. Measured on the
            // extract panel, where the thresholded reading is the one that fails.
            using var bitmap = ToSoftwareBitmap(image, region, out var scale);
            var result = await _engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            return new(Read(result, region, scale), stopwatch.Elapsed, ProviderName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new([], TimeSpan.Zero, ProviderName, false, "ocr_provider_failed");
        }
    }

    /// <summary>
    /// Copies the requested region into the bitmap shape the engine wants.
    /// </summary>
    /// <remarks>
    /// The engine refuses an image whose larger side is above
    /// <see cref="OcrEngine.MaxImageDimension"/>, and an ultrawide screenshot is over it, so an
    /// oversized picture is halved until it fits. The factor is reported back so the bounds it
    /// returns can be multiplied out again: a caller matching a line to a place on the screen
    /// would otherwise be told a position in a picture that no longer exists.
    /// </remarks>
    private static SoftwareBitmap ToSoftwareBitmap(CapturedImage image, PixelRect region, out int scale)
    {
        scale = 1;
        var max = (int)OcrEngine.MaxImageDimension;
        while (Math.Max(region.Width / scale, region.Height / scale) > max)
        {
            scale *= 2;
        }

        var width = Math.Max(1, region.Width / scale);
        var height = Math.Max(1, region.Height / scale);
        var pixels = new byte[width * height * 4];
        var source = image.Pixels.Span;
        var bytesPerPixel = BytesPerPixel(image.Format);
        var redOffset = image.Format == PixelFormat.Rgba8888 ? 0 : 2;
        var blueOffset = image.Format == PixelFormat.Rgba8888 ? 2 : 0;

        for (var y = 0; y < height; y++)
        {
            var sourceRow = ((region.Y + (y * scale)) * image.Stride) + (region.X * bytesPerPixel);
            var targetRow = y * width * 4;
            for (var x = 0; x < width; x++)
            {
                var from = sourceRow + (x * scale * bytesPerPixel);
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
                    // Bgra8888 out, whatever came in. The engine is told Bgra8 below, so the
                    // order here is the one thing that must not be got wrong; reading a frame
                    // with the channels swapped is a picture that still looks like text and
                    // reads like noise.
                    pixels[to] = source[from + blueOffset];
                    pixels[to + 1] = source[from + 1];
                    pixels[to + 2] = source[from + redOffset];
                }

                // Opaque. The source alpha is whatever the screenshot decoder left behind and
                // a premultiplied bitmap with a zero alpha is a picture of nothing.
                pixels[to + 3] = 255;
            }
        }

        var bitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
        bitmap.CopyFromBuffer(pixels.AsBuffer());
        return bitmap;
    }

    /// <summary>
    /// Turns the engine's lines into ours, in the frame's own coordinates.
    /// </summary>
    /// <remarks>
    /// Bounds come from the words rather than the line, because the engine gives a rectangle
    /// per word and none per line. They are then shifted by the region's own origin and
    /// multiplied back up by whatever the picture was scaled down by, so a caller that asked
    /// about one corner of the screen is answered about that corner of the screen.
    ///
    /// The confidence is stated as unknown rather than invented. This engine returns no score
    /// of any kind, and a made-up one would be ranked against another engine's real one.
    /// </remarks>
    private static IReadOnlyList<OcrLine> Read(OcrResult result, PixelRect region, int scale)
    {
        var lines = new List<OcrLine>(result.Lines.Count);
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
                ? new PixelRect(region.X, region.Y, 0, 0)
                : new PixelRect(
                    region.X + ((int)left * scale),
                    region.Y + ((int)top * scale),
                    Math.Max(0, (int)(right - left) * scale),
                    Math.Max(0, (int)(bottom - top) * scale));
            lines.Add(new(line.Text.Trim(), bounds, Confidence.Unknown));
        }

        return lines;
    }

    private static PixelRect Clamp(CapturedImage image, PixelRect? region)
    {
        if (region is not { } requested)
        {
            return new(0, 0, image.Width, image.Height);
        }

        var x = Math.Clamp(requested.X, 0, image.Width);
        var y = Math.Clamp(requested.Y, 0, image.Height);
        return new(
            x,
            y,
            Math.Clamp(requested.Width, 0, image.Width - x),
            Math.Clamp(requested.Height, 0, image.Height - y));
    }

    private static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Bgra8888 or PixelFormat.Rgba8888 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static string Summarize(Exception exception) => exception switch
    {
        TypeLoadException or DllNotFoundException or EntryPointNotFoundException =>
            "This Windows install does not carry the OCR component.",
        UnauthorizedAccessException =>
            "Windows refused access to its OCR component.",
        _ => "Windows could not start its OCR engine: " + exception.Message,
    };
}
