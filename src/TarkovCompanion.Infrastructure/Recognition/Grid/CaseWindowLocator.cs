using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>
/// Finds the grid of the case window open in front of the stash: an ammo case, a key tool, a Junk
/// box. Returns null when no case window is open.
/// </summary>
/// <remarks>
/// <para>
/// A case sub-scan asks for the case, not the stash behind it. The general line detector cannot
/// tell them apart: measured on the two real screenshots with a 14x14 Junk box open (2026-09-18,
/// frames 7 and 8), it returned an 18x13 and a 20x13 lattice that reached past the window, lost
/// its title row, and so placed 37 of 47 and 84 of 93 labelled items, with 44 and 2 footprints
/// outside the window. With this frame: 47/47 and 93/93, none outside
/// (<c>RealStashNamingMeasurementTests</c>).
/// </para>
/// <para>
/// The open window is drawn with a one-pixel gold frame, (231, 201, 97) on both frames, and
/// nothing else on the inventory screen is a straight gold line several cells long. The top and
/// bottom edges start at the same column and the left edge joins them; the right edge can be
/// hidden by the scrollbar (frame 7), so it is not required. Inside the frame the grid sits
/// 5 pixels in from the sides and 27 down from the top, under the title bar, at the stash's
/// own pitch (both frames, at 1080 lines; scaled with the pitch elsewhere).
/// </para>
/// </remarks>
public sealed class CaseWindowLocator
{
    private const double InsetInPitches = 5.0 / 63.0;
    private const double TitleInPitches = 27.0 / 63.0;

    /// <summary>The shortest frame edge, in cells, worth calling a window: a key tool is four wide.</summary>
    private const int MinimumEdgeCells = 3;

    /// <summary>The share of the left edge that must be drawn: an item's tooltip may cross it.</summary>
    private const double MinimumLeftEdgeShare = 0.8;

    public ContainerGridSpec? Locate(CapturedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        CapturedImagePixels.Validate(image, CapturedImagePixels.MaximumPixels);
        if (image.Format == PixelFormat.Gray8)
        {
            return null;
        }

        var pitch = StashGrid.Pitch(image);
        var minimumRun = pitch * MinimumEdgeCells;
        var runs = new List<(int Y, int Start, int End)>();
        for (var y = 0; y < image.Height; y++)
        {
            if ((y & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var run = 0;
            for (var x = 0; x <= image.Width; x++)
            {
                if (x < image.Width && IsGold(image, x, y))
                {
                    run++;
                    continue;
                }

                // A thicker line is one edge: keep only its first row.
                if (run >= minimumRun && !runs.Any(existing => existing.Y == y - 1 && Math.Abs(existing.Start - (x - run)) <= 2))
                {
                    runs.Add((y, x - run, x - 1));
                }

                run = 0;
            }
        }

        ContainerGridSpec? best = null;
        var bestArea = 0L;
        foreach (var top in runs)
        {
            foreach (var bottom in runs)
            {
                if (bottom.Y - top.Y < pitch * 2 || Math.Abs(bottom.Start - top.Start) > 3 ||
                    LeftEdgeShare(image, top.Start, top.Y, bottom.Y) < MinimumLeftEdgeShare)
                {
                    continue;
                }

                var right = Math.Max(top.End, bottom.End);
                var inset = (int)Math.Round(pitch * InsetInPitches);
                var x0 = top.Start + inset;
                var y0 = top.Y + (int)Math.Round(pitch * TitleInPitches);
                var columns = (right - x0) / pitch;
                var rows = (bottom.Y - y0) / pitch;
                var area = (long)columns * rows;
                if (columns < 1 || rows < 1 || area <= bestArea)
                {
                    continue;
                }

                bestArea = area;
                best = new ContainerGridSpec(new PixelRect(x0, y0, columns * pitch, rows * pitch), columns, rows);
            }
        }

        return best;
    }

    private static double LeftEdgeShare(CapturedImage image, int x, int top, int bottom)
    {
        var drawn = 0;
        for (var y = top; y <= bottom; y++)
        {
            drawn += IsGold(image, x, y) || (x + 1 < image.Width && IsGold(image, x + 1, y)) ? 1 : 0;
        }

        return drawn / (double)(bottom - top + 1);
    }

    private static bool IsGold(CapturedImage image, int x, int y)
    {
        var offset = (y * image.Stride) + (x * 4);
        var pixels = image.Pixels.Span;
        var (red, green, blue) = image.Format == PixelFormat.Bgra8888
            ? (pixels[offset + 2], pixels[offset + 1], pixels[offset])
            : (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
        return red > 190 && green > 160 && blue < 140 && red - blue > 90;
    }
}
