using TarkovCompanion.Application.Services;

namespace TarkovCompanion.UnitTests;

public sealed class ScreenshotFilenameParserTests
{
    [Fact]
    public void ParsesObservedFilenameConvention()
    {
        var parser = new ScreenshotFilenameParser();

        var result = parser.TryParse(
            "2026-09-04[18-33]_7.86, 38.06, -27.57_-0.03307, -0.13322, 0.00384, -0.99053_21.87 (0).png",
            TimeSpan.FromHours(-4),
            out var position);

        Assert.True(result);
        Assert.NotNull(position);
        Assert.Equal(7.86, position.Position.X, 3);
        Assert.Equal(38.06, position.Position.Y, 3);
        Assert.Equal(-27.57, position.Position.Z, 3);
        Assert.NotNull(position.InGameTime);
        Assert.Equal(21.87, position.InGameTime.Value.TotalSeconds, 2);
        Assert.Equal(0, position.DuplicateIndex);
        Assert.InRange(position.HeadingDegrees, 0, 360);
    }

    [Theory]
    [InlineData("screenshot.png")]
    [InlineData("2026-99-99[18-33]_1, 2, 3_0, 0, 0, 1.png")]
    [InlineData("2026-09-04[18-33]_1, 2, 3_0, 0, 0, 0.png")]
    [InlineData("2026-09-04[18-33]_1, 2, 3_0, 0, 0, 1.exe")]
    public void RejectsMalformedOrUnsafeNames(string filename)
    {
        var parser = new ScreenshotFilenameParser();

        Assert.False(parser.TryParse(filename, TimeSpan.Zero, out _));
    }

    [Fact]
    public void ConvertsKnownQuarterTurnToHeading()
    {
        var halfSqrt = Math.Sqrt(0.5);
        var heading = ScreenshotFilenameParser.HeadingDegrees(new(0, halfSqrt, 0, halfSqrt));

        Assert.Equal(90, heading, 6);
    }
}
