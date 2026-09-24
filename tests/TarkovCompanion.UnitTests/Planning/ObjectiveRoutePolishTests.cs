using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.UnitTests.Runtime;

namespace TarkovCompanion.UnitTests.Planning;

/// <summary>[#307] Route stops merged into objective pins, and the Raid route following the plan.</summary>
public sealed class ObjectiveRoutePolishTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    [Fact]
    public void AStopOnItsObjectivesPinIsNumberedOnThatPinInsteadOfGettingASecondPin()
    {
        var route = ObjectiveRoutePlanner.Plan(
            new(0, 0),
            "the selected spawn",
            [Stop("pinned", 10, 0), Stop("loose", 20, 0)],
            unitsPerMetre: 1);

        var scene = ObjectiveRouteSceneBuilder.Build(
            route,
            new(0, 0),
            Now,
            TarkovCompanion.App.Localization.PlanText.ObjectiveRouteWords(),
            [new("pinned", new("quest:pinned:A"), new(10.2, 0))]);

        Assert.Equal("1", scene.Badges[new("quest:pinned:A")]);
        var waypoint = Assert.Single(scene.Objects, item => item.Kind == MapSceneObjectKind.Waypoint);
        Assert.Equal("2", waypoint.Label);
        Assert.Equal("objective-route:step:loose", waypoint.Id.Value);
        // The line still visits both, the merged stop included.
        var line = Assert.Single(scene.Objects, item => item.Kind == MapSceneObjectKind.Route);
        Assert.Equal(3, line.Geometry.Points.Count);
    }

    [Fact]
    public void APinOfAnotherObjectiveOrTooFarAwayIsNeverNumbered()
    {
        var route = ObjectiveRoutePlanner.Plan(new(0, 0), "the selected spawn", [Stop("a", 10, 0)], unitsPerMetre: 1);

        var scene = ObjectiveRouteSceneBuilder.Build(
            route,
            new(0, 0),
            Now,
            TarkovCompanion.App.Localization.PlanText.ObjectiveRouteWords(),
            [
                new("b", new("quest:b"), new(10, 0)),
                new("a", new("quest:a-far"), new(10 + ObjectiveRouteSceneBuilder.PinTolerance + 0.1, 0)),
            ]);

        Assert.Empty(scene.Badges);
        Assert.Equal("1", Assert.Single(scene.Objects, item => item.Kind == MapSceneObjectKind.Waypoint).Label);
    }

    [Fact]
    public async Task ANewScreenshotPositionRecomputesTheRouteOnceAfterTheDebounce()
    {
        var time = new ManualTimeProvider(Now);
        var published = new List<ObjectiveRouteFollowerResult?>();
        using var follower = new ObjectiveRouteFollower(time, Debounce, result => { lock (published) { published.Add(result); } }, null);
        follower.SetStops("customs", [Stop("near-origin", 1, 0), Stop("far-origin", 50, 0)]);
        follower.SetOrigin(Origin(0, 0));
        follower.SetEnabled(true);
        await RuntimeTestTasks.AdvanceUntilAsync(time, Debounce, () => Count(published) == 1);
        Assert.Equal("near-origin", published[0]!.Route.Steps[0].ObjectiveId);

        // Two positions in quick succession: one recompute, for the last of them.
        follower.SetOrigin(Origin(20, 0));
        time.Advance(TimeSpan.FromMilliseconds(100));
        follower.SetOrigin(Origin(60, 0));
        await RuntimeTestTasks.AdvanceUntilAsync(time, Debounce, () => Count(published) == 2);
        Assert.Equal(2, follower.ComputeCount);
        Assert.Equal("far-origin", published[1]!.Route.Steps[0].ObjectiveId);
    }

    [Fact]
    public async Task TheSameInputsAgainDoNotRecomputeSoTheRebuildCannotLoop()
    {
        var time = new ManualTimeProvider(Now);
        var published = new List<ObjectiveRouteFollowerResult?>();
        using var follower = new ObjectiveRouteFollower(time, Debounce, result => { lock (published) { published.Add(result); } }, null);
        follower.SetStops("customs", [Stop("a", 1, 0)]);
        follower.SetOrigin(Origin(0, 0));
        follower.SetEnabled(true);
        await RuntimeTestTasks.AdvanceUntilAsync(time, Debounce, () => Count(published) == 1);

        follower.SetOrigin(Origin(0, 0));
        follower.SetStops("customs", [Stop("a", 1, 0)]);

        Assert.False(follower.IsPending);
        Assert.Equal(1, follower.ComputeCount);
    }

    [Fact]
    public async Task ChangedPlanStopsRecomputeButAHiddenRouteComputesNothing()
    {
        var time = new ManualTimeProvider(Now);
        var published = new List<ObjectiveRouteFollowerResult?>();
        using var follower = new ObjectiveRouteFollower(time, Debounce, result => { lock (published) { published.Add(result); } }, null);
        follower.SetOrigin(Origin(0, 0));
        follower.SetStops("customs", [Stop("a", 1, 0)]);

        // Not shown yet: nothing waits and nothing runs.
        Assert.False(follower.IsPending);
        follower.SetEnabled(true);
        await RuntimeTestTasks.AdvanceUntilAsync(time, Debounce, () => Count(published) == 1);

        follower.SetStops("customs", [Stop("a", 1, 0), Stop("b", 2, 0)]);
        await RuntimeTestTasks.AdvanceUntilAsync(time, Debounce, () => Count(published) == 2);
        Assert.Equal(["a", "b"], published[1]!.Route.Steps.Select(step => step.ObjectiveId));

        follower.SetEnabled(false);
        follower.SetOrigin(Origin(5, 5));
        Assert.False(follower.IsPending);
        time.Advance(Debounce * 4);
        await Task.Delay(20);
        Assert.Equal(2, follower.ComputeCount);
    }

    private static int Count(List<ObjectiveRouteFollowerResult?> published)
    {
        lock (published)
        {
            return published.Count;
        }
    }

    private static ObjectiveRouteOrigin Origin(double x, double y) => new(new(x, y), "your last screenshot", 1);

    private static ObjectiveRouteStop Stop(string id, double x, double y) => new(id, id, new MapScenePoint(x, y));
}
