using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>
/// Finds the stash panel's lattice by solving for where it is, not for what it is.
/// </summary>
/// <remarks>
/// <para>
/// The general line detector looks for any regular run of contrast, and on a packed stash the most
/// regular thing on screen is the artwork. Measured on a painted 34-row stash (package 40): it
/// returned 25-pixel and 37-pixel lattices for frames whose cells are 63 pixels, and nothing
/// downstream survived that.
/// </para>
/// <para>
/// The stash is the one grid whose shape is already known. It is ten columns wide and its cells
/// are 63 pixels on a 1080-tall frame (<see cref="StashGrid.PitchInHeights"/>, measured on a real
/// screenshot), so the only unknowns are the panel's left edge, the row phase, and how many whole
/// rows are showing. Eleven lines at an exact pitch are scored together, which is what lets an
/// item hide some of them without moving the answer - the same reasoning as
/// <see cref="StashGrid.FindPhase"/>, extended to find the panel rather than be told it.
/// </para>
/// <para>
/// A line is a single pixel standing clear of the pixels two away on both sides, in either
/// polarity (<see cref="StashLuminancePlane"/>), and a column or row is scored by the share of
/// its length that is such a pixel. The first version scored the mean luminance of a whole
/// column against columns three away; on real screenshots (2026-09-18) that found nothing on a
/// screen of large cases, whose few borders vanish in a column mean.
/// </para>
/// <para>
/// The real inventory screen has other grids at the stash's own pitch: a rig, pockets, a
/// backpack, and an opened case in front of everything. Measured on those screenshots, the first
/// version took a ten-column window out of a fourteen-column Junk case on both screens that had
/// one open. So a candidate must have both of its outer edges drawn, and is refused when the comb
/// carries on at the same strength one pitch beyond either edge: a wider grid is not the stash,
/// and a panel whose left columns are behind a case has no left edge to find.
/// </para>
/// <para>
/// Only whole rows are returned. The viewport cuts the last row, and at the bottom of the stash
/// the first, by any amount - 47 of 63 pixels were showing on one real screen, which the first
/// version's half-row test accepted. The rows now have to lie inside the vertical run over which
/// the outer edges are drawn; art or a tooltip hiding an edge for a row does not end the run.
/// </para>
/// </remarks>
public sealed class StashPanelLatticeDetector
{
    /// <summary>The stash is ten cells wide in every edition of the game.</summary>
    public const int Columns = 10;

    /// <summary>The least share of the frame's height an outer edge must be drawn over.</summary>
    /// <remarks>Three rows of a 1080-tall frame: less than that is not a panel worth reading.</remarks>
    private const double MinimumEdgeShare = 0.17;

    /// <summary>How strong a line one pitch outside the panel may be, against the weaker edge.</summary>
    private const double MaximumContinuation = 0.6;

    /// <summary>The longest stretch, in pixels, both edges may be hidden for without ending the panel.</summary>
    private const int MaximumEdgeGap = 8;

    /// <summary>The share of the panel's width a row must be a line over to be the viewport's top frame.</summary>
    private const double MinimumFrameLine = 0.6;

    /// <summary>How much brighter than the gutter the panel must stand for its edge to count as drawn.</summary>
    private const int MinimumEdgeStep = 15;

    /// <summary>The least share of the viewport's height over which both outer edges are drawn.</summary>
    private const double MinimumBothEdgesShare = 0.5;

    public ContainerGridSpec? Detect(CapturedImage image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        CapturedImagePixels.Validate(image, CapturedImagePixels.MaximumPixels);
        cancellationToken.ThrowIfCancellationRequested();

        var pitch = StashGrid.Pitch(image);
        var panelWidth = pitch * Columns;
        if (image.Width < panelWidth + 5 || image.Height < pitch * 3)
        {
            return null;
        }

        var plane = StashLuminancePlane.From(image, cancellationToken);
        var columnShare = new double[image.Width];
        for (var x = 2; x < image.Width - 2; x++)
        {
            if ((x & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var lines = 0;
            for (var y = 0; y < image.Height; y += 2)
            {
                if (plane.IsLinePixel(x, y, vertical: true))
                {
                    lines++;
                }
            }

            columnShare[x] = lines / (image.Height / 2.0);
        }

        if (FindPanelLeft(columnShare, pitch) is not { } left)
        {
            return null;
        }

        // The viewport is the vertical run over which the outer edges are drawn. A tooltip hides
        // one edge for a row or two, so the run carries on while either is drawn; but both have
        // to be drawn over most of it, which a panel half behind an opened case cannot offer.
        var right = left + panelWidth;
        var leftDrawn = EdgeDrawn(plane, left, inside: 1);
        var rightDrawn = EdgeDrawn(plane, right, inside: -1);
        var top = -1;
        var bottom = -1;
        var bestLength = 0;
        var runTop = -1;
        var lastDrawn = -1;
        var bothInRun = 0;
        for (var y = 0; y <= image.Height; y++)
        {
            var either = y < image.Height && (leftDrawn[y] || rightDrawn[y]);
            if (either)
            {
                if (runTop < 0)
                {
                    runTop = y;
                    bothInRun = 0;
                }

                lastDrawn = y;
                if (leftDrawn[y] && rightDrawn[y])
                {
                    bothInRun++;
                }
            }

            if (runTop >= 0 && (y == image.Height || y - lastDrawn > MaximumEdgeGap))
            {
                var length = lastDrawn - runTop;
                if (length > bestLength && bothInRun >= length * MinimumBothEdgesShare)
                {
                    bestLength = length;
                    top = runTop;
                    bottom = lastDrawn;
                }

                runTop = -1;
            }
        }

        if (bestLength < pitch * 2)
        {
            return null;
        }

        var rowShare = RowShare(plane, left, panelWidth, top, bottom, pitch);
        if (FindRowPhase(rowShare, top, bottom, pitch) is not { } rowLattice)
        {
            return null;
        }

        var phase = rowLattice.Phase;

        // The panel's frame starts in the header above the viewport, so the run of drawn edges
        // begins above the first row. The viewport's own top is a line across the whole panel
        // (0.66 to 0.89 of its width on the real screens, where header text reads 0.1 to 0.2),
        // and the first whole row is the first lattice line at or below it.
        var viewportTop = top;
        for (var y = Math.Max(1, top - 5); y <= Math.Min(rowShare.Length - 1, top + pitch); y++)
        {
            if (rowShare[y] >= MinimumFrameLine)
            {
                viewportTop = y;
                break;
            }
        }

        var firstLine = phase;
        while (firstLine < viewportTop - 2)
        {
            firstLine += pitch;
        }

        // The bottom needs no such anchor: the run ends where the viewport does, and large items
        // cut by it cross the last whole row's bottom line and hide it.
        var rows = (bottom + 2 - firstLine) / pitch;
        return rows < 2
            ? null
            : new(new PixelRect(left, firstLine, panelWidth, rows * pitch), Columns, rows);
    }

    private static int? FindPanelLeft(double[] share, int pitch)
    {
        double At(int x)
        {
            if (x < 1 || x >= share.Length - 1)
            {
                return 0;
            }

            return Math.Max(share[x], Math.Max(share[x - 1], share[x + 1]));
        }

        var span = pitch * Columns;
        var best = -1;
        var bestScore = 0d;
        for (var left = 2; left + span < share.Length - 2; left++)
        {
            var leftEdge = share[left];
            var rightEdge = At(left + span);
            var weaker = Math.Min(leftEdge, rightEdge);
            if (weaker < MinimumEdgeShare)
            {
                continue;
            }

            if (At(left - pitch) > weaker * MaximumContinuation || At(left + span + pitch) > weaker * MaximumContinuation)
            {
                continue;
            }

            var score = 0d;
            for (var line = 0; line <= Columns; line++)
            {
                score += At(left + (line * pitch));
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = left;
            }
        }

        return best < 0 ? null : best;
    }

    /// <summary>
    /// Whether the panel's outer edge at <paramref name="x"/> is drawn at each row: a line within
    /// a pixel either way, or the panel standing brighter than the gutter beside it.
    /// </summary>
    /// <remarks>
    /// A bright or pale tile against the outer edge leaves no ridge there - its own background is
    /// as bright as the border - but it does leave a step up from the dark gutter, and the header
    /// above the panel, whose bright button sits outside the edge, steps the other way.
    /// </remarks>
    private static bool[] EdgeDrawn(StashLuminancePlane plane, int x, int inside)
    {
        var drawn = new bool[plane.Height];
        for (var y = 0; y < plane.Height; y++)
        {
            var within = (plane.At(x + (3 * inside), y) + plane.At(x + (4 * inside), y) + plane.At(x + (5 * inside), y)) / 3;
            var beyond = (plane.At(x - (3 * inside), y) + plane.At(x - (4 * inside), y) + plane.At(x - (5 * inside), y)) / 3;
            drawn[y] = within - beyond >= MinimumEdgeStep ||
                       plane.IsLinePixel(x, y, vertical: true) ||
                       plane.IsLinePixel(x - 1, y, vertical: true) ||
                       plane.IsLinePixel(x + 1, y, vertical: true);
        }

        return drawn;
    }

    /// <summary>For each row of the frame near the panel, the share of the panel's width that is a horizontal line.</summary>
    private static double[] RowShare(StashLuminancePlane plane, int left, int panelWidth, int top, int bottom, int pitch)
    {
        var share = new double[plane.Height];
        for (var y = Math.Max(2, top - pitch); y <= Math.Min(plane.Height - 3, bottom + pitch); y++)
        {
            var lines = 0;
            for (var x = left + 3; x < left + panelWidth - 2; x += 2)
            {
                if (plane.IsLinePixel(x, y, vertical: false))
                {
                    lines++;
                }
            }

            share[y] = lines / (panelWidth / 2.0);
        }

        return share;
    }

    /// <summary>The row phase whose lattice lines are best drawn, and how well drawn they typically are.</summary>
    private static (int Phase, double TypicalLine)? FindRowPhase(double[] share, int top, int bottom, int pitch)
    {
        var best = -1;
        var bestScore = 0d;
        for (var phase = 0; phase < pitch; phase++)
        {
            var score = 0d;
            var count = 0;
            for (var y = top - (top % pitch) + phase - pitch; y <= bottom; y += pitch)
            {
                if (y < top - 2 || y < 0)
                {
                    continue;
                }

                score += share[y];
                count++;
            }

            if (count > 0 && score / count > bestScore)
            {
                bestScore = score / count;
                best = phase;
            }
        }

        return best < 0 || bestScore < 0.15 ? null : (best, bestScore);
    }
}
