using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition;

/// <summary>One cell of the stash grid, and the part of it that carries a caption.</summary>
/// <param name="Bounds">The whole cell.</param>
/// <param name="Caption">
/// The top band, where the item's short label is drawn. Deliberately not the whole cell: the
/// stack count sits in the bottom-right and reading it in the same pass is another source of
/// two things on one line.
/// </param>
public sealed record StashCell(PixelRect Bounds, PixelRect Caption);

/// <summary>
/// Finds the cells of the stash grid, so each one can be read on its own.
/// </summary>
/// <remarks>
/// <para>
/// Reading a band of the grid in one pass merges neighbours. Measured on a real screenshot:
/// two adjacent captions came back as the single line <c>Mel TT @55Al1</c>, which is two items
/// and neither of them. Cropping per cell makes that structurally impossible, because the
/// neighbour is not in the picture.
/// </para>
/// <para>
/// It does not make the reading good. The same measurement had <c>TSS AP</c> come back as
/// TaSgAP, TS5,AP and TSS,AP across preparations, from a single cell with nothing adjacent to
/// blame: small light glyphs over a textured icon are hard for reasons cropping cannot help.
/// What this is for is making the next comparison a test of the engine rather than a test of
/// the merging.
/// </para>
/// <para>
/// The pitch is 63 pixels on a 1080-tall frame, and the lines land on an exact multiple: ten
/// consecutive vertical lines all satisfied x mod 63 = 19, not approximately. That regularity
/// is what makes per-cell cropping safe, because nothing drifts across the panel.
/// </para>
/// <para>
/// The phase is found rather than assumed. The panel moves with the window, so a hardcoded
/// origin is wrong the first time somebody plays windowed; and 63 is suspiciously exactly
/// 7/120 of 1080, so it is treated as a function of the frame's height rather than a constant.
/// Both were measured once, on one machine, at one size.
/// </para>
/// </remarks>
public static class StashGrid
{
    /// <summary>The cell pitch, as a fraction of the frame's height.</summary>
    /// <remarks>
    /// 63 pixels at 1080 tall, measured by autocorrelation on both axes: the strongest peak was
    /// at lag 63 for rows and for columns, so the cells are square. Expressed against height
    /// rather than width because a 32:9 frame and a 16:9 frame share a height and not a width,
    /// and storing both would be storing two numbers that have to agree.
    /// </remarks>
    public const double PitchInHeights = 63.0 / 1080.0;

    /// <summary>How much of a cell's height the caption occupies, from the top.</summary>
    /// <remarks>
    /// Roughly the top third. The caption is drawn top-right and the stack count bottom-right,
    /// and taking the whole cell would put "50" and the label in one reading.
    /// </remarks>
    private const double CaptionBand = 0.34;

    /// <summary>How much darker a line has to be than the cell to count as a line.</summary>
    /// <remarks>
    /// The grid is drawn darker than everything in it. This is the margin that separates a real
    /// grid from a phase that happens to fall on the dark parts of some icons, and below it the
    /// answer is "no grid found" rather than a confident wrong one.
    /// </remarks>
    private const double MinimumContrast = 1.08;

    public static int Pitch(CapturedImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Math.Max(2, (int)Math.Round(image.Height * PitchInHeights));
    }

    /// <summary>
    /// The cells of the grid inside a region, or nothing when no grid was found.
    /// </summary>
    /// <remarks>
    /// Nothing rather than a guess. A caller handed a made-up grid would crop arbitrary
    /// rectangles out of a picture and read whatever happened to be in them, which is the
    /// failure that looks like working.
    ///
    /// Finding the phase reads every pixel of the region once per axis, which for a whole 4K
    /// frame is sixteen million reads that nothing could interrupt. The token is checked in
    /// bounded chunks of them, so a caller's deadline stops the search partway through.
    /// </remarks>
    public static IReadOnlyList<StashCell> Cells(
        CapturedImage image,
        PixelRect region,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);
        CapturedImagePixels.Validate(image);
        cancellationToken.ThrowIfCancellationRequested();

        var pitch = Pitch(image);
        if (region.Width < pitch * 2 || region.Height < pitch * 2)
        {
            return [];
        }

        if (FindPhase(image, region, pitch, vertical: true, cancellationToken) is not { } columnPhase ||
            FindPhase(image, region, pitch, vertical: false, cancellationToken) is not { } rowPhase)
        {
            return [];
        }

        var captionHeight = Math.Max(1, (int)Math.Round(pitch * CaptionBand));
        var cells = new List<StashCell>();
        for (var top = rowPhase; top + pitch <= region.Y + region.Height; top += pitch)
        {
            for (var left = columnPhase; left + pitch <= region.X + region.Width; left += pitch)
            {
                // One pixel in on every side, so the grid line itself is never part of the
                // cell. A dark line along an edge is a stroke the reader would try to make a
                // letter out of.
                var bounds = new PixelRect(left + 1, top + 1, pitch - 2, pitch - 2);
                cells.Add(new(bounds, new(bounds.X, bounds.Y, bounds.Width, captionHeight)));
            }
        }

        return cells;
    }

    /// <summary>
    /// Where the grid lines start, by trying every offset and taking the darkest fit.
    /// </summary>
    /// <remarks>
    /// The pitch is known and only the phase is not, so this solves for one number over a range
    /// of sixty-odd rather than looking for lines. That is what makes it survive a panel full
    /// of icons: an icon can hide a line, and it cannot hide most of them.
    ///
    /// Rows are the noisier axis because icons interrupt them, which is why both axes are
    /// scored the same way and both have to clear the same contrast before any cell is
    /// returned.
    /// </remarks>
    public static int? FindPhase(
        CapturedImage image,
        PixelRect region,
        int pitch,
        bool vertical,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(region);
        cancellationToken.ThrowIfCancellationRequested();
        var span = vertical ? region.Width : region.Height;
        var origin = vertical ? region.X : region.Y;
        if (pitch < 2 || span < pitch * 2)
        {
            return null;
        }

        var means = new double[span];
        var check = new PixelCancellationCheck(cancellationToken);
        for (var offset = 0; offset < span; offset++)
        {
            means[offset] = MeanLuminance(image, region, origin + offset, vertical, ref check);
        }

        var average = means.Average();
        if (average <= 0)
        {
            return null;
        }

        var bestPhase = 0;
        var bestDarkness = double.MaxValue;
        for (var phase = 0; phase < pitch; phase++)
        {
            double total = 0;
            var count = 0;
            for (var at = phase; at < span; at += pitch)
            {
                total += means[at];
                count++;
            }

            var darkness = total / count;
            if (darkness < bestDarkness)
            {
                bestDarkness = darkness;
                bestPhase = phase;
            }
        }

        // The best phase of a picture with no grid in it is still the best phase of something.
        // Without this the answer is always yes.
        return average / Math.Max(bestDarkness, 0.0001) >= MinimumContrast ? origin + bestPhase : null;
    }

    private static double MeanLuminance(
        CapturedImage image,
        PixelRect region,
        int at,
        bool vertical,
        ref PixelCancellationCheck check)
    {
        double total = 0;
        var count = 0;
        if (vertical)
        {
            for (var y = region.Y; y < region.Y + region.Height; y++)
            {
                check.Read();
                total += CapturedImagePixels.GetLuminance(image, at, y);
                count++;
            }
        }
        else
        {
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                check.Read();
                total += CapturedImagePixels.GetLuminance(image, x, at);
                count++;
            }
        }

        return count == 0 ? 0 : total / count;
    }
}
