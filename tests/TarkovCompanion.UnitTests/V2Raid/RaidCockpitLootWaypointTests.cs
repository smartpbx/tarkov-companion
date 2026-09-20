using TarkovCompanion.App.ViewModels.V2.MapRenderer;
using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.App.Views.V2.MapRenderer;
using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Core.Abstractions.V2;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps.Scene;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>Issue 318: a spawn the player selected can become a waypoint, from the desktop.</summary>
public sealed class RaidCockpitLootWaypointTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OnlyASpawnWithADrawnMarkerIsOfferedAWaypointAndAskingNamesThatSpawn()
    {
        var renderer = MapSceneRendererGalleryViewModel.Create(largeText: false).Renderer;
        var loot = Assert.IsType<HighValueLootLayerViewModel>(renderer.HighValueLoot);
        HighValueLootEntryRequest? requested = null;
        loot.WaypointRequested += entry => requested = new(entry.Spawn.SpawnId);

        var mapOnly = loot.Rows.Single(row => row.Entry.Spawn.SpawnId == "map-only-cache");
        var drawn = loot.Rows.First(row => !row.IsListOnly);

        Assert.False(mapOnly.CanMakeWaypoint);
        Assert.True(drawn.CanMakeWaypoint);
        Assert.Equal("Make waypoint", drawn.MakeWaypointLabel);

        drawn.MakeWaypointCommand.Execute(null);

        Assert.Equal(drawn.Entry.Spawn.SpawnId, requested?.SpawnId);
    }

    [Fact]
    public void APointSpawnIsPlacedOnItsMarkerOnTheFloorItIsOn()
    {
        var item = Spawn([new(40, 60)], floors: ["upper"]);

        var place = RaidCockpitViewModel.PlaceSpawnWaypoint(item, selectedFloorId: "ground");

        Assert.Equal(new RaidCockpitViewModel.SpawnWaypointPlacement(40, 60, "upper"), place);
    }

    [Fact]
    public void AnAreaSpawnIsPlacedInsideItsOutlineAndTheFloorBeingViewedWinsWhenItIsOneOfTheSpawnsFloors()
    {
        var item = Spawn([new(0, 0), new(10, 0), new(10, 10), new(0, 10)], floors: ["ground", "upper"]);

        var place = RaidCockpitViewModel.PlaceSpawnWaypoint(item, selectedFloorId: "upper");

        Assert.Equal(new RaidCockpitViewModel.SpawnWaypointPlacement(5, 5, "upper"), place);
    }

    [Fact]
    public void ASpawnThatNamesNoFloorTakesTheFloorBeingViewed()
    {
        var everywhere = Spawn([new(1, 2)], floors: []);

        Assert.Equal(
            new RaidCockpitViewModel.SpawnWaypointPlacement(1, 2, "ground"),
            RaidCockpitViewModel.PlaceSpawnWaypoint(everywhere, selectedFloorId: "ground"));
    }

    [Fact]
    public void ASecondRequestForTheSameSpawnAddsNothing()
    {
        var place = new RaidCockpitViewModel.SpawnWaypointPlacement(40, 60, "upper");
        var marks = new[]
        {
            new RaidMark(Guid.NewGuid(), RaidMarkKind.Waypoint, new("customs", "upper", 40, 60, "Old gas station", null), NowUtc),
        };

        Assert.True(RaidCockpitViewModel.HasWaypointAt(marks, "customs", place));
        Assert.False(RaidCockpitViewModel.HasWaypointAt(marks, "woods", place));
        Assert.False(RaidCockpitViewModel.HasWaypointAt(marks, "customs", place with { FloorId = "ground" }));
        Assert.False(RaidCockpitViewModel.HasWaypointAt(
            [new RaidMark(Guid.NewGuid(), RaidMarkKind.Ping, new("customs", "upper", 40, 60, null, null), NowUtc)],
            "customs",
            place));
    }

    private sealed record HighValueLootEntryRequest(string SpawnId);

    private static MapSceneObject Spawn(IReadOnlyList<MapScenePoint> points, IReadOnlyList<string> floors) => new(
        new("spawn:1"),
        new("high-value-loot"),
        MapSceneObjectKind.LootSpawn,
        MapSceneTruthKind.PotentialSpawn,
        "Old gas station",
        null,
        points.Count == 1 ? MapSceneGeometry.At(points[0]) : new(MapSceneGeometryKind.Area, points),
        floors,
        new("fixture", NowUtc, Confidence: Confidence.Certain));
}
