using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>Reads the stash scrollbar thumb only when its bright vertical handle is unambiguous.</summary>
public static class StashScrollbarReader
{
    private const int MinimumThumbHeight = 8;
    private const int MaximumThumbHeight = 256;

    public static double? Read(
        CapturedImage image,
        ContainerGridSpec grid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(grid);
        var left = grid.Bounds.X + grid.Bounds.Width + 3;
        var right = Math.Min(image.Width, left + 28);
        var top = Math.Max(0, grid.Bounds.Y);
        var bottom = Math.Min(image.Height, grid.Bounds.Y + grid.Bounds.Height);
        if (right - left < 3 || bottom - top < MinimumThumbHeight * 2)
        {
            return null;
        }

        var bright = new bool[bottom - top];
        var pixels = image.Pixels.Span;
        for (var y = top; y < bottom; y++)
        {
            if ((y & 127) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var count = 0;
            for (var x = left; x < right; x++)
            {
                var offset = (y * image.Stride) + (x * 4);
                var blue = pixels[offset];
                var green = pixels[offset + 1];
                var red = pixels[offset + 2];
                var luminance = (red + (green * 2) + blue) / 4;
                if (luminance >= 80)
                {
                    count++;
                }
            }

            bright[y - top] = count >= 3;
        }

        var runs = new List<(int Start, int End)>();
        for (var index = 0; index < bright.Length;)
        {
            if (!bright[index])
            {
                index++;
                continue;
            }

            var start = index;
            while (index < bright.Length && bright[index])
            {
                index++;
            }

            var length = index - start;
            if (length is >= MinimumThumbHeight and <= MaximumThumbHeight)
            {
                runs.Add((start, index - 1));
            }
        }

        if (runs.Count == 0)
        {
            return null;
        }

        var ranked = runs.OrderByDescending(run => run.End - run.Start).ToArray();
        if (ranked.Length > 1 && ranked[0].End - ranked[0].Start < ranked[1].End - ranked[1].Start + 3)
        {
            return null;
        }

        var thumb = ranked[0];
        var center = (thumb.Start + thumb.End) / 2d;
        return Math.Clamp(center / (bright.Length - 1d), 0, 1);
    }
}
