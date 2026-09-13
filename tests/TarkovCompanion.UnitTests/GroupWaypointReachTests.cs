using TarkovCompanion.Application.Services.Group;
using TarkovCompanion.Core.Domain.Maps;

namespace TarkovCompanion.UnitTests;

/// <summary>
/// When a place the group marked counts as somewhere they have been.
/// </summary>
/// <remarks>
/// The map has always drawn a reached waypoint quiet rather than removing it, and nothing ever
/// decided that anybody had reached one, so a group's plan was a list that only grew.
/// </remarks>
public sealed class GroupWaypointReachTests
{
    [Fact]
    public void StandingOnItCountsAsReached() =>
        Assert.True(GroupWaypointReach.IsReached(new(100, 5, -200), 100, 5, -200));

    [Theory]
    [InlineData(49)]
    [InlineData(50)]
    public void SoDoesStandingNearIt(double metres) =>
        Assert.True(GroupWaypointReach.IsReached(new(100 + metres, 5, -200), 100, 5, -200));

    [Fact]
    public void AcrossTheMapDoesNot() =>
        Assert.False(GroupWaypointReach.IsReached(new(400, 5, -200), 100, 5, -200));

    /// <summary>
    /// Height counts, so another floor of the same building is not the same place.
    /// </summary>
    [Fact]
    public void DirectlyAboveItDoesNot() =>
        Assert.False(GroupWaypointReach.IsReached(new(100, 90, -200), 100, 5, -200));

    /// <summary>The radius is a sphere, not a box.</summary>
    [Fact]
    public void TheCornerOfTheRadiusIsOutsideIt() =>
        Assert.False(GroupWaypointReach.IsReached(new(140, 5, -160), 100, 5, -200));
}
