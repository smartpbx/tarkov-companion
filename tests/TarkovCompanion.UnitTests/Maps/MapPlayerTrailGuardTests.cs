using System.Collections.Immutable;
using TarkovCompanion.App.ViewModels.Maps;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests.Maps;

/// <summary>
/// ShowPlayer skips its projection when the player and the trail are unchanged, and the store
/// copies the trail into a new boxed immutable array every time it publishes the raid, so the
/// trail has to be compared by what is in it and not by which object it is.
/// </summary>
public sealed class MapPlayerTrailGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_copy_of_the_trail_with_the_same_points_is_the_same_trail()
    {
        var points = Enumerable.Range(0, 30).Select(Point).ToArray();
        IReadOnlyList<ScreenshotPosition> first = points.ToImmutableArray();
        IReadOnlyList<ScreenshotPosition> second = points.ToImmutableArray();

        Assert.NotSame(first, second);
        Assert.True(MapViewModel.SameTrail(first, second));
    }

    [Fact]
    public void A_trail_with_another_point_or_another_length_is_not()
    {
        var points = Enumerable.Range(0, 5).Select(Point).ToArray();

        Assert.False(MapViewModel.SameTrail(points, [.. points, Point(5)]));
        Assert.False(MapViewModel.SameTrail(points, [.. points[..4], Point(9)]));
        Assert.True(MapViewModel.SameTrail([], []));
    }

    private static ScreenshotPosition Point(int index) => new(
        Start.AddSeconds(index * 30),
        new(index, 0, index),
        default,
        index,
        null,
        null,
        $"shot-{index}.png");
}
