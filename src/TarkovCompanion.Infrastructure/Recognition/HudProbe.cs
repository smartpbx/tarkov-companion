using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// Finds the game's heads-up display in a screenshot, or reports that it was not drawn.
/// </summary>
/// <remarks>
/// <para>
/// Every number in here was measured on a real installation rather than reasoned about. The
/// first attempt looked in the bottom-right of the frame on the reasoning that most games put
/// a health widget there, and the region contained nothing but grass: the display is anchored
/// to the frame's true left edge, and the installation runs at 3840x1080, where a fraction of
/// the width is a completely different place from a fraction of the height.
/// </para>
/// <para>
/// So the search region is expressed in units of frame height from the left and bottom edges,
/// which is how a left-anchored overlay actually scales, and it is deliberately generous. The
/// element is then located by its own colour inside that region rather than at an assumed
/// offset. On three screenshots from the same machine the stamina bar landed on identical
/// pixels, so its colour finds it exactly; and if the anchoring model is ever wrong, a colour
/// search over a generous box still finds the thing, where a fixed crop would silently return
/// an empty rectangle.
/// </para>
/// <para>
/// The display is often not drawn at all. The game fades it out when nothing has changed
/// recently, and two of five in-raid screenshots on that machine had none. Absence is
/// therefore an ordinary answer with a sentence attached, not a zero.
/// </para>
/// </remarks>
public static class HudProbe
{
    /// <summary>How far right of the left edge to look, in frame heights.</summary>
    /// <remarks>
    /// The measured cluster ends by 0.19 heights. A third is generous on purpose: too wide
    /// costs a few milliseconds of scanning, too narrow returns "no display" on a frame that
    /// has one, which is the failure nobody would notice.
    /// </remarks>
    private const double SearchWidthInHeights = 0.33;

    /// <summary>How far up from the bottom edge to look, in frame heights.</summary>
    private const double SearchHeightInHeights = 0.40;

    /// <summary>
    /// The fewest bar pixels that count as a display being drawn.
    /// </summary>
    /// <remarks>
    /// Measured: 427 and 438 where the bar was drawn, 0 where it was not. There is no middle
    /// ground to split, so this only has to be above the noise a scene can produce by
    /// accident and far below the real thing.
    /// </remarks>
    private const int MinimumBarPixels = 60;

    /// <summary>The shortest run of bar colour that is a bar rather than a stray pixel.</summary>
    private const int MinimumBarLength = 16;

    public static HudReading Read(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        CapturedImagePixels.Validate(image);

        var region = SearchRegion(image);
        var rows = FindBarRows(image, region, out var total);
        if (total < MinimumBarPixels || rows.Count == 0)
        {
            return HudReading.Absent;
        }

        var bars = Merge(rows);
        if (bars.Count == 0)
        {
            return HudReading.Absent;
        }

        return new(
            true,
            bars.Count == 1
                ? $"One bar drawn, {bars[0].Length} pixels wide."
                : $"{bars.Count} bars drawn, the longest {bars[0].Length} pixels wide.")
        {
            Bars = bars,
            Bounds = Union(bars),
        };
    }

    /// <summary>
    /// Where in the frame the display could be, in units of frame height.
    /// </summary>
    /// <remarks>
    /// Deliberately not a fraction of the width. The installation this was measured on runs
    /// 32:9, where the same overlay occupies half the fraction of width it would at 16:9, and
    /// a region calibrated on either one would miss on the other. Anchoring measures from the
    /// left and bottom edges in heights, which is what the overlay itself does.
    /// </remarks>
    public static PixelRect SearchRegion(CapturedImage image)
    {
        var width = Math.Min(image.Width, (int)Math.Round(image.Height * SearchWidthInHeights));
        var height = Math.Min(image.Height, (int)Math.Round(image.Height * SearchHeightInHeights));
        return new(0, image.Height - height, width, height);
    }

    /// <summary>
    /// Whether a pixel is the colour the game draws its bars in.
    /// </summary>
    /// <remarks>
    /// Measured on real frames, not taken from a palette: a strong blue-green well clear of
    /// any scene colour. The blue-over-red margin is what keeps sky and water out, both of
    /// which are blue and neither of which is this green.
    /// </remarks>
    public static bool IsBarColour(byte red, byte green, byte blue) =>
        blue > 110 && green > 90 && red < 90 && blue - red > 55;

    private static List<(int Y, int Left, int Right, int Count)> FindBarRows(
        CapturedImage image,
        PixelRect region,
        out int total)
    {
        total = 0;
        var rows = new List<(int Y, int Left, int Right, int Count)>();
        var pixels = image.Pixels.Span;
        var bytesPerPixel = CapturedImagePixels.BytesPerPixel(image.Format);
        var redOffset = image.Format == PixelFormat.Rgba8888 ? 0 : 2;
        var blueOffset = image.Format == PixelFormat.Rgba8888 ? 2 : 0;

        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            var rowStart = y * image.Stride;
            var left = -1;
            var right = -1;
            var count = 0;
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var offset = rowStart + (x * bytesPerPixel);
                bool isBar;
                if (image.Format == PixelFormat.Gray8)
                {
                    // A grey frame cannot carry a colour, so nothing in it is a bar. Said
                    // explicitly rather than left to the comparisons below, which would read
                    // one byte as all three channels and could match by accident.
                    isBar = false;
                }
                else
                {
                    isBar = IsBarColour(pixels[offset + redOffset], pixels[offset + 1], pixels[offset + blueOffset]);
                }

                if (!isBar)
                {
                    continue;
                }

                count++;
                if (left < 0)
                {
                    left = x;
                }

                right = x;
            }

            total += count;
            if (count > 0 && right - left + 1 >= MinimumBarLength)
            {
                rows.Add((y, left, right, count));
            }
        }

        return rows;
    }

    /// <summary>
    /// Collapses adjacent rows of bar colour into the bars they belong to.
    /// </summary>
    /// <remarks>
    /// The display draws more than one bar and they sit a few pixels apart, so a run of
    /// consecutive rows is one bar and a gap starts the next. Returned longest first, because
    /// a caller asking for "the bar" means the one that is actually showing something.
    /// </remarks>
    private static List<HudBar> Merge(List<(int Y, int Left, int Right, int Count)> rows)
    {
        var bars = new List<HudBar>();
        var index = 0;
        while (index < rows.Count)
        {
            var start = index;
            while (index + 1 < rows.Count && rows[index + 1].Y == rows[index].Y + 1)
            {
                index++;
            }

            var left = int.MaxValue;
            var right = int.MinValue;
            var filled = 0;
            for (var row = start; row <= index; row++)
            {
                left = Math.Min(left, rows[row].Left);
                right = Math.Max(right, rows[row].Right);
                filled += rows[row].Count;
            }

            bars.Add(new(
                new(left, rows[start].Y, right - left + 1, rows[index].Y - rows[start].Y + 1),
                filled));
            index++;
        }

        return [.. bars.OrderByDescending(bar => bar.Length)];
    }

    private static PixelRect Union(IReadOnlyList<HudBar> bars)
    {
        var left = bars.Min(bar => bar.Bounds.X);
        var top = bars.Min(bar => bar.Bounds.Y);
        var right = bars.Max(bar => bar.Bounds.X + bar.Bounds.Width);
        var bottom = bars.Max(bar => bar.Bounds.Y + bar.Bounds.Height);
        return new(left, top, right - left, bottom - top);
    }
}
