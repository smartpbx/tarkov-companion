using TarkovCompanion.App.Services.Diagnostics;

namespace TarkovCompanion.UnitTests;

public sealed class WindowSizeOverrideTests
{
    [Fact]
    public void Parses_the_gallery_size_without_reporting_an_unknown_option()
    {
        var options = AppCommandLine.Parse(["--ui-shell", "v2-a", "--window-size", "1920x1080"]);

        Assert.Equal(new WindowSizeOverride(1920, 1080), options.WindowSize);
        Assert.Empty(options.UnknownOptions);
    }

    [Theory]
    [InlineData("1920")]
    [InlineData("1920x")]
    [InlineData("0x1080")]
    [InlineData("1920x-1")]
    [InlineData("1920x1080x32")]
    public void Refuses_a_size_that_cannot_describe_a_window(string value)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            AppCommandLine.Parse(["--window-size", value]));

        Assert.Contains("--window-size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sizes_the_client_so_the_whole_frame_matches_the_gallery_move()
    {
        // A Windows 11 frame: 8 px invisible borders left, right and bottom, a 31 px caption.
        var (width, height) = new WindowSizeOverride(1920, 1080).ClientSizeFor(1936, 1119, 1920, 1080);

        Assert.Equal(1904, width);
        Assert.Equal(1041, height);
    }

    [Fact]
    public void Asking_again_once_the_frame_fits_changes_nothing()
    {
        var size = new WindowSizeOverride(1920, 1080);

        Assert.Equal((1904d, 1041d), size.ClientSizeFor(1920, 1080, 1904, 1041));
    }

    [Fact]
    public void A_window_without_a_frame_keeps_the_requested_size()
    {
        Assert.Equal((560d, 720d), new WindowSizeOverride(560, 720).ClientSizeFor(560, 720, 560, 720));
    }
}
