using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition.Grid;

namespace TarkovCompanion.UnitTests.StashScan;

public sealed class CaseWindowLocatorTests
{
    private const int Width = 1920;
    private const int Height = 1080;
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FindsTheGridInsideTheGoldFrameUnderTheTitleBar()
    {
        var image = Frame((left: 400, top: 100, right: 400 + 892, bottom: 100 + 915, rightEdge: true));

        var spec = new CaseWindowLocator().Locate(image);

        Assert.NotNull(spec);
        Assert.Equal(new PixelRect(405, 127, 14 * 63, 14 * 63), spec!.Bounds);
        Assert.Equal(14, spec.Columns);
        Assert.Equal(14, spec.Rows);
    }

    [Fact]
    public void A_scrollbar_over_the_right_edge_does_not_lose_the_window()
    {
        var image = Frame((left: 300, top: 200, right: 300 + 262, bottom: 200 + 285, rightEdge: false));

        var spec = new CaseWindowLocator().Locate(image);

        Assert.NotNull(spec);
        Assert.Equal(4, spec!.Columns);
        Assert.Equal(4, spec.Rows);
    }

    [Fact]
    public void No_gold_frame_is_no_case_window()
    {
        var image = Frame();
        Paint(image, 100, 500, 1000, 1, Grey);

        Assert.Null(new CaseWindowLocator().Locate(image));
    }

    [Fact]
    public void A_gold_line_with_no_left_edge_is_not_a_window()
    {
        // Two gold item captions stacked, say: straight lines, but nothing joins them.
        var image = Frame();
        Paint(image, 100, 300, 600, 1, Gold);
        Paint(image, 100, 700, 600, 1, Gold);

        Assert.Null(new CaseWindowLocator().Locate(image));
    }

    private static readonly byte[] Gold = [97, 201, 231];
    private static readonly byte[] Grey = [120, 120, 120];

    private static CapturedImage Frame(params (int left, int top, int right, int bottom, bool rightEdge)[] windows)
    {
        var image = new CapturedImage(new byte[Width * Height * 4], Width, Height, Width * 4, PixelFormat.Bgra8888, Now, "painted");
        foreach (var (left, top, right, bottom, rightEdge) in windows)
        {
            Paint(image, left, top, right - left + 1, 1, Gold);
            Paint(image, left, bottom, right - left + 1, 1, Gold);
            Paint(image, left, top, 1, bottom - top + 1, Gold);
            if (rightEdge)
            {
                Paint(image, right, top, 1, bottom - top + 1, Gold);
            }
        }

        return image;
    }

    private static void Paint(CapturedImage image, int x, int y, int width, int height, byte[] bgr)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.AsMemory(image.Pixels).Span;
        for (var row = y; row < y + height; row++)
        {
            for (var column = x; column < x + width; column++)
            {
                var offset = (row * image.Stride) + (column * 4);
                pixels[offset] = bgr[0];
                pixels[offset + 1] = bgr[1];
                pixels[offset + 2] = bgr[2];
                pixels[offset + 3] = 255;
            }
        }
    }
}
