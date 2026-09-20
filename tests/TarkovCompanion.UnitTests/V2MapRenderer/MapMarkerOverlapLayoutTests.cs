using TarkovCompanion.App.ViewModels.V2.MapRenderer;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// Issue 508: "when two pins land on each other, they must both remain legible (offset or
/// stack), not one hiding the other." These are the pure coordinates behind that — whether a
/// waypoint and a quest objective dropped on the exact same spot end up drawn apart.
/// </summary>
public sealed class MapMarkerOverlapLayoutTests
{
    [Fact]
    public void A_lone_marker_is_never_nudged()
    {
        var result = MapMarkerOverlapLayout.Resolve([(100, 100)]);

        Assert.Equal((0d, 0d), result[0]);
    }

    [Fact]
    public void Two_markers_far_apart_are_never_nudged()
    {
        var result = MapMarkerOverlapLayout.Resolve([(0, 0), (500, 500)]);

        Assert.Equal((0d, 0d), result[0]);
        Assert.Equal((0d, 0d), result[1]);
    }

    [Fact]
    public void Two_markers_on_the_exact_same_spot_are_pulled_apart_and_stay_equidistant_from_it()
    {
        var result = MapMarkerOverlapLayout.Resolve([(100, 100), (100, 100)]);

        Assert.NotEqual((0d, 0d), result[0]);
        Assert.NotEqual((0d, 0d), result[1]);
        // Legible means far enough apart that a 44px marker box centred on each nudged point no
        // longer sits on top of the other's.
        var dx = (result[0].DeltaX) - (result[1].DeltaX);
        var dy = (result[0].DeltaY) - (result[1].DeltaY);
        Assert.True(Math.Sqrt((dx * dx) + (dy * dy)) >= 20);
        AssertSameDistanceFromOrigin(result[0], result[1]);
    }

    [Fact]
    public void Three_markers_on_the_exact_same_spot_are_spread_around_it_not_stacked_in_a_line()
    {
        var result = MapMarkerOverlapLayout.Resolve([(0, 0), (0, 0), (0, 0)]);

        Assert.Equal(3, result.Count);
        Assert.All(result, offset => Assert.NotEqual((0d, 0d), offset));
        // No two of the three land on the same nudged spot as each other.
        for (var i = 0; i < result.Count; i++)
        {
            for (var j = i + 1; j < result.Count; j++)
            {
                Assert.NotEqual(result[i], result[j]);
            }
        }
    }

    [Fact]
    public void A_marker_with_nothing_near_it_is_untouched_even_when_two_others_collide()
    {
        var result = MapMarkerOverlapLayout.Resolve([(0, 0), (0, 0), (900, 900)]);

        Assert.NotEqual((0d, 0d), result[0]);
        Assert.NotEqual((0d, 0d), result[1]);
        Assert.Equal((0d, 0d), result[2]);
    }

    [Fact]
    public void Two_separate_collided_pairs_are_each_resolved_on_their_own()
    {
        var result = MapMarkerOverlapLayout.Resolve([(0, 0), (0, 0), (900, 900), (900, 900)]);

        Assert.NotEqual((0d, 0d), result[0]);
        Assert.NotEqual((0d, 0d), result[1]);
        Assert.NotEqual((0d, 0d), result[2]);
        Assert.NotEqual((0d, 0d), result[3]);
        // The two pairs do not bleed into one four-way ring: each nudge stays close to its own
        // shared point rather than being pulled toward the other collision.
        Assert.True(Math.Abs(result[0].DeltaX) < 100 && Math.Abs(result[0].DeltaY) < 100);
        Assert.True(Math.Abs(result[2].DeltaX) < 100 && Math.Abs(result[2].DeltaY) < 100);
    }

    [Fact]
    public void The_same_input_always_resolves_to_the_same_output()
    {
        (double X, double Y)[] anchors = [(10, 10), (10, 10), (10, 10), (500, 10)];

        var first = MapMarkerOverlapLayout.Resolve(anchors);
        var second = MapMarkerOverlapLayout.Resolve(anchors);

        Assert.Equal(first, second);
    }

    private static void AssertSameDistanceFromOrigin((double DeltaX, double DeltaY) a, (double DeltaX, double DeltaY) b)
    {
        var distanceA = Math.Sqrt((a.DeltaX * a.DeltaX) + (a.DeltaY * a.DeltaY));
        var distanceB = Math.Sqrt((b.DeltaX * b.DeltaX) + (b.DeltaY * b.DeltaY));
        Assert.Equal(distanceA, distanceB, 6);
    }
}
