using TarkovCompanion.Application.Services;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// [V2 rough package 43a] Telling "this shot never had a position" apart from "this shot has one
/// and we cannot read it". Reported by Clayton: the self-test called his post-raid screenshot
/// broken, and it was the game behaving exactly as it should.
/// </summary>
public sealed class ScreenshotNameKindTests
{
    [Theory]
    // The name from Clayton's own run, duplicate suffix and all.
    [InlineData("2026-09-18[19-03]_19.67 (1).png")]
    [InlineData("2026-09-18[19-03]_19.67.png")]
    [InlineData("2026-09-18[19-03].png")]
    [InlineData("2026-09-18[19-03] (2).jpg")]
    public void A_shot_taken_outside_a_raid_is_recognised_as_one(string name) =>
        Assert.Equal(ScreenshotNameKind.OutsideRaid, ScreenshotFilenameParser.Classify(name));

    [Theory]
    [InlineData("2026-09-18[19-03]_-125.4, 2.3, 189.7_0.0, 0.7, 0.0, -0.7_12.34.png")]
    [InlineData("2026-09-18[19-03]_-125.4, 2.3, 189.7_0.0, 0.7, 0.0, -0.7_12.34 (1).png")]
    [InlineData("2026-09-18[19-03]_-125.4, 2.3, 189.7_0.0, 0.7, 0.0, -0.7.png")]
    public void A_shot_taken_in_a_raid_is_recognised_as_one(string name) =>
        Assert.Equal(ScreenshotNameKind.InRaid, ScreenshotFilenameParser.Classify(name));

    [Theory]
    [InlineData("holiday.png")]
    [InlineData("Screenshot 2026-09-18 190312.png")]
    [InlineData("2026-09-18[19-03]_19.67.txt")]
    public void Anything_else_is_not_one_of_the_games_names(string name) =>
        Assert.Equal(ScreenshotNameKind.Unrecognized, ScreenshotFilenameParser.Classify(name));

    [Fact]
    public void An_in_raid_name_is_the_one_that_must_also_parse()
    {
        // The classification promises the blocks are there; the parser is what reads them. A name
        // that clears the first and fails the second is the only case worth calling broken.
        const string name = "2026-09-18[19-03]_-125.4, 2.3, 189.7_0.0, 0.7, 0.0, -0.7_12.34.png";
        Assert.Equal(ScreenshotNameKind.InRaid, ScreenshotFilenameParser.Classify(name));

        Assert.True(new ScreenshotFilenameParser().TryParse(name, TimeSpan.Zero, out var position));
        Assert.NotNull(position);
    }

    [Fact]
    public void A_shot_from_outside_a_raid_does_not_parse_and_that_is_correct()
    {
        Assert.False(new ScreenshotFilenameParser().TryParse("2026-09-18[19-03]_19.67 (1).png", TimeSpan.Zero, out _));
    }
}
