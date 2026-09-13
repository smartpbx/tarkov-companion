using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

/// <summary>
/// Finding the game's own display in a frame, and saying so when it is not drawn.
/// </summary>
/// <remarks>
/// The measurements these assert against came off a real installation: 3840x1080, the display
/// anchored to the left edge, the stamina bar at x 51..196 and y 1027..1036, and no display at
/// all in two of five in-raid screenshots.
/// </remarks>
public sealed class HudProbeTests
{
    /// <summary>The stamina bar's measured colour on a real frame.</summary>
    private static readonly (byte R, byte G, byte B) BarColour = (60, 140, 170);

    [Fact]
    public void Finds_the_bar_where_it_was_measured_on_a_real_frame()
    {
        var image = Frame(3840, 1080);
        Draw(image, 51, 196, 1027, 1036, BarColour);

        var reading = HudProbe.Read(image);

        Assert.True(reading.IsPresent);
        var bar = Assert.Single(reading.Bars);
        Assert.Equal(51, bar.Bounds.X);
        Assert.Equal(1027, bar.Bounds.Y);
        Assert.Equal(146, bar.Length);
        Assert.Equal(10, bar.Bounds.Height);
        Assert.Equal(1460, bar.FilledPixels);
    }

    [Fact]
    public void Says_the_display_was_not_drawn_rather_than_reporting_nothing_left()
    {
        // Two of five real in-raid screenshots. The game fades the display out, and a
        // companion that answered "zero stamina" here would be inventing a reading.
        var reading = HudProbe.Read(Frame(3840, 1080));

        Assert.False(reading.IsPresent);
        Assert.Contains("faded", reading.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(reading.Bars);
        Assert.Null(reading.Bounds);
    }

    [Fact]
    public void Ignores_the_corner_the_first_attempt_looked_in()
    {
        // The first guess searched the bottom right on the reasoning that health widgets live
        // there. On this frame that region is grass. A bar drawn there is not the display.
        var image = Frame(3840, 1080);
        Draw(image, 3600, 3740, 1027, 1036, BarColour);

        Assert.False(HudProbe.Read(image).IsPresent);
    }

    [Theory]
    [InlineData(3840, 1080)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(5120, 1440)]
    public void Searches_a_region_measured_from_the_left_and_bottom_edges(int width, int height)
    {
        // Not a fraction of the width. At 32:9 a left-anchored overlay occupies half the
        // fraction of width it does at 16:9, so a width-based region calibrated on either one
        // misses on the other; measuring in heights is what the overlay itself scales by.
        var region = HudProbe.SearchRegion(Frame(width, height));

        Assert.Equal(0, region.X);
        Assert.Equal(height - region.Height, region.Y);
        Assert.Equal((int)Math.Round(height * 0.40), region.Height);
        Assert.Equal(Math.Min(width, (int)Math.Round(height * 0.33)), region.Width);
    }

    [Fact]
    public void Finds_the_same_bar_on_a_sixteen_by_nine_frame()
    {
        // The same pixels, on a frame less than half as wide. A region expressed as a fraction
        // of width would have moved; this one has not.
        var image = Frame(1920, 1080);
        Draw(image, 51, 196, 1027, 1036, BarColour);

        Assert.True(HudProbe.Read(image).IsPresent);
    }

    [Fact]
    public void Separates_bars_that_sit_apart_and_reports_the_longer_first()
    {
        var image = Frame(3840, 1080);
        Draw(image, 51, 120, 1005, 1012, BarColour);
        Draw(image, 51, 196, 1027, 1036, BarColour);

        var reading = HudProbe.Read(image);

        Assert.Equal(2, reading.Bars.Count);
        Assert.Equal(146, reading.Bars[0].Length);
        Assert.Equal(70, reading.Bars[1].Length);
        Assert.Contains("2 bars", reading.Detail, StringComparison.Ordinal);
        Assert.Equal(new PixelRect(51, 1005, 146, 32), reading.Bounds);
    }

    [Fact]
    public void Ignores_a_run_too_short_to_be_a_bar()
    {
        var image = Frame(3840, 1080);
        Draw(image, 51, 58, 1027, 1036, BarColour);

        Assert.False(HudProbe.Read(image).IsPresent);
    }

    [Fact]
    public void Does_not_mistake_sky_or_water_for_a_bar()
    {
        // Both are blue. Neither is this green, which is what the blue-over-red margin is for.
        var image = Frame(3840, 1080);
        Draw(image, 20, 300, 900, 1000, (120, 150, 210));
        Draw(image, 20, 300, 1010, 1060, (40, 60, 130));

        Assert.False(HudProbe.Read(image).IsPresent);
    }

    [Fact]
    public void Reads_nothing_out_of_a_grey_frame()
    {
        // A grey buffer has one byte per pixel and cannot carry a colour. Reading it as three
        // channels would find bars in a picture that has none.
        var image = new CapturedImage(
            new byte[1920 * 1080],
            1920,
            1080,
            1920,
            PixelFormat.Gray8,
            DateTimeOffset.UnixEpoch,
            "test");

        Assert.False(HudProbe.Read(image).IsPresent);
    }

    [Fact]
    public void Reads_the_same_bar_out_of_either_channel_order()
    {
        var bgra = Frame(1920, 1080);
        Draw(bgra, 51, 196, 1027, 1036, BarColour);
        var rgba = Frame(1920, 1080, PixelFormat.Rgba8888);
        Draw(rgba, 51, 196, 1027, 1036, BarColour);

        Assert.Equal(
            HudProbe.Read(bgra).Bars[0].Bounds,
            HudProbe.Read(rgba).Bars[0].Bounds);
    }

    private static CapturedImage Frame(int width, int height, PixelFormat format = PixelFormat.Bgra8888) =>
        new(new byte[width * height * 4], width, height, width * 4, format, DateTimeOffset.UnixEpoch, "test");

    private static void Draw(
        CapturedImage image,
        int left,
        int right,
        int top,
        int bottom,
        (byte R, byte G, byte B) colour)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.AsMemory(image.Pixels).Span;
        var redOffset = image.Format == PixelFormat.Rgba8888 ? 0 : 2;
        var blueOffset = image.Format == PixelFormat.Rgba8888 ? 2 : 0;
        for (var y = top; y <= bottom; y++)
        {
            for (var x = left; x <= right; x++)
            {
                var offset = (y * image.Stride) + (x * 4);
                pixels[offset + redOffset] = colour.R;
                pixels[offset + 1] = colour.G;
                pixels[offset + blueOffset] = colour.B;
                pixels[offset + 3] = 255;
            }
        }
    }
}
