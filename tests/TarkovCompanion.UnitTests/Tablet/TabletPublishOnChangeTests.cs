using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.TabletSurface;

/// <summary>
/// What decides that there is anything to publish at all.
/// </summary>
/// <remarks>
/// [V2 rough package 34] The desktop's scene is rebuilt on every runtime tick and most ticks
/// change nothing a tablet would draw, so the publisher compares what it is about to send with
/// what it last sent rather than trusting the revision that carries it — a revision rises on every
/// rebuild. The one field that differs on every publish is the timestamp, so the comparison blanks
/// it; if it did not, every tick would look like a change and the publish-on-change would be a
/// timer again with extra steps.
///
/// This pins the predicate rather than the view model around it, because the view model needs an
/// Avalonia renderer and the predicate is the part that would silently go wrong.
/// </remarks>
public sealed class TabletPublishOnChangeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheSameSceneAtADifferentMomentIsNotAChange()
    {
        var scene = Scene(Marker(10, 20));

        var first = Comparable(Build(scene, Now));
        var second = Comparable(Build(scene, Now.AddSeconds(37)));

        Assert.Equal(first, second);
    }

    [Fact]
    public void AMarkerThatMovedIsAChange()
    {
        var before = Comparable(Build(Scene(Marker(10, 20)), Now));
        var after = Comparable(Build(Scene(Marker(10.5, 20)), Now));

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void ANewSearchResultIsAChange()
    {
        // The lookup rides on the same surface, so an answer arriving has to wake the tablet the
        // same way a moved marker does.
        var scene = Scene(Marker(10, 20));
        var without = Comparable(Build(scene, Now));
        var with = Comparable(Build(scene, Now, new("salewa", [new("id", "Salewa first aid kit", "Salewa", 31747, 15090)])));

        Assert.NotEqual(without, with);
    }

    [Fact]
    public void TheTimestampIsStillOnTheWire()
    {
        // Blanked for the comparison only. The tablet needs it to decide the desktop has gone
        // quiet, and a surface published without one would never look stale.
        var surface = Build(Scene(Marker(10, 20)), Now);

        Assert.Equal(Now, surface.PublishedUtc);
        Assert.NotEqual(Comparable(surface), TabletMapSurfaceJson.Serialize(surface));
    }

    private static byte[] Comparable(TabletMapSurface surface) =>
        TabletMapSurfaceJson.Serialize(surface with { PublishedUtc = default });

    private static TabletMapSurface Build(MapSceneSnapshot scene, DateTimeOffset at, TabletSearch? search = null) =>
        TabletMapSurfaceBuilder.Build(
            scene,
            "Customs",
            new("image/png", new string('b', 64), 1024, 1024),
            null,
            search,
            null,
            at);

    private static MapSceneObject Marker(double x, double y) => new(
        new("teammate:one"),
        new("live"),
        MapSceneObjectKind.TeammateLastKnown,
        MapSceneTruthKind.TeamSharedLastKnown,
        "Teammate",
        null,
        MapSceneGeometry.At(new(x, y)),
        [],
        new("fixture", Now, Confidence: Confidence.Certain));

    private static MapSceneSnapshot Scene(MapSceneObject marker) => new(
        7,
        "customs",
        "customs-plan",
        "catalog-sha:customs",
        new(0, 0, 100, 100),
        ["base"],
        new(
            MapSceneCapability.Available,
            MapSceneCapability.Unavailable("No floors."),
            MapSceneCapability.Unavailable("No interior.")),
        new(MapSceneMode.Flat2D, "base", new(50, 50, 1, 0, 0), []),
        [new(new("live"), "Live", 50, true)],
        [marker],
        [
            new(
                new("asset:plan"),
                MapSceneAssetKind.Background2D,
                new("https://example.test/maps/customs.svg"),
                new("https://example.test/licence"),
                new string('a', 64),
                "Example map author",
                "map-1",
                "game-1",
                MapSceneAssetReviewStatus.Reviewed,
                Now),
        ]);
}
