using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

/// <summary>
/// Finding the stash grid's cells so each can be read on its own.
/// </summary>
/// <remarks>
/// Measured on a real 3840x1080 screenshot: the pitch is 63 pixels on both axes, and ten
/// consecutive vertical grid lines all satisfied x mod 63 = 19 exactly. Reading a band of the
/// grid in one pass merged neighbours — two adjacent captions came back as the single line
/// "Mel TT @55Al1", which is two items and neither of them.
/// </remarks>
public sealed class StashGridTests
{
    private const int Pitch = 63;

    [Fact]
    public void Reads_the_pitch_from_the_frames_height()
    {
        // 63 at 1080, and treated as a fraction of height rather than a constant: a 32:9 frame
        // and a 16:9 frame share a height and not a width.
        Assert.Equal(63, StashGrid.Pitch(Frame(3840, 1080)));
        Assert.Equal(63, StashGrid.Pitch(Frame(1920, 1080)));
        Assert.Equal(84, StashGrid.Pitch(Frame(2560, 1440)));
    }

    [Fact]
    public void Finds_the_phase_the_lines_actually_sit_on()
    {
        // The real measurement: lines at x = 19 + 63k.
        var image = Frame(3840, 1080);
        var region = new PixelRect(2224, 630, 630, 315);
        Paint(image, region, 150);
        DrawGrid(image, region, phaseX: 19, phaseY: 18);

        Assert.Equal(2224 + ((19 - (2224 % Pitch) + Pitch) % Pitch), Found(image, region, vertical: true));
    }

    [Fact]
    public void Survives_icons_hiding_some_of_the_lines()
    {
        // An icon can hide a line and cannot hide most of them, which is why the phase is
        // scored across the whole panel rather than found line by line.
        var image = Frame(3840, 1080);
        var region = new PixelRect(2224, 630, 630, 315);
        Paint(image, region, 150);
        DrawGrid(image, region, phaseX: 19, phaseY: 18);
        // Two columns' worth of bright icon painted straight over the lines.
        Paint(image, new PixelRect(2287, 630, 126, 315), 230);

        Assert.NotNull(StashGrid.FindPhase(image, region, Pitch, vertical: true));
        Assert.NotEmpty(StashGrid.Cells(image, region));
    }

    [Fact]
    public void Says_there_is_no_grid_rather_than_inventing_one()
    {
        // The best phase of a picture with no grid in it is still the best phase of something.
        // A caller handed a made-up grid crops arbitrary rectangles and reads whatever is in
        // them, which is the failure that looks like working.
        var image = Frame(3840, 1080);
        var region = new PixelRect(2224, 630, 630, 315);
        Paint(image, region, 150);

        Assert.Null(StashGrid.FindPhase(image, region, Pitch, vertical: true));
        Assert.Empty(StashGrid.Cells(image, region));
    }

    [Fact]
    public void Cuts_cells_that_never_include_the_grid_line_itself()
    {
        // A dark line along an edge is a stroke the reader would try to make a letter out of.
        var image = Frame(3840, 1080);
        var region = new PixelRect(2224, 630, 630, 315);
        Paint(image, region, 150);
        DrawGrid(image, region, phaseX: 19, phaseY: 18);

        var cells = StashGrid.Cells(image, region);

        Assert.NotEmpty(cells);
        foreach (var cell in cells)
        {
            Assert.Equal(Pitch - 2, cell.Bounds.Width);
            Assert.Equal(Pitch - 2, cell.Bounds.Height);
        }
    }

    [Fact]
    public void Crops_the_caption_band_rather_than_the_whole_cell()
    {
        // The caption is top-right and the stack count bottom-right. Taking the whole cell puts
        // "50" and the label in one reading, which is the merging again by another route.
        var image = Frame(3840, 1080);
        var region = new PixelRect(2224, 630, 630, 315);
        Paint(image, region, 150);
        DrawGrid(image, region, phaseX: 19, phaseY: 18);

        var cell = StashGrid.Cells(image, region)[0];

        Assert.Equal(cell.Bounds.X, cell.Caption.X);
        Assert.Equal(cell.Bounds.Y, cell.Caption.Y);
        Assert.Equal(cell.Bounds.Width, cell.Caption.Width);
        Assert.True(cell.Caption.Height < cell.Bounds.Height / 2, "the caption band is the top of the cell");
        Assert.True(cell.Caption.Height >= 1);
    }

    [Fact]
    public void A_region_too_small_to_hold_a_grid_holds_no_cells()
    {
        var image = Frame(3840, 1080);

        Assert.Empty(StashGrid.Cells(image, new PixelRect(2224, 630, 40, 40)));
        Assert.Null(StashGrid.FindPhase(image, new PixelRect(2224, 630, 40, 40), Pitch, vertical: true));
    }

    private static int? Found(CapturedImage image, PixelRect region, bool vertical) =>
        StashGrid.FindPhase(image, region, Pitch, vertical);

    private static CapturedImage Frame(int width, int height) =>
        new(new byte[width * height * 4], width, height, width * 4, PixelFormat.Bgra8888, DateTimeOffset.UnixEpoch, "test");

    private static void Paint(CapturedImage image, PixelRect region, byte grey)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.AsMemory(image.Pixels).Span;
        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            for (var x = region.X; x < region.X + region.Width; x++)
            {
                var offset = (y * image.Stride) + (x * 4);
                pixels[offset] = grey;
                pixels[offset + 1] = grey;
                pixels[offset + 2] = grey;
                pixels[offset + 3] = 255;
            }
        }
    }

    /// <summary>Dark lines at the measured phase: x = phaseX + 63k, y = phaseY + 63k.</summary>
    private static void DrawGrid(CapturedImage image, PixelRect region, int phaseX, int phaseY)
    {
        for (var x = region.X; x < region.X + region.Width; x++)
        {
            if (((x % Pitch) + Pitch) % Pitch == phaseX)
            {
                Paint(image, new PixelRect(x, region.Y, 1, region.Height), 20);
            }
        }

        for (var y = region.Y; y < region.Y + region.Height; y++)
        {
            if (((y % Pitch) + Pitch) % Pitch == phaseY)
            {
                Paint(image, new PixelRect(region.X, y, region.Width, 1), 20);
            }
        }
    }
}
