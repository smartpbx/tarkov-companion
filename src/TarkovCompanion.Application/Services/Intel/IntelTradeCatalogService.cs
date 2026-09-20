using System.Text.Json;
using TarkovCompanion.Application.Services.Catalogs;
using TarkovCompanion.Core.Abstractions;
using TarkovCompanion.Core.Domain.Items;
using TarkovCompanion.Core.Domain.Profile;

namespace TarkovCompanion.Application.Services.Intel;

public enum IntelTradeKind
{
    Craft,
    Barter,
}

/// <summary>
/// Whether the active profile's own station or trader level is enough to take this trade right
/// now.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is a property of the trade's own data, not of the profile: the feed
/// left the craft's station level unstated, or a barter names a loyalty requirement but not
/// which trader it is against. The profile's own station and trader levels always have a value
/// (an untouched one reads as zero, "not built"/"no loyalty yet" — a real fact, not a gap), so
/// there is never an "unknown" that comes from the player's side once the trade's own
/// requirement is known.
/// </remarks>
public enum IntelTradeReadiness
{
    Ready,
    Locked,
    Unknown,
}

/// <summary>One item a craft or barter consumes or yields, resolved to a name.</summary>
public sealed record IntelTradeIngredient(string ItemId, string Name, int Count);

/// <summary>One craft or barter, priced at the catalog's current numbers.</summary>
/// <param name="SourceName">The hideout station (craft) or trader (barter) that makes it.</param>
/// <param name="LevelLabel">"Level N" or "Loyalty N", or empty where the feed states no requirement.</param>
/// <param name="Duration">A craft's build time; null for a barter, which completes instantly.</param>
/// <param name="InputCostRoubles">What every input costs at its cheapest known buy price, or null.</param>
/// <param name="OutputValueRoubles">What the output sells for at its best price, or null.</param>
/// <param name="ProfitRoubles">Output value minus input cost, or null the moment either is unknown.</param>
public sealed record IntelTradeRow(
    string TradeId,
    IntelTradeKind Kind,
    IReadOnlyList<IntelTradeIngredient> Inputs,
    IntelTradeIngredient Output,
    string SourceName,
    string LevelLabel,
    TimeSpan? Duration,
    long? InputCostRoubles,
    long? OutputValueRoubles,
    long? ProfitRoubles,
    IntelTradeReadiness Readiness);

/// <summary>Every craft and barter the last sync stored, priced and ready to search (#287).</summary>
public interface IIntelTradeCatalogService
{
    Task<IReadOnlyList<IntelTradeRow>> GetAllAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Builds the Crafts &amp; barters tab's whole list: 214 crafts, read station by station because
/// <see cref="ICraftPlanningCatalog"/> has no "every craft" query and adding one would touch a
/// contract <c>HideoutWorkspaceViewModel</c> also depends on; 789 barters from
/// <see cref="IBarterCatalog"/> in one read. Every distinct item id across both is priced once and
/// shared, because a handful of items (screws, bolts, duct tape) show up in dozens of rows.
/// </summary>
/// <remarks>
/// Cached in memory for a short window rather than rebuilt on every call: pricing ~900 distinct
/// items is the same one-query-per-item shape <c>HideoutBarterRoutes</c> already accepts for a
/// single station's wanted list, multiplied out over the whole catalog. The cache keeps a second
/// caller in the same session — the tab's own list and an item detail's "Made by"/"Used in" —
/// from paying for that twice.
/// </remarks>
public sealed class IntelTradeCatalogService(
    ICraftPlanningCatalog craftPlanning,
    IBarterCatalog barterCatalog,
    IRequirementCatalog requirements,
    ITraderCatalog traders,
    IItemRepository itemRepository,
    IPlayerProfileService profileService,
    IItemMarketFactSource? marketFacts = null,
    TimeProvider? timeProvider = null) : IIntelTradeCatalogService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(45);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _buildLock = new(1, 1);
    private IReadOnlyList<IntelTradeRow>? _cached;
    private DateTimeOffset _cachedUtc = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<IntelTradeRow>> GetAllAsync(CancellationToken cancellationToken)
    {
        if (Fresh(_cached, _cachedUtc))
        {
            return _cached!;
        }

        await _buildLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Fresh(_cached, _cachedUtc))
            {
                return _cached!;
            }

            var built = await BuildAsync(cancellationToken).ConfigureAwait(false);
            _cached = built;
            _cachedUtc = _time.GetUtcNow();
            return built;
        }
        finally
        {
            _buildLock.Release();
        }
    }

    private bool Fresh(IReadOnlyList<IntelTradeRow>? cached, DateTimeOffset cachedUtc) =>
        cached is not null && _time.GetUtcNow() - cachedUtc < CacheLifetime;

    private async Task<IReadOnlyList<IntelTradeRow>> BuildAsync(CancellationToken cancellationToken)
    {
        var profile = await profileService.GetActiveAsync(cancellationToken).ConfigureAwait(false);
        var stations = await requirements.GetStationsAsync(cancellationToken).ConfigureAwait(false);
        var stationNames = stations.ToDictionary(station => station.StationId, station => station.Name, StringComparer.Ordinal);
        var traderNames = await traders.GetNamesAsync(cancellationToken).ConfigureAwait(false);

        var craftEntries = new List<CraftPlanningEntry>();
        foreach (var station in stations)
        {
            // historyLimitPerCraft: 0 — the tab prices from the current catalog, not a cost
            // history, so the (bounded but real) history read is skipped entirely.
            var entries = await craftPlanning
                .GetByStationAsync(station.StationId, historyLimitPerCraft: 0, cancellationToken)
                .ConfigureAwait(false);
            craftEntries.AddRange(entries);
        }

        var barterOffers = await barterCatalog.GetAsync(cancellationToken).ConfigureAwait(false);
        var facts = await PriceFactsAsync(craftEntries, barterOffers, cancellationToken).ConfigureAwait(false);

        var rows = new List<IntelTradeRow>(craftEntries.Count + barterOffers.Count);
        foreach (var craft in craftEntries)
        {
            if (BuildCraftRow(craft, facts, stationNames, profile) is { } row)
            {
                rows.Add(row);
            }
        }

        foreach (var barter in barterOffers)
        {
            rows.Add(BuildBarterRow(barter, facts, traderNames, profile));
        }

        return rows;
    }

    private async Task<Dictionary<string, ItemFacts>> PriceFactsAsync(
        IReadOnlyList<CraftPlanningEntry> crafts,
        IReadOnlyList<BarterOffer> barters,
        CancellationToken cancellationToken)
    {
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var craft in crafts)
        {
            foreach (var requirement in craft.Requirements)
            {
                if (requirement.ItemId is { Length: > 0 } id)
                {
                    itemIds.Add(id);
                }
            }

            foreach (var output in craft.Outputs)
            {
                if (output.ItemId is { Length: > 0 } id)
                {
                    itemIds.Add(id);
                }
            }
        }

        foreach (var barter in barters)
        {
            itemIds.Add(barter.Gives.ItemId);
            foreach (var want in barter.Wants)
            {
                itemIds.Add(want.ItemId);
            }
        }

        FleaMarketRates? rates = null;
        if (marketFacts is not null)
        {
            try
            {
                rates = await marketFacts.GetFleaRatesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                rates = null;
            }
        }

        var facts = new Dictionary<string, ItemFacts>(itemIds.Count, StringComparer.Ordinal);
        foreach (var itemId in itemIds)
        {
            var item = await itemRepository.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            var price = await itemRepository.GetPriceAsync(itemId, cancellationToken).ConfigureAwait(false);
            var fee = await FeeRoublesAsync(itemId, price?.FleaPriceRoubles, rates, cancellationToken).ConfigureAwait(false);
            facts[itemId] = new ItemFacts(
                item?.Name ?? itemId,
                price?.FleaPriceRoubles,
                price?.BestTrader?.ValueRoubles,
                fee);
        }

        return facts;
    }

    /// <summary>
    /// The flea listing fee at the item's current price. Mirrors <c>ItemIntelService</c>'s own
    /// fee lookup, which an item detail's "flea listing fee" line already shows — the same
    /// figure, so a craft's profit and the item's own price panel never disagree about what
    /// selling it on the flea actually nets.
    /// </summary>
    private async Task<long?> FeeRoublesAsync(
        string itemId,
        long? fleaPriceRoubles,
        FleaMarketRates? rates,
        CancellationToken cancellationToken)
    {
        if (marketFacts is null || fleaPriceRoubles is not > 0 || rates is null)
        {
            return null;
        }

        try
        {
            var itemFacts = await marketFacts.GetAsync(itemId, cancellationToken).ConfigureAwait(false);
            return itemFacts?.BasePriceRoubles is > 0
                ? FleaMarketFee.Calculate(itemFacts.BasePriceRoubles.Value, fleaPriceRoubles.Value, 1, rates)
                : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private static IntelTradeRow? BuildCraftRow(
        CraftPlanningEntry craft,
        IReadOnlyDictionary<string, ItemFacts> facts,
        IReadOnlyDictionary<string, string> stationNames,
        PlayerProfile profile)
    {
        var outputQuantity = craft.Outputs.FirstOrDefault(output => output.ItemId is { Length: > 0 });
        if (outputQuantity?.ItemId is not { Length: > 0 } outputItemId)
        {
            // No resolvable output: nothing to search for and nothing to price. Dropped rather
            // than shown as a trade with a blank result.
            return null;
        }

        var outputCount = ToCount(outputQuantity.Count);
        var outputFacts = facts.GetValueOrDefault(outputItemId);
        var output = new IntelTradeIngredient(outputItemId, outputFacts?.Name ?? outputItemId, outputCount);

        var inputs = craft.Requirements
            .Where(requirement => requirement.ItemId is { Length: > 0 })
            .Select(requirement => Ingredient(requirement.ItemId!, requirement.Count, facts))
            .ToArray();

        var inputCost = CraftBarterProfitCalculator.InputCost(
            inputs.Select(input => new IntelTradeInput(input.Count, facts.GetValueOrDefault(input.ItemId)?.FleaPriceRoubles)));
        var outputValue = CraftBarterProfitCalculator.OutputValue(
            outputCount, outputFacts?.FleaPriceRoubles, outputFacts?.FleaFeeRoubles, outputFacts?.BestTraderRoubles);

        var stationName = craft.StationId is { Length: > 0 } stationId
            ? stationNames.GetValueOrDefault(stationId, stationId)
            : string.Empty;

        return new IntelTradeRow(
            craft.CraftId,
            IntelTradeKind.Craft,
            inputs,
            output,
            stationName,
            craft.StationLevel is { } level ? $"Level {level}" : string.Empty,
            ParseDuration(craft.SourceJson),
            inputCost,
            outputValue,
            CraftBarterProfitCalculator.Profit(inputCost, outputValue),
            CraftReadiness(craft, profile));
    }

    private static IntelTradeRow BuildBarterRow(
        BarterOffer barter,
        IReadOnlyDictionary<string, ItemFacts> facts,
        IReadOnlyDictionary<string, string> traderNames,
        PlayerProfile profile)
    {
        var outputFacts = facts.GetValueOrDefault(barter.Gives.ItemId);
        var outputCount = Math.Max(1, barter.Gives.Count);
        var output = new IntelTradeIngredient(barter.Gives.ItemId, outputFacts?.Name ?? barter.Gives.ItemId, outputCount);

        var inputs = barter.Wants
            .Select(want => new IntelTradeIngredient(
                want.ItemId,
                facts.GetValueOrDefault(want.ItemId)?.Name ?? want.ItemId,
                Math.Max(1, want.Count)))
            .ToArray();

        var inputCost = CraftBarterProfitCalculator.InputCost(
            inputs.Select(input => new IntelTradeInput(input.Count, facts.GetValueOrDefault(input.ItemId)?.FleaPriceRoubles)));
        var outputValue = CraftBarterProfitCalculator.OutputValue(
            outputCount, outputFacts?.FleaPriceRoubles, outputFacts?.FleaFeeRoubles, outputFacts?.BestTraderRoubles);

        var traderName = barter.TraderId is { Length: > 0 } traderId
            ? traderNames.GetValueOrDefault(traderId, traderId)
            : string.Empty;

        return new IntelTradeRow(
            barter.BarterId,
            IntelTradeKind.Barter,
            inputs,
            output,
            traderName,
            barter.MinimumTraderLevel is { } level ? $"Loyalty {level}" : string.Empty,
            null,
            inputCost,
            outputValue,
            CraftBarterProfitCalculator.Profit(inputCost, outputValue),
            BarterReadiness(barter, profile));
    }

    private static IntelTradeIngredient Ingredient(string itemId, decimal? count, IReadOnlyDictionary<string, ItemFacts> facts) =>
        new(itemId, facts.GetValueOrDefault(itemId)?.Name ?? itemId, ToCount(count));

    private static int ToCount(decimal? count) => (int)Math.Max(1m, count ?? 1m);

    /// <summary>Ready when the profile's own station level meets the craft's; unknown when the craft's own level is unstated.</summary>
    private static IntelTradeReadiness CraftReadiness(CraftPlanningEntry craft, PlayerProfile profile)
    {
        if (craft.StationId is not { Length: > 0 } stationId || craft.StationLevel is not { } required)
        {
            return IntelTradeReadiness.Unknown;
        }

        return profile.HideoutStationLevels.GetValueOrDefault(stationId) >= required
            ? IntelTradeReadiness.Ready
            : IntelTradeReadiness.Locked;
    }

    /// <summary>
    /// Mirrors <c>BarterRouting.CanTake</c>: an unstated requirement is open to anybody, and a
    /// stated one with no named trader cannot be checked at all.
    /// </summary>
    private static IntelTradeReadiness BarterReadiness(BarterOffer barter, PlayerProfile profile)
    {
        if (barter.MinimumTraderLevel is not { } required || required <= 0)
        {
            return IntelTradeReadiness.Ready;
        }

        if (barter.TraderId is not { Length: > 0 } traderId)
        {
            return IntelTradeReadiness.Unknown;
        }

        return profile.TraderLevels.GetValueOrDefault(traderId) >= required
            ? IntelTradeReadiness.Ready
            : IntelTradeReadiness.Locked;
    }

    /// <summary>The craft's build time, in the one field of its own source payload that names it.</summary>
    private static TimeSpan? ParseDuration(string sourceJson)
    {
        try
        {
            using var document = JsonDocument.Parse(sourceJson);
            if (document.RootElement.TryGetProperty("duration", out var duration) &&
                duration.ValueKind == JsonValueKind.Number &&
                duration.TryGetInt64(out var seconds) &&
                seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private sealed record ItemFacts(string Name, long? FleaPriceRoubles, long? BestTraderRoubles, long? FleaFeeRoubles);
}
