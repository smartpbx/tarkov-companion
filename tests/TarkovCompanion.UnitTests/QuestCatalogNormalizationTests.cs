using System.Text.Json;
using System.Text.Json.Serialization;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Quests;
using TarkovCompanion.Infrastructure.TarkovDevJson;

namespace TarkovCompanion.UnitTests;

public sealed class QuestCatalogNormalizationTests
{
    private static readonly DateTimeOffset FetchedUtc = new(2026, 9, 10, 3, 23, 23, TimeSpan.Zero);
    private static readonly DateTimeOffset ValidatedUtc = new(2026, 9, 10, 5, 14, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    [Fact]
    public async Task CurrentTaxonomyAndUnknownVariantNormalizeWithoutInventingSemantics()
    {
        var catalog = await LoadContractAsync();
        var task = Assert.Single(catalog.Tasks);
        var known = task.Objectives.Where(objective => !objective.IsUnsupported).Select(objective => objective.Kind).ToHashSet();

        Assert.Equal(
            Enum.GetValues<QuestObjectiveKind>().Where(kind => kind != QuestObjectiveKind.Unsupported).ToHashSet(),
            known);
        var unsupported = Assert.Single(task.Objectives, objective => objective.IsUnsupported);
        Assert.Equal("futureObjective", unsupported.SourceType);
        Assert.Contains("futureCondition", unsupported.RawSourceJson, StringComparison.Ordinal);
        Assert.Contains("approximately", unsupported.SubtypeJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrerequisitesItemsMapsAndZonesRetainSourceMeaningAndOrder()
    {
        var catalog = await LoadContractAsync();
        var task = Assert.Single(catalog.Tasks);

        Assert.True(task.Restartable);
        Assert.True(task.KappaRequired);
        Assert.False(task.LightkeeperRequired);
        Assert.Equal("prestige-001", task.RequiredPrestigeId);
        Assert.Equal(["complete", "active", "future-status"], Assert.Single(task.Requirements).RequiredStatuses);

        var itemObjective = Assert.Single(task.Objectives, objective => objective.Kind == QuestObjectiveKind.FindItem);
        Assert.Equal(3, itemObjective.TargetCount);
        Assert.True(itemObjective.FoundInRaidRequired);
        Assert.Equal(["item-a", "item-b"], itemObjective.ItemTargets.Select(target => target.ItemId));
        Assert.All(itemObjective.ItemTargets, target => Assert.Equal(0, target.AlternativeGroup));

        var questItem = Assert.Single(task.Objectives, objective => objective.Kind == QuestObjectiveKind.FindQuestItem);
        Assert.Equal(4, questItem.MapAssociations.Count);
        Assert.Equal(2, questItem.MapAssociations.Count(link => link.Kind == QuestMapAssociationKind.Declared));
        Assert.Equal(2, questItem.MapAssociations.Count(link => link.Kind == QuestMapAssociationKind.PossibleLocation));
        Assert.Equal(3, questItem.ItemTargets.Count(target => target.SourceField == "requiredKeys"));
        Assert.Equal([0, 0, 1], questItem.ItemTargets
            .Where(target => target.SourceField == "requiredKeys")
            .Select(target => target.AlternativeGroup));
        Assert.Equal(2, questItem.Zones.Count);
        Assert.Equal(["map-one", "map-two"], questItem.Zones.Select(zone => zone.MapId));
        Assert.All(questItem.Zones, zone => Assert.Null(zone.SourceZoneId));
        Assert.Equal([0, 1], questItem.Zones.Select(zone => zone.SourceOrdinal));
        Assert.Equal([(1d, 2d, 3d), (4d, 5d, 6d)], questItem.Zones.Select(zone =>
            (zone.Position!.Value.X, zone.Position.Value.Y, zone.Position.Value.Z)));

        var mark = Assert.Single(task.Objectives, objective => objective.Kind == QuestObjectiveKind.Mark);
        var zone = Assert.Single(mark.Zones);
        Assert.Equal("zone-a", zone.SourceZoneId);
        Assert.Equal(10.5, zone.Position?.X);
        Assert.Equal(19, zone.BottomElevation);
        Assert.Equal(22, zone.TopElevation);
        Assert.Equal(20.25, zone.TerrainElevation);
        Assert.Equal(3, zone.Outline.Count);
        Assert.Contains("futureGeometryField", zone.RawSourceJson, StringComparison.Ordinal);

        var noMap = Assert.Single(task.Objectives, objective => objective.Kind == QuestObjectiveKind.BuildWeapon);
        Assert.Empty(noMap.MapAssociations);
    }

    [Fact]
    public async Task FailureConditionsAndRawProvenanceRemainSeparateFromObjectives()
    {
        var catalog = await LoadContractAsync();
        var task = Assert.Single(catalog.Tasks);

        Assert.Equal(2, task.FailureConditions.Count);
        Assert.All(task.FailureConditions, failure => Assert.True(failure.IsFailureCondition));
        var branchFailure = Assert.Single(
            task.FailureConditions,
            failure => failure.Kind == QuestObjectiveKind.TaskStatus);
        Assert.Equal("task-exclusive-branch", branchFailure.TargetTaskId);
        Assert.Equal(["complete", "future-status"], branchFailure.TargetStatuses);
        Assert.Contains("futureTaskField", task.RawSourceJson, StringComparison.Ordinal);
        Assert.Equal("json.tarkov.dev/tasks", catalog.Provenance.Source);
        Assert.Equal("regular", catalog.Provenance.SourceMode);
        Assert.Equal(GameMode.Regular, catalog.Provenance.GameMode);
        Assert.Equal(FetchedUtc, catalog.Provenance.FetchedUtc);
        Assert.Equal(ValidatedUtc, catalog.Provenance.ValidatedUtc);
        Assert.Equal(64, catalog.Provenance.PayloadSha256.Length);
    }

    private static async Task<QuestCatalogSnapshot> LoadContractAsync()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "api", "tasks-contract.json");
        var json = await File.ReadAllTextAsync(path, CancellationToken.None);
        var envelope = JsonSerializer.Deserialize<TarkovDevEnvelope<TarkovDevTasksData>>(json, SerializerOptions);
        Assert.NotNull(envelope);
        var response = new TarkovDevResponse<TarkovDevTasksData>(
            envelope.Data,
            json,
            FetchedUtc,
            false,
            false,
            "\"contract-v1\"",
            FetchedUtc,
            json);
        return new TarkovDevQuestCatalogNormalizer().Normalize(
            response,
            GameMode.Regular,
            "en",
            ValidatedUtc);
    }
}
