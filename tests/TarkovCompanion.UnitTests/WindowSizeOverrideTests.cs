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
}
