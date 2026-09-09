using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

internal static class CapturedImagePixels
{
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
