using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Profile;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Common;
using TarkovCompanion.Core.Domain.Events;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Maps;
using TarkovCompanion.Core.Domain.Profile;
using TarkovCompanion.Core.Domain.Quests;
using static TarkovCompanion.UnitTests.Profiles.ProfileV2Fixtures;

namespace TarkovCompanion.UnitTests.V2Capture;

/// <summary>
/// The catalog, profile, quest board and requirement doubles the Loot Scan and stash plan tests
/// share, so both are decided from one set of invented prices.
/// </summary>
internal static class LootScanFactFixtures
{
    internal static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    internal static readonly FleaMarketRates Rates = new(0.05, 0.05, Now.AddHours(-1));

    internal sealed class Catalog : IItemRepository, IItemMarketFactSource
    {
        private static readonly Dictionary<string, (string Name, int Width, int Height, bool Flea, long? Average, long? Trader, long Base, int Offers, int StampedHoursAgo)> Items = new()
        {
            ["gpu"] = ("Graphics card", 2, 1, true, 337_352, 120_000, 250_000, 40, 1),
            ["bolts"] = ("Bolts", 1, 1, true, 9_000, 3_000, 7_000, 60, 1),
            ["salewa"] = ("Salewa", 1, 2, true, 60_000, 12_000, 40_000, 25, 1),
            ["keycard"] = ("Lab keycard", 1, 1, false, null, 90_000, 50_000, 0, 720),
            ["relic"] = ("Relic", 1, 1, true, 100_000, 20_000, 60_000, 30, 720),
            ["item-gas-analyzer"] = ("Gas analyzer", 1, 1, true, 40_000, 15_000, 20_000, 35, 1),
            ["rifle"] = ("Rifle", 5, 2, true, 60_000, 25_000, 45_000, 50, 1),
        };

        public Task<ItemDefinition?> GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(itemId, out var item)
                ? new ItemDefinition(
                    itemId,
                    item.Name,
                    item.Name,
                    string.Empty,
                    itemId == "rifle" ? ItemCategory.Weapon : ItemCategory.Barter,
                    new ItemDimensions(item.Width, item.Height),
                    item.Flea,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new HashSet<string>(),
                    new DataProvenance("fixture", Now.AddHours(-item.StampedHoursAgo)))
                : null);

        public Task<IReadOnlyList<ItemSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ItemSearchHit>>([]);

        public Task<ItemPriceSnapshot?> GetPriceAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(itemId, out var item)
                ? new ItemPriceSnapshot(
                    item.Average,
                    item.Trader is { } trader ? [new TraderOffer("therapist", "Therapist", trader, new DataProvenance("fixture", Now.AddHours(-item.StampedHoursAgo)))] : [],
                    item.Average,
                    null,
                    null,
                    new DataProvenance("fixture", Now.AddHours(-item.StampedHoursAgo)))
                : null);

        Task<ItemMarketFacts?> IItemMarketFactSource.GetAsync(string itemId, CancellationToken cancellationToken) =>
            Task.FromResult(Items.TryGetValue(itemId, out var item)
                ? new ItemMarketFacts(itemId, item.Base, item.Offers, false, Now.AddHours(-item.StampedHoursAgo), Now.AddHours(-1))
                : null);

        public Task<FleaMarketRates?> GetFleaRatesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<FleaMarketRates?>(Rates);

        public Task<IReadOnlyDictionary<string, CurrencyRoubleRate>> GetCurrencyRoubleRatesAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, CurrencyRoubleRate>>(
                new Dictionary<string, CurrencyRoubleRate>(StringComparer.Ordinal)
                {
                    ["RUB"] = new("RUB", 1, new DataProvenance("fixture", Now)),
                    ["EUR"] = new("EUR", 222, new DataProvenance("fixture", Now)),
                    ["USD"] = new("USD", 190, new DataProvenance("fixture", Now)),
                });
    }

    internal sealed class Profiles : IPlayerProfileService
    {
        public Task<PlayerProfile> GetActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PlayerProfile(
                Id(282),
                "Local profile",
                GameMode.Regular,
                20,
                Faction.Unknown,
                null,
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                new Dictionary<string, int>(StringComparer.Ordinal),
                new Dictionary<string, EventItemState>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.Ordinal),
                DateTimeOffset.UnixEpoch));

        public Task SaveAsync(PlayerProfile profile, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> ExportJsonAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PlayerProfile> ImportJsonAsync(string json, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class Quests(QuestSummaryReadModel[] tasks) : IQuestReadService
    {
        public Task<QuestBoardReadModel> GetQuestBoardAsync(QuestProfileScope scope, CancellationToken cancellationToken) =>
            Task.FromResult(new QuestBoardReadModel(scope, 1, null, tasks, []));

        public Task<QuestItemNeedsReadModel> GetItemNeedsAsync(QuestProfileScope scope, string itemId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<QuestMapObjectivesReadModel> GetActiveMapObjectivesAsync(
            QuestProfileScope scope,
            IReadOnlyCollection<string> mapIds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    internal sealed class Requirements(QuestItemRequirement[]? quest = null) : IRequirementCatalog
    {
        public Task<IReadOnlyList<HideoutItemRequirement>> GetHideoutRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HideoutItemRequirement>>([]);

        public Task<IReadOnlyList<QuestItemRequirement>> GetQuestRequirementsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<QuestItemRequirement>>(quest ?? []);

        public Task<IReadOnlyList<HideoutStationSummary>> GetStationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<HideoutStationSummary>>([]);

        public void Invalidate()
        {
        }
    }

    internal sealed class NoMaps : IMapDataService
    {
        public Task<MapDefinition?> GetAsync(string mapId, CancellationToken cancellationToken) =>
            Task.FromResult<MapDefinition?>(null);
    }
}
