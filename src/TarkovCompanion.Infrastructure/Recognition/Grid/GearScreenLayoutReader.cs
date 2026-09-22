using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.Infrastructure.Recognition.Grid;

/// <summary>Which part of the in-raid Gear screen a grid belongs to.</summary>
public enum GearGridSection
{
    /// <summary>The opened container, body or pile on the right of the carried column.</summary>
    Loot = 1,
    TacticalRig,
    Pockets,
    Backpack,

    /// <summary>A carried grid below the pockets that is not the backpack, such as the pouch.</summary>
    OtherCarried,
}

/// <summary>One framed grid of the Gear screen, in source pixels.</summary>
/// <param name="Frame">The rectangle of the grid's outer frame, frame lines included.</param>
/// <param name="FrameShare">The share of the frame's left edge that was drawn, 0.75 to 1.</param>
public sealed record GearGrid(GearGridSection Section, PixelRect Frame, int Columns, int Rows, int Pitch, double FrameShare)
{
    /// <summary>The lattice the grid recognizer reads: the cells, starting one pixel inside the frame.</summary>
    public ContainerGridSpec Lattice => new(new(Frame.X + 1, Frame.Y + 1, Columns * Pitch, Rows * Pitch), Columns, Rows);

    public int Cells => Columns * Rows;
}

/// <summary>What the Gear screen showed, grid by grid.</summary>
public sealed record GearScreenLayout(int Pitch, IReadOnlyList<GearGrid> Grids)
{
    public IEnumerable<GearGrid> In(GearGridSection section) => Grids.Where(grid => grid.Section == section);

    /// <summary>The largest grid of a section, which is the one a single-lattice planner can use.</summary>
    public GearGrid? Largest(GearGridSection section) => In(section)
        .OrderByDescending(grid => grid.Cells)
        .ThenBy(grid => grid.Frame.Y)
        .ThenBy(grid => grid.Frame.X)
        .FirstOrDefault();
}

/// <summary>
/// Finds every grid on the in-raid Gear screen and says which is the loot, the rig, the pockets
/// and the backpack.
/// </summary>
/// <remarks>
/// <para>
/// Measured on three real 3840x1080 in-raid Gear screens (2026-09-20): the general line detector
/// returned an 11x6 lattice spread across the rig, pockets and backpack for a frame whose loot
/// was a 3x3 wooden ammo box, and a 3x2 corner of the secure container for a frame with no loot
/// open at all. The carried column is a stack of separate small grids, which is the most regular
/// thing on that screen.
/// </para>
/// <para>
/// Every grid on this screen has its own outer frame: a one-pixel line of the same grey,
/// (88, 93, 96), luminance 85 to 93, with near-black just outside it. The lines between cells are
/// darker (luminance 35 to 80). So a grid is found as a top and a bottom frame run of the same
/// start and length, a whole number of cells apart, with the left frame drawn between them. The
/// equipment slot boxes beside each container are drawn with the same grey on top and bottom and
/// none on the left, and are not grids. On the three frames this found every grid whose frame is
/// in view: 14, 12 and 13 grids, none spurious. A tooltip over a frame hides that grid.
/// </para>
/// <para>
/// Which grid is which comes from the layout, measured on the same frames. The interface is a
/// 16:9 box centred in the frame; the carried column's slot boxes start 656 interface pixels in
/// and the loot panel 1,264. The pockets are the one carried grid that starts on the slot column
/// itself, since every other container has its slot box there. Grids above the pockets are the
/// rig. The first group below them is the backpack when it starts where the backpack's header
/// puts it (44 pixels below the pockets on both frames with a bag), and something else, the
/// pouch, when an empty backpack slot sits between them.
/// </para>
/// <para>
/// Unmeasured: other interface scales and 16:10 or 21:9 frames (the pitch is taken as 63 pixels
/// per 1080 of height), a looted body (whose own gear is several grids on the loot side), and
/// a carried column scrolled far enough to hide the pockets, which returns no carried sections.
/// </para>
/// </remarks>
public sealed class GearScreenLayoutReader
{
    private const double PitchPer1080 = 63;
    private const int FrameLuminanceMinimum = 82;
    private const int FrameLuminanceMaximum = 100;
    private const int FrameChromaMaximum = 16;
    private const double MinimumLeftEdgeShare = 0.75;

    /// <summary>Interface pixels, on a 1920-wide 16:9 interface.</summary>
    private const double SearchLeft = 600;
    private const double SlotColumnLeft = 656;
    private const double LootPanelLeft = 1240;
    private const double SearchBottomShare = 0.9;

    /// <summary>Largest gap, in interface pixels, between two grids of one container.</summary>
    private const double ContainerGap = 12;

    /// <summary>Furthest below the pockets, in interface pixels, a backpack grid starts.</summary>
    private const double BackpackOffsetMaximum = 70;

    public GearScreenLayout? Read(CapturedImage image, CancellationToken cancellationToken = default)
    {
        CapturedImagePixels.Validate(image);
        if (CapturedImagePixels.ExceedsPixelCeiling(image))
        {
            return null;
        }

        var scale = image.Height / 1080d;
        var pitch = (int)Math.Round(PitchPer1080 * scale);
        var interfaceWidth = Math.Min(image.Width, (int)Math.Round(image.Height * 16d / 9d));
        var interfaceLeft = (image.Width - interfaceWidth) / 2;
        int At(double interfacePixels) => interfaceLeft + (int)Math.Round(interfacePixels * interfaceWidth / 1920d);

        var left = Math.Clamp(At(SearchLeft), 0, image.Width);
        var right = Math.Clamp(interfaceLeft + interfaceWidth, left, image.Width);
        var bottom = Math.Clamp((int)Math.Round(image.Height * SearchBottomShare), 0, image.Height);
        var boxes = FindBoxes(image, left, right, bottom, pitch, cancellationToken);
        if (boxes.Count == 0)
        {
            return null;
        }

        var lootLeft = At(LootPanelLeft);
        var slotLeft = At(SlotColumnLeft);
        var tolerance = Math.Max(3, (int)Math.Round(4 * scale));
        var gap = (int)Math.Round(ContainerGap * scale);
        var grids = new List<GearGrid>();
        grids.AddRange(boxes.Where(box => box.X >= lootLeft).Select(box => box.As(GearGridSection.Loot, pitch)));

        var carried = boxes.Where(box => box.X < lootLeft).ToList();
        var firstPocket = carried
            .Where(box => Math.Abs(box.X - slotLeft) <= tolerance)
            .OrderBy(box => box.Y)
            .FirstOrDefault();
        if (firstPocket is null)
        {
            return grids.Count == 0 ? null : new(pitch, grids);
        }

        var pockets = new List<Box> { firstPocket };
        while (carried.FirstOrDefault(box =>
                   !pockets.Contains(box) &&
                   Math.Abs(box.Y - firstPocket.Y) <= tolerance &&
                   box.X > pockets[^1].Right &&
                   box.X - pockets[^1].Right <= gap) is { } next)
        {
            pockets.Add(next);
        }

        grids.AddRange(pockets.Select(box => box.As(GearGridSection.Pockets, pitch)));
        var pocketsTop = pockets.Min(box => box.Y);
        var pocketsBottom = pockets.Max(box => box.Bottom);
        var groups = Group(carried.Except(pockets).OrderBy(box => box.Y).ThenBy(box => box.X), gap);
        var above = groups.Where(group => group.Max(box => box.Bottom) <= pocketsTop).ToList();
        if (above.Count > 0)
        {
            grids.AddRange(above[^1].Select(box => box.As(GearGridSection.TacticalRig, pitch)));
        }

        var below = groups.Where(group => group.Min(box => box.Y) >= pocketsBottom).ToList();
        for (var index = 0; index < below.Count; index++)
        {
            var top = below[index].Min(box => box.Y);
            var section = index == 0 && top - pocketsBottom <= BackpackOffsetMaximum * scale
                ? GearGridSection.Backpack
                : GearGridSection.OtherCarried;
            grids.AddRange(below[index].Select(box => box.As(section, pitch)));
        }

        return new(pitch, grids);
    }

    private static List<List<Box>> Group(IEnumerable<Box> boxes, int gap)
    {
        var groups = new List<List<Box>>();
        foreach (var box in boxes)
        {
            var group = groups.FirstOrDefault(existing => box.Y <= existing.Max(member => member.Bottom) + gap);
            if (group is null)
            {
                groups.Add([box]);
            }
            else
            {
                group.Add(box);
            }
        }

        return groups;
    }

    private static List<Box> FindBoxes(CapturedImage image, int left, int right, int bottom, int pitch, CancellationToken cancellationToken)
    {
        var minimumRun = pitch - Math.Max(4, pitch / 6);
        var runs = new List<(int Y, int X, int Length)>();
        for (var y = 0; y < bottom; y++)
        {
            if ((y & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var start = -1;
            for (var x = left; x <= right; x++)
            {
                var frame = x < right && IsFrame(image, x, y);
                if (frame && start < 0)
                {
                    start = x;
                }
                else if (!frame && start >= 0)
                {
                    if (x - start >= minimumRun)
                    {
                        runs.Add((y, start, x - start));
                    }

                    start = -1;
                }
            }
        }

        var found = new List<Box>();
        foreach (var top in runs)
        {
            foreach (var closing in runs)
            {
                if (closing.Y <= top.Y || Math.Abs(closing.X - top.X) > 1)
                {
                    continue;
                }

                var width = Math.Min(top.Length, closing.Length);
                var columns = (int)Math.Round((width - 2) / (double)pitch);
                var height = closing.Y - top.Y;
                var rows = (int)Math.Round((height - 1) / (double)pitch);
                if (columns is < 1 or > GridGeometryLimits.MaxColumns || rows is < 1 or > GridGeometryLimits.MaxRows ||
                    Math.Abs(width - ((pitch * columns) + 2)) > 2 ||
                    Math.Abs(height - ((pitch * rows) + 1)) > 2 ||
                    LeftEdgeShare(image, top.X, top.Y, closing.Y) is var share && share < MinimumLeftEdgeShare)
                {
                    continue;
                }

                found.Add(new(top.X, top.Y, columns, rows, (pitch * columns) + 2, height + 1, share));
            }
        }

        // A frame can pair with a later grid's frame as well as its own bottom; the tighter box is
        // the grid, and anything holding another box's corner spans more than one grid.
        return found
            .Distinct()
            .Where(box => !found.Any(other => !other.Equals(box) &&
                other.X >= box.X && other.Y >= box.Y && (other.X, other.Y) != (box.X, box.Y) &&
                other.X < box.Right - 2 && other.Y < box.Bottom - 2))
            .Where(box => !found.Any(other => other.X == box.X && other.Y == box.Y && other.Rows < box.Rows))
            .OrderBy(box => box.Y)
            .ThenBy(box => box.X)
            .ToList();
    }

    private static double LeftEdgeShare(CapturedImage image, int x, int top, int bottom)
    {
        var drawn = 0;
        for (var y = top; y <= bottom; y++)
        {
            if (IsFrame(image, x, y) || (x + 1 < image.Width && IsFrame(image, x + 1, y)) || (x > 0 && IsFrame(image, x - 1, y)))
            {
                drawn++;
            }
        }

        return drawn / (double)(bottom - top + 1);
    }

    internal static bool IsFrame(CapturedImage image, int x, int y)
    {
        var luminance = CapturedImagePixels.GetLuminance(image, x, y);
        if (luminance is < FrameLuminanceMinimum or > FrameLuminanceMaximum)
        {
            return false;
        }

        if (image.Format == PixelFormat.Gray8)
        {
            return true;
        }

        var pixels = image.Pixels.Span;
        var offset = (y * image.Stride) + (x * CapturedImagePixels.BytesPerPixel(image.Format));
        var a = pixels[offset];
        var b = pixels[offset + 1];
        var c = pixels[offset + 2];
        return Math.Max(a, Math.Max(b, c)) - Math.Min(a, Math.Min(b, c)) <= FrameChromaMaximum;
    }

    private sealed record Box(int X, int Y, int Columns, int Rows, int Width, int Height, double Share)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;

        public GearGrid As(GearGridSection section, int pitch) =>
            new(section, new(X, Y, Width, Height), Columns, Rows, pitch, Share);
    }
}

file static class GridGeometryLimits
{
    public const int MaxColumns = 20;
    public const int MaxRows = 20;
}
