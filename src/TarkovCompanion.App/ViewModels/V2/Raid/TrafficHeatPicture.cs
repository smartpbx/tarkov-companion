using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using TarkovCompanion.Application.Services.Strategy.Prior;

namespace TarkovCompanion.App.ViewModels.V2.Raid;

/// <summary>[Issue 286] A modelled traffic field as a transparent picture to lay over the plan.</summary>
/// <remarks>
/// Four pixels to a cell, interpolated here rather than left to the stretch: a field is sixty-four
/// cells across, and stretched twenty times by the compositor its cells show as squares. Quiet
/// ground is fully transparent, so the layer never greys the whole map to say "nothing here".
/// </remarks>
internal static class TrafficHeatPicture
{
    private const int PixelsPerCell = 4;

    /// <summary>Below this the field is drawn as nothing at all.</summary>
    internal const double Floor = 0.25;

    private const double MaximumAlpha = 0.7;

    public static WriteableBitmap Draw(TrafficField field)
    {
        // The plan's extent, not the grid's: see TrafficField.Width.
        var width = Math.Max(1, (int)Math.Round(field.Width / field.Cell * PixelsPerCell));
        var height = Math.Max(1, (int)Math.Round(field.Height / field.Cell * PixelsPerCell));
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (red, green, blue, alpha) = Colour(Sample(
                    field,
                    ((x + 0.5) / width * field.Width / field.Cell) - 0.5,
                    ((y + 0.5) / height * field.Height / field.Cell) - 0.5));
                var offset = ((y * width) + x) * 4;
                pixels[offset] = (byte)(blue * alpha);
                pixels[offset + 1] = (byte)(green * alpha);
                pixels[offset + 2] = (byte)(red * alpha);
                pixels[offset + 3] = (byte)(255 * alpha);
            }
        }

        using var locked = bitmap.Lock();
        for (var y = 0; y < height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, y * width * 4, locked.Address + (y * locked.RowBytes), width * 4);
        }

        return bitmap;
    }

    /// <summary>Amber through orange to red, and how opaque: the legend draws the same ramp.</summary>
    internal static (double Red, double Green, double Blue, double Alpha) Colour(double value)
    {
        if (!(value > Floor))
        {
            return (0, 0, 0, 0);
        }

        var t = Math.Clamp((value - Floor) / (1 - Floor), 0, 1);
        var (red, green, blue) = t < 0.5
            ? (255d, 196 - (76 * t * 2), 70 - (34 * t * 2))
            : (255 - (35 * (t - 0.5) * 2), 120 - (76 * (t - 0.5) * 2), 36 - (2 * (t - 0.5) * 2));
        return (red, green, blue, MaximumAlpha * Math.Pow(t, 0.75));
    }

    private static double Sample(TrafficField field, double column, double row)
    {
        var x0 = Math.Clamp((int)Math.Floor(column), 0, field.Columns - 1);
        var y0 = Math.Clamp((int)Math.Floor(row), 0, field.Rows - 1);
        var x1 = Math.Min(x0 + 1, field.Columns - 1);
        var y1 = Math.Min(y0 + 1, field.Rows - 1);
        var fx = Math.Clamp(column - x0, 0, 1);
        var fy = Math.Clamp(row - y0, 0, 1);
        var top = (field[x0, y0] * (1 - fx)) + (field[x1, y0] * fx);
        var bottom = (field[x0, y1] * (1 - fx)) + (field[x1, y1] * fx);
        return (top * (1 - fy)) + (bottom * fy);
    }
}
