using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests.Grid;

/// <summary>
/// Renders a grid-shaped frame pixel by pixel, the way a screenshot would present one, so
/// <see cref="TarkovCompanion.Infrastructure.Recognition.ContainerGridDetector"/> and
/// <see cref="TarkovCompanion.Infrastructure.Recognition.Grid.GridPixelReconstructionBuilder"/> can
/// be exercised through their real pixel-reading path instead of a shortcut around it.
/// </summary>
internal static class SyntheticGrid
{
    private const byte BackgroundLuminance = 180;
    private const byte LineLuminance = 30;

    public static CapturedImage Build(
        int width,
        int height,
        int originX,
        int originY,
        int columns,
        int rows,
        int pitch,
        IReadOnlySet<(int Row, int Column)> occupied)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        Fill(buffer, stride, 0, 0, width, height, BackgroundLuminance);

        var random = new Random(20260917);
        foreach (var cell in occupied)
        {
            var cellX = originX + (cell.Column * pitch);
            var cellY = originY + (cell.Row * pitch);
            var margin = Math.Max(2, pitch / 12);
            for (var y = cellY + margin; y < cellY + pitch - margin; y++)
            {
                for (var x = cellX + margin; x < cellX + pitch - margin; x++)
                {
                    SetPixel(buffer, stride, x, y, (byte)(random.Next(2) == 0 ? 40 : 230));
                }
            }
        }

        for (var column = 0; column <= columns; column++)
        {
            DrawVerticalLine(buffer, stride, Math.Min(originX + (column * pitch), width - 1), originY, rows * pitch, height);
        }

        for (var row = 0; row <= rows; row++)
        {
            DrawHorizontalLine(buffer, stride, Math.Min(originY + (row * pitch), height - 1), originX, columns * pitch, width);
        }

        return new CapturedImage(buffer, width, height, stride, PixelFormat.Bgra8888, DateTimeOffset.UtcNow, "synthetic-grid");
    }

    public static CapturedImage BuildBlank(int width, int height)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        Fill(buffer, stride, 0, 0, width, height, BackgroundLuminance);
        return new CapturedImage(buffer, width, height, stride, PixelFormat.Bgra8888, DateTimeOffset.UtcNow, "synthetic-blank");
    }

    public static ulong ComputeFingerprint(CapturedImage image, int x, int y, int width, int height)
    {
        var stride = width * 4;
        var buffer = new byte[stride * height];
        var source = image.Pixels.Span;
        for (var row = 0; row < height; row++)
        {
            var sourceOffset = ((y + row) * image.Stride) + (x * 4);
            source.Slice(sourceOffset, stride).CopyTo(buffer.AsSpan(row * stride, stride));
        }

        var cropped = new CapturedImage(buffer, width, height, stride, image.Format, image.CapturedUtc, "synthetic-crop");
        return SkiaPerceptualIconMatcher.ComputeDifferenceHash(cropped);
    }

    private static void Fill(byte[] buffer, int stride, int x, int y, int width, int height, byte luminance)
    {
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                SetPixel(buffer, stride, column, row, luminance);
            }
        }
    }

    private static void DrawVerticalLine(byte[] buffer, int stride, int x, int originY, int length, int frameHeight)
    {
        var end = Math.Min(originY + length, frameHeight);
        for (var y = originY; y < end; y++)
        {
            SetPixel(buffer, stride, x, y, LineLuminance);
        }
    }

    private static void DrawHorizontalLine(byte[] buffer, int stride, int y, int originX, int length, int frameWidth)
    {
        var end = Math.Min(originX + length, frameWidth);
        for (var x = originX; x < end; x++)
        {
            SetPixel(buffer, stride, x, y, LineLuminance);
        }
    }

    private static void SetPixel(byte[] buffer, int stride, int x, int y, byte luminance)
    {
        var offset = (y * stride) + (x * 4);
        buffer[offset] = luminance;
        buffer[offset + 1] = luminance;
        buffer[offset + 2] = luminance;
        buffer[offset + 3] = 255;
    }
}
