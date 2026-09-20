using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>
/// A small colour picture of an icon, reduced the same way whatever size it arrived at, that two
/// icons can be compared by.
/// </summary>
/// <remarks>
/// <para>
/// The 64-bit difference hash is a shortlist, not an answer. Measured against the 5,320 grid
/// images json.tarkov.dev publishes, 42 of 353 items share their nearest hash with another item
/// outright, and after a 1080p frame is resampled to 1440p a hash-only match at "within 8 bits,
/// 4 clear of the runner-up" names the wrong item once in 155. Sixty-four bits of luminance
/// gradient cannot tell one ammunition box from the next.
/// </para>
/// <para>
/// This keeps colour and about 250 times as many numbers, so it can. The comparison is a
/// zero-mean normalised correlation, which does not move when a frame is uniformly darker or
/// flatter than the reference. The outermost ring is left out because it is the cell border,
/// which every icon has and which therefore says nothing about which icon this is.
/// </para>
/// </remarks>
public sealed class IconPixelDescriptor
{
    /// <summary>Descriptor pixels per grid cell along each axis.</summary>
    public const int PixelsPerCell = 16;

    private readonly float[] _values;

    private IconPixelDescriptor(float[] values, int widthCells, int heightCells)
    {
        _values = values;
        WidthCells = widthCells;
        HeightCells = heightCells;
    }

    public int WidthCells { get; }

    public int HeightCells { get; }

    /// <summary>
    /// Descriptor rows left out at the top and at the bottom when the game's own writing is
    /// masked: the caption band and the band that holds the stack count, the durability figure
    /// and the found-in-raid tick. A quarter of a cell each.
    /// </summary>
    public const int MaskedBandRows = PixelsPerCell / 4;

    /// <summary>Describes an image known to span the given number of cells.</summary>
    public static IconPixelDescriptor? Create(CapturedImage image, int widthCells, int heightCells) =>
        Create(image, widthCells, heightCells, maskGameWriting: false);

    /// <summary>
    /// Describes an image, optionally leaving out the two bands the game writes over an icon.
    /// </summary>
    /// <remarks>
    /// The reference art carries a caption in its own font and nothing else. A real cell carries
    /// the game's caption, a stack count or durability figure, and a found-in-raid tick, all in
    /// bright pixels. On a bright item that is a detail; on a dark one (an attachment, a bolt, a
    /// phone) it is most of the picture's variance, and measured on real screenshots it is what
    /// drags a true match from 0.9 down to 0.4.
    /// </remarks>
    public static IconPixelDescriptor? Create(CapturedImage image, int widthCells, int heightCells, bool maskGameWriting)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (widthCells < 1 || heightCells < 1 || widthCells > 16 || heightCells > 16 ||
            image.Format == PixelFormat.Gray8)
        {
            return null;
        }

        var targetWidth = widthCells * PixelsPerCell;
        var targetHeight = heightCells * PixelsPerCell;
        if (image.Width < targetWidth || image.Height < targetHeight)
        {
            return null;
        }

        var redOffset = image.Format == PixelFormat.Rgba8888 ? 0 : 2;
        var blueOffset = image.Format == PixelFormat.Rgba8888 ? 2 : 0;
        var pixels = image.Pixels.Span;
        var firstRow = maskGameWriting ? MaskedBandRows : 1;
        var lastRow = maskGameWriting ? targetHeight - MaskedBandRows : targetHeight - 1;
        var innerWidth = targetWidth - 2;
        var innerHeight = lastRow - firstRow;
        var values = new float[innerWidth * innerHeight * 3];
        var index = 0;
        double sum = 0;
        for (var ty = firstRow; ty < lastRow; ty++)
        {
            var top = (image.Height * ty) / targetHeight;
            var bottom = Math.Max(top + 1, (image.Height * (ty + 1)) / targetHeight);
            for (var tx = 1; tx < targetWidth - 1; tx++)
            {
                var left = (image.Width * tx) / targetWidth;
                var right = Math.Max(left + 1, (image.Width * (tx + 1)) / targetWidth);
                long red = 0;
                long green = 0;
                long blue = 0;
                for (var y = top; y < bottom; y++)
                {
                    var offset = (y * image.Stride) + (left * 4);
                    for (var x = left; x < right; x++)
                    {
                        red += pixels[offset + redOffset];
                        green += pixels[offset + 1];
                        blue += pixels[offset + blueOffset];
                        offset += 4;
                    }
                }

                var count = (float)((bottom - top) * (right - left));
                values[index++] = red / count;
                values[index++] = green / count;
                values[index++] = blue / count;
                sum += (red + green + blue) / count;
            }
        }

        var mean = (float)(sum / values.Length);
        double energy = 0;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] -= mean;
            energy += values[i] * values[i];
        }

        if (energy < 1e-3)
        {
            // A flat picture has no pattern to correlate; it matches nothing rather than everything.
            return null;
        }

        var norm = (float)Math.Sqrt(energy);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= norm;
        }

        return new(values, widthCells, heightCells);
    }

    /// <summary>Correlation between minus one and one; one is the same picture.</summary>
    public double Correlate(IconPixelDescriptor other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other._values.Length != _values.Length)
        {
            return -1;
        }

        double dot = 0;
        for (var i = 0; i < _values.Length; i++)
        {
            dot += _values[i] * other._values[i];
        }

        return dot;
    }
}
