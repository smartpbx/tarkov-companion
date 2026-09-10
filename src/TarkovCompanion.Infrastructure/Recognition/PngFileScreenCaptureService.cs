using System.Runtime.InteropServices;
using SkiaSharp;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// Loads an explicitly supplied developer/test PNG into the same in-memory pixel contract used
/// by native capture. It never persists or modifies the source image.
/// </summary>
public sealed class PngFileScreenCaptureService : IScreenCaptureService
{
    private const long MaximumFileBytes = 32 * 1024 * 1024;
    private const long MaximumPixels = 40_000_000;

    public Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.SourceImagePath))
        {
            throw new InvalidOperationException("An explicit PNG source path is required for file capture.");
        }

        var path = Path.GetFullPath(request.SourceImagePath);
        if (!string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Developer file capture accepts PNG images only.");
        }

        var file = new FileInfo(path);
        if (!file.Exists || file.Length is <= 0 or > MaximumFileBytes)
        {
            throw new InvalidDataException("The PNG source is missing, empty, or exceeds the 32 MiB input limit.");
        }

        using var source = File.OpenRead(path);
        using var decoded = SKBitmap.Decode(source)
            ?? throw new InvalidDataException("The PNG source could not be decoded.");
        if (decoded.Width <= 0 || decoded.Height <= 0 || (long)decoded.Width * decoded.Height > MaximumPixels)
        {
            throw new InvalidDataException("The decoded PNG dimensions are invalid or exceed the 40 megapixel limit.");
        }

        using var bitmap = new SKBitmap(
            new SKImageInfo(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.DrawBitmap(decoded, 0, 0);
            canvas.Flush();
        }

        var pixels = new byte[checked(bitmap.RowBytes * bitmap.Height)];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return Task.FromResult(new CapturedImage(
            pixels,
            bitmap.Width,
            bitmap.Height,
            bitmap.RowBytes,
            PixelFormat.Bgra8888,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            "developer-png:" + file.Name));
    }
}

public sealed class DeveloperScreenCaptureService(
    PngFileScreenCaptureService fileCapture,
    IScreenCaptureService nativeCapture) : IScreenCaptureService
{
    public Task<CapturedImage> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(request.SourceImagePath)
            ? nativeCapture.CaptureAsync(request, cancellationToken)
            : fileCapture.CaptureAsync(request, cancellationToken);
}
