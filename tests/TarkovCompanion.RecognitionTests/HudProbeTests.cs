using TarkovCompanion.Core.Domain.Recognition;
using TarkovCompanion.Infrastructure.Recognition;

namespace TarkovCompanion.RecognitionTests;

/// <summary>
/// Finding the game's own display in a frame, and saying so when it is not drawn.
/// </summary>
/// <remarks>
/// Every number asserted here came off a real installation: 3840x1080, the display anchored to
/// the left edge, two bars running x 51..196 with the upper at y 1027 in 43,129,151 and the
/// lower at y 1036 in 43,151,129, and no display at all in 35 of 261 in-raid screenshots.
/// </remarks>
public sealed class HudProbeTests
{
    /// <summary>The upper bar's measured colour: blue over green.</summary>
    private static readonly (byte R, byte G, byte B) UpperBar = (43, 129, 151);

    /// <summary>The lower bar's measured colour: green over blue.</summary>
    private static readonly (byte R, byte G, byte B) LowerBar = (43, 151, 129);

    [Fact]
    public void Finds_both_bars_where_they_were_measured_on_a_real_frame()
    {
        var image = Frame(3840, 1080);
        Draw(image, 51, 196, 1027, 1028, UpperBar);
        Draw(image, 51, 196, 1036, 1037, LowerBar);

        var reading = HudProbe.Read(image);

        Assert.True(reading.IsPresent);
        Assert.Equal(2, reading.Bars.Count);
        var blue = reading.Bar(HudBarKind.Blue);
        var green = reading.Bar(HudBarKind.Green);
        Assert.NotNull(blue);
        Assert.NotNull(green);
        Assert.Equal(new PixelRect(51, 1027, 146, 2), blue.Bounds);
        Assert.Equal(new PixelRect(51, 1036, 146, 2), green.Bounds);
        Assert.Equal(146, blue.Length);
        Assert.Equal(new PixelRect(51, 1027, 146, 11), reading.Bounds);
    }

    [Fact]
    public void Tells_the_two_bars_apart_by_colour_rather_than_by_the_gap()
    {
        // The real bars sit six pixels apart, so contiguity would separate them. Colour is the
        // thing that always separates them, and this is the frame that proves the difference:
        // the two are drawn touching.
        var image = Frame(3840, 1080);
        Draw(image, 51, 196, 1027, 1029, UpperBar);
        Draw(image, 51, 160, 1030, 1032, LowerBar);

        var reading = HudProbe.Read(image);

        Assert.Equal(2, reading.Bars.Count);
        Assert.Equal(HudBarKind.Blue, reading.Bars[0].Kind);
        Assert.Equal(146, reading.Bars[0].Length);
        Assert.Equal(HudBarKind.Green, reading.Bars[1].Kind);
        Assert.Equal(110, reading.Bars[1].Length);
    }

    [Fact]
    public void Says_the_display_was_not_drawn_rather_than_reporting_nothing_left()
    {
        // 35 of 261 real in-raid screenshots. The game fades the display out, and a companion
        // that answered "empty" here would be inventing a reading off a frame that has none.
        var reading = HudProbe.Read(Frame(3840, 1080));

        Assert.False(reading.IsPresent);
        Assert.Contains("faded", reading.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(reading.Bars);
        Assert.Null(reading.Bounds);
        Assert.Null(reading.Bar(HudBarKind.Blue));
    }

    [Fact]
    public void Ignores_the_corner_the_first_attempt_looked_in()
    {
        // The first guess searched the bottom right on the reasoning that health widgets live
        // there. On this frame that region is grass. A bar drawn there is not the display.
        var image = Frame(3840, 1080);
        Draw(image, 3600, 3740, 1027, 1029, UpperBar);

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
        // of width would have moved off them; this one has not.
        var image = Frame(1920, 1080);
        Draw(image, 51, 196, 1027, 1029, UpperBar);

        Assert.True(HudProbe.Read(image).IsPresent);
    }

    [Fact]
    public void Ignores_a_run_too_short_to_be_a_bar()
    {
        var image = Frame(3840, 1080);
        Draw(image, 51, 58, 1027, 1029, UpperBar);

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
        Draw(bgra, 51, 196, 1027, 1029, UpperBar);
        var rgba = Frame(1920, 1080, PixelFormat.Rgba8888);
        Draw(rgba, 51, 196, 1027, 1029, UpperBar);

        var fromBgra = Assert.Single(HudProbe.Read(bgra).Bars);
        var fromRgba = Assert.Single(HudProbe.Read(rgba).Bars);
        Assert.Equal(fromBgra, fromRgba);
    }

    [Theory]
    [InlineData(43, 129, 151, HudBarKind.Blue)]
    [InlineData(34, 114, 131, HudBarKind.Blue)]
    [InlineData(43, 151, 129, HudBarKind.Green)]
    public void Classifies_the_colours_a_real_frame_actually_contained(byte red, byte green, byte blue, HudBarKind expected) =>
        Assert.Equal(expected, HudProbe.Classify(red, green, blue));

    [Theory]
    [InlineData(22, 94, 105)]   // the antialiased row under the upper bar
    [InlineData(22, 105, 83)]   // the antialiased row under the lower bar
    [InlineData(23, 30, 13)]    // the commonest colour inside the translucent silhouette: grass
    [InlineData(44, 46, 45)]    // the grey separator between the bars
    [InlineData(60, 140, 140)]  // blue and green exactly equal, which neither bar produces
    public void Leaves_out_what_is_not_a_bar(byte red, byte green, byte blue) =>
        Assert.Null(HudProbe.Classify(red, green, blue));

    [Fact]
    public void Reports_no_fraction_until_a_longer_bar_has_been_seen()
    {
        // A companion that answered "full" the first time it saw a bar would be right only by
        // accident, and wrong in the one case that matters: the first screenshot somebody takes
        // after running themselves empty.
        var bar = new HudBar(HudBarKind.Blue, new(51, 1027, 100, 3));

        Assert.Null(bar.Fraction(0));
        Assert.Null(bar.Fraction(80));
        Assert.Equal(1, bar.Fraction(100));
        Assert.Equal(0.5, bar.Fraction(200));
    }

    [Fact]
    public void Refuses_limb_health_when_the_outline_is_too_dim_to_tell_from_the_scene()
    {
        // Measured on a real frame over grass: the brightest pixel anywhere in the silhouette
        // reaches 83, the median is 31, and nothing has all three channels above 150. Run over
        // a frame this dim, every classifier of this kind answers "destroyed" for a healthy
        // limb, and "destroyed" again when the display was never drawn.
        var image = Frame(3840, 1080);
        Fill(image, HudProbe.SilhouetteRegion(image), (23, 30, 13));
        Draw(image, 60, 170, 900, 960, (81, 88, 64));

        var silhouette = HudProbe.ReadSilhouette(image);

        Assert.False(silhouette.IsLegible);
        Assert.Equal(83, silhouette.Brightest);
        Assert.Contains("83", silhouette.Detail, StringComparison.Ordinal);
        Assert.Contains("not", silhouette.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_a_sand_bank_too_although_it_is_the_brightest_thing_measured()
    {
        // The frame that would have produced a false injury. Sand at 178,140,99 showing through
        // a translucent figure passes every red test going, and the brightest pixel in the
        // whole region still only reaches 146 by this weighting, against the 150 an outline needs.
        var image = Frame(3840, 1080);
        Fill(image, HudProbe.SilhouetteRegion(image), (163, 113, 68));
        Draw(image, 60, 170, 900, 960, (178, 140, 99));

        var silhouette = HudProbe.ReadSilhouette(image);

        Assert.False(silhouette.IsLegible);
        Assert.Equal(146, silhouette.Brightest);
    }

    [Fact]
    public void Accepts_an_outline_bright_enough_to_classify()
    {
        var image = Frame(3840, 1080);
        Draw(image, 60, 170, 900, 960, (200, 200, 200));

        var silhouette = HudProbe.ReadSilhouette(image);

        Assert.True(silhouette.IsLegible);
        Assert.True(silhouette.Brightest >= 150);
    }

    [Fact]
    public void Looks_above_the_bars_rather_than_at_them()
    {
        // The figure sits 30 pixels above the bars and the bars move as the player drains them,
        // so the region is anchored to the frame rather than to the bars underneath it.
        var region = HudProbe.SilhouetteRegion(Frame(3840, 1080));

        Assert.True(region.Y <= 837, $"top {region.Y} should include the measured figure at 837");
        Assert.True(region.Y + region.Height >= 997, "the region should reach the figure's feet at 997");
        Assert.True(region.Y + region.Height < 1027, "the region should stop short of the bars at 1027");
        Assert.True(region.X <= 55 && region.X + region.Width >= 180, "it should span the measured figure");
    }

    [Fact]
    public void Says_it_looked_at_nothing_when_the_display_was_not_drawn()
    {
        var reading = HudProbe.Read(Frame(3840, 1080));

        Assert.False(reading.Silhouette.IsLegible);
        Assert.Equal(0, reading.Silhouette.Brightest);
        Assert.Contains("no display", reading.Silhouette.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static void Fill(CapturedImage image, PixelRect region, (byte R, byte G, byte B) colour) =>
        Draw(image, region.X, region.X + region.Width - 1, region.Y, region.Y + region.Height - 1, colour);

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
