using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>One item's rectangle of cells, in lattice coordinates.</summary>
public readonly record struct StashFootprint(int Row, int Column, int Width, int Height);

/// <summary>
/// Reads which cells of a packed stash belong to the same item, from the grid lines between them.
/// </summary>
/// <remarks>
/// <para>
/// The general footprint merge joins every occupied cell that touches another. That is right for a
/// looted container, where items sit apart, and it cannot be right for a stash, where nearly every
/// cell is occupied. Measured on the painted 34-row stash (package 40): 125 of 202 footprints
/// found with 261 spurious ones, and in the low-contrast variant the whole 10x14 panel came back
/// as one item.
/// </para>
/// <para>
/// Two things replace it here. A cell is empty when it is flat - the general segmenter compares
/// each cell with the panel's lower-quartile mean, and in a packed panel that quartile is itself
/// an item. And two occupied neighbours are one item only when no grid line runs between them:
/// the game draws an item across the lines inside its own rectangle and leaves the line between
/// two different items, including two of the same item side by side.
/// </para>
/// <para>
/// A joined group that does not fill its own bounding box is not trusted as a shape; its cells
/// fall back to single cells, which the icon match will then decline rather than guess.
/// </para>
/// </remarks>
public sealed class StashFootprintReader
{
    /// <summary>The largest luminance range, in levels, a cell can show and still be empty.</summary>
    private const int MaximumEmptyRange = 14;

    /// <summary>How far a line must stand out from the pixels either side of it, in levels.</summary>
    private const int MinimumLineDepth = 12;

    /// <summary>The share of samples along a boundary that must show a line.</summary>
    private const double MinimumLineShare = 0.6;

    public IReadOnlyList<StashFootprint> Read(
        CapturedImage image,
        ContainerGridSpec grid,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(grid);
        CapturedImagePixels.Validate(image, CapturedImagePixels.MaximumPixels);
        cancellationToken.ThrowIfCancellationRequested();
        if (grid.Rows < 1 || grid.Columns < 1)
        {
            return [];
        }

        var check = new PixelCancellationCheck(cancellationToken);
        var cellWidth = grid.Bounds.Width / grid.Columns;
        var cellHeight = grid.Bounds.Height / grid.Rows;
        if (cellWidth < 8 || cellHeight < 8)
        {
            return [];
        }

        var occupied = new bool[grid.Rows, grid.Columns];
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                occupied[row, column] = !IsFlat(image, grid, row, column, cellWidth, cellHeight, ref check);
            }
        }

        // Union-find over the cells: a missing line between two occupied neighbours joins them.
        var parent = Enumerable.Range(0, grid.Rows * grid.Columns).ToArray();
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                if (!occupied[row, column])
                {
                    continue;
                }

                if (column + 1 < grid.Columns && occupied[row, column + 1] &&
                    !HasLine(image, grid, row, column, cellWidth, cellHeight, vertical: true, ref check))
                {
                    Union(parent, (row * grid.Columns) + column, (row * grid.Columns) + column + 1);
                }

                if (row + 1 < grid.Rows && occupied[row + 1, column] &&
                    !HasLine(image, grid, row, column, cellWidth, cellHeight, vertical: false, ref check))
                {
                    Union(parent, (row * grid.Columns) + column, ((row + 1) * grid.Columns) + column);
                }
            }
        }

        var groups = new Dictionary<int, List<(int Row, int Column)>>();
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                if (!occupied[row, column])
                {
                    continue;
                }

                var root = Find(parent, (row * grid.Columns) + column);
                if (!groups.TryGetValue(root, out var cells))
                {
                    groups[root] = cells = [];
                }

                cells.Add((row, column));
            }
        }

        var footprints = new List<StashFootprint>();
        foreach (var cells in groups.Values)
        {
            var top = cells.Min(cell => cell.Row);
            var left = cells.Min(cell => cell.Column);
            var height = cells.Max(cell => cell.Row) - top + 1;
            var width = cells.Max(cell => cell.Column) - left + 1;
            if (cells.Count == width * height)
            {
                footprints.Add(new(top, left, width, height));
            }
            else
            {
                footprints.AddRange(cells.Select(cell => new StashFootprint(cell.Row, cell.Column, 1, 1)));
            }
        }

        return footprints
            .OrderBy(footprint => footprint.Row)
            .ThenBy(footprint => footprint.Column)
            .ToArray();
    }

    private static bool IsFlat(
        CapturedImage image,
        ContainerGridSpec grid,
        int row,
        int column,
        int cellWidth,
        int cellHeight,
        ref PixelCancellationCheck check)
    {
        var left = grid.Bounds.X + (column * cellWidth) + (cellWidth / 6);
        var top = grid.Bounds.Y + (row * cellHeight) + (cellHeight / 6);
        var right = grid.Bounds.X + ((column + 1) * cellWidth) - (cellWidth / 6);
        var bottom = grid.Bounds.Y + ((row + 1) * cellHeight) - (cellHeight / 6);
        var stepX = Math.Max(1, (right - left) / 14);
        var stepY = Math.Max(1, (bottom - top) / 14);
        int lowest = byte.MaxValue;
        int highest = byte.MinValue;
        for (var y = top; y < bottom && y < image.Height; y += stepY)
        {
            for (var x = left; x < right && x < image.Width; x += stepX)
            {
                check.Read();
                var luminance = CapturedImagePixels.GetLuminance(image, x, y);
                lowest = Math.Min(lowest, luminance);
                highest = Math.Max(highest, luminance);
                if (highest - lowest > MaximumEmptyRange)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Whether a grid line runs along the right (vertical) or bottom edge of a cell.</summary>
    private static bool HasLine(
        CapturedImage image,
        ContainerGridSpec grid,
        int row,
        int column,
        int cellWidth,
        int cellHeight,
        bool vertical,
        ref PixelCancellationCheck check)
    {
        var reach = Math.Max(2, cellWidth / 21);
        var along = vertical ? cellHeight : cellWidth;
        var from = along / 5;
        var to = along - from;
        var boundary = vertical
            ? grid.Bounds.X + ((column + 1) * cellWidth)
            : grid.Bounds.Y + ((row + 1) * cellHeight);
        var origin = vertical
            ? grid.Bounds.Y + (row * cellHeight)
            : grid.Bounds.X + (column * cellWidth);
        var lines = 0;
        var samples = 0;
        for (var offset = from; offset < to; offset += 2)
        {
            check.Read(5);
            samples++;
            // The detected origin can sit a pixel off the drawn line, so the line is looked for
            // across three pixels and compared with what lies clear of it on both sides.
            int lowest = byte.MaxValue;
            int highest = byte.MinValue;
            for (var nudge = -1; nudge <= 1; nudge++)
            {
                var value = Sample(image, boundary + nudge, origin + offset, vertical);
                lowest = Math.Min(lowest, value);
                highest = Math.Max(highest, value);
            }

            var before = Sample(image, boundary - reach - 1, origin + offset, vertical);
            var after = Sample(image, boundary + reach + 1, origin + offset, vertical);
            if (lowest <= Math.Min(before, after) - MinimumLineDepth ||
                highest >= Math.Max(before, after) + MinimumLineDepth)
            {
                lines++;
            }
        }

        return samples > 0 && lines >= samples * MinimumLineShare;
    }

    private static int Sample(CapturedImage image, int across, int along, bool vertical)
    {
        var x = Math.Clamp(vertical ? across : along, 0, image.Width - 1);
        var y = Math.Clamp(vertical ? along : across, 0, image.Height - 1);
        return CapturedImagePixels.GetLuminance(image, x, y);
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Union(int[] parent, int left, int right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (leftRoot != rightRoot)
        {
            parent[rightRoot] = leftRoot;
        }
    }
}
