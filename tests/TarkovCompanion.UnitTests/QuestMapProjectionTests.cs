using TarkovCompanion.Application.Services.Maps;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Quests;

namespace TarkovCompanion.UnitTests;

public sealed class QuestMapProjectionTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-10T12:00:00Z");

    [Fact]
    public void ValidSourcePointAndRegionRetainBothCatalogProvenances()
    {
        var point = Objective("point", [Zone("point-zone", new(10, 0, 20), [])]);
        var region = Objective("region", [Zone(
            "region-zone",
            null,
            [new(1, 0, 1), new(5, 0, 1), new(5, 0, 5)])]);

        var result = Project(point, region);

        Assert.Equal(2, result.Objectives.Count);
        var pointResult = result.Objectives.Single(value => value.ObjectiveId == "point");
        var regionResult = result.Objectives.Single(value => value.ObjectiveId == "region");
        Assert.Equal(QuestMapGeometryKind.Point, pointResult.GeometryKind);
        Assert.Single(pointResult.Points);
        Assert.True(pointResult.HasExactGeometry);
        Assert.Equal(QuestMapGeometryKind.Region, regionResult.GeometryKind);
        Assert.Equal(3, regionResult.Points.Count);
        Assert.Equal("quest-hash", regionResult.QuestCatalogProvenance.PayloadSha256);
        Assert.Equal("map-hash", regionResult.MapCatalogProvenance.ContentSha256);
        Assert.Contains("Fixture author", regionResult.Attribution, StringComparison.Ordinal);
        Assert.Equal("wipe-a", result.Scope.Generation);
    }

    [Fact]
    public void CorruptSourceGeometryIsSuppressedWithoutInventingAPoint()
    {
        var incompleteRegion = Objective("objective", [Zone(
            "bad-zone",
            new(10, 0, 20),
            [new(1, 0, 1), new(2, 0, 2)])]);

        var result = Assert.Single(Project(incompleteRegion).Objectives);

        Assert.Equal(QuestMapGeometryKind.AssociationOnly, result.GeometryKind);
        Assert.Empty(result.Points);
        Assert.False(result.HasExactGeometry);
        Assert.Contains("incomplete", result.Availability, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransformAndMapMismatchesSuppressExactGeometry()
    {
        var objective = Objective("objective", [Zone("zone", new(10, 0, 20), [])]);
        var query = Query(objective);
        var location = Location();
        var variant = Variant();
        var service = new QuestMapProjectionService();

        var wrongVariant = service.Project(query, location, variant with { LocationId = "other" }, null, MapProvenance());
        var wrongZoneMap = service.Project(
            Query(objective with { Zones = [objective.Zones[0] with { MapId = "other-map" }] }),
            location,
            variant,
            null,
            MapProvenance());
        var missingTransform = service.Project(query, location, variant with { Transform = null }, null, MapProvenance());
        var wrongMode = service.Project(
            query with
            {
                CatalogProvenance = query.CatalogProvenance! with
                {
                    GameMode = GameMode.Pve,
                    SourceMode = "pve",
                },
            },
            location,
            variant,
            null,
            MapProvenance());

        Assert.All(
            new[] { wrongVariant, wrongZoneMap, missingTransform, wrongMode },
            projection => Assert.All(projection.Objectives, value =>
            {
                Assert.Equal(QuestMapGeometryKind.AssociationOnly, value.GeometryKind);
                Assert.Empty(value.Points);
            }));
        Assert.Contains("different map", Assert.Single(wrongVariant.Objectives).Availability, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not match", Assert.Single(wrongZoneMap.Objectives).Availability, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("validated transform", Assert.Single(missingTransform.Objectives).Availability, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exact profile scope", Assert.Single(wrongMode.Objectives).Availability, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SelectedFloorFiltersOnlyWhenSourceElevationSupportsIt()
    {
        var upper = Objective("upper", [Zone("upper-zone", new(10, 12, 20), [])]);
        var unknownElevation = Objective("unknown", [Zone("unknown-zone", null,
            [new(1, double.NaN, 1), new(5, double.NaN, 1), new(5, double.NaN, 5)])]);
        var baseFloor = new MapFloorDefinition(
            "base",
            "Base",
            null,
            null,
            true,
            [new(-5, 5, [])]);

        var filtered = Assert.Single(ProjectWithFloor(baseFloor, upper).Objectives);

        Assert.True(filtered.IsFloorFiltered);
        Assert.False(filtered.HasExactGeometry);
        Assert.Contains("selected upstream floor", filtered.Availability, StringComparison.OrdinalIgnoreCase);

        var noElevation = Assert.Single(ProjectWithFloor(null, unknownElevation).Objectives);
        Assert.Equal(QuestMapGeometryKind.AssociationOnly, noElevation.GeometryKind);
        Assert.Contains("invalid", noElevation.Availability, StringComparison.OrdinalIgnoreCase);
    }

    private static QuestMapProjectionReadModel Project(params QuestMapObjectiveReadModel[] objectives) =>
        ProjectWithFloor(null, objectives);

    private static QuestMapProjectionReadModel ProjectWithFloor(
        MapFloorDefinition? floor,
        params QuestMapObjectiveReadModel[] objectives) => new QuestMapProjectionService().Project(
        Query(objectives),
        Location(),
        Variant(),
        floor,
        MapProvenance());

    private static QuestMapObjectivesReadModel Query(params QuestMapObjectiveReadModel[] objectives)
    {
        var scope = new QuestProfileScope(Guid.Parse("c2491ab6-c6ea-4f61-bb03-fefdc2d319a4"), GameMode.Regular, "wipe-a");
        return new(scope, 7, QuestProvenance(), ["map-source-id"], objectives, []);
    }

    private static QuestMapObjectiveReadModel Objective(
        string id,
        IReadOnlyList<QuestObjectiveZone> zones) => new(
        "task",
        "Fixture quest",
        "trader",
        id,
        0,
        $"Objective {id}",
        QuestObjectiveKind.Visit,
        false,
        false,
        RecordedTaskState.Active,
        RecordedObjectiveState.InProgress,
        false,
        false,
        null,
        "Manual",
        Now,
        ["map-source-id"],
        zones,
        []);

    private static QuestObjectiveZone Zone(
        string id,
        WorldPosition? position,
        IReadOnlyList<WorldPosition> outline) => new(
        0,
        id,
        "map-source-id",
        position,
        outline,
        null,
        null,
        null,
        null,
        id,
        "{}");

    private static MapLocation Location() => new(
        "synthetic-port",
        "map-source-id",
        "Synthetic Port",
        null,
        null,
        [Variant()]);

    private static MapVariant Variant() => new(
        "synthetic-port",
        "interactive",
        MapProjectionKind.Interactive,
        "interactive",
        null,
        null,
        new("https://assets.tarkov.dev/map.svg"),
        null,
        256,
        null,
        null,
        new(new(-100, -100), new(100, 100)),
        null,
        new(1, 0, 1, 0, 0),
        null,
        null,
        null,
        "Fixture author",
        new("https://tarkov.dev"),
        [],
        [],
        []);

    private static QuestCatalogProvenance QuestProvenance() => new(
        "json.tarkov.dev",
        "https://json.tarkov.dev/regular/tasks",
        GameMode.Regular,
        "regular",
        "en",
        "quest-hash",
        "translation-hash",
        null,
        null,
        Now,
        Now);

    private static MapCatalogProvenance MapProvenance() => new(
        new("https://raw.githubusercontent.com/the-hideout/tarkov-dev/main/src/data/maps.json"),
        Now,
        "map-hash",
        MapCatalogAvailability.Current);
}
