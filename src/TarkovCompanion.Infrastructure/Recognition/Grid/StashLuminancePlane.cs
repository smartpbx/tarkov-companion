using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>
/// One frame's luminance, read once, and the one question the stash reader asks of it: is this
/// pixel a one-pixel line?
/// </summary>
/// <remarks>
/// <para>
/// Measured on real stash screenshots (3840x1080, 2026-09-18). An item's border is a single pixel
/// about 25 to 45 luminance levels brighter than the pixels on both sides of it, teal-tinted, and
/// unbroken along the whole side. The line between two empty cells stands about 15 above a hatch
/// that itself alternates by about 6 from one pixel to the next. What shows through the
/// transparent parts of a large item's art stands 8 or less above its surroundings.
/// </para>
/// <para>
/// So a line is judged against the pixels two away on each side, not four: bright art sits within
/// a few pixels of a border often enough that the wider comparison missed borders, and one missed
/// border was enough to chain two items into a shape that is not a rectangle. Either polarity
/// counts, because an earlier measurement of another screen found the lines darker than the cells.
/// </para>
/// </remarks>
internal sealed class StashLuminancePlane
{
    /// <summary>How far a pixel must stand clear of both neighbours to be part of a line.</summary>
    public const int MinimumRidge = 10;

    private readonly byte[] _luminance;

    private StashLuminancePlane(byte[] luminance, int width, int height)
    {
        _luminance = luminance;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public static StashLuminancePlane From(CapturedImage image, CancellationToken cancellationToken)
    {
        var plane = new byte[image.Width * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            if ((y & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            for (var x = 0; x < image.Width; x++)
            {
                plane[(y * image.Width) + x] = CapturedImagePixels.GetLuminance(image, x, y);
            }
        }

        return new(plane, image.Width, image.Height);
    }

    public int At(int x, int y) => _luminance[(Math.Clamp(y, 0, Height - 1) * Width) + Math.Clamp(x, 0, Width - 1)];

    /// <summary>
    /// How far the pixel stands clear of the pixels two away on either side, across a vertical
    /// line (<paramref name="vertical"/>) or a horizontal one. Zero when it does not stand clear
    /// of both in the same direction.
    /// </summary>
    public int Ridge(int x, int y, bool vertical)
    {
        var value = At(x, y);
        var before = vertical ? At(x - 2, y) : At(x, y - 2);
        var after = vertical ? At(x + 2, y) : At(x, y + 2);
        var above = value - Math.Max(before, after);
        var below = Math.Min(before, after) - value;
        return Math.Max(0, Math.Max(above, below));
    }

    public bool IsLinePixel(int x, int y, bool vertical) => Ridge(x, y, vertical) >= MinimumRidge;
}
