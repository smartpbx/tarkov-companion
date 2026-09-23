using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Application.Services.Planning;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.UnitTests.Planning;

public sealed class AcquisitionChainPlanningServiceTests
{
    private const string Target = "544fb45d4bdc2dee738b4568";
    private const string Input = "5755356824597772cb798962";

    [Fact]
    public async Task A_craft_requires_both_the_profile_station_level_and_its_task_unlock()
    {
        var stationLocked = await Service(Profile(station: 0, completed: false)).PlanAsync(Target, 1, default);
        var taskLocked = await Service(Profile(station: 1, completed: false)).PlanAsync(Target, 1, default);
        var ready = await Service(Profile(station: 1, completed: true)).PlanAsync(Target, 1, default);

        Assert.Equal(AcquisitionChainMethod.Buy, stationLocked.Cheapest!.Method);
        Assert.Equal(AcquisitionChainMethod.Buy, taskLocked.Cheapest!.Method);
        Assert.Equal(AcquisitionChainMethod.Craft, ready.Cheapest!.Method);
        Assert.Equal("Medstation", ready.Cheapest.SourceName);
    }

    private static AcquisitionChainPlanningService Service(PlayerProfile profile)
    {
        var provenance = new DataProvenance("json.tarkov.dev", new DateTimeOffset(2026, 9, 14, 23, 38, 0, TimeSpan.Zero));
        var itemRepository = new ChainItemRepository(
            new Dictionary<string, ItemDefinition>(StringComparer.Ordinal)
            {
                [Target] = Item(Target, "Salewa first aid kit", provenance),
                [Input] = Item(Input, "AI-2 medkit", provenance),
            },
            new Dictionary<string, ItemPriceSnapshot>(StringComparer.Ordinal)
            {
                [Target] = new(100_000, [], null, null, null, provenance),
                [Input] = new(10_000, [], null, null, null, provenance),
            });
        return new(
            new ChainCraftCatalog(new(
                "salewa-craft",
                "medstation",
                1,
                "{\"duration\":60,\"taskUnlock\":\"medical-task\"}",
                [new(Input, 1, "{\"attributes\":{}}")],
                [new(Target, 1, "{}")],
                [])),
            new EmptyBarterCatalog(),
            new EmptyCashCatalog(),
            new ChainRequirementCatalog(),
            new ChainTraderCatalog(),
            itemRepository,
            new ChainProfileService(profile));
    }

    private static ItemDefinition Item(string id, string name, DataProvenance provenance) => new(
        id,
        name,
        name,
        string.Empty,
        ItemCategory.Medicine,
        new(1, 1),
        true,
        null,
        null,
        null,
        null,
        null,
        new HashSet<string>(),
        provenance);

    private static PlayerProfile Profile(int station, bool completed) => new(
        Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
        "Chain",
        GameMode.Regular,
        30,
        Faction.Usec,
        null,
        new Dictionary<string, int>(),
        completed ? new HashSet<string>(["medical-task"]) : new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, int> { ["medstation"] = station },
        new HashSet<string>(),
        new Dictionary<string, int>(),
        new Dictionary<string, TarkovCompanion.Core.Domain.Events.EventItemState>(),
        new Dictionary<string, string>(),
        new DateTimeOffset(2026, 9, 14, 23, 40, 0, TimeSpan.Zero));

    private sealed class ChainCraftCatalog(CraftPlanningEntry entry) : ICraftPlanningCatalog
    {
        public Task<IReadOnlyList<CraftPlanningEntry>> GetByStationAsync(
            string stationId,
            int historyLimitPerCraft,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CraftPlanningEntry>>(stationId == entry.StationId ? [entry] : []);

        public Task<CraftPlanningEntry?> GetByCraftAsync(string craftId, int historyLimit, CancellationToken cancellationToken) =>
            Task.FromResult<CraftPlanningEntry?>(craftId == entry.CraftId ? entry : null);
    }

    private sealed class EmptyBarterCatalog : IBarterCatalog
    {
        public Task<IReadOnlyList<BarterOffer>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BarterOffer>>([]);
    }

    private sealed class EmptyCashCatalog : IItemCashOfferCatalog
    {
        public Task<IReadOnlyList<ItemCashOffer>> GetAllAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemCashOffer>>([]);
    }

    private sealed class ChainRequirementCatalog : IRequirementCatalog
    {
        public Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HideoutItemRequirement>>([]);

        public Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuestItemRequirement>>([]);

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HideoutStationSummary>>([new("medstation", "Medstation", [1, 2, 3])]);

        public void Invalidate()
        {
        }
    }

    private sealed class ChainTraderCatalog : ITraderCatalog
    {
        public Task<IReadOnlyDictionary<string, string>> GetNamesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
    }

    private sealed class ChainItemRepository(
        IReadOnlyDictionary<string, ItemDefinition> items,
        IReadOnlyDictionary<string, ItemPriceSnapshot> prices) : IItemRepository
    {
        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(items.GetValueOrDefault(itemId));

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(prices.GetValueOrDefault(itemId));
    }

    private sealed class ChainProfileService(PlayerProfile profile) : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) => Task.FromResult(profile);

        public Task SaveAsync(PlayerProfile value, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
