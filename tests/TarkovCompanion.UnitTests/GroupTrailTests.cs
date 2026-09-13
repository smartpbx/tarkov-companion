using TarkovCompanion.App.ViewModels.Maps;
using Avalonia;
using Avalonia.Collections;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// Drawing where a squadmate has been, faded by how long ago they were there.
/// </summary>
public sealed class GroupTrailTests
{
    [Fact]
    public void Fades_by_the_age_of_its_oldest_point()
    {
        // A member who took three screenshots in ten seconds and then none for five minutes
        // must not draw a line implying they walked it recently.
        var fresh = Trail(TimeSpan.FromSeconds(20));
        var stale = Trail(TimeSpan.FromMinutes(4));
        var ancient = Trail(TimeSpan.FromMinutes(30));

        Assert.NotEqual(fresh.StrokeColor, stale.StrokeColor);
        Assert.NotEqual(stale.StrokeColor, ancient.StrokeColor);
    }

    [Theory]
    [InlineData(30, "B0")]
    [InlineData(120, "70")]
    [InlineData(270, "40")]
    [InlineData(3600, "22")]
    public void Grows_fainter_the_older_it_gets(int seconds, string alpha)
    {
        var trail = Trail(TimeSpan.FromSeconds(seconds));

        Assert.StartsWith("#" + alpha, trail.StrokeColor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Is_drawn_in_that_members_own_colour()
    {
        // The same colour as their dot and their row in the panel, which is the only thing
        // joining three lines on a map to three names beside it.
        var mine = Trail(TimeSpan.FromSeconds(10)) with { Rgb = "#77B895" };
        var theirs = Trail(TimeSpan.FromSeconds(10)) with { Rgb = "#C7A66B" };

        Assert.Contains("77B895", mine.StrokeColor, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("C7A66B", theirs.StrokeColor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void One_point_is_not_a_path()
    {
        var single = new GroupTrailViewModel("Someone", [new Point(1, 1)], TimeSpan.Zero);

        Assert.False(single.HasPath);
        Assert.True(Trail(TimeSpan.Zero).HasPath);
    }

    private static GroupTrailViewModel Trail(TimeSpan oldest) =>
        new("Someone", [new Point(0, 0), new Point(10, 10)], oldest);
}
