using System.Text.Json;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Application.Services.Intel;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Planning;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.Application.Services.Planning;

public interface IAcquisitionChainPlanningService
{
    Task<AcquisitionChainPlan> PlanAsync(string itemId, int quantity, CancellationToken cancellationToken);
}

/// <summary>
/// Builds the profile-aware graph consumed by <see cref="AcquisitionChainPlanner"/> from the
/// synced craft, barter, trader and market catalogs.
/// </summary>
/// <remarks>
/// A recipe is admitted only when its station/trader level and task unlock are known to be met.
/// Missing profile facts therefore remove a candidate instead of silently calling it obtainable.
/// Fuel uses the cheapest current full-tank price per unit and the game's base generator burn
/// rate. Craft time uses the best positive profit-per-hour of another available craft at the same
/// station: occupying a station has no invented wage, but it can displace a better catalog-backed
/// craft. The Lavatory is the one station whose crafts do not consume generator fuel.
/// </remarks>
public sealed class AcquisitionChainPlanningService(
    ICraftPlanningCatalog crafts,
    IBarterCatalog barters,
    IItemCashOfferCatalog cashOffers,
    IRequirementCatalog requirements,
    ITraderCatalog traders,
    IItemRepository items,
    IPlayerProfileService profiles,
    IItemMarketFactSource? marketFacts = null,
    TimeProvider? clock = null) : IAcquisitionChainPlanningService
{
    private const string MetalFuelTankId = "5d1b36a186f7742523398433";
    private const string ExpeditionaryFuelTankId = "5d1b371186f774253763a656";
    private const decimal BaseGeneratorFuelUnitsPerHour = 60m / 12.4m;
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(45);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private Snapshot? _cached;
    private DateTimeOffset _cachedUtc = DateTimeOffset.MinValue;

    public async Task<AcquisitionChainPlan> PlanAsync(
        string itemId,
        int quantity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentOutOfRangeException.ThrowIfLessThan(quantity, 1);
        var snapshot = await SnapshotAsync(cancellationToken).ConfigureAwait(false);
        return AcquisitionChainPlanner.Plan(itemId, quantity, snapshot.Items, snapshot.Recipes);
    }

    private async Task<Snapshot> SnapshotAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null && _clock.GetUtcNow() - _cachedUtc < CacheLifetime)
        {
            return _cached;
        }

        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && _clock.GetUtcNow() - _cachedUtc < CacheLifetime)
            {
                return _cached;
            }

            _cached = await BuildAsync(cancellationToken).ConfigureAwait(false);
            _cachedUtc = _clock.GetUtcNow();
            return _cached;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private async Task<Snapshot> BuildAsync(CancellationToken cancellationToken)
    {
        var profile = await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var stations = await requirements.GetStationsAsync(cancellationToken).ConfigureAwait(false);
        var stationNames = stations.ToDictionary(station => station.StationId, station => station.Name, StringComparer.Ordinal);
        var traderNames = await traders.GetNamesAsync(cancellationToken).ConfigureAwait(false);
        var craftEntries = new List<CraftPlanningEntry>();
        foreach (var station in stations)
        {
            craftEntries.AddRange(await crafts
                .GetByStationAsync(station.StationId, historyLimitPerCraft: 0, cancellationToken)
                .ConfigureAwait(false));
        }

        var barterEntries = await barters.GetAsync(cancellationToken).ConfigureAwait(false);
        var cashEntries = await cashOffers.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var itemIds = new HashSet<string>(StringComparer.Ordinal)
        {
            MetalFuelTankId,
            ExpeditionaryFuelTankId,
        };
        foreach (var craft in craftEntries)
        {
            itemIds.UnionWith(craft.Requirements.Where(row => row.ItemId is not null).Select(row => row.ItemId!));
            itemIds.UnionWith(craft.Outputs.Where(row => row.ItemId is not null).Select(row => row.ItemId!));
        }

        foreach (var barter in barterEntries)
        {
            itemIds.Add(barter.Gives.ItemId);
            itemIds.UnionWith(barter.Wants.Select(row => row.ItemId));
        }

        itemIds.UnionWith(cashEntries.Select(row => row.ItemId));
        var facts = await ReadItemFactsAsync(itemIds, cashEntries, traderNames, profile, cancellationToken).ConfigureAwait(false);
        var available = BuildAvailableRecipes(craftEntries, barterEntries, stationNames, traderNames, profile);
        return new(facts, AddCraftOverhead(available, facts));
    }

    private async Task<IReadOnlyDictionary<string, AcquisitionChainItem>> ReadItemFactsAsync(
        IReadOnlySet<string> itemIds,
        IReadOnlyList<ItemCashOffer> cashEntries,
        IReadOnlyDictionary<string, string> traderNames,
        PlayerProfile profile,
        CancellationToken cancellationToken)
    {
        FleaMarketRates? rates = null;
        if (marketFacts is not null)
        {
            try
            {
                rates = await marketFacts.GetFleaRatesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
        }

        var cashByItem = cashEntries.GroupBy(entry => entry.ItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var result = new Dictionary<string, AcquisitionChainItem>(itemIds.Count, StringComparer.Ordinal);
        foreach (var itemId in itemIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await items.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            var price = await items.GetPriceAsync(itemId, cancellationToken).ConfigureAwait(false);
            long? fee = null;
            if (item?.FleaEligible == true && price?.FleaPriceRoubles is > 0 and var flea && rates is not null && marketFacts is not null)
            {
                try
                {
                    var market = await marketFacts.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
                    if (market?.BasePriceRoubles is > 0 and var basePrice)
                    {
                        fee = FleaMarketFee.Calculate(basePrice, flea, 1, rates);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                }
            }

            var buys = new List<(long Price, string Source)>();
            if (item?.FleaEligible == true && price?.FleaPriceRoubles is > 0 and var fleaBuy)
            {
                buys.Add((fleaBuy, "Flea market"));
            }

            if (cashByItem.TryGetValue(itemId, out var itemCash))
            {
                foreach (var cash in itemCash)
                {
                    var traderName = traderNames.GetValueOrDefault(cash.TraderId, cash.TraderId);
                    var offer = new ItemAcquisitionOffer(
                        cash.ItemId,
                        ItemAcquisitionKind.Cash,
                        cash.TraderId,
                        traderName,
                        cash.MinimumTraderLevel,
                        cash.TaskUnlockId,
                        cash.TaskUnlockId,
                        cash.PriceRoubles,
                        []);
                    if (cash.PriceRoubles > 0 && ItemObtainabilityRule.Evaluate(offer, profile).IsObtainable)
                    {
                        buys.Add((cash.PriceRoubles, traderName));
                    }
                }
            }

            var bestBuy = buys.OrderBy(buy => buy.Price).ThenBy(buy => buy.Source, StringComparer.Ordinal).FirstOrDefault();
            var fleaNet = price?.FleaPriceRoubles is > 0 and var asking && fee is { } listingFee
                ? Math.Max(0, asking - listingFee)
                : 0;
            var traderSale = price?.BestTrader?.ValueRoubles ?? 0;
            var opportunity = Math.Max(fleaNet, traderSale);
            result[itemId] = new(
                itemId,
                item?.Name ?? itemId,
                bestBuy.Price > 0 ? bestBuy.Price : null,
                bestBuy.Price > 0 ? bestBuy.Source : null,
                opportunity > 0 ? opportunity : null,
                price?.Provenance.SourceUpdatedUtc ?? price?.Provenance.ObservedUtc);
        }

        return result;
    }

    private static IReadOnlyList<AcquisitionChainRecipe> BuildAvailableRecipes(
        IReadOnlyList<CraftPlanningEntry> craftEntries,
        IReadOnlyList<BarterOffer> barterEntries,
        IReadOnlyDictionary<string, string> stationNames,
        IReadOnlyDictionary<string, string> traderNames,
        PlayerProfile profile)
    {
        var result = new List<AcquisitionChainRecipe>();
        foreach (var craft in craftEntries)
        {
            var readiness = IntelTradeReadinessResolver.ForCraft(craft.StationId, craft.StationLevel, profile.HideoutStationLevels);
            var taskUnlock = ReadTaskUnlock(craft.SourceJson);
            if (readiness.Readiness != IntelTradeReadiness.Ready ||
                taskUnlock is { Length: > 0 } && !profile.CompletedTaskIds.Contains(taskUnlock))
            {
                continue;
            }

            var inputs = craft.Requirements
                .Where(row => row.ItemId is { Length: > 0 })
                .Select(row => new AcquisitionChainIngredient(row.ItemId!, ToCount(row.Count), IsTool(row.SourceJson)))
                .ToArray();
            foreach (var output in craft.Outputs.Where(row => row.ItemId is { Length: > 0 }))
            {
                result.Add(new(
                    craft.CraftId,
                    AcquisitionChainMethod.Craft,
                    output.ItemId!,
                    ToCount(output.Count),
                    inputs,
                    craft.StationId is { Length: > 0 } stationId
                        ? stationNames.GetValueOrDefault(stationId, stationId)
                        : "Hideout",
                    ReadDuration(craft.SourceJson),
                    0,
                    0));
            }
        }

        foreach (var barter in barterEntries)
        {
            if (barter.TraderId is not { Length: > 0 } traderId)
            {
                continue;
            }

            var traderName = traderNames.GetValueOrDefault(traderId, traderId);
            var offer = new ItemAcquisitionOffer(
                barter.Gives.ItemId,
                ItemAcquisitionKind.Barter,
                traderId,
                traderName,
                barter.MinimumTraderLevel,
                barter.TaskUnlock,
                barter.TaskUnlock,
                null,
                []);
            if (!ItemObtainabilityRule.Evaluate(offer, profile).IsObtainable)
            {
                continue;
            }

            result.Add(new(
                barter.BarterId,
                AcquisitionChainMethod.Barter,
                barter.Gives.ItemId,
                Math.Max(1, barter.Gives.Count),
                barter.Wants.Select(row => new AcquisitionChainIngredient(row.ItemId, Math.Max(1, row.Count))).ToArray(),
                traderName,
                null,
                0,
                0));
        }

        return result;
    }

    private static IReadOnlyList<AcquisitionChainRecipe> AddCraftOverhead(
        IReadOnlyList<AcquisitionChainRecipe> recipes,
        IReadOnlyDictionary<string, AcquisitionChainItem> items)
    {
        var fuelPerHour = FuelRoublesPerHour(items);
        var recurring = recipes.ToDictionary(
            recipe => recipe.RecipeId,
            recipe => RecurringProfitPerHour(recipe, items, fuelPerHour),
            StringComparer.Ordinal);
        var byStation = recipes.Where(recipe => recipe.Method == AcquisitionChainMethod.Craft)
            .GroupBy(recipe => recipe.SourceName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var result = new List<AcquisitionChainRecipe>(recipes.Count);
        foreach (var recipe in recipes)
        {
            if (recipe.Method != AcquisitionChainMethod.Craft || recipe.Duration is not { } duration)
            {
                result.Add(recipe);
                continue;
            }

            var hours = (decimal)duration.TotalHours;
            var fuel = string.Equals(recipe.SourceName, "Lavatory", StringComparison.OrdinalIgnoreCase)
                ? 0
                : RoundCost(fuelPerHour * hours);
            var displacedPerHour = byStation.GetValueOrDefault(recipe.SourceName, [])
                .Where(other => !string.Equals(other.RecipeId, recipe.RecipeId, StringComparison.Ordinal))
                .Select(other => recurring.GetValueOrDefault(other.RecipeId))
                .DefaultIfEmpty(0)
                .Max();
            result.Add(recipe with
            {
                FuelCostRoubles = fuel,
                StationTimeCostRoubles = RoundCost(Math.Max(0, displacedPerHour) * hours),
            });
        }

        return result;
    }

    private static decimal RecurringProfitPerHour(
        AcquisitionChainRecipe recipe,
        IReadOnlyDictionary<string, AcquisitionChainItem> items,
        decimal fuelPerHour)
    {
        if (recipe.Method != AcquisitionChainMethod.Craft || recipe.Duration is not { TotalHours: > 0 } duration ||
            items.GetValueOrDefault(recipe.OutputItemId)?.OpportunityValueRoubles is not { } outputEach)
        {
            return 0;
        }

        decimal inputs = 0;
        foreach (var input in recipe.Inputs.Where(input => !input.Reusable))
        {
            if (items.GetValueOrDefault(input.ItemId)?.OpportunityValueRoubles is not { } inputEach)
            {
                return 0;
            }

            inputs += inputEach * input.Count;
        }

        var fuel = string.Equals(recipe.SourceName, "Lavatory", StringComparison.OrdinalIgnoreCase)
            ? 0
            : fuelPerHour * (decimal)duration.TotalHours;
        var profit = (outputEach * recipe.OutputCount) - inputs - fuel;
        return profit > 0 ? profit / (decimal)duration.TotalHours : 0;
    }

    private static decimal FuelRoublesPerHour(IReadOnlyDictionary<string, AcquisitionChainItem> items)
    {
        var perUnit = new[]
            {
                FuelUnitPrice(items.GetValueOrDefault(MetalFuelTankId), 100),
                FuelUnitPrice(items.GetValueOrDefault(ExpeditionaryFuelTankId), 60),
            }
            .Where(price => price is > 0)
            .DefaultIfEmpty(0)
            .Min();
        return perUnit * BaseGeneratorFuelUnitsPerHour;
    }

    private static decimal FuelUnitPrice(AcquisitionChainItem? item, int units) =>
        item?.BuyPriceRoubles is > 0 and var price ? (decimal)price / units : 0;

    private static long RoundCost(decimal value) => value <= 0
        ? 0
        : value >= long.MaxValue
            ? long.MaxValue
            : (long)Math.Ceiling(value);

    private static int ToCount(decimal? value) => (int)Math.Max(1, Math.Ceiling(value ?? 1));

    private static bool IsTool(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("attributes", out var attributes) &&
                attributes.TryGetProperty("tool", out var tool) &&
                tool.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static TimeSpan? ReadDuration(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("duration", out var duration) &&
                duration.TryGetInt64(out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadTaskUnlock(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("taskUnlock", out var task)
                ? task.ValueKind switch
                {
                    JsonValueKind.String => task.GetString(),
                    JsonValueKind.Object when task.TryGetProperty("id", out var id) => id.GetString(),
                    _ => null,
                }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Snapshot(
        IReadOnlyDictionary<string, AcquisitionChainItem> Items,
        IReadOnlyList<AcquisitionChainRecipe> Recipes);
}
