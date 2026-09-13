using TarkovCompanion.App.Services.Diagnostics;
using TarkovCompanion.Core.Domain.Recognition;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Pointing the probe at part of a frame instead of all of it.
/// </summary>
/// <remarks>
/// Measured on a real 3840x1080 screenshot: probing the whole frame returned 240 lines with
/// the extract names not among the first twelve any preparation printed, and probing the panel
/// alone returned sixteen with every name legible. Without this, somebody probing a whole frame
/// concludes the text is unreadable when it is merely buried.
/// </remarks>
public sealed class OcrProbeRegionTests
{
    [Fact]
    public void Reads_a_region_as_fractions_of_the_frame()
    {
        // The extract panel, as measured: top-right, x 0.85 across, y 0.005 down.
        var region = OcrProbe.ParseRegion("0.85,0.005,0.145,0.5", Frame(3840, 1080));

        Assert.NotNull(region);
        Assert.Equal(3264, region.X);
        Assert.Equal(5, region.Y);
        Assert.Equal(557, region.Width);
        Assert.Equal(540, region.Height);
    }

    [Fact]
    public void Puts_the_same_fractions_somewhere_else_on_a_different_shape()
    {
        // Which is the point of fractions: a region measured on one screenshot is usually
        // pointed at another.
        var region = OcrProbe.ParseRegion("0.85,0.005,0.145,0.5", Frame(1920, 1080));

        Assert.NotNull(region);
        Assert.Equal(1632, region.X);
        Assert.Equal(278, region.Width);
    }

    [Fact]
    public void Keeps_a_region_inside_the_frame()
    {
        var region = OcrProbe.ParseRegion("0.9,0.9,0.5,0.5", Frame(1000, 1000));

        Assert.NotNull(region);
        Assert.Equal(900, region.X);
        Assert.Equal(100, region.Width);
        Assert.Equal(100, region.Height);
    }

    [Theory]
    [InlineData("0.1,0.1,0.1")]
    [InlineData("0.1,0.1,0.1,0.1,0.1")]
    [InlineData("a,b,c,d")]
    [InlineData("0.1,0.1,0,0.5")]
    [InlineData("1.0,0,0.5,0.5")]
    public void Ignores_a_region_it_cannot_use_rather_than_silently_reading_everything(string value) =>
        // Silently falling back to the whole frame would look like the region simply did not
        // help, which is the wrong conclusion to leave somebody with.
        Assert.Null(OcrProbe.ParseRegion(value, Frame(1920, 1080)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Reads_the_whole_frame_when_no_region_was_asked_for(string? value) =>
        Assert.Null(OcrProbe.ParseRegion(value, Frame(1920, 1080)));

    private static CapturedImage Frame(int width, int height) =>
        new(new byte[width * height * 4], width, height, width * 4, PixelFormat.Bgra8888, DateTimeOffset.UnixEpoch, "test");
}
