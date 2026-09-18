using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Core.Domain.Recognition.Grid;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>
/// Says, for every pair of neighbouring cells, whether a border line is drawn between them.
/// </summary>
/// <remarks>
/// <para>
/// This is what decides where one item ends and the next begins. The game draws a border round
/// every item and round every empty cell, and nothing inside an item, so two cells with no line
/// between them are the same item and two cells with a line between them are not.
/// </para>
/// <para>
/// The builder used to join every occupied cell that touched another. A container is packed, so
/// that read two bandages side by side as one 2x1 item nobody has ever seen, and then split
/// anything whose corner looked empty into single cells. Measured on composed frames it got 347
/// footprints right out of 528 items; the lines are right there in the picture to be read.
/// </para>
/// </remarks>
internal static class GridBorderProbe
{
    /// <summary>The share of an edge that has to show a line for the edge to count as one.</summary>
    private const double MinimumLineShare = 0.5;

    public static GridBorders Measure(CapturedImage image, DetectedGridLattice lattice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(lattice);
        var right = new bool[lattice.Rows, lattice.Columns];
        var below = new bool[lattice.Rows, lattice.Columns];
        for (var row = 0; row < lattice.Rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < lattice.Columns; column++)
            {
                var left = lattice.Bounds.X + (column * lattice.CellWidthPixels);
                var top = lattice.Bounds.Y + (row * lattice.CellHeightPixels);
                right[row, column] = column + 1 >= lattice.Columns || HasLine(
                    image,
                    vertical: true,
                    left + lattice.CellWidthPixels,
                    top,
                    lattice.CellHeightPixels);
                below[row, column] = row + 1 >= lattice.Rows || HasLine(
                    image,
                    vertical: false,
                    top + lattice.CellHeightPixels,
                    left,
                    lattice.CellWidthPixels);
            }
        }

        return new(right, below);
    }

    private static bool HasLine(CapturedImage image, bool vertical, int position, int crossStart, int crossLength)
    {
        var inset = Math.Max(2, crossLength / 6);
        var from = crossStart + inset;
        var to = crossStart + crossLength - inset;
        var axisLimit = vertical ? image.Width : image.Height;
        var crossLimit = vertical ? image.Height : image.Width;
        var samples = 0;
        var lines = 0;
        for (var cross = Math.Max(0, from); cross < Math.Min(crossLimit, to); cross++)
        {
            samples++;
            // A lattice rounded to whole pixels can sit one pixel off the drawn line.
            for (var axis = position - 1; axis <= position + 1; axis++)
            {
                if (axis < 2 || axis >= axisLimit - 2)
                {
                    continue;
                }

                int value = Luminance(image, vertical, axis, cross);
                var fromBefore = value - Luminance(image, vertical, axis - 2, cross);
                var fromAfter = value - Luminance(image, vertical, axis + 2, cross);
                if ((fromBefore >= ContainerGridDetector.RidgeContrast && fromAfter >= ContainerGridDetector.RidgeContrast) ||
                    (fromBefore <= -ContainerGridDetector.RidgeContrast && fromAfter <= -ContainerGridDetector.RidgeContrast))
                {
                    lines++;
                    break;
                }
            }
        }

        return samples > 0 && lines >= samples * MinimumLineShare;
    }

    private static byte Luminance(CapturedImage image, bool vertical, int axis, int cross) => vertical
        ? CapturedImagePixels.GetLuminance(image, axis, cross)
        : CapturedImagePixels.GetLuminance(image, cross, axis);
}

/// <summary>Whether a line separates a cell from its right-hand and lower neighbours.</summary>
internal sealed record GridBorders(bool[,] Right, bool[,] Below);
