using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.StashScanFixtures;

/// <summary>One kind of item a synthetic stash can hold.</summary>
internal sealed record SyntheticStashItem(string ItemId, string Name, int Width, int Height);

/// <summary>One item lying in the synthetic stash: the truth a scan is scored against.</summary>
internal sealed record SyntheticStashPlacement(SyntheticStashItem Item, int Row, int Column);

/// <summary>How a synthetic stash frame is drawn.</summary>
/// <remarks>
/// <para>
/// Drawn the way real stash screenshots measured (2026-09-18, 3840x1080): ten columns of 63-pixel
/// cells on a 1080-tall frame; empty cells dark with a one-pixel hatch; the grid a single pixel
/// about 15 levels over that; an item's border a single bright pixel on the lattice line itself;
/// a pale tile with almost no border behind large items, and the grid showing faintly through
/// them; a bright line across the panel just above the first row; and a last row cut with most
/// of it still showing. The first version of this painter drew dark lines on pale cells, items
/// that hid every line inside them and flat empties, and code tuned to it failed on the real
/// thing in each of those places.
/// </para>
/// <para>
/// The item art and the caption marks are still invented, which is why identification measured on
/// these frames describes the plumbing and not the game.
/// </para>
/// </remarks>
internal sealed record SyntheticStashFrameOptions(
    int FrameWidth = 1920,
    int FrameHeight = 1080,
    int PanelX = 1270,
    int PanelY = 81,
    int VisibleRows = 14,
    int PartialRowPixels = 47,
    byte CellLuminance = 38,
    byte LineLuminance = 53,
    bool DrawCaptions = true)
{
    public int Pitch => Math.Max(2, (int)Math.Round(FrameHeight * 63.0 / 1080.0));
}

/// <summary>A whole stash, taller than one screen, with every item's place known.</summary>
internal sealed class SyntheticStashLayout
{
    public const int Columns = 10;

    private SyntheticStashLayout(int rows, IReadOnlyList<SyntheticStashPlacement> placements)
    {
        Rows = rows;
        Placements = placements;
    }

    public int Rows { get; }

    public IReadOnlyList<SyntheticStashPlacement> Placements { get; }

    public static IReadOnlyList<SyntheticStashItem> Catalog { get; } =
    [
        new("syn-bolts", "Bolts", 1, 1),
        new("syn-nuts", "Screw nuts", 1, 1),
        new("syn-wires", "Wires", 1, 1),
        new("syn-tape", "Insulating tape", 1, 1),
        new("syn-matches", "Matches", 1, 1),
        new("syn-ledx", "LEDX", 1, 1),
        new("syn-gpu", "Graphics card", 2, 1),
        new("syn-hose", "Corrugated hose", 1, 2),
        new("syn-salewa", "Salewa", 1, 2),
        new("syn-water", "Water bottle", 1, 2),
        new("syn-fuel", "Expeditionary fuel", 2, 2),
        new("syn-filter", "Water filter", 2, 2),
        new("syn-motor", "Electric motor", 2, 2),
        new("syn-rig", "Tactical rig", 3, 2),
        new("syn-armor", "Body armour", 3, 3),
        new("syn-pistol", "Pistol", 2, 1),
        new("syn-smg", "Submachine gun", 4, 2),
        new("syn-rifle", "Assault rifle", 5, 2),
        new("syn-case", "Item case", 4, 4),
        new("syn-pack", "Backpack", 4, 5),
    ];

    /// <summary>A stash holding exactly these items, for a fixture that needs one known tile.</summary>
    public static SyntheticStashLayout Of(int rows, params SyntheticStashPlacement[] placements) => new(rows, placements);

    /// <summary>Packs a stash first-fit from a seeded shuffle, leaving a few cells empty.</summary>
    public static SyntheticStashLayout Build(int rows, int seed = 20260918, double emptyCellFraction = 0.12)
    {
        var random = new Random(seed);
        var occupied = new bool[rows, Columns];
        var placements = new List<SyntheticStashPlacement>();
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                if (occupied[row, column])
                {
                    continue;
                }

                if (random.NextDouble() < emptyCellFraction)
                {
                    continue;
                }

                // Large items are rarer than small ones, as in a real stash.
                var item = Catalog[random.Next(Catalog.Count)];
                if (item.Width * item.Height > 4 && random.Next(3) != 0)
                {
                    item = Catalog[random.Next(6)];
                }

                if (!Fits(occupied, rows, row, column, item))
                {
                    item = Catalog[random.Next(6)];
                }

                for (var r = row; r < row + item.Height; r++)
                {
                    for (var c = column; c < column + item.Width; c++)
                    {
                        occupied[r, c] = true;
                    }
                }

                placements.Add(new(item, row, column));
            }
        }

        return new(rows, placements);
    }

    private static bool Fits(bool[,] occupied, int rows, int row, int column, SyntheticStashItem item)
    {
        if (row + item.Height > rows || column + item.Width > Columns)
        {
            return false;
        }

        for (var r = row; r < row + item.Height; r++)
        {
            for (var c = column; c < column + item.Width; c++)
            {
                if (occupied[r, c])
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>
/// Draws what one screenshot of a scrolled stash would show, pixel by pixel, so the scan path can
/// be run through its real pixel-reading code and scored against a truth nobody had to label.
/// </summary>
internal static class SyntheticStashPainter
{
    private const byte ScreenLuminance = 20;
    private const byte FrameLineLuminance = 138;
    private const byte BorderLuminance = 100;
    private const byte TileLuminance = 58;
    private const byte PaleTileLuminance = 112;

    /// <summary>The stash panel as seen with <paramref name="firstRow"/> scrolled to the top.</summary>
    /// <remarks>
    /// An item that crosses the top or bottom of the viewport is drawn cut, because that is what
    /// the game does and a cut item is the hazard an overlap stitch has to survive.
    /// </remarks>
    public static CapturedImage RenderFrame(
        SyntheticStashLayout layout,
        int firstRow,
        SyntheticStashFrameOptions? options = null)
    {
        options ??= new();
        var pitch = options.Pitch;
        var stride = options.FrameWidth * 4;
        var buffer = new byte[stride * options.FrameHeight];
        FillRect(buffer, stride, options, 0, 0, options.FrameWidth, options.FrameHeight, ScreenLuminance);
        DrawGearPanel(buffer, stride, options);

        var viewportHeight = (options.VisibleRows * pitch) + options.PartialRowPixels;
        var viewport = (X: options.PanelX, Y: options.PanelY, Width: SyntheticStashLayout.Columns * pitch, Height: viewportHeight);
        FillHatch(buffer, stride, options, viewport.X, viewport.Y, viewport.Width, viewport.Height, options.CellLuminance, viewport.Y, viewport.Y + viewport.Height);

        // The viewport's own top edge, just above the first row.
        FillRect(buffer, stride, options, viewport.X, viewport.Y - 3, viewport.Width + 1, 1, FrameLineLuminance);

        var visibleRowCount = options.VisibleRows + (options.PartialRowPixels > 0 ? 1 : 0);
        for (var row = 0; row <= visibleRowCount; row++)
        {
            var y = viewport.Y + (row * pitch);
            if (y < viewport.Y + viewport.Height)
            {
                FillRect(buffer, stride, options, viewport.X, y, viewport.Width + 1, 1, options.LineLuminance);
            }
        }

        for (var column = 0; column <= SyntheticStashLayout.Columns; column++)
        {
            FillRect(buffer, stride, options, viewport.X + (column * pitch), viewport.Y, 1, viewport.Height, options.LineLuminance);
        }

        // Pale tiles last: where one meets a dark tile, its faint border is the one that shows.
        foreach (var placement in layout.Placements.OrderBy(placement => placement.Item.Width * placement.Item.Height >= 9))
        {
            var top = viewport.Y + ((placement.Row - firstRow) * pitch);
            var left = viewport.X + (placement.Column * pitch);
            DrawItem(buffer, stride, options, placement.Item, left, top, pitch, viewport.Y, viewport.Y + viewport.Height, options.DrawCaptions);
        }

        return new CapturedImage(
            buffer,
            options.FrameWidth,
            options.FrameHeight,
            stride,
            PixelFormat.Bgra8888,
            DateTimeOffset.UnixEpoch.AddSeconds(firstRow),
            $"synthetic-stash-row-{firstRow}");
    }

    /// <summary>
    /// The item as a published catalogue icon would show it: the art alone on a plain tile, with
    /// no caption, no count and a different cell size from the game's.
    /// </summary>
    public static CapturedImage RenderReferenceIcon(SyntheticStashItem item, int cellPixels = 64)
    {
        var width = item.Width * cellPixels;
        var height = item.Height * cellPixels;
        var options = new SyntheticStashFrameOptions(FrameWidth: width, FrameHeight: height);
        var stride = width * 4;
        var buffer = new byte[stride * height];
        FillRect(buffer, stride, options, 0, 0, width, height, 70);
        DrawArt(buffer, stride, options, item, 0, 0, width, height, 0, height);
        return new CapturedImage(buffer, width, height, stride, PixelFormat.Bgra8888, DateTimeOffset.UnixEpoch, $"synthetic-icon-{item.ItemId}");
    }

    private static void DrawItem(
        byte[] buffer,
        int stride,
        SyntheticStashFrameOptions options,
        SyntheticStashItem item,
        int left,
        int top,
        int pitch,
        int clipTop,
        int clipBottom,
        bool drawCaption)
    {
        var width = item.Width * pitch;
        var height = item.Height * pitch;
        if (top + height <= clipTop || top >= clipBottom)
        {
            return;
        }

        // A large item sits on a pale tile whose border barely shows; anything else on a dark
        // tinted one inside a bright border. Both are drawn on the lattice lines themselves.
        var pale = item.Width * item.Height >= 9;
        var tile = pale ? PaleTileLuminance : TileLuminance;
        var border = pale ? (byte)(PaleTileLuminance + 4) : BorderLuminance;
        if (pale)
        {
            FillRectClipped(buffer, stride, options, left + 1, top + 1, width - 1, height - 1, clipTop, clipBottom, tile);
        }
        else
        {
            FillHatch(buffer, stride, options, left + 1, top + 1, width - 1, height - 1, tile, clipTop, clipBottom);
        }

        // The grid shows faintly through the parts of a tile the art leaves clear.
        for (var column = 1; column < item.Width; column++)
        {
            FillRectClipped(buffer, stride, options, left + (column * pitch), top + 1, 1, height - 1, clipTop, clipBottom, (byte)(tile + 7));
        }

        for (var row = 1; row < item.Height; row++)
        {
            FillRectClipped(buffer, stride, options, left + 1, top + (row * pitch), width - 1, 1, clipTop, clipBottom, (byte)(tile + 7));
        }

        FillRectClipped(buffer, stride, options, left, top, width + 1, 1, clipTop, clipBottom, border);
        FillRectClipped(buffer, stride, options, left, top + height, width + 1, 1, clipTop, clipBottom, border);
        FillRectClipped(buffer, stride, options, left, top, 1, height + 1, clipTop, clipBottom, border);
        FillRectClipped(buffer, stride, options, left + width, top, 1, height + 1, clipTop, clipBottom, border);
        DrawArt(buffer, stride, options, item, left + 1, top + 1, width - 1, height - 1, clipTop, clipBottom);
        if (drawCaption)
        {
            // Caption marks top-right and a count bottom-right: not glyphs, only bright strokes
            // where the game draws bright strokes, so the tile differs from its catalogue icon
            // in the places a real one does.
            var seed = StableSeed(item.ItemId);
            for (var mark = 0; mark < 5; mark++)
            {
                var markX = left + width - 8 - (mark * 6);
                var markHeight = 5 + ((seed >> mark) & 3);
                FillRectClipped(buffer, stride, options, markX, top + 5, 3, markHeight, clipTop, clipBottom, 235);
            }

            FillRectClipped(buffer, stride, options, left + width - 12, top + height - 13, 3, 8, clipTop, clipBottom, 235);
            FillRectClipped(buffer, stride, options, left + width - 7, top + height - 13, 3, 8, clipTop, clipBottom, 235);
        }
    }

    private static void DrawArt(
        byte[] buffer,
        int stride,
        SyntheticStashFrameOptions options,
        SyntheticStashItem item,
        int left,
        int top,
        int width,
        int height,
        int clipTop,
        int clipBottom)
    {
        // Smooth shading seeded by the item, in units of cells so a 64-pixel catalogue cell and
        // the game's 63 draw the same picture. The first painter used hard-edged blocks four to a
        // cell, which put an edge on every cell boundary for the whole of its length - something a
        // rendered item does not do, and which reads as a border.
        var random = new Random(StableSeed(item.ItemId));
        var frequencyX = (0.35 + (random.NextDouble() * 0.25)) * 2 * Math.PI;
        var frequencyY = (0.35 + (random.NextDouble() * 0.25)) * 2 * Math.PI;
        var frequencyDiagonal = (0.6 + (random.NextDouble() * 0.3)) * 2 * Math.PI;
        var phaseX = random.NextDouble() * 2 * Math.PI;
        var phaseY = random.NextDouble() * 2 * Math.PI;
        var phaseDiagonal = random.NextDouble() * 2 * Math.PI;
        var insetX = Math.Max(2, width / 10);
        var insetY = Math.Max(2, height / 10);
        var cellWidth = width / (double)item.Width;
        var cellHeight = height / (double)item.Height;
        var x0 = Math.Max(0, left + insetX);
        var x1 = Math.Min(options.FrameWidth, left + width - insetX);
        var y0 = Math.Max(Math.Max(0, clipTop), top + insetY);
        var y1 = Math.Min(Math.Min(options.FrameHeight, clipBottom), top + height - insetY);
        for (var y = y0; y < y1; y++)
        {
            var v = (y - top) / cellHeight;
            var offset = (y * stride) + (x0 * 4);
            for (var x = x0; x < x1; x++)
            {
                var u = (x - left) / cellWidth;
                var shade = 125 +
                            (65 * Math.Sin((u * frequencyX) + phaseX) * Math.Cos((v * frequencyY) + phaseY)) +
                            (30 * Math.Sin(((u + v) * frequencyDiagonal) + phaseDiagonal));
                var value = (byte)Math.Clamp(shade, 0, 255);
                buffer[offset] = value;
                buffer[offset + 1] = value;
                buffer[offset + 2] = value;
                buffer[offset + 3] = 255;
                offset += 4;
            }
        }
    }

    /// <summary>A few slot boxes at a different pitch, so the stash is not the only grid on screen.</summary>
    private static void DrawGearPanel(byte[] buffer, int stride, SyntheticStashFrameOptions options)
    {
        var scale = options.FrameHeight / 1080.0;
        var slot = (int)(126 * scale);
        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var x = (int)(520 * scale) + (column * (slot + (int)(34 * scale)));
                var y = (int)(150 * scale) + (row * (slot + (int)(40 * scale)));
                if (x + slot >= options.PanelX)
                {
                    continue;
                }

                FillRect(buffer, stride, options, x, y, slot, slot, 40);
                FillRect(buffer, stride, options, x, y, slot, 1, 110);
                FillRect(buffer, stride, options, x, y + slot, slot, 1, 110);
                FillRect(buffer, stride, options, x, y, 1, slot, 110);
                FillRect(buffer, stride, options, x + slot, y, 1, slot, 110);
            }
        }
    }

    private static int StableSeed(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in value)
            {
                hash = (hash * 31) + character;
            }

            return hash & 0x7fffffff;
        }
    }

    /// <summary>The one-pixel hatch the game fills a cell with: alternate pixels three either side of the base.</summary>
    private static void FillHatch(
        byte[] buffer,
        int stride,
        SyntheticStashFrameOptions options,
        int x,
        int y,
        int width,
        int height,
        byte luminance,
        int clipTop,
        int clipBottom)
    {
        var x0 = Math.Max(0, x);
        var x1 = Math.Min(options.FrameWidth, x + width);
        var y0 = Math.Max(Math.Max(0, clipTop), y);
        var y1 = Math.Min(Math.Min(options.FrameHeight, clipBottom), y + height);
        for (var row = y0; row < y1; row++)
        {
            var offset = (row * stride) + (x0 * 4);
            for (var column = x0; column < x1; column++)
            {
                var value = (byte)(((row + column) & 1) == 0 ? luminance - 3 : luminance + 3);
                buffer[offset] = value;
                buffer[offset + 1] = value;
                buffer[offset + 2] = value;
                buffer[offset + 3] = 255;
                offset += 4;
            }
        }
    }

    private static void FillRect(byte[] buffer, int stride, SyntheticStashFrameOptions options, int x, int y, int width, int height, byte luminance) =>
        FillRectClipped(buffer, stride, options, x, y, width, height, 0, options.FrameHeight, luminance);

    private static void FillRectClipped(
        byte[] buffer,
        int stride,
        SyntheticStashFrameOptions options,
        int x,
        int y,
        int width,
        int height,
        int clipTop,
        int clipBottom,
        byte luminance)
    {
        var x0 = Math.Max(0, x);
        var x1 = Math.Min(options.FrameWidth, x + width);
        var y0 = Math.Max(Math.Max(0, clipTop), y);
        var y1 = Math.Min(Math.Min(options.FrameHeight, clipBottom), y + height);
        for (var row = y0; row < y1; row++)
        {
            var offset = (row * stride) + (x0 * 4);
            for (var column = x0; column < x1; column++)
            {
                buffer[offset] = luminance;
                buffer[offset + 1] = luminance;
                buffer[offset + 2] = luminance;
                buffer[offset + 3] = 255;
                offset += 4;
            }
        }
    }
}
