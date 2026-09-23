using TarkovCompanion.Application.Services.LootSpawns;
using TarkovCompanion.Application.Services.Maps;

namespace TarkovCompanion.UnitTests.LootSpawns;

public sealed class LootSpawnFloorMapTests
{
    [Fact]
    public void Unbounded_base_is_an_overview_and_explicit_floor_is_kept_beside_it()
    {
        var floors = new[]
        {
            Floor("base", new(null, null, [])),
            Floor("upper", new(25, 34, [])),
        };

        var overview = LootSpawnFloorMap.OverviewFloorIds(floors);

        Assert.Equal(["base"], overview);
        Assert.Equal(["base", "upper"], LootSpawnFloorMap.RenderFloorIds(["upper"], overview));
        Assert.Equal(["base"], LootSpawnFloorMap.RenderFloorIds([], overview));
    }

    [Fact]
    public void Height_bounded_base_is_a_physical_floor_not_an_overview()
    {
        var floors = new[]
        {
            Floor("base", new(-7, 22, [])),
            Floor("upper", new(22, 30, [])),
        };

        var overview = LootSpawnFloorMap.OverviewFloorIds(floors);

        Assert.Empty(overview);
        Assert.Equal(["upper"], LootSpawnFloorMap.RenderFloorIds(["upper"], overview));
        Assert.Empty(LootSpawnFloorMap.RenderFloorIds([], overview));
    }

    private static MapFloorDefinition Floor(string id, MapLayerExtent extent) =>
        new(id, id, null, null, id == "base", [extent]);
}
