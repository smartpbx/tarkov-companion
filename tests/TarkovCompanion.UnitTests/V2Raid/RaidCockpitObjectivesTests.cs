using TarkovCompanion.App.ViewModels.V2.Raid;
using TarkovCompanion.Application.Services.Quests;
using TarkovCompanion.Core.Domain.Maps.Scene;
using TarkovCompanion.UnitTests.V2MapRenderer;

namespace TarkovCompanion.UnitTests.V2Raid;

/// <summary>[Package 35] The Raid plan draws the quest objectives read for the map and artwork it shows.</summary>
public sealed class RaidCockpitObjectivesTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

    private static readonly RealQuestZones Zones = RealQuestZones.Load();

    [Fact]
    public void The_objectives_read_for_this_map_are_drawn_and_every_one_is_listed()
    {
        var model = Zones.Model("lighthouse");

        var scene = RaidCockpitViewModel.BuildQuestScene(Projection("lighthouse", model.Location.Id, model.Variant.Key), model, NowUtc);

        Assert.Equal(Zones.Objectives("lighthouse").Count, scene.Entries.Count);
        Assert.NotEmpty(scene.Objects);
        Assert.All(scene.Objects, item => Assert.Equal(MapSceneObjectKind.QuestObjective, item.Kind));
    }

    [Fact]
    public void Nothing_is_drawn_from_a_projection_made_for_another_map_or_another_artwork()
    {
        var model = Zones.Model("lighthouse");

        Assert.Empty(RaidCockpitViewModel.BuildQuestScene(null, model, NowUtc).Objects);
        Assert.Empty(RaidCockpitViewModel.BuildQuestScene(Projection("lighthouse", "customs", model.Variant.Key), model, NowUtc).Entries);
        Assert.Empty(RaidCockpitViewModel.BuildQuestScene(Projection("lighthouse", model.Location.Id, "lighthouse-2d"), model, NowUtc).Entries);
    }

    private static QuestMapProjectionReadModel Projection(string map, string locationId, string variantKey) => new(
        RealQuestZones.Scope,
        1,
        locationId,
        variantKey,
        Zones.Project(map, Zones.Model(map)),
        []);
}
