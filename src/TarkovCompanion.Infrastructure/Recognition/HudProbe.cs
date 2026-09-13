using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>
/// Finds the game's heads-up display in a screenshot, or reports that it was not drawn.
/// </summary>
/// <remarks>
/// <para>
/// Every number in here was measured on a real installation rather than reasoned about. The
/// first attempt looked in the bottom-right of the frame on the reasoning that most games put a
/// health widget there, and the region contained nothing but grass: the display is anchored to
/// the frame's true left edge, and that installation runs at 3840x1080, where a fraction of the
/// width is a completely different place from a fraction of the height.
/// </para>
/// <para>
/// So the search region is expressed in units of frame height from the left and bottom edges,
/// which is how a left-anchored overlay actually scales, and it is deliberately generous. The
/// bars are then located by their own colour inside that region rather than at an assumed
/// offset. On every frame measured they landed on identical pixels, so colour finds them
/// exactly; and if the anchoring model is ever wrong, a colour search over a generous box still
/// finds the thing, where a fixed crop would silently return an empty rectangle.
/// </para>
/// <para>
/// There are two bars, three pixels tall, six pixels apart, and they are not the same colour: a
/// vertical slice found the upper at 43,129,151 and the lower at 43,151,129. One predicate
/// catches both and would report them as one element, so they are separated by which of blue
/// and green is on top.
/// </para>
/// <para>
/// The display is often not drawn at all. The game fades it out when nothing has changed
/// recently, and 35 of 261 in-raid screenshots on that machine had none. Absence is therefore
/// an ordinary answer with a sentence attached, not a zero.
/// </para>
/// <para>
/// What is deliberately not here is a limb verdict. The silhouette beside these bars is
/// measured and refused instead: see <see cref="SilhouetteLegibility"/> for the numbers and for
/// what a classifier run over these frames actually returns, which is a confident wrong answer
/// in both directions rather than a failure.
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

    /// <summary>The shortest run of bar colour that is a bar rather than a stray pixel.</summary>
    /// <remarks>
    /// The real bars run 146 pixels at 1080 tall. Scene noise that happens to be blue-green
    /// comes in ones and twos, and a leaf-sized patch does not run sixteen pixels straight.
    /// </remarks>
    private const int MinimumBarLength = 16;

    public static HudReading Read(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        CapturedImagePixels.Validate(image);

        var region = SearchRegion(image);
        var rows = FindBarRows(image, region);
        if (rows.Count == 0)
        {
            return HudReading.Absent;
        }

        var bars = Merge(rows);
        if (bars.Count == 0)
        {
            return HudReading.Absent;
        }

        return new(true, Describe(bars))
        {
            Bars = bars,
            Bounds = Union(bars),
            Silhouette = ReadSilhouette(image),
        };
    }

    /// <summary>
    /// Where in the frame the display could be, in units of frame height.
    /// </summary>
    /// <remarks>
    /// Deliberately not a fraction of the width. The installation this was measured on runs
    /// 32:9, where the same overlay occupies half the fraction of width it would at 16:9, and a
    /// region calibrated on either one would miss on the other. Measuring from the left and
    /// bottom edges in heights is what the overlay itself scales by.
    /// </remarks>
    public static PixelRect SearchRegion(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var width = Math.Min(image.Width, (int)Math.Round(image.Height * SearchWidthInHeights));
        var height = Math.Min(image.Height, (int)Math.Round(image.Height * SearchHeightInHeights));
        return new(0, image.Height - height, width, height);
    }

    /// <summary>
    /// Where the body silhouette sits, above the bars and anchored the same way.
    /// </summary>
    /// <remarks>
    /// Measured at x 55..180 and y 837..997 on a 3840x1080 frame, which in units of frame
    /// height from the left and bottom edges is 0.051..0.167 across and 0.077..0.225 up. The
    /// box here is a little wider than that on each side and still stops short of the bars,
    /// which start 30 pixels below the figure's feet.
    ///
    /// Not measured relative to the bars, although they are right underneath: a bar's length is
    /// the thing that changes as the player drains it, so anchoring to it would move the
    /// silhouette every time they ran.
    /// </remarks>
    public static PixelRect SilhouetteRegion(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var left = (int)Math.Round(image.Height * 0.04);
        var right = Math.Min(image.Width, (int)Math.Round(image.Height * 0.19));
        var top = image.Height - (int)Math.Round(image.Height * 0.25);
        var bottom = image.Height - (int)Math.Round(image.Height * 0.06);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>
    /// Measures how bright the silhouette's outline is, and refuses rather than guesses.
    /// </summary>
    /// <remarks>
    /// The brightest pixel in the region is the whole measurement. A classifier needs the
    /// outline to stand clear of the background, and if the brightest thing present is dimmer
    /// than the threshold it would have to clear, there is nothing to classify and every
    /// verdict it returns is about the scenery. Measured on real frames: 83 over grass, 147
    /// over a sand bank, against the 150 it needs.
    /// </remarks>
    public static SilhouetteLegibility ReadSilhouette(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        CapturedImagePixels.Validate(image);

        var region = SilhouetteRegion(image);
        var brightest = 0;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var luminance = CapturedImagePixels.GetLuminance(image, x, y);
                if (luminance > brightest)
                {
                    brightest = luminance;
                }
            }
        }

        return brightest >= SilhouetteLegibility.RequiredLuminance
            ? new(
                true,
                brightest,
                $"The silhouette's outline reaches {brightest} here, which is bright enough to tell a limb from its background.")
            : new(
                false,
                brightest,
                $"The silhouette's outline only reaches {brightest} in this screenshot and telling a limb from " +
                $"its background needs about {SilhouetteLegibility.RequiredLuminance}, so limb health is not " +
                "readable from it. Reporting nothing rather than a guess: run over frames this dim, every way " +
                "of reading a limb answers confidently and wrongly.");
    }

    /// <summary>
    /// Which bar a pixel belongs to, or none.
    /// </summary>
    /// <remarks>
    /// Measured on real frames, not taken from a palette: both bars are a strong blue-green well
    /// clear of any scene colour, and they differ only in which of the two leads. The
    /// blue-over-red margin is what keeps sky and water out, both of which are blue and neither
    /// of which is this green.
    /// </remarks>
    public static HudBarKind? Classify(byte red, byte green, byte blue)
    {
        if (red >= 90 || blue <= 110 || green <= 90 || blue - red <= 55)
        {
            return null;
        }

        // Equal channels belong to neither and are not produced by either bar; returning one of
        // them arbitrarily would put an antialiased pixel in whichever came first in the enum.
        return blue == green ? null : blue > green ? HudBarKind.Blue : HudBarKind.Green;
    }

    private static string Describe(IReadOnlyList<HudBar> bars) => bars.Count == 1
        ? $"One bar drawn, {bars[0].Length} pixels wide."
        : $"{bars.Count} bars drawn, the longest {bars[0].Length} pixels wide.";

    private static List<(HudBarKind Kind, int Y, int Left, int Right)> FindBarRows(
        CapturedImage image,
        PixelRect region)
    {
        var rows = new List<(HudBarKind Kind, int Y, int Left, int Right)>();
        if (image.Format == PixelFormat.Gray8)
        {
            // A grey buffer has one byte per pixel and cannot carry a colour. Said here rather
            // than left to the comparisons below, which would read one byte as all three
            // channels and could match by accident.
            return rows;
        }

        var pixels = image.Pixels.Span;
        var bytesPerPixel = CapturedImagePixels.BytesPerPixel(image.Format);
        var redOffset = image.Format == PixelFormat.Rgba8888 ? 0 : 2;
        var blueOffset = image.Format == PixelFormat.Rgba8888 ? 2 : 0;

        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            var rowStart = y * image.Stride;
            foreach (var kind in (ReadOnlySpan<HudBarKind>)[HudBarKind.Blue, HudBarKind.Green])
            {
                var left = -1;
                var right = -1;
                for (var x = region.X; x < region.X + region.Width; x++)
                {
                    var offset = rowStart + (x * bytesPerPixel);
                    if (Classify(pixels[offset + redOffset], pixels[offset + 1], pixels[offset + blueOffset]) != kind)
                    {
                        continue;
                    }

                    if (left < 0)
                    {
                        left = x;
                    }

                    right = x;
                }

                if (left >= 0 && right - left + 1 >= MinimumBarLength)
                {
                    rows.Add((kind, y, left, right));
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// Collapses adjacent rows of one colour into the bar they belong to.
    /// </summary>
    /// <remarks>
    /// Each bar is three rows tall, so a run of consecutive rows of the same colour is one bar
    /// and a gap or a change of colour starts the next. Returned longest first, because a caller
    /// asking for "the bar" means the one that is actually showing something.
    /// </remarks>
    private static List<HudBar> Merge(List<(HudBarKind Kind, int Y, int Left, int Right)> rows)
    {
        var bars = new List<HudBar>();
        foreach (var group in rows.GroupBy(row => row.Kind))
        {
            var ordered = group.OrderBy(row => row.Y).ToArray();
            var index = 0;
            while (index < ordered.Length)
            {
                var start = index;
                while (index + 1 < ordered.Length && ordered[index + 1].Y == ordered[index].Y + 1)
                {
                    index++;
                }

                var left = int.MaxValue;
                var right = int.MinValue;
                for (var row = start; row <= index; row++)
                {
                    left = Math.Min(left, ordered[row].Left);
                    right = Math.Max(right, ordered[row].Right);
                }

                bars.Add(new(
                    group.Key,
                    new(left, ordered[start].Y, right - left + 1, ordered[index].Y - ordered[start].Y + 1)));
                index++;
            }
        }

        return [.. bars.OrderByDescending(bar => bar.Length).ThenBy(bar => bar.Bounds.Y)];
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
