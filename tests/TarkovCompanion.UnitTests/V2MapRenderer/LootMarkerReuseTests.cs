using System.Collections.Specialized;
using System.Globalization;
using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps.Scene;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2MapRenderer;

/// <summary>
/// [#657] A squadmate moving must not rebuild the loot layer's controls, and zooming must add or
/// remove only the spawns it reveals or hides.
/// </summary>
public sealed class LootMarkerReuseTests
{
    private static readonly MapSceneLayer Loot = new(new("loot"), "Loot", 10, true);
    private static readonly MapSceneLayer Squad = new(new("squad"), "Squad", 20, true);

    [Fact]
    public void A_squadmate_moving_keeps_every_loot_marker_and_badge_the_view_holds()
    {
        var renderer = Renderer(Scene(1, mateX: 100));
        var markers = renderer.LootMarkers.ToArray();
        var badges = renderer.LootBadges.ToArray();
        var changes = 0;
        ((INotifyCollectionChanged)renderer.LootMarkers).CollectionChanged += (_, _) => changes++;
        ((INotifyCollectionChanged)renderer.LootBadges).CollectionChanged += (_, _) => changes++;

        for (var step = 2; step < 6; step++)
        {
            renderer.Present(Scene(step, mateX: 100 + step));
        }

        Assert.Equal(0, changes);
        Assert.Equal(markers, renderer.LootMarkers);
        Assert.Equal(badges, renderer.LootBadges);
        Assert.NotEmpty(badges);
    }

    [Fact]
    public void Zooming_in_appends_the_revealed_spawns_and_zooming_out_only_hides_them()
    {
        var renderer = Renderer(Scene(1, mateX: 100));
        var fitted = renderer.LootMarkers.ToArray();
        Assert.Equal(MapLootRanking.TopAtFit, fitted.Length);

        renderer.RequestZoom(1);
        renderer.RequestZoom(1);
        renderer.RequestZoom(1);
        renderer.RequestZoom(1);

        Assert.True(renderer.LootMarkers.Count > fitted.Length, $"{renderer.LootMarkers.Count} at zoom {renderer.CameraZoom}");
        Assert.Equal(fitted, renderer.LootMarkers.Take(fitted.Length));
        Assert.All(renderer.LootMarkers, marker => Assert.True(marker.IsShownOnPlan));
        var zoomed = renderer.LootMarkers.ToArray();
        var changes = 0;
        ((INotifyCollectionChanged)renderer.LootMarkers).CollectionChanged += (_, _) => changes++;

        for (var i = 0; i < 4; i++)
        {
            renderer.RequestZoom(-1);
        }

        Assert.Equal(fitted, renderer.LootMarkers.Where(marker => marker.IsShownOnPlan));
        for (var i = 0; i < 4; i++)
        {
            renderer.RequestZoom(1);
        }

        Assert.Equal(0, changes);
        Assert.Equal(zoomed, renderer.LootMarkers);
        Assert.All(renderer.LootMarkers, marker => Assert.True(marker.IsShownOnPlan));
    }

    private static MapSceneRendererViewModel Renderer(MapSceneSnapshot scene)
    {
        var renderer = new MapSceneRendererViewModel(
            scene,
            MapSceneRendererPresentation.English(CultureInfo.InvariantCulture, TimeZoneInfo.Utc),
            showsDetailsPanel: false,
            ranksLootByValue: true);
        renderer.ViewChangeRequested += change => renderer.Present(MapSceneViewReducer.Apply(renderer.Scene, change).Scene);
        return renderer;
    }

    private static MapSceneSnapshot Scene(long revision, double mateX)
    {
        var provenance = new DataProvenance("fixture", new DateTimeOffset(2026, 9, 22, 0, 0, 0, TimeSpan.Zero), Confidence: new Confidence(1));
        var loot = Enumerable.Range(0, 60)
            .Select(index => new MapSceneObject(
                new($"loot:{index:D2}"),
                Loot.Id,
                MapSceneObjectKind.LootSpawn,
                MapSceneTruthKind.PotentialSpawn,
                $"Loot {index}",
                null,
                MapSceneGeometry.At(new(20 + (index % 10 * 35), 30 + (index / 10 * 40))),
                [],
                provenance));
        var mate = new MapSceneObject(
            new("squad:mate"),
            Squad.Id,
            MapSceneObjectKind.TeammateLastKnown,
            MapSceneTruthKind.TeamSharedLastKnown,
            "Mate",
            null,
            MapSceneGeometry.At(new(mateX, 150)),
            [],
            provenance);
        return new MapSceneSnapshot(
            revision, "customs", "customs", "customs", new MapSceneBounds(0, 0, 400, 300), [],
            new(MapSceneCapability.Available, MapSceneCapability.Unavailable("no stack"), MapSceneCapability.Unavailable("no interior")),
            new(MapSceneMode.Flat2D, null, new(200, 150, 1, 0, 0), [new(Loot.Id, true), new(Squad.Id, true)]),
            [Loot, Squad], [.. loot, mate], []);
    }
}
