using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>What was measured along the side two neighbouring cells share.</summary>
internal readonly record struct StashBoundaryMeasure(int Ridge, int Step, int DarkerSide)
{
    /// <summary>The median ridge from which a shared side is a border.</summary>
    public const int MinimumRidge = 12;

    /// <summary>The median step in brightness across a side from which it is a border.</summary>
    public const int MinimumStep = 40;

    /// <summary>Whether two different items meet here.</summary>
    /// <remarks>
    /// <para>
    /// Chosen against 1,109 hand-labelled boundaries on six real screenshots (794 borders, 315
    /// inside items), out of every combination of a ridge threshold, a separate ridge threshold
    /// for dark tiles and a step threshold: this pair misjudges 19 of them, where the ridge alone
    /// misjudged 39.
    /// </para>
    /// <para>
    /// The ridge is an item's bright one-pixel border: median 45 on true borders, 10 or less on
    /// 95% of what is inside an item. The step is for the pale tile the game draws behind armour
    /// and backpacks, whose border measures 1 to 5 - weaker than the grid showing through it - and
    /// whose edge is instead a jump of 50 to 65 from its own background to the dark one next door.
    /// What is left are borders between two dark weapon parts at 8 to 10, which these two numbers
    /// cannot tell from grid showing through an item at 10. How flat the margins either side of
    /// the line are was tried as a third and did not help: a scope's art runs to the cell's edge.
    /// </para>
    /// </remarks>
    public bool IsBorder => Ridge >= MinimumRidge || Step >= MinimumStep;

    /// <summary>How nearly this side is a border: 1 is the threshold on either measure.</summary>
    public double Evidence => Math.Max(Ridge / (double)MinimumRidge, Step / (double)MinimumStep);

    /// <summary>Below this a side has nothing to say, and an irregular shape is left to fall apart.</summary>
    public const double MinimumEvidenceToCut = 0.3;
}

/// <summary>One item's rectangle of cells, in lattice coordinates.</summary>
public readonly record struct StashFootprint(int Row, int Column, int Width, int Height);

/// <summary>
/// Reads which cells of a packed stash belong to the same item, from the borders between them.
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
/// Two occupied neighbours are one item only when no border runs between them. The first version
/// of this assumed an item hides the grid lines inside its rectangle and that an empty cell is
/// flat, which is how the painted frames were drawn and not how the game draws. On real
/// screenshots (2026-09-18) the grid shows faintly through the transparent parts of a large
/// item's art, an empty cell carries a one-pixel hatch, and bright art within a few pixels of a
/// border hid the border from a comparison made four pixels away: a 3x3 vest, a 4x4 rig and a 5x7
/// backpack came back as single cells, and 139 of 140 cells of a half-empty screen read as
/// occupied.
/// </para>
/// <para>
/// So a border is now the median, along the whole shared side, of how far the boundary pixel
/// stands clear of the pixels two away (<see cref="StashLuminancePlane"/>): an item border
/// measures 25 to 45, what shows through art 8 or less, and the median ignores the stretch of a
/// side that art happens to touch. A cell is empty when it is flat once the hatch is averaged out
/// in small blocks.
/// </para>
/// <para>
/// A joined group that does not fill its own bounding box is not trusted as a shape; its cells
/// fall back to single cells, which the icon match will then decline rather than guess.
/// </para>
/// </remarks>
public sealed class StashFootprintReader
{
    /// <summary>The side of the square blocks an empty cell's hatch is averaged over.</summary>
    private const int HatchBlock = 5;

    /// <summary>The largest spread of block means, in levels, a cell can show and still be empty.</summary>
    private const int MaximumEmptySpread = 9;

    /// <summary>An empty cell is dark; a pale, even item (a sheet of paper) is not empty.</summary>
    private const int MaximumEmptyLuminance = 80;

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

        var cellWidth = grid.Bounds.Width / grid.Columns;
        var cellHeight = grid.Bounds.Height / grid.Rows;
        if (cellWidth < 8 || cellHeight < 8)
        {
            return [];
        }

        var plane = StashLuminancePlane.From(image, cancellationToken);
        var occupied = new bool[grid.Rows, grid.Columns];
        for (var row = 0; row < grid.Rows; row++)
        {
            for (var column = 0; column < grid.Columns; column++)
            {
                occupied[row, column] = !IsEmpty(plane, grid, row, column, cellWidth, cellHeight);
            }
        }

        // Every shared side of two occupied cells, measured once.
        var sides = new List<Side>();
        for (var row = 0; row < grid.Rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var column = 0; column < grid.Columns; column++)
            {
                if (!occupied[row, column])
                {
                    continue;
                }

                if (column + 1 < grid.Columns && occupied[row, column + 1])
                {
                    sides.Add(new(row, column, row, column + 1, MeasureBoundary(plane, grid, row, column, cellWidth, cellHeight, vertical: true)));
                }

                if (row + 1 < grid.Rows && occupied[row + 1, column])
                {
                    sides.Add(new(row, column, row + 1, column, MeasureBoundary(plane, grid, row, column, cellWidth, cellHeight, vertical: false)));
                }
            }
        }

        var cut = new bool[sides.Count];
        for (var index = 0; index < sides.Count; index++)
        {
            cut[index] = sides[index].Measure.IsBorder;
        }

        // One missed border is enough to bridge two items into a shape that is not a rectangle.
        // Rather than give up on every cell of it, the side inside that shape with the most
        // evidence of being a border is cut, and again, until what is left are rectangles or no
        // side has any evidence left to offer.
        var footprints = new List<StashFootprint>();
        var ordered = Enumerable.Range(0, sides.Count)
            .Where(index => !cut[index] && sides[index].Measure.Evidence >= StashBoundaryMeasure.MinimumEvidenceToCut)
            .OrderByDescending(index => sides[index].Measure.Evidence)
            .ToList();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var groups = Groups(grid, occupied, sides, cut);
            var irregular = groups.Where(cells => !FillsItsBox(cells)).ToArray();
            var inIrregular = irregular.SelectMany(cells => cells).ToHashSet();
            var next = ordered.FindIndex(index => inIrregular.Contains((sides[index].Row, sides[index].Column)) &&
                                                  inIrregular.Contains((sides[index].OtherRow, sides[index].OtherColumn)));
            if (irregular.Length == 0 || next < 0)
            {
                foreach (var cells in groups)
                {
                    if (FillsItsBox(cells))
                    {
                        var top = cells.Min(cell => cell.Row);
                        var left = cells.Min(cell => cell.Column);
                        footprints.Add(new(top, left, cells.Max(cell => cell.Column) - left + 1, cells.Max(cell => cell.Row) - top + 1));
                    }
                    else
                    {
                        footprints.AddRange(cells.Select(cell => new StashFootprint(cell.Row, cell.Column, 1, 1)));
                    }
                }

                break;
            }

            cut[ordered[next]] = true;
            ordered.RemoveAt(next);
        }

        return footprints
            .OrderBy(footprint => footprint.Row)
            .ThenBy(footprint => footprint.Column)
            .ToArray();
    }

    private static List<List<(int Row, int Column)>> Groups(ContainerGridSpec grid, bool[,] occupied, IReadOnlyList<Side> sides, bool[] cut)
    {
        var parent = Enumerable.Range(0, grid.Rows * grid.Columns).ToArray();
        for (var index = 0; index < sides.Count; index++)
        {
            if (!cut[index])
            {
                Union(parent, (sides[index].Row * grid.Columns) + sides[index].Column, (sides[index].OtherRow * grid.Columns) + sides[index].OtherColumn);
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

        return [.. groups.Values];
    }

    private static bool FillsItsBox(List<(int Row, int Column)> cells)
    {
        var height = cells.Max(cell => cell.Row) - cells.Min(cell => cell.Row) + 1;
        var width = cells.Max(cell => cell.Column) - cells.Min(cell => cell.Column) + 1;
        return cells.Count == width * height;
    }

    private readonly record struct Side(int Row, int Column, int OtherRow, int OtherColumn, StashBoundaryMeasure Measure);

    /// <summary>Flat once the one-pixel hatch is averaged out, and dark.</summary>
    internal static bool IsEmpty(StashLuminancePlane plane, ContainerGridSpec grid, int row, int column, int cellWidth, int cellHeight)
    {
        var left = grid.Bounds.X + (column * cellWidth) + (cellWidth / 8);
        var top = grid.Bounds.Y + (row * cellHeight) + (cellHeight / 8);
        var right = grid.Bounds.X + ((column + 1) * cellWidth) - (cellWidth / 8);
        var bottom = grid.Bounds.Y + ((row + 1) * cellHeight) - (cellHeight / 8);
        var lowest = int.MaxValue;
        var highest = int.MinValue;
        long total = 0;
        var blocks = 0;
        for (var y = top; y + HatchBlock <= bottom; y += HatchBlock)
        {
            for (var x = left; x + HatchBlock <= right; x += HatchBlock)
            {
                var sum = 0;
                for (var dy = 0; dy < HatchBlock; dy++)
                {
                    for (var dx = 0; dx < HatchBlock; dx++)
                    {
                        sum += plane.At(x + dx, y + dy);
                    }
                }

                var mean = sum / (HatchBlock * HatchBlock);
                lowest = Math.Min(lowest, mean);
                highest = Math.Max(highest, mean);
                total += mean;
                blocks++;
                if (highest - lowest > MaximumEmptySpread)
                {
                    return false;
                }
            }
        }

        return blocks > 0 && total / blocks <= MaximumEmptyLuminance;
    }

    /// <summary>
    /// What the shared side of two cells looks like: the median ridge on whichever of the three
    /// pixel lines about the lattice line carries it best, the median step in brightness from one
    /// side of the line to the other, and how bright the darker side is.
    /// </summary>
    internal static StashBoundaryMeasure MeasureBoundary(
        StashLuminancePlane plane,
        ContainerGridSpec grid,
        int row,
        int column,
        int cellWidth,
        int cellHeight,
        bool vertical)
    {
        var along = vertical ? cellHeight : cellWidth;
        var from = along / 8;
        var to = along - from;
        var boundary = vertical
            ? grid.Bounds.X + ((column + 1) * cellWidth)
            : grid.Bounds.Y + ((row + 1) * cellHeight);
        var origin = vertical
            ? grid.Bounds.Y + (row * cellHeight)
            : grid.Bounds.X + (column * cellWidth);
        var count = to - from;
        var ridges = new int[count];
        var bestRidge = 0;
        for (var nudge = -1; nudge <= 1; nudge++)
        {
            for (var offset = from; offset < to; offset++)
            {
                ridges[offset - from] = vertical
                    ? plane.Ridge(boundary + nudge, origin + offset, vertical: true)
                    : plane.Ridge(origin + offset, boundary + nudge, vertical: false);
            }

            Array.Sort(ridges);
            bestRidge = Math.Max(bestRidge, ridges[count / 2]);
        }

        var steps = new int[count];
        var before = new int[count];
        var after = new int[count];
        for (var offset = from; offset < to; offset++)
        {
            var near = 0;
            var far = 0;
            for (var depth = 3; depth <= 6; depth++)
            {
                near += vertical ? plane.At(boundary - depth, origin + offset) : plane.At(origin + offset, boundary - depth);
                far += vertical ? plane.At(boundary + depth, origin + offset) : plane.At(origin + offset, boundary + depth);
            }

            before[offset - from] = near / 4;
            after[offset - from] = far / 4;
            steps[offset - from] = Math.Abs(near - far) / 4;
        }

        Array.Sort(steps);
        Array.Sort(before);
        Array.Sort(after);
        return new(bestRidge, steps[count / 2], Math.Min(before[count / 2], after[count / 2]));
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
