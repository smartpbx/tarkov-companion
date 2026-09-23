using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.UnitTests.LootScan;

/// <summary>
/// The Gear-screen reader on painted frames laid out with the geometry measured on the real
/// 2026-09-20 screenshots: frame grey (88, 93, 96), 63-pixel cells, a 16:9 interface centred in a
/// 3840x1080 frame. What it is measured against in real pixels is
/// <c>RealGearScreenMeasurementTests</c>; this holds the classification logic.
/// </summary>
public sealed class GearScreenLayoutReaderTests
{
    private const int Width = 3840;
    private const int Height = 1080;

    [Fact]
    public void ALootScreenIsSplitIntoLootRigPocketsAndBackpack()
    {
        var frame = new Painter();
        PaintCarried(frame, pocketsTop: 418, backpackTop: 528);
        frame.Grid(2224, 78, columns: 3, rows: 3);

        var layout = new GearScreenLayoutReader().Read(frame.Image());

        Assert.NotNull(layout);
        Assert.Equal(63, layout!.Pitch);
        var loot = Assert.Single(layout.In(GearGridSection.Loot));
        Assert.Equal((2224, 78, 3, 3), (loot.Frame.X, loot.Frame.Y, loot.Columns, loot.Rows));
        Assert.Equal(new PixelRect(2225, 79, 189, 189), loot.Lattice.Bounds);
        Assert.Equal(8, layout.In(GearGridSection.TacticalRig).Count());
        Assert.Equal(4, layout.In(GearGridSection.Pockets).Count());
        var backpack = Assert.Single(layout.In(GearGridSection.Backpack));
        Assert.Equal((4, 3), (backpack.Columns, backpack.Rows));
        Assert.Empty(layout.In(GearGridSection.OtherCarried));
    }

    [Fact]
    public void EquipmentSlotBoxesAreNotGrids()
    {
        var frame = new Painter();
        PaintCarried(frame, pocketsTop: 418, backpackTop: 528);

        var layout = new GearScreenLayoutReader().Read(frame.Image());

        // The slot boxes at x 1616 are drawn top and bottom with the frame grey and no left edge.
        Assert.DoesNotContain(layout!.Grids, grid => grid.Frame.X == 1616 && grid.Section != GearGridSection.Pockets);
        Assert.Empty(layout.In(GearGridSection.Loot));
    }

    [Fact]
    public void AGridFarBelowThePocketsIsNotTheBackpack()
    {
        // No backpack: its empty slot sits between the pockets and the pouch, which starts 214
        // pixels below them on the real scav frame.
        var frame = new Painter();
        PaintCarried(frame, pocketsTop: 412, backpackTop: null);
        frame.Grid(1748, 755, columns: 3, rows: 3);

        var layout = new GearScreenLayoutReader().Read(frame.Image());

        Assert.Empty(layout!.In(GearGridSection.Backpack));
        var pouch = Assert.Single(layout.In(GearGridSection.OtherCarried));
        Assert.Equal((3, 3), (pouch.Columns, pouch.Rows));
    }

    [Fact]
    public void ABackpackWithATooltipAcrossItsTopFrameIsRecoveredFromItsLattice()
    {
        // A tooltip over the backpack's top edge leaves the cell lattice and side edge to recover.
        var frame = new Painter();
        PaintCarried(frame, pocketsTop: 418, backpackTop: 528);
        frame.Fill(1700, 520, 500, 20, 30);

        var layout = new GearScreenLayoutReader().Read(frame.Image());

        var backpack = Assert.Single(layout!.In(GearGridSection.Backpack));
        Assert.Equal((4, 3), (backpack.Columns, backpack.Rows));
        Assert.Equal(4, layout.In(GearGridSection.Pockets).Count());
    }

    [Fact]
    public void ABackpackAtTheBottomOfTheScreenIsStillReported()
    {
        var frame = new Painter();
        PaintCarried(frame, pocketsTop: 784, backpackTop: 888);

        var layout = new GearScreenLayoutReader().Read(frame.Image());

        var backpack = Assert.Single(layout!.In(GearGridSection.Backpack));
        Assert.Equal((4, 3), (backpack.Columns, backpack.Rows));
    }

    [Fact]
    public void AFrameWithNoGearScreenGivesNothing()
    {
        Assert.Null(new GearScreenLayoutReader().Read(new Painter().Image()));
    }

    private static void PaintCarried(Painter frame, int pocketsTop, int? backpackTop)
    {
        var rigTop = pocketsTop - 308;
        frame.SlotBox(1616, rigTop - 1);
        foreach (var top in new[] { rigTop, rigTop + 133 })
        {
            foreach (var left in new[] { 1751, 1821, 1891, 1961 })
            {
                frame.Grid(left, top, columns: 1, rows: 2);
            }
        }

        foreach (var left in new[] { 1616, 1686, 1756, 1826 })
        {
            frame.Grid(left, pocketsTop, columns: 1, rows: 1);
        }

        if (backpackTop is { } top2)
        {
            frame.SlotBox(1616, top2 - 1);
            frame.Grid(1748, top2, columns: 4, rows: 3);
        }
    }

    private sealed class Painter
    {
        private static readonly (byte B, byte G, byte R) FrameGrey = (96, 93, 88);
        private readonly byte[] _pixels = new byte[Width * Height * 4];

        public Painter()
        {
            Fill(0, 0, Width, Height, 8);
        }

        public void Fill(int x, int y, int width, int height, byte grey)
        {
            for (var row = y; row < y + height; row++)
            {
                for (var column = x; column < x + width; column++)
                {
                    Set(column, row, (grey, grey, grey));
                }
            }
        }

        /// <summary>A grid as the game draws it: an outer frame, dark cells, darker lines between.</summary>
        public void Grid(int x, int y, int columns, int rows)
        {
            var width = (63 * columns) + 2;
            var height = (63 * rows) + 2;
            Fill(x + 1, y + 1, width - 2, height - 2, 24);
            for (var column = 1; column < columns; column++)
            {
                Fill(x + 1 + (63 * column), y + 1, 1, height - 2, 45);
            }

            for (var row = 1; row < rows; row++)
            {
                Fill(x + 1, y + 1 + (63 * row), width - 2, 1, 45);
            }

            Line(x, y, width, horizontal: true);
            Line(x, y + height - 1, width, horizontal: true);
            Line(x, y, height, horizontal: false);
            Line(x + width - 1, y, height, horizontal: false);
        }

        /// <summary>An equipment slot: frame grey above and below, a softer grey at the sides.</summary>
        public void SlotBox(int x, int y)
        {
            Fill(x, y, 128, 128, 40);
            Line(x, y, 128, horizontal: true);
            Line(x, y + 128, 128, horizontal: true);
        }

        public CapturedImage Image() =>
            new(_pixels, Width, Height, Width * 4, PixelFormat.Bgra8888, DateTimeOffset.UnixEpoch, "painted-gear-screen");

        private void Line(int x, int y, int length, bool horizontal)
        {
            for (var step = 0; step < length; step++)
            {
                Set(horizontal ? x + step : x, horizontal ? y : y + step, FrameGrey);
            }
        }

        private void Set(int x, int y, (byte B, byte G, byte R) colour)
        {
            var offset = ((y * Width) + x) * 4;
            _pixels[offset] = colour.B;
            _pixels[offset + 1] = colour.G;
            _pixels[offset + 2] = colour.R;
            _pixels[offset + 3] = 255;
        }
    }
}
