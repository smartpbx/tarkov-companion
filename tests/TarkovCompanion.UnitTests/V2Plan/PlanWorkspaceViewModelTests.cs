using TarkovCompanion.App.ViewModels.V2.Plan;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Plan;

/// <summary>
/// Covers the Plan workspace's substantive new logic — actionable-by-default filtering, map
/// bucketing (including an objective on several maps, and one on none), and the hand-in/remaining
/// count labels — as pure static methods, the way <c>RaidCockpitViewModel.BuildMarksLayer</c> is
/// tested rather than instantiating the whole workspace. The workspace itself needs the shared
/// <c>MapViewModel</c> singleton, which pulls in the map catalog client and cache and has no
/// lightweight construction path for a unit test.
/// </summary>
public sealed class PlanWorkspaceViewModelTests
{
    [Fact]
    public void Default_filter_keeps_only_active_quests_and_their_unfinished_objectives()
    {
        var active = Task("active-quest", RecordedTaskState.Active,
        [
            Objective("obj-unfinished", RecordedObjectiveState.InProgress),
            Objective("obj-done", RecordedObjectiveState.Completed),
        ]);
        var notStarted = Task("not-started-quest", RecordedTaskState.NotStarted,
        [
            Objective("obj-not-started", RecordedObjectiveState.Unknown),
        ]);

        var entries = PlanWorkspaceViewModel.Bucket([active, notStarted], showAll: false);

        var entry = Assert.Single(entries);
        Assert.Equal("obj-unfinished", entry.Objective.ObjectiveId);
        Assert.Equal("active-quest", entry.Task.TaskId);
    }

    [Fact]
    public void Show_everything_includes_every_quest_and_every_objective()
    {
        var active = Task("active-quest", RecordedTaskState.Active,
        [
            Objective("obj-unfinished", RecordedObjectiveState.InProgress),
            Objective("obj-done", RecordedObjectiveState.Completed),
        ]);
        var notStarted = Task("not-started-quest", RecordedTaskState.NotStarted,
        [
            Objective("obj-not-started", RecordedObjectiveState.Unknown),
        ]);

        var entries = PlanWorkspaceViewModel.Bucket([active, notStarted], showAll: true);

        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void An_objective_on_several_maps_appears_once_per_map()
    {
        var task = Task("active-quest", RecordedTaskState.Active,
        [
            Objective("obj-multi-map", RecordedObjectiveState.InProgress, mapIds: ["customs", "woods"]),
        ]);

        var entries = PlanWorkspaceViewModel.Bucket([task], showAll: false);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, entry => entry.MapKey == "customs");
        Assert.Contains(entries, entry => entry.MapKey == "woods");
    }

    [Fact]
    public void An_objective_with_no_map_falls_back_to_the_any_map_bucket()
    {
        var task = Task("active-quest", RecordedTaskState.Active,
        [
            Objective("obj-no-map", RecordedObjectiveState.InProgress, mapIds: []),
        ]);

        var entries = PlanWorkspaceViewModel.Bucket([task], showAll: false);

        var entry = Assert.Single(entries);
        Assert.Equal(string.Empty, entry.MapKey);
    }

    [Theory]
    [InlineData(true, "Find in raid")]
    [InlineData(false, "Hand in")]
    [InlineData(null, "")]
    public void Handling_label_only_speaks_where_the_catalog_says_which(bool? foundInRaidRequired, string expected)
    {
        var objective = Objective("obj", RecordedObjectiveState.InProgress, foundInRaidRequired: foundInRaidRequired);

        Assert.Equal(expected, PlanWorkspaceViewModel.DescribeHandling(objective));
    }

    [Fact]
    public void Remaining_label_counts_down_from_the_target()
    {
        var objective = Objective("obj", RecordedObjectiveState.InProgress, recordedCount: 2, targetCount: 5);

        Assert.Equal("3 remaining of 5", PlanWorkspaceViewModel.DescribeRemaining(objective));
    }

    [Fact]
    public void Remaining_label_never_goes_negative_when_recorded_count_overshoots_the_target()
    {
        var objective = Objective("obj", RecordedObjectiveState.Completed, recordedCount: 7, targetCount: 5);

        Assert.Equal("0 remaining of 5", PlanWorkspaceViewModel.DescribeRemaining(objective));
    }

    [Fact]
    public void A_map_group_numbers_its_steps_and_counts_distinct_quests()
    {
        var first = Task("quest-a", RecordedTaskState.Active,
        [
            Objective("obj-1", RecordedObjectiveState.InProgress),
            Objective("obj-2", RecordedObjectiveState.InProgress),
        ]);
        var second = Task("quest-b", RecordedTaskState.Active,
        [
            Objective("obj-3", RecordedObjectiveState.InProgress),
        ]);
        var rows = PlanWorkspaceViewModel.Bucket([first, second], showAll: false)
            .Select((entry, index) => new PlanObjectiveRowViewModel(entry.Task, entry.Objective, null!, index + 1, index == 2))
            .ToArray();

        var group = new PlanMapGroupViewModel("customs", "Customs", rows);

        Assert.Equal([1, 2, 3], rows.Select(row => row.Number));
        Assert.True(rows[0].HasNext);
        Assert.False(rows[2].HasNext);
        Assert.Equal(2, group.Quests.Count);
        Assert.Equal("2 objectives", group.Quests[0].ObjectivesLabel);
        Assert.Equal("3 objectives · 2 quests", group.Summary);
        Assert.True(group.CanOpenInRaid);
        Assert.False(new PlanMapGroupViewModel(null, "Any map", rows).CanOpenInRaid);
    }

    [Fact]
    public void The_objective_scene_carries_the_row_numbers_and_lists_objectives_with_no_placed_geometry_as_having_none()
    {
        var task = Task("quest-a", RecordedTaskState.Active,
        [
            Objective("placed-point", RecordedObjectiveState.InProgress),
            Objective("association-only", RecordedObjectiveState.InProgress),
            Objective("placed-region", RecordedObjectiveState.InProgress),
        ]);
        var rows = PlanWorkspaceViewModel.Bucket([task], showAll: false)
            .Select((entry, index) => new PlanObjectiveRowViewModel(entry.Task, entry.Objective, null!, index + 1, index == 2))
            .ToArray();
        IReadOnlyList<QuestMapObjectiveProjection> projected =
        [
            Projected("placed-point", QuestMapGeometryKind.Point, [new(10, 20)]),
            Projected("association-only", QuestMapGeometryKind.AssociationOnly, []),
            Projected("placed-region", QuestMapGeometryKind.Region, [new(40, 40), new(60, 40), new(60, 60), new(40, 60)]),
        ];

        var scene = PlanWorkspaceViewModel.BuildObjectiveScene(rows, projected, RealQuestZones.Load().Model("customs"), DateTimeOffset.UnixEpoch);

        // The points are the projection's own: there is no second mapping between them and the plan.
        var spot = Assert.Single(scene.Objects, item => item.Label == "1");
        Assert.Equal(new(10, 20), spot.Geometry.Points[0]);
        var area = Assert.Single(scene.Objects, item => item.Geometry.Kind == MapSceneGeometryKind.Area);
        Assert.Equal("Area 3", area.Label);
        Assert.Equal(4, area.Geometry.Points.Count);
        Assert.Equal(new(50, 50), Assert.Single(scene.Objects, item => item.Label == "3").Geometry.Points[0]);
        Assert.Equal(["placed-point", "association-only", "placed-region"], scene.Entries.Select(entry => entry.ObjectiveId));
        var none = scene.Entries.Single(entry => entry.ObjectiveId == "association-only");
        Assert.False(none.IsPlaced);
        Assert.Equal("No location", none.PlacementLabel);
        Assert.DoesNotContain(scene.Objects, item => item.Id.Value.Contains("association-only", StringComparison.Ordinal));
    }

    [Fact]
    public void The_objective_scene_draws_only_the_objectives_the_list_shows()
    {
        var task = Task("quest-a", RecordedTaskState.Active, [Objective("listed", RecordedObjectiveState.InProgress)]);
        var rows = PlanWorkspaceViewModel.Bucket([task], showAll: false)
            .Select(entry => new PlanObjectiveRowViewModel(entry.Task, entry.Objective, null!))
            .ToArray();

        var scene = PlanWorkspaceViewModel.BuildObjectiveScene(
            rows,
            [
                Projected("listed", QuestMapGeometryKind.Point, [new(10, 20)]),
                Projected("someone-elses", QuestMapGeometryKind.Point, [new(30, 40)]),
            ],
            RealQuestZones.Load().Model("customs"),
            DateTimeOffset.UnixEpoch);

        Assert.Equal("listed", Assert.Single(scene.Entries).ObjectiveId);
        Assert.Single(scene.Objects);
    }

    private static QuestMapObjectiveProjection Projected(
        string objectiveId,
        QuestMapGeometryKind geometry,
        IReadOnlyList<TarkovCompanion.Core.Domain.Maps.MapPoint> points) => new(
        "quest-a",
        "quest-a",
        objectiveId,
        "description",
        QuestObjectiveKind.Visit,
        IsUnsupported: false,
        IsPinned: false,
        ZoneId: null,
        geometry,
        points,
        IsFloorFiltered: false,
        Availability: "test",
        FloorHint: null,
        FoundInRaidRequired: null,
        ItemTargets: [],
        QuestCatalogProvenance: null!,
        MapCatalogProvenance: null!,
        Attribution: "test");

    [Fact]
    public void Remaining_label_falls_back_to_the_recorded_state_when_there_is_no_target_or_count()
    {
        var objective = Objective("obj", RecordedObjectiveState.Unknown);

        Assert.Equal("Unknown", PlanWorkspaceViewModel.DescribeRemaining(objective));
    }

    private static QuestSummaryReadModel Task(
        string taskId,
        RecordedTaskState state,
        IReadOnlyList<QuestObjectiveReadModel> objectives) => new(
        taskId,
        Name: taskId,
        TraderId: "trader-1",
        PrimaryMapId: null,
        RecordedState: state,
        ProgressSource: "test",
        ProgressModifiedUtc: null,
        Eligibility: new(QuestEligibilityState.Available, []),
        RecordedObjectivesSatisfied: RecordedObjectivesSatisfaction.Indeterminate,
        IsPinned: false,
        Restartable: false,
        HasFailureConditions: false,
        FailureConditionNotes: [],
        Prerequisites: [],
        Objectives: objectives);

    private static QuestObjectiveReadModel Objective(
        string objectiveId,
        RecordedObjectiveState state,
        decimal? recordedCount = null,
        decimal? targetCount = null,
        bool? foundInRaidRequired = null,
        IReadOnlyList<string>? mapIds = null) => new(
        objectiveId,
        Description: $"Do the thing for {objectiveId}",
        Kind: QuestObjectiveKind.FindItem,
        IsOptional: false,
        IsUnsupported: false,
        RecordedState: state,
        RecordedCount: recordedCount,
        TargetCount: targetCount,
        FoundInRaidRequired: foundInRaidRequired,
        ProgressSource: "test",
        ProgressModifiedUtc: null,
        IsPinned: false,
        MapIds: mapIds ?? [],
        ItemTargets: []);
}
