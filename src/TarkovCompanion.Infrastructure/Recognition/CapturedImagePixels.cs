using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// Checks cancellation once per bounded number of pixel reads.
/// </summary>
/// <remarks>
/// Once per row is not bounded: a 40-million-pixel single-row region reads to its end before the
/// next row's check. This counts reads instead, and checks on the first one, so a token that was
/// already cancelled stops the work before any pixel is read. Passed by reference, because a copy
/// would count on its own.
/// </remarks>
internal struct PixelCancellationCheck(CancellationToken cancellationToken)
{
    public const int Interval = 1 << 16;

    private int _sinceCheck = Interval;

    public void Read(int pixels = 1)
    {
        _sinceCheck += pixels;
        if (_sinceCheck >= Interval)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sinceCheck = 0;
        }
    }
}

internal static class CapturedImagePixels
{
    /// <summary>
    /// The most pixels a whole-frame walk accepts: the providers' default source ceiling and the
    /// screenshot loader's decode ceiling.
    /// </summary>
    /// <remarks>
    /// The providers refused a frame over it, but container grid detection and segmentation walk
    /// the frame before any provider sees it, and they accepted any size the buffer covered.
    /// </remarks>
    public const long MaximumPixels = 40_000_000;

    /// <summary>True when the frame is over <see cref="MaximumPixels"/>, measured without reading a pixel.</summary>
    public static bool ExceedsPixelCeiling(CapturedImage image) =>
        (long)image.Width * image.Height > MaximumPixels;

    /// <summary>Validates the frame and refuses one over <paramref name="maximumPixels"/> before any pixel is read.</summary>
    public static void Validate(CapturedImage image, long maximumPixels)
    {
        Validate(image);
        if ((long)image.Width * image.Height > maximumPixels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(image),
                $"Captured image has more than the {maximumPixels:N0} pixels a whole-frame walk accepts.");
        }
    }

    public static void Validate(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentException("Captured image dimensions must be positive.", nameof(image));
        }

        var bytesPerPixel = BytesPerPixel(image.Format);
        if (image.Stride < checked(image.Width * bytesPerPixel))
        {
            throw new ArgumentException("Captured image stride is smaller than its visible row.", nameof(image));
        }

        if (image.Pixels.Length < checked(image.Stride * image.Height))
        {
            throw new ArgumentException("Captured image buffer is smaller than its dimensions and stride.", nameof(image));
        }
    }

    public static byte GetLuminance(CapturedImage image, int x, int y)
    {
        var pixels = image.Pixels.Span;
        var offset = checked((y * image.Stride) + (x * BytesPerPixel(image.Format)));
        if (image.Format == PixelFormat.Gray8)
        {
            return pixels[offset];
        }

        var redOffset = image.Format == PixelFormat.Rgba8888 ? 0 : 2;
        var blueOffset = image.Format == PixelFormat.Rgba8888 ? 2 : 0;
        var red = pixels[offset + redOffset];
        var green = pixels[offset + 1];
        var blue = pixels[offset + blueOffset];
        return (byte)(((red * 77) + (green * 150) + (blue * 29)) >> 8);
    }

    public static int BytesPerPixel(PixelFormat format) => format switch
    {
        PixelFormat.Gray8 => 1,
        PixelFormat.Bgra8888 or PixelFormat.Rgba8888 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static bool Intersects(PixelRect left, PixelRect right) =>
        left.Width > 0 &&
        left.Height > 0 &&
        right.Width > 0 &&
        right.Height > 0 &&
        left.X < right.X + right.Width &&
        left.X + left.Width > right.X &&
        left.Y < right.Y + right.Height &&
        left.Y + left.Height > right.Y;

    public static PixelRect FromNormalized(
        CapturedImage image,
        double x,
        double y,
        double width,
        double height)
    {
        var left = (int)Math.Round(image.Width * x, MidpointRounding.AwayFromZero);
        var top = (int)Math.Round(image.Height * y, MidpointRounding.AwayFromZero);
        var right = (int)Math.Round(image.Width * (x + width), MidpointRounding.AwayFromZero);
        var bottom = (int)Math.Round(image.Height * (y + height), MidpointRounding.AwayFromZero);
        return new(
            Math.Clamp(left, 0, image.Width),
            Math.Clamp(top, 0, image.Height),
            Math.Clamp(right - left, 0, image.Width - Math.Clamp(left, 0, image.Width)),
            Math.Clamp(bottom - top, 0, image.Height - Math.Clamp(top, 0, image.Height)));
    }
}
