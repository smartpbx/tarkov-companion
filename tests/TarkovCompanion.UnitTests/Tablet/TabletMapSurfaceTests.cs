using TarkovCompanion.Application.Services.Devices;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.TabletSurface;

/// <summary>
/// What the desktop hands a paired tablet to draw with (#407).
/// </summary>
/// <remarks>
/// The property that matters is that there is no second projection. The tablet is given the plan
/// rectangle the desktop's own artwork covers and coordinates already inside it, so "the mark is
/// in the same place on both screens" holds by construction rather than by two transforms
/// agreeing. These check that on a wide map and a tall one, because a square test map hides
/// exactly the mistake — swapping width for height — that would break it.
/// </remarks>
public sealed class TabletMapSurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    // Streets-shaped: much wider than it is tall, and not starting at the origin.
    [InlineData(1200d, 40d, 3400d, 900d)]
    // Factory-shaped: taller than it is wide.
    [InlineData(-50d, -400d, 260d, 950d)]
    public void AnObjectLandsOnTheSameFractionOfThePlanOnBothScreens(
        double minimumX,
        double minimumY,
        double maximumX,
        double maximumY)
    {
        // A point a third of the way across the plan and three quarters down it. Whatever size the
        // tablet's canvas is, the artwork is drawn over exactly the plan rectangle, so this has to
        // come back at the same fraction of that rectangle or the two screens disagree.
        var x = minimumX + ((maximumX - minimumX) * 1 / 3d);
        var y = minimumY + ((maximumY - minimumY) * 3 / 4d);
        var scene = Scene(new(minimumX, minimumY, maximumX, maximumY), Point("extract:water", x, y));

        var surface = TabletMapSurfaceBuilder.Build(scene, "Customs", Artwork(), null, null, null, Now);

        var plan = Assert.IsType<TabletMapPlan>(surface.Plan);
        var item = Assert.Single(surface.Objects);
        Assert.Equal(minimumX, plan.MinimumX);
        Assert.Equal(maximumY, plan.MaximumY);
        Assert.Equal(1 / 3d, (item.Points[0] - plan.MinimumX) / (plan.MaximumX - plan.MinimumX), 10);
        Assert.Equal(3 / 4d, (item.Points[1] - plan.MinimumY) / (plan.MaximumY - plan.MinimumY), 10);
    }

    [Fact]
    public void TheCameraTravelsInThePlansOwnUnitsToo()
    {
        // Follow mirrors the desktop's viewport. The centre is in the same units as the objects,
        // so a following tablet has nothing to convert — and nothing to convert wrongly.
        var scene = Scene(new(0, 0, 1000, 500), Point("extract:water", 100, 100));
        var withCamera = new MapSceneSnapshot(
            scene.Revision,
            scene.LocationId,
            scene.VariantKey,
            scene.TransformVersion,
            scene.Bounds,
            scene.FloorIds,
            scene.Capabilities,
            new(MapSceneMode.Flat2D, "base", new(250, 375, 2.5, 0, 0), []),
            scene.Layers,
            scene.Objects,
            scene.Assets);

        var surface = TabletMapSurfaceBuilder.Build(withCamera, "Customs", Artwork(), null, null, null, Now);

        Assert.Equal(250, surface.View.CenterX);
        Assert.Equal(375, surface.View.CenterY);
        Assert.Equal(2.5, surface.View.Zoom);
        Assert.Equal("base", surface.View.FloorId);
    }

    [Fact]
    public void AMapWithNoReviewedPlanSaysSoAndCarriesNoArtwork()
    {
        // "Blank coords are not usable for a human": a tablet that cannot draw the map says so
        // rather than plotting dots on an empty canvas. A scene never carries an unreviewed asset
        // (MapSceneSnapshot refuses one outright), so the case is a scene with no plan at all.
        var scene = Scene(new(0, 0, 100, 100), [Point("extract:water", 10, 10)], withArtwork: false);

        var surface = TabletMapSurfaceBuilder.Build(scene, "Customs", Artwork(), null, null, null, Now);

        Assert.Null(surface.Artwork);
        Assert.Equal("This map has no reviewed 2D plan yet.", surface.Message);
    }

    [Fact]
    public void TheReviewedAssetsProvenanceTravelsWithIt()
    {
        // ADR 0015: the source, the licence and the content hash are shown on the tablet, not
        // only on the desktop that fetched the artwork.
        var scene = Scene(new(0, 0, 100, 100), Point("extract:water", 10, 10));

        var surface = TabletMapSurfaceBuilder.Build(scene, "Customs", Artwork(), null, null, null, Now);

        var attribution = Assert.Single(surface.Attribution);
        Assert.Equal("Example map author", attribution.Text);
        Assert.Equal("https://example.test/maps/customs.svg", attribution.SourceUri);
        Assert.Equal("https://example.test/licence", attribution.LicenseUri);
        Assert.Equal(new string('a', 64), attribution.ContentSha256);
        // The artwork's own hash is of the bytes actually sent, which is a different fact from
        // the reviewed upstream asset's, and both are needed.
        Assert.Equal(new string('b', 64), surface.Artwork!.ContentSha256);
    }

    [Fact]
    public void ItSurvivesTheJsonTheTabletActuallyReads()
    {
        var scene = Scene(new(10, 20, 110, 220), Point("extract:water", 35, 170));

        var surface = TabletMapSurfaceBuilder.Build(scene, "Customs", Artwork(), null, null, null, Now);
        var round = TabletMapSurfaceJson.Deserialize(TabletMapSurfaceJson.Serialize(surface));

        Assert.NotNull(round);
        Assert.Equal(surface.Plan, round!.Plan);
        Assert.Equal(surface.Objects[0].Points, round.Objects[0].Points);
        Assert.Equal(surface.View, round.View);
    }

    [Fact]
    public void AHostileSceneCannotMakeAnUnboundedPayload()
    {
        var objects = Enumerable.Range(0, TabletMapSurfaceBuilder.MaximumObjects + 50)
            .Select(index => Point($"object:{index}", index % 90, index % 90))
            .ToArray();
        var scene = Scene(new(0, 0, 100, 100), objects);

        var surface = TabletMapSurfaceBuilder.Build(scene, "Customs", Artwork(), null, null, null, Now);

        Assert.Equal(TabletMapSurfaceBuilder.MaximumObjects, surface.Objects.Count);
    }

    private static TabletMapArtwork Artwork() => new("image/png", new string('b', 64), 2048, 1024);

    private static MapSceneObject Point(string id, double x, double y) => new(
        new(id),
        new("extracts"),
        MapSceneObjectKind.Extract,
        MapSceneTruthKind.StaticReference,
        id,
        null,
        MapSceneGeometry.At(new(x, y)),
        [],
        new("fixture", Now, Confidence: Confidence.Certain));

    private static MapSceneSnapshot Scene(MapSceneBounds bounds, MapSceneObject item) =>
        Scene(bounds, [item]);

    private static MapSceneSnapshot Scene(
        MapSceneBounds bounds,
        IReadOnlyList<MapSceneObject> objects,
        bool withArtwork = true) => new(
        4,
        "customs",
        "customs-plan",
        "catalog-sha:customs",
        bounds,
        ["base"],
        new(
            MapSceneCapability.Available,
            MapSceneCapability.Unavailable("No floors."),
            MapSceneCapability.Unavailable("No interior.")),
        new(MapSceneMode.Flat2D, "base", new(bounds.MinimumX + (bounds.Width / 2), bounds.MinimumY + (bounds.Height / 2), 1, 0, 0), []),
        [new(new("extracts"), "Extracts", 10, true)],
        objects,
        withArtwork
            ?
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
            ]
            : []);
}
